using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Turns a chain of points into per-joint world transforms for <c>Scene3D.DrawSkinned</c>,
    /// orienting each frame's run axis along the local chain direction (a simple up-hint frame, sufficient for
    /// tentacles/cables; consumers that already track per-segment rotations can skip this). Presentation only.</summary>
    public static class PolylineFrames
    {
        /// <summary>Build one world transform per point. Each frame is positioned at its point with its
        /// <paramref name="runAxis"/> aligned to the direction toward the next point (the last reuses the
        /// previous direction), using <paramref name="up"/> as the roll reference.</summary>
        public static Matrix4x4[] Build(ReadOnlySpan<Vector3> points, Axis runAxis, Vector3 up)
        {
            if (points.Length == 0) return Array.Empty<Matrix4x4>();
            var frames = new Matrix4x4[points.Length];
            Vector3 prevDir = AxisVec(runAxis);
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 dir = i + 1 < points.Length ? points[i + 1] - points[i] : prevDir;
                if (dir.LengthSquared() < 1e-10f) dir = prevDir;
                dir = Vector3.Normalize(dir);
                prevDir = dir;
                frames[i] = OrientAxisTo(runAxis, dir, up) * Matrix4x4.CreateTranslation(points[i]);
            }
            return frames;
        }

        static Vector3 AxisVec(Axis a) => a switch
        {
            Axis.X => Vector3.UnitX, Axis.Y => Vector3.UnitY, _ => Vector3.UnitZ
        };

        // Rotation mapping the run axis onto `dir`, with `up` as the secondary reference (Gram-Schmidt basis).
        static Matrix4x4 OrientAxisTo(Axis runAxis, Vector3 dir, Vector3 up)
        {
            Vector3 f = dir;
            Vector3 r = Vector3.Cross(up, f);
            if (r.LengthSquared() < 1e-8f)
            {
                // up is parallel to the run direction: pick any axis that is not near-parallel to f so the
                // cross product is well-conditioned (f near +/-X -> use UnitY, else UnitX). Avoids a NaN basis.
                Vector3 alt = MathF.Abs(f.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
                r = Vector3.Cross(alt, f);
            }
            r = Vector3.Normalize(r);
            Vector3 u = Vector3.Normalize(Vector3.Cross(f, r));
            // Rows map local (X=r, Y=u, Z=f) when runAxis is Z; remap for X/Y so the chosen axis lands on dir.
            (Vector3 lx, Vector3 ly, Vector3 lz) = runAxis switch
            {
                Axis.X => (f, u, r),
                Axis.Y => (r, f, u),
                _ => (r, u, f),
            };
            return new Matrix4x4(
                lx.X, lx.Y, lx.Z, 0,
                ly.X, ly.Y, ly.Z, 0,
                lz.X, lz.Y, lz.Z, 0,
                0, 0, 0, 1);
        }
    }
}
