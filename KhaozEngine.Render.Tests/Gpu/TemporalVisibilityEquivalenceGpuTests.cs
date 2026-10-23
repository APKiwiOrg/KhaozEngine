using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;
using Flag = KhaozEngine.Tests.Gpu.TemporalVisibilityEquivalenceProbe.Flag;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// THE PRODUCTION VISIBILITY DECISION IS D'S, CASE FOR CASE (plan Task 7f.1, proof 2). Frozen D and the production
/// decision, each compiled as its own program so no compiler can share a value between them, run on millions of
/// stored cases at the real internal sizes: 2560x1440 (Native at 1440p), 1707x960
/// (Quality at 1440p) and 3342x2160 (the capped Native under 3456x2234), with consecutive Halton jitter, the Halton
/// wrap and zero jitter. Every release decision must match, every validity must match unless the production side
/// reports a range skip, and the signed change must match bit for bit wherever both computed it.
/// <para>The coverage counts say the cases met what the cost cut can break: edge and corner centres, fractions on and
/// near integers, NaN and both infinities apart, fast-path cases meeting a non-finite neighbour depth and a non-finite
/// stored depth, background depths, depths exactly at and one ulp either side of both tolerances,
/// changes within ulps of 0.1 on both sides, and R4 bounds both sides of the <c>0.1 - 2^-13</c> margin. The
/// classifier's predicted paths are independent of production, and the shipped decision must report each case's
/// predicted path: fast or slow, and the range skip. No predicted R4 skip may meet a D release.</para>
/// <para>The binade cases (fix round 7f r3) centre every case beside or on a power-of-two column, row or both, up to
/// 2048, under jitter of each sign on each axis, zero, a denormal's neighbour and just under one half. A block that
/// fits must take D's path exactly where an axis with positive jitter holds a power of two, and the fast path
/// otherwise, with the same zero mismatches. The shipped decision with the rule written out runs beside it, and the
/// fact reports, without asserting, how often that one differs from D on the device.</para>
/// </summary>
public sealed class TemporalVisibilityEquivalenceGpuTests(ITestOutputHelper output)
{
    const int HardwareTarget = 1024, SoftwareTarget = 256;

    // Input and predicted-path coverage, per internal size over its three draws of a hardware-sized target. Software
    // adapters draw a smaller target and scale these down, never below one.
    static readonly (Flag Flag, int Minimum)[] Coverage =
    [
        (Flag.DValid, 100_000), (Flag.DRelease, 10_000), (Flag.EdgeCentre, 10_000), (Flag.CornerCentre, 1_000),
        (Flag.NearIntegerFraction, 10_000), (Flag.NaN, 1_000), (Flag.PositiveInfinity, 1_000),
        (Flag.NegativeInfinity, 1_000), (Flag.FastNonFiniteNeighbour, 1_000), (Flag.FastNonFiniteStored, 1_000),
        (Flag.Background, 1_000),
        (Flag.CurrentToleranceExact, 100), (Flag.CurrentToleranceUlp, 100), (Flag.PreviousToleranceExact, 100),
        (Flag.PreviousToleranceUlp, 100), (Flag.NearThresholdAbove, 16), (Flag.NearThresholdBelow, 16),
        (Flag.EmptyHistory, 1_000), (Flag.PredictedFast, 100_000), (Flag.PredictedSlowEdge, 10_000),
        (Flag.PredictedSlowDeviation, 1_000), (Flag.PredictedSlowBorder, 1_000), (Flag.PredictedRangeSkip, 10_000),
        (Flag.RangeSkipNearMarginBelow, 16), (Flag.RangeSkipNearMarginAbove, 16),
    ];

    // The paths the shipped decision reports, per internal size over its three draws of a hardware-sized target and
    // scaled like the coverage above: the fast path, the slow path for each of its three reasons, and the range skip.
    // Every reported path must also equal the classifier's prediction case for case. Metal on 7f.2 reported, over the
    // three sizes, fast 2516372 to 2572817, slow at an edge 423174 to 425119, on a base deviation 101946 to 166744,
    // at the border 39438 to 45846 and range skips 250383 to 255408. The minimums sit under half of those.
    static readonly (Path Path, int Minimum)[] Reported =
    [
        (Path.Fast, 1_000_000), (Path.SlowEdge, 100_000), (Path.SlowDeviation, 50_000), (Path.SlowBorder, 15_000),
        (Path.RangeSkip, 100_000),
    ];

    enum Path { Fast, SlowEdge, SlowDeviation, SlowBorder, SlowBinade, RangeSkip }

    public static TheoryData<int, int, int, int> Sizes => new()
    {
        { 2560, 1440, 2560, 1440 },
        { 1707, 960, 2560, 1440 },
        { 3342, 2160, 3456, 2234 },
    };

    [GpuTheory]
    [MemberData(nameof(Sizes))]
    public void ProductionDecisionsMatchFrozenDOnEveryCase(int width, int height, int displayWidth, int displayHeight)
    {
        using var run = new TemporalVisibilityEquivalenceProbe(HardwareTarget, SoftwareTarget);
        output.WriteLine($"{run.Device.Backend} on {run.Device.Capabilities.DeviceName}, {width}x{height} internal, "
            + $"{displayWidth}x{displayHeight} display, {run.Size}x{run.Size} cases a draw");
        var inputs = new TemporalVisibilityEquivalenceInputs(width, height, (ulong)(width * 7919 + height));
        output.WriteLine($"  tiles: {string.Join(", ", Enum.GetValues<TemporalVisibilityEquivalenceInputs.Kind>()
            .Select(k => $"{k} {inputs.KindCounts[(int)k]}"))}");
        using var textures = new TemporalVisibilityEquivalenceProbe.Textures(run.Device, inputs);

        float ratio = TemporalResolveMath.DisplayOverInternal(displayWidth, displayHeight, width, height);
        int phases = TemporalJitter.PhaseCount(ratio);
        (string Name, Vector2 Jitter, Vector2 Previous)[] draws =
        [
            ("Halton phase 3 after 2", TemporalJitter.Offset(3, phases), TemporalJitter.Offset(2, phases)),
            ("Halton wrap", TemporalJitter.Offset(0, phases), TemporalJitter.Offset(phases - 1, phases)),
            ("zero jitter", Vector2.Zero, Vector2.Zero),
        ];
        int bitsCount = Enum.GetValues<Flag>().Length;
        var totals = new long[bitsCount];
        var paths = new long[Enum.GetValues<Path>().Length];
        long cases = 0;
        var mismatches = new List<string>();
        for (int d = 0; d < draws.Length; d++)
        {
            TemporalVisibilityEquivalenceProbe.Outputs outputs = run.Run(textures,
                new Vector2(displayWidth, displayHeight), draws[d].Jitter, draws[d].Previous, d + 1);
            var counts = new long[bitsCount];
            for (int i = 0; i < outputs.Centre.Length; i++)
            {
                ulong flags = TemporalVisibilityEquivalenceProbe.Combine(outputs, i);
                for (int b = 0; b < bitsCount; b++) if ((flags & (1UL << b)) != 0) counts[b]++;
                CountPaths(flags, paths);
                const ulong Wrong = (1UL << (int)Flag.DecisionMismatch) | (1UL << (int)Flag.ValidityMismatch)
                    | (1UL << (int)Flag.ChangeMismatch);
                if ((flags & Wrong) != 0 && mismatches.Count < 16)
                    mismatches.Add($"{draws[d].Name} case ({i % run.Size}, {i / run.Size}): flags 0x{flags:x10}, "
                        + $"D change {outputs.DChange[i]:R} (0x{BitConverter.SingleToUInt32Bits(outputs.DChange[i]):x8}), "
                        + $"production {outputs.NewChange[i]:R} "
                        + $"(0x{BitConverter.SingleToUInt32Bits(outputs.NewChange[i]):x8})");
            }
            output.WriteLine($"  {draws[d].Name}, jitter ({draws[d].Jitter.X:R}, {draws[d].Jitter.Y:R}) after "
                + $"({draws[d].Previous.X:R}, {draws[d].Previous.Y:R}): {Describe(counts)}");
            for (int b = 0; b < bitsCount; b++) totals[b] += counts[b];
            cases += outputs.Centre.Length;
        }
        output.WriteLine($"  all {cases} cases: {Describe(totals)}");
        output.WriteLine($"  reported paths: {string.Join(", ", Enum.GetValues<Path>().Select(p => $"{p} {paths[(int)p]}"))}");
        foreach (string mismatch in mismatches) output.WriteLine("  MISMATCH " + mismatch);

        Assert.Equal(cases, totals[(int)Flag.Written]);
        Assert.True(totals[(int)Flag.DecisionMismatch] == 0, $"{totals[(int)Flag.DecisionMismatch]} release decisions differ from D");
        Assert.True(totals[(int)Flag.ValidityMismatch] == 0, $"{totals[(int)Flag.ValidityMismatch]} validities differ from D");
        Assert.True(totals[(int)Flag.ChangeMismatch] == 0, $"{totals[(int)Flag.ChangeMismatch]} changes differ from D's bits");
        Assert.True(totals[(int)Flag.RangeSkipMeetsRelease] == 0,
            $"{totals[(int)Flag.RangeSkipMeetsRelease]} predicted R4 skips meet a D release");

        // Every case takes exactly one predicted path.
        Assert.Equal(cases, totals[(int)Flag.PredictedFast] + totals[(int)Flag.PredictedSlowEdge]
            + totals[(int)Flag.PredictedSlowDeviation] + totals[(int)Flag.PredictedSlowBorder]
            + totals[(int)Flag.PredictedSlowBinade]);
        double scale = cases / (3.0 * HardwareTarget * HardwareTarget);
        var thin = Coverage.Where(c => totals[(int)c.Flag] < Math.Max(1, (long)(c.Minimum * scale)))
            .Select(c => $"{c.Flag} {totals[(int)c.Flag]} under {Math.Max(1, (long)(c.Minimum * scale))}").ToArray();
        Assert.True(thin.Length == 0, "coverage too thin: " + string.Join(", ", thin));

        // The reported paths: each the predicted one, every case on exactly one of fast and slow, and each path met.
        Assert.True(totals[(int)Flag.ReportedPathMismatch] == 0,
            $"{totals[(int)Flag.ReportedPathMismatch]} reported paths differ from the predicted ones");
        Assert.Equal(cases, totals[(int)Flag.ReportedFast] + totals[(int)Flag.ReportedSlow]);
        Assert.Equal(totals[(int)Flag.PredictedFast], totals[(int)Flag.ReportedFast]);
        Assert.Equal(totals[(int)Flag.PredictedRangeSkip], totals[(int)Flag.ReportedRangeSkip]);
        var unmet = Reported.Where(r => paths[(int)r.Path] < Math.Max(1, (long)(r.Minimum * scale)))
            .Select(r => $"{r.Path} {paths[(int)r.Path]} under {Math.Max(1, (long)(r.Minimum * scale))}").ToArray();
        Assert.True(unmet.Length == 0, "reported paths too thin: " + string.Join(", ", unmet));
    }

    // The binade draws' jitter, each against a previous jitter of the other signs: every sign pair, zero, the float
    // after zero and the float before one half.
    static readonly (string Name, Vector2 Jitter, Vector2 Previous)[] BinadeDraws =
    [
        ("(+, +)", new(0.3125f, 0.2109375f), new(-0.125f, -0.375f)),
        ("(+, -)", new(0.1875f, -0.4375f), new(-0.25f, 0.125f)),
        ("(-, +)", new(-0.375f, 0.0625f), new(0.25f, -0.125f)),
        ("(-, -)", new(-0.0625f, -0.2890625f), new(0.375f, 0.3125f)),
        ("zero", Vector2.Zero, Vector2.Zero),
        ("smallest positive x, largest y", new(float.Epsilon, BitConverter.Int32BitsToSingle(0x3EFFFFFF)),
            new(-0.25f, 0f)),
    ];

    [GpuTheory]
    [MemberData(nameof(Sizes))]
    public void BinadeCasesTakeDsPathUnderPositiveJitterAndTheFastPathOtherwise(int width, int height, int displayWidth,
        int displayHeight)
    {
        using var run = new TemporalVisibilityEquivalenceProbe(HardwareTarget, SoftwareTarget);
        output.WriteLine($"{run.Device.Backend} on {run.Device.Capabilities.DeviceName}, {width}x{height} internal, "
            + $"{displayWidth}x{displayHeight} display, {run.Size}x{run.Size} binade cases a draw");
        var inputs = new TemporalVisibilityEquivalenceInputs(width, height, (ulong)(width * 7919 + height));
        using var textures = new TemporalVisibilityEquivalenceProbe.Textures(run.Device, inputs);
        // The powers of two the case pass places per axis: up to the last texel but one.
        int[] lines = [Lines(width), Lines(height)];
        int bitsCount = Enum.GetValues<Flag>().Length;
        long cases = 0, wrong = 0, pathMismatch = 0, unruledDecisions = 0, unruledChanges = 0, unruledChecked = 0;
        var mismatches = new List<string>();
        for (int d = 0; d < BinadeDraws.Length; d++)
        {
            (string name, Vector2 jitter, Vector2 previous) = BinadeDraws[d];
            TemporalVisibilityEquivalenceProbe.Outputs outputs = run.Run(textures,
                new Vector2(displayWidth, displayHeight), jitter, previous, 11 + d, binade: true);
            var counts = new long[bitsCount];
            // Per power of two up to 2048, by axis: cases sent to D's path by it, and fast-path cases beside it.
            var sent = new long[2, 12];
            var kept = new long[2, 12];
            long drawUnruledDecisions = 0, drawUnruledChanges = 0;
            for (int i = 0; i < outputs.Centre.Length; i++)
            {
                ulong flags = TemporalVisibilityEquivalenceProbe.Combine(outputs, i);
                for (int b = 0; b < bitsCount; b++) if ((flags & (1UL << b)) != 0) counts[b]++;
                const ulong Wrong = (1UL << (int)Flag.DecisionMismatch) | (1UL << (int)Flag.ValidityMismatch)
                    | (1UL << (int)Flag.ChangeMismatch);
                if ((flags & Wrong) != 0) wrong++;
                if ((flags & (1UL << (int)Flag.ReportedPathMismatch)) != 0) pathMismatch++;
                if ((flags & (Wrong | (1UL << (int)Flag.ReportedPathMismatch))) != 0 && mismatches.Count < 16)
                    mismatches.Add($"{name} case ({i % run.Size}, {i / run.Size}) centre {outputs.Centre[i]:R}: flags "
                        + $"0x{flags:x10}, D change 0x{BitConverter.SingleToUInt32Bits(outputs.DChange[i]):x8}, production "
                        + $"0x{BitConverter.SingleToUInt32Bits(outputs.NewChange[i]):x8}");
                int packed = (int)outputs.Centre[i];
                (int cx, int cy) = (packed % 4096, packed / 4096);
                bool binade = (flags & (1UL << (int)Flag.ReportedSlowBinade)) != 0;
                bool fast = (flags & (1UL << (int)Flag.ReportedFast)) != 0;
                for (int axis = 0; axis < 2; axis++)
                {
                    int centre = axis == 0 ? cx : cy;
                    float axisJitter = axis == 0 ? jitter.X : jitter.Y;
                    for (int k = 0; k < 12; k++)
                    {
                        if (Math.Abs(centre - (1 << k)) > 1) continue;
                        if (binade && TemporalVisibilityFastPath.SlowForBinade(1 << k, axisJitter)) sent[axis, k]++;
                        if (fast) kept[axis, k]++;
                    }
                }
                (bool decision, bool change) = TemporalVisibilityEquivalenceProbe.UnruledApart(outputs, i);
                if (decision) drawUnruledDecisions++;
                if (change) drawUnruledChanges++;
            }
            unruledDecisions += drawUnruledDecisions;
            unruledChanges += drawUnruledChanges;
            unruledChecked += outputs.Centre.Length;
            cases += outputs.Centre.Length;
            output.WriteLine($"  {name}, jitter ({jitter.X:R}, {jitter.Y:R}): fast {counts[(int)Flag.ReportedFast]}, "
                + $"binade {counts[(int)Flag.ReportedSlowBinade]}, edge {counts[(int)Flag.PredictedSlowEdge]}, deviation "
                + $"{counts[(int)Flag.PredictedSlowDeviation]}, border {counts[(int)Flag.PredictedSlowBorder]}, range skip "
                + $"{counts[(int)Flag.ReportedRangeSkip]}, D releases {counts[(int)Flag.DRelease]}");
            output.WriteLine($"    sent to D's path by x 1 to 2048: {Row(sent, 0)}, by y: {Row(sent, 1)}");
            output.WriteLine($"    kept fast beside x 1 to 2048: {Row(kept, 0)}, beside y: {Row(kept, 1)}");
            output.WriteLine($"    DIAGNOSTIC, not asserted: without the binade rule {drawUnruledDecisions} decisions and "
                + $"{drawUnruledChanges} change bits differ from D");

            Assert.Equal(outputs.Centre.Length, counts[(int)Flag.Written]);
            // A positive axis sends every fitting block beside one of its powers of two to D's path, and a negative or
            // zero one keeps them fast, line by line on the axes the size holds.
            for (int axis = 0; axis < 2; axis++)
            {
                float axisJitter = axis == 0 ? jitter.X : jitter.Y;
                for (int k = 0; k < lines[axis]; k++)
                {
                    long floor = run.Size >= HardwareTarget ? 100 : 4;
                    if (BitConverter.SingleToInt32Bits(axisJitter) > 0)
                        Assert.True(sent[axis, k] >= floor, $"{name}: {sent[axis, k]} cases sent to D's path by "
                            + $"{"xy"[axis]} = {1 << k}, under {floor}");
                    else
                        Assert.True(kept[axis, k] >= floor, $"{name}: {kept[axis, k]} fast-path cases beside "
                            + $"{"xy"[axis]} = {1 << k}, under {floor}");
                }
            }
            if (jitter.X <= 0f && jitter.Y <= 0f)
                Assert.True(counts[(int)Flag.ReportedSlowBinade] == 0, $"{name}: a draw without positive jitter sent "
                    + $"{counts[(int)Flag.ReportedSlowBinade]} cases to D's path for a binade");
        }
        output.WriteLine($"  all {cases} cases: {wrong} differ from D, {pathMismatch} report another path than predicted");
        output.WriteLine($"  DIAGNOSTIC, not asserted: without the binade rule, over {unruledChecked} cases, "
            + $"{unruledDecisions} decisions and {unruledChanges} change bits differ from D on this device");
        foreach (string mismatch in mismatches) output.WriteLine("  MISMATCH " + mismatch);
        Assert.True(wrong == 0, $"{wrong} binade cases differ from D");
        Assert.True(pathMismatch == 0, $"{pathMismatch} binade cases report another path than predicted");
    }

    static int Lines(int size)
    {
        int lines = 0;
        for (int p = 1; p <= size - 2; p *= 2) lines++;
        return lines;
    }

    static string Row(long[,] counts, int axis) => string.Join(" ", Enumerable.Range(0, 12).Select(k => counts[axis, k]));

    // One case's reported path, the slow path split by the classifier's reason.
    static void CountPaths(ulong flags, long[] paths)
    {
        static bool Has(ulong flags, Flag flag) => (flags & (1UL << (int)flag)) != 0;
        if (Has(flags, Flag.ReportedFast)) paths[(int)Path.Fast]++;
        if (Has(flags, Flag.ReportedRangeSkip)) paths[(int)Path.RangeSkip]++;
        if (!Has(flags, Flag.ReportedSlow)) return;
        if (Has(flags, Flag.PredictedSlowEdge)) paths[(int)Path.SlowEdge]++;
        else if (Has(flags, Flag.PredictedSlowDeviation)) paths[(int)Path.SlowDeviation]++;
        else if (Has(flags, Flag.PredictedSlowBorder)) paths[(int)Path.SlowBorder]++;
        else if (Has(flags, Flag.PredictedSlowBinade)) paths[(int)Path.SlowBinade]++;
    }

    static string Describe(long[] counts) => string.Join(", ",
        Enum.GetValues<Flag>().Select(f => $"{f} {counts[(int)f]}"));
}
