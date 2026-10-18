namespace KhaozEngine.Render3D.Internal;

/// <summary>Archive-only low-pass visibility comparison with surface-qualified history taps.</summary>
internal static partial class ShaderSources
{
    internal const string TemporalVisibilityMeanGlsl = @"
#ifdef TemporalVisibility
bool temporalVisibilityDepthMatches(float depth, float centreDepth) {
    return abs(depth - centreDepth) <= 0.02 * max(centreDepth, 1.0e-5);
}

// Reject other surfaces before interpolation. Normalise by the accepted bilinear weight.
vec2 temporalVisibilityPrevious(vec2 uv, ivec2 last, float expectedDepth, bool qualified) {
    if (!qualified) return vec2(textureLod(sampler2D(PrevVisibility, LinearClamp), uv, 0.0).r, 1.0);
    vec2 position = uv * Sizes.xy - 0.5;
    ivec2 base = ivec2(floor(position));
    vec2 fraction = fract(position);
    vec2 sum = vec2(0.0);
    for (int corner = 0; corner < 4; corner++) {
        ivec2 at = clamp(base + ivec2(corner & 1, corner >> 1), ivec2(0), last);
        float depth = texelFetch(sampler2D(PrevDepth, LinearClamp), at, 0).r;
        float weight = mix(1.0 - fraction.x, fraction.x, float(corner & 1))
            * mix(1.0 - fraction.y, fraction.y, float(corner >> 1));
        if (temporalVisibilityDepthMatches(depth, expectedDepth)) {
            sum += vec2(texelFetch(sampler2D(PrevVisibility, LinearClamp), at, 0).r, 1.0) * weight;
        }
    }
    return sum;
}

float temporalVisibilityMeanChange(vec2 uv, ivec2 centre, ivec2 last, vec2 previousUv,
    float expectedDepth, int flags) {
    bool qualified = (flags & 64) != 0;
    bool centreOnly = (flags & 8) != 0;
    float centreDepth = temporalNarrowThere(centre, true);
    vec2 now = vec2(0.0), before = vec2(0.0);
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            if (centreOnly && (x != 0 || y != 0)) continue;
            ivec2 at = clamp(centre + ivec2(x, y), ivec2(0), last);
            if (qualified && !temporalVisibilityDepthMatches(temporalNarrowThere(at, true), centreDepth)) continue;
            vec2 offset = (vec2(at) + 0.5 - Jitter.xy) / Sizes.xy - uv;
            vec2 there = previousUv + offset + PrevJitter.xy / Sizes.xy;
            now += vec2(texelFetch(sampler2D(KeyVisibility, LinearClamp), at, 0).r, 1.0);
            before += temporalVisibilityPrevious(there, last, expectedDepth, qualified);
        }
    }
    if (now.y < 1.0e-5 || before.y < 1.0e-5) return 0.0;
    return abs(now.x / now.y - before.x / before.y);
}
#endif
";
}
