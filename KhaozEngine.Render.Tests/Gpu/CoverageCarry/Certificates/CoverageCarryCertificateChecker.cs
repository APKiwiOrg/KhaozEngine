using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Tests.Gpu;

// This verifier does not invoke the builder, its axis search, the constructor or the sampled geometry oracle.
// It derives the enclosures from the claimed domain again and checks every triangle and every pair itself.
internal static class CoverageCarryCertificateChecker
{
    internal static CoverageCarryCheck Check(CoverageCarryCell cell)
    {
        long operations = 0; int currentCommand = -1;
        var best = new CoverageCarryCheck(true, "none", -1, -1, double.PositiveInfinity, 0, 0);
        bool Note(string cause, int triangle, int other, double margin)
        {
            operations++;
            if (!double.IsFinite(margin) || !(margin > 0))
            { best = new(false, cause, triangle, other, margin, operations, best.SlopeUpper, currentCommand); return false; }
            if (margin < best.Margin) best = best with { Cause = cause, Triangle = triangle, Other = other, Margin = margin, Command = currentCommand };
            return true;
        }
        if (!CoverageCarryCellBinding.Admits(cell, cell.Center, cell.Commands)
            || CoverageCarryCellBinding.Domain(cell) != cell.DomainDigest || cell.Center.Length != 2
            || cell.Commands.Length != 2 || cell.Commands.Any(c => c.TransitionWeight != 1))
            return new(false, "input/domain binding", -1, -1, double.NaN, operations, 0);
        var t = cell.Anchor.Topology;
        var admitted = new CoverageReferenceScratch(t);
        if (!CoverageReferenceFrames.TryPrepare(t, Matrix4x4.Identity, cell.Commands, admitted, out var refusal)
            || !Connected(t)) return new(false, $"command/topology admission {refusal.Code}", -1, -1, 0, operations, 0);
        foreach (var alias in t.SourceAliases)
        {
            var a = t.Source.SkinVertices[alias.SourceVertex]; var b = t.Source.SkinVertices[alias.Representative];
            if (a.Position != b.Position || a.BoneWeights != b.BoneWeights || a.BoneIndices != b.BoneIndices)
                return new(false, "alias provenance", alias.SourceVertex, alias.Representative, 0, operations, 0);
        }
        var enclosure = new CoverageCarryEnclosure(cell);
        var anchored = new CoverageCarryEnclosure(cell with { Radius = 0 });
        operations += enclosure.Operations + anchored.Operations;
        var after = Enumerable.Range(0, t.TriangleCount).Select(i => CoverageCarryProofGeometry.Triangle(t, enclosure.After, i)).ToArray();
        var before = Enumerable.Range(0, t.TriangleCount).Select(i => CoverageCarryProofGeometry.Triangle(t, enclosure.Before, i)).ToArray();
        var groups = Enumerable.Range(0, t.TriangleCount).Select(i => CoverageCarryProofGeometry.Groups(t, i)).ToArray();
        var planes = new Dictionary<(int, int, int), Vector3>();
        foreach (var p in cell.Planes)
            if (!planes.TryAdd((p.Triangle, p.Other, p.Command), p.Axis))
                return new(false, "duplicate witness", p.Triangle, p.Other, 0, operations, 0);
        for (int f = 0; f < t.TriangleCount; f++)
        {
            if (!Note("projected fold", f, -1, CoverageCarryProofGeometry.Orient(after[f][0], after[f][1], after[f][2]))) return best;
            var normal = CoverageBoundedVector.Cross(after[f][1] - after[f][0], after[f][2] - after[f][0]);
            var original = CoverageBoundedVector.Cross(before[f][1] - before[f][0], before[f][2] - before[f][0]);
            double fold = Divide(CoverageBoundedVector.Dot(normal, original), Multiply(normal.Length, original.Length)).Min;
            if (!Note("normal fold cosine", f, -1, fold)) return best;
        }
        // Every pair is enumerated, including protected and distant host triangles. Strict projected separation
        // implies 3D separation. Shared edges/vertices use the same correlated coordinates, not independent boxes.
        for (int f = 0; f < after.Length; f++)
            for (int g = f + 1; g < after.Length; g++)
            {
                double margin = CoverageCarryProofGeometry.CoordinateGap(after[f], after[g], true);
                if (margin > 0) { Note("host/injectivity coordinate", f, g, margin); continue; }
                planes.TryGetValue((f, g, -1), out var axis);
                if (!Note("host/injectivity", f, g, CoverageCarryProofGeometry.HostMargin(after[f], after[g], groups[f], groups[g], axis))) return best;
            }
        for (int k = 0; k < cell.Commands.Length; k++)
        {
            currentCommand = k;
            var c = cell.Commands[k];
            if (!CoverageAffine.TryFrom(c.CoverageWorld, out var coverage)
                || !CoverageBoundedAffine.From(coverage).TryInverse(out var inverse)
                || !CoverageAffine.TryFrom(c.EquipmentWorld, out var equipment)
                || !CoverageBoundedAffine.From(equipment).TryInverse(out var equipmentInverse)
                || !CoverageAffine.TryFrom(c.Support.SourceToCoverage, out var projection))
                return new(false, "transform enclosure", -1, k, 0, operations, 0);
            var actual = CoverageBoundedAffine.Compose(equipmentInverse, CoverageBoundedAffine.From(projection));
            // The declared F0 frames have a vertical coverage Z axis. Their inverse XY is an invertible affine
            // map of world XY, independent of host Z. Thus the global injectivity proof transfers to command XY.
            // Tilted frames need a separate projected proof and are rejected by current-input admission.
            var determinant = Subtract(Multiply(inverse.R1.X, inverse.R2.Y), Multiply(inverse.R1.Y, inverse.R2.X));
            if (!Note("command XY determinant", -1, k, determinant.Min)) return best;
            for (int f = 0; f < t.TriangleCount; f++)
            {
                if (t.Triangles[f].RegionIndex != c.RegionIndex) continue;
                var e1 = inverse.ApplyLinear(after[f][1] - after[f][0]);
                var e2 = inverse.ApplyLinear(after[f][2] - after[f][0]);
                var area = Multiply(determinant, CoverageBoundedVector.Cross(after[f][1] - after[f][0], after[f][2] - after[f][0]).Z);
                double ratio = Subtract(Divide(area, Multiply(e1.Length, e2.Length)), new(1e-9, 1e-9)).Min;
                if (!Note("command projected fold ratio", f, k, ratio)) return best;
            }
            double slope = CoverageCarryProofGeometry.SlopeUpper(c.Support);
            if (!Note("support slope", -1, k, double.IsFinite(slope) ? 1 : double.NaN)) return best;
            best = best with { SlopeUpper = Math.Max(best.SlopeUpper, slope) };
            var column = new CoverageBoundedVector(inverse.R1.Z, inverse.R2.Z, inverse.R3.Z);
            double cap = Multiply(new(c.Clearance, c.Clearance), column.Length).Max;
            for (int v = 0; v < t.VertexCount; v++)
            {
                var r = t.Vertices[v]; int rep = cell.Anchor.Representatives[r.PositionGroup];
                if (r.Strength == 0 && rep >= 0 && cell.Anchor.Recession[r.PositionGroup] != 0)
                    return new(false, "zero boundary", v, k, 0, operations, slope);
                if (r.RegionIndex != c.RegionIndex || r.Strength == 0) continue;
                var p = inverse.Apply(enclosure.After[v].Real); var q = inverse.Apply(enclosure.Before[v].Real);
                double low = Add(new(c.MinZ, c.MinZ), new(cap, cap)).Max;
                double high = Subtract(new(c.MaxZ, c.MaxZ), new(cap, cap)).Min;
                if (!Note("caps", v, k, Math.Min(Math.Min(p.Z.Min, q.Z.Min) - low, high - Math.Max(p.Z.Max, q.Z.Max)))
                    || !Note("world recession", v, k, Subtract(new(c.MaxWorldRecession, c.MaxWorldRecession), enclosure.Displacement[v].Real.Length).Min)) return best;
                if (!t.IsCoreVertex(v)) continue;
                var a = actual.Apply(enclosure.After[v].Real);
                if (!Containment(c.Support, a, out double contained) || !Note("sheet containment", v, k, contained)) return best with { Accepted = false, Cause = "sheet containment", Triangle = v, Other = k, Margin = contained };
            }
            var solid = CoverageCarryProofGeometry.Equipment(c);
            // All F0 faces are connected through their welded groups. A far protected corner is outside the
            // entire displayed solid's box. Separation of every face along the convex enclosure homotopy then
            // preserves that exterior component for cores, collars and protected faces alike.
            var far = anchored.After[0].Real;
            double outside = double.NegativeInfinity;
            foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                outside = Math.Max(outside, CoverageCarryProofGeometry.Gap([far, far, far], solid.SelectMany(x => x).ToArray(), axis));
            if (!Note("outside solid anchor", 0, k, outside)) return best;
            for (int f = 0; f < after.Length; f++)
            {
                bool own = t.Triangles[f].RegionIndex == c.RegionIndex;
                // Preserve NC2 as a whole world-distance reserve, including collar geometry.
                double required = own ? Multiply(new(c.Clearance, c.Clearance), new(1.25, 1.25)).Max : 0;
                for (int g = 0; g < solid.Length; g++)
                {
                    double gap = CoverageCarryProofGeometry.CoordinateGap(after[f], solid[g]);
                    if (gap > required)
                    {
                        Note("equipment coordinate clearance + NC2", f, g,
                        Subtract(new(gap, gap), new(required, required)).Min); continue;
                    }
                    planes.TryGetValue((f, g, k), out var axis);
                    double margin = Subtract(new(CoverageCarryProofGeometry.Gap(after[f], solid[g], axis),
                        CoverageCarryProofGeometry.Gap(after[f], solid[g], axis)), new(required, required)).Min;
                    if (!Note(own ? "whole-triangle clearance + NC2" : "equipment separation", f, g, margin)) return best;
                }
                if (!own || t.Triangles[f].Role != CoverageTriangleRole.Core) continue;
                // Connected, contained triangles cannot cross a sheet from their anchored inward component.
                // The separating inequalities above hold on the entire convex vertex enclosure, hence also on
                // the straight homotopy to every admitted current triangle. One anchored corner fixes its side.
                var corner = anchored.After[(int)t.Indices[f * 3]];
                var point = actual.Apply(new Vector3(corner.X.Min, corner.Y.Min, corner.Z.Min));
                double x = point.X.Midpoint, y = point.Y.Midpoint;
                if (!c.Support.TryGetHeightBounds(CoverageAffinePoint.FromDouble(x, y), out var height))
                    return best with { Accepted = false, Cause = "anchor sheet side", Triangle = f, Other = k, Margin = 0 };
                var rho = Sqrt(Add(Square(Subtract(point.X, new(x, x))), Square(Subtract(point.Y, new(y, y)))));
                double side = Subtract(Subtract(height, Multiply(new(slope, slope), rho)), point.Z).Min;
                if (!Note("anchor sheet side", f, k, side)) return best;
            }
        }
        return best with { Operations = operations };
    }

    static bool Connected(CoverageRefinement t)
    {
        var parents = Enumerable.Range(0, t.PositionGroupCount).ToArray();
        int Root(int i) { while (parents[i] != i) i = parents[i]; return i; }
        foreach (int f in Enumerable.Range(0, t.TriangleCount))
        {
            var groups = CoverageCarryProofGeometry.Groups(t, f);
            parents[Root(groups[1])] = Root(groups[0]); parents[Root(groups[2])] = Root(groups[0]);
        }
        return parents.All(i => Root(i) == Root(0));
    }

    static bool Containment(CoverageSupportSurface support, CoverageBoundedVector p, out double margin)
    {
        margin = double.PositiveInfinity;
        var loop = support.BoundarySourceVertices;
        var first = support.InwardTriangles[0];
        int sign = CoverageGeometry.Edge(support.ProjectedPositions[first.A], support.ProjectedPositions[first.B],
            support.ProjectedPositions[first.C]).Sign;
        for (int e = 0; e < loop.Length; e++)
        {
            var a = CoverageBoundedVector.From(support.ProjectedPositions[loop[e]]);
            var b = CoverageBoundedVector.From(support.ProjectedPositions[loop[(e + 1) % loop.Length]]);
            var edge = b - a;
            var side = CoverageBoundedVector.Cross(edge, p - a).Z;
            double signed = sign > 0 ? side.Min : -side.Max;
            margin = Math.Min(margin, Divide(new(signed, signed), Sqrt(Add(Square(edge.X), Square(edge.Y)))).Min);
        }
        return double.IsFinite(margin) && margin > 0;
    }
}
