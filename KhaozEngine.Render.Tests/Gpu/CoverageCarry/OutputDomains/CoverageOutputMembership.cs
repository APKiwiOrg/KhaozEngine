using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Tests.Gpu;

internal readonly record struct CoverageOutputMembershipCost(long BoundComparisons, long WordComparisons, long IntervalNodes);

// Only the independent checker creates this owned immutable certificate. Per-frame admission
// does not hash or recheck the geometric proof, and it never accepts a predicted pose.
internal sealed class CoverageOutputVerified
{
    readonly CoverageOutputCell cell;
    readonly int[] representatives;
    internal CoverageOutputVerified(CoverageOutputCell checkedCell)
    {
        cell = CoverageOutputCapture.Clone(checkedCell);
        representatives = Enumerable.Repeat(-1, cell.Anchor.Topology.PositionGroupCount).ToArray();
        for (int v = 0; v < cell.Anchor.Topology.VertexCount; v++)
        {
            int g = cell.Anchor.Topology.Vertices[v].PositionGroup;
            if (representatives[g] < 0) representatives[g] = v;
        }
    }
    internal CoverageOutputCell Snapshot() => CoverageOutputCapture.Clone(cell);
    internal bool Admits(CoverageOutputFrame frame, out CoverageOutputMembershipCost cost, int layer = 0) =>
        CoverageOutputMembership.Admits(cell, frame, representatives, out cost, layer);
}

internal static class CoverageOutputMembership
{
    internal static bool Transform(Matrix4x4 matrix, double limit, out CoverageBoundedAffine inverse, out long nodes)
    {
        inverse = default; nodes = 0;
        if (!(limit > 0 && limit < .1) || !CoverageAffine.TryFrom(matrix, out var affine)
            || Math.Abs(matrix.M41) > 4 || Math.Abs(matrix.M42) > 4 || Math.Abs(matrix.M43) > 4) return false;
        var a = CoverageBoundedAffine.From(affine);
        Span<CoverageBoundedVector> rows = stackalloc CoverageBoundedVector[] { a.R1, a.R2, a.R3 };
        double defect = 0;
        for (int i = 0; i < 3; i++)
        {
            double sum = 0;
            for (int j = 0; j < 3; j++)
            {
                var value = Subtract(CoverageBoundedVector.Dot(rows[i], rows[j]), new(i == j ? 1 : 0, i == j ? 1 : 0));
                sum = Math.BitIncrement(sum + Math.Max(Math.Abs(value.Min), Math.Abs(value.Max)));
                nodes += 6;
            }
            defect = Math.Max(defect, sum);
        }
        nodes += 59;
        return defect <= limit && a.TryInverse(out inverse);
    }

    internal static bool Admits(CoverageOutputCell c, CoverageOutputFrame frame, int[] representatives, out CoverageOutputMembershipCost cost, int layer)
    {
        long comparisons = 0, words = 0, nodes = 0;
        bool Finish(bool result, out CoverageOutputMembershipCost counted)
        { counted = new(comparisons, words, nodes); return result; }
        var t = c.Anchor.Topology; var p = frame.Command; var expected = c.Command;
        if (layer != c.Layer || layer != 0 || frame.Before.Length != t.VertexCount || frame.After.Length != t.VertexCount
            || !ReferenceEquals(p.Topology, t) || !ReferenceEquals(p.Support, expected.Support)
            || p.Id != expected.Id || p.EquipmentInstanceId != expected.EquipmentInstanceId || p.RegionIndex != expected.RegionIndex
            || p.MinZ != expected.MinZ || p.MaxZ != expected.MaxZ || p.CollarWidth != expected.CollarWidth
            || p.Clearance != expected.Clearance || p.MaxWorldRecession != expected.MaxWorldRecession
            || p.TransitionWeight != expected.TransitionWeight || p.CoverageWorld != p.Support.CoverageLocal * p.EquipmentWorld
            || !Transform(p.EquipmentWorld, c.GramDefect, out var inverse, out nodes)) return Finish(false, out cost);
        for (int v = 0; v < t.VertexCount; v++)
        {
            var before = frame.Before[v]; var after = frame.After[v];
            int g = t.Vertices[v].PositionGroup;
            if (c.Copies[v])
            {
                words += Marshal.SizeOf<ModelVertex>() / 4;
                if (!MemoryMarshal.AsBytes(new ReadOnlySpan<ModelVertex>(in before))
                    .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<ModelVertex>(in after)))) return Finish(false, out cost);
            }
            if (representatives[g] != v)
            {
                words += 6;
                if (!SamePosition(before.Position, frame.Before[representatives[g]].Position)
                    || !SamePosition(after.Position, frame.After[representatives[g]].Position)) return Finish(false, out cost);
                continue;
            }
            var b = inverse.Apply(before.Position); nodes += 18; comparisons += 6;
            if (!Inside(b, c.Before[g])) return Finish(false, out cost);
            if (c.Copies[v]) continue;
            var a = inverse.Apply(after.Position); nodes += 18; comparisons += 6;
            if (!Inside(a, c.After[g])) return Finish(false, out cost);
        }
        return Finish(true, out cost);
    }

    static bool SamePosition(Vector3 a, Vector3 b) => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
    static bool Inside(CoverageBoundedVector p, CoverageBoundedVector box) => p.IsFinite && box.IsFinite
        && p.X.Min >= box.X.Min && p.X.Max <= box.X.Max && p.Y.Min >= box.Y.Min && p.Y.Max <= box.Y.Max
        && p.Z.Min >= box.Z.Min && p.Z.Max <= box.Z.Max;
}
