using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>The scalar binary32 expression graph reproduced by reference coverage kernels.</summary>
internal static class CoverageReferenceMath
{
    internal static float Dot3(Vector3 a, Vector3 b) => ((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z);
    internal static float Dot2(Vector2 a, Vector2 b) => (a.X * b.X) + (a.Y * b.Y);
    internal static Vector3 Cross(Vector3 a, Vector3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));
    internal static Vector3 Point(Vector3 p, in Matrix4x4 matrix) => new(
        (((p.X * matrix.M11) + (p.Y * matrix.M21)) + (p.Z * matrix.M31)) + matrix.M41,
        (((p.X * matrix.M12) + (p.Y * matrix.M22)) + (p.Z * matrix.M32)) + matrix.M42,
        (((p.X * matrix.M13) + (p.Y * matrix.M23)) + (p.Z * matrix.M33)) + matrix.M43);
    internal static Vector3 Linear(Vector3 p, in Matrix4x4 matrix) => new(
        ((p.X * matrix.M11) + (p.Y * matrix.M21)) + (p.Z * matrix.M31),
        ((p.X * matrix.M12) + (p.Y * matrix.M22)) + (p.Z * matrix.M32),
        ((p.X * matrix.M13) + (p.Y * matrix.M23)) + (p.Z * matrix.M33));
    internal static float Edge(Vector3 a, Vector3 b, Vector3 p) =>
        ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

    internal static float Height(Vector3 a, Vector3 b, Vector3 c, Vector2 p)
    {
        if (p.X == a.X && p.Y == a.Y) return a.Z;
        if (p.X == b.X && p.Y == b.Y) return b.Z;
        if (p.X == c.X && p.Y == c.Y) return c.Z;
        Vector3 query = new(p, 0);
        float u = Edge(b, c, query), v = Edge(c, a, query), w = Edge(a, b, query);
        if (u == 0) return EdgeHeight(b, c, p);
        if (v == 0) return EdgeHeight(c, a, p);
        if (w == 0) return EdgeHeight(a, b, p);
        return (((u * a.Z) + (v * b.Z)) + (w * c.Z)) / Edge(a, b, c);
    }

    static float EdgeHeight(Vector3 a, Vector3 b, Vector2 p)
    {
        if (a.X > b.X || a.X == b.X && (a.Y > b.Y || a.Y == b.Y && a.Z > b.Z)) (a, b) = (b, a);
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float t = MathF.Abs(dx) >= MathF.Abs(dy) ? (p.X - a.X) / dx : (p.Y - a.Y) / dy;
        return ((1 - t) * a.Z) + (t * b.Z);
    }
}
