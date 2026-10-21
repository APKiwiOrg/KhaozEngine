using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
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
