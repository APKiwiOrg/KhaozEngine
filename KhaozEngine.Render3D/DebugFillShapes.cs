using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Pure geometry builders for the FILLED debug overlay (the translucent counterpart to the line builders in
    /// <see cref="DebugShapes"/>). Each method appends TRIANGLE vertices (always a multiple of 3
    /// <see cref="Vector3"/>, three per triangle) to the supplied list; the caller (typically
    /// <see cref="Scene3D"/>) attaches the colour. Winding is consistent: each emitted triangle's geometric
    /// normal points along the plane's <c>normal</c> argument (the rasterizer culls nothing, so this is cosmetic,
    /// but it keeps the output predictable and testable). No GPU, so the geometry is unit-testable headlessly.
    /// </summary>
    public static class DebugFillShapes
    {
        /// <summary>Append the 2 triangles (6 vertices) of a flat quad centred at <paramref name="center"/>, lying
        /// in the plane with the given <paramref name="normal"/>, with its first in-plane axis along
        /// <paramref name="uAxis"/> (projected perpendicular to the normal). <paramref name="halfExtents"/>.X scales
        /// the u axis, .Y the perpendicular v axis. Degenerate inputs (zero normal, or a uAxis parallel to the
        /// normal) append nothing.</summary>
        public static void FilledQuad(List<Vector3> tris, Vector3 center, Vector3 normal, Vector3 uAxis, Vector2 halfExtents)
        {
            if (!PlaneBasis(normal, uAxis, out Vector3 u, out Vector3 v)) return;
            Vector3 du = u * halfExtents.X;
            Vector3 dv = v * halfExtents.Y;
            Vector3 p00 = center - du - dv;
            Vector3 p10 = center + du - dv;
            Vector3 p11 = center + du + dv;
            Vector3 p01 = center - du + dv;
            // CCW about +normal: (p00,p10,p11) then (p00,p11,p01), sharing the p00-p11 diagonal.
            tris.Add(p00); tris.Add(p10); tris.Add(p11);
            tris.Add(p00); tris.Add(p11); tris.Add(p01);
        }

        /// <summary>Append a filled disc as a triangle fan of <paramref name="segmentCount"/> triangles
        /// (<c>segmentCount*3</c> vertices) at <paramref name="radius"/> from <paramref name="center"/>, in the
        /// plane perpendicular to <paramref name="normal"/>. Degenerate inputs (fewer than 3 segments, or a zero
        /// normal) append nothing.</summary>
        public static void FilledCircle(List<Vector3> tris, Vector3 center, Vector3 normal, float radius, int segmentCount)
        {
            if (segmentCount < 3) return;
            // Any in-plane reference axis works for a disc; pick one not parallel to the normal.
            Vector3 reference = Math.Abs(Vector3.Normalize(SafeNormal(normal)).Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            if (!PlaneBasis(normal, Vector3.Cross(reference, normal), out Vector3 u, out Vector3 v)) return;

            Vector3 Rim(int i)
            {
                float t = (float)(i % segmentCount) / segmentCount * MathF.Tau;
                return center + (u * MathF.Cos(t) + v * MathF.Sin(t)) * radius;
            }

            for (int i = 0; i < segmentCount; i++)
            {
                // CCW about +normal: each wedge is (center, rim[i], rim[i+1]).
                tris.Add(center);
                tris.Add(Rim(i));
                tris.Add(Rim(i + 1));
            }
        }

        /// <summary>Append a triangle fan from <paramref name="center"/> out to an arbitrary boundary
        /// <paramref name="rim"/> (an already-ordered polygon outline, e.g. a turret's star-shaped line-of-sight
        /// area). For each adjacent rim pair this appends the triangle (center, rim[i], rim[i+1]); when
        /// <paramref name="closed"/> it also appends the wrap triangle (center, rim[last], rim[0]) to seal the loop.
        /// Winding follows the same convention as <see cref="FilledCircle"/>: a rim wound CCW about the desired
        /// +normal yields triangles facing +normal. Degenerate input (fewer than 2 rim points) appends nothing.</summary>
        public static void FilledFan(List<Vector3> tris, Vector3 center, IReadOnlyList<Vector3> rim, bool closed)
        {
            if (rim == null || rim.Count < 2) return;

            for (int i = 0; i < rim.Count - 1; i++)
            {
                // CCW about +normal: each wedge is (center, rim[i], rim[i+1]).
                tris.Add(center);
                tris.Add(rim[i]);
                tris.Add(rim[i + 1]);
            }

            if (closed)
            {
                // Seal the loop with the wrap wedge (center, rim[last], rim[0]).
                tris.Add(center);
                tris.Add(rim[rim.Count - 1]);
                tris.Add(rim[0]);
            }
        }

        // Orthonormal in-plane basis (u, v) with u along uAxis projected off the normal and v = cross(n, u), so a
        // triangle wound (a, a+u, a+u+v) has its geometric normal along +n. Returns false on degenerate input.
        static bool PlaneBasis(Vector3 normal, Vector3 uAxis, out Vector3 u, out Vector3 v)
        {
            u = default; v = default;
            if (normal.LengthSquared() < 1e-12f) return false;
            Vector3 n = Vector3.Normalize(normal);
            Vector3 uProj = uAxis - n * Vector3.Dot(uAxis, n);   // project uAxis into the plane
            if (uProj.LengthSquared() < 1e-12f) return false;    // uAxis parallel to normal
            u = Vector3.Normalize(uProj);
            v = Vector3.Cross(n, u);
            return true;
        }

        static Vector3 SafeNormal(Vector3 normal) =>
            normal.LengthSquared() < 1e-12f ? Vector3.UnitY : normal;
    }
}
