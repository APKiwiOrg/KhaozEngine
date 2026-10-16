using System;
using System.Numerics;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Render3D;

/// <summary>Outward affine arithmetic for the actual float matrix contract, including inversion and composition.</summary>
internal readonly record struct CoverageBoundedVector(CoverageScalarBounds X, CoverageScalarBounds Y, CoverageScalarBounds Z)
{
    internal static CoverageBoundedVector From(CoverageVector p) => new(new(p.X, p.X), new(p.Y, p.Y), new(p.Z, p.Z));
    internal static CoverageBoundedVector From(Vector3 p) => From(CoverageVector.From(p));
    internal static CoverageBoundedVector From(CoverageAffinePoint p) => new(p.X.Bounds, p.Y.Bounds, p.Z.Bounds);
    internal bool IsFinite => X.IsFinite && Y.IsFinite && Z.IsFinite;
    internal CoverageScalarBounds Length => Sqrt(Add(Add(Square(X), Square(Y)), Square(Z)));
    internal static CoverageScalarBounds Dot(CoverageBoundedVector a, CoverageBoundedVector b) =>
        Add(Add(Multiply(a.X, b.X), Multiply(a.Y, b.Y)), Multiply(a.Z, b.Z));
    internal static CoverageBoundedVector Cross(CoverageBoundedVector a, CoverageBoundedVector b) =>
        new(Subtract(Multiply(a.Y, b.Z), Multiply(a.Z, b.Y)), Subtract(Multiply(a.Z, b.X), Multiply(a.X, b.Z)),
            Subtract(Multiply(a.X, b.Y), Multiply(a.Y, b.X)));
    internal CoverageBoundedVector DivideBy(CoverageScalarBounds d) => new(Divide(X, d), Divide(Y, d), Divide(Z, d));
    public static CoverageBoundedVector operator +(CoverageBoundedVector a, CoverageBoundedVector b) => new(Add(a.X, b.X), Add(a.Y, b.Y), Add(a.Z, b.Z));
    public static CoverageBoundedVector operator -(CoverageBoundedVector a, CoverageBoundedVector b) => new(Subtract(a.X, b.X), Subtract(a.Y, b.Y), Subtract(a.Z, b.Z));
    internal CoverageBoundedVector Negated => new(Negate(X), Negate(Y), Negate(Z));
}

internal readonly record struct CoverageBoundedAffine(CoverageBoundedVector R1, CoverageBoundedVector R2,
    CoverageBoundedVector R3, CoverageBoundedVector Translation)
{
    internal static CoverageBoundedAffine From(in CoverageAffine a) => new(CoverageBoundedVector.From(a.Row(0)),
        CoverageBoundedVector.From(a.Row(1)), CoverageBoundedVector.From(a.Row(2)), CoverageBoundedVector.From(a.Translation));
    internal bool IsFinite => R1.IsFinite && R2.IsFinite && R3.IsFinite && Translation.IsFinite;
    internal CoverageBoundedVector Apply(Vector3 p) => Apply(CoverageBoundedVector.From(p));
    internal CoverageBoundedVector Apply(CoverageBoundedVector p) => ApplyLinear(p) + Translation;
    internal CoverageBoundedVector ApplyLinear(CoverageBoundedVector p) => new(
        Add(Add(Multiply(p.X, R1.X), Multiply(p.Y, R2.X)), Multiply(p.Z, R3.X)),
        Add(Add(Multiply(p.X, R1.Y), Multiply(p.Y, R2.Y)), Multiply(p.Z, R3.Y)),
        Add(Add(Multiply(p.X, R1.Z), Multiply(p.Y, R2.Z)), Multiply(p.Z, R3.Z)));
    internal static CoverageBoundedAffine Compose(CoverageBoundedAffine a, CoverageBoundedAffine b) =>
        new(b.ApplyLinear(a.R1), b.ApplyLinear(a.R2), b.ApplyLinear(a.R3), b.Apply(a.Translation));

    internal bool TryInverse(out CoverageBoundedAffine inverse)
    {
        CoverageBoundedVector c1 = CoverageBoundedVector.Cross(R2, R3), c2 = CoverageBoundedVector.Cross(R3, R1),
            c3 = CoverageBoundedVector.Cross(R1, R2);
        CoverageScalarBounds det = CoverageBoundedVector.Dot(R1, c1);
        inverse = default;
        if (!det.IsFinite || det.Min <= 0) return false;
        c1 = c1.DivideBy(det); c2 = c2.DivideBy(det); c3 = c3.DivideBy(det);
        inverse = new(new(c1.X, c2.X, c3.X), new(c1.Y, c2.Y, c3.Y), new(c1.Z, c2.Z, c3.Z), default);
        inverse = inverse with { Translation = inverse.ApplyLinear(Translation).Negated };
        return inverse.IsFinite;
    }
}

/// <summary>Actual-sheet domain and side certificates. These use the actual equipment inverse, with outward bounds,
/// followed by the authoritative compiled projection. They do not consume construction slack.</summary>
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
