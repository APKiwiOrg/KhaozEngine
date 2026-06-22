using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Pure (GPU-free) helpers for building a camera-facing BEAM quad: a flat strip stretched along the axis
    /// a-&gt;b whose width direction faces the camera (perpendicular to both the beam axis and the view
    /// direction). Sibling to <see cref="BillboardGeometry"/>; the internal beam renderer consumes it.
    /// Unit-testable without a renderer.
    /// </summary>
    public static class BeamGeometry
    {
        /// <summary>
        /// The 4 corners of the camera-facing strip from <paramref name="a"/> to <paramref name="b"/>:
        /// <c>side = normalize(cross(viewDir, axis))</c>, each end offset <c>±side*(width/2)</c>. Outputs the
        /// a-end pair (<paramref name="aLeft"/>/<paramref name="aRight"/>) and the b-end pair. Returns false (no
        /// corners written) when the beam is degenerate (<paramref name="a"/>≈<paramref name="b"/> or
        /// <paramref name="width"/> &lt;= 0). When the axis is ~parallel to <paramref name="viewDir"/> (the beam
        /// points at/away from the camera) the cross degenerates; a stable perpendicular is chosen so the output
        /// stays finite (the strip is then edge-on, which is correct).
        /// </summary>
        public static bool Corners(Vector3 a, Vector3 b, Vector3 viewDir, float width,
            out Vector3 aLeft, out Vector3 aRight, out Vector3 bLeft, out Vector3 bRight)
        {
            aLeft = aRight = bLeft = bRight = default;
            Vector3 axisRaw = b - a;
            float len2 = axisRaw.LengthSquared();
            if (len2 < 1e-12f || width <= 0f) return false;

            Vector3 axis = axisRaw / MathF.Sqrt(len2);
            Vector3 vd = viewDir.LengthSquared() < 1e-12f ? -Vector3.UnitZ : Vector3.Normalize(viewDir);

            Vector3 s = Vector3.Cross(vd, axis);
            if (s.LengthSquared() < 1e-8f)          // axis ~parallel to viewDir: pick any perpendicular to axis
                s = PerpendicularTo(axis);
            Vector3 side = Vector3.Normalize(s) * (width * 0.5f);

            aLeft = a - side; aRight = a + side;
            bLeft = b - side; bRight = b + side;
            return true;
        }

        /// <summary>An arbitrary unit vector perpendicular to <paramref name="axis"/> (assumed unit length),
        /// chosen stably from whichever world axis is least parallel to it.</summary>
        static Vector3 PerpendicularTo(Vector3 axis)
        {
            Vector3 reference = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            return Vector3.Normalize(Vector3.Cross(reference, axis));
        }

        /// <summary>
        /// Write the 6 triangle-list vertex positions (two triangles aLeft,aRight,bLeft and aRight,bRight,bLeft)
        /// for the beam strip into <paramref name="positions"/>, and matching UVs into <paramref name="uvs"/>:
        /// <c>u</c> is the across coordinate (0 on the left edge, 1 on the right), <c>v</c> the along coordinate
        /// (0 at <paramref name="a"/>, 1 at <paramref name="b"/>). Both spans must hold at least 6 elements.
        /// Returns 6, or 0 when the beam is degenerate (nothing written).
        /// </summary>
        public static int Triangles(Vector3 a, Vector3 b, Vector3 viewDir, float width,
            Span<Vector3> positions, Span<Vector2> uvs)
        {
            if (positions.Length < 6) throw new ArgumentException("positions span must hold at least 6 vertices", nameof(positions));
            if (uvs.Length < 6) throw new ArgumentException("uvs span must hold at least 6 vertices", nameof(uvs));

            if (!Corners(a, b, viewDir, width, out var aL, out var aR, out var bL, out var bR))
                return 0;

            positions[0] = aL; uvs[0] = new Vector2(0f, 0f);
            positions[1] = aR; uvs[1] = new Vector2(1f, 0f);
            positions[2] = bL; uvs[2] = new Vector2(0f, 1f);
            positions[3] = aR; uvs[3] = new Vector2(1f, 0f);
            positions[4] = bR; uvs[4] = new Vector2(1f, 1f);
            positions[5] = bL; uvs[5] = new Vector2(0f, 1f);
            return 6;
        }
    }
}
