using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure light-space fitting for the directional (key-light) shadow map: builds the orthographic
    /// world-&gt;light-clip matrix that frames a focus sphere (the region the camera looks at), texel-SNAPPED so a
    /// sub-texel camera pan does not slide the sampled shadow edge (the "swimming shadows" fix). No GPU, no engine
    /// state - just matrix math, so the headless <c>ShadowMapMathTests</c> can pin containment + snapping.
    /// </summary>
    /// <remarks>
    /// The fit is deliberately coarse: one ortho frustum around a sphere of <c>radius</c> centred on the camera
    /// focus (no cascades - a single map at the "A"-tier target). The map covers <c>2*radius</c> world units per
    /// axis at <c>resolution</c> texels, so a bigger radius trades crisper contact shadows for coverage. The depth
    /// range spans the sphere along the light axis with slack so a caster a little above/below the focus plane still
    /// writes depth. Depth is authored [0,1] (the DepthRangeZeroToOne convention every supported backend uses).
    /// </remarks>
    internal static class ShadowMapMath
    {
        /// <summary>A stable fallback light direction (straight down) when the caller's is degenerate/zero.</summary>
        static readonly Vector3 FallbackLightDir = new(0f, -1f, 0f);

        /// <summary>Blend weight (0 = uniform/linear split, 1 = logarithmic split) for the practical cascade split.
        /// A logarithmic split packs texels onto the near cascades (where the eye is closest to the ground and needs
        /// the most resolution) but leaves the far cascade too coarse, and a uniform split wastes near texels. The
        /// standard PSSM compromise blends the two. <c>0.6</c> leans logarithmic (crisper near shadows).</summary>
        public const float DefaultSplitLambda = 0.6f;

        /// <summary>
        /// Fill <paramref name="radii"/> (length == <paramref name="count"/>) with each concentric cascade's focus
        /// radius, from the tight near cascade (<paramref name="focusRadius"/>) out to the far cascade
        /// (<paramref name="maxDistance"/>), via the practical split (a <paramref name="lambda"/>-blend of a
        /// logarithmic and a linear progression). Cascade 0 is ALWAYS exactly <paramref name="focusRadius"/> (so the
        /// near-shadow contact quality is preserved and <c>count == 1</c> reproduces the pre-cascade single map), and
        /// the outermost cascade is ALWAYS exactly <paramref name="maxDistance"/>. With <c>count == 1</c> the single
        /// entry is <paramref name="focusRadius"/> (<paramref name="maxDistance"/> unused). Pure, headless-tested.
        /// </summary>
        public static void FillCascadeRadii(Span<float> radii, int count, float focusRadius, float maxDistance, float lambda = DefaultSplitLambda)
        {
            int n = Math.Clamp(count, 1, radii.Length);
            float near = MathF.Max(focusRadius, 1e-3f);
            float far = MathF.Max(maxDistance, near);   // never fit the outer cascade tighter than the near one
            float lam = Math.Clamp(lambda, 0f, 1f);
            if (n == 1) { radii[0] = near; return; }
            for (int i = 0; i < n; i++)
            {
                float f = (float)i / (n - 1);                       // 0 at cascade 0, 1 at the outer cascade
                float lin = near + (far - near) * f;                // linear (uniform) progression
                float log = near * MathF.Pow(far / near, f);        // logarithmic (geometric) progression
                radii[i] = lam * log + (1f - lam) * lin;            // exact at the ends (f==0 -> near, f==1 -> far)
            }
        }

        /// <summary>
        /// Clip-space X remap that packs one cascade's full ortho frustum into column <paramref name="index"/> of a
        /// <paramref name="count"/>-wide side-by-side shadow atlas (there is no viewport in the command-list seam, so
        /// the column placement is baked into the depth-pass matrix and a per-column scissor clips the overflow). It
        /// scales clip.x by <c>1/count</c> and biases it to the column's NDC sub-range, leaving Y and Z (the stored
        /// depth) untouched, so the receiver can sample with the plain per-cascade matrix and map UV into the column
        /// itself. Post-multiply it onto the GPU-clip-corrected per-cascade matrix (row-vector convention:
        /// <c>depthMat = receiverMat * AtlasColumnTransform(i, n)</c>). <c>count == 1</c> is the identity.
        /// </summary>
        public static Matrix4x4 AtlasColumnTransform(int index, int count)
        {
            int n = Math.Max(1, count);
            int i = Math.Clamp(index, 0, n - 1);
            float sx = 1f / n;
            float bx = -1f + (2f * i + 1f) / n;   // maps clip.x [-1,1] -> column i NDC range
            // Row-vector clip' = clip * C:  clip'.x = sx*clip.x + bx*clip.w, and y,z,w are unchanged.
            var c = Matrix4x4.Identity;
            c.M11 = sx;
            c.M41 = bx;
            return c;
        }

        /// <summary>
        /// Pick the tightest cascade whose light-clip projection of <paramref name="worldPos"/> lands inside its map
        /// (UV within <paramref name="uvMargin"/>..1-<paramref name="uvMargin"/> and depth in [0,1]), scanning from
        /// cascade 0 outward, mirroring the receiver shader's selection. Returns the cascade index, or <c>-1</c> when
        /// the point is beyond every cascade (fully lit / faded). The matrices are the per-cascade RECEIVER matrices
        /// (world-&gt;light-clip, as sampled). Pure. Lets the headless test pin "a point at distance d falls in the
        /// expected cascade" without a GPU.
        /// </summary>
        public static int SelectCascade(ReadOnlySpan<Matrix4x4> receiverMats, int count, Vector3 worldPos, float uvMargin = 0f)
        {
            int n = Math.Min(count, receiverMats.Length);
            for (int i = 0; i < n; i++)
            {
                Vector4 lc = Vector4.Transform(new Vector4(worldPos, 1f), receiverMats[i]);
                if (lc.W <= 0f) continue;
                float x = lc.X / lc.W, y = lc.Y / lc.W, z = lc.Z / lc.W;
                float u = x * 0.5f + 0.5f, v = y * 0.5f + 0.5f;
                if (u < uvMargin || u > 1f - uvMargin || v < uvMargin || v > 1f - uvMargin || z < 0f || z > 1f) continue;
                return i;
            }
            return -1;
        }

        /// <summary>World size (in world units) of ONE shadow-map texel for a fit of the given
        /// <paramref name="radius"/> at <paramref name="resolution"/> texels: <c>2*radius/resolution</c>. Used both
        /// for the texel snap and to hand the fragment shader its filter kernel step.</summary>
        public static float TexelWorldSize(float radius, int resolution)
        {
            float r = MathF.Max(radius, 1e-3f);
            int res = Math.Max(resolution, 1);
            return (2f * r) / res;
        }

        /// <summary>
        /// Build the world-&gt;light-clip matrix (view * ortho) framing the sphere of <paramref name="radius"/> at
        /// <paramref name="focus"/>, lit from <paramref name="lightDir"/> (the direction the light travels), snapped
        /// to <paramref name="resolution"/>-texel increments. The result maps the focus sphere into the light clip
        /// box [-1,1] x [-1,1] x [0,1]; a caster inside it writes shadow depth. Degenerate inputs fall back to a
        /// straight-down light so it never NaNs.
        /// </summary>
        public static Matrix4x4 BuildLightViewProj(Vector3 lightDir, Vector3 focus, float radius, int resolution)
        {
            float r = MathF.Max(radius, 1e-3f);
            int res = Math.Max(resolution, 1);

            Vector3 dir = lightDir.LengthSquared() > 1e-8f ? Vector3.Normalize(lightDir) : FallbackLightDir;

            // Pick an up vector not parallel to the light axis (world-up unless the light is near-vertical).
            Vector3 up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;

            // The light VIEW rotation depends only on the light direction + up (both constant across a camera pan), so
            // the light-space X/Y axes are fixed. Snap the FOCUS to the texel grid IN light-view space (not clip), so
            // the frustum origin steps in whole texels as the camera slides: the projected position of any fixed world
            // point then only ever moves by integer texels, killing the sub-texel shadow-edge swim. Build the view
            // around the ORIGIN first to read the fixed rotation, snap there, then place the eye.
            Vector3 axisEye = -dir; // a unit reference eye so CreateLookAt yields the pure light-view rotation basis
            Matrix4x4 rotView = Matrix4x4.CreateLookAt(axisEye, Vector3.Zero, up);

            // Focus in light-view space, quantized to texel-sized increments on X and Y (the map plane).
            Vector3 focusView = Vector3.Transform(focus, rotView);
            float texel = TexelWorldSize(r, res);
            focusView.X = MathF.Round(focusView.X / texel) * texel;
            focusView.Y = MathF.Round(focusView.Y / texel) * texel;
            // Back to world: the snapped focus the frustum centres on.
            Matrix4x4.Invert(rotView, out Matrix4x4 rotViewInv);
            Vector3 snappedFocus = Vector3.Transform(focusView, rotViewInv);

            // Depth slack along the light axis: place the light "eye" a full diameter back so a tall caster between it
            // and the focus plane still writes depth and nothing clips the near plane.
            float depthExtent = 2f * r;
            Vector3 eye = snappedFocus - dir * depthExtent;

            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, snappedFocus, up);
            // Ortho covering [-r, r] in X/Y; near 0 at the eye, far spanning eye->focus->far side of the sphere.
            Matrix4x4 proj = Matrix4x4.CreateOrthographic(2f * r, 2f * r, 0f, depthExtent + 2f * r);
            return view * proj;
        }
    }
}
