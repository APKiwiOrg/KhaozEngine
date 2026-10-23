using System;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Diagnostics;

namespace KhaozEngine.Windowing;

internal readonly record struct FrameDiagnosticsBoundary(long Timestamp, FrameDiagnosticsWindowState Window, GpuDeviceCounters Counters,
    MetalFramePoolCounters NativePools);

internal readonly record struct FrameDiagnosticsSample(long StartTimestamp, long EndTimestamp,
    double MonotonicIntervalSeconds, double LoopDeltaSeconds, FrameDiagnosticsWindowState CompletedFrameWindow,
    FrameDiagnosticsWindowState BoundaryWindow, GpuDeviceCounters Counters, MetalFramePoolCounters NativePools);

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
    FrameDiagnosticsCounterDelta? CounterDelta, MetalFramePoolCounters NativePoolDelta)
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
        }
        Array.Sort(milliseconds);
        int count = samples.Length;
        double median = count % 2 == 1 ? milliseconds[count / 2]
            : (milliseconds[count / 2 - 1] + milliseconds[count / 2]) / 2;
        return new FrameDiagnosticsSummary(count / seconds, seconds * 1000 / count, median,
            milliseconds[(int)Math.Ceiling(count * 0.95) - 1], milliseconds[(int)Math.Ceiling(count * 0.99) - 1],
            milliseconds[count - 1], withinBudget / (double)count, valid, capped, capacityReached, capacityLimited,
            countersSupported ? FrameDiagnosticsCounterDelta.Between(baseline.Counters, end.Counters, count) : null,
            new(end.NativePools.NativePushCount - baseline.NativePools.NativePushCount,
                end.NativePools.NativePopCount - baseline.NativePools.NativePopCount,
                end.NativePools.BypassedLocalScopeCount - baseline.NativePools.BypassedLocalScopeCount,
                end.NativePools.ExplicitFrameScopeCount - baseline.NativePools.ExplicitFrameScopeCount));
    }

    static bool Valid(FrameDiagnosticsWindowState value, FrameDiagnosticsWindowState baseline) =>
        value.Focused && !value.Minimized && !value.RenderSuppressed && value.Width > 0 && value.Height > 0
        && value.Width == baseline.Width && value.Height == baseline.Height
        && value.RendererInitializesSwapchain == baseline.RendererInitializesSwapchain
        && value.NativePoolBatching == baseline.NativePoolBatching;

    static bool Capped(FrameDiagnosticsWindowState value) => value.EffectiveBaseCapHz > 0 || value.Vsync;
}

// These durations measure CPU waiting. They are not GPU execution timings. The per-interval
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
