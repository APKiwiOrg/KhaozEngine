using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

internal static class TileWaterColumnCoverage
{
    const int SpanLimit = MovementQueryLease.MaxCoverageSpans;
    const int ContactLimit = MovementQueryLease.MaxDomainContacts;
    [ThreadStatic] static Scratch? t_scratch;

    sealed class Scratch
    {
        internal float[] Cuts { get; } = new float[SpanLimit + 2];
        internal MovementCoverageSpan[] Spans { get; } = new MovementCoverageSpan[SpanLimit];
        internal MovementDomainContact[] Contacts { get; } = new MovementDomainContact[ContactLimit];
    }

    internal static MovementCoverageResult Trace(ReadOnlySpan<TileWaterColumn> columns,
        in MovementMediumSweepQuery query, in MovementQueryIdentity identity,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        Trace(columns, TileWaterBoundary.Build(columns, true), TileWaterBoundary.Build(columns, false), query, Vector3.Zero,
            identity, spans, contacts);

    // Runtime callers retain topology with the immutable field. Only bounded per-thread output scratch is mutable.
    // The query is local to the physics origin. Every geometric test uses the absolute start local + origin.
    internal static MovementCoverageResult Trace(ReadOnlySpan<TileWaterColumn> columns,
        ReadOnlySpan<TileWaterBoundary> boundaries, ReadOnlySpan<TileWaterBoundary> exterior,
        in MovementMediumSweepQuery query, Vector3 origin, in MovementQueryIdentity identity,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        if (!query.IsValid || !identity.IsValid)
            return Refuse(MovementAvailability.Invalid, identity);
        foreach (TileWaterColumn column in columns)
            if (column.Domain.WorldId != query.Body.CurrentSpace.WorldId)
                return Refuse(MovementAvailability.Invalid, identity);
        Scratch scratch = t_scratch ??= new();
        Span<float> cuts = scratch.Cuts;
        int cutCount = 2;
        cuts[0] = 0;
        cuts[1] = 1;
        double error = 0;
        foreach (TileWaterColumn column in columns)
            if (!AddRange(column.Minimum, column.Maximum, query, origin, cuts, ref cutCount, ref error))
                return Refuse(MovementAvailability.CapacityExceeded, identity, SpanLimit + 1);
        foreach (TileWaterBoundary face in boundaries)
            if (!AddRange(face.Minimum, face.Maximum, query, origin, cuts, ref cutCount, ref error))
                return Refuse(MovementAvailability.CapacityExceeded, identity, SpanLimit + 1);
        foreach (TileWaterBoundary face in exterior)
            if (!AddRange(face.Minimum, face.Maximum, query, origin, cuts, ref cutCount, ref error))
                return Refuse(MovementAvailability.CapacityExceeded, identity, SpanLimit + 1);
        float certified = Upper(error);
        if (!float.IsFinite(certified) || certified > MovementQueryLease.CoverageSkinMetres)
            return Refuse(MovementAvailability.Unresolved, identity);
        int spanCount = cutCount - 1;
        if (spanCount > SpanLimit) return Refuse(MovementAvailability.CapacityExceeded, identity, spanCount);
        int contactCount = 0;
        double locationError = 0;
        try
        {
            for (int i = 0; i < spanCount; i++)
            {
                float enter = cuts[i], exit = cuts[i + 1];
                int first = contactCount;
                foreach (TileWaterColumn column in columns)
                {
                    TileCapsuleBoxPath.Range range = TileCapsuleBoxPath.Intersect(column.Minimum, column.Maximum, query, origin, enter, exit);
                    if (!range.Hit) continue;
                    bool hasFace = false;
                    foreach (TileWaterBoundary face in boundaries)
                    {
                        if (columns[face.Column] != column) continue;
                        var contactRange = TileCapsuleBoxPath.Intersect(face.Minimum, face.Maximum, query, origin, enter, exit);
                        if (!contactRange.Hit) continue;
                        hasFace = true;
                        if (!Append(column, face, range, contactRange, enter, exit, query, origin, scratch.Contacts, ref contactCount, ref locationError))
                            return Refuse(MovementAvailability.CapacityExceeded, identity, spanCount, ContactLimit + 1, certified);
                    }
                    if (!hasFace && !AlreadyCovered(column, scratch.Contacts.AsSpan(first, contactCount - first)))
                    {
                        if (!Append(column, new TileWaterBoundary(0, new(column.Minimum.X, column.Maximum.Y, column.Minimum.Z), column.Maximum, Vector3.UnitY, column.SurfaceBoundaryId), range, range, enter, exit, query, origin, scratch.Contacts, ref contactCount, ref locationError))
                            return Refuse(MovementAvailability.CapacityExceeded, identity, spanCount, ContactLimit + 1, certified);
                    }
                }
                bool dry = DryCoverage(columns, exterior, query, origin, enter, exit);
                scratch.Spans[i] = new(enter, exit, first, contactCount - first, dry);
            }
            certified = Upper(error + locationError);
            if (!float.IsFinite(certified) || certified > MovementQueryLease.CoverageSkinMetres)
                return Refuse(MovementAvailability.Unresolved, identity);
            if (spanCount > spans.Length || contactCount > contacts.Length)
                return Refuse(MovementAvailability.CapacityExceeded, identity, spanCount, contactCount, certified);
            scratch.Spans.AsSpan(0, spanCount).CopyTo(spans);
            scratch.Contacts.AsSpan(0, contactCount).CopyTo(contacts);
            return new(MovementAvailability.Known, spanCount, spanCount, contactCount, contactCount, certified, identity);
        }
        finally { scratch.Contacts.AsSpan(0, contactCount).Clear(); }
    }

    static bool AddRange(Vector3 min, Vector3 max, in MovementMediumSweepQuery query, Vector3 origin,
        Span<float> cuts, ref int count, ref double error)
    {
        var range = TileCapsuleBoxPath.Intersect(min, max, query, origin);
        double fractionError = 0;
        bool fits = !range.Hit || AddCut(range.Enter, query, cuts, ref count, ref fractionError) &&
            AddCut(range.Exit, query, cuts, ref count, ref fractionError);
        error = Math.Max(error, range.ErrorMetres + fractionError);
        return fits;
    }

    static bool AddCut(double at, in MovementMediumSweepQuery query, Span<float> cuts, ref int count, ref double error)
    {
        if (at <= 0 || at >= 1) return true;
        float low = Lower(at), high = Upper(at);
        double length = Math.Sqrt((double)query.Delta.X * query.Delta.X + (double)query.Delta.Y * query.Delta.Y + (double)query.Delta.Z * query.Delta.Z);
        error = Math.Max(error, Math.Max(at - low, high - at) * length);
        return Insert(low, cuts, ref count) && Insert(high, cuts, ref count);
    }

    static bool Insert(float at, Span<float> cuts, ref int count)
    {
        int index = 0;
        while (index < count && cuts[index] < at) index++;
        if (index < count && cuts[index] == at) return true;
        if (count == cuts.Length) return false;
        cuts[index..count].CopyTo(cuts[(index + 1)..]);
        cuts[index] = at;
        count++;
        return true;
    }

    static bool Append(TileWaterColumn column, TileWaterBoundary face, TileCapsuleBoxPath.Range range, TileCapsuleBoxPath.Range contactRange,
        float enter, float exit, in MovementMediumSweepQuery query, Vector3 origin, Span<MovementDomainContact> contacts,
        ref int count, ref double locationError)
    {
        if (count == contacts.Length) return false;
        float fraction = Math.Clamp((float)contactRange.Enter, enter, exit);
        double x = (double)query.Body.Centre.X + origin.X + (double)fraction * query.Delta.X;
        double z = (double)query.Body.Centre.Z + origin.Z + (double)fraction * query.Delta.Z;
        Vector2 witness = new(Math.Clamp((float)x, column.Minimum.X, column.Maximum.X), Math.Clamp((float)z, column.Minimum.Z, column.Maximum.Z));
        double dx = witness.X - Math.Clamp(x, column.Minimum.X, column.Maximum.X);
        double dz = witness.Y - Math.Clamp(z, column.Minimum.Z, column.Maximum.Z);
        locationError = Math.Max(locationError, Math.Sqrt(dx * dx + dz * dz));
        double r2 = (double)query.Body.Radius * query.Body.Radius;
        var spanOverlap = range.MinimumSquared >= r2 ? MovementContactOverlap.Tangent : MovementContactOverlap.Overlapping;
        var overlap = TileCapsuleBoxPath.DistanceSquared(column.Minimum, column.Maximum, query, origin, fraction) >= r2
            ? MovementContactOverlap.Tangent : MovementContactOverlap.Overlapping;
        if (spanOverlap == MovementContactOverlap.Tangent) overlap = MovementContactOverlap.Tangent;
        contacts[count++] = new(column.Domain, query.Body.CurrentSpace, column.Interval, witness,
            TileCapsuleBoxPath.Normal(face, query, origin, fraction), fraction, face.Id, 0, overlap, spanOverlap);
        return true;
    }

    static bool AlreadyCovered(TileWaterColumn column, ReadOnlySpan<MovementDomainContact> contacts)
    {
        foreach (MovementDomainContact contact in contacts)
            if (contact.Domain == column.Domain && contact.Interval.LowerY == column.Minimum.Y && contact.Interval.UpperY == column.Maximum.Y)
                return true;
        return false;
    }

    static bool DryCoverage(ReadOnlySpan<TileWaterColumn> columns, ReadOnlySpan<TileWaterBoundary> exterior,
        in MovementMediumSweepQuery query, Vector3 origin, float enter, float exit)
    {
        double middle = ((double)enter + exit) * 0.5;
        double x = (double)query.Body.Centre.X + origin.X + middle * query.Delta.X,
            y = (double)query.Body.Centre.Y + origin.Y + middle * query.Delta.Y,
            z = (double)query.Body.Centre.Z + origin.Z + middle * query.Delta.Z;
        bool centreInside = false;
        foreach (TileWaterColumn column in columns)
            if (x >= column.Minimum.X && x <= column.Maximum.X && y >= column.Minimum.Y &&
                y <= column.Maximum.Y && z >= column.Minimum.Z && z <= column.Maximum.Z)
            { centreInside = true; break; }
        if (!centreInside) return true;
        double squaredRadius = (double)query.Body.Radius * query.Body.Radius;
        foreach (TileWaterBoundary face in exterior)
            if (TileCapsuleBoxPath.Intersect(face.Minimum, face.Maximum, query, origin, enter, exit).MinimumSquared < squaredRadius)
                return true;
        return false;
    }

    internal static float Lower(double value) { float f = (float)value; return f > value ? MathF.BitDecrement(f) : f; }
    internal static float Upper(double value) { float f = (float)value; return f < value ? MathF.BitIncrement(f) : f; }
    static MovementCoverageResult Refuse(MovementAvailability status, MovementQueryIdentity identity,
        int spans = 0, int contacts = 0, float error = 0) => new(status, 0, spans, 0, contacts, error, identity);
}
