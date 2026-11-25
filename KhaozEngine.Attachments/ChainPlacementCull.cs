using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Immediate consumers share row zero. A bound group can have a placement awaiting velocity for each chain.
internal sealed class ChainPlacementCull(int rowCount, int colliderCount)
{
    private const float DirectionLengthBound = 1.000002f;
    private const float FloatRoundingMargin = 1f / (1 << 19);
    private readonly SweptSphere[] _bounds = new SweptSphere[colliderCount];
    private readonly ChainFloatAxis[] _currentAxes = new ChainFloatAxis[colliderCount];
    private readonly ChainFloatAxis[] _sweptAxes = new ChainFloatAxis[colliderCount];
    private readonly float[] _radiusGrowth = new float[colliderCount];
    private readonly int[] _indices = AllocateIndices(rowCount, colliderCount);

    [ThreadStatic]
    internal static bool DisableForTests;

    internal void SetBounds(int index, in ColliderCapsule current, in ColliderCapsule previous)
    {
        var currentCenter = Midpoint(current.Start, current.End);
        var previousCenter = Midpoint(previous.Start, previous.End);
        var center = Midpoint(currentCenter, previousCenter);
        float currentReach = Vector3.Distance(center, currentCenter) + HalfAxisLength(current) + current.Radius;
        float previousReach = Vector3.Distance(center, previousCenter) + HalfAxisLength(previous) + previous.Radius;
        float radius = MathF.Max(currentReach, previousReach);
        _bounds[index] = new SweptSphere(center, radius + FloatRoundingMargin * (MaxAbs(center) + radius));
        // The chord uses the current radius even at the opening end.
        _radiusGrowth[index] = MathF.Max(0, current.Radius - previous.Radius);
        _currentAxes[index] = new ChainFloatAxis(current.Start, current.End, current.Radius);
        _sweptAxes[index] = ChainFloatAxis.Sweep(current, previous);
    }

    internal ChainCapsuleCandidates Select(int row, Vector3 parent, float length, float chainRadius,
        scoped in ChainCapsuleCandidates capsules)
    {
        if (capsules.Count == 0 || _indices.Length == 0 || row >= rowCount || DisableForTests)
            return capsules;

        // Cover the admitted direction length and float rounding in the sphere and distance test.
        float reach = length * DirectionLengthBound + chainRadius + (float)ChainCapsuleCollision.TouchTolerance +
            FloatRoundingMargin * (MaxAbs(parent) + length + chainRadius);
        var indices = Indices(row);
        int count = 0;
        for (int index = 0; index < capsules.Count; index++)
        {
            int original = capsules.OriginalIndex(index);
            if (_bounds[original].CanReach(parent, reach))
                indices[count++] = original;
        }

        return Selected(row, capsules, count);
    }

    internal static SweptSphere BandReach(Vector3 firstParent, float firstLength,
        Vector3 secondParent, float secondLength, Vector3 firstUpper, Vector3 secondUpper, float radius,
        Vector3 openingFirstUpper, Vector3 openingSecondUpper, Vector3 openingFirstLower, Vector3 openingSecondLower,
        (Vector3 A, Vector3 B, Vector3 C, Vector3 D) prediction)
    {
        // The convex hull bridges the gap between length spheres. The fixed corners of
        // an unequal-length tip band can also be different from the actual tip parents.
        var openingCenter = Midpoint(Midpoint(openingFirstUpper, openingSecondUpper),
            Midpoint(openingFirstLower, openingSecondLower));
        var center = Midpoint(Midpoint(firstParent, secondParent), openingCenter);
        float reach = PlacedBandRadius(center, firstParent, firstLength, secondParent, secondLength, firstUpper, secondUpper);
        reach = MathF.Max(reach, MathF.Max(Vector3.Distance(center, openingFirstUpper),
            Vector3.Distance(center, openingSecondUpper)));
        reach = MathF.Max(reach, MathF.Max(Vector3.Distance(center, openingFirstLower),
            Vector3.Distance(center, openingSecondLower)));
        reach = MathF.Max(reach, MathF.Max(Vector3.Distance(center, prediction.A), Vector3.Distance(center, prediction.B)));
        reach = MathF.Max(reach, MathF.Max(Vector3.Distance(center, prediction.C), Vector3.Distance(center, prediction.D)));
        reach += radius + (float)ChainCapsuleCollision.TouchTolerance;
        return new SweptSphere(center, reach + FloatRoundingMargin * (MaxAbs(center) + reach));
    }

    internal static SweptSphere ClosingBandReach(Vector3 firstParent, float firstLength, Vector3 secondParent,
        float secondLength, Vector3 firstUpper, Vector3 secondUpper)
    {
        var center = Midpoint(firstParent, secondParent);
        return Pad(center, PlacedBandRadius(center, firstParent, firstLength, secondParent, secondLength, firstUpper, secondUpper));
    }

    private static float PlacedBandRadius(Vector3 center, Vector3 firstParent, float firstLength, Vector3 secondParent,
        float secondLength, Vector3 firstUpper, Vector3 secondUpper)
    {
        float moving = MathF.Max(Vector3.Distance(center, firstParent) + firstLength * DirectionLengthBound,
            Vector3.Distance(center, secondParent) + secondLength * DirectionLengthBound);
        return MathF.Max(moving, MathF.Max(Vector3.Distance(center, firstUpper), Vector3.Distance(center, secondUpper)));
    }

    internal bool ReachesBand(int index, in SweptSphere band)
    {
        var counts = ChainContactCounts.Active;
        counts?.Record(ChainContactQuery.BandFilter);
        bool reaches = DisableForTests || (_bounds[index].CanReach(band.Center, band.Radius + _radiusGrowth[index]) &&
            (!AxisCullEnabled || ReachesAxis(index, band, swept: true)));
        if (reaches)
            counts?.Record(ChainContactQuery.BandCandidate);
        return reaches;
    }

    internal bool ReachesTight(int index, in SweptSphere reach) =>
        (uint)index >= (uint)_bounds.Length || _bounds[index].Radius == 0 ||
        _bounds[index].CanReach(reach.Center, reach.Radius + _radiusGrowth[index]);

    internal static bool AxisCullEnabled =>
        !DisableForTests && ChainContactOptimizations.Use(ChainContactOptimization.AxisCull);

    internal bool ReachesAxis(int index, in SweptSphere reach, bool swept)
    {
        var axes = swept ? _sweptAxes : _currentAxes;
        return (uint)index >= (uint)axes.Length || axes[index].Reaches(reach.Center, reach.Radius);
    }

    internal static SweptSphere TriangleReach((Vector3 A, Vector3 B, Vector3 C) current,
        (Vector3 A, Vector3 B, Vector3 C) opening, (Vector3 A, Vector3 B, Vector3 C) prediction,
        float radius, bool includeOpening)
    {
        var center = Centroid(current);
        if (includeOpening)
            center = Midpoint(center, Centroid(opening));

        float reach = Farthest(current, center);
        if (includeOpening)
            reach = MathF.Max(reach, MathF.Max(Farthest(opening, center), Farthest(prediction, center)));
        return Pad(center, reach + radius);

        static Vector3 Centroid((Vector3 A, Vector3 B, Vector3 C) triangle) =>
            triangle.A / 3 + triangle.B / 3 + triangle.C / 3;

        static float Farthest((Vector3 A, Vector3 B, Vector3 C) triangle, Vector3 center) =>
            MathF.Max(Vector3.Distance(triangle.A, center),
                MathF.Max(Vector3.Distance(triangle.B, center), Vector3.Distance(triangle.C, center)));
    }

    internal static SweptSphere SegmentReach(in ChainSegmentContact.Segment segment,
        in ChainSegmentContact.Segment opening, float radius, bool includeOpening,
        ChainSegmentContact.Segment? prediction = null)
    {
        var start = segment.Root.ToVector3();
        var end = (segment.Root + segment.Displacement).ToVector3();
        var center = Midpoint(start, end);
        var openingStart = opening.Root.ToVector3();
        var openingEnd = (opening.Root + opening.Displacement).ToVector3();
        if (includeOpening)
            center = Midpoint(center, Midpoint(openingStart, openingEnd));

        float reach = MathF.Max(Vector3.Distance(start, center), Vector3.Distance(end, center));
        if (includeOpening)
            reach = MathF.Max(reach, MathF.Max(Vector3.Distance(openingStart, center), Vector3.Distance(openingEnd, center)));
        if (prediction is { } raw)
        {
            reach = MathF.Max(reach, MathF.Max(Vector3.Distance(center, raw.Root.ToVector3()),
                Vector3.Distance(center, (raw.Root + raw.Displacement).ToVector3())));
        }
        return Pad(center, reach + radius);
    }

    private static SweptSphere Pad(Vector3 center, float radius) =>
        new(center, radius + FloatRoundingMargin * (MaxAbs(center) + radius));

    internal ChainCapsuleCandidates Selected(int row, scoped in ChainCapsuleCandidates capsules, int count) =>
        count == capsules.Count ? capsules : capsules.WithIndices(Indices(row)[..count]);

    private Span<int> Indices(int row) => _indices.AsSpan(row * colliderCount, colliderCount);

    private static int[] AllocateIndices(int rows, int colliders)
    {
        long count = (long)rows * colliders;
        if (count > Array.MaxLength)
            return [];

        // This is an optional filter. A rig can still run if its scratch cannot be allocated.
        try
        {
            return new int[(int)count];
        }
        catch (OutOfMemoryException)
        {
            return [];
        }
    }

    private static Vector3 Midpoint(Vector3 a, Vector3 b) => 0.5f * a + 0.5f * b;

    private static float HalfAxisLength(in ColliderCapsule capsule) =>
        Vector3.Distance(0.5f * capsule.Start, 0.5f * capsule.End);

    private static float MaxAbs(Vector3 point)
    {
        var absolute = Vector3.Abs(point);
        return MathF.Max(absolute.X, MathF.Max(absolute.Y, absolute.Z));
    }

    internal readonly record struct SweptSphere(Vector3 Center, float Radius)
    {
        internal bool CanReach(Vector3 parent, float reach)
        {
            float limit = Radius + reach;
            // Overlapping infinities and indeterminate values stay in the candidate list.
            return !(Vector3.DistanceSquared(parent, Center) > limit * limit);
        }
    }
}
