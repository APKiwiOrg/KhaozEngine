using System;
using static KhaozEngine.Render3D.CoverageIntervals;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

internal static class CoverageSheetSide
{
    internal static bool Contains(CoverageSupportSurface support, CoverageBoundedVector p)
    {
        if (!p.IsFinite) return false;
        var positions = support.ProjectedPositions;
        var boundary = support.BoundarySourceVertices;
        CoverageSourceTriangle first = support.InwardTriangles[0];
        int sign = CoverageGeometry.Edge(positions[first.A], positions[first.B], positions[first.C]).Sign;
        for (int i = 0; i < boundary.Length; i++)
        {
            var a = CoverageBoundedVector.From(positions[boundary[i]]);
            var b = CoverageBoundedVector.From(positions[boundary[(i + 1) % boundary.Length]]);
            CoverageScalarBounds side = CoverageBoundedVector.Cross(b - a, p - a).Z;
            if (sign < 0) side = Negate(side);
            if (!side.IsFinite || side.Min < 0) return false;
        }
        return true;
    }

    internal static bool Behind(CoverageSupportSurface support, CoverageBoundedVector p)
    {
        if (!p.IsFinite) return false;
        double x = p.X.Midpoint, y = p.Y.Midpoint;
        if (!support.TryGetHeightBounds(CoverageAffinePoint.FromDouble(x, y), out var height)) return false;
        CoverageScalarBounds dx = Subtract(p.X, new(x, x)), dy = Subtract(p.Y, new(y, y));
        var radius = Sqrt(Add(Square(dx), Square(dy)));
        var loss = Multiply(radius, new(support.MaxSlope, support.MaxSlope));
        var gap = Subtract(Subtract(height, loss), p.Z);
        return gap.IsFinite && gap.Min > 0;
    }
}
