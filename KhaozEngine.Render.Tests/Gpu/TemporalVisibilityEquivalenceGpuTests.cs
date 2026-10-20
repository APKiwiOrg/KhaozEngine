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
/// THE PRODUCTION VISIBILITY DECISION IS D'S, CASE FOR CASE (plan Task 7f.1, proof 2). One program runs frozen D and
/// the production decision on millions of cases at the real internal sizes: 2560x1440 (Native at 1440p), 1707x960
/// (Quality at 1440p) and 3342x2160 (the capped Native under 3456x2234), with consecutive Halton jitter, the Halton
/// wrap and zero jitter. Every release decision must match, every validity must match unless the production side
/// reports a range skip, and the signed change must match bit for bit wherever both computed it.
/// <para>The coverage counts say the cases met what the cost cut can break: edge and corner centres, fractions on and
/// near integers, non-finite and background depths, depths exactly at and one ulp either side of both tolerances,
/// changes within ulps of 0.1 on both sides, and R4 bounds both sides of the <c>0.1 - 2^-13</c> margin. The
/// classifier's predicted paths are independent of production, and the shipped decision must report each case's
/// predicted path: fast or slow, and the range skip. No predicted R4 skip may meet a D release.</para>
/// </summary>
public sealed class TemporalVisibilityEquivalenceGpuTests(ITestOutputHelper output)
{
    const int HardwareTarget = 1024, SoftwareTarget = 256;

    // Input and predicted-path coverage, per internal size over its three draws of a hardware-sized target. Software
    // adapters draw a smaller target and scale these down, never below one.
    static readonly (Flag Flag, int Minimum)[] Coverage =
    [
        (Flag.DValid, 100_000), (Flag.DRelease, 10_000), (Flag.EdgeCentre, 10_000), (Flag.CornerCentre, 1_000),
        (Flag.NearIntegerFraction, 10_000), (Flag.NonFinite, 1_000), (Flag.Background, 1_000),
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

    enum Path { Fast, SlowEdge, SlowDeviation, SlowBorder, RangeSkip }

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
            (uint[] flags, float[] dChange, float[] newChange) = run.Run(textures,
                new Vector2(displayWidth, displayHeight), draws[d].Jitter, draws[d].Previous, d + 1);
            var counts = new long[bitsCount];
            for (int i = 0; i < flags.Length; i++)
            {
                for (int b = 0; b < bitsCount; b++) if ((flags[i] & (1u << b)) != 0) counts[b]++;
                CountPaths(flags[i], paths);
                const uint Wrong = (1u << (int)Flag.DecisionMismatch) | (1u << (int)Flag.ValidityMismatch)
                    | (1u << (int)Flag.ChangeMismatch);
                if ((flags[i] & Wrong) != 0 && mismatches.Count < 16)
                    mismatches.Add($"{draws[d].Name} case ({i % run.Size}, {i / run.Size}): flags 0x{flags[i]:x8}, "
                        + $"D change {dChange[i]:R} (0x{BitConverter.SingleToUInt32Bits(dChange[i]):x8}), production "
                        + $"{newChange[i]:R} (0x{BitConverter.SingleToUInt32Bits(newChange[i]):x8})");
            }
            output.WriteLine($"  {draws[d].Name}, jitter ({draws[d].Jitter.X:R}, {draws[d].Jitter.Y:R}) after "
                + $"({draws[d].Previous.X:R}, {draws[d].Previous.Y:R}): {Describe(counts)}");
            for (int b = 0; b < bitsCount; b++) totals[b] += counts[b];
            cases += flags.Length;
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
            + totals[(int)Flag.PredictedSlowDeviation] + totals[(int)Flag.PredictedSlowBorder]);
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

    // One case's reported path, the slow path split by the classifier's reason.
    static void CountPaths(uint flags, long[] paths)
    {
        static bool Has(uint flags, Flag flag) => (flags & (1u << (int)flag)) != 0;
        if (Has(flags, Flag.ReportedFast)) paths[(int)Path.Fast]++;
        if (Has(flags, Flag.ReportedRangeSkip)) paths[(int)Path.RangeSkip]++;
        if (!Has(flags, Flag.ReportedSlow)) return;
        if (Has(flags, Flag.PredictedSlowEdge)) paths[(int)Path.SlowEdge]++;
        else if (Has(flags, Flag.PredictedSlowDeviation)) paths[(int)Path.SlowDeviation]++;
        else if (Has(flags, Flag.PredictedSlowBorder)) paths[(int)Path.SlowBorder]++;
    }

    static string Describe(long[] counts) => string.Join(", ",
        Enum.GetValues<Flag>().Select(f => $"{f} {counts[(int)f]}"));
}
