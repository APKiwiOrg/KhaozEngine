using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Frozen D (<see cref="TemporalVisibilityFrozenD"/>) and the production decision, each compiled as its own program, on
/// the same cases, one case per fragment, and an independent classifier in a third program that says which inputs and
/// which predicted paths each case exercised. A first pass builds every case from a hash PRNG against full-size
/// textures (<see cref="TemporalVisibilityEquivalenceInputs"/>) and stores it in single-float targets, so the three
/// programs read the same case bit for bit and no compiler can share a value between D and production. Every target is
/// cleared to -1 before its pass, so a fragment a pass did not write shows. <see cref="Combine"/> joins the outputs into
/// one word of <see cref="Flag"/> bits per case.
/// <para>Binade draws (fix round 7f r3) place every centre so that a column, a row or both of its 3x3 lie on a power
/// of two up to 2048, where the binade rule (<see cref="TemporalVisibilityFastPath.SlowForBinade"/>) sends a positive
/// jitter axis to D's path. A fifth program, the shipped decision with that rule written out
/// (<see cref="WithoutBinadeRule"/>), runs only when asked and only to report how often it would differ from D on the
/// device.</para>
/// </summary>
internal sealed class TemporalVisibilityEquivalenceProbe : IDisposable
{
    /// <summary>The bits of a case. The first <see cref="ClassifierFlags"/> are the classifier program's own, in its
    /// bit order. The rest are joined from the D and production programs' outputs.</summary>
    internal enum Flag
    {
        PredictedFast, PredictedSlowEdge, PredictedSlowDeviation, PredictedSlowBorder, PredictedSlowBinade,
        PredictedRangeSkip,
        RangeSkipNearMarginBelow, RangeSkipNearMarginAbove, EdgeCentre, CornerCentre, NearIntegerFraction, NaN,
        PositiveInfinity, NegativeInfinity, Background, CurrentToleranceExact, CurrentToleranceUlp,
        PreviousToleranceExact, PreviousToleranceUlp, EmptyHistory, FastNonFiniteNeighbour, FastNonFiniteStored,
        DValid, DRelease, NewComputed, NewRelease, DecisionMismatch, ValidityMismatch, ChangeMismatch, Written,
        RangeSkipMeetsRelease, NearThresholdAbove, NearThresholdBelow, ReportedFast, ReportedSlow, ReportedRangeSkip,
        ReportedSlowBinade, ReportedPathMismatch,
    }

    /// <summary>How many of <see cref="Flag"/>'s bits the classifier program sets.</summary>
    internal const int ClassifierFlags = (int)Flag.FastNonFiniteStored + 1;

    /// <summary>
    /// THE PRODUCTION SIDE: the shipped decision, <c>temporalVisibilityReleases</c>, and the path bits it reports, with
    /// current depth and motion read by the decision itself. A range skip leaves <c>computed</c> false where D
    /// computed a change, and only there may the validities differ.
    /// </summary>
    internal const string ProductionDecisionGlsl = @"
const uint ReportedFast = 1u, ReportedSlow = 2u, ReportedRangeSkip = 4u, ReportedSlowBinade = 8u;
bool productionReleases(vec2 uv, ivec2 centre, ivec2 last, vec2 previousUv, float expectedDepth,
    out bool computed, out float change, out uint path) {
    change = 0.0;
    uint shipped = 0u;
    bool release = temporalVisibilityReleases(uv, centre, last, previousUv, expectedDepth,
        change, shipped);
    computed = (shipped & VisibilityChangeComputed) != 0u;
    path = ((shipped & VisibilityFastPath) != 0u ? ReportedFast : 0u)
        | ((shipped & VisibilitySlowPath) != 0u ? ReportedSlow : 0u)
        | ((shipped & VisibilityRangeSkip) != 0u ? ReportedRangeSkip : 0u)
        | ((shipped & VisibilitySlowBinade) != 0u ? ReportedSlowBinade : 0u);
    return release;
}
";

    // The scene's inputs, the probe's uniforms and the stored case, as the D, production and classifier programs bind
    // them.
    const string Header = @"
layout(set=0,binding=0) uniform texture2D SceneDepth;
layout(set=0,binding=1) uniform texture2D MotionTex;
layout(set=0,binding=2) uniform texture2D PrevDepth;
layout(set=0,binding=3) uniform texture2D KeyShadowVisibility;
layout(set=0,binding=4) uniform texture2D PreviousKeyShadowVisibility;
layout(set=0,binding=5) uniform sampler LinearClamp;
layout(set=0,binding=6) uniform Probe { vec4 CurrentDepth; vec4 Sizes; vec4 Jitter; vec4 PreviousJitter; vec4 Seed; };
layout(set=0,binding=7) uniform texture2D CaseUvX;
layout(set=0,binding=8) uniform texture2D CaseUvY;
layout(set=0,binding=9) uniform texture2D CasePreviousUvX;
layout(set=0,binding=10) uniform texture2D CasePreviousUvY;
layout(set=0,binding=11) uniform texture2D CaseCentre;
layout(set=0,binding=12) uniform texture2D CaseExpectedDepth;
";

    // The case pass binds only what it reads.
    const string CaseHeader = @"
layout(set=0,binding=0) uniform texture2D PrevDepth;
layout(set=0,binding=1) uniform sampler LinearClamp;
layout(set=0,binding=2) uniform Probe { vec4 CurrentDepth; vec4 Sizes; vec4 Jitter; vec4 PreviousJitter; vec4 Seed; };
";

    // The case, as every program holds it.
    const string CaseStructGlsl = @"
struct VisibilityCase { vec2 uv; ivec2 centre; vec2 previousUv; float expectedDepth; };
";

    // The PRNG helpers the case pass and the classifier share.
    const string CaseCommonGlsl = @"
uint caseHash(uint v) {
    v ^= v >> 16; v *= 0x7feb352du; v ^= v >> 15; v *= 0x846ca68bu; v ^= v >> 16;
    return v;
}
uint caseNext(inout uint s) { s = caseHash(s + 0x9e3779b9u); return s; }
float caseUnit(inout uint s) { return float(caseNext(s) >> 8) * (1.0 / 16777216.0); }
int caseInt(inout uint s, int n) { return int(caseNext(s) % uint(n)); }
// k ulps away from a positive finite value, the value itself otherwise.
float caseUlps(float v, int k) {
    return v > 0.0 && v < 3.0e38 ? uintBitsToFloat(uint(int(floatBitsToUint(v)) + k)) : v;
}
";

    // The case: a centre texel, the pixel's UV, a previous UV that puts the centre's history position at a chosen
    // texel plus a chosen fraction, and an expected depth related to the stored depths there.
    const string BuildCaseGlsl = @"
// A power of two no larger than last - 1, then one texel either side of it or on it.
int casePowerOfTwoLine(inout uint s, int last) {
    int power = 1 << caseInt(s, findMSB(last - 1) + 1);
    return power + caseInt(s, 3) - 1;
}
int caseEdge(inout uint s, int last) {
    int r = caseInt(s, 5);
    return r == 0 ? 0 : r == 1 ? 1 : r == 2 ? last - 1 : r == 3 ? last : caseInt(s, last + 1);
}
VisibilityCase buildCase(ivec2 fragment) {
    uint s = caseHash(uint(fragment.x) * 0x8da6b343u ^ uint(fragment.y) * 0xd8163841u
        ^ (uint(Seed.x) + 1u) * 0xcb1ab31fu);
    vec2 size = Sizes.xy;
    ivec2 last = ivec2(size) - 1;
    VisibilityCase c;
    float mode = caseUnit(s);
    bool designed = mode < 0.45;
    vec2 inTexel = vec2(caseUnit(s), caseUnit(s)) * 0.98 - 0.49;
    if (designed) {
        ivec2 tiles = ivec2(size) / 8;
        c.centre = ivec2(caseInt(s, tiles.x), caseInt(s, tiles.y)) * 8 + 3;
    } else if (mode < 0.6) {
        // Edges and corners, and centres one texel in, whose history block can leave the texture.
        c.centre = ivec2(caseEdge(s, last.x), caseEdge(s, last.y));
        float force = caseUnit(s);
        if (force < 0.35) c.centre.x = caseUnit(s) < 0.5 ? 0 : last.x;
        else if (force < 0.7) c.centre.y = caseUnit(s) < 0.5 ? 0 : last.y;
    } else {
        c.centre = ivec2(caseInt(s, last.x + 1), caseInt(s, last.y + 1));
    }
    bool binadeDraw = Seed.y > 0.5;
    if (binadeDraw) {
        // A column, a row or both of the 3x3 on a power of two up to 2048, the centre one texel either side of it or
        // on it, never on the edge.
        int pick = caseInt(s, 4);
        if (pick != 1) c.centre.x = clamp(casePowerOfTwoLine(s, last.x), 1, last.x - 1);
        if (pick != 0) c.centre.y = clamp(casePowerOfTwoLine(s, last.y), 1, last.y - 1);
    }
    c.uv = (vec2(c.centre) + 0.5 - Jitter.xy + inTexel) / size;
    if (!designed && !binadeDraw && mode >= 0.6 && mode < 0.85) {
        // A display pixel and the texel its jittered sample lands in, as temporalPixelSite finds them.
        vec2 pixel = floor(vec2(caseUnit(s), caseUnit(s)) * Sizes.zw);
        c.uv = (pixel + 0.5) / Sizes.zw;
        c.centre = clamp(ivec2(floor(c.uv * size + Jitter.xy)), ivec2(0), last);
    }
    float motionKind = caseUnit(s);
    vec2 shift = designed ? (motionKind < 0.5 ? vec2(0.0) : vec2(caseInt(s, 3) - 1, caseInt(s, 3) - 1))
        : motionKind < 0.6 ? vec2(caseInt(s, 7) - 3, caseInt(s, 7) - 3)
        : floor((vec2(caseUnit(s), caseUnit(s)) - 0.5) * 40.0);
    vec2 target = vec2(c.centre) + shift;
    if (mode >= 0.45 && mode < 0.6 && caseUnit(s) < 0.5)
        target = vec2(caseEdge(s, last.x), caseEdge(s, last.y));
    float fractionKind = caseUnit(s);
    vec2 fraction = fractionKind < 0.25 ? vec2(0.0)
        : fractionKind < 0.35 ? (vec2(caseUnit(s), caseUnit(s)) - 0.5) * 2.0e-3
        : vec2(caseUnit(s), caseUnit(s));
    // The centre's history position is previousUv * S + (centre + 0.5 - J) - uv * S + Jp - 0.5, so this previous UV
    // lands it at target + fraction, up to rounding.
    vec2 offsetInTexel = c.uv * size - (vec2(c.centre) + 0.5 - Jitter.xy);
    c.previousUv = (target + fraction + 0.5 + offsetInTexel - PreviousJitter.xy) / size;
    if (fractionKind < 0.35 && caseUnit(s) < 0.6)
        c.previousUv = vec2(caseUlps(c.previousUv.x, caseInt(s, 7) - 3), caseUlps(c.previousUv.y, caseInt(s, 7) - 3));
    c.previousUv = clamp(c.previousUv, vec2(0.0), vec2(1.0));

    ivec2 base = clamp(ivec2(floor(target + fraction)), ivec2(0), last);
    float corner = texelFetch(sampler2D(PrevDepth, LinearClamp), base, 0).r;
    float site = texelFetch(sampler2D(PrevDepth, LinearClamp), clamp(base / 8 * 8 + 3, ivec2(0), last), 0).r;
    float r = caseUnit(s);
    float e;
    if (r < 0.30) e = corner;
    else if (r < 0.45) e = site;
    else if (r < 0.60) e = caseUlps(corner, caseInt(s, 7) - 3);
    else if (r < 0.72) e = caseUlps(site / 1.02, caseInt(s, 5) - 2);
    else if (r < 0.77) e = caseUlps(site * 1.02, caseInt(s, 5) - 2);
    else if (r < 0.85) e = 0.05 * exp2(caseUnit(s) * 12.0);
    else if (r < 0.90) {
        int special = caseInt(s, 7);
        e = special == 0 ? uintBitsToFloat(0x7fc00000u) : special == 1 ? uintBitsToFloat(0x7f800000u)
            : special == 2 ? uintBitsToFloat(0xff800000u) : special == 3 ? 0.0 : special == 4 ? -1.0
            : special == 5 ? 1.0e30 : 1.0e-6;
    } else e = texelFetch(sampler2D(PrevDepth, LinearClamp),
        clamp(base + ivec2(caseInt(s, 7) - 3, caseInt(s, 7) - 3), ivec2(0), last), 0).r;
    c.expectedDepth = e;
    return c;
}
";

    // The case pass: each fragment's case into six single-float targets. The centre is packed as x + 4096 y, which a
    // single float holds exactly at every size the oracle draws.
    const string CaseMain = @"
layout(location=0) out float oUvX;
layout(location=1) out float oUvY;
layout(location=2) out float oPreviousUvX;
layout(location=3) out float oPreviousUvY;
layout(location=4) out float oCentre;
layout(location=5) out float oExpectedDepth;
void main() {
    VisibilityCase c = buildCase(ivec2(gl_FragCoord.xy));
    oUvX = c.uv.x;
    oUvY = c.uv.y;
    oPreviousUvX = c.previousUv.x;
    oPreviousUvY = c.previousUv.y;
    oCentre = float(c.centre.x + c.centre.y * 4096);
    oExpectedDepth = c.expectedDepth;
}
";

    // The stored case of a fragment, as the case pass wrote it.
    const string ReadCaseGlsl = @"
VisibilityCase readCase(ivec2 f) {
    VisibilityCase c;
    c.uv = vec2(texelFetch(sampler2D(CaseUvX, LinearClamp), f, 0).r, texelFetch(sampler2D(CaseUvY, LinearClamp), f, 0).r);
    c.previousUv = vec2(texelFetch(sampler2D(CasePreviousUvX, LinearClamp), f, 0).r,
        texelFetch(sampler2D(CasePreviousUvY, LinearClamp), f, 0).r);
    int packed = int(texelFetch(sampler2D(CaseCentre, LinearClamp), f, 0).r);
    c.centre = ivec2(packed % 4096, packed / 4096);
    c.expectedDepth = texelFetch(sampler2D(CaseExpectedDepth, LinearClamp), f, 0).r;
    return c;
}
";

    // D's program: D's helper as it shipped, alone with the case.
    const string DMain = @"
layout(location=0) out float oFlags;
layout(location=1) out float oChange;
void main() {
    VisibilityCase c = readCase(ivec2(gl_FragCoord.xy));
    float change = 0.0;
    bool valid = temporalVisibilityChange(c.uv, c.centre, ivec2(Sizes.xy) - 1, c.previousUv, c.expectedDepth, change);
    bool release = valid && abs(change) > VisibilityReleaseThreshold;
    oFlags = float((valid ? 1u : 0u) | (release ? 2u : 0u));
    oChange = change;
}
";

    // The production program: the shipped decision alone with the case.
    const string ProductionMain = @"
layout(location=0) out float oFlags;
layout(location=1) out float oChange;
void main() {
    VisibilityCase c = readCase(ivec2(gl_FragCoord.xy));
    bool computed;
    float change;
    uint path;
    bool release = productionReleases(c.uv, c.centre, ivec2(Sizes.xy) - 1, c.previousUv, c.expectedDepth, computed,
        change, path);
    oFlags = float((computed ? 1u : 0u) | (release ? 2u : 0u) | (path << 2));
    oChange = computed ? change : 0.0;
}
";

    // Independent of both sides: which inputs the case met, and which path the cost cut's preconditions (separable
    // consecutive bases, a 4x4 block inside the texture) and its R4 bound predict.
    const string ClassifierGlsl = @"
// Which kind of non-finite a value is, read from its bits so no compiler can fold the test away.
uint classNonFinite(float v) {
    uint b = floatBitsToUint(v);
    if ((b & 0x7f800000u) != 0x7f800000u) return 0u;
    if ((b & 0x007fffffu) != 0u) return ClassNaN;
    return (b & 0x80000000u) != 0u ? ClassNegativeInfinity : ClassPositiveInfinity;
}
uint classify(VisibilityCase c) {
    uint bits = 0u;
    ivec2 last = ivec2(Sizes.xy) - 1;
    bool edgeX = c.centre.x == 0 || c.centre.x == last.x, edgeY = c.centre.y == 0 || c.centre.y == last.y;
    if (edgeX || edgeY) bits |= ClassEdgeCentre;
    if (edgeX && edgeY) bits |= ClassCornerCentre;
    float centreDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), c.centre, 0).rg,
        texelFetch(sampler2D(SceneDepth, LinearClamp), c.centre, 0).r);
    float e = c.expectedDepth;
    bits |= classNonFinite(centreDepth) | classNonFinite(e);
    bool references = classNonFinite(centreDepth) == 0u && classNonFinite(e) == 0u;
    float currentTolerance = VisibilityDepthTolerance * max(centreDepth, 1.0e-5);
    float previousTolerance = VisibilityDepthTolerance * max(e, 1.0e-5);
    float currentSum = 0.0, currentCount = 0.0, previousAccepted = 0.0;
    bool neighbourNonFinite = false, storedNonFinite = false;
    int baseX[3], baseY[3], atX[3], atY[3];
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 at = clamp(c.centre + ivec2(x, y), ivec2(0), last);
            vec2 motion = texelFetch(sampler2D(MotionTex, LinearClamp), at, 0).rg;
            if (abs(motion.x) > MotionSentinel) bits |= ClassBackground;
            float depth = temporalViewDepth(motion, texelFetch(sampler2D(SceneDepth, LinearClamp), at, 0).r);
            vec2 historyUv = c.previousUv + (vec2(at) + 0.5 - Jitter.xy) / Sizes.xy - c.uv + PreviousJitter.xy / Sizes.xy;
            vec2 position = historyUv * Sizes.xy - 0.5;
            ivec2 base = ivec2(floor(position));
            if (y == 0) { baseX[x + 1] = base.x; atX[x + 1] = at.x; }
            if (x == 0) { baseY[y + 1] = base.y; atY[y + 1] = at.y; }
            vec2 fraction = position - vec2(base);
            if (x == 0 && y == 0 && any(lessThan(min(fraction, 1.0 - fraction), vec2(1.0e-3))))
                bits |= ClassNearIntegerFraction;
            if (classNonFinite(depth) != 0u) {
                bits |= classNonFinite(depth);
                if (x != 0 || y != 0) neighbourNonFinite = true;
                continue;
            }
            if (!references) continue;
            float apart = abs(depth - centreDepth);
            if (apart == currentTolerance) bits |= ClassCurrentToleranceExact;
            float up = centreDepth + currentTolerance, down = centreDepth - currentTolerance;
            if (depth == caseUlps(up, 1) || depth == caseUlps(up, -1) || depth == caseUlps(down, 1)
                || depth == caseUlps(down, -1)) bits |= ClassCurrentToleranceUlp;
            if (!(apart <= currentTolerance)) continue;
            currentSum += texelFetch(sampler2D(KeyShadowVisibility, LinearClamp), at, 0).r;
            currentCount += 1.0;
            for (int j = 0; j < 2; j++) {
                for (int i = 0; i < 2; i++) {
                    float stored = texelFetch(sampler2D(PrevDepth, LinearClamp),
                        clamp(base + ivec2(i, j), ivec2(0), last), 0).r;
                    if (classNonFinite(stored) != 0u) {
                        bits |= classNonFinite(stored);
                        storedNonFinite = true;
                        continue;
                    }
                    if (abs(stored - e) <= previousTolerance) previousAccepted += 1.0;
                    if (abs(stored - e) == previousTolerance) bits |= ClassPreviousToleranceExact;
                    float pUp = e + previousTolerance, pDown = e - previousTolerance;
                    if (stored == caseUlps(pUp, 1) || stored == caseUlps(pUp, -1) || stored == caseUlps(pDown, 1)
                        || stored == caseUlps(pDown, -1)) bits |= ClassPreviousToleranceUlp;
                }
            }
        }
    }
    if (references && currentCount > 0.0 && previousAccepted == 0.0) bits |= ClassEmptyHistory;
    bool consecutive = baseX[0] == baseX[1] - 1 && baseX[2] == baseX[1] + 1
        && baseY[0] == baseY[1] - 1 && baseY[2] == baseY[1] + 1;
    bool inside = baseX[1] - 1 >= 0 && baseX[1] + 2 <= last.x && baseY[1] - 1 >= 0 && baseY[1] + 2 <= last.y;
    // The binade rule, written apart from production's: a single set bit, under a jitter whose sign bit is clear and
    // whose bits are not zero, so a denormal a device flushes still counts as positive.
    bvec2 positive = bvec2((floatBitsToUint(Jitter.x) & 0x80000000u) == 0u && floatBitsToUint(Jitter.x) != 0u,
        (floatBitsToUint(Jitter.y) & 0x80000000u) == 0u && floatBitsToUint(Jitter.y) != 0u);
    bool binade = false;
    for (int k = 0; k < 3; k++)
        binade = binade || (positive.x && bitCount(atX[k]) == 1) || (positive.y && bitCount(atY[k]) == 1);
    bool fast = !(edgeX || edgeY) && consecutive && inside && !binade;
    if (edgeX || edgeY) bits |= ClassPredictedSlowEdge;
    else if (!consecutive) bits |= ClassPredictedSlowDeviation;
    else if (!inside) bits |= ClassPredictedSlowBorder;
    else if (binade) bits |= ClassPredictedSlowBinade;
    else bits |= ClassPredictedFast;
    if (fast && references && neighbourNonFinite) bits |= ClassFastNonFiniteNeighbour;
    if (fast && references && currentCount > 0.0) {
        float lowest = 2.0, highest = -1.0;
        for (int j = -1; j <= 2; j++)
            for (int i = -1; i <= 2; i++) {
                float v = texelFetch(sampler2D(PreviousKeyShadowVisibility, LinearClamp),
                    ivec2(baseX[1] + i, baseY[1] + j), 0).r;
                lowest = min(lowest, v);
                highest = max(highest, v);
            }
        // THIS IS THE PRODUCTION FORMULA. The current mean's distance from each end of the block's range, times the
        // count, against the margin times the count: the same expression the shipped skip evaluates, so a skip it
        // predicts is one production takes, and the reported-equals-predicted check holds R4 to its own formula only.
        // The evidence that R4 is sound is RangeSkipMeetsRelease: no case predicted to skip may meet a release by D,
        // which D decides on its own. The near-margin bands are 2^-11 times the count either side of the margin.
        float bound = max(currentSum - lowest * currentCount, highest * currentCount - currentSum);
        const float Margin = VisibilityReleaseThreshold - 1.220703125e-4;
        bool skip = bound <= Margin * currentCount;
        if (skip) bits |= ClassPredictedRangeSkip;
        if (abs(bound - Margin * currentCount) <= 4.8828125e-4 * currentCount)
            bits |= skip ? ClassRangeSkipNearMarginBelow : ClassRangeSkipNearMarginAbove;
        if (!skip && storedNonFinite) bits |= ClassFastNonFiniteStored;
    }
    return bits;
}
";

    const string ClassifierMain = @"
layout(location=0) out float oLow;
layout(location=1) out float oHigh;
void main() {
    uint bits = classify(readCase(ivec2(gl_FragCoord.xy)));
    oLow = float(bits & 0xffffu);
    oHigh = float(bits >> 16);
}
";

    // The classifier's bit constants, named as Flag names them.
    static readonly string ClassifierBits = string.Concat(Enumerable.Range(0, ClassifierFlags)
        .Select(i => $"const uint Class{(Flag)i} = {1u << i}u;\n"));

    static readonly string[] CaseTargets =
        ["CaseUvX", "CaseUvY", "CasePreviousUvX", "CasePreviousUvY", "CaseCentre", "CaseExpectedDepth"];

    const int CentreTarget = 4;

    static string Program(string body) => "#version 450\n" + Header + ShaderSources.TemporalCommonGlsl
        + ShaderSources.TemporalResolveTuningGlsl + body;

    /// <summary>The case pass.</summary>
    internal static string CaseSource => "#version 450\n" + CaseHeader + CaseStructGlsl + CaseCommonGlsl + BuildCaseGlsl
        + CaseMain;

    /// <summary>Frozen D alone, its helper as it shipped.</summary>
    internal static string DSource => Program(TemporalVisibilityFrozenD.VisibilityGlsl + CaseStructGlsl + ReadCaseGlsl
        + DMain);

    /// <summary>The shipped decision alone.</summary>
    internal static string ProductionSource => Program(ShaderSources.TemporalVisibilityGlsl + ProductionDecisionGlsl
        + CaseStructGlsl + ReadCaseGlsl + ProductionMain);

    /// <summary>The shipped decision with the binade rule written out, for the diagnostic count only: its blocks at a
    /// power of two under positive jitter take the fast path, whichever order FXC compiles their chains in.</summary>
    internal static string UnruledSource => WithoutBinadeRule(ProductionSource);

    /// <summary>A test-only rewrite of a program carrying the shipped decision: the binade test reads false. Throws
    /// where the decision's binade statement is missing or repeated.</summary>
    internal static string WithoutBinadeRule(string program)
    {
        const string Start = "    bool binade = ";
        int first = program.IndexOf(Start, StringComparison.Ordinal);
        if (first < 0 || program.IndexOf(Start, first + Start.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("the rewrite expects one binade statement in the shipped decision");
        int end = program.IndexOf(';', first);
        return program[..first] + Start + "false" + program[end..];
    }

    /// <summary>The classifier alone.</summary>
    internal static string ClassifierSource => Program(ClassifierBits + CaseStructGlsl + CaseCommonGlsl + ReadCaseGlsl
        + ClassifierGlsl + ClassifierMain);

    /// <summary>Every program the probe compiles, by name.</summary>
    internal static (string Name, string Source)[] Sources =>
    [
        ("case", CaseSource), ("frozen D", DSource), ("production decision", ProductionSource),
        ("classifier", ClassifierSource), ("production decision without the binade rule", UnruledSource),
    ];

    readonly GpuDeviceContext _gpu = GpuDeviceContext.CreateHeadless();
    readonly List<IDisposable> _owned = [];
    readonly IGpuResourceLayout _caseLayout, _layout;
    readonly Pass _case, _d, _production, _classifier, _unruled;
    readonly IGpuBuffer _uniforms;
    readonly IGpuTexture[] _caseTargets;
    internal IGpuDevice Device => _gpu.GpuDevice;
    internal readonly int Size;

    sealed record Pass(string Name, IGpuPipeline Pipeline, IGpuFramebuffer Framebuffer, IGpuTexture[] Targets);

    /// <summary>A probe drawing <paramref name="hardwareSize"/> squared cases a draw, or
    /// <paramref name="softwareSize"/> squared on a software adapter.</summary>
    internal TemporalVisibilityEquivalenceProbe(int hardwareSize, int softwareSize)
    {
        Size = Device.Diagnostics.SoftwareAdapter == true ? softwareSize : hardwareSize;
        IGpuResourceFactory f = Device.Factory;
        _uniforms = Own(f.CreateBuffer(new GpuBufferDescription(80, GpuBufferUsage.UniformBuffer)));
        _caseLayout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("PrevDepth", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("Probe", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment))));
        var elements = new List<GpuResourceLayoutElement>();
        foreach (string name in new[] { "SceneDepth", "MotionTex", "PrevDepth", "KeyShadowVisibility", "PreviousKeyShadowVisibility" })
            elements.Add(new(name, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment));
        elements.Add(new("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment));
        elements.Add(new("Probe", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment));
        foreach (string name in CaseTargets) elements.Add(new(name, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment));
        _layout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(elements.ToArray())));
        _case = MakePass("case", CaseSource, _caseLayout, CaseTargets.Length);
        _caseTargets = _case.Targets;
        _d = MakePass("frozen D", DSource, _layout, 2);
        _production = MakePass("production decision", ProductionSource, _layout, 2);
        _classifier = MakePass("classifier", ClassifierSource, _layout, 2);
        _unruled = MakePass("without the binade rule", UnruledSource, _layout, 2);
    }

    Pass MakePass(string name, string source, IGpuResourceLayout layout, int targets)
    {
        IGpuResourceFactory f = Device.Factory;
        var textures = new IGpuTexture[targets];
        for (int i = 0; i < targets; i++)
            textures[i] = Own(f.CreateTexture(GpuTextureDescription.Texture2D((uint)Size, (uint)Size,
                GpuPixelFormat.R32Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));
        IGpuFramebuffer framebuffer = Own(f.CreateFramebuffer(null, textures));
        IGpuShaderSet shaders = Own(f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, source));
        IGpuPipeline pipeline = Own(f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = Enumerable.Repeat(GpuBlendAttachment.OverrideBlend, targets).ToArray(),
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [layout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = framebuffer.Outputs,
        }));
        return new Pass(name, pipeline, framebuffer, textures);
    }

    T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    /// <summary>The textures of one internal size, uploaded once for every draw at that size.</summary>
    internal sealed class Textures(IGpuDevice device, TemporalVisibilityEquivalenceInputs inputs) : IDisposable
    {
        internal readonly TemporalVisibilityEquivalenceInputs Inputs = inputs;
        internal readonly IGpuTexture Depth = Upload(device, inputs, GpuPixelFormat.R32Float, inputs.Depth);
        internal readonly IGpuTexture Motion = Upload(device, inputs, GpuPixelFormat.R16G16Float, inputs.Motion);
        internal readonly IGpuTexture PreviousDepth = Upload(device, inputs, GpuPixelFormat.R32Float, inputs.PreviousDepth);
        internal readonly IGpuTexture Current = Upload(device, inputs, inputs.Current);
        internal readonly IGpuTexture Previous = Upload(device, inputs, inputs.Previous);

        static IGpuTexture Create(IGpuDevice device, TemporalVisibilityEquivalenceInputs inputs, GpuPixelFormat format)
            => device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)inputs.Width, (uint)inputs.Height,
                format, GpuTextureUsage.Sampled));

        static IGpuTexture Upload(IGpuDevice device, TemporalVisibilityEquivalenceInputs inputs, GpuPixelFormat format,
            float[] values)
        {
            IGpuTexture texture = Create(device, inputs, format);
            TemporalTextureIo.Upload(device, texture, values);
            return texture;
        }

        static IGpuTexture Upload(IGpuDevice device, TemporalVisibilityEquivalenceInputs inputs, byte[] bytes)
        {
            IGpuTexture texture = Create(device, inputs, GpuPixelFormat.R8UNorm);
            device.UpdateTexture(texture, bytes, 0, 0, (uint)inputs.Width, (uint)inputs.Height);
            return texture;
        }

        public void Dispose()
        {
            Depth.Dispose();
            Motion.Dispose();
            PreviousDepth.Dispose();
            Current.Dispose();
            Previous.Dispose();
        }
    }

    /// <summary>One draw's outputs, one value per case, rows top to bottom. The unruled program's outputs are empty
    /// unless the draw asked for them.</summary>
    internal sealed record Outputs(float[] Centre, float[] DFlags, float[] DChange, float[] NewFlags, float[] NewChange,
        float[] ClassLow, float[] ClassHigh, float[] UnruledFlags, float[] UnruledChange);

    /// <summary>One draw: every fragment of the target one case, at a display size, a jitter pair and a seed. The case
    /// pass runs first, then D, production and the classifier, each its own program. A binade draw places the
    /// centres on power-of-two columns and rows, and also runs the decision without the binade rule.</summary>
    internal Outputs Run(Textures textures, Vector2 display, Vector2 jitter, Vector2 previousJitter, int seed,
        bool binade = false)
    {
        float[] uniforms = [0, 0, 1, 0, textures.Inputs.Width, textures.Inputs.Height, display.X, display.Y,
            jitter.X, jitter.Y, 1, 1, previousJitter.X, previousJitter.Y, 1, 0, seed, binade ? 1 : 0, 0, 0];
        Device.UpdateBuffer(_uniforms, 0, uniforms);
        using IGpuResourceSet caseSet = Device.Factory.CreateResourceSet(new GpuResourceSetDescription(_caseLayout,
            textures.PreviousDepth, Device.LinearSampler, _uniforms));
        var resources = new List<IGpuBindableResource>
        {
            textures.Depth, textures.Motion, textures.PreviousDepth, textures.Current, textures.Previous,
            Device.LinearSampler, _uniforms,
        };
        resources.AddRange(_caseTargets);
        using IGpuResourceSet set = Device.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
            resources.ToArray()));
        Draw(_case, caseSet);
        Draw(_d, set);
        Draw(_production, set);
        Draw(_classifier, set);
        if (binade) Draw(_unruled, set);
        return new Outputs(TemporalTextureIo.Read(Device, _caseTargets[CentreTarget]),
            TemporalTextureIo.Read(Device, _d.Targets[0]), TemporalTextureIo.Read(Device, _d.Targets[1]),
            TemporalTextureIo.Read(Device, _production.Targets[0]), TemporalTextureIo.Read(Device, _production.Targets[1]),
            TemporalTextureIo.Read(Device, _classifier.Targets[0]), TemporalTextureIo.Read(Device, _classifier.Targets[1]),
            binade ? TemporalTextureIo.Read(Device, _unruled.Targets[0]) : [],
            binade ? TemporalTextureIo.Read(Device, _unruled.Targets[1]) : []);
    }

    void Draw(Pass pass, IGpuResourceSet set)
    {
        using IGpuCommandList commands = Device.Factory.CreateCommandList();
        using (GpuRecording.Open(Device, commands, nameof(TemporalVisibilityEquivalenceProbe) + " " + pass.Name))
        {
            commands.SetFramebuffer(pass.Framebuffer);
            for (uint t = 0; t < pass.Targets.Length; t++) commands.ClearColorTarget(t, new Color(-1f, -1f, -1f, -1f));
            commands.SetPipeline(pass.Pipeline);
            commands.SetGraphicsResourceSet(0, set);
            commands.Draw(3);
        }
        Device.Submit(commands);
        Device.WaitForIdle();
    }

    /// <summary>Case <paramref name="i"/>'s <see cref="Flag"/> bits from one draw's outputs: the classifier's own,
    /// D's and production's results, their comparison, and the classifier's prediction against both.</summary>
    internal static ulong Combine(Outputs o, int i)
    {
        static ulong Bit(Flag flag) => 1UL << (int)flag;
        bool written = o.Centre[i] >= 0f && o.DFlags[i] >= 0f && o.NewFlags[i] >= 0f && o.ClassLow[i] >= 0f
            && o.ClassHigh[i] >= 0f;
        if (!written) return 0;
        ulong bits = (ulong)o.ClassLow[i] | ((ulong)o.ClassHigh[i] << 16) | Bit(Flag.Written);
        uint d = (uint)o.DFlags[i], p = (uint)o.NewFlags[i];
        bool dValid = (d & 1) != 0, dRelease = (d & 2) != 0, computed = (p & 1) != 0, release = (p & 2) != 0;
        bool fast = (p & 4) != 0, slow = (p & 8) != 0, skip = (p & 16) != 0, binade = (p & 32) != 0;
        if (dValid) bits |= Bit(Flag.DValid);
        if (dRelease) bits |= Bit(Flag.DRelease);
        if (computed) bits |= Bit(Flag.NewComputed);
        if (release) bits |= Bit(Flag.NewRelease);
        if (fast) bits |= Bit(Flag.ReportedFast);
        if (slow) bits |= Bit(Flag.ReportedSlow);
        if (skip) bits |= Bit(Flag.ReportedRangeSkip);
        if (binade) bits |= Bit(Flag.ReportedSlowBinade);
        if (dRelease != release) bits |= Bit(Flag.DecisionMismatch);
        if ((computed && !dValid) || (dValid && !computed && !skip)) bits |= Bit(Flag.ValidityMismatch);
        if (computed && dValid && BitConverter.SingleToUInt32Bits(o.DChange[i]) != BitConverter.SingleToUInt32Bits(o.NewChange[i]))
            bits |= Bit(Flag.ChangeMismatch);
        bool predictedFast = (bits & Bit(Flag.PredictedFast)) != 0, predictedSkip = (bits & Bit(Flag.PredictedRangeSkip)) != 0;
        bool predictedBinade = (bits & Bit(Flag.PredictedSlowBinade)) != 0;
        if (predictedSkip && dRelease) bits |= Bit(Flag.RangeSkipMeetsRelease);
        // The reported path must be the predicted one, case for case: fast or slow, the binade and the range skip.
        if (fast != predictedFast || slow == predictedFast || skip != predictedSkip || binade != predictedBinade)
            bits |= Bit(Flag.ReportedPathMismatch);
        float magnitude = MathF.Abs(o.DChange[i]);
        if (dValid && MathF.Abs(magnitude - TemporalResolveTuning.VisibilityReleaseThreshold) <= 2.384185791015625e-7f)
            bits |= magnitude > TemporalResolveTuning.VisibilityReleaseThreshold ? Bit(Flag.NearThresholdAbove)
                : Bit(Flag.NearThresholdBelow);
        return bits;
    }

    /// <summary>Case <paramref name="i"/> of a binade draw, the decision without the binade rule against D: whether
    /// its release decision differs, and whether its change differs in bits where both computed one.</summary>
    internal static (bool Decision, bool Change) UnruledApart(Outputs o, int i)
    {
        uint d = (uint)o.DFlags[i], u = (uint)o.UnruledFlags[i];
        bool dValid = (d & 1) != 0, dRelease = (d & 2) != 0, computed = (u & 1) != 0, release = (u & 2) != 0;
        return (dRelease != release, computed && dValid
            && BitConverter.SingleToUInt32Bits(o.DChange[i]) != BitConverter.SingleToUInt32Bits(o.UnruledChange[i]));
    }

    public void Dispose()
    {
        Device.WaitForIdle();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _gpu.Dispose();
    }
}
