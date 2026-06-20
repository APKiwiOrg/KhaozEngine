using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Pure (GPU-free) helpers for building a camera-facing billboard quad. Given a world centre, a half-size,
    /// and a camera right/up basis, produces the 4 corners (or 6 triangle-list vertices) of a square that faces
    /// the camera, plus matching UVs. The camera basis can be derived from a forward vector via
    /// <see cref="CameraBasis"/>. Unit-testable without a renderer; the internal billboard renderer consumes it.
    /// </summary>
    public static class BillboardGeometry
    {
        /// <summary>
        /// Derive an orthonormal camera right/up basis from a view <paramref name="forward"/> direction:
        /// <c>right = normalize(cross(UnitY, forward))</c>, falling back to <see cref="Vector3.UnitX"/> when
        /// forward is ~parallel to <see cref="Vector3.UnitY"/> (cross degenerates); then
        /// <c>up = normalize(cross(forward, right))</c>. Both outputs are unit length and perpendicular to each
        /// other and to forward.
        /// </summary>
        public static void CameraBasis(Vector3 forward, out Vector3 right, out Vector3 up)
        {
            if (forward.LengthSquared() < 1e-12f) forward = -Vector3.UnitZ;
            forward = Vector3.Normalize(forward);

            Vector3 r = Vector3.Cross(Vector3.UnitY, forward);
            if (r.LengthSquared() < 1e-8f)          // forward ~parallel to UnitY: cross is ~zero, pick a stable axis
                r = Vector3.UnitX;
            right = Vector3.Normalize(r);
            up = Vector3.Normalize(Vector3.Cross(forward, right));
        }

        /// <summary>
        /// The 4 corners of the quad, in order: bottom-left, bottom-right, top-left, top-right, where
        /// <c>corner = center ± right*size ± up*size</c> (so the quad spans 2*size on each axis, centred on
        /// <paramref name="center"/>). UVs map to (0,0) BL, (1,0) BR, (0,1) TL, (1,1) TR.
        /// </summary>
        public static void Corners(Vector3 center, float size, Vector3 right, Vector3 up,
            out Vector3 bl, out Vector3 br, out Vector3 tl, out Vector3 tr)
        {
            Vector3 r = right * size;
            Vector3 u = up * size;
            bl = center - r - u;
            br = center + r - u;
            tl = center - r + u;
            tr = center + r + u;
        }

        /// <summary>The 4 corner UVs, matching <see cref="Corners"/> order: (0,0),(1,0),(0,1),(1,1).</summary>
        public static readonly Vector2 UvBL = new(0f, 0f);
        public static readonly Vector2 UvBR = new(1f, 0f);
        public static readonly Vector2 UvTL = new(0f, 1f);
        public static readonly Vector2 UvTR = new(1f, 1f);

        /// <summary>
        /// Write the 6 triangle-list vertex positions (two triangles BL,BR,TL and BR,TR,TL) for the quad into
        /// <paramref name="positions"/>, and the matching UVs into <paramref name="uvs"/>. Both spans must hold
        /// at least 6 elements. Returns 6 (the count written).
        /// </summary>
        public static int Triangles(Vector3 center, float size, Vector3 right, Vector3 up,
            Span<Vector3> positions, Span<Vector2> uvs)
        {
            if (positions.Length < 6) throw new ArgumentException("positions span must hold at least 6 vertices", nameof(positions));
            if (uvs.Length < 6) throw new ArgumentException("uvs span must hold at least 6 vertices", nameof(uvs));

            Corners(center, size, right, up, out var bl, out var br, out var tl, out var tr);

            // Triangle 1: BL, BR, TL   Triangle 2: BR, TR, TL
            positions[0] = bl; uvs[0] = UvBL;
            positions[1] = br; uvs[1] = UvBR;
            positions[2] = tl; uvs[2] = UvTL;
            positions[3] = br; uvs[3] = UvBR;
            positions[4] = tr; uvs[4] = UvTR;
            positions[5] = tl; uvs[5] = UvTL;
            return 6;
        }

        /// <summary>
        /// Like <see cref="Triangles(Vector3,float,Vector3,Vector3,Span{Vector3},Span{Vector2})"/> but maps the quad
        /// to a sub-rectangle of a texture instead of the full <c>[0,1]²</c>, for sprite-sheet frame selection.
        /// <paramref name="sourceUv"/> is <c>(u0,v0,u1,v1)</c>: <c>(u0,v0)</c> maps to the bottom-left corner and
        /// <c>(u1,v1)</c> to the top-right, following the same corner order as the full-square overload. Pass
        /// <c>(0,0,1,1)</c> for the whole texture; swap <c>v0</c>/<c>v1</c> (or <c>u0</c>/<c>u1</c>) to flip a frame.
        /// Both spans must hold at least 6 elements. Returns 6.
        /// </summary>
        public static int Triangles(Vector3 center, float size, Vector3 right, Vector3 up, Vector4 sourceUv,
            Span<Vector3> positions, Span<Vector2> uvs)
        {
            if (positions.Length < 6) throw new ArgumentException("positions span must hold at least 6 vertices", nameof(positions));
            if (uvs.Length < 6) throw new ArgumentException("uvs span must hold at least 6 vertices", nameof(uvs));

            Corners(center, size, right, up, out var bl, out var br, out var tl, out var tr);

            // Map the unit-square corners onto the source rect: u0,v0 = BL ... u1,v1 = TR.
            var uvBL = new Vector2(sourceUv.X, sourceUv.Y);
            var uvBR = new Vector2(sourceUv.Z, sourceUv.Y);
            var uvTL = new Vector2(sourceUv.X, sourceUv.W);
            var uvTR = new Vector2(sourceUv.Z, sourceUv.W);

            positions[0] = bl; uvs[0] = uvBL;
            positions[1] = br; uvs[1] = uvBR;
            positions[2] = tl; uvs[2] = uvTL;
            positions[3] = br; uvs[3] = uvBR;
            positions[4] = tr; uvs[4] = uvTR;
            positions[5] = tl; uvs[5] = uvTL;
            return 6;
        }
    }
}
