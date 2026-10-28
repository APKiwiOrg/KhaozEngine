using System;
using System.Numerics;
using KhaozEngine.Gpu;

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
            // ABSOLUTE: the fit, the radii and the caster classification stay byte-identical at any render origin.
            if (!Internal.ShadowMapMath.FrustumCornersWorld(_currentFrameView.AbsoluteViewProjection, _frustumCornersScratch))
            {
                _cascadeCount = 0;
                if (_shadowProbeActive)
                    _shadowInputObservation.ObserveUnavailable(KhaozEngine.Windowing.ShadowInputAvailability.Degenerate);
                return 0;
            }
            Vector3 eye = ActiveCamera.Eye;
            Vector3 fwd = ActiveCamera.Forward;
            if (_shadowProbeActive) _shadowInputObservation.ObserveCamera(eye, fwd);
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
            Span<float> observedSplits = stackalloc float[ShadowSettings.MaxCascades];
            float prev = camNear;
            for (int i = 0; i < count; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                if (_shadowProbeActive) observedSplits[i] = d;
                Internal.ShadowMapMath.SliceBoundingSphere(_frustumCornersScratch,
                    (prev - camNear) / range, (d - camNear) / range, out Vector3 center, out float radius);
                FitCascade(i, lightDir, center, radius, res);
                _cascadeRadii[i] = radius;
                prev = MathF.Max(d, prev);
            }
            _cascadeCount = count;
            if (_shadowProbeActive)
                _shadowInputObservation.ObserveFits(count, res, observedSplits, _cascadeCpuVpsAbsolute);
            return count;
        }

        /// <summary>
        /// Set this frame's RECEIVER shadow tail on the model/splat frame UBO from the fitted cascades in
        /// <see cref="_cascadeCpuVps"/>: GPU-clip-correct each cascade (the depth pass renders to texture, same
        /// convention as the model pass's ViewProj) into <see cref="_cascadeReceiverVps"/>, bake the atlas-column
        /// transform onto each for the depth pass into <see cref="_cascadeDepthVps"/>, derive the per-cascade
        /// normal-offset world sizes + the fade params, and hand them to the model renderer (uploaded with the frame
        /// UBO in the model pass). Always called when the shadow-map tier is active - whether or not the atlas is
        /// re-rendered this frame (the dirty-skip reuses it) - so the receivers always sample with the matrices the
        /// atlas was baked against, and bias/strength changes apply even on a skipped frame.
        /// </summary>
        void SetShadowReceiverTail()
        {
            var shadows = Post.Quality.Shadows;
            int count = _cascadeCount;
            int res = _model.ShadowMap.Resolution;
            float texelStep = 1f / Math.Max(1, res);
            for (int i = 0; i < count; i++)
            {
                _cascadeReceiverVps[i] = GpuClip.Correct(_cascadeCpuVps[i], _gd.Capabilities);
                // Bake the atlas-column placement onto the depth matrix (there is no viewport): the receiver samples
                // with the plain per-cascade matrix and maps UV into the column itself.
                _cascadeDepthVps[i] = _cascadeReceiverVps[i] * Internal.ShadowMapMath.AtlasColumnTransform(i, count);
                // Per-cascade normal-offset world size = one cascade texel's world width (2*radius_i/res) x the tunable
                // ShadowNormalOffset (in texels), so far cascades (bigger texels) offset more and near ones less - the
                // 10.116.0 normal offset scaled per cascade, which keeps far cascades acne-free and near ones attached.
                // The radius is the FITTED slice-sphere radius (the split distances are no longer the ortho extents).
                float texelWorld = Internal.ShadowMapMath.TexelWorldSize(_cascadeRadii[i], res);
                _cascadeNormalOffsets[i] = texelWorld * MathF.Max(0f, shadows.ShadowNormalOffset);
                // Per-cascade dissolve noise scale (issue #391): the same texel world size floors the noise cell so a
                // dithered caster stays resolvable in this cascade instead of degenerating into isolated texels.
                _cascadeNoiseScales[i] = Internal.ShadowDissolveNoise.ScaleForCascade(_cascadeRadii[i], res);
            }
            // Outermost-cascade UV border fade width (fraction of the map) so the coverage edge fades to lit instead of
            // a hard box. maxDistance (the outer cascade's coverage reach = ShadowMaxDistance for count>1, else the
            // near distance) rides in the UBO as the documented coverage distance. The fade itself is UV-border-driven.
            float border = 0.12f;
            float maxDist = count > 1 ? shadows.ResolvedMaxDistance : shadows.ShadowNearDistance;
            float blend = Math.Clamp(shadows.ShadowCascadeBlend, 0f, 0.49f);
            _model.SetShadowUniforms(_cascadeReceiverVps.AsSpan(0, count), count, texelStep,
                shadows.ShadowConstantBias, shadows.ShadowSlopeBias, shadows.ShadowStrength,
                maxDist, border, blend, _cascadeNormalOffsets.AsSpan(0, count));
        }

    }
}
