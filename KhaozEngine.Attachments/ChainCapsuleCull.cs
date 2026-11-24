using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal sealed class ChainCapsuleCull(int colliderCount)
{
    private readonly int[] _indices = new int[colliderCount];
    private ChainCapsuleCollision.Axis[]? _axes;

    // The squared-length check leaves room for its own float rounding in this upper bound.
    private const double DirectionLengthBound = 1.000002;
    private const double PositionRoundingBound = 1d / (1 << 21);

    [ThreadStatic]
    internal static bool DisableForTests;

    internal ChainCapsuleCandidates Select(Vector3 root, ReadOnlySpan<float> lengths, float chainRadius,
        ReadOnlySpan<ColliderCapsule> colliders, ChainCapsuleCollision.Axis[]? axes = null)
    {
        _axes = axes;
        var all = new ChainCapsuleCandidates(colliders, axes);
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
        ChainCapsuleCollision.Axis scratch = default;
        for (int index = 0; index < colliders.Length; index++)
        {
            ref readonly var axis = ref all.ReadAxis(index, ref scratch);
            if (!ChainCapsuleCollision.BeyondReach(root, contactReach, colliders[index], axis))
                _indices[count++] = index;
        }

        return count == colliders.Length ? all : new ChainCapsuleCandidates(colliders, axes, _indices.AsSpan(0, count));
    }

    internal ChainCapsuleCandidates Selected(ReadOnlySpan<ColliderCapsule> colliders, int count) =>
        count == colliders.Length ? new ChainCapsuleCandidates(colliders, _axes) :
            new ChainCapsuleCandidates(colliders, _axes, _indices.AsSpan(0, count));

    internal static bool FitsReach(Vector3 direction) => direction.LengthSquared() <= 1.000002f;
}

internal readonly ref struct ChainCapsuleCandidates
{
    private readonly ReadOnlySpan<ColliderCapsule> _colliders;
    private readonly ReadOnlySpan<ChainCapsuleCollision.Axis> _axes;
    private readonly ReadOnlySpan<int> _indices;

    internal ChainCapsuleCandidates(ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ChainCapsuleCollision.Axis> axes = default)
    {
        _colliders = colliders;
        _axes = axes;
        Count = colliders.Length;
    }

    internal ChainCapsuleCandidates(ReadOnlySpan<ColliderCapsule> colliders,
        ReadOnlySpan<ChainCapsuleCollision.Axis> axes, ReadOnlySpan<int> indices)
    {
        _colliders = colliders;
        _axes = axes;
        _indices = indices;
        Count = indices.Length;
    }

    internal int Count { get; }
    internal int ObstacleCount => _colliders.Length;
    internal bool IsFiltered => Count != _colliders.Length;
    internal ChainCapsuleCandidates All => new(_colliders, _axes);
    internal ChainCapsuleCandidates WithIndices(ReadOnlySpan<int> indices) => new(_colliders, _axes, indices);
    internal int OriginalIndex(int index) => IsFiltered ? _indices[index] : index;
    internal ref readonly ColliderCapsule this[int index] => ref _colliders[OriginalIndex(index)];

    internal ref readonly ChainCapsuleCollision.Axis ReadAxis(int index, ref ChainCapsuleCollision.Axis scratch)
    {
        int original = OriginalIndex(index);
        if (!_axes.IsEmpty)
            return ref _axes[original];

        scratch = new ChainCapsuleCollision.Axis(_colliders[original]);
        return ref scratch;
    }
}
