using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>A finite vertical water slab over all represented horizontal coordinates.</summary>
internal sealed class LevelMovementWaterField : IMovementWaterField
{
    readonly string _worldId;
    readonly MovementDomainKey _domain;
    readonly MovementWaterInterval _interval;
    public string SemanticIdentity { get; }
    public long Generation => 0;
    public MovementWaterExtent Extent { get; }

    internal LevelMovementWaterField(string worldId, float surfaceY, float floorY)
    {
        MovementEnvironmentValidation.RequireName(worldId, nameof(worldId));
        MovementEnvironmentValidation.Require(float.IsFinite(surfaceY) && float.IsFinite(floorY) &&
            floorY < surfaceY, nameof(floorY));
        _worldId = worldId;
        _domain = new(worldId, "level");
        _interval = new(floorY, surfaceY, surfaceY, true, "level/bed", "level/surface");
        Extent = new(new(-float.MaxValue, floorY, -float.MaxValue), new(float.MaxValue, surfaceY, float.MaxValue));
        SemanticIdentity = $"level-v1/{worldId.Length}/{worldId}/{OrdinaryMovementRegistration.Bits(floorY)}/{OrdinaryMovementRegistration.Bits(surfaceY)}";
    }

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin)
    {
        if (!body.IsValid || !MovementEnvironmentValidation.Finite(physicsOrigin) || body.CurrentSpace.WorldId != _worldId)
            return new(MovementAvailability.Invalid, default, null, false, 0, null);
        // The exact signs of local + origin - halfHeight - bound, with no rounded absolute centre.
        float y = body.Centre.Y, originY = physicsOrigin.Y, half = -body.HalfHeight;
        bool wet = OrdinaryMovementEnvironment.ExactSign(y, originY, half, -_interval.LowerY) >= 0 &&
            OrdinaryMovementEnvironment.ExactSign(y, originY, half, -_interval.UpperY) < 0;
        return new(MovementAvailability.Known, body.CurrentSpace, wet ? _domain : null, wet, 1, wet ? _interval : null);
    }

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        if (!query.IsValid || !identity.IsValid || !MovementEnvironmentValidation.Finite(physicsOrigin) ||
            query.Body.CurrentSpace.WorldId != _worldId)
            return new(MovementAvailability.Invalid, 0, 0, 0, 0, 0, identity);
        double half = query.Body.HalfHeight, skin = MovementQueryLease.CoverageSkinMetres;
        double error = 0, startError = 0;
        // The absolute start in binary64. Its residual is zero for every binary32 pair one binary64 holds.
        Start start = new(OrdinaryMovementEnvironment.Absolute(query.Body.Centre.X, physicsOrigin.X, ref startError),
            OrdinaryMovementEnvironment.Absolute(query.Body.Centre.Y, physicsOrigin.Y, ref startError),
            OrdinaryMovementEnvironment.Absolute(query.Body.Centre.Z, physicsOrigin.Z, ref startError));
        Span<float> cuts = stackalloc float[34];
        int cutCount = 2;
        cuts[0] = 0;
        cuts[1] = 1;
        for (int planeIndex = 0; planeIndex < 2; planeIndex++)
        {
            float plane = planeIndex == 0 ? _interval.LowerY : _interval.UpperY;
            AddCut(plane - half - skin, start, query, cuts, ref cutCount, ref error);
            AddCut(plane + half + skin, start, query, cuts, ref cutCount, ref error);
            AddCut(plane - half, start, query, cuts, ref cutCount, ref error);
            AddCut(plane + half, start, query, cuts, ref cutCount, ref error);
        }
        cuts[..cutCount].Sort();
        int unique = 0;
        for (int i = 0; i < cutCount; i++)
            if (unique == 0 || cuts[i] != cuts[unique - 1]) cuts[unique++] = cuts[i];
        Span<TraceSpan> output = stackalloc TraceSpan[67];
        int count = 0, contactCount = 0;
        for (int i = 0; i < unique; i++)
        {
            Describe(cuts[i], cuts[i], start, query, output, ref count, ref contactCount, ref error);
            if (i + 1 < unique) Describe(cuts[i], cuts[i + 1], start, query, output, ref count, ref contactCount, ref error);
        }
        float certified = OrdinaryServedDomain.Upper(error + startError);
        if (!float.IsFinite(certified) || certified > MovementQueryLease.CoverageSkinMetres)
            return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, identity);
        if (count > Math.Min(spans.Length, MovementQueryLease.MaxCoverageSpans) ||
            contactCount > Math.Min(contacts.Length, MovementQueryLease.MaxDomainContacts))
            return new(MovementAvailability.CapacityExceeded, 0, count, 0, contactCount, certified, identity);
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            TraceSpan item = output[i];
            int first = written;
            if (item.Wet)
                contacts[written++] = new(_domain, query.Body.CurrentSpace, _interval, item.Column, item.Normal,
                    item.Fraction, item.Normal.Y < 0 ? "level/bed" : "level/surface", 0, item.Overlap, item.SpanOverlap);
            spans[i] = new(item.Enter, item.Exit, first, written - first, item.Dry);
        }
        return new(MovementAvailability.Known, count, count, written, written, certified, identity);
    }

    static void AddCut(double y, Start start, in MovementMediumSweepQuery query, Span<float> cuts, ref int count,
        ref double error)
    {
        if (query.Delta.Y == 0) return;
        double at = (y - start.Y) / query.Delta.Y;
        if (!(at > 0 && at < 1)) return;
        float lower = OrdinaryServedDomain.Lower(at), upper = OrdinaryServedDomain.Upper(at);
        cuts[count++] = lower;
        cuts[count++] = upper;
        error = Math.Max(error, Math.Max(at - lower, upper - at) * Math.Abs(query.Delta.Y));
    }

    void Describe(float enter, float exit, Start start, in MovementMediumSweepQuery query, Span<TraceSpan> output,
        ref int count, ref int contactCount, ref double error)
    {
        double y0 = start.Y + (double)enter * query.Delta.Y;
        double y1 = start.Y + (double)exit * query.Delta.Y;
        double low = Math.Min(y0, y1), high = Math.Max(y0, y1), half = query.Body.HalfHeight;
        double skin = MovementQueryLease.CoverageSkinMetres;
        bool wet = high + half + skin >= _interval.LowerY && low - half - skin <= _interval.UpperY;
        bool dry = low - half - skin < _interval.LowerY || high + half + skin > _interval.UpperY;
        float fraction = enter;
        if (wet && !(y0 + half + skin >= _interval.LowerY && y0 - half - skin <= _interval.UpperY))
        {
            double target = y0 < _interval.LowerY ? _interval.LowerY - half - skin : _interval.UpperY + half + skin;
            fraction = Math.Clamp((float)((target - start.Y) / query.Delta.Y), enter, exit);
        }
        double y = start.Y + (double)fraction * query.Delta.Y;
        double x = start.X + (double)fraction * query.Delta.X;
        double z = start.Z + (double)fraction * query.Delta.Z;
        Vector2 column = new((float)x, (float)z);
        double columnError = Math.Sqrt((column.X - x) * (column.X - x) + (column.Y - z) * (column.Y - z));
        error = Math.Max(error, columnError);
        bool positive = y + half > _interval.LowerY && y - half < _interval.UpperY;
        bool spanPositive = high + half > _interval.LowerY && low - half < _interval.UpperY;
        Vector3 normal = y < ((double)_interval.LowerY + _interval.UpperY) / 2 ? -Vector3.UnitY : Vector3.UnitY;
        output[count++] = new(enter, exit, fraction, column, normal, wet, dry,
            positive ? MovementContactOverlap.Overlapping : MovementContactOverlap.Tangent,
            spanPositive ? MovementContactOverlap.Overlapping : MovementContactOverlap.Tangent);
        if (wet) contactCount++;
    }

    readonly record struct Start(double X, double Y, double Z);

    readonly record struct TraceSpan(float Enter, float Exit, float Fraction, Vector2 Column, Vector3 Normal,
        bool Wet, bool Dry, MovementContactOverlap Overlap, MovementContactOverlap SpanOverlap);
}
