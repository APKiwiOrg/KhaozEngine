using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageOutputBuilder
{
    internal static CoverageOutputCell Propose(CoverageOutputCell seed)
    {
        var t = seed.Anchor.Topology;
        CoverageBoundedVector[] Triangle(CoverageBoundedVector[] points, int f) =>
            Enumerable.Range(0, 3).Select(i => points[t.Vertices[(int)t.Indices[f * 3 + i]].PositionGroup]).ToArray();
        var after = Enumerable.Range(0, t.TriangleCount).Select(f => Triangle(seed.After, f)).ToArray();
        var groups = Enumerable.Range(0, t.TriangleCount).Select(f => CoverageOutputGeometry.Groups(t, f)).ToArray();
        var changed = Enumerable.Range(0, t.TriangleCount).Select(f => Enumerable.Range(0, 3)
            .Any(i => !seed.Copies[(int)t.Indices[f * 3 + i]])).ToArray();
        var planes = new List<CoverageOutputPlane>();
        void Pair(int kind, int f, int g, CoverageBoundedVector[] a, CoverageBoundedVector[] b, int[]? ag, int[]? bg, double required)
        {
            if (CoverageOutputGeometry.CoordinateGap(a, b) > required) return;
            Vector3 best = default; double bestMargin = double.NegativeInfinity;
            var axes = kind == 2 && ag!.Intersect(bg!).Count() == 2
                ? new[] { Vector3.UnitZ } : CoverageOutputGeometry.Axes(a, b, kind == 2);
            foreach (var axis in axes)
            {
                double margin = kind == 0 ? CoverageOutputGeometry.Gap(a, b, axis)
                    : CoverageOutputGeometry.HostMargin(a, b, ag!, bg!, axis);
                if (margin > bestMargin) { bestMargin = margin; best = axis; }
                if (margin > required) break;
            }
            planes.Add(new(kind, f, g, best));
        }
        var s = seed.Command.Support.Source;
        var solid = s.Triangles.Select(f => new[] { CoverageBoundedVector.From(s.Positions[f.A]),
            CoverageBoundedVector.From(s.Positions[f.B]), CoverageBoundedVector.From(s.Positions[f.C]) }).ToArray();
        double sigma = Math.Sqrt(1 - seed.GramDefect);
        for (int f = 0; f < t.TriangleCount; f++)
            for (int g = 0; g < solid.Length; g++)
                Pair(0, f, g, after[f], solid[g], null, null,
                    t.Triangles[f].RegionIndex == seed.Command.RegionIndex ? seed.Command.Clearance * 1.25 / sigma : 0);
        for (int f = 0; f < t.TriangleCount; f++)
            for (int g = f + 1; g < t.TriangleCount; g++)
                if (changed[f] || changed[g]) Pair(1, f, g, after[f], after[g], groups[f], groups[g], 0);
        if (!CoverageAffine.TryFrom(seed.Command.Support.CoverageLocal, out var local)
            || !CoverageBoundedAffine.From(local).TryInverse(out var inverse)) return seed;
        var projected = after.Select(face => face.Select(p => inverse.Apply(p) with { Z = Zero }).ToArray()).ToArray();
        for (int f = 0; f < t.TriangleCount; f++)
            for (int g = f + 1; g < t.TriangleCount; g++)
                if (t.Triangles[f].RegionIndex == seed.Command.RegionIndex && t.Triangles[g].RegionIndex == seed.Command.RegionIndex)
                    Pair(2, f, g, projected[f], projected[g], groups[f], groups[g], 0);
        var result = seed with { Planes = planes.ToArray() };
        return result with { Digest = CoverageOutputBinding.Domain(result) };
    }
}
