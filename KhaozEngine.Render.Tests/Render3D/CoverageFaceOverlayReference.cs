using System;
using KhaozEngine.Render3D;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Frozen copy of the overlay before Edge deduplication (3912d4f80): 2 area, 18 inside and 36 repeated pair Edge
/// evaluations per pair, and the support loop that recomputed both areas per pair. It shares only the exact kernels and
/// never calls the production overlay, so it is the equivalence reference for every bool, maximum and refusal.</summary>
internal static class CoverageFaceOverlayReference
{
    internal enum Stage { None, Area, Inside, CornerAccumulate, PairEdge, Division, PairAccumulate }

    internal readonly record struct Result(bool Succeeded, double Maximum, int EdgeEvaluations, Stage Failure, int Crossings);

    internal readonly record struct SupportResult(bool Succeeded, double Maximum, int EdgeEvaluations, int FailedIndex, Stage Failure);

    internal static SupportResult TrySupportMaximum(ReadOnlySpan<CoverageAffinePoint> host, CoverageSupportSurface support)
    {
        double deficit = double.NegativeInfinity;
        int edges = 0;
        Span<CoverageAffinePoint> sheet = stackalloc CoverageAffinePoint[3];
        for (int index = 0; index < support.InwardTriangles.Count; index++)
        {
            CoverageSourceTriangle s = support.InwardTriangles[index];
            sheet[0] = support.ProjectedPositions[s.A]; sheet[1] = support.ProjectedPositions[s.B]; sheet[2] = support.ProjectedPositions[s.C];
            Result pair = TryMaximum(host, sheet);
            edges += pair.EdgeEvaluations;
            if (!pair.Succeeded) return new(false, deficit, edges, index, pair.Failure);
            deficit = Math.Max(deficit, pair.Maximum);
        }
        return new(true, deficit, edges, -1, Stage.None);
    }

    internal static Result TryMaximum(ReadOnlySpan<CoverageAffinePoint> host, ReadOnlySpan<CoverageAffinePoint> sheet)
    {
        int edges = 0, crossings = 0;
        double maximum = double.NegativeInfinity;
        CoverageScalar ha = Edge(host[0], host[1], host[2], ref edges), sa = Edge(sheet[0], sheet[1], sheet[2], ref edges);
        if (!Usable(ha) || !Usable(sa)) return new(false, maximum, edges, Stage.Area, crossings);
        for (int i = 0; i < 3; i++)
        {
            if (!TryInside(sheet, host[i], sa.Sign, ref edges, out bool inside)) return new(false, maximum, edges, Stage.Inside, crossings);
            if (inside && !Accumulate(host, sheet, ha, sa, CoverageBoundedVector.From(host[i]), ref maximum))
                return new(false, maximum, edges, Stage.CornerAccumulate, crossings);
            if (!TryInside(host, sheet[i], ha.Sign, ref edges, out inside)) return new(false, maximum, edges, Stage.Inside, crossings);
            if (inside && !Accumulate(host, sheet, ha, sa, CoverageBoundedVector.From(sheet[i]), ref maximum))
                return new(false, maximum, edges, Stage.CornerAccumulate, crossings);
        }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                CoverageAffinePoint a = host[i], b = host[(i + 1) % 3], c = sheet[j], d = sheet[(j + 1) % 3];
                CoverageScalar ca = Edge(c, d, a, ref edges), cb = Edge(c, d, b, ref edges),
                    ac = Edge(a, b, c, ref edges), ad = Edge(a, b, d, ref edges);
                if (!ca.IsFinite || !cb.IsFinite || !ac.IsFinite || !ad.IsFinite) return new(false, maximum, edges, Stage.PairEdge, crossings);
                if (ca.Sign * cb.Sign > 0 || ac.Sign * ad.Sign > 0) continue;
                if (ca.Sign == 0 && cb.Sign == 0) continue;
                CoverageBoundedVector p;
                if (ca.Sign == 0) p = CoverageBoundedVector.From(a);
                else if (cb.Sign == 0) p = CoverageBoundedVector.From(b);
                else
                {
                    CoverageScalarBounds t = Divide(ca.Bounds, Subtract(ca.Bounds, cb.Bounds));
                    if (!t.IsFinite) return new(false, maximum, edges, Stage.Division, crossings);
                    crossings++;
                    t = ClampUnit(t);
                    var first = CoverageBoundedVector.From(a);
                    var delta = CoverageBoundedVector.From(b) - first;
                    p = first + new CoverageBoundedVector(Multiply(t, delta.X), Multiply(t, delta.Y), Multiply(t, delta.Z));
                }
                if (!Accumulate(host, sheet, ha, sa, p, ref maximum)) return new(false, maximum, edges, Stage.PairAccumulate, crossings);
            }
        return new(true, maximum, edges, Stage.None, crossings);
    }

    static CoverageScalar Edge(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint point, ref int edges)
    {
        edges++;
        return CoverageGeometry.Edge(a, b, point);
    }

    static bool Usable(CoverageScalar area) => area.IsFinite && area.Sign != 0 && (area.Bounds.Min > 0 || area.Bounds.Max < 0);

    static bool TryInside(ReadOnlySpan<CoverageAffinePoint> triangle, CoverageAffinePoint p, int sign, ref int edges, out bool inside)
    {
        inside = true;
        for (int i = 0; i < 3; i++)
        {
            CoverageScalar side = Edge(triangle[i], triangle[(i + 1) % 3], p, ref edges);
            if (!side.IsFinite) return false;
            if (side.Sign * sign < 0) inside = false;
        }
        return true;
    }

    static bool Accumulate(ReadOnlySpan<CoverageAffinePoint> host, ReadOnlySpan<CoverageAffinePoint> sheet,
        CoverageScalar hostArea, CoverageScalar sheetArea, CoverageBoundedVector p, ref double maximum)
    {
        CoverageScalarBounds difference = Subtract(Height(host, hostArea, p), Height(sheet, sheetArea, p));
        if (!difference.IsFinite) return false;
        maximum = Math.Max(maximum, difference.Max);
        return true;
    }

    static CoverageScalarBounds Height(ReadOnlySpan<CoverageAffinePoint> triangle, CoverageScalar area, CoverageBoundedVector p)
    {
        CoverageScalarBounds sum = Zero;
        for (int i = 0; i < 3; i++)
        {
            var a = CoverageBoundedVector.From(triangle[(i + 1) % 3]);
            var b = CoverageBoundedVector.From(triangle[(i + 2) % 3]);
            var barycentricNumerator = CoverageBoundedVector.Cross(b - a, p - a).Z;
            sum = Add(sum, Multiply(barycentricNumerator, triangle[i].Z.Bounds));
        }
        return Divide(sum, area.Bounds);
    }
}
