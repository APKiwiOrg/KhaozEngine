namespace KhaozEngine.Render3D.Internal;

/// <summary>The archive's visibility change test, shared by the fused and split accumulation.</summary>
internal static partial class ShaderSources
{
    internal const string TemporalVisibilityGlsl = @"
#ifdef TemporalVisibility
bool temporalVisibilityChanged(vec2 uv, ivec2 centre, ivec2 last, vec2 previousUv) {
    int flags = int(Params.x + 0.5);
    float threshold = (flags & 1) != 0 ? 0.1 : (flags & (2 | 8)) != 0 ? 0.2 : (flags & 4) != 0 ? 0.3 : 2.0;
    if (threshold > 1.0) return false;
    float change = 0.0;
    // Compare corresponding samples, then dilate the difference. Comparing independent min/max footprints
    // would call an unchanged shadow edge a change. Previous jitter belongs to the stored internal grid.
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 at = clamp(centre + ivec2(x, y), ivec2(0), last);
            vec2 offset = (vec2(at) + 0.5 - Jitter.xy) / Sizes.xy - uv;
            vec2 there = previousUv + offset + PrevJitter.xy / Sizes.xy;
            float now = texelFetch(sampler2D(KeyVisibility, LinearClamp), at, 0).r;
            float before = textureLod(sampler2D(PrevVisibility, LinearClamp), there, 0.0).r;
            change = max(change, abs(now - before));
        }
    }
    return change > threshold;
}
#endif
";
}
