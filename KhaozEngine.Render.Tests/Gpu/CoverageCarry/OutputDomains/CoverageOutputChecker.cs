using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Tests.Gpu;

// Deliberately independent of the proposal search and every sampled pose verdict.
internal static class CoverageOutputChecker
{
    internal static CoverageOutputCheck Check(CoverageOutputCell c, string expectedBinding)
    {
        long operations = 0;
        var best = new CoverageOutputCheck(true, "none", -1, -1, double.PositiveInfinity, 0);
        bool Note(string cause, int f, int g, double margin)
        {
            operations++;
            if (!double.IsFinite(margin) || !(margin > 0))
            { best = new(false, cause, f, g, margin, operations); return false; }
            if (margin < best.Margin) best = new(true, cause, f, g, margin, operations);
            return true;
        }
        var t = c.Anchor.Topology; var command = c.Command;
        if (c.Layer != 0 || !ReferenceEquals(command.Topology, t) || command.TransitionWeight != 1
            || command.Support.CoverageLocal != Matrix4x4.Identity || c.InverseBind.Length == 0
            || c.Before.Length != t.PositionGroupCount || c.After.Length != t.PositionGroupCount
            || c.Before.Any(p => !p.IsFinite) || c.After.Any(p => !p.IsFinite)
            || c.Binding != expectedBinding || CoverageOutputBinding.Inputs(c) != expectedBinding
            || !CoverageOutputIdentity.Check(c.Anchor, command, c.Copies)
            || !(c.GramDefect > 0 && c.GramDefect < .1)
            || command.CoverageWorld != command.Support.CoverageLocal * command.EquipmentWorld
            || !CoverageOutputMembership.Transform(command.EquipmentWorld, c.GramDefect, out _, out _))
            return new(false, "input/copy binding", -1, -1, 0, operations);
        if (CoverageOutputBinding.Domain(c) != c.Digest) return new(false, "domain digest", -1, -1, 0, operations);
        for (int v = 0; v < t.VertexCount; v++)
        {
            var r = t.Vertices[v];
            if ((r.Strength == 0 || r.RegionIndex < 0) && !c.Copies[v])
                return new(false, "zero boundary", v, -1, 0, operations);
            if (c.Copies[v] && c.Before[r.PositionGroup] != c.After[r.PositionGroup])
                return new(false, "copy domain", v, -1, 0, operations);
        }
        foreach (var alias in t.SourceAliases)
        {
            var a = t.Source.SkinVertices[alias.SourceVertex]; var b = t.Source.SkinVertices[alias.Representative];
            if (a.Position != b.Position || a.BoneWeights != b.BoneWeights || a.BoneIndices != b.BoneIndices)
                return new(false, "alias provenance", alias.SourceVertex, alias.Representative, 0, operations);
        }
        CoverageBoundedVector[] Triangle(CoverageBoundedVector[] points, int f) =>
            Enumerable.Range(0, 3).Select(i => points[t.Vertices[(int)t.Indices[f * 3 + i]].PositionGroup]).ToArray();
        var before = Enumerable.Range(0, t.TriangleCount).Select(f => Triangle(c.Before, f)).ToArray();
        var after = Enumerable.Range(0, t.TriangleCount).Select(f => Triangle(c.After, f)).ToArray();
        var groups = Enumerable.Range(0, t.TriangleCount).Select(f => CoverageOutputGeometry.Groups(t, f)).ToArray();
        var changed = Enumerable.Range(0, t.TriangleCount).Select(f => Enumerable.Range(0, 3)
            .Any(i => !c.Copies[(int)t.Indices[f * 3 + i]])).ToArray();
        var planes = new Dictionary<(int, int, int), Vector3>();
        foreach (var p in c.Planes)
            if (p.Kind < 0 || p.Kind > 2 || p.Triangle < 0 || p.Triangle >= t.TriangleCount
                || !planes.TryAdd((p.Kind, p.Triangle, p.Other), p.Axis))
                return new(false, "plane identity", p.Triangle, p.Other, 0, operations);
        var sigma = Sqrt(Subtract(One, new(c.GramDefect, c.GramDefect)));
        var norm = Sqrt(Add(One, new(c.GramDefect, c.GramDefect)));
        var distortion = Divide(new(c.GramDefect, c.GramDefect), Subtract(One, new(c.GramDefect, c.GramDefect)));
        int sign = 0;
        for (int f = 0; f < t.TriangleCount; f++)
        {
            var u = after[f][1] - after[f][0]; var v = after[f][2] - after[f][0];
            var normal = CoverageBoundedVector.Cross(u, v);
            if (changed[f])
            {
                var original = CoverageBoundedVector.Cross(before[f][1] - before[f][0], before[f][2] - before[f][0]);
                double fold = Subtract(CoverageBoundedVector.Dot(normal, original),
                    Multiply(distortion, Multiply(normal.Length, original.Length))).Min;
                if (!Note("3D fold with affine distortion", f, -1, fold)) return best;
                double area = Subtract(Multiply(Square(sigma), normal.Length),
                    Multiply(new(1e-12, 1e-12), Multiply(Square(norm), Multiply(u.Length, v.Length)))).Min;
                if (!Note("3D area", f, -1, area)) return best;
            }
            if (t.Triangles[f].RegionIndex != command.RegionIndex) continue;
            int current = normal.Z.Min > 0 ? 1 : normal.Z.Max < 0 ? -1 : 0;
            if (sign == 0) sign = current;
            if (current == 0 || current != sign)
                return new(false, "projected orientation includes zero or opposing sign", f, -1,
                    Math.Min(normal.Z.Min, -normal.Z.Max), operations, normal.Z.Min, normal.Z.Max);
            double projected = Subtract(Divide(current > 0 ? normal.Z : Negate(normal.Z), Multiply(u.Length, v.Length)), new(1e-9, 1e-9)).Min;
            if (!Note("projected injective fold", f, -1, projected)) return best;
        }
        double cap = Divide(new(command.Clearance, command.Clearance), sigma).Max;
        if (!CoverageAffine.TryFrom(command.Support.SourceToCoverage, out var compiled))
            return new(false, "compiled projection", -1, -1, 0, operations);
        var actualSheet = CoverageBoundedAffine.From(compiled);
        double slope = CoverageOutputGeometry.SlopeUpper(command.Support);
        if (!double.IsFinite(slope)) return new(false, "support slope", -1, -1, 0, operations);
        for (int v = 0; v < t.VertexCount; v++)
        {
            var r = t.Vertices[v]; if (r.RegionIndex != command.RegionIndex || r.Strength == 0) continue;
            var a = c.After[r.PositionGroup]; var b = c.Before[r.PositionGroup];
            double low = Add(new(command.MinZ, command.MinZ), new(cap, cap)).Max;
            double high = Subtract(new(command.MaxZ, command.MaxZ), new(cap, cap)).Min;
            double caps = Math.Min(Math.Min(a.Z.Min, b.Z.Min) - low, high - Math.Max(a.Z.Max, b.Z.Max));
            if (!Note("caps", v, -1, Math.BitDecrement(caps))) return best;
            double displacement = c.Copies[v] ? 0 : Multiply((a - b).Length, norm).Max;
            if (!Note("total world recession", v, -1, Math.BitDecrement(command.MaxWorldRecession - displacement))) return best;
            if (!t.IsCoreVertex(v)) continue;
            if (!CoverageSheetSide.Contains(command.Support, actualSheet.Apply(a)))
                return new(false, "actual sheet containment", v, -1, 0, operations);
        }
        var s = command.Support.Source;
        var solid = s.Triangles.Select(f => new[] { CoverageBoundedVector.From(s.Positions[f.A]),
            CoverageBoundedVector.From(s.Positions[f.B]), CoverageBoundedVector.From(s.Positions[f.C]) }).ToArray();
        for (int f = 0; f < t.TriangleCount; f++)
            for (int g = 0; g < solid.Length; g++)
            {
                double required = t.Triangles[f].RegionIndex == command.RegionIndex
                    ? Divide(Multiply(new(command.Clearance, command.Clearance), new(1.25, 1.25)), sigma).Max : 0;
                double margin = Math.BitDecrement(CoverageOutputGeometry.CoordinateGap(after[f], solid[g]) - required);
                if (!(margin > 0))
                {
                    planes.TryGetValue((0, f, g), out var axis);
                    margin = Subtract(new(CoverageOutputGeometry.Gap(after[f], solid[g], axis), CoverageOutputGeometry.Gap(after[f], solid[g], axis)), new(required, required)).Min;
                }
                if (!Note("whole-triangle equipment clearance + NC2", f, g, margin)) return best;
            }
        for (int f = 0; f < t.TriangleCount; f++)
            for (int g = f + 1; g < t.TriangleCount; g++)
            {
                if (!changed[f] && !changed[g]) continue;
                double margin = CoverageOutputGeometry.CoordinateGap(after[f], after[g]);
                if (!(margin > 0))
                {
                    planes.TryGetValue((1, f, g), out var axis);
                    margin = CoverageOutputGeometry.HostMargin(after[f], after[g], groups[f], groups[g], axis);
                }
                if (!Note("H1 changed host pair", f, g, margin)) return best;
            }
        var xy = after.Select(face => face.Select(p => p with { Z = Zero }).ToArray()).ToArray();
        for (int f = 0; f < t.TriangleCount; f++)
            for (int g = f + 1; g < t.TriangleCount; g++)
            {
                if (t.Triangles[f].RegionIndex != command.RegionIndex || t.Triangles[g].RegionIndex != command.RegionIndex) continue;
                double margin = CoverageOutputGeometry.CoordinateGap(xy[f], xy[g], true);
                if (!(margin > 0))
                {
                    planes.TryGetValue((2, f, g), out var axis);
                    margin = CoverageOutputGeometry.HostMargin(xy[f], xy[g], groups[f], groups[g], axis);
                }
                if (!Note("projected nonadjacent injectivity", f, g, margin)) return best;
            }
        for (int f = 0; f < t.TriangleCount; f++)
        {
            if (t.Triangles[f].RegionIndex != command.RegionIndex || t.Triangles[f].Role != CoverageTriangleRole.Core) continue;
            var p = actualSheet.Apply(after[f][0]);
            double x = p.X.Midpoint, y = p.Y.Midpoint;
            if (!command.Support.TryGetHeightBounds(CoverageAffinePoint.FromDouble(x, y), out var height))
                return new(false, "sheet side seed", f, -1, 0, operations);
            var radius = Sqrt(Add(Square(Subtract(p.X, new(x, x))), Square(Subtract(p.Y, new(y, y)))));
            double side = Subtract(Subtract(height, Multiply(new(slope, slope), radius)), p.Z).Min;
            if (!Note("actual whole-core sheet side", f, -1, side)) return best;
        }
        if (!Exterior(t, c.After, solid)) return new(false, "solid exterior component", -1, -1, 0, operations);
        return best with { Operations = operations };
    }

    static bool Exterior(CoverageRefinement t, CoverageBoundedVector[] after, CoverageBoundedVector[][] solid)
    {
        var parents = Enumerable.Range(0, t.PositionGroupCount).ToArray(); var used = new bool[parents.Length];
        int Root(int i) { while (parents[i] != i) i = parents[i]; return i; }
        for (int f = 0; f < t.TriangleCount; f++)
        {
            var g = CoverageOutputGeometry.Groups(t, f);
            foreach (int v in g) used[v] = true;
            parents[Root(g[1])] = Root(g[0]); parents[Root(g[2])] = Root(g[0]);
        }
        var points = solid.SelectMany(p => p).ToArray();
        foreach (int component in Enumerable.Range(0, parents.Length).Where(i => used[i]).Select(Root).Distinct())
        {
            bool outside = false;
            foreach (int g in Enumerable.Range(0, parents.Length).Where(i => used[i] && Root(i) == component))
                foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    outside |= CoverageOutputGeometry.Gap([after[g], after[g], after[g]], points, axis) > 0;
            if (!outside) return false;
        }
        return true;
    }

    internal static CoverageOutputVerified? Certify(CoverageOutputCell c, string expectedBinding) =>
        Check(c, expectedBinding).Accepted ? new CoverageOutputVerified(c) : null;
}
