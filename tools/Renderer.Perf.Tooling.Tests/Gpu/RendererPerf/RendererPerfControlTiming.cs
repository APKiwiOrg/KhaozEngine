using System;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfControlResolution(int WarmupCount, int WarmupBelowResolution,
    int MeasuredCount, int MeasuredBelowResolution, ImmutableArray<double?> ReportedTickPeriodsNs);

// Equal ready query values are evidence for empty controls, never a numerical duration or a rate.
internal static class RendererPerfControlTiming
{
    static readonly RendererPerfCounter[] NoWork = [RendererPerfCounter.Draws, RendererPerfCounter.IndexedDraws,
        RendererPerfCounter.Dispatches, RendererPerfCounter.BufferUpdates, RendererPerfCounter.TextureUpdates,
        RendererPerfCounter.BufferCopies, RendererPerfCounter.TextureCopies, RendererPerfCounter.TextureSubresourceCopies,
        RendererPerfCounter.Resolves, RendererPerfCounter.MipGenerations, RendererPerfCounter.ResourceCreations,
        RendererPerfCounter.WaitForIdleCalls];

    internal static bool AcceptsZero(RendererPerfFrameSample frame, int controlId, double? admittedPeriod)
        => controlId is >= 1 and <= 3 && frame.Errors.IsEmpty && frame.Passes.IsEmpty
            && frame.GpuFrame.Status == RendererPerfMetricStatus.BelowResolution && frame.GpuFrame.Value is null
            && frame.GpuFrame.Unit == "ns" && frame.GpuElapsedTicks == 0
            && frame.GpuPeriodNs is > 0 && double.IsFinite(frame.GpuPeriodNs.Value) && frame.GpuPeriodNs == admittedPeriod
            && Count(frame, RendererPerfCounter.Submissions, 1)
            && Count(frame, RendererPerfCounter.ColorClears, controlId == 2 ? 5 : 1)
            && Count(frame, RendererPerfCounter.DepthClears, controlId == 1 ? 0 : 1)
            && NoWork.All(counter => Count(frame, counter, 0));

    internal static bool Promotable(RendererPerfControlReport control, double? admittedPeriod)
    {
        var samples = control.Warmup.Concat(control.Measured);
        return samples.All(frame => RendererPerfReportJson.Promotable(frame)
                || AcceptsZero(frame, control.Control.Id, admittedPeriod))
            && (!samples.Any(frame => frame.GpuFrame.Status == RendererPerfMetricStatus.BelowResolution)
                || control.Control.Id is >= 1 and <= 3 && control.Control.Version == "1"
                && control.Control.WorkSha256 == RendererPerfControls.Definition(control.Control.Id).WorkSha256
                && control.Metrics.TryGetValue("control.outputValidated", out var output)
                && output.Status == RendererPerfMetricStatus.Available && output.Unit == "count" && output.Value == 1);
    }

    static bool Count(RendererPerfFrameSample frame, RendererPerfCounter counter, int expected)
        => frame.Metrics.TryGetValue("logical.total." + counter, out var metric)
            && metric.Status == RendererPerfMetricStatus.Available && metric.Unit == "count" && metric.Value == expected;

    internal static RendererPerfControlResolution? Summarize(RendererPerfControlReport control)
    {
        var warm = control.Warmup.Where(Below).ToArray();
        var measured = control.Measured.Where(Below).ToArray();
        return warm.Length + measured.Length == 0 ? null : new(control.Warmup.Length, warm.Length,
            control.Measured.Length, measured.Length,
            [.. warm.Concat(measured).Select(frame => frame.GpuPeriodNs).Distinct().Order()]);
    }

    static bool Below(RendererPerfFrameSample frame) => frame.GpuFrame.Status == RendererPerfMetricStatus.BelowResolution;
}
