using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Immediate consumers share row zero. A bound group can have a placement awaiting velocity for each chain.
internal sealed class ChainPlacementCull(int rowCount, int colliderCount)
{
    private const float DirectionLengthBound = 1.000002f;
    private const float FloatRoundingMargin = 1f / (1 << 19);
    private readonly SweptSphere[] _bounds = new SweptSphere[colliderCount];
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
    }

    internal ChainCapsuleCandidates Select(int row, Vector3 parent, float length, float chainRadius,
        ChainCapsuleCandidates capsules)
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

    internal ChainCapsuleCandidates Selected(int row, ChainCapsuleCandidates capsules, int count) =>
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

    private readonly record struct SweptSphere(Vector3 Center, float Radius)
    {
        internal bool CanReach(Vector3 parent, float reach)
        {
            float limit = Radius + reach;
            // Overlapping infinities and indeterminate values stay in the candidate list.
            return !(Vector3.DistanceSquared(parent, Center) > limit * limit);
        }
    }
}
