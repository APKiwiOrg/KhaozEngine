using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Exact predicate operations for offline geometry, using the retained scalar rounding policy.</summary>
internal static class CoveragePredicateArithmetic
{
    internal static CoverageReferenceScalar Edge(this CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector3 p)
    {
        var result = CoverageReferencePredicates.Edge(Point(f, a), Point(f, b), Point(f, p));
        return new(result.Sign, f.Read(result.Value));
    }

    internal static CoverageReferenceScalar Dot(this CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var result = CoverageReferencePredicates.Dot(Point(f, a), Point(f, b), Point(f, c), Point(f, d));
        return new(result.Sign, f.Read(result.Value));
    }

    internal static float CrossingParameter(this CoverageReferenceArithmetic f, Vector3 s, Vector3 t, Vector3 a, Vector3 b)
    {
        if (!CoverageReferencePredicates.TryScaledSides(Point(f, s), Point(f, t), Point(f, a), Point(f, b), out float x, out float y))
            return float.NaN;
        x = f.Read(x); y = f.Read(y);
        return f.Div(x, f.Sub(x, y));
    }

    static Vector3 Point(CoverageReferenceArithmetic f, Vector3 value) => new(f.Read(value.X), f.Read(value.Y), value.Z);
}
