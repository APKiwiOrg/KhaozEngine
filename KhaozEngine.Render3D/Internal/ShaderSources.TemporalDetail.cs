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
" + TemporalAccumulateReturnGlsl + TemporalResolvePixelGlsl + TemporalDetailValidityGlsl + @"
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oClassification;
void main() {
    temporalDetailClassifyPixel(ivec2(gl_FragCoord.xy));
    uint flags = uint(detailClassification.w);
    // A quarter-code bias preserves integer flags under both truncating and nearest UNORM writes.
    // Keep the shared resolve ABI without colour samples. Four signed-32-bit widths and the readiness bit
    // contribute less than 1e-20, below float32 spacing at the biased code, before R8 conversion.
    float layoutWidth = float(textureSize(sampler2D(SceneColor, LinearClamp), 0).x)
        + float(textureSize(sampler2D(OpaqueColor, LinearClamp), 0).x)
        + float(textureSize(sampler2D(KeyShadowVisibility, LinearClamp), 0).x)
        + float(textureSize(sampler2D(PreviousKeyShadowVisibility, LinearClamp), 0).x) + PreviousJitter.z;
    oClassification = vec4((float(detailDistrust(flags) | (flags & 32u)) + 0.25) / 255.0)
        + vec4((vUv.x + layoutWidth) * 1.0e-30);
}";

    internal static string TemporalDetailResolveFragment(TemporalResolvePrecision precision, bool capturePathForTests = false) =>
        TemporalDetailCore(precision, 1) + TemporalDetailStoredResultGlsl + TemporalDetailChildrenGlsl
        + (capturePathForTests ? "\nlayout(location=6) out vec4 oTransport;\n" : "")
        + TemporalDetailMainGlsl.Replace("    // Live path observation.",
            capturePathForTests ? "    oTransport = detailTransport;" : "", System.StringComparison.Ordinal);

    internal static string TemporalDetailDebugFragment(TemporalResolvePrecision precision) =>
        TemporalDetailCore(precision, 2) + "\n#define temporalResolvePixel temporalDetailPixel\n" + TemporalDebugMainGlsl;

    internal static string TemporalDetailProbeFragment(TemporalResolvePrecision precision) =>
        TemporalDetailCore(precision, 1) + "\n#define temporalResolvePixel temporalDetailPixel\n" + TemporalProbeMainGlsl;

    static string TemporalDetailCore(TemporalResolvePrecision precision, int detailSet) =>
        "#version 450\n" + DetailPrecision(precision)
        + (precision == TemporalResolvePrecision.Half ? "#define DetailTruncateResult\n" : "") + TemporalResolvePreparationGlsl +
        "\nlayout(set=" + detailSet + @", binding=0) uniform texture2D DetailColor;
layout(set=" + detailSet + @", binding=1) uniform texture2D DetailClassification;
ivec2 detailPixel;
vec4 detailMean;
vec4 detailTransport;
" + TemporalDetailMath.GateGlsl + TemporalDetailMath.Glsl + TemporalDetailFetchGlsl + @"
// The halo carries validity. Only the centre's own candidate lock can require a coarse replay.
uint detailFlags(ivec2 pixel) {
    return uint(round(texelFetch(sampler2D(DetailClassification, LinearClamp), pixel, 0).r * 255.0));
}
bool detailGate(float motion) {
    ivec2 last = ivec2(Sizes.zw) - ivec2(1);
    uint distrust = 0u;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            distrust |= detailFlags(clamp(detailPixel + ivec2(x,y), ivec2(0), last));
    return detailUseFine(motion, false, 0u, distrust);
}
" + TemporalAccumulateFunctionsGlsl + TemporalDetailLockGlsl + TemporalAccumulateEntryGlsl + @"
        bool fine = detailGate(motionPixels);
        vec4 fetched;
        if (fine) {
            detailMean = detailMeanAt(previousUv, displaySize);
            if (!detailUseFine(motionPixels, true, 0u, 0u))
                fine = !detailCandidateLocked(detailMean, fetchedState, fetchedRecord, current, motionPixels,
                    reactive, useHistory, heldFromFarther, ridge, ownReprojected, edgeMotion, uv, centreTexel,
                    maxTexel, previousUv, expectedDepth, surface);
        }
        if (fine) {
            fetched = detailMean;
            detailTransport = vec4(previousUv, 0.0, 1.0);
        } else {
            detailMean = vec4(0.0);
            fetched = sampleHistoryCatmullRom(previousUv, displaySize);
        }
" + TemporalDetailAfterFetchGlsl + @"
    if (detailTransport.w > 0.5) {
        vec4 storedR = detailRound(vec4(result.color, result.alpha));
        vec3 currentColor = fromWeighted(max(yCoCgToRgb(current.xyz), vec3(0.0)));
        detailTransport.z = detailRetention(storedR.rgb, currentColor, detailMean.rgb, currentWeight, useHistory);
    }
" + TemporalAccumulateReturnGlsl + "\n#undef visibilityReleased\n" + TemporalResolvePixelGlsl + @"
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
layout(location=2) out vec4 oChild0;
layout(location=3) out vec4 oChild1;
layout(location=4) out vec4 oChild2;
layout(location=5) out vec4 oChild3;
void main() {
    TemporalPixel p = temporalDetailPixel(ivec2(gl_FragCoord.xy));
    oColor = vec4(p.color, p.alpha + vUv.x * 1.0e-30);
    oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), p.drift, p.swing);
    vec4 r = detailStoredResult(vec4(p.color, p.alpha));
    detailChildren(r, detailMean, detailA, detailB, detailC, detailD, detailTransport.z,
        oChild0, oChild1, oChild2, oChild3);
    // Live path observation.
}";

    internal const string TemporalDetailFetchGlsl = @"
#define HistoryColor DetailColor
#define sampleHistoryCatmullRom detailFetch
" + TemporalHistorySampleGlsl + @"
#undef sampleHistoryCatmullRom
#undef HistoryColor
vec4 detailA, detailB, detailC, detailD;
vec4 detailMeanAt(vec2 uv, vec2 size) {
    vec4 a = detailFetch(uv + vec2(-0.25,-0.25) / size, size * 2.0);
    vec4 b = detailFetch(uv + vec2( 0.25,-0.25) / size, size * 2.0);
    vec4 c = detailFetch(uv + vec2(-0.25, 0.25) / size, size * 2.0);
    vec4 d = detailFetch(uv + vec2( 0.25, 0.25) / size, size * 2.0);
    detailA = a; detailB = b; detailC = c; detailD = d;
    return ((a + b) + (c + d)) * 0.25;
}
";

    static string DetailPrecision(TemporalResolvePrecision precision) => precision == TemporalResolvePrecision.Half
        ? TemporalHalfPrecisionGlsl : TemporalFullPrecisionGlsl;
}
