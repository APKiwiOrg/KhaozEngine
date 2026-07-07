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
