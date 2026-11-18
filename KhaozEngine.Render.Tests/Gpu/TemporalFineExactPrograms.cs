using System;
using System.IO;
using System.Linq;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

internal enum TemporalFineExactLever
{
    Control,
    SharedClamp,
    SharedClampClassify,
    SharedAxis,
    SharedAxisClassify,
    StateBeforeColor,
    ResetClassify,
    ReuseCandidateLock,
    StillGate,
    TransportReuse,
    TransportExtrema,
    StoreParity,
    ArrayStore,
    AttachDepth,
    AttachVisibility,
}

internal static class TemporalFineExactPrograms
{
    internal static string Rewrite(string source, TemporalFineExactLever[] levers, TemporalDetailPassProbe topology)
    {
        bool classify = source.Contains("void temporalDetailClassifyPixel(", StringComparison.Ordinal);
        bool core = source.Contains("bool detailGate(float motion)", StringComparison.Ordinal);
        bool resolve = source.Contains("void detailChildren(", StringComparison.Ordinal);
        bool store = source == ShaderSources.TemporalDetailUpdateFrag;
        if (source == ShaderSources.TemporalDepthStoreFrag && topology.AttachVisibility && !topology.AttachDepth)
            return AttachToDepth(Normalize(source));
        if (!(classify || core || store)) return source;
        source = Normalize(source);
        bool shareClamp = levers.Contains(TemporalFineExactLever.SharedClamp)
            || classify && levers.Contains(TemporalFineExactLever.SharedClampClassify);
        bool shareAxis = levers.Contains(TemporalFineExactLever.SharedAxis)
            || classify && levers.Contains(TemporalFineExactLever.SharedAxisClassify);
        if ((classify || core) && (shareClamp || shareAxis))
            source = ShareSampling(source, shareClamp, shareAxis);
        foreach (var lever in levers)
        {
            source = lever switch
            {
                TemporalFineExactLever.StateBeforeColor when classify => StateBeforeColor(source),
                TemporalFineExactLever.ResetClassify when classify => ResetClassify(source),
                TemporalFineExactLever.ReuseCandidateLock when core => TemporalFineCostProbePrograms.ReuseCandidate(source),
                TemporalFineExactLever.StillGate when core => Replace(source, "bool detailGate(float motion) {",
                    "bool detailGate(float motion) {\n    [[dont_flatten]] if (detailStill(motion)) return false;"),
                TemporalFineExactLever.TransportReuse when resolve => ReuseTransport(source),
                TemporalFineExactLever.TransportExtrema when resolve => TransportExtrema(source),
                TemporalFineExactLever.StoreParity when store && !topology.ArrayStore => StoreParity(source),
                TemporalFineExactLever.ArrayStore when store => ArrayStore(source),
                _ => source,
            };
        }
        return classify && topology.AttachDepth ? AttachToClassifier(source, topology.AttachVisibility) : source;
    }

    internal static TemporalFineExactLever[] Parse(string name) =>
        name.Split('+').Select(Enum.Parse<TemporalFineExactLever>).ToArray();

    internal static void Export(string directory, string name, TemporalResolvePrecision precision,
        TemporalFineExactLever[] levers, TemporalDetailPassProbe topology)
    {
        Directory.CreateDirectory(directory);
        var programs = new (string Name, string Source)[]
        {
            ("FineClassify", ShaderSources.TemporalDetailClassifyFragment(precision)),
            ("FineResolve", ShaderSources.TemporalDetailResolveFragment(precision)),
            ("FineDebug", ShaderSources.TemporalDetailDebugFragment(precision)),
            ("FineProbe", ShaderSources.TemporalDetailProbeFragment(precision)),
            ("FineStore", ShaderSources.TemporalDetailUpdateFrag),
            ("DepthStore", ShaderSources.TemporalDepthStoreFrag),
        };
        foreach (var program in programs)
        {
            string revised = Rewrite(program.Source, levers, topology);
            string original = Normalize(program.Source);
            if (Normalize(revised) == original) continue;
            string path = Path.Combine(directory, name + "." + program.Name);
            File.WriteAllText(path + ".before.glsl", original + "\n");
            File.WriteAllText(path + ".after.glsl", revised + "\n");
        }
    }

    static string ResetClassify(string source)
    {
        const string signature = "void temporalDetailClassifyPixel(ivec2 pixel) {";
        const string oldSite = "    vec2 uv, pixelCentre;";
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        int site = start < 0 ? -1 : source.IndexOf(oldSite, start, StringComparison.Ordinal);
        if (site < 0) throw new InvalidOperationException("Missing classifier reset anchor.");
        const string gate = """
    if (!(Jitter.w > 0.5)) {
        detailClassification = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

""";
        return source[..site] + gate + source[site..];
    }

    static string ShareSampling(string source, bool shareClamp, bool shareAxis)
    {
        const string start = "vec4 sampleHistoryCatmullRom(vec2 uv, vec2 size) {";
        string macroStart = "#define HistoryColor DetailColor";
        int offset = source.IndexOf(macroStart, StringComparison.Ordinal);
        if (offset < 0) throw new InvalidOperationException("Fine fetch macro missing.");
        string prefix = source[..offset];
        string detail = source[offset..];
        int at = detail.IndexOf(start, StringComparison.Ordinal);
        string original = Cut(detail, start, "#undef sampleHistoryCatmullRom");
        string filtered = original;
        if (shareClamp)
        {
            string bounds = Cut(filtered, "    ivec2 base =", "    vec4 texelMin =");
            filtered = Replace(filtered, bounds, "");
        }
        string args = shareAxis ? "ProbeFineAxis axis, vec2 size" : "vec2 uv, vec2 size";
        if (shareClamp) args += ", vec4 t00, vec4 t10, vec4 t01, vec4 t11";
        filtered = Replace(filtered, start, $"vec4 probeDetailFilter({args}) {{");
        if (shareAxis)
            filtered = ShareWeights(filtered, original);
        detail = detail[..at] + original + filtered + (shareClamp ? ClampReads : "") + "\n" + detail[(at + original.Length)..];
        string calls = Cut(detail, "    vec4 a = detailFetch(", "    detailA = a;");
        string replacement = CandidateSites + (shareAxis ? AxisSites : "");
        if (shareClamp)
        {
            string selection = SharedClampCalls;
            if (shareAxis)
            {
                selection = Replace(selection, "ivec2(floor(ua * fineSize - 0.5))", "lo.base");
                selection = Replace(selection, "ivec2(floor(ud * fineSize - 0.5))", "hi.base");
                selection = Replace(selection, "probeDetailFilter(ua,", "probeDetailFilter(lo,");
                selection = Replace(selection, "probeDetailFilter(ub,", "probeDetailFilter(right,");
                selection = Replace(selection, "probeDetailFilter(uc,", "probeDetailFilter(bottom,");
                selection = Replace(selection, "probeDetailFilter(ud,", "probeDetailFilter(hi,");
            }
            replacement += selection;
        }
        else
        {
            replacement += """
    vec4 a = probeDetailFilter(lo, fineSize);
    vec4 b = probeDetailFilter(right, fineSize);
    vec4 c = probeDetailFilter(bottom, fineSize);
    vec4 d = probeDetailFilter(hi, fineSize);

""";
        }
        detail = Replace(detail, calls, replacement);
        return prefix + detail;
    }

    const string ClampReads = """
vec4 probeDetailPoint(ivec2 p, ivec2 last) {
    return texelFetch(sampler2D(DetailColor, LinearClamp), clamp(p, ivec2(0), last), 0);
}

""";

    const string CandidateSites = """
    vec2 fineSize = size * 2.0;
    vec2 ua = uv + vec2(-0.25,-0.25) / size;
    vec2 ub = uv + vec2( 0.25,-0.25) / size;
    vec2 uc = uv + vec2(-0.25, 0.25) / size;
    vec2 ud = uv + vec2( 0.25, 0.25) / size;

""";

    const string AxisSites = """
    ProbeFineAxis lo = probeFineAxis(ua, fineSize);
    ProbeFineAxis hi = probeFineAxis(ud, fineSize);
    ProbeFineAxis right = probeMixAxes(hi, lo);
    ProbeFineAxis bottom = probeMixAxes(lo, hi);

""";

    const string SharedClampCalls = """
    ivec2 p = ivec2(floor(ua * fineSize - 0.5));
    ivec2 q = ivec2(floor(ud * fineSize - 0.5));
    vec4 a, b, c, d;
    [[dont_flatten]] if (all(equal(q, p + ivec2(1)))) {
        ivec2 last = ivec2(fineSize) - 1;
        vec4 t00 = probeDetailPoint(p, last);
        vec4 t10 = probeDetailPoint(p + ivec2(1,0), last);
        vec4 t20 = probeDetailPoint(p + ivec2(2,0), last);
        vec4 t01 = probeDetailPoint(p + ivec2(0,1), last);
        vec4 t11 = probeDetailPoint(p + ivec2(1,1), last);
        vec4 t21 = probeDetailPoint(p + ivec2(2,1), last);
        vec4 t02 = probeDetailPoint(p + ivec2(0,2), last);
        vec4 t12 = probeDetailPoint(p + ivec2(1,2), last);
        vec4 t22 = probeDetailPoint(p + ivec2(2,2), last);
        a = probeDetailFilter(ua, fineSize, t00, t10, t01, t11);
        b = probeDetailFilter(ub, fineSize, t10, t20, t11, t21);
        c = probeDetailFilter(uc, fineSize, t01, t11, t02, t12);
        d = probeDetailFilter(ud, fineSize, t11, t21, t12, t22);
    } else {
        a = detailFetch(ua, fineSize);
        b = detailFetch(ub, fineSize);
        c = detailFetch(uc, fineSize);
        d = detailFetch(ud, fineSize);
    }

""";

    static string ShareWeights(string filtered, string original)
    {
        string axes = Cut(original, "    vec2 samplePos", "    vec4 top");
        filtered = Replace(filtered, axes, """
    vec2 w0 = axis.w0, w3 = axis.w3, w12 = axis.w12;
    vec2 tc0 = axis.tc0, tc3 = axis.tc3, tc12 = axis.tc12;

""");
        if (filtered.Contains("ivec2 base = ivec2(floor(samplePos - 0.5));", StringComparison.Ordinal))
            filtered = Replace(filtered, "ivec2 base = ivec2(floor(samplePos - 0.5));", "ivec2 base = axis.base;");
        return """
struct ProbeFineAxis {
    vec2 w0, w3, w12, tc0, tc3, tc12;
    ivec2 base;
};
ProbeFineAxis probeFineAxis(vec2 uv, vec2 size) {

""" + axes + """
    return ProbeFineAxis(w0, w3, w12, tc0, tc3, tc12, ivec2(floor(samplePos - 0.5)));
}
ProbeFineAxis probeMixAxes(ProbeFineAxis x, ProbeFineAxis y) {
    return ProbeFineAxis(vec2(x.w0.x, y.w0.y), vec2(x.w3.x, y.w3.y), vec2(x.w12.x, y.w12.y),
        vec2(x.tc0.x, y.tc0.y), vec2(x.tc3.x, y.tc3.y), vec2(x.tc12.x, y.tc12.y), ivec2(x.base.x, y.base.y));
}

""" + filtered;
    }

    internal const string CheckedState = """
all(greaterThan(fetchedState, vec2(-HalfMax))) && all(lessThan(fetchedState, vec2(HalfMax)))
        && all(greaterThan(fetchedRecord, vec2(-HalfMax))) && all(lessThan(fetchedRecord, vec2(HalfMax)))
""";

    internal const string CheckedColor = """
all(greaterThan(fetched, vec4(-HalfMax))) && all(lessThan(fetched, vec4(HalfMax)))
""";

    static string StateBeforeColor(string source)
    {
        string validity = Cut(source, "void temporalDetailClassifyPixel(", "layout(location=0) in vec2 vUv;");
        string check = Cut(validity, "    if (useHistory) {", "    detailClassification = vec4(previousUv");
        string fetch = Cut(check, "        vec4 fetched", "        bool finite");
        string replacement = "    useHistory = useHistory && " + CheckedState + ";\n"
            + "    [[dont_flatten]] if (useHistory) {\n" + fetch + "        useHistory = " + CheckedColor + ";\n    }\n";
        return Replace(source, check, replacement);
    }

    static string ReuseTransport(string source)
    {
        source = Replace(source,
            """
    if (!(r >= 0.0 && r <= 65504.0) || !detailFinite4(fetched) || !detailFinite(mean) || !(scalar > 0.0))
        return vec4(r);
""", "");
        return Replace(source, "    vec4 stored = detailRound(children);",
            """
    vec4 stored = vec4(detailHalf(children.x), detailHalf(children.y), detailHalf(children.z), 0.0);
""");
    }

    static string TransportExtrema(string source)
    {
        string bounds = Cut(source, "    for (int i = 0; i < 4; i++) {", "    vec4 children = vec4(r) + k * delta;");
        return Replace(source, bounds, """
    float remainderDelta = -((delta.x + delta.y) + delta.z);
    float lo = min(min(min(delta.x, delta.y), min(delta.z, delta.w)), remainderDelta);
    float hi = max(max(max(delta.x, delta.y), max(delta.z, delta.w)), remainderDelta);
    if (lo < 0.0) k = min(k, r / -lo);
    if (hi > 0.0) k = min(k, (65504.0 - r) / hi);

""");
    }

    static string StoreParity(string source)
    {
        string selection = Cut(source, "    int child =", "    // Covered fullscreen");
        return Replace(source, selection, """
    if ((texel.y & 1) == 0) {
        if ((texel.x & 1) == 0) oColor = texelFetch(sampler2D(Child0, LinearClamp), pixel, 0);
        else oColor = texelFetch(sampler2D(Child1, LinearClamp), pixel, 0);
    } else {
        if ((texel.x & 1) == 0) oColor = texelFetch(sampler2D(Child2, LinearClamp), pixel, 0);
        else oColor = texelFetch(sampler2D(Child3, LinearClamp), pixel, 0);
    }

""");
    }

    static string AttachToClassifier(string source, bool visibility)
    {
        const string output = "layout(location=0) out vec4 oClassification;";
        const string classify = "    temporalDetailClassifyPixel(ivec2(gl_FragCoord.xy));";
        source = Replace(source, output, output + "\nlayout(location=1) out vec4 probeDepth;"
            + (visibility ? "\nlayout(location=2) out vec4 probeVisibility;" : ""));
        return Replace(source, classify, """
    ivec2 probeTexel = ivec2(gl_FragCoord.xy);
    float probeNdcDepth = texelFetch(sampler2D(SceneDepth, LinearClamp), probeTexel, 0).r;
    vec2 probeMotion = texelFetch(sampler2D(MotionTex, LinearClamp), probeTexel, 0).rg;
    float probeViewDepth = temporalViewDepth(probeMotion, probeNdcDepth);
    probeDepth = vec4(probeViewDepth + vUv.x * 1.0e-30, 0.0, 0.0, 1.0);

""" + (visibility ? VisibilityWrite("probeTexel", "LinearClamp") : "") + classify);
    }

    static string AttachToDepth(string source)
    {
        const string depth = "layout(location=0) out vec4 oDepth;";
        source = Replace(source, depth, depth + "\nlayout(set=0, binding=4) uniform texture2D KeyShadowVisibility;"
            + "\nlayout(location=1) out vec4 probeVisibility;");
        return Replace(source, "    oDepth = vec4", VisibilityWrite("texel", "Samp") + "    oDepth = vec4");
    }

    static string VisibilityWrite(string texel, string sampler) =>
        $"    float visibilityCode = round(texelFetch(sampler2D(KeyShadowVisibility, {sampler}), {texel}, 0).r * 255.0);\n"
        + "    probeVisibility = vec4((visibilityCode + 0.25) / 255.0, 0.0, 0.0, 1.0);\n";

    static string ArrayStore(string source)
    {
        source = Replace(source, Cut(source, "layout(set=0, binding=0)", "layout(location=0) in"), """
layout(set=0, binding=0) uniform texture2DArray Children;
layout(set=0, binding=1) uniform sampler LinearClamp;

""");
        return Replace(source, Cut(source, "    if (child == 0)", "    // Covered fullscreen"),
            "    oColor = texelFetch(sampler2DArray(Children, LinearClamp), ivec3(pixel, child), 0);\n");
    }

    internal static string Normalize(string source) => source.Replace("\r\n", "\n", StringComparison.Ordinal);

    internal static string Cut(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        int last = first < 0 ? -1 : source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        return first >= 0 && last >= 0 ? source[first..last]
            : throw new InvalidOperationException($"Missing probe shader boundaries '{start}' and '{end}'.");
    }

    internal static string Replace(string source, string before, string after)
    {
        int at = source.IndexOf(before, StringComparison.Ordinal);
        if (at < 0 || source.IndexOf(before, at + before.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Expected one probe shader anchor '{before}'.");
        return source[..at] + after + source[(at + before.Length)..];
    }
}
