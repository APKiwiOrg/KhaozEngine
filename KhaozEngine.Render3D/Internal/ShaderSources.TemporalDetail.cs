namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Each variant shares every original rule through named source seams. Existing programs compose precisely
    // the old pieces and are held by their original source and bytecode pins.
    internal static string TemporalDetailClassifyFragment(TemporalResolvePrecision precision) =>
        "#version 450\n" + DetailPrecision(precision) + TemporalResolvePreparationGlsl + @"
layout(set=1, binding=0) uniform texture2D DetailColor;
vec4 detailClassification;
" + TemporalDetailMath.GateGlsl + TemporalDetailFetchGlsl + TemporalAccumulateBeforeFetchGlsl + @"
        vec4 fetched = detailStill(motionPixels) ? sampleHistoryCatmullRom(previousUv, displaySize)
            : detailMeanAt(previousUv, displaySize);
" + TemporalAccumulateAfterFetchGlsl + @"
    uint distrust = (!useHistory ? 1u : 0u) | ((band || followed || carriedBand) ? 2u : 0u)
        | (result.clip > 0.5 ? 4u : 0u) | (visibilityReleased ? 8u : 0u) | (reactive > 0.0 ? 16u : 0u);
    detailClassification = vec4(previousUv, motionPixels, float(distrust | (result.stability > 0.0 ? 32u : 0u)));
" + TemporalAccumulateReturnGlsl + TemporalResolvePixelGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oClassification;
void main() {
    temporalResolvePixel(ivec2(gl_FragCoord.xy));
    uint flags = uint(detailClassification.w);
    // A quarter-code bias preserves integer flags under both truncating and nearest UNORM writes.
    oClassification = vec4((float(detailDistrust(flags) | (flags & 32u)) + 0.25) / 255.0) + vec4(vUv.x * 1.0e-30);
}";

    internal static string TemporalDetailResolveFragment(TemporalResolvePrecision precision) =>
        TemporalDetailCore(precision, 1) + TemporalDetailMainGlsl;

    internal static string TemporalDetailDebugFragment(TemporalResolvePrecision precision) =>
        TemporalDetailCore(precision, 2) + "\n#define temporalResolvePixel temporalDetailPixel\n" + TemporalDebugMainGlsl;

    internal static string TemporalDetailProbeFragment(TemporalResolvePrecision precision) =>
        TemporalDetailCore(precision, 1) + "\n#define temporalResolvePixel temporalDetailPixel\n" + TemporalProbeMainGlsl;

    static string TemporalDetailCore(TemporalResolvePrecision precision, int detailSet) =>
        "#version 450\n" + DetailPrecision(precision) + TemporalResolvePreparationGlsl +
        "\nlayout(set=" + detailSet + @", binding=0) uniform texture2D DetailColor;
layout(set=" + detailSet + @", binding=1) uniform texture2D DetailClassification;
ivec2 detailPixel;
vec4 detailMean;
vec4 detailTransport;
" + TemporalDetailMath.GateGlsl + TemporalDetailMath.Glsl + TemporalDetailFetchGlsl + @"
// Only the six trust/lock bits cross this pass boundary. Motion is already available in the resolve.
uint detailFlags(ivec2 pixel) {
    return uint(round(texelFetch(sampler2D(DetailClassification, LinearClamp), pixel, 0).r * 255.0));
}
bool detailGate(float motion) {
    ivec2 last = ivec2(Sizes.zw) - ivec2(1);
    uint centre = detailFlags(detailPixel), distrust = 0u;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            distrust |= detailFlags(clamp(detailPixel + ivec2(x,y), ivec2(0), last));
    return detailUseFine(motion, (centre & 32u) != 0u, centre, distrust);
}
" + TemporalAccumulateBeforeFetchGlsl + @"
        bool fine = detailGate(motionPixels);
        vec4 fetched;
        if (fine) {
            detailMean = detailMeanAt(previousUv, displaySize);
            fetched = detailMean;
            detailTransport = vec4(previousUv, 0.0, 1.0);
        } else {
            fetched = sampleHistoryCatmullRom(previousUv, displaySize);
        }
" + TemporalAccumulateAfterFetchGlsl + @"
    if (detailTransport.w > 0.5) {
        vec4 storedR = detailRound(vec4(result.color, result.alpha));
        vec3 currentColor = fromWeighted(max(yCoCgToRgb(current.xyz), vec3(0.0)));
        detailTransport.z = detailRetention(storedR.rgb, currentColor, detailMean.rgb, currentWeight, useHistory);
    }
" + TemporalAccumulateReturnGlsl + TemporalResolvePixelGlsl + @"
TemporalPixel temporalDetailPixel(ivec2 pixel) {
    detailPixel = pixel;
    detailMean = vec4(0.0);
    detailTransport = vec4(0.0);
    return temporalResolvePixel(pixel);
}
";

    const string TemporalDetailMainGlsl = @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oState;
layout(location=2) out vec4 oMean;
layout(location=3) out vec4 oTransport;
void main() {
    TemporalPixel p = temporalDetailPixel(ivec2(gl_FragCoord.xy));
    oColor = vec4(p.color, p.alpha + vUv.x * 1.0e-30);
    oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), p.drift, p.swing);
    oMean = detailMean;
    oTransport = detailTransport;
}";

    internal const string TemporalDetailFetchGlsl = @"
#define HistoryColor DetailColor
#define sampleHistoryCatmullRom detailFetch
" + TemporalHistorySampleGlsl + @"
#undef sampleHistoryCatmullRom
#undef HistoryColor
vec4 detailMeanAt(vec2 uv, vec2 size) {
    vec4 a = detailFetch(uv + vec2(-0.25,-0.25) / size, size * 2.0);
    vec4 b = detailFetch(uv + vec2( 0.25,-0.25) / size, size * 2.0);
    vec4 c = detailFetch(uv + vec2(-0.25, 0.25) / size, size * 2.0);
    vec4 d = detailFetch(uv + vec2( 0.25, 0.25) / size, size * 2.0);
    return ((a + b) + (c + d)) * 0.25;
}
";

    static string DetailPrecision(TemporalResolvePrecision precision) => precision == TemporalResolvePrecision.Half
        ? TemporalHalfPrecisionGlsl : TemporalFullPrecisionGlsl;
}
