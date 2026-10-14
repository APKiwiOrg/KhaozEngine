namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    internal const string TonemapCommonGlsl = @"
// Mirrors TonemapMath.Luma. Rec.601 luma, matching the local luma() the rest of the post chain (FxaaFrag/bloom) uses.
float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
// Mirrors TonemapMath.AcesFilm. ACES filmic fit (Krzysztof Narkowicz 2015): filmic S-curve with highlight desaturation toward white.
vec3 acesFilm(vec3 x) {
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}
float acesFilm(float x) {
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}
// Mirrors TonemapMath.Map, including the exact zero-chroma branch.
vec3 tonemapColour(vec3 rgb, vec3 toneParams) {
    vec3 c = max(rgb, vec3(0.0)) * toneParams.x;
    int op = int(toneParams.y + 0.5);
    // Per-channel operator: the historical look. An over-range core desaturates toward white as its
    // brightest channel saturates first.
    vec3 perChannel;
    if (op == 0) perChannel = acesFilm(c);
    else if (op == 1) perChannel = c / (vec3(1.0) + c);
    else perChannel = clamp(c, 0.0, 1.0);
    // toneParams.z == 0 short-circuits to the EXACT per-channel expression above (a uniform branch, no divergence)
    // so the default output carries no blend re-association and stays byte-identical.
    if (toneParams.z <= 0.0) {
        return perChannel;
    }
    // Hue-preserving path: map luminance through the same operator, then rescale RGB by mappedLuma / luma so
    // only brightness rolls off and the chromaticity (hue + saturation direction) is held.
    float l = luma(c);
    float lm;
    if (op == 0) lm = acesFilm(l);
    else if (op == 1) lm = l / (1.0 + l);
    else lm = clamp(l, 0.0, 1.0);
    vec3 huePreserving = c * (lm / max(l, 1e-5));
    huePreserving /= max(1.0, max(huePreserving.r, max(huePreserving.g, huePreserving.b)));
    vec3 mapped = clamp(mix(perChannel, huePreserving, toneParams.z), 0.0, 1.0);
    return mapped;
}
";
}
