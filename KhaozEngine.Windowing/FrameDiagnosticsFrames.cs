using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Windowing;

// A frame has one final buffer and at most one prefix on the same queue. Every part must complete successfully.
internal static class FrameDiagnosticsFrames
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

    internal static (FrameDiagnosticsDistribution Completion, FrameDiagnosticsDistribution Idle) Intervals(FrameTimingSnapshot timing)
    {
        var intervals = new List<double>();
        var idle = new List<double>();
        foreach (var queue in Frames(timing).GroupBy(f => f.Queue))
        {
            var ordered = queue.OrderBy(f => f.Callback).ToArray();
            for (int i = 1; i < ordered.Length; i++)
            {
                var a = ordered[i - 1];
                var b = ordered[i];
                if (b.Callback != a.Callback + 1 || b.End < a.End) continue;
                intervals.Add((b.End - a.End) * 1000);
                idle.Add(Math.Max(0, b.Start - a.End) * 1000);
            }
        }
        return (FrameDiagnosticsDistribution.Create(intervals), FrameDiagnosticsDistribution.Create(idle));
    }
}
