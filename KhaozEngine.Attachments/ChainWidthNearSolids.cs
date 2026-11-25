using System;
using System.Numerics;
using static KhaozEngine.Attachments.ChainCapsuleCollision;

namespace KhaozEngine.Attachments;

// Width projection can revisit a parent while the tier's solid axes remain fixed.
internal sealed class ChainWidthNearSolids
{
    private readonly int[] _rowByChain;
    private readonly Row[] _rows;
    private readonly Solid[] _solids;
    private readonly int _capacity;

    private ChainWidthNearSolids(int chains, int rows, int capacity)
    {
        _rowByChain = new int[chains];
        _rows = new Row[rows];
        _solids = new Solid[rows * capacity];
        _capacity = capacity;
    }

    // A tier without solid obstacles has nothing to cache.
    internal static ChainWidthNearSolids? TryCreate(int chains, int rows, int capacity) =>
        capacity == 0 ? null : new ChainWidthNearSolids(chains, rows, capacity);

    internal void SetRow(int chain, int row) => _rowByChain[chain] = row;

    internal void BeginProjection() => Array.Clear(_rows);

    internal Candidates Select(ChainParticles particles, ReadOnlySpan<ColliderCapsule> solids, int chain,
        Vector3 parent, float length, float radius)
    {
        if (solids.Length > _capacity || !float.IsFinite(length))
            return new Candidates(solids.Length);

        int index = _rowByChain[chain];
        ref var row = ref _rows[index];
        var entries = _solids.AsSpan(index * _capacity, solids.Length);
        if (!row.Ready || !SameBits(row.Parent, parent) || row.Length != length || row.Radius != radius)
            row = new Row(parent, length, radius, Fill(particles, solids, entries, parent, length, radius), Ready: true);

        double storedReach = (double)length + ChainWidthClearance.PositionTolerance;
        return new Candidates(solids.Length, entries[..row.Count], storedReach * storedReach);
    }

    private static int Fill(ChainParticles particles, ReadOnlySpan<ColliderCapsule> solids, Span<Solid> entries,
        Vector3 parent, float length, float radius)
    {
        var root = new Offset(parent);
        int count = 0;
        for (int index = 0; index < solids.Length; index++)
        {
            ref readonly var obstacle = ref solids[index];
            double inflated = (double)radius + obstacle.Radius;
            double reach = length + inflated + ChainWidthClearance.PositionTolerance;
            // Give the stored-segment query room for the axis projection's double rounding.
            reach += ReachRoundingMargin(parent, obstacle);
            double squared = particles.CapsuleAxes[index].Residual(root, default, out _).LengthSquared;
            if (!(squared > reach * reach))
                entries[count++] = new Solid(index, squared);
        }
        return count;
    }

    private static bool SameBits(Vector3 left, Vector3 right) =>
        BitConverter.SingleToInt32Bits(left.X) == BitConverter.SingleToInt32Bits(right.X) &&
        BitConverter.SingleToInt32Bits(left.Y) == BitConverter.SingleToInt32Bits(right.Y) &&
        BitConverter.SingleToInt32Bits(left.Z) == BitConverter.SingleToInt32Bits(right.Z);

    private readonly record struct Row(Vector3 Parent, float Length, float Radius, int Count, bool Ready);

    internal readonly record struct Solid(int Index, double ParentDistanceSquared);

    internal readonly ref struct Candidates(int fullCount, ReadOnlySpan<Solid> near = default, double reachSquared = -1)
    {
        private readonly ReadOnlySpan<Solid> _near = near;
        private bool IsFiltered => reachSquared >= 0;
        internal int Count => IsFiltered ? _near.Length : fullCount;

        internal Candidates ForStored(in Offset displacement) =>
            !IsFiltered || displacement.LengthSquared <= reachSquared ? this : new Candidates(fullCount);

        internal int Index(int row) => IsFiltered ? _near[row].Index : row;

        internal double ParentDistanceSquared(int row, in Axis axis, Offset parent) =>
            IsFiltered ? _near[row].ParentDistanceSquared : axis.Residual(parent, default, out _).LengthSquared;
    }
}
