using System;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

// Only llvmpipe's continuous record takes a software arithmetic bound. Existing stored state never does.
internal sealed class TemporalEntryStateComparison(bool softwareVulkan)
{
    internal RecordChannel Drift { get; } = new();
    internal RecordChannel Swing { get; } = new();

    internal void Compare(int frame, string pair, float[] a, float[] b)
    {
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(0, a.Length % 4);
        if (!softwareVulkan)
        {
            TemporalHistorySample.Exact(frame, pair, a, b);
            return;
        }
        for (int i = 0; i < a.Length; i += 4)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                uint left = BitConverter.SingleToUInt32Bits(a[i + channel]);
                uint right = BitConverter.SingleToUInt32Bits(b[i + channel]);
                if (left != right)
                    Assert.Fail($"frame {frame}: {pair} {(channel == 0 ? "confidence" : "stored lock and mark")} "
                        + $"differs at texel {i / 4}: {a[i + channel]:R} (0x{left:X8}) vs "
                        + $"{b[i + channel]:R} (0x{right:X8})");
            }
            Drift.Add(frame, i / 4, a[i + 2], b[i + 2]);
            Swing.Add(frame, i / 4, a[i + 3], b[i + 3]);
        }
    }

    internal void AssertComplete(ITestOutputHelper output, string pair)
    {
        if (!softwareVulkan) return;
        Report(output, pair);
        AssertWithinBound(pair);
    }

    internal void Report(ITestOutputHelper output, string pair)
    {
        if (!softwareVulkan) return;
        Channel("drift", Drift);
        Channel("swing", Swing);

        void Channel(string channel, RecordChannel difference)
            => output.WriteLine(FormattableString.Invariant(
                $"SHIMMER-IDENTITY|{pair}|{channel}|{difference.WorstHalfSteps}|{difference.Differing}|{difference.Compared}|{difference.Share:R}|{difference.WorstLocation}"));
    }

    internal void AssertWithinBound(string pair)
    {
        if (!softwareVulkan) return;
        Assert.True(Allowed(Drift) && Allowed(Swing),
            $"{pair}: software Vulkan record left its bound ({TemporalSoftwareRecordDivergenceRule.HalfStepBound} "
            + $"half steps, {TemporalSoftwareRecordDivergenceRule.DifferingShareBound:P3} of values). "
            + $"Drift {Drift}. Swing {Swing}");

        static bool Allowed(RecordChannel difference) => TemporalSoftwareRecordDivergenceRule.Allows(
            difference.WorstHalfSteps, difference.Differing, difference.Compared,
            TemporalSoftwareRecordDivergenceRule.HalfStepBound, TemporalSoftwareRecordDivergenceRule.DifferingShareBound);
    }

    internal sealed class RecordChannel
    {
        internal long Compared, Differing;
        internal int WorstHalfSteps;
        internal string WorstLocation = "none";
        internal double Share => Compared > 0 ? TemporalSoftwareCompileDivergenceRule.ShareOf(Differing, Compared) : 0;

        internal void Add(int frame, int texel, float a, float b)
        {
            Compared++;
            if (!float.IsFinite(a) || !float.IsFinite(b))
            {
                WorstHalfSteps = int.MaxValue;
                WorstLocation = FormattableString.Invariant($"non-finite f{frame} texel {texel}: {a:R}/{b:R}");
            }
            if (BitConverter.SingleToUInt32Bits(a) == BitConverter.SingleToUInt32Bits(b)) return;
            Differing++;
            if (WorstHalfSteps == int.MaxValue) return;
            int steps = TemporalHistoryDifference.HalfSteps(a, b);
            if (Differing == 1 || steps > WorstHalfSteps)
                WorstLocation = FormattableString.Invariant($"f{frame} texel {texel}: {a:R}/{b:R}");
            WorstHalfSteps = Math.Max(WorstHalfSteps, steps);
        }

        public override string ToString() => FormattableString.Invariant(
            $"{Differing}/{Compared} differ, share {Share:R}, worst {WorstHalfSteps} half steps, {WorstLocation}");
    }
}

internal static class TemporalSoftwareRecordDivergenceRule
{
    // Calibration gate. Keep exact until the complete hosted walks supply record-sized bounds.
    internal const int HalfStepBound = 0;
    internal const double DifferingShareBound = 0;

    internal static bool Allows(int worstHalfSteps, long differing, long compared, int stepBound, double shareBound)
        => worstHalfSteps >= 0 && worstHalfSteps < int.MaxValue && worstHalfSteps <= stepBound
            && compared > 0 && differing >= 0 && differing <= compared
            && TemporalSoftwareCompileDivergenceRule.ShareOf(differing, compared) <= shareBound;
}
