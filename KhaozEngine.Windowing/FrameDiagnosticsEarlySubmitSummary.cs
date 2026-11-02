using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Windowing;

internal sealed record FrameDiagnosticsEarlySubmitState(bool Enabled, int ScheduledCallbacks, int AppliedCallbacks,
    int SettledCallbacks, FrameDiagnosticsDistribution CallbackIntervalMs, double FramesPerSecond,
    FrameDiagnosticsDistribution FrameCompletionIntervalMs, FrameDiagnosticsDistribution GpuIdlePerFrameMs,
    FrameDiagnosticsDistribution AttributedBusyMs, FrameDiagnosticsDistribution DrawableAcquireWaitMsPerCallback,
    FrameDiagnosticsDistribution PresentedIntervalMs);

/// <summary>One frame consists of its final buffer plus any early prefix. A missing or invalid part provides no frame
/// coverage. Native handles are matched with the latest preceding commit on the same queue.</summary>
internal static class FrameDiagnosticsEarlySubmitSummary
{
    internal readonly record struct Frame(long Queue, int Callback, double Start, double End, double BusyMs);

    internal static Frame[] Frames(FrameTimingSnapshot timing)
    {
        var commits = timing.Submissions.Records;
        var byHandle = commits.GroupBy(s => (s.Queue, s.CommandBuffer)).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Timestamp).ToArray());
        var matched = new Dictionary<FrameTimingSubmit, FrameTimingCompletion>();
        foreach (var completion in timing.Completions.Records)
        {
            FrameTimingSubmit? submit = null;
            if (byHandle.TryGetValue((completion.Queue, completion.CommandBuffer), out var candidates))
                foreach (var candidate in candidates)
                    if (candidate.Timestamp <= completion.Timestamp) submit = candidate;
            if (submit is { } found && completion.Status == 4 && completion.GpuStartTime > 0
                && completion.GpuEndTime >= completion.GpuStartTime && double.IsFinite(completion.GpuEndTime)
                && double.IsFinite(completion.GpuStartTime)) matched[found] = completion;
        }
        var frames = new List<Frame>();
        foreach (var group in commits.Where(s => s.CallbackIndex >= 0 && s.Kind is FrameTimingCommandKind.FrameRecording
            or FrameTimingCommandKind.EarlyFrameRecording).GroupBy(s => (s.Queue, s.CallbackIndex)))
        {
            var parts = group.ToArray();
            if (parts.Count(s => s.Kind == FrameTimingCommandKind.FrameRecording) != 1
                || parts.Count(s => s.Kind == FrameTimingCommandKind.EarlyFrameRecording) > 1 || parts.Any(s => !matched.ContainsKey(s))) continue;
            var completed = parts.Select(s => matched[s]).ToArray();
            frames.Add(new(group.Key.Queue, group.Key.CallbackIndex, completed.Min(c => c.GpuStartTime),
                completed.Max(c => c.GpuEndTime), completed.Sum(c => c.GpuEndTime - c.GpuStartTime) * 1000));
        }
        return frames.ToArray();
    }

    internal static FrameDiagnosticsEarlySubmitState[] States(FrameTimingSnapshot timing, PassTimingSnapshot passes,
        FrameEarlySubmitSnapshot experiment, FrameDiagnosticsBoundary baseline, FrameDiagnosticsSample[] samples)
    {
        var frames = Frames(timing);
        var busy = FrameDiagnosticsShaderGateSummary.AttributedBusy(passes);
        return new[] { false, true }.Select(enabled =>
        {
            var blocks = experiment.Schedule.Where(b => b.Enabled == enabled).ToArray();
            bool Contains(int callback) => blocks.Any(b => callback >= b.StartCallback && callback < b.EndCallbackExclusive);
            var callbacks = Enumerable.Range(0, samples.Length).Where(Contains).ToArray();
            var callbackMs = callbacks.Select(i => samples[i].MonotonicIntervalSeconds * 1000).ToList();
            var acquire = callbacks.Where(i => samples[i].Counters.HasValue && (i == 0 ? baseline.Counters : samples[i - 1].Counters).HasValue)
                .Select(i => samples[i].Counters.AcquireWaitMs - (i == 0 ? baseline.Counters : samples[i - 1].Counters).AcquireWaitMs).ToList();
            var intervals = new List<double>();
            var idle = new List<double>();
            foreach (var queue in frames.GroupBy(f => f.Queue))
            {
                var ordered = queue.OrderBy(f => f.Callback).ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    var a = ordered[i - 1];
                    var b = ordered[i];
                    if (b.Callback != a.Callback + 1 || !blocks.Any(block => a.Callback >= block.StartCallback && b.Callback < block.EndCallbackExclusive)) continue;
                    intervals.Add((b.End - a.End) * 1000);
                    idle.Add(Math.Max(0, b.Start - a.End) * 1000);
                }
            }
            var settled = busy.Where(s => blocks.Any(b => s.Key >= b.StartCallback + FrameDiagnosticsShaderGateSummary.EdgeCallbacks
                && s.Key < b.EndCallbackExclusive - FrameDiagnosticsShaderGateSummary.EdgeCallbacks)).Select(s => s.Value).ToList();
            var presented = PresentedIntervals(timing, blocks);
            return new FrameDiagnosticsEarlySubmitState(enabled, callbacks.Length,
                callbacks.Count(i => i < experiment.AppliedPerCallback.Length && experiment.AppliedPerCallback[i]), settled.Count,
                FrameDiagnosticsDistribution.Create(callbackMs), callbackMs.Count == 0 ? 0 : callbackMs.Count * 1000 / callbackMs.Sum(),
                FrameDiagnosticsDistribution.Create(intervals), FrameDiagnosticsDistribution.Create(idle),
                FrameDiagnosticsDistribution.Create(settled), FrameDiagnosticsDistribution.Create(acquire), FrameDiagnosticsDistribution.Create(presented));
        }).ToArray();
    }

    static List<double> PresentedIntervals(FrameTimingSnapshot timing, FrameEarlySubmitBlock[] blocks)
    {
        var matched = new List<(int Callback, double Time)>();
        foreach (var presented in timing.Presented.Records.Where(p => p.PresentedTime > 0 && double.IsFinite(p.PresentedTime)))
        {
            FrameTimingPresentCall? call = null;
            foreach (var candidate in timing.PresentCalls.Records)
                if (candidate.DrawableId == presented.DrawableId && candidate.Timestamp <= presented.Timestamp
                    && (call is null || candidate.Timestamp > call.Value.Timestamp)) call = candidate;
            if (call is { CallbackIndex: >= 0 } found) matched.Add((found.CallbackIndex, presented.PresentedTime));
        }
        var ordered = matched.OrderBy(p => p.Time).ToArray();
        var values = new List<double>();
        for (int i = 1; i < ordered.Length; i++)
        {
            var a = ordered[i - 1];
            var b = ordered[i];
            if (b.Callback == a.Callback + 1 && blocks.Any(block => a.Callback >= block.StartCallback && b.Callback < block.EndCallbackExclusive))
                values.Add((b.Time - a.Time) * 1000);
        }
        return values;
    }
}
