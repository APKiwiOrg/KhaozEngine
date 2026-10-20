using System;
using System.Globalization;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// WHICH PATH THE SHIPPED DECISION TOOK, PIXEL BY PIXEL, IN A REWRITE OF THE PRODUCTION PROGRAM WHOSE STORED LOCKS MUST
/// EQUAL PRODUCTION'S (plan Task 7f, reviews 7f r1 I2 and r2 m-2). <see cref="Rewrite"/> is a test-only rewrite through
/// the renderer's fragment source hook. It keeps every production rule and adds three things: two private globals, a
/// slow path that also names its reason (an edge centre, bases off their consecutive run, or a block over the border),
/// and a call site that copies the shipped path bits, whether the post-release lock's bits are zero, and the signed
/// change into them. The program's colour output then carries them instead of the colour: the path word in red, the
/// change's 32 bits in green, blue and alpha, 11, 11 and 10 bits, whole numbers a half float holds exactly. The colour
/// is still computed, so the program binds what production binds, and the state output is untouched, so a rig running
/// it still writes production's locks, which the A/B fact checks. The counts are read from that rewrite, never from the
/// production program itself, and the lock equality is what ties the two.
/// <see cref="RewriteKeepingTheDecision"/> leaves the visibility functions as production ships them and changes only
/// the call site and the colour line, so its path words, less the slow path's reasons, must equal the full export's.
/// <see cref="Add"/> counts a frame's pixels by path.
/// </summary>
internal sealed class TemporalVisibilityProductionPaths
{
    /// <summary>The path word: the shipped bits, the slow path's reason, the call site reached and a lock whose bits
    /// are zero there, which the call site's first test (R1) gates off.</summary>
    internal const uint Fast = 1, Slow = 2, RangeSkip = 4, Computed = 8, Edge = 16, Deviation = 32, Border = 64,
        Reached = 128, LockZero = 256;

    /// <summary>The slow path's reason bits, which only <see cref="Rewrite"/> sets.</summary>
    internal const uint Reasons = Edge | Deviation | Border;

    // Anchors stop before their line ending, so a CRLF checkout of the shader sources matches too.
    const string SlowLine = "        path = VisibilitySlowPath;";
    const string SlowWithReason = "        path = VisibilitySlowPath | (any(equal(centre, ivec2(0))) || any(equal(centre, last))"
        + " ? 16u\n            : base0 == base1 - 1 && base2 == base1 + 1 ? 64u : 32u);";
    const string CallSiteLine = "    if (visibilityReleased) lockValue = 0.0;";
    const string ColourLine = "    oColor = vec4(p.color, p.alpha + vUv.x * 1.0e-30);";
    const string Globals = "\nuint visibilityPathForTests = 0u;\nfloat visibilityChangeForTests = 0.0;\n";

    /// <summary>A production program with the path export, or the program unchanged where it runs no visibility
    /// decision. Throws where an anchor is missing or repeated.</summary>
    internal static string Rewrite(string program) => Export(program, reasons: true);

    /// <summary>The path export without the slow path's reasons: the visibility functions stay production's text, and
    /// only the globals, the call site's copy and the colour line are added.</summary>
    internal static string RewriteKeepingTheDecision(string program) => Export(program, reasons: false);

    static string Export(string program, bool reasons)
    {
        if (!program.Contains(ShaderSources.TemporalVisibilityGlsl, StringComparison.Ordinal)) return program;
        string visibility = ShaderSources.TemporalVisibilityGlsl;
        Once(visibility, SlowLine);
        Once(program, ShaderSources.TemporalVisibilityGlsl);
        Once(program, CallSiteLine);
        Once(program, ColourLine);
        string rewritten = program.Replace(ShaderSources.TemporalVisibilityGlsl, Globals
            + (reasons ? visibility.Replace(SlowLine, SlowWithReason, StringComparison.Ordinal) : visibility),
            StringComparison.Ordinal);
        string reached = Reached.ToString(CultureInfo.InvariantCulture), lockZero = LockZero.ToString(CultureInfo.InvariantCulture);
        rewritten = rewritten.Replace(CallSiteLine, "    visibilityPathForTests = visibilityPath | " + reached
            + "u | (floatBitsToUint(lockValue) == 0u ? " + lockZero + "u : 0u);\n"
            + "    visibilityChangeForTests = signedVisibilityChange;\n" + CallSiteLine, StringComparison.Ordinal);
        // The colour stays computed, so the program binds every resource production binds: a channel larger than
        // 3e38 never occurs, and the half it would add truncates away in the decode.
        return rewritten.Replace(ColourLine, "    uint changeBitsForTests = floatBitsToUint(visibilityChangeForTests);\n"
            + "    float keepForTests = any(greaterThan(abs(p.color), vec3(3.0e38))) || abs(p.alpha) > 3.0e38 ? 0.5 : 0.0;\n"
            + "    oColor = vec4(float(visibilityPathForTests) + keepForTests, float(changeBitsForTests & 2047u),\n"
            + "        float((changeBitsForTests >> 11) & 2047u), float(changeBitsForTests >> 22) + vUv.x * 1.0e-30);",
            StringComparison.Ordinal);
    }

    static void Once(string text, string part)
    {
        int first = text.IndexOf(part, StringComparison.Ordinal);
        if (first < 0 || text.IndexOf(part, first + part.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"the path export expects one \"{part.Trim()}\"");
    }

    /// <summary>The path word one exported pixel carries.</summary>
    internal static uint PathAt(float[] colour, int pixel) => (uint)colour[pixel * 4];

    /// <summary>The signed change one exported pixel carries.</summary>
    internal static float ChangeAt(float[] colour, int pixel) => BitConverter.UInt32BitsToSingle((uint)colour[pixel * 4 + 1]
        | ((uint)colour[pixel * 4 + 2] << 11) | ((uint)colour[pixel * 4 + 3] << 22));

    internal long Pixels, NotReached, GatedByLock, GatedOtherwise, FastPath, SlowPath, SlowEdge, SlowDeviation,
        SlowBorder, RangeSkips, ChangeComputed, FastComputed, Released, FastReleased, FastNearThresholdAbove,
        FastNearThresholdBelow;

    /// <summary>Pixels the call site reached and gated off before the decision, by the lock or otherwise.</summary>
    internal long Gated => GatedByLock + GatedOtherwise;

    /// <summary>Fast-path pixels that neither skipped by range nor computed a change: no accepted texel either side.</summary>
    internal long FastWithoutChange => FastPath - RangeSkips - FastComputed;

    /// <summary>Counts one frame's display pixels from the exported colour, four floats a pixel.</summary>
    internal void Add(float[] colour)
    {
        for (int p = 0; p < colour.Length / 4; p++) AddPixel(colour, p);
    }

    /// <summary>Counts one display pixel of the exported colour.</summary>
    internal void AddPixel(float[] colour, int p)
    {
        Pixels++;
        uint path = PathAt(colour, p);
        if ((path & Reached) == 0) { NotReached++; return; }
        if ((path & (Fast | Slow)) == 0)
        {
            if ((path & LockZero) != 0) GatedByLock++;
            else GatedOtherwise++;
            return;
        }
        bool fast = (path & Fast) != 0;
        if (fast) FastPath++;
        else
        {
            SlowPath++;
            if ((path & Edge) != 0) SlowEdge++;
            else if ((path & Deviation) != 0) SlowDeviation++;
            else if ((path & Border) != 0) SlowBorder++;
        }
        if ((path & RangeSkip) != 0) RangeSkips++;
        if ((path & Computed) == 0) return;
        ChangeComputed++;
        float magnitude = MathF.Abs(ChangeAt(colour, p)), threshold = TemporalResolveTuning.VisibilityReleaseThreshold;
        if (magnitude > threshold) Released++;
        if (!fast) return;
        FastComputed++;
        if (magnitude > threshold) FastReleased++;
        if (MathF.Abs(magnitude - threshold) <= 2.384185791015625e-7f)
        {
            if (magnitude > threshold) FastNearThresholdAbove++;
            else FastNearThresholdBelow++;
        }
    }

    public override string ToString() => $"{Pixels} pixels: {NotReached} not reaching the call site, gated before the "
        + $"decision {GatedByLock} by a zero lock and {GatedOtherwise} otherwise, fast {FastPath} (range skip "
        + $"{RangeSkips}, change computed {FastComputed}, released {FastReleased}, within 2^-22 of 0.1 above "
        + $"{FastNearThresholdAbove} and below {FastNearThresholdBelow}), slow {SlowPath} (at an edge {SlowEdge}, on a "
        + $"base deviation {SlowDeviation}, at the border {SlowBorder}), change computed {ChangeComputed}, released "
        + $"{Released}";
}
