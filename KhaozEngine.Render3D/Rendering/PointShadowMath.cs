using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Pure face math for the omnidirectional POINT-light shadow atlas: the six face bases, the shared 90 degree
    /// projection, the atlas cell bake that places one (face, light row) cell, and the cube-map face selection the
    /// receiver mirrors. No GPU and no engine state, so the headless <c>PointShadowMathTests</c> pins the whole
    /// convention. See <c>docs/design/POINT-LIGHT-SHADOWS-DESIGN-2026-09-17.md</c>, decision 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ONE equation binds the two halves of the feature, and everything here exists to satisfy it: a point in
    /// direction <c>d</c> from the light must rasterize into the atlas texel that
    /// <c>AtlasUv(face, slot, rows, FaceAndUv(d))</c> names. The pass reaches that texel through
    /// <see cref="FaceViewProjection"/> and the receiver reaches it through <see cref="FaceAndUv"/>'s table, so if
    /// the two ever disagree a shadow lands on the wrong wall rather than failing loudly.
    /// <c>PointShadowPassGpuTests</c> reads the atlas back on a real device to keep them honest.
    /// </para>
    /// <para>
    /// The clip-to-atlas mapping is the engine's AUTHORED convention (Metal/Direct3D, see <c>GpuClip</c>): clip x
    /// +1 is the right edge, clip y +1 is the TOP edge, and a texture readback is row-major from the top. So
    /// <c>u = (x + 1) / 2</c> and <c>v = (1 - y) / 2</c>, which is why the row bake below biases Y the opposite way
    /// from the way the column bake biases X. The per-backend adaptation is <c>GpuClip.Correct</c>, applied to the
    /// FINISHED matrix by the caller, never folded in here.
    /// </para>
    /// <para>
    /// THE FACE BASES ARE MIRRORED, AND THAT IS THE CUBE-MAP CONVENTION RATHER THAN A SIGN SLIP. The design doc's
    /// table is the standard cube-map one, which is left-handed with respect to the world: for every face, the
    /// basis that sends <c>sc</c> to clip x and <c>tc</c> to atlas v has determinant -1, so no right-handed
    /// <c>Matrix4x4.CreateLookAt</c> can produce it and the bases are written out directly instead. The visible
    /// consequence is that triangle winding is reversed in this pass, which is why
    /// <c>PointShadowRenderer</c> culls nothing rather than naming a face to cull.
    /// </para>
    /// </remarks>
    internal static class PointShadowMath
    {
        /// <summary>The near plane every face projects with. Small enough that a caster standing against the
        /// fixture still records, large enough to keep the [0,1] depth buffer usable for the nearest-surface
        /// resolve (the STORED value is a linear distance, so this only sizes the depth test).</summary>
        public const float NearMetres = 0.05f;

        /// <summary>The six cube-map faces, in the design doc's order: +X, -X, +Y, -Y, +Z, -Z.</summary>
        public const int FaceCount = 6;

        // Per face: the axis the face looks down, then the basis rows of the view rotation. Row-vector convention,
        // so a world direction d maps to view (dot(d, X), dot(d, Y), dot(d, Z)) and the perspective below divides
        // by -z, which is dot(d, forward). X and Y are chosen so clip x = sc / ma and clip y = -tc / ma, i.e. the
        // design's u grows with clip x and its v grows DOWNWARD, matching the readback.
        static readonly Vector3[] Forward =
        {
            new(1f, 0f, 0f), new(-1f, 0f, 0f), new(0f, 1f, 0f), new(0f, -1f, 0f), new(0f, 0f, 1f), new(0f, 0f, -1f),
        };

        static readonly Vector3[] BasisX =
        {
            new(0f, 0f, -1f), new(0f, 0f, 1f), new(1f, 0f, 0f), new(1f, 0f, 0f), new(1f, 0f, 0f), new(-1f, 0f, 0f),
        };

        static readonly Vector3[] BasisY =
        {
            new(0f, 1f, 0f), new(0f, 1f, 0f), new(0f, 0f, -1f), new(0f, 0f, 1f), new(0f, 1f, 0f), new(0f, 1f, 0f),
        };

        /// <summary>The world-to-view rotation + translation for one face of a light at <paramref name="lightPos"/>
        /// (the SAME space the caster geometry is in, so render space when a render origin is in force). Laid out
        /// exactly as <c>Matrix4x4.CreateLookAt</c> lays its result out, and built by hand rather than through it
        /// because the face bases are mirrored (see the type remarks).</summary>
        public static Matrix4x4 FaceView(int face, Vector3 lightPos)
        {
            int f = Math.Clamp(face, 0, FaceCount - 1);
            Vector3 x = BasisX[f], y = BasisY[f], z = -Forward[f];
            return new Matrix4x4(
                x.X, y.X, z.X, 0f,
                x.Y, y.Y, z.Y, 0f,
                x.Z, y.Z, z.Z, 0f,
                -Vector3.Dot(x, lightPos), -Vector3.Dot(y, lightPos), -Vector3.Dot(z, lightPos), 1f);
        }

        /// <summary>The 90 degree square perspective every face shares: near at <see cref="NearMetres"/>, far at the
        /// light's <paramref name="radius"/> (nothing past the radius can be lit, so nothing past it can shadow).
        /// Depth is authored [0,1], the convention every supported backend uses.</summary>
        public static Matrix4x4 FaceProjection(float radius) =>
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI * 0.5f, 1f, NearMetres, MathF.Max(radius, NearMetres * 2f));

        /// <summary>
        /// The clip-space remap that packs one face's full square frustum into atlas cell
        /// (<paramref name="face"/>, <paramref name="slot"/>) of a six-column by <paramref name="rows"/>-row
        /// atlas. There is no viewport in the command-list seam, so the cell placement is baked into the matrix and
        /// a per-cell scissor clips the overflow, exactly as <c>ShadowMapMath.AtlasColumnTransform</c> does for the
        /// cascade columns. Post-multiply it (row-vector convention).
        /// <para>
        /// X is the cascade bake one more time. Y is its MIRROR, because atlas v runs down the target while clip y
        /// runs up it: row <paramref name="slot"/> occupies v in <c>[slot / rows, (slot + 1) / rows]</c>, which is
        /// clip y in <c>[1 - 2(slot + 1) / rows, 1 - 2 slot / rows]</c>.
        /// </para>
        /// </summary>
        public static Matrix4x4 CellBake(int face, int slot, int rows)
        {
            int n = Math.Max(1, rows);
            int s = Math.Clamp(slot, 0, n - 1);
            int f = Math.Clamp(face, 0, FaceCount - 1);
            var c = Matrix4x4.Identity;
            c.M11 = 1f / FaceCount;
            c.M41 = -1f + (2f * f + 1f) / FaceCount;
            c.M22 = 1f / n;
            c.M42 = 1f - (2f * s + 1f) / n;
            return c;
        }

        /// <summary>The finished world-to-cell matrix for one face of one light row: view, projection and cell
        /// bake. In the AUTHORED clip convention, so the caller applies <c>GpuClip.Correct</c> before handing it to
        /// a backend.</summary>
        public static Matrix4x4 FaceViewProjection(int face, int slot, int rows, Vector3 lightPos, float radius) =>
            FaceView(face, lightPos) * FaceProjection(radius) * CellBake(face, slot, rows);

        /// <summary>
        /// The design doc's face table: which cube face a light-to-fragment direction falls on, and where inside
        /// that face. The GLSL receiver mirrors this verbatim. A degenerate (zero) direction answers face 0's
        /// centre rather than dividing by zero, so a fragment exactly at the light is unshadowed rather than NaN.
        /// </summary>
        public static void FaceAndUv(Vector3 dir, out int face, out Vector2 uv)
        {
            float ax = MathF.Abs(dir.X), ay = MathF.Abs(dir.Y), az = MathF.Abs(dir.Z);
            float ma;
            float sc, tc;
            if (ax >= ay && ax >= az)
            {
                ma = ax;
                face = dir.X > 0f ? 0 : 1;
                sc = dir.X > 0f ? -dir.Z : dir.Z;
                tc = -dir.Y;
            }
            else if (ay >= az)
            {
                ma = ay;
                face = dir.Y > 0f ? 2 : 3;
                sc = dir.X;
                tc = dir.Y > 0f ? dir.Z : -dir.Z;
            }
            else
            {
                ma = az;
                face = dir.Z > 0f ? 4 : 5;
                sc = dir.Z > 0f ? dir.X : -dir.X;
                tc = -dir.Y;
            }
            if (ma <= 0f) { face = 0; uv = new Vector2(0.5f, 0.5f); return; }
            uv = new Vector2((sc / ma + 1f) * 0.5f, (tc / ma + 1f) * 0.5f);
        }

        /// <summary>Where one face-local UV sits in the whole atlas: column <paramref name="face"/> of six,
        /// row <paramref name="slot"/> of <paramref name="rows"/>.</summary>
        public static Vector2 AtlasUv(int face, int slot, int rows, Vector2 uv)
        {
            int n = Math.Max(1, rows);
            return new Vector2((face + uv.X) / FaceCount, (slot + uv.Y) / n);
        }
    }
}
