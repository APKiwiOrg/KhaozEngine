using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

internal static class CoverageExactHeight
{
    internal static float Height(CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector3 c, Vector2 p)
    {
        if (f.Equal(p.X, a.X) && f.Equal(p.Y, a.Y)) return f.Read(a.Z);
        if (f.Equal(p.X, b.X) && f.Equal(p.Y, b.Y)) return f.Read(b.Z);
        if (f.Equal(p.X, c.X) && f.Equal(p.Y, c.Y)) return f.Read(c.Z);
        Vector3 query = new Vector3(p, 0);
        CoverageReferenceScalar u = f.Edge(b, c, query), v = f.Edge(c, a, query), w = f.Edge(a, b, query);
        if (u.Sign == 0) return EdgeHeight(f, b, c, p);
        if (v.Sign == 0) return EdgeHeight(f, c, a, p);
        if (w.Sign == 0) return EdgeHeight(f, a, b, p);
        return f.Div(f.Add(f.Add(f.Mul(u.Value, a.Z), f.Mul(v.Value, b.Z)), f.Mul(w.Value, c.Z)), f.Edge(a, b, c).Value);
    }

    static float EdgeHeight(CoverageReferenceArithmetic f, Vector3 a, Vector3 b, Vector2 p)
    {
        if (f.Greater(a.X, b.X) || f.Equal(a.X, b.X) && (f.Greater(a.Y, b.Y) || f.Equal(a.Y, b.Y) && f.Greater(a.Z, b.Z)))
        { Vector3 temporary = a; a = b; b = temporary; }
        float dx = f.Sub(b.X, a.X), dy = f.Sub(b.Y, a.Y);
        float t = f.GreaterOrEqual(f.Abs(dx), f.Abs(dy)) ? f.Div(f.Sub(p.X, a.X), dx) : f.Div(f.Sub(p.Y, a.Y), dy);
        return f.Add(f.Mul(f.Sub(1, t), a.Z), f.Mul(t, b.Z));
    }
}
