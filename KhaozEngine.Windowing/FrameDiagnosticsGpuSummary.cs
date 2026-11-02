using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Windowing;

internal sealed record FrameDiagnosticsDistribution(int Count, double Mean, double Median, double P95, double P99, double Worst)
{
    internal static FrameDiagnosticsDistribution Create(List<double> values)
    {
        if (values.Count == 0) return new(0, 0, 0, 0, 0, 0);
        values.Sort();
        int n = values.Count;
        return new(n, values.Average(), n % 2 == 0 ? (values[n / 2 - 1] + values[n / 2]) / 2 : values[n / 2],
            values[(int)Math.Ceiling(n * .95) - 1], values[(int)Math.Ceiling(n * .99) - 1], values[n - 1]);
    }
}

internal sealed record FrameDiagnosticsGpuSummary(FrameDiagnosticsDistribution FrameGpuBusyMs,
    FrameDiagnosticsDistribution GpuIdleGapMs, FrameDiagnosticsDistribution PresentedIntervalMs,
    int UnpresentedDrawables, bool[] FrameCompletionCoverage, bool[] PresentCallCoverage, bool[] PresentedCoverage,
    bool AllCallbacksCovered, bool Overflowed, bool IncompleteWrites, long NativeReadFailures,
    int UnmatchedCompletions, int InvalidGpuTimings)
{
    public FrameDiagnosticsPassDistribution[] PassEncoders { get; init; } = [];
    public FrameDiagnosticsDistribution TimedFrameCompletionIntervalMs { get; init; } = new(0, 0, 0, 0, 0, 0);
    public FrameDiagnosticsDistribution UntimedFrameCompletionIntervalMs { get; init; } = new(0, 0, 0, 0, 0, 0);
    public FrameDiagnosticsDiscardState[] CascadeDepthDiscard { get; init; } = [];
    public FrameDiagnosticsEarlySubmitState[] EarlySubmitStates { get; init; } = [];
    public FrameDiagnosticsShaderGateState[] ShaderGateStates { get; init; } = [];
    internal FrameDiagnosticsGpuSummary WithDiscardStates(FrameTimingSnapshot timing, PassTimingSnapshot passes, PassExperimentSnapshot experiment)
        => this with { CascadeDepthDiscard = FrameDiagnosticsPassSummary.DiscardStates(timing, passes, experiment) };
    internal FrameDiagnosticsGpuSummary WithPassTiming(FrameTimingSnapshot timing, PassTimingSnapshot passes)
    {
        var (timed, untimed) = FrameDiagnosticsPassSummary.Intervals(timing, passes);
        return this with
        {
            PassEncoders = FrameDiagnosticsPassSummary.Encoders(passes),
            TimedFrameCompletionIntervalMs = timed,
            UntimedFrameCompletionIntervalMs = untimed
        };
    }

    internal static FrameDiagnosticsGpuSummary Create(FrameTimingSnapshot timing, int count)
    {
        var busy = new List<double>();
        var idle = new List<double>();
        var intervals = new List<double>();
        var frameCoverage = new bool[count];
        var presentCoverage = new bool[count];
        var presentedCoverage = new bool[count];
        var submits = timing.Submissions.Records;
        var completions = timing.Completions.Records;
        int unmatched = 0, invalid = 0;
        // A native handle can be recycled within the capture. Pair by queue and the latest preceding commit,
        // never by handle alone or by completion delivery order (Metal makes no delivery-order guarantee).
        var byHandle = submits.GroupBy(s => (s.Queue, s.CommandBuffer))
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Timestamp).ToArray());
        foreach (var completion in completions)
        {
            FrameTimingSubmit? submit = null;
            if (byHandle.TryGetValue((completion.Queue, completion.CommandBuffer), out var matches))
                foreach (var candidate in matches)
                    if (candidate.Timestamp <= completion.Timestamp) submit = candidate;
            if (submit is null) unmatched++;
            if (!Valid(completion)) { invalid++; continue; }
            // A frame list submitted on another thread has no callback to join, but its raw busy span still counts.
            if (submit is { Kind: FrameTimingCommandKind.FrameRecording, CallbackIndex: < 0 })
                busy.Add((completion.GpuEndTime - completion.GpuStartTime) * 1000);
        }
        foreach (var frame in FrameDiagnosticsEarlySubmitSummary.Frames(timing))
        {
            busy.Add(frame.BusyMs);
            if ((uint)frame.Callback < (uint)count) frameCoverage[frame.Callback] = true;
        }
        foreach (var queue in completions.Where(Valid).GroupBy(c => c.Queue))
        {
            double? previousEnd = null;
            foreach (var completion in queue.OrderBy(c => c.GpuStartTime))
            {
                if (previousEnd.HasValue) idle.Add(Math.Max(0, completion.GpuStartTime - previousEnd.Value) * 1000);
                previousEnd = Math.Max(previousEnd ?? 0, completion.GpuEndTime);
            }
        }
        var presented = timing.Presented.Records;
        var validPresented = presented.Where(p => p.PresentedTime > 0 && double.IsFinite(p.PresentedTime))
            .OrderBy(p => p.PresentedTime).ToArray();
        for (int i = 1; i < validPresented.Length; i++)
            intervals.Add((validPresented[i].PresentedTime - validPresented[i - 1].PresentedTime) * 1000);
        var callsById = timing.PresentCalls.Records.GroupBy(c => c.DrawableId);
        foreach (var group in callsById)
        {
            var calls = group.OrderBy(c => c.Timestamp).ToArray();
            for (int i = 0; i < calls.Length; i++)
            {
                var call = calls[i];
                if ((uint)call.CallbackIndex >= (uint)count) continue;
                long nextCall = i + 1 < calls.Length ? calls[i + 1].Timestamp : long.MaxValue;
                presentCoverage[call.CallbackIndex] = true;
                presentedCoverage[call.CallbackIndex] |= validPresented.Any(p => p.DrawableId == call.DrawableId
                    && p.Timestamp >= call.Timestamp && p.Timestamp < nextCall);
            }
        }
        return new(FrameDiagnosticsDistribution.Create(busy), FrameDiagnosticsDistribution.Create(idle),
            FrameDiagnosticsDistribution.Create(intervals), presented.Count(p => p.PresentedTime == 0), frameCoverage,
            presentCoverage, presentedCoverage, frameCoverage.All(v => v) && presentCoverage.All(v => v) && presentedCoverage.All(v => v),
            timing.Submissions.Overflow + timing.Completions.Overflow + timing.PresentCalls.Overflow + timing.Presented.Overflow > 0,
            timing.Submissions.IncompleteWrites + timing.Completions.IncompleteWrites + timing.PresentCalls.IncompleteWrites + timing.Presented.IncompleteWrites > 0,
            timing.NativeReadFailures, unmatched, invalid);
    }

    static bool Valid(FrameTimingCompletion value) => value.Status == 4 && value.GpuStartTime > 0
        && value.GpuEndTime >= value.GpuStartTime && double.IsFinite(value.GpuStartTime) && double.IsFinite(value.GpuEndTime);
}
