using System;
using System.Reflection;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing;

internal readonly record struct FrameDiagnosticsBoundary(long Timestamp, FrameDiagnosticsWindowState Window, GpuDeviceCounters Counters);

internal readonly record struct FrameDiagnosticsSample(long StartTimestamp, long EndTimestamp,
    double MonotonicIntervalSeconds, double LoopDeltaSeconds, FrameDiagnosticsWindowState CompletedFrameWindow,
    FrameDiagnosticsWindowState BoundaryWindow, GpuDeviceCounters Counters);

internal sealed record FrameDiagnosticsReport(int SchemaVersion, int ProcessId, string AppAssembly,
    string EngineAssembly, long StopwatchFrequency, double DurationSeconds, int SampleCount, string StopReason,
    FrameDiagnosticsBoundary Baseline, FrameDiagnosticsBoundary End, FrameDiagnosticsSample[] Samples,
    FrameDiagnosticsSummary Summary)
{
    internal static FrameDiagnosticsReport Create(long frequency, FrameDiagnosticsBoundary baseline,
        FrameDiagnosticsBoundary end, FrameDiagnosticsSample[] buffer, int count, string reason)
    {
        var samples = new FrameDiagnosticsSample[count];
        Array.Copy(buffer, samples, count);
        var engine = typeof(FrameDiagnosticsRecorder).Assembly;
        return new FrameDiagnosticsReport(2, Environment.ProcessId,
            Assembly.GetEntryAssembly()?.FullName ?? "unknown", engine.FullName + " MVID=" + engine.ManifestModule.ModuleVersionId,
            frequency, (end.Timestamp - baseline.Timestamp) / (double)frequency, count, reason, baseline, end, samples,
            FrameDiagnosticsSummary.Create(baseline, end, samples, reason == "capacity", count == buffer.Length));
    }
}

internal sealed record FrameDiagnosticsSummary(double FramesPerSecond, double MeanFrameMs, double MedianFrameMs,
    double P95FrameMs, double P99FrameMs, double WorstFrameMs, double ShareWithin144HzBudget,
    bool AllForegroundRenderedStableSize, bool CadenceCapped, bool BufferCapacityReached, bool CapacityLimited,
    bool AllPreparePhaseSubmissionsPaired, FrameDiagnosticsCounterDelta? CounterDelta)
{
    internal static FrameDiagnosticsSummary Create(FrameDiagnosticsBoundary baseline, FrameDiagnosticsBoundary end,
        FrameDiagnosticsSample[] samples, bool capacityLimited, bool capacityReached)
    {
        var milliseconds = new double[samples.Length];
        double seconds = 0;
        int withinBudget = 0;
        bool valid = Valid(baseline.Window, baseline.Window);
        bool capped = Capped(baseline.Window);
        bool countersSupported = baseline.Counters.HasValue;
        bool paired = true;
        FrameDiagnosticsWindowState previous = baseline.Window;
        for (int i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            seconds += sample.MonotonicIntervalSeconds;
            double ms = sample.MonotonicIntervalSeconds * 1000;
            milliseconds[i] = ms;
            if (ms <= 1000.0 / 144) withinBudget++;
            valid &= Valid(sample.BoundaryWindow, baseline.Window);
            capped |= Capped(sample.BoundaryWindow);
            countersSupported &= sample.Counters.HasValue;
            var boundary = sample.BoundaryWindow;
            long expected = baseline.Window.PreparePhaseSplitActive ? 1 : 0;
            paired &= boundary.PreparePhaseSplitActive == baseline.Window.PreparePhaseSplitActive
                && sample.CompletedFrameWindow.PreparePhaseSplitActive == baseline.Window.PreparePhaseSplitActive
                && boundary.PreparePhaseSubmissionCount - previous.PreparePhaseSubmissionCount == expected
                && boundary.PreparePhaseCompositeCount - previous.PreparePhaseCompositeCount == expected;
            previous = boundary;
        }
        Array.Sort(milliseconds);
        int count = samples.Length;
        double median = count % 2 == 1 ? milliseconds[count / 2]
            : (milliseconds[count / 2 - 1] + milliseconds[count / 2]) / 2;
        return new FrameDiagnosticsSummary(count / seconds, seconds * 1000 / count, median,
            milliseconds[(int)Math.Ceiling(count * 0.95) - 1], milliseconds[(int)Math.Ceiling(count * 0.99) - 1],
            milliseconds[count - 1], withinBudget / (double)count, valid, capped, capacityReached, capacityLimited,
            paired, countersSupported ? FrameDiagnosticsCounterDelta.Between(baseline.Counters, end.Counters, count) : null);
    }

    static bool Valid(FrameDiagnosticsWindowState value, FrameDiagnosticsWindowState baseline) =>
        value.Focused && !value.Minimized && !value.RenderSuppressed && value.Width > 0 && value.Height > 0
        && value.Width == baseline.Width && value.Height == baseline.Height
        && value.RendererInitializesSwapchain == baseline.RendererInitializesSwapchain
        && value.PreparePhaseSplitActive == baseline.PreparePhaseSplitActive;

    static bool Capped(FrameDiagnosticsWindowState value) => value.EffectiveBaseCapHz > 0 || value.Vsync;
}

// These cumulative durations are CPU-side measurements, including full acquisition API calls,
// not GPU execution timings or physical display cadence. The per-interval
// denominator is the same full-client callback count as cadence, with FramesBegun retained separately.
internal sealed record FrameDiagnosticsCounterDelta(long FramesBegun, long DrainCount, double DrainMs,
    long BackpressureStallCount, double BackpressureStallMs, long AcquireWaitCount, double AcquireWaitMs,
    long OffTimelineDeferred, long OffTimelineOutstandingChange,
    double DrainCountPerInterval, double DrainMsPerInterval, double BackpressureStallCountPerInterval,
    double BackpressureStallMsPerInterval, double AcquireWaitCountPerInterval, double AcquireWaitMsPerInterval)
{
    internal static FrameDiagnosticsCounterDelta Between(GpuDeviceCounters start, GpuDeviceCounters end, int count) => new(
        end.FramesBegun - start.FramesBegun, end.DrainCount - start.DrainCount, end.DrainMs - start.DrainMs,
        end.BackpressureStallCount - start.BackpressureStallCount, end.BackpressureStallMs - start.BackpressureStallMs,
        end.AcquireWaitCount - start.AcquireWaitCount, end.AcquireWaitMs - start.AcquireWaitMs,
        end.OffTimelineDeferred - start.OffTimelineDeferred, end.OffTimelineOutstanding - start.OffTimelineOutstanding,
        (end.DrainCount - start.DrainCount) / (double)count, (end.DrainMs - start.DrainMs) / count,
        (end.BackpressureStallCount - start.BackpressureStallCount) / (double)count,
        (end.BackpressureStallMs - start.BackpressureStallMs) / count,
        (end.AcquireWaitCount - start.AcquireWaitCount) / (double)count, (end.AcquireWaitMs - start.AcquireWaitMs) / count);
}
