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
        maximum = double.NegativeInfinity;
        CoverageScalar ha = CoverageGeometry.Edge(host[0], host[1], host[2]), sa = CoverageGeometry.Edge(sheet[0], sheet[1], sheet[2]);
        if (!Usable(ha) || !Usable(sa)) return false;
        for (int i = 0; i < 3; i++)
        {
            if (!TryInside(sheet, host[i], sa.Sign, out bool inside)) return false;
            if (inside && !Accumulate(host, sheet, ha, sa, CoverageBoundedVector.From(host[i]), ref maximum)) return false;
            if (!TryInside(host, sheet[i], ha.Sign, out inside)) return false;
            if (inside && !Accumulate(host, sheet, ha, sa, CoverageBoundedVector.From(sheet[i]), ref maximum)) return false;
        }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                CoverageAffinePoint a = host[i], b = host[(i + 1) % 3], c = sheet[j], d = sheet[(j + 1) % 3];
                CoverageScalar ca = CoverageGeometry.Edge(c, d, a), cb = CoverageGeometry.Edge(c, d, b),
                    ac = CoverageGeometry.Edge(a, b, c), ad = CoverageGeometry.Edge(a, b, d);
                if (!ca.IsFinite || !cb.IsFinite || !ac.IsFinite || !ad.IsFinite) return false;
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

    static bool TryInside(ReadOnlySpan<CoverageAffinePoint> triangle, CoverageAffinePoint p, int sign, out bool inside)
    {
        inside = true;
        for (int i = 0; i < 3; i++)
        {
            CoverageScalar side = CoverageGeometry.Edge(triangle[i], triangle[(i + 1) % 3], p);
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
