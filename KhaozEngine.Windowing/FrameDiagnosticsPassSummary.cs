using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Windowing;

internal sealed record FrameDiagnosticsPassDistribution(string Kind, int SignatureId,
    FrameDiagnosticsDistribution VertexMs, FrameDiagnosticsDistribution FragmentMs,
    FrameDiagnosticsDistribution EncoderMs);

internal static class FrameDiagnosticsPassSummary
{
    internal static FrameDiagnosticsPassDistribution[] Encoders(PassTimingSnapshot passes)
    {
        var output = new List<FrameDiagnosticsPassDistribution>();
        foreach (var group in passes.Encoders.Records.GroupBy(e => (e.Kind, e.SignatureId)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.SignatureId))
        {
            var vertex = new List<double>();
            var fragment = new List<double>();
            var whole = new List<double>();
            foreach (var encoder in group)
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
            output.Add(new(group.Key.Kind, group.Key.SignatureId, FrameDiagnosticsDistribution.Create(vertex),
                FrameDiagnosticsDistribution.Create(fragment), FrameDiagnosticsDistribution.Create(whole)));
        }
        return output.ToArray();
    }

    static bool Valid(ulong? start, ulong? end) => start is > 0 && end >= start
        && start != ulong.MaxValue && end != ulong.MaxValue;

    static void Add(List<double> values, ulong? start, ulong? end, PassTimingSnapshot passes)
    {
        if (!Valid(start, end)) return;
        var clocks = passes.ClockPairs.Records;
        double nearest = double.PositiveInfinity, scale = 0;
        for (int i = 1; i < clocks.Length; i++)
        {
            var a = clocks[i - 1];
            var b = clocks[i];
            if (b.GpuTicks <= a.GpuTicks || b.CpuNanoseconds <= a.CpuNanoseconds) continue;
            double distance = start < a.GpuTicks ? a.GpuTicks - start.Value : start > b.GpuTicks ? start.Value - b.GpuTicks : 0;
            if (distance >= nearest) continue;
            nearest = distance;
            scale = (b.CpuNanoseconds - a.CpuNanoseconds) / (double)(b.GpuTicks - a.GpuTicks);
        }
        if (scale > 0) values.Add((end!.Value - start!.Value) * scale / 1_000_000);
    }

    internal static (FrameDiagnosticsDistribution Timed, FrameDiagnosticsDistribution Untimed) Intervals(
        FrameTimingSnapshot timing, PassTimingSnapshot passes)
    {
        var timed = new List<double>();
        var untimed = new List<double>();
        var frames = new List<(FrameTimingCompletion Completion, int Callback)>();
        var submits = timing.Submissions.Records.GroupBy(s => (s.Queue, s.CommandBuffer))
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Timestamp).ToArray());
        foreach (var completion in timing.Completions.Records)
        {
            if (completion.Status != 4 || completion.GpuStartTime <= 0 || !double.IsFinite(completion.GpuStartTime)
                || completion.GpuEndTime < completion.GpuStartTime || !double.IsFinite(completion.GpuEndTime)
                || !submits.TryGetValue((completion.Queue, completion.CommandBuffer), out var matches)) continue;
            FrameTimingSubmit? submit = null;
            foreach (var candidate in matches) if (candidate.Timestamp <= completion.Timestamp) submit = candidate;
            if (submit is { Kind: FrameTimingCommandKind.FrameRecording, CallbackIndex: >= 0 } frame)
                frames.Add((completion, frame.CallbackIndex));
        }
        foreach (var queue in frames.GroupBy(f => f.Completion.Queue))
        {
            var sorted = queue.OrderBy(f => f.Completion.GpuEndTime).ToArray();
            for (int i = 1; i < sorted.Length; i++)
            {
                var a = sorted[i - 1];
                var b = sorted[i];
                if (b.Callback != a.Callback + 1) continue;
                foreach (var block in passes.Blocks)
                {
                    if (a.Callback < block.StartCallback || b.Callback >= block.EndCallbackExclusive) continue;
                    (block.Timed ? timed : untimed).Add((b.Completion.GpuEndTime - a.Completion.GpuEndTime) * 1000);
                    break;
                }
            }
        }
        return (FrameDiagnosticsDistribution.Create(timed), FrameDiagnosticsDistribution.Create(untimed));
    }
}
