using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure light-space fitting for the directional (key-light) shadow map: builds the orthographic
    /// world-&gt;light-clip matrix that frames a per-cascade slice bounding sphere (the camera-frustum slab a
    /// cascade covers), texel-SNAPPED so a sub-texel camera pan does not slide the sampled shadow edge (the
    /// "swimming shadows" fix). No GPU, no engine state - just matrix math, so the headless
    /// <c>ShadowMapMathTests</c> can pin containment + snapping.
    /// </summary>
    /// <remarks>
    /// The fit is per-cascade: <see cref="FrustumCornersWorld"/> unprojects the camera frustum's 8 world corners
    /// once per frame, and each cascade calls <see cref="SliceBoundingSphere"/> with its near/far edge fractions
    /// (from <see cref="FillCascadeSplits"/>) to get the sphere that exactly contains that slice of the ACTUAL
    /// camera frustum - not a fixed-radius sphere around the gaze focus point. That is what fixes the near-caster
    /// regression: a point visible at the bottom of the screen but far from the centre of gaze still lands inside
    /// its slice's frustum corners, so it gets near-cascade texel density regardless of where the camera looks.
    /// Each cascade still ends up as one ortho frustum around a centre + radius, still texel-snapped by
    /// <see cref="BuildLightViewProj"/>. The map covers <c>2*radius</c> world units per axis at <c>resolution</c>
    /// texels, so a bigger radius trades crisper contact shadows for coverage. Depth is authored [0,1] (the
    /// DepthRangeZeroToOne convention every supported backend uses).
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
        /// Fill <paramref name="splits"/> (length == <paramref name="count"/>) with each cascade's outer VIEW-DEPTH
        /// split distance, from the tight near cascade (<paramref name="nearDistance"/>) out to the far cascade
        /// (<paramref name="maxDistance"/>), via the practical split (a <paramref name="lambda"/>-blend of a
        /// logarithmic and a linear progression). Cascade <c>i</c> covers view depth <c>splits[i-1]</c>..
        /// <c>splits[i]</c>, with the camera near plane sitting below <c>splits[0]</c>. Cascade 0 is ALWAYS exactly
        /// <paramref name="nearDistance"/> (so the near-shadow contact quality is preserved and <c>count == 1</c>
        /// reproduces the pre-cascade single map), and the outermost cascade is ALWAYS exactly
        /// <paramref name="maxDistance"/>. With <c>count == 1</c> the single entry is <paramref name="nearDistance"/>
        /// (<paramref name="maxDistance"/> unused). Pure, headless-tested.
        /// </summary>
        public static void FillCascadeSplits(Span<float> splits, int count, float nearDistance, float maxDistance, float lambda = DefaultSplitLambda)
        {
            int n = Math.Clamp(count, 1, splits.Length);
            float near = MathF.Max(nearDistance, 1e-3f);
            float far = MathF.Max(maxDistance, near);   // never fit the outer cascade tighter than the near one
            float lam = Math.Clamp(lambda, 0f, 1f);
            if (n == 1) { splits[0] = near; return; }
            for (int i = 0; i < n; i++)
            {
                float f = (float)i / (n - 1);                       // 0 at cascade 0, 1 at the outer cascade
                float lin = near + (far - near) * f;                // linear (uniform) progression
                float log = near * MathF.Pow(far / near, f);        // logarithmic (geometric) progression
                splits[i] = lam * log + (1f - lam) * lin;           // exact at the ends (f==0 -> near, f==1 -> far)
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

        /// <summary>
        /// The CPU mirror of the shadow DEPTH pass's near-plane pancake: a caster in front of the light's near plane
        /// (light-clip depth below 0) records at the near plane rather than being clipped away, so its silhouette
        /// still shadows everything behind it. Clamping is the correct answer for a directional light because every
        /// caster up-light of the near plane shadows the whole depth range below it, and it is what lets
        /// <see cref="BuildLightViewProj"/>'s eye placement be a texel-density choice instead of a correctness one
        /// (see the note there). The depth pass does this per fragment in
        /// <c>ShaderSources.ShadowDepthVert</c>/<c>ShadowDepthFrag</c> and their dissolve + skinned siblings. This is
        /// the same clamp, so the headless tests can pin the contract without a GPU. Mirrors
        /// <see cref="SelectCascade"/>'s role for the RECEIVER shader.
        /// </summary>
        public static float PancakeDepth(float lightClipDepth) => MathF.Max(lightClipDepth, 0f);

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
        /// Unproject the camera frustum's 8 corners into world space from the CPU-authored
        /// <paramref name="viewProj"/> (NDC z in [0,1], the engine's pre-GpuClip convention). Near-plane quad
        /// at indices 0..3 and far-plane quad at 4..7, in matching XY order, so corner <c>i+4</c> is the far
        /// end of near corner <c>i</c>'s frustum edge (the invariant <see cref="SliceBoundingSphere"/> slices
        /// along). Works for perspective and orthographic projections alike. Returns <c>false</c> for a
        /// non-invertible matrix or a degenerate unproject (a caller skips shadows that frame).
        /// </summary>
        public static bool FrustumCornersWorld(in Matrix4x4 viewProj, Span<Vector3> corners)
        {
            if (corners.Length < 8 || !Matrix4x4.Invert(viewProj, out Matrix4x4 inv)) return false;
            int k = 0;
            for (int z = 0; z <= 1; z++)
                for (int y = -1; y <= 1; y += 2)
                    for (int x = -1; x <= 1; x += 2)
                    {
                        Vector4 p = Vector4.Transform(new Vector4(x, y, z, 1f), inv);
                        if (MathF.Abs(p.W) < 1e-9f) return false;
                        corners[k++] = new Vector3(p.X, p.Y, p.Z) / p.W;
                    }
            return true;
        }

        /// <summary>
        /// Bounding sphere of the camera-frustum slice between edge fractions <paramref name="tNear"/> and
        /// <paramref name="tFar"/> (0 = near plane, 1 = far plane). View depth varies LINEARLY along each
        /// near-to-far frustum edge, so lerping the corner pairs by t yields the true camera-depth slice for
        /// both perspective and orthographic projections. The centre is placed on the axis between the two
        /// slice-quad centroids so the worst near-corner and far-corner distances balance, and the radius is
        /// the exact maximum corner distance, so all 8 slice corners are contained. Deterministic and
        /// rotation-invariant: a camera rotation transforms the corners rigidly, so the radius is unchanged
        /// and only the centre moves - the property that keeps the ortho extent (and the texel world size the
        /// snap quantizes by) from breathing as the camera turns.
        /// </summary>
        public static void SliceBoundingSphere(ReadOnlySpan<Vector3> corners, float tNear, float tFar,
            out Vector3 center, out float radius)
        {
            Span<Vector3> s = stackalloc Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                Vector3 edge = corners[i + 4] - corners[i];
                s[i] = corners[i] + edge * tNear;
                s[i + 4] = corners[i] + edge * tFar;
            }
            Vector3 a = (s[0] + s[1] + s[2] + s[3]) * 0.25f;
            Vector3 b = (s[4] + s[5] + s[6] + s[7]) * 0.25f;
            float rn2 = 0f, rf2 = 0f;
            for (int i = 0; i < 4; i++)
            {
                rn2 = MathF.Max(rn2, (s[i] - a).LengthSquared());
                rf2 = MathF.Max(rf2, (s[i + 4] - b).LengthSquared());
            }
            Vector3 ab = b - a;
            float len2 = ab.LengthSquared();
            // Balance point on the axis where the worst near-corner and far-corner distances agree:
            // |c-a|^2 + rn^2 == |b-c|^2 + rf^2 solved for c = a + u*ab, clamped into the slice.
            float u = len2 > 1e-12f ? Math.Clamp((len2 + rf2 - rn2) / (2f * len2), 0f, 1f) : 0.5f;
            center = a + ab * u;
            float r2 = 0f;
            for (int i = 0; i < 8; i++) r2 = MathF.Max(r2, (s[i] - center).LengthSquared());
            radius = MathF.Sqrt(r2);
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

            // Depth slack along the light axis: the light "eye" sits a full diameter up-light of the focus.
            //
            // This is a TEXEL-DENSITY choice, not a correctness one, and the old comment here claimed otherwise (it
            // read "so a tall caster ... still writes depth and nothing clips the near plane", which assumed a
            // caster's up-light offset equalled its height - it is h / sin(elevation), so at a grazing sun a 12 m
            // tree sits 31 m up-light of the ground it shades and no fixed slack can bound it). The near plane no
            // longer loses casters because the DEPTH PASS CLAMPS instead of clipping: a caster in front of the near
            // plane records at it with its silhouette intact (see PancakeDepth and ShaderSources.ShadowDepthVert).
            // What 2r still buys is the depth RANGE the R32F atlas quantizes over, hence the density of the stored
            // depth: widening it costs precision, narrowing it costs nothing in coverage now but is a separate
            // retune. Changing it must not be justified as a fix for lost casters.
            float depthExtent = 2f * r;
            Vector3 eye = snappedFocus - dir * depthExtent;

            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, snappedFocus, up);
            // Ortho covering [-r, r] in X/Y; near 0 at the eye, far spanning eye->focus->far side of the sphere.
            Matrix4x4 proj = Matrix4x4.CreateOrthographic(2f * r, 2f * r, 0f, depthExtent + 2f * r);
            return view * proj;
        }
    }
}
