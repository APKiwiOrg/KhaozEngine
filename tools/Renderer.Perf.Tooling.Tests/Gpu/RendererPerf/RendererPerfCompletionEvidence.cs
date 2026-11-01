using System.Collections.Immutable;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Batch telemetry for each query availability wait, keyed by the frame's ordinal in its batch. A query's
// HRESULT sequence is its notReady count of S_FALSE reads, the first non-flushing, then finalHresult.
internal static class RendererPerfCompletionEvidence
{
    const string Source = "query availability wait after the batch fence drain";

    internal static ImmutableDictionary<string, RendererPerfMetric> Metrics(
        ImmutableArray<(int SampleId, GpuTimestampAvailability Wait)> waits, int firstSampleId)
    {
        var metrics = ImmutableDictionary.CreateBuilder<string, RendererPerfMetric>();
        foreach (var (sampleId, wait) in waits)
        {
            string prefix = "completion.queryWait." + (sampleId - firstSampleId) + ".";
            metrics.Add(prefix + "ready", Value(wait.Ready ? 1 : 0, "count"));
            metrics.Add(prefix + "polls", Value(wait.Polls, "count"));
            metrics.Add(prefix + "elapsedTicks", Value(wait.EndTicks - wait.StartTicks, "ticks"));
            metrics.Add(prefix + "fenceTarget", Value(wait.FenceTarget, "timeline value"));
            metrics.Add(prefix + "fenceCompleted", Value(wait.FenceCompleted, "timeline value"));
            metrics.Add(prefix + "drainTarget", Value(wait.DrainTarget, "timeline value"));
            foreach (var query in wait.Queries)
            {
                metrics.Add(prefix + query.Query + ".notReady", Value(query.NotReady, "count"));
                metrics.Add(prefix + query.Query + ".finalHresult", Value(query.FinalResult, "HRESULT"));
            }
        }
        return metrics.ToImmutable();
    }

    static RendererPerfMetric Value(double value, string unit) => RendererPerfDriver.Value(value, unit, Source);
}
