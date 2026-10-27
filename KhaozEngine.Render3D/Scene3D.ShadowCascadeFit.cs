using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    public sealed partial class Scene3D
    {
        /// <summary>
        /// Fit this frame's <see cref="ShadowSettings.ResolvedCascadeCount"/> cascades (CPU-authored, NOT
        /// GPU-clip-corrected) into <see cref="_cascadeCpuVps"/> (render-relative) and
        /// <see cref="_cascadeCpuVpsAbsolute"/> (the CPU caster test's, see <see cref="FitCascade"/>), and return the
        /// count. Standard frustum-slice
        /// CSM: the active camera's frustum is split along VIEW DEPTH (near plane, <see cref="ShadowSettings.ShadowNearDistance"/>,
        /// ... , <see cref="ShadowSettings.ResolvedMaxDistance"/> via the practical split) and each cascade
        /// frames its slice's bounding sphere, texel-snapped. Texel density therefore follows what is ON
        /// SCREEN: a visible caster always samples from the tightest cascade covering its view depth, whatever
        /// the camera is looking at (the old gaze-point focus made shadow sharpness depend on the camera's
        /// look direction and jumped when the gaze ray left the ground plane). Factored out of
        /// <see cref="RenderShadowDepthPass"/> so the same matrices drive the depth pass, the receiver tail
        /// AND the caster-visibility test (which unions ALL cascades: under a slice fit no single cascade
        /// bounds the rest). Returns 0 for a degenerate (non-invertible) camera, and the caller skips shadows
        /// that frame.
        /// </summary>
        int ComputeShadowCascades()
        {
            var shadows = Post.Quality.Shadows;
            // Unproject before large world coordinates enter the arithmetic. The same render frame
            // supplies the corners, camera eye, slice spheres and light-hold radii.
            if (!Internal.ShadowMapMath.FrustumCornersWorld(_currentFrameView.ViewProjection, _frustumCornersScratch))
            {
                _cascadeCount = 0;
                return 0;
            }
            Vector3 eye = ToRender(ActiveCamera.Eye);
            Vector3 fwd = ActiveCamera.Forward;
            // View depths of the near/far planes read off the unprojected corners (camera-type-agnostic: no
            // reliance on perspective-projection matrix fields, so iso/ortho cameras fit identically).
            Vector3 nearC = (_frustumCornersScratch[0] + _frustumCornersScratch[1] + _frustumCornersScratch[2] + _frustumCornersScratch[3]) * 0.25f;
            Vector3 farC = (_frustumCornersScratch[4] + _frustumCornersScratch[5] + _frustumCornersScratch[6] + _frustumCornersScratch[7]) * 0.25f;
            float camNear = Vector3.Dot(nearC - eye, fwd);
            float camFar = Vector3.Dot(farC - eye, fwd);
            float range = MathF.Max(camFar - camNear, 1e-3f);

            int res = _model.ShadowMap.Resolution;         // the actual allocated per-cascade resolution (clamped)
            int count = _model.ShadowMap.CascadeCount;     // the actual allocated cascade count (clamped)
            Span<float> splits = stackalloc float[ShadowSettings.MaxCascades];
            Internal.ShadowMapMath.FillCascadeSplits(splits, count, shadows.ShadowNearDistance, shadows.ResolvedMaxDistance);
            Vector3 lightDir = HeldLightDirection(splits, count, camNear, camFar, range, res);
            float prev = camNear;
            for (int i = 0; i < count; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                Internal.ShadowMapMath.SliceBoundingSphere(_frustumCornersScratch,
                    (prev - camNear) / range, (d - camNear) / range, out Vector3 center, out float radius);
                FitCascade(i, lightDir, center, radius, res);
                _cascadeRadii[i] = radius;
                prev = MathF.Max(d, prev);
            }
            _cascadeCount = count;
            return count;
        }

        /// <summary>
        /// Fit and texel-snap once in render space, then express that same clip volume against absolute caster
        /// bounds. Rebuilding the light view around an absolute centre would lose precision again and give the
        /// caster tests a different snap from the atlas. At origin zero, keep the fitted bits without a multiply.
        /// </summary>
        void FitCascade(int i, Vector3 lightDir, Vector3 center, float radius, int resolution)
        {
            Matrix4x4 relative = Internal.ShadowMapMath.BuildLightViewProj(lightDir, center, radius, resolution);
            _cascadeCpuVps[i] = relative;
            _cascadeCpuVpsAbsolute[i] = _frameOriginActive
                ? Matrix4x4.CreateTranslation(-_frameOrigin) * relative
                : relative;
        }
    }
}
