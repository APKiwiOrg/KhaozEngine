using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Windowing;

/// <summary>One gate state's frames. The raw spans overlap neighbouring passes and frames on a tile-based GPU. The
/// exclusive values split each instant of GPU time evenly among the stage intervals active at it, per settled callback, so
/// shares add up to busy time. Blocks decide the state.</summary>
internal sealed record FrameDiagnosticsShaderGateState(string State, int Gates, string MainPassLabel, int SettledCallbacks,
    FrameDiagnosticsDistribution FrameCompletionIntervalMs, FrameDiagnosticsDistribution MainPassVertexMs,
    FrameDiagnosticsDistribution MainPassFragmentMs, FrameDiagnosticsDistribution MainPassEncoderMs,
    FrameDiagnosticsDistribution MainPassExclusiveVertexMs, FrameDiagnosticsDistribution MainPassExclusiveFragmentMs,
    FrameDiagnosticsDistribution AttributedBusyMs, FrameDiagnosticsDistribution GpuIdlePerFrameMs,
    string TaaPassLabel, FrameDiagnosticsDistribution TaaPassFragmentMs, FrameDiagnosticsDistribution TaaPassExclusiveFragmentMs);

internal static class FrameDiagnosticsShaderGateSummary
{
    /// <summary>The label an encoder carries when the main forward pass opens it. Terrain, foliage and skinned draws
    /// usually share that encoder under their own scopes.</summary>
    internal const string MainPassLabel = "opaque.rigid";
    /// <summary>Callbacks skipped at each block edge in the exclusive values, whose overlap can reach a neighbouring state.</summary>
    internal const int EdgeCallbacks = 3;
    internal const string TaaPassLabel = "temporal.accumulate";

    internal static FrameDiagnosticsShaderGateState[] States(FrameTimingSnapshot timing, PassTimingSnapshot passes,
        PassLabelsSnapshot labels, FrameDiagnosticsShaderGateReport report)
    {
        int mainLabel = Array.IndexOf(labels.Labels, MainPassLabel);
        int taaLabel = Array.IndexOf(labels.Labels, TaaPassLabel);
        var shares = ExclusiveShares(passes, mainLabel, taaLabel);
        return report.Counts.Select(count =>
        {
            var blocks = report.Schedule.Where(b => b.Gates == count.Gates).ToArray();
            var (intervals, _) = FrameDiagnosticsPassSummary.IntervalsBy(timing,
                report.Schedule.Select(b => (b.StartCallback, b.EndCallbackExclusive, b.Gates == count.Gates)));
            var (vertex, fragment, whole) = FrameDiagnosticsPassSummary.Spans(passes.Encoders.Records.Where(e => mainLabel >= 0
                && e.LabelId == mainLabel && blocks.Any(b => e.CallbackIndex >= b.StartCallback && e.CallbackIndex < b.EndCallbackExclusive)), passes);
            var (_, taaFragment, _) = FrameDiagnosticsPassSummary.Spans(passes.Encoders.Records.Where(e => taaLabel >= 0
                && e.LabelId == taaLabel && blocks.Any(b => e.CallbackIndex >= b.StartCallback && e.CallbackIndex < b.EndCallbackExclusive)), passes);
            var idle = new List<double>();
            foreach (var queue in FrameDiagnosticsEarlySubmitSummary.Frames(timing).GroupBy(f => f.Queue))
            {
                var frames = queue.OrderBy(f => f.Callback).ToArray();
                for (int i = 1; i < frames.Length; i++)
                    if (frames[i].Callback == frames[i - 1].Callback + 1 && blocks.Any(b => frames[i - 1].Callback >= b.StartCallback
                        && frames[i].Callback < b.EndCallbackExclusive))
                        idle.Add(Math.Max(0, frames[i].Start - frames[i - 1].End) * 1000);
            }
            var settled = shares.Where(s => blocks.Any(b => s.Key >= b.StartCallback + EdgeCallbacks
                && s.Key < b.EndCallbackExclusive - EdgeCallbacks)).Select(s => s.Value).ToArray();
            return new FrameDiagnosticsShaderGateState(count.State, count.Gates, MainPassLabel, settled.Length, intervals, vertex, fragment, whole,
                FrameDiagnosticsDistribution.Create(settled.Select(s => s.MainVertexMs).ToList()),
                FrameDiagnosticsDistribution.Create(settled.Select(s => s.MainFragmentMs).ToList()),
                FrameDiagnosticsDistribution.Create(settled.Select(s => s.BusyMs).ToList()),
                FrameDiagnosticsDistribution.Create(idle), TaaPassLabel, taaFragment,
                FrameDiagnosticsDistribution.Create(settled.Select(s => s.TaaFragmentMs).ToList()));
        }).ToArray();
    }

    internal static Dictionary<int, double> AttributedBusy(PassTimingSnapshot passes)
        => ExclusiveShares(passes, -1).ToDictionary(pair => pair.Key, pair => pair.Value.BusyMs);

    readonly record struct Interval(ulong Start, ulong End, bool MainVertex, bool MainFragment, bool TaaFragment, int Callback, double Scale);
    sealed class Share { internal double MainVertexMs, MainFragmentMs, TaaFragmentMs, BusyMs; }

    static Dictionary<int, Share> ExclusiveShares(PassTimingSnapshot passes, int mainLabel, int taaLabel = -1)
    {
        var intervals = new List<Interval>();
        foreach (var encoder in passes.Encoders.Records)
        {
            if (encoder.Status != "valid") continue;
            if (encoder.Kind == "render")
            {
                bool main = encoder.LabelId == mainLabel && mainLabel >= 0;
                AddInterval(intervals, encoder.StartVertex, encoder.EndVertex, main, false, false, encoder.CallbackIndex, passes);
                AddInterval(intervals, encoder.StartFragment, encoder.EndFragment, false, main, taaLabel >= 0 && encoder.LabelId == taaLabel, encoder.CallbackIndex, passes);
            }
            else AddInterval(intervals, encoder.StartEncoder, encoder.EndEncoder, false, false, false, encoder.CallbackIndex, passes);
        }
        var ticks = intervals.SelectMany(i => new[] { i.Start, i.End }).Distinct().Order().ToArray();
        var index = new Dictionary<ulong, int>(ticks.Length);
        for (int i = 0; i < ticks.Length; i++) index[ticks[i]] = i;
        var startsAt = new List<int>?[ticks.Length];
        var endsAt = new List<int>?[ticks.Length];
        for (int k = 0; k < intervals.Count; k++)
        {
            (startsAt[index[intervals[k].Start]] ??= []).Add(k);
            (endsAt[index[intervals[k].End]] ??= []).Add(k);
        }
        var shares = new Dictionary<int, Share>();
        var active = new HashSet<int>();
        for (int i = 0; i + 1 < ticks.Length; i++)
        {
            foreach (int k in endsAt[i] ?? []) active.Remove(k);
            foreach (int k in startsAt[i] ?? []) active.Add(k);
            if (active.Count == 0) continue;
            double step = (ticks[i + 1] - ticks[i]) / (double)active.Count;
            foreach (int k in active)
            {
                Interval interval = intervals[k];
                double ms = step * interval.Scale / 1_000_000;
                if (!shares.TryGetValue(interval.Callback, out Share? share)) shares[interval.Callback] = share = new();
                share.BusyMs += ms;
                if (interval.MainVertex) share.MainVertexMs += ms;
                if (interval.MainFragment) share.MainFragmentMs += ms;
                if (interval.TaaFragment) share.TaaFragmentMs += ms;
            }
        }
        return shares;
    }

    static void AddInterval(List<Interval> intervals, ulong? start, ulong? end, bool mainVertex, bool mainFragment, bool taaFragment, int callback, PassTimingSnapshot passes)
    {
        if (start is not { } from || end is not { } to || to <= from) return;
        double scale = FrameDiagnosticsPassSummary.NanosecondsPerTick(from, passes);
        if (scale > 0) intervals.Add(new(from, to, mainVertex, mainFragment, taaFragment, callback, scale));
    }
}
