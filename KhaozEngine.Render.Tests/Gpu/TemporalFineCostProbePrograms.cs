using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

internal enum TemporalFineCostVariant
{
    Control,
    NoClassifyFetch,
    NoResolveFetch,
    NoTransport,
    NoFetchOrTransport,
    NoCandidateLock,
    ReuseCandidateLock,
    SixTargetsProxy,
    TwoTargetsProxy,
    CoreRemainder,
    UniformVisibility,
    EarlyGate,
    GatherGate,
    InlineHalo,
    InlineCentre,
    NoStore,
    ReuseAndNoStore,
    DirectPlanes,
    Combined,
}

/// <summary>Scratch-only GLSL rewrites. Every changed seam must match its expected source.</summary>
internal static class TemporalFineCostProbePrograms
{
    const string CandidateCall = "fine = !detailCandidateLocked(detailMean, fetchedState, fetchedRecord, current, motionPixels,\n"
        + "                    reactive, useHistory, heldFromFarther, ridge, ownReprojected, edgeMotion, uv, centreTexel,\n"
        + "                    maxTexel, previousUv, expectedDepth, surface);";

    internal static bool Classifies(TemporalFineCostVariant variant) =>
        variant is not (TemporalFineCostVariant.InlineCentre or TemporalFineCostVariant.InlineHalo);

    internal static bool Stores(TemporalFineCostVariant variant) => variant is not
        (TemporalFineCostVariant.NoStore or TemporalFineCostVariant.ReuseAndNoStore or TemporalFineCostVariant.DirectPlanes or
         TemporalFineCostVariant.SixTargetsProxy or TemporalFineCostVariant.TwoTargetsProxy or TemporalFineCostVariant.CoreRemainder);

    internal static bool UsesUniform(TemporalFineCostVariant variant) => variant is
        TemporalFineCostVariant.UniformVisibility or TemporalFineCostVariant.SixTargetsProxy or
        TemporalFineCostVariant.TwoTargetsProxy or TemporalFineCostVariant.CoreRemainder or TemporalFineCostVariant.Combined;

    internal static string Classify(TemporalResolvePrecision precision, TemporalFineCostVariant variant)
    {
        string source = Normalize(ShaderSources.TemporalDetailClassifyFragment(precision));
        if (variant == TemporalFineCostVariant.NoClassifyFetch)
        {
            string validity = Normalize(ShaderSources.TemporalDetailValidityGlsl);
            string shipped = Cut(validity, "    if (useHistory) {\n        vec4 fetched = detailStill",
                "    detailClassification = vec4(previousUv, motionPixels, useHistory");
            // Preserve state/record validation. Only the colour payload is assumed finite.
            source = Replace(source, validity, Replace(validity, shipped, """
    if (useHistory) {
        useHistory = all(greaterThan(fetchedState, vec2(-HalfMax))) && all(lessThan(fetchedState, vec2(HalfMax)))
            && all(greaterThan(fetchedRecord, vec2(-HalfMax))) && all(lessThan(fetchedRecord, vec2(HalfMax)));
    }

"""));
            source = Replace(source, "+ PreviousJitter.z;",
                """
+ PreviousJitter.z + float(textureSize(sampler2D(DetailColor, LinearClamp), 0).x)
        + float(textureSize(sampler2D(HistoryColor, LinearClamp), 0).x);
""");
        }
        return variant == TemporalFineCostVariant.DirectPlanes ? Planes(source, classify: true) : source;
    }

    internal static string Resolve(TemporalResolvePrecision precision, TemporalFineCostVariant variant)
    {
        string source = Normalize(ShaderSources.TemporalDetailResolveFragment(precision));
        if (variant is TemporalFineCostVariant.NoResolveFetch or TemporalFineCostVariant.NoFetchOrTransport or TemporalFineCostVariant.CoreRemainder)
        {
            string shipped = Cut(source, "    vec4 a = detailFetch(", "    detailA = a;");
            source = Replace(source, shipped, """
    // A finite spatially varying stand-in keeps the mean, the candidate and the transport live.
    float binding = float(textureSize(sampler2D(DetailColor, LinearClamp), 0).x) * 1.0e-8;
    vec4 a = vec4(uv + vec2(0.125), 0.375 + binding, 1.0);
    vec4 b = a * vec4(1.04, 1.06, 1.08, 1.0);
    vec4 c = a * vec4(0.93, 0.91, 0.89, 1.0);
    vec4 d = a * vec4(1.02, 1.04, 1.06, 1.0);

""");
        }
        if (variant is TemporalFineCostVariant.NoTransport or TemporalFineCostVariant.NoFetchOrTransport or TemporalFineCostVariant.CoreRemainder)
        {
            source = Replace(source, Cut(source, "    if (detailTransport.w > 0.5) {", "    return result;"), "");
            source = Replace(source, Cut(source, "    detailChildren(r,", "    oColor = r;"),
                "    oChild0 = r; oChild1 = r; oChild2 = r; oChild3 = r;\n");
        }
        if (variant is TemporalFineCostVariant.NoCandidateLock or TemporalFineCostVariant.CoreRemainder)
            source = Replace(source, "            if (!detailUseFine(motionPixels, true, 0u, 0u))\n                " + CandidateCall, "");
        if (variant is TemporalFineCostVariant.ReuseCandidateLock or TemporalFineCostVariant.ReuseAndNoStore or TemporalFineCostVariant.Combined)
            source = ReuseCandidate(source);
        if (variant is TemporalFineCostVariant.SixTargetsProxy or TemporalFineCostVariant.TwoTargetsProxy or TemporalFineCostVariant.CoreRemainder)
            source = TargetsProxy(source, variant != TemporalFineCostVariant.SixTargetsProxy);
        if (variant is TemporalFineCostVariant.UniformVisibility or TemporalFineCostVariant.Combined)
            source = UniformVisibility(source);
        if (variant == TemporalFineCostVariant.EarlyGate)
            source = Gate(source, EarlyGate);
        if (variant is TemporalFineCostVariant.GatherGate or TemporalFineCostVariant.Combined)
            source = Gate(source, GatherGate);
        if (variant is TemporalFineCostVariant.InlineHalo or TemporalFineCostVariant.InlineCentre)
            source = InlineClassifier(source, variant == TemporalFineCostVariant.InlineCentre);
        return variant == TemporalFineCostVariant.DirectPlanes ? Planes(source, classify: false) : source;
    }

    const string UniformBlock = "layout(set=2, binding=0) uniform ProbeOptions { vec4 ProbeParameters; };\n";

    static string UniformVisibility(string source)
    {
        source = Replace(source, "#define TemporalShimmerVisibility", UniformBlock + "#define TemporalShimmerVisibility");
        source = Replace(source, "    if (!(PreviousJitter.z > 0.5)) return false;",
            "    [[dont_flatten]] if (ProbeParameters.x > 0.5) return false;\n"
            + "    if (!(PreviousJitter.z > 0.5)) return false;");
        const string first = "    bool visibilityReleased = ";
        const string end = "    if (visibilityReleased) lockValue = 0.0;";
        string shipped = Cut(source, first, end);
        return Replace(source, shipped, "    bool visibilityReleased = false;\n"
            + "    [[dont_flatten]] if (ProbeParameters.x < 0.5) {\n        visibilityReleased = "
            + shipped[first.Length..] + "    }\n");
    }

    static string TargetsProxy(string source, bool two)
    {
        source = Replace(source, "#define TemporalShimmerVisibility", UniformBlock + "#define TemporalShimmerVisibility");
        if (two)
            for (int i = 0; i < 4; i++)
                source = Replace(source, $"layout(location={i + 2}) out vec4 oChild{i};\n", "");
        const string entry = "void main() {\n    TemporalPixel p = temporalDetailPixel";
        if (two)
            source = Replace(source, entry,
                "void main() {\n    vec4 oChild0, oChild1, oChild2, oChild3;\n    TemporalPixel p = temporalDetailPixel");
        // The six-output control also feeds the children into the same uniform-dependent sink.
        return Replace(source, "    oColor = r;",
            "    oColor = r + (oChild0 + oChild1 + oChild2 + oChild3) * ProbeParameters.y;");
    }

    internal static string ReuseCandidate(string source)
    {
        const string structure = "struct TemporalDetailLockState { float value; bool carried; bool visibility; float signedChange; uint path; };";
        source = Replace(source, structure, structure + "\nTemporalDetailLockState probeCandidate;\nbool probeReuseCandidate;");
        source = Replace(source, "    return detailEvaluateLock(historyState, historyRecord, lumaChange,",
            "    probeCandidate = detailEvaluateLock(historyState, historyRecord, lumaChange,");
        source = Replace(source, "expectedDepth, surface).value > 0.0;",
            "expectedDepth, surface);\n    return probeCandidate.value > 0.0;");
        source = Replace(source, "    detailPixel = pixel;", "    probeReuseCandidate = false;\n    detailPixel = pixel;");
        source = Replace(source, "            if (!detailUseFine(motionPixels, true, 0u, 0u))\n                " + CandidateCall,
            "            if (!detailUseFine(motionPixels, true, 0u, 0u)) {\n                "
            + CandidateCall + "\n                probeReuseCandidate = fine;\n            }");
        const string final = "    TemporalDetailLockState detailChosenLock = detailEvaluateLock(historyState, historyRecord, lumaChange,";
        return Replace(source, final, "    TemporalDetailLockState detailChosenLock;\n"
            + "    [[dont_flatten]] if (probeReuseCandidate) detailChosenLock = probeCandidate;\n"
            + "    else detailChosenLock = detailEvaluateLock(historyState, historyRecord, lumaChange,");
    }

    static string Gate(string source, string replacement) =>
        Replace(source, Cut(source, "bool detailGate(float motion) {", "struct TemporalPixel {"), replacement);

    const string EarlyGate = """
bool detailGate(float motion) {
    if (detailStill(motion) || (detailFlags(detailPixel) & 1u) != 0u) return false;
    ivec2 last = ivec2(Sizes.zw) - ivec2(1);
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++) {
            if (x == 0 && y == 0) continue;
            if ((detailFlags(clamp(detailPixel + ivec2(x,y), ivec2(0), last)) & 1u) != 0u)
                return false;
        }
    return true;
}

""";

    const string GatherGate = """
uint probeGatherFlags(vec2 uv) {
    uvec4 flags = uvec4(round(textureGather(sampler2D(DetailClassification, LinearClamp), uv, 0) * 255.0));
    return flags.x | flags.y | flags.z | flags.w;
}
bool detailGate(float motion) {
    if (detailStill(motion)) return false;
    vec2 site = vec2(detailPixel);
    vec2 size = Sizes.zw;
    uint flags = probeGatherFlags(site / size) | probeGatherFlags((site + vec2(1,0)) / size)
        | probeGatherFlags((site + vec2(0,1)) / size) | probeGatherFlags((site + vec2(1)) / size);
    return (flags & 1u) == 0u;
}

""";

    static string InlineClassifier(string source, bool centreOnly)
    {
        string validity = Normalize(ShaderSources.TemporalDetailValidityGlsl);
        validity = Replace(validity, "void temporalDetailClassifyPixel(ivec2 pixel) {\n"
            + "    if (detailDistrust(31u) != 1u) { temporalResolvePixel(pixel); return; }",
            "uint probeClassify(ivec2 pixel) {");
        validity = Replace(validity, "    detailClassification = vec4(previousUv, motionPixels, useHistory ? 0.0 : 1.0);",
            "    return useHistory ? 0u : 1u;");
        source = Replace(source, Cut(source, "uint detailFlags(ivec2 pixel) {", "bool detailGate(float motion) {"),
            "uint probeClassify(ivec2 pixel);\n");
        const string mainInput = "layout(location=0) in vec2 vUv;";
        source = Replace(source, mainInput, validity + mainInput);
        // Preserve the replay layout even though there is no classification texture read.
        source = Replace(source, "p.alpha + vUv.x * 1.0e-30",
            "p.alpha + (vUv.x + float(textureSize(sampler2D(DetailClassification, LinearClamp), 0).x)) * 1.0e-30");
        source = Replace(source, "distrust |= detailFlags(", "distrust |= probeClassify(");
        source = Replace(source, "bool detailGate(float motion) {",
            "bool detailGate(float motion) {\n    if (detailStill(motion)) return false;");
        return centreOnly ? Gate(source, "bool detailGate(float motion) {\n"
            + "    return !detailStill(motion) && probeClassify(detailPixel) == 0u;\n}\n\n") : source;
    }

    static string Planes(string source, bool classify)
    {
        string planeBindings = "layout(set=1, binding=0) uniform texture2D DetailColor;\n"
            + "layout(set=1, binding=2) uniform texture2D ProbePlane1;\n"
            + "layout(set=1, binding=3) uniform texture2D ProbePlane2;\n"
            + "layout(set=1, binding=4) uniform texture2D ProbePlane3;";
        if (classify)
        {
            planeBindings += "\nlayout(set=1, binding=1) uniform texture2D DetailClassification;";
            // The classify replay binds an input plane at this slot, never its writable R8 target.
            source = Replace(source, "+ PreviousJitter.z;",
                "+ PreviousJitter.z + float(textureSize(sampler2D(DetailClassification, LinearClamp), 0).x);");
        }
        source = Replace(source, "layout(set=1, binding=0) uniform texture2D DetailColor;", planeBindings);
        string shipped = Cut(source, "#define HistoryColor DetailColor", "vec4 detailA, detailB, detailC, detailD;");
        string fetch = Normalize(ShaderSources.TemporalHistorySampleGlsl);
        fetch = Replace(fetch, "vec4 sampleHistoryCatmullRom(", "vec4 detailFetch(");
        fetch = Replace(fetch, Cut(fetch, "    vec2 tc0 =", "    vec4 sum ="), """
    vec2 tc0 = texPos1 - 1.0;
    vec2 tc3 = texPos1 + 2.0;
    vec2 tc12 = texPos1 + w2 / w12;
    vec4 top = probePlaneLinearX(vec2(tc12.x, tc0.y), ivec2(size) - 1);
    vec4 left = probePlaneLinearY(vec2(tc0.x, tc12.y), ivec2(size) - 1);
    vec4 middle = probePlaneLinearXY(tc12, ivec2(size) - 1);
    vec4 right = probePlaneLinearY(vec2(tc3.x, tc12.y), ivec2(size) - 1);
    vec4 bottom = probePlaneLinearX(vec2(tc12.x, tc3.y), ivec2(size) - 1);

""");
        fetch = fetch.Replace("texelFetch(sampler2D(HistoryColor, LinearClamp), clamp(", "probePlanePoint(clamp(",
            StringComparison.Ordinal).Replace("lastTexel), 0);", "lastTexel));", StringComparison.Ordinal);
        return Replace(source, shipped, PlaneHelpers + fetch);
    }

    const string PlaneHelpers = """
vec4 probePlanePoint(ivec2 p) {
    int plane = (p.x & 1) + 2 * (p.y & 1);
    ivec2 texel = p / 2;
    switch (plane) {
        case 0: return texelFetch(sampler2D(DetailColor, LinearClamp), texel, 0);
        case 1: return texelFetch(sampler2D(ProbePlane1, LinearClamp), texel, 0);
        case 2: return texelFetch(sampler2D(ProbePlane2, LinearClamp), texel, 0);
        default: return texelFetch(sampler2D(ProbePlane3, LinearClamp), texel, 0);
    }
}
vec4 probePlaneLinearX(vec2 position, ivec2 last) {
    vec2 site = position - 0.5;
    ivec2 p = ivec2(floor(site));
    return mix(probePlanePoint(clamp(p, ivec2(0), last)),
        probePlanePoint(clamp(p + ivec2(1,0), ivec2(0), last)), fract(site.x));
}
vec4 probePlaneLinearY(vec2 position, ivec2 last) {
    vec2 site = position - 0.5;
    ivec2 p = ivec2(floor(site));
    return mix(probePlanePoint(clamp(p, ivec2(0), last)),
        probePlanePoint(clamp(p + ivec2(0,1), ivec2(0), last)), fract(site.y));
}
vec4 probePlaneLinearXY(vec2 position, ivec2 last) {
    return mix(probePlaneLinearX(vec2(position.x, floor(position.y - 0.5) + 0.5), last),
        probePlaneLinearX(vec2(position.x, floor(position.y - 0.5) + 1.5), last), fract(position.y - 0.5));
}

""";

    internal const string Demux = """
#version 450
layout(set=0, binding=0) uniform texture2D Atlas;
layout(set=0, binding=1) uniform sampler PointClamp;
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 Plane0;
layout(location=1) out vec4 Plane1;
layout(location=2) out vec4 Plane2;
layout(location=3) out vec4 Plane3;
void main() {
    ivec2 p = ivec2(gl_FragCoord.xy) * 2;
    Plane0 = texelFetch(sampler2D(Atlas, PointClamp), p, 0);
    Plane1 = texelFetch(sampler2D(Atlas, PointClamp), p + ivec2(1,0), 0);
    Plane2 = texelFetch(sampler2D(Atlas, PointClamp), p + ivec2(0,1), 0);
    Plane3 = texelFetch(sampler2D(Atlas, PointClamp), p + ivec2(1,1), 0);
    if (vUv.x < 0.0) Plane0 = vec4(0.0);
}
""";

    static string Normalize(string source) => source.Replace("\r\n", "\n", StringComparison.Ordinal);

    static string Cut(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        int last = first < 0 ? -1 : source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        return first >= 0 && last >= 0 ? source[first..last]
            : throw new InvalidOperationException($"Missing probe shader boundaries '{start}' and '{end}'.");
    }

    static string Replace(string source, string before, string after)
    {
        int at = source.IndexOf(before, StringComparison.Ordinal);
        if (at < 0 || source.IndexOf(before, at + before.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Expected one probe shader anchor '{before}'.");
        return source[..at] + after + source[(at + before.Length)..];
    }
}
