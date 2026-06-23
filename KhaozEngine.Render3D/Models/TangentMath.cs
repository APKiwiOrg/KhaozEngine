using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Pure per-vertex tangent math (Lengyel UV+position method), shared by the rigid welding path
    /// (<see cref="MeshAssembler"/>) and the directly-indexed skinned loader (<c>GltfLoader.LoadSkinned</c>), so
    /// both produce the same tangent basis the model fragment shader's TBN expects. A computed tangent is the
    /// accumulated UV-space s-direction Gram-Schmidt orthogonalized against the finalized normal, with the
    /// handedness <c>w</c> taken from the bitangent sign; a supplied source tangent (e.g. the glTF
    /// <c>TANGENT</c> accessor) wins and is normalized with its handedness preserved. Degenerate input (no UV
    /// gradient) yields a zero tangent, which the shader reads as "no TBN" (geometric normal). Presentation only.</summary>
    internal static class TangentMath
    {
        /// <summary>Per-face tangent (<paramref name="sdir"/>) and bitangent (<paramref name="tdir"/>) directions
        /// from one triangle's edge + UV gradient. A degenerate UV (no gradient) yields zero directions.</summary>
        public static void FaceDirections(
            Vector3 p0, Vector3 p1, Vector3 p2, Vector2 uv0, Vector2 uv1, Vector2 uv2,
            out Vector3 sdir, out Vector3 tdir)
        {
            Vector3 e1 = p1 - p0, e2 = p2 - p0;
            float du1 = uv1.X - uv0.X, dv1 = uv1.Y - uv0.Y;
            float du2 = uv2.X - uv0.X, dv2 = uv2.Y - uv0.Y;
            float r = du1 * dv2 - du2 * dv1;
            sdir = Vector3.Zero; tdir = Vector3.Zero;
            if (MathF.Abs(r) > 1e-12f)
            {
                float f = 1f / r;
                sdir = (e1 * dv2 - e2 * dv1) * f;
                tdir = (e2 * du1 - e1 * du2) * f;
            }
        }

        /// <summary>Finalize one vertex's tangent. A supplied <paramref name="source"/> tangent (normalized,
        /// handedness preserved; a zero <c>w</c> defaults to +1) wins; otherwise Gram-Schmidt orthogonalize the
        /// accumulated s-direction <paramref name="sdir"/> against <paramref name="n"/> and take the handedness
        /// sign from the bitangent. Degenerate input (no UV gradient) returns <see cref="Vector4.Zero"/>.</summary>
        public static Vector4 Resolve(Vector3 n, Vector3 sdir, Vector3 tdir, Vector4? source)
        {
            if (source.HasValue)
            {
                var s = source.Value;
                var t = new Vector3(s.X, s.Y, s.Z);
                if (t.LengthSquared() <= 1e-12f) return Vector4.Zero;
                return new Vector4(Vector3.Normalize(t), s.W == 0f ? 1f : s.W);
            }
            Vector3 ortho = sdir - n * Vector3.Dot(n, sdir);
            if (ortho.LengthSquared() <= 1e-12f) return Vector4.Zero;
            ortho = Vector3.Normalize(ortho);
            float w = Vector3.Dot(Vector3.Cross(n, sdir), tdir) < 0f ? -1f : 1f;
            return new Vector4(ortho, w);
        }
    }
}
