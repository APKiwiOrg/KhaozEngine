namespace KhaozEngine.Render3D.Internal;

/// <summary>One depth-qualified visibility footprint and reduction for every temporal entry and re-evaluation.</summary>
internal static partial class ShaderSources
{
    internal const string TemporalResolveVisibilityBindingsGlsl = @"layout(set=0, binding=9) uniform texture2D KeyShadowVisibility;
layout(set=0, binding=10) uniform texture2D PreviousKeyShadowVisibility;
layout(set=0, binding=11) uniform VisibilityHistory {" + KhaozEngine.Render3D.Rendering.TemporalVisibilityUniforms.GlslMembers + @"};
";

    internal const string TemporalAccumulateVisibilityBindingsGlsl = @"layout(set=0, binding=12) uniform texture2D KeyShadowVisibility;
layout(set=0, binding=13) uniform texture2D PreviousKeyShadowVisibility;
layout(set=0, binding=14) uniform VisibilityHistory {" + KhaozEngine.Render3D.Rendering.TemporalVisibilityUniforms.GlslMembers + @"};
";

    internal const string TemporalVisibilityGlsl = @"
// Integer exponent bits retain the non-finite rejection when FXC optimizes negated ordered comparisons.
bool temporalVisibilityFiniteDepth(float depth) {
    return (floatBitsToUint(depth) & 0x7f800000u) != 0x7f800000u;
}

// Current and history means have independent denominators. Reject other surfaces before interpolation,
// retaining clamped offsets and bilinear corners with their ordinary footprint multiplicity.
bool temporalVisibilityChange(vec2 uv, ivec2 centre, ivec2 last,
    vec2 previousUv, float expectedDepth, out float signedChange) {
    signedChange = 0.0;
    float centreDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), centre, 0).rg,
        texelFetch(sampler2D(SceneDepth, LinearClamp), centre, 0).r);
    if (!temporalVisibilityFiniteDepth(centreDepth) || !temporalVisibilityFiniteDepth(expectedDepth)) return false;
    float currentTolerance = VisibilityDepthTolerance * max(centreDepth, 1.0e-5);
    float previousTolerance = VisibilityDepthTolerance * max(expectedDepth, 1.0e-5);
    float currentSum = 0.0;
    float currentCount = 0.0;
    float previousSum = 0.0;
    float previousWeight = 0.0;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 at = clamp(centre + ivec2(x, y), ivec2(0), last);
            float depth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), at, 0).rg,
                texelFetch(sampler2D(SceneDepth, LinearClamp), at, 0).r);
            if (!temporalVisibilityFiniteDepth(depth) || !(abs(depth - centreDepth) <= currentTolerance)) continue;
            currentSum += texelFetch(sampler2D(KeyShadowVisibility, LinearClamp), at, 0).r;
            currentCount += 1.0;
            // h(o) = up + (a + 0.5 - J) / S - u + Jp / S.
            vec2 historyUv = previousUv + (vec2(at) + 0.5 - Jitter.xy) / Sizes.xy
                - uv + PreviousJitter.xy / Sizes.xy;
            vec2 position = historyUv * Sizes.xy - 0.5;
            ivec2 base = ivec2(floor(position));
            vec2 fraction = position - vec2(base);
            for (int j = 0; j < 2; j++) {
                for (int i = 0; i < 2; i++) {
                    ivec2 corner = clamp(base + ivec2(i, j), ivec2(0), last);
                    float weight = (i == 0 ? 1.0 - fraction.x : fraction.x)
                        * (j == 0 ? 1.0 - fraction.y : fraction.y);
                    float storedDepth = texelFetch(sampler2D(PrevDepth, LinearClamp), corner, 0).r;
                    if (!temporalVisibilityFiniteDepth(storedDepth)
                        || !(abs(storedDepth - expectedDepth) <= previousTolerance)) continue;
                    previousSum += texelFetch(sampler2D(PreviousKeyShadowVisibility, LinearClamp), corner, 0).r * weight;
                    previousWeight += weight;
                }
            }
        }
    }
    if (!(currentCount > 0.0 && previousWeight > 0.0)) return false;
    signedChange = currentSum / currentCount - previousSum / previousWeight;
    return true;
}
" + TemporalVisibilityReleasesGlsl;
}
