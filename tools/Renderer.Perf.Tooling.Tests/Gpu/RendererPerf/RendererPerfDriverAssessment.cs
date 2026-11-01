using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfDriverAssessment
{
    internal static void Drift(IReadOnlyList<RendererPerfBlockReport> blocks, List<string> reasons)
    {
        var iterationMeans = new Dictionary<(string, RendererPerfLayout), double>();
        foreach (var block in blocks)
        {
            var measured = block.Measured;
            if (measured.Length < 64 || measured.Any(f => f.GpuFrame.Status != RendererPerfMetricStatus.Available)) continue;
            double first = measured.Take(64).Average(f => f.GpuFrame.Value!.Value);
            double last = measured.TakeLast(64).Average(f => f.GpuFrame.Value!.Value);
            double mean = measured.Average(f => f.GpuFrame.Value!.Value);
            var key = (block.Block.SceneId, block.Block.Layout);
            if (Drifted(first, last) || iterationMeans.TryGetValue(key, out double previous) && Drifted(previous, mean))
                reasons.Add($"ClockOrWorkDrift {key.SceneId}/{key.Layout}/{block.Block.Iteration}.");
            iterationMeans.TryAdd(key, mean);
        }
    }
    internal static ImmutableDictionary<string, RendererPerfMetric> WindowMetrics(IReadOnlyList<RendererPerfBlockReport> blocks,
        ImmutableArray<RendererPerfFrameSample> conditioning)
    {
        var metrics = ImmutableDictionary.CreateBuilder<string, RendererPerfMetric>();
        void Mean(string key, IEnumerable<RendererPerfFrameSample> frames)
        {
            var values = frames.ToArray();
            metrics.Add(key, values.Length == 64 && values.All(f => f.GpuFrame.Status == RendererPerfMetricStatus.Available)
                ? RendererPerfDriver.Value(values.Average(f => f.GpuFrame.Value!.Value), "ns", "untrimmed 64-frame native outer-pair mean")
                : new(null, "ns", "64-frame outer-pair mean", RendererPerfMetricStatus.ModelIncomplete, "A complete valid window is unavailable."));
        }
        Mean("conditioning.last64.mean", conditioning.TakeLast(64));
        for (int i = 0; i < blocks.Count; i++)
        {
            Mean($"block.{i}.first64.mean", blocks[i].Measured.Take(64));
            Mean($"block.{i}.last64.mean", blocks[i].Measured.TakeLast(64));
        }
        return metrics.ToImmutable();
    }
    static bool Drifted(double first, double last) => !double.IsFinite(first) || !double.IsFinite(last)
        || first <= 0 || Math.Abs(last - first) / first > .05;
}
