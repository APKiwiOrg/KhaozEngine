using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Pure (GPU-free) builder for a motion-trail ribbon: turns an ordered list of <see cref="TrailSample"/> (oldest
    /// first) into a triangle-list strip. Sibling to <see cref="BeamGeometry"/> (a trail is a beam generalised from
    /// two points to N, with per-sample width and alpha). Each sample's across-direction is computed ONCE - from the
    /// bisector (miter) of its two adjacent segment tangents - so the two segments meeting at a sample share the same
    /// corner pair and the joint is continuous (no gap or overlap). For a plain sample the across-direction faces the
    /// camera (<c>cross(viewDir, tangent)</c>); a sample with a non-zero <see cref="TrailSample.Facing"/> twists onto
    /// a fixed plane (<c>cross(Facing, tangent)</c>). Degenerate cases (tangent parallel to the facing vector, a
    /// near-180 fold) fall back to a stable perpendicular so the output stays finite. Unit-testable without a renderer.
    /// </summary>
    public static class TrailGeometry
    {
        /// <summary>
        /// Append the triangle-list strip for <paramref name="samples"/> (oldest-first) into the parallel output
        /// lists (the caller clears them). Per vertex: <paramref name="positions"/> world position,
        /// <paramref name="uvs"/> <c>(u,v)</c> where <c>u</c> is across (0 left edge, 1 right) and <c>v</c> is along
        /// (0 at the tail, 1 at the head), and <paramref name="alphas"/> the sample's alpha. Returns the vertex count
        /// written: <c>6*(n-1)</c> for <c>n</c> samples, or 0 (nothing written) when <c>n &lt; 2</c>.
        /// <paramref name="viewDir"/> is the camera forward for camera-facing samples.
        /// </summary>
        public static int Build(ReadOnlySpan<TrailSample> samples, Vector3 viewDir,
            List<Vector3> positions, List<Vector2> uvs, List<float> alphas)
        {
            int n = samples.Length;
            if (n < 2) return 0;

            Vector3 vd = viewDir.LengthSquared() < 1e-12f ? -Vector3.UnitZ : Vector3.Normalize(viewDir);
            float invSpan = 1f / (n - 1);

            for (int i = 0; i < n - 1; i++)
            {
                FrameAt(samples, i, vd, out var aL, out var aR);
                FrameAt(samples, i + 1, vd, out var bL, out var bR);
                float vA = i * invSpan;
                float vB = (i + 1) * invSpan;
                float alphaA = samples[i].Alpha;
                float alphaB = samples[i + 1].Alpha;

                // Two triangles (aL,aR,bL) and (aR,bR,bL) - same winding as BeamGeometry.Triangles.
                Emit(positions, uvs, alphas, aL, 0f, vA, alphaA);
                Emit(positions, uvs, alphas, aR, 1f, vA, alphaA);
                Emit(positions, uvs, alphas, bL, 0f, vB, alphaB);
                Emit(positions, uvs, alphas, aR, 1f, vA, alphaA);
                Emit(positions, uvs, alphas, bR, 1f, vB, alphaB);
                Emit(positions, uvs, alphas, bL, 0f, vB, alphaB);
            }
            return 6 * (n - 1);
        }

        static void Emit(List<Vector3> positions, List<Vector2> uvs, List<float> alphas,
            Vector3 pos, float u, float v, float alpha)
        {
            positions.Add(pos);
            uvs.Add(new Vector2(u, v));
            alphas.Add(alpha);
        }

        // The left/right corners at sample i. Deterministic in (samples, i, vd), so a sample shared by two segments
        // yields identical corners each call -> continuous joints.
        static void FrameAt(ReadOnlySpan<TrailSample> samples, int i, Vector3 vd, out Vector3 left, out Vector3 right)
        {
            TrailSample s = samples[i];
            Vector3 tangent = TangentAt(samples, i);
            Vector3 dir = s.Facing.LengthSquared() > 1e-12f ? Vector3.Normalize(s.Facing) : vd;

            Vector3 acrossRaw = Vector3.Cross(dir, tangent);
            if (acrossRaw.LengthSquared() < 1e-8f)      // dir ~parallel to the tangent: pick a stable perpendicular
                acrossRaw = PerpendicularTo(tangent);
            Vector3 across = Vector3.Normalize(acrossRaw) * s.HalfWidth;

            left = s.Position - across;
            right = s.Position + across;
        }

        // Smoothed (bisector) tangent at sample i: the direction from the previous sample to the next, so the offset
        // miters the joint. Endpoints use their single adjacent segment. A degenerate bisector (a near-180 fold where
        // the neighbours coincide) falls back to an adjacent segment direction, then to +X, so it always stays finite.
        static Vector3 TangentAt(ReadOnlySpan<TrailSample> samples, int i)
        {
            int n = samples.Length;
            Vector3 t;
            if (i == 0) t = samples[1].Position - samples[0].Position;
            else if (i == n - 1) t = samples[n - 1].Position - samples[n - 2].Position;
            else t = samples[i + 1].Position - samples[i - 1].Position;

            if (t.LengthSquared() < 1e-10f)
                t = i > 0 ? samples[i].Position - samples[i - 1].Position
                          : samples[i + 1].Position - samples[i].Position;

            return t.LengthSquared() < 1e-10f ? Vector3.UnitX : Vector3.Normalize(t);
        }

        // An arbitrary unit vector perpendicular to axis (assumed unit), chosen stably from whichever world axis is
        // least parallel to it. Mirrors BeamGeometry's fallback so an edge-on trail stays finite and full-width.
        static Vector3 PerpendicularTo(Vector3 axis)
        {
            Vector3 reference = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            return Vector3.Normalize(Vector3.Cross(reference, axis));
        }
    }
}
