using System;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// D AS SHIPPED AT 583a3ae7b, FROZEN. The visibility helper, the call-site block that decides the lock release, the
/// later hold release and the <c>precise</c> lock release helper, copied verbatim from
/// <c>ShaderSources.TemporalVisibility.cs</c> and <c>ShaderSources.TemporalAccumulate.cs</c>. The cost cut (plan Task
/// 7f) must keep every output and every observable decision bit-identical to this text, so it is the oracle the
/// equivalence and resolve-level facts compare against. Never edit the four texts or their pins. <see cref="ToD"/>
/// finds the production pieces through anchors, which may move with production. It also restores what the current
/// mask (Task 7f.3) changed in the first pass, the surface and the fused entry, frozen in
/// <see cref="TemporalVisibilityFrozenDMask"/>, and undoes every later production change outside those pieces, listed
/// in <see cref="TemporalVisibilityFrozenDLaterChanges"/>.
/// <para><b>TWO D PROGRAMS SINCE 7g.3a.</b> Production's lock release lost <c>precise</c> for an ordered barrier form
/// (<c>ShaderSources.TemporalLockReleaseGlsl</c>). <see cref="ToShippedD"/> also restores D's precise helper
/// (<see cref="LockReleaseGlsl"/>), so its program is the whole program D shipped, which the pins hold. It must match
/// production byte for byte on Metal, which lowered the precise helper as the barrier form computes. <see cref="ToD"/>,
/// D prime, keeps production's helper, so D and production share one lock arithmetic on every backend. Every other
/// rule compares against D prime.</para>
/// </summary>
internal static class TemporalVisibilityFrozenD
{
    /// <summary>D's <c>ShaderSources.TemporalVisibilityGlsl</c>.</summary>
    internal const string VisibilityGlsl = @"
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
";

    /// <summary>D's lock decision in <c>temporalAccumulatePixel</c>, from the lock read to the hold.</summary>
    internal const string CallSiteGlsl = @"    float lockValue = useHistory && !heldFromFarther ? max(historyState.y - LockDecay, 0.0) : 0.0;
    if (ridge) lockValue = 1.0;
    lockValue = temporalReleaseLock(lockValue, motionPixels, reactive, ownReprojected && useHistory ? 0.0 : edgeMotion);
    // Preserve the colour release's carried-lock classification before visibility clears the stored lock.
    bool carriedLock = !ridge && lockValue > 0.0;
    float signedVisibilityChange = 0.0;
    bool visibilityReleased = useHistory && !surface.background && PreviousJitter.z > 0.5
        && temporalVisibilityChange(uv, centreTexel, maxTexel, previousUv, expectedDepth, signedVisibilityChange)
        && abs(signedVisibilityChange) > VisibilityReleaseThreshold;
    if (visibilityReleased) lockValue = 0.0;
    float hold = clamp(lockValue * LockHoldGain, 0.0, 1.0);";

    /// <summary>D's second effect of the release, after the colour release block.</summary>
    internal const string HoldReleaseGlsl = "    if (released || visibilityReleased) hold = 0.0;";

    /// <summary>D's lock release helper, its comment and the blank line before it, with its two <c>precise</c>
    /// markings. <see cref="ToShippedD"/> puts it where production's <c>ShaderSources.TemporalLockReleaseGlsl</c>
    /// stands.</summary>
    internal const string LockReleaseGlsl = @"
// Keep only the carried-lock release arithmetic ordered. Visibility adds another use of this value, which
// exposed different contractions in the fused and split programs on Metal. Unqualified function inputs keep
// precise from propagating into history sampling, reconstruction or surface preparation.
float temporalReleaseLock(float value, float motionPixels, float reactive, float edgeMotion) {
    precise float motionRelease = clamp((motionPixels - LockMotionStartPixels)
        / (LockMotionEndPixels - LockMotionStartPixels), 0.0, 1.0);
    precise float result = value * ((1.0 - motionRelease) * (1.0 - clamp(reactive * LockReactiveRelease, 0.0, 1.0))
        * (1.0 - clamp(edgeMotion * LockEdgeRelease, 0.0, 1.0)));
    return result;
}
";

    /// <summary>SHA-256 of each text, LF line endings, UTF-8, at 583a3ae7b.</summary>
    internal const string VisibilitySha256 = "a4e2f6fcc9d3857ce66d1857ff395a6057f2db31b0f2beea3ac5fa84dbc982b6";
    internal const string CallSiteSha256 = "e94fbb15a8c2745e3aee38d21aa692bf72838c532b3d4e4bdfbcd70ec1020734";
    internal const string HoldReleaseSha256 = "20758e966da369cc0a6f9ca592544251d6ee3dfaed722d8655439c5621c0ce7e";
    internal const string LockReleaseSha256 = "e2ae2ac8655f07847f1000a363bfd301d3cea93c8377ebe6739b4698d0c96695";

    // Where the production call-site block starts and ends and its hold release. They equal D's lines at 583a3ae7b. A
    // later production change that moves one updates the anchor here, never the frozen text above.
    internal const string ProductionCallSiteFirstLine =
        "    float lockValue = useHistory && !heldFromFarther ? max(historyState.y - LockDecay, 0.0) : 0.0;";
    internal const string ProductionCallSiteLastLine = "    float hold = clamp(lockValue * LockHoldGain, 0.0, 1.0);";
    internal const string ProductionHoldRelease = "    if (released || visibilityReleased) hold = 0.0;";

    /// <summary>The SHA-256 the pins name: lowercase hex over the LF-normalized UTF-8 text, so a CRLF checkout of
    /// either file hashes the same.</summary>
    internal static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))).ToLowerInvariant();

    /// <summary>A production fragment program as D prime: the production visibility block, call-site block and hold
    /// release replaced by the frozen texts, then the current mask's changes undone by
    /// <see cref="TemporalVisibilityFrozenDMask.Restore"/>. Production's lock release helper stays. A program that holds
    /// none of them, such as the depth store, comes back unchanged, and the split's first pass takes only the mask
    /// restore. One that holds only some of the release pieces, or holds one twice, or holds the release without the
    /// mask's pieces, throws, so a production change cannot leave the D program half rewritten.</summary>
    internal static string ToD(string program) => Rewrite(program, VisibilityGlsl, CallSiteGlsl, HoldReleaseGlsl);

    /// <summary>A production fragment program as the program D shipped: <see cref="ToD"/>, then production's lock
    /// release helper replaced by <see cref="LockReleaseGlsl"/>. Every program the renderer compiles comes out as the
    /// program D shipped, which <c>TemporalVisibilityFrozenDTests</c> holds to
    /// <see cref="TemporalVisibilityFrozenDMask.ProgramSha256"/>. A program without the helper comes back as
    /// <see cref="ToD"/> gives it. One that holds the visibility release without the helper, or the helper twice,
    /// throws.</summary>
    internal static string ToShippedD(string program)
    {
        string d = ToD(program);
        int helpers = Count(d, ShaderSources.TemporalLockReleaseGlsl);
        bool release = Count(d, CallSiteGlsl) > 0;
        if (helpers == 0 && !release) return d;
        if (helpers != 1)
            throw new InvalidOperationException($"the shipped D rewrite found {helpers} lock release helpers, one expected");
        return d.Replace(ShaderSources.TemporalLockReleaseGlsl, LockReleaseGlsl, StringComparison.Ordinal);
    }

    /// <summary>As <see cref="ToD"/> with the call site's text transformed first, for mutant controls that prove a
    /// rewrite reaches the compiled program.</summary>
    internal static string ToMutantD(string program, Func<string, string> callSite)
        => Rewrite(program, VisibilityGlsl, callSite(CallSiteGlsl), HoldReleaseGlsl);

    static string Rewrite(string program, string visibility, string callSite, string holdRelease)
    {
        // Test pipelines compile both members. Restore omitted resources before applying the frozen decision.
        program = TemporalVisibilityFullyLitGpuTests.ForceCheck(program);
        // Production changes after D outside its frozen pieces come out first, so the anchors below see D's program.
        program = TemporalVisibilityFrozenDLaterChanges.Restore(program);
        string released = RewriteRelease(program, visibility, callSite, holdRelease);
        string restored = TemporalVisibilityFrozenDMask.Restore(released);
        if (!ReferenceEquals(released, program) && ReferenceEquals(restored, released))
            throw new InvalidOperationException("the D rewrite found the visibility release without the current mask's "
                + "pieces");
        return restored;
    }

    static string RewriteRelease(string program, string visibility, string callSite, string holdRelease)
    {
        int helpers = Count(program, ShaderSources.TemporalVisibilityGlsl);
        int firsts = Count(program, ProductionCallSiteFirstLine);
        int holds = Count(program, ProductionHoldRelease);
        if (helpers == 0 && firsts == 0 && holds == 0) return program;
        if (helpers != 1 || firsts != 1 || holds != 1)
            throw new InvalidOperationException($"the D rewrite found {helpers} visibility blocks, {firsts} call sites "
                + $"and {holds} hold releases, one of each or none expected");
        int start = program.IndexOf(ProductionCallSiteFirstLine, StringComparison.Ordinal);
        int last = program.IndexOf(ProductionCallSiteLastLine, start, StringComparison.Ordinal);
        if (last < 0) throw new InvalidOperationException("the D rewrite found no end to the production call site");
        int end = last + ProductionCallSiteLastLine.Length;
        string rewritten = program[..start] + callSite + program[end..];
        // D has no attributed release selection. Restore its original header as well as its call site.
        rewritten = rewritten.Replace(ShaderSources.TemporalControlFlowGlsl, "", StringComparison.Ordinal);
        rewritten = rewritten.Replace(ShaderSources.TemporalVisibilityGlsl, visibility, StringComparison.Ordinal);
        int hold = rewritten.IndexOf(ProductionHoldRelease, StringComparison.Ordinal);
        return rewritten[..hold] + holdRelease + rewritten[(hold + ProductionHoldRelease.Length)..];
    }

    static int Count(string text, string part)
    {
        int n = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
