using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainCapsuleCull
{
    // Worn motion caps a wearer at 64 colliders. Standalone simulations may supply more.
    private readonly int[] _indices = new int[64];

    // The squared-length check leaves room for its own float rounding in this upper bound.
    private const double DirectionLengthBound = 1.000002;
    private const double PositionRoundingBound = 1d / (1 << 21);

    [ThreadStatic]
    internal static bool DisableForTests;

    internal ChainCapsuleCandidates Select(Vector3 root, ReadOnlySpan<float> lengths, float chainRadius,
        ReadOnlySpan<ColliderCapsule> colliders)
    {
        var all = new ChainCapsuleCandidates(colliders);
        if (colliders.IsEmpty || colliders.Length > _indices.Length || DisableForTests)
            return all;

        double reach = 0;
        foreach (float length in lengths)
            reach += length;
        reach *= DirectionLengthBound;
        var absoluteRoot = Vector3.Abs(root);
        double positionScale = Math.Max(absoluteRoot.X, Math.Max(absoluteRoot.Y, absoluteRoot.Z)) + reach;
        if (!(positionScale < float.MaxValue / 2d))
            return all;

        // Each world-space float addition can round the next joint away from the root.
        reach += lengths.Length * PositionRoundingBound * positionScale;
        double contactReach = reach + chainRadius + ChainCapsuleCollision.TouchTolerance;
        int count = 0;
        for (int index = 0; index < colliders.Length; index++)
        {
            if (!ChainCapsuleCollision.BeyondReach(root, contactReach, colliders[index]))
                _indices[count++] = index;
        }

        return count == colliders.Length ? all : new ChainCapsuleCandidates(colliders, _indices.AsSpan(0, count));
    }

    internal ChainCapsuleCandidates Selected(ReadOnlySpan<ColliderCapsule> colliders, int count) =>
        count == colliders.Length ? new ChainCapsuleCandidates(colliders) : new ChainCapsuleCandidates(colliders, _indices.AsSpan(0, count));

    internal static bool FitsReach(Vector3 direction) => direction.LengthSquared() <= 1.000002f;
}

internal readonly ref struct ChainCapsuleCandidates
{
    private readonly ReadOnlySpan<ColliderCapsule> _colliders;
    private readonly ReadOnlySpan<int> _indices;

    internal ChainCapsuleCandidates(ReadOnlySpan<ColliderCapsule> colliders)
    {
        _colliders = colliders;
        Count = colliders.Length;
    }

    internal ChainCapsuleCandidates(ReadOnlySpan<ColliderCapsule> colliders, ReadOnlySpan<int> indices)
    {
        _colliders = colliders;
        _indices = indices;
        Count = indices.Length;
    }

    internal int Count { get; }
    internal bool IsFiltered => Count != _colliders.Length;
    internal ChainCapsuleCandidates All => new(_colliders);
    internal int OriginalIndex(int index) => IsFiltered ? _indices[index] : index;
    internal ref readonly ColliderCapsule this[int index] => ref _colliders[OriginalIndex(index)];
}
