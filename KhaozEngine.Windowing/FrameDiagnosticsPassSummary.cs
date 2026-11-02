using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Windowing;

internal sealed record FrameDiagnosticsPassDistribution(string Kind, int SignatureId,
    FrameDiagnosticsDistribution VertexMs, FrameDiagnosticsDistribution FragmentMs,
    FrameDiagnosticsDistribution EncoderMs);

/// <summary>One discard state's frames and directional encoders. Blocks decide the state, so a discard-on encoder that
/// kept Store because a blit split the pass still counts as on. Each record's storeResolution separates them.</summary>
internal sealed record FrameDiagnosticsDiscardState(bool Discard, FrameDiagnosticsDistribution FrameCompletionIntervalMs,
    FrameDiagnosticsDistribution DirectionalVertexMs, FrameDiagnosticsDistribution DirectionalFragmentMs,
    FrameDiagnosticsDistribution DirectionalEncoderMs);

internal static class FrameDiagnosticsPassSummary
{
    internal static FrameDiagnosticsPassDistribution[] Encoders(PassTimingSnapshot passes)
    {
        var output = new List<FrameDiagnosticsPassDistribution>();
        foreach (var group in passes.Encoders.Records.GroupBy(e => (e.Kind, e.SignatureId)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.SignatureId))
        {
            var (vertex, fragment, whole) = Spans(group, passes);
            output.Add(new(group.Key.Kind, group.Key.SignatureId, vertex, fragment, whole));
        }
        return output.ToArray();
    }

    internal static FrameDiagnosticsDiscardState[] DiscardStates(FrameTimingSnapshot timing, PassTimingSnapshot passes,
        PassExperimentSnapshot experiment)
    {
        var blocks = experiment.Blocks;
        var (on, off) = IntervalsBy(timing, blocks.Select(b => (b.StartCallback, b.EndCallbackExclusive, b.Discard)));
        return [State(false, off), State(true, on)];

        FrameDiagnosticsDiscardState State(bool discard, FrameDiagnosticsDistribution intervals)
        {
            var (vertex, fragment, whole) = Spans(passes.Encoders.Records.Where(e => e.DirectionalScope
                && blocks.Any(b => b.Discard == discard && e.CallbackIndex >= b.StartCallback && e.CallbackIndex < b.EndCallbackExclusive)), passes);
            return new(discard, intervals, vertex, fragment, whole);
        }
    }

    internal static (FrameDiagnosticsDistribution Vertex, FrameDiagnosticsDistribution Fragment, FrameDiagnosticsDistribution Whole) Spans(
        IEnumerable<PassTimingEncoder> encoders, PassTimingSnapshot passes)
    {
        var vertex = new List<double>();
        var fragment = new List<double>();
        var whole = new List<double>();
        foreach (var encoder in encoders)
        {
            if (encoder.Status is "resolveFailure" or "encoderUnavailable") continue;
            if (encoder.Kind == "render")
            {
                Add(vertex, encoder.StartVertex, encoder.EndVertex, passes);
                Add(fragment, encoder.StartFragment, encoder.EndFragment, passes);
                if (Valid(encoder.StartVertex, encoder.EndVertex) && Valid(encoder.StartFragment, encoder.EndFragment))
                    Add(whole, Math.Min(encoder.StartVertex!.Value, encoder.StartFragment!.Value),
                        Math.Max(encoder.EndVertex!.Value, encoder.EndFragment!.Value), passes);
            }
            else Add(whole, encoder.StartEncoder, encoder.EndEncoder, passes);
        }
        return (FrameDiagnosticsDistribution.Create(vertex), FrameDiagnosticsDistribution.Create(fragment), FrameDiagnosticsDistribution.Create(whole));
    }

    static bool Valid(ulong? start, ulong? end) => start is > 0 && end >= start
        && start != ulong.MaxValue && end != ulong.MaxValue;

    static void Add(List<double> values, ulong? start, ulong? end, PassTimingSnapshot passes)
    {
        if (!Valid(start, end)) return;
        double scale = NanosecondsPerTick(start!.Value, passes);
        if (scale > 0) values.Add((end!.Value - start.Value) * scale / 1_000_000);
    }

    /// <summary>The nearest valid adjacent clock pair's scale, or 0 without calibration.</summary>
    internal static double NanosecondsPerTick(ulong start, PassTimingSnapshot passes)
    {
        var clocks = passes.ClockPairs.Records;
        double nearest = double.PositiveInfinity, scale = 0;
        for (int i = 1; i < clocks.Length; i++)
        {
            var a = clocks[i - 1];
            var b = clocks[i];
            if (b.GpuTicks <= a.GpuTicks || b.CpuNanoseconds <= a.CpuNanoseconds) continue;
            double distance = start < a.GpuTicks ? a.GpuTicks - start : start > b.GpuTicks ? start - b.GpuTicks : 0;
            if (distance >= nearest) continue;
            nearest = distance;
            scale = (b.CpuNanoseconds - a.CpuNanoseconds) / (double)(b.GpuTicks - a.GpuTicks);
        }
        return scale;
    }

    internal static (FrameDiagnosticsDistribution Timed, FrameDiagnosticsDistribution Untimed) Intervals(
        FrameTimingSnapshot timing, PassTimingSnapshot passes)
        => IntervalsBy(timing, passes.Blocks.Select(b => (b.StartCallback, b.EndCallbackExclusive, b.Timed)));

    /// <summary>Adjacent callbacks inside one block. Transitions, gaps and non-frame buffers do not contribute.</summary>
    internal static (FrameDiagnosticsDistribution Set, FrameDiagnosticsDistribution Unset) IntervalsBy(FrameTimingSnapshot timing,
        IEnumerable<(int Start, int EndExclusive, bool State)> schedule)
    {
        var blocks = schedule.ToArray();
        var set = new List<double>();
        var unset = new List<double>();
        var frames = FrameDiagnosticsEarlySubmitSummary.Frames(timing);
        foreach (var queue in frames.GroupBy(f => f.Queue))
        {
            var sorted = queue.OrderBy(f => f.End).ToArray();
            for (int i = 1; i < sorted.Length; i++)
            {
                var a = sorted[i - 1];
                var b = sorted[i];
                if (b.Callback != a.Callback + 1) continue;
                foreach (var block in blocks)
                {
                    if (a.Callback < block.Start || b.Callback >= block.EndExclusive) continue;
                    (block.State ? set : unset).Add((b.End - a.End) * 1000);
                    break;
                }
            }
        }
        return (FrameDiagnosticsDistribution.Create(set), FrameDiagnosticsDistribution.Create(unset));
    }
}
