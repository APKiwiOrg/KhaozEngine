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

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body)
    {
        if (!body.IsValid || body.CurrentSpace.WorldId != _worldId)
            return new(MovementAvailability.Invalid, default, null, false, 0, null);
        double feet = (double)body.Centre.Y - body.HalfHeight;
        bool wet = feet >= _interval.LowerY && feet < _interval.UpperY;
        return new(MovementAvailability.Known, body.CurrentSpace, wet ? _domain : null, wet, 1, wet ? _interval : null);
    }

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, in MovementQueryIdentity identity,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        if (!query.IsValid || !identity.IsValid || query.Body.CurrentSpace.WorldId != _worldId)
            return new(MovementAvailability.Invalid, 0, 0, 0, 0, 0, identity);
        double half = query.Body.HalfHeight, skin = MovementQueryLease.CoverageSkinMetres;
        Span<float> cuts = stackalloc float[34];
        int cutCount = 2;
        cuts[0] = 0;
        cuts[1] = 1;
        double error = 0;
        for (int planeIndex = 0; planeIndex < 2; planeIndex++)
        {
            float plane = planeIndex == 0 ? _interval.LowerY : _interval.UpperY;
            AddCut(plane - half - skin, query, cuts, ref cutCount, ref error);
            AddCut(plane + half + skin, query, cuts, ref cutCount, ref error);
            AddCut(plane - half, query, cuts, ref cutCount, ref error);
            AddCut(plane + half, query, cuts, ref cutCount, ref error);
        }
        cuts[..cutCount].Sort();
        int unique = 0;
        for (int i = 0; i < cutCount; i++)
            if (unique == 0 || cuts[i] != cuts[unique - 1]) cuts[unique++] = cuts[i];
        Span<TraceSpan> output = stackalloc TraceSpan[67];
        int count = 0, contactCount = 0;
        for (int i = 0; i < unique; i++)
        {
            Describe(cuts[i], cuts[i], query, output, ref count, ref contactCount, ref error);
            if (i + 1 < unique) Describe(cuts[i], cuts[i + 1], query, output, ref count, ref contactCount, ref error);
        }
        float certified = OrdinaryServedDomain.Upper(error);
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

    static void AddCut(double y, in MovementMediumSweepQuery query, Span<float> cuts, ref int count, ref double error)
    {
        if (query.Delta.Y == 0) return;
        double at = (y - query.Body.Centre.Y) / query.Delta.Y;
        if (!(at > 0 && at < 1)) return;
        float lower = OrdinaryServedDomain.Lower(at), upper = OrdinaryServedDomain.Upper(at);
        cuts[count++] = lower;
        cuts[count++] = upper;
        error = Math.Max(error, Math.Max(at - lower, upper - at) * Math.Abs(query.Delta.Y));
    }

    void Describe(float enter, float exit, in MovementMediumSweepQuery query, Span<TraceSpan> output,
        ref int count, ref int contactCount, ref double error)
    {
        double y0 = query.Body.Centre.Y + (double)enter * query.Delta.Y;
        double y1 = query.Body.Centre.Y + (double)exit * query.Delta.Y;
        double low = Math.Min(y0, y1), high = Math.Max(y0, y1), half = query.Body.HalfHeight;
        double skin = MovementQueryLease.CoverageSkinMetres;
        bool wet = high + half + skin >= _interval.LowerY && low - half - skin <= _interval.UpperY;
        bool dry = low - half - skin < _interval.LowerY || high + half + skin > _interval.UpperY;
        float fraction = enter;
        if (wet && !(y0 + half + skin >= _interval.LowerY && y0 - half - skin <= _interval.UpperY))
        {
            double target = y0 < _interval.LowerY ? _interval.LowerY - half - skin : _interval.UpperY + half + skin;
            fraction = Math.Clamp((float)((target - query.Body.Centre.Y) / query.Delta.Y), enter, exit);
        }
        double y = query.Body.Centre.Y + (double)fraction * query.Delta.Y;
        double x = query.Body.Centre.X + (double)fraction * query.Delta.X;
        double z = query.Body.Centre.Z + (double)fraction * query.Delta.Z;
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

    readonly record struct TraceSpan(float Enter, float Exit, float Fraction, Vector2 Column, Vector3 Normal,
        bool Wet, bool Dry, MovementContactOverlap Overlap, MovementContactOverlap SpanOverlap);
}
