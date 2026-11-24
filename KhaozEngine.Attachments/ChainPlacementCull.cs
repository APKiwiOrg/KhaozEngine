using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// A bound group can have one placement awaiting velocity constraints for each chain.
internal sealed class ChainPlacementCull(int chainCount, int colliderCount)
{
    private const float DirectionLengthBound = 1.000002f;
    private const float FloatRoundingMargin = 1f / (1 << 19);
    private readonly SweptSphere[] _bounds = new SweptSphere[colliderCount];
    private readonly int[] _indices = new int[checked(chainCount * colliderCount)];

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

    internal ChainCapsuleCandidates Select(int chain, Vector3 parent, float length, float chainRadius,
        ChainCapsuleCandidates capsules)
    {
        if (capsules.Count == 0 || DisableForTests)
            return capsules;

        // Cover the admitted direction length and float rounding in the sphere and distance test.
        float reach = length * DirectionLengthBound + chainRadius + (float)ChainCapsuleCollision.TouchTolerance +
            FloatRoundingMargin * (MaxAbs(parent) + length + chainRadius);
        var indices = Indices(chain);
        int count = 0;
        for (int index = 0; index < capsules.Count; index++)
        {
            int original = capsules.OriginalIndex(index);
            if (_bounds[original].CanReach(parent, reach))
                indices[count++] = original;
        }

        return Selected(chain, capsules, count);
    }

    internal ChainCapsuleCandidates Selected(int chain, ChainCapsuleCandidates capsules, int count) =>
        count == capsules.Count ? capsules : capsules.WithIndices(Indices(chain)[..count]);

    private Span<int> Indices(int chain) => _indices.AsSpan(chain * colliderCount, colliderCount);

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
