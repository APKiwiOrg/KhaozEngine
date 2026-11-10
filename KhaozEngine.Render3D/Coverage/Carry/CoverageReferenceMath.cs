using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>The scalar binary32 expression graph reproduced by reference coverage kernels.</summary>
internal static class CoverageReferenceMath
{
    internal static float Dot3(CoverageReferenceArithmetic f, Vector3 a, Vector3 b) => f.Add(f.Add(f.Mul(a.X, b.X), f.Mul(a.Y, b.Y)), f.Mul(a.Z, b.Z));
    internal static float Dot2(CoverageReferenceArithmetic f, Vector2 a, Vector2 b) => f.Add(f.Mul(a.X, b.X), f.Mul(a.Y, b.Y));
    internal static Vector3 Cross(CoverageReferenceArithmetic f, Vector3 a, Vector3 b) => new Vector3(
        f.Sub(f.Mul(a.Y, b.Z), f.Mul(a.Z, b.Y)), f.Sub(f.Mul(a.Z, b.X), f.Mul(a.X, b.Z)), f.Sub(f.Mul(a.X, b.Y), f.Mul(a.Y, b.X)));
    internal static Vector3 Point(CoverageReferenceArithmetic f, Vector3 p, in Matrix4x4 matrix) => new Vector3(
        f.Add(f.Add(f.Add(f.Mul(p.X, matrix.M11), f.Mul(p.Y, matrix.M21)), f.Mul(p.Z, matrix.M31)), matrix.M41),
        f.Add(f.Add(f.Add(f.Mul(p.X, matrix.M12), f.Mul(p.Y, matrix.M22)), f.Mul(p.Z, matrix.M32)), matrix.M42),
        f.Add(f.Add(f.Add(f.Mul(p.X, matrix.M13), f.Mul(p.Y, matrix.M23)), f.Mul(p.Z, matrix.M33)), matrix.M43));
    internal static Vector3 Linear(CoverageReferenceArithmetic f, Vector3 p, in Matrix4x4 matrix) => new Vector3(
        f.Add(f.Add(f.Mul(p.X, matrix.M11), f.Mul(p.Y, matrix.M21)), f.Mul(p.Z, matrix.M31)),
        f.Add(f.Add(f.Mul(p.X, matrix.M12), f.Mul(p.Y, matrix.M22)), f.Mul(p.Z, matrix.M32)),
        f.Add(f.Add(f.Mul(p.X, matrix.M13), f.Mul(p.Y, matrix.M23)), f.Mul(p.Z, matrix.M33)));
    internal static Vector3 Sub3(CoverageReferenceArithmetic f, Vector3 a, Vector3 b) => new Vector3(f.Sub(a.X, b.X), f.Sub(a.Y, b.Y), f.Sub(a.Z, b.Z));
    internal static bool Equal3(CoverageReferenceArithmetic f, Vector3 a, Vector3 b) => f.Equal(a.X, b.X) && f.Equal(a.Y, b.Y) && f.Equal(a.Z, b.Z);
    internal static bool IsFinite3(CoverageReferenceArithmetic f, Vector3 v) => f.IsFinite(v.X) && f.IsFinite(v.Y) && f.IsFinite(v.Z);
    internal static bool IsFiniteVertex(CoverageReferenceArithmetic f, ModelVertex v) => IsFinite3(f, v.Position) && IsFinite3(f, v.Normal)
        && f.IsFinite(v.Color.X) && f.IsFinite(v.Color.Y) && f.IsFinite(v.Color.Z) && f.IsFinite(v.Color.W)
        && f.IsFinite(v.Uv.X) && f.IsFinite(v.Uv.Y) && f.IsFinite(v.Tangent.X) && f.IsFinite(v.Tangent.Y)
        && f.IsFinite(v.Tangent.Z) && f.IsFinite(v.Tangent.W);

    internal static bool ReadVertex(CoverageReferenceArithmetic f, ref ModelVertex v) =>
        f.IsFinite(ref v.Position.X) && f.IsFinite(ref v.Position.Y) && f.IsFinite(ref v.Position.Z)
        && f.IsFinite(ref v.Normal.X) && f.IsFinite(ref v.Normal.Y) && f.IsFinite(ref v.Normal.Z)
        && f.IsFinite(ref v.Color.X) && f.IsFinite(ref v.Color.Y) && f.IsFinite(ref v.Color.Z) && f.IsFinite(ref v.Color.W)
        && f.IsFinite(ref v.Uv.X) && f.IsFinite(ref v.Uv.Y) && f.IsFinite(ref v.Tangent.X) && f.IsFinite(ref v.Tangent.Y)
        && f.IsFinite(ref v.Tangent.Z) && f.IsFinite(ref v.Tangent.W);
}
