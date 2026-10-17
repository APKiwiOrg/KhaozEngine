using System;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Render3D;

/// <summary>Analytic triangle overlay vertices with exact membership and outward affine height differences.
/// Every overlap vertex is an included triangle corner or an intersection of two boundary edges.</summary>
internal static class CoverageFaceOverlay
{
    internal static bool TryMaximum(ReadOnlySpan<CoverageAffinePoint> host, ReadOnlySpan<CoverageAffinePoint> sheet,
        out double maximum)
    {
        int edgeEvaluations = 0;
        return TryMaximum(host, sheet, ref edgeEvaluations, out maximum);
    }

    /// <summary>Counts every executed exact Edge evaluation into edgeEvaluations.</summary>
    internal static bool TryMaximum(ReadOnlySpan<CoverageAffinePoint> host, ReadOnlySpan<CoverageAffinePoint> sheet,
        ref int edgeEvaluations, out double maximum)
    {
        CoverageScalar ha = CoverageGeometry.Edge(host[0], host[1], host[2]), sa = CoverageGeometry.Edge(sheet[0], sheet[1], sheet[2]);
        edgeEvaluations += 2;
        return TryMaximum(host, sheet, ha, sa, ref edgeEvaluations, out maximum);
    }

    /// <summary>The maximum host minus sheet height over every support inward triangle, in support order. Every pair
    /// runs. A refusal returns at once with the maximum of the pairs before it. The host area is the same Edge for every
    /// pair, and each sheet area is the support's own construction-time Edge of the same corners.</summary>
    internal static bool TrySupportMaximum(ReadOnlySpan<CoverageAffinePoint> host, CoverageSupportSurface support,
        Span<CoverageAffinePoint> sheet, ref int edgeEvaluations, out double maximum)
    {
        maximum = double.NegativeInfinity;
        CoverageScalar ha = CoverageGeometry.Edge(host[0], host[1], host[2]);
        edgeEvaluations++;
        for (int index = 0; index < support.InwardTriangles.Count; index++)
        {
            CoverageSourceTriangle s = support.InwardTriangles[index];
            sheet[0] = support.ProjectedPositions[s.A]; sheet[1] = support.ProjectedPositions[s.B]; sheet[2] = support.ProjectedPositions[s.C];
            if (!TryMaximum(host, sheet, ha, support.InwardArea(index), ref edgeEvaluations, out double upper)) return false;
            maximum = Math.Max(maximum, upper);
        }
        return true;
    }

    /// <summary>The pair maximum with both triangle areas supplied, each exactly the Edge of its own three corners.
    /// The 18 mixed Edge values are each evaluated once, by the inside tests, and reused by the 9 edge pairs.</summary>
    internal static bool TryMaximum(ReadOnlySpan<CoverageAffinePoint> host, ReadOnlySpan<CoverageAffinePoint> sheet,
        CoverageScalar ha, CoverageScalar sa, ref int edgeEvaluations, out double maximum)
    {
        maximum = double.NegativeInfinity;
        if (!Usable(ha) || !Usable(sa)) return false;
        // hostInSheet[i * 3 + j] = Edge(sheet[j], sheet[j + 1], host[i]) and sheetInHost[j * 3 + i] = Edge(host[i],
        // host[i + 1], sheet[j]). The inside tests fill them in their original order, so a refusal leaves the same
        // partial maximum. Reaching the pair stage means all 18 were written and are finite.
        Span<CoverageScalar> hostInSheet = stackalloc CoverageScalar[9], sheetInHost = stackalloc CoverageScalar[9];
        for (int i = 0; i < 3; i++)
        {
            if (!TryInside(sheet, host[i], sa.Sign, hostInSheet.Slice(i * 3, 3), ref edgeEvaluations, out bool inside)) return false;
            if (inside && !Accumulate(host, sheet, ha, sa, CoverageBoundedVector.From(host[i]), ref maximum)) return false;
            if (!TryInside(host, sheet[i], ha.Sign, sheetInHost.Slice(i * 3, 3), ref edgeEvaluations, out inside)) return false;
            if (inside && !Accumulate(host, sheet, ha, sa, CoverageBoundedVector.From(sheet[i]), ref maximum)) return false;
        }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                int nextI = (i + 1) % 3, nextJ = (j + 1) % 3;
                CoverageAffinePoint a = host[i], b = host[nextI];
                CoverageScalar ca = hostInSheet[i * 3 + j], cb = hostInSheet[nextI * 3 + j],
                    ac = sheetInHost[j * 3 + i], ad = sheetInHost[nextJ * 3 + i];
                if (ca.Sign * cb.Sign > 0 || ac.Sign * ad.Sign > 0) continue;
                // Collinear overlap extrema are endpoints, already included above. An endpoint touch needs no division.
                if (ca.Sign == 0 && cb.Sign == 0) continue;
                CoverageBoundedVector p;
                if (ca.Sign == 0) p = CoverageBoundedVector.From(a);
                else if (cb.Sign == 0) p = CoverageBoundedVector.From(b);
                else
                {
                    CoverageScalarBounds t = Divide(ca.Bounds, Subtract(ca.Bounds, cb.Bounds));
                    if (!t.IsFinite) return false;
                    t = ClampUnit(t);
                    var first = CoverageBoundedVector.From(a);
                    var delta = CoverageBoundedVector.From(b) - first;
                    p = first + new CoverageBoundedVector(Multiply(t, delta.X), Multiply(t, delta.Y), Multiply(t, delta.Z));
                }
                if (!Accumulate(host, sheet, ha, sa, p, ref maximum)) return false;
            }
        return true;
    }

    static bool Usable(CoverageScalar area) => area.IsFinite && area.Sign != 0 && (area.Bounds.Min > 0 || area.Bounds.Max < 0);

    /// <summary>sides[i] receives Edge(triangle[i], triangle[i + 1], p). A nonfinite side returns before the rest.</summary>
    static bool TryInside(ReadOnlySpan<CoverageAffinePoint> triangle, CoverageAffinePoint p, int sign, Span<CoverageScalar> sides,
        ref int edgeEvaluations, out bool inside)
    {
        inside = true;
        for (int i = 0; i < 3; i++)
        {
            CoverageScalar side = sides[i] = CoverageGeometry.Edge(triangle[i], triangle[(i + 1) % 3], p);
            edgeEvaluations++;
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
