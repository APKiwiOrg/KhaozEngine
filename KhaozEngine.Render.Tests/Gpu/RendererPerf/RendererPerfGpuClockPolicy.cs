using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfGpuClockState { NotApplicable, Verified, Refused }
internal sealed record RendererPerfGpuClockVerification(int? LockedGraphicsClockMHz,
    RendererPerfGpuClockState State, int Samples, string? Reason);

// Clock admission is independent of duration drift. Cached observations retain their acquisition ticks.
internal static class RendererPerfGpuClockPolicy
{
    internal const string Reason = "GpuClockNotLocked";

    internal static RendererPerfGpuClockVerification Assess(RendererPerfManifest manifest,
        IReadOnlyList<RendererPerfBatchReport> batches)
    {
        int? locked = manifest.Execution.LockedGraphicsClockMHz;
        if (manifest.ExecutionClass == RendererPerfExecutionClass.Rehearsal)
            return new(locked, RendererPerfGpuClockState.NotApplicable, 0, null);
        var samples = new HashSet<double>();
        string? failure = null;
        string? operation = null;
        int boundary = 0, previousGeneration = -1;
        double previousAt = -1;
        foreach (var batch in batches)
        {
            // Each operation has one startup ReadInfo, followed by exactly one per retained batch.
            if (operation != batch.Operation) { boundary++; operation = batch.Operation; }
            int generation = boundary++ / 64;
            string? cause = locked is { } clock ? Refusal(batch.Telemetry, clock) : "The manifest has no paid graphics clock.";
            if (TryValue(batch.Telemetry, "clock.observedAtTicks", "ticks", out double at))
            {
                samples.Add(at);
                if (at < 0 || (generation == previousGeneration ? at != previousAt : at <= previousAt))
                    cause ??= "The identified observation does not follow the fixed 64-boundary sampler cadence.";
                previousAt = at;
            }
            previousGeneration = generation;
            if (cause is not null) failure ??= batch.Operation + " batch " + batch.FirstFrame + ": " + cause;
        }
        if (batches.Count == 0) failure = "No conditioning, scene or control host telemetry was retained.";
        return new(locked, failure is null ? RendererPerfGpuClockState.Verified : RendererPerfGpuClockState.Refused,
            samples.Count, failure);
    }

    internal static string? Refusal(ImmutableDictionary<string, RendererPerfMetric> telemetry, int clock)
    {
        if (!TryValue(telemetry, "clock.observedMHz", "MHz", out double observed) || observed != clock)
            return "Observed graphics clock is unavailable or differs from the admitted lock.";
        // NVIDIA bits 0x1 and 0x2 describe idle and application settings. Every other bit refuses.
        if (!TryValue(telemetry, "clock.throttlingReasons", "flags", out double flags)
            || flags < 0 || flags > 3 || flags != Math.Truncate(flags))
            return "Throttle reasons are unavailable or include a power, thermal, hardware or external limiter.";
        if (!TryValue(telemetry, "clock.observedAtTicks", "ticks", out double at) || at < 0)
            return "Identified host telemetry is missing.";
        return null;
    }

    internal static bool TryValue(ImmutableDictionary<string, RendererPerfMetric> telemetry, string key,
        string unit, out double value)
    {
        value = 0;
        if (!telemetry.TryGetValue(key, out var metric) || metric is null || metric.Status != RendererPerfMetricStatus.Available
            || metric.Unit != unit || metric.Value is not { } number || !double.IsFinite(number)) return false;
        value = number;
        return true;
    }

    internal static void ValidateClaim(RendererPerfReport report, RendererPerfGpuClockVerification claim)
    {
        if (claim != report.GpuClock)
            throw new RendererPerfProtocolException(Reason, "Retained graphics-clock verification differs from the batch telemetry.");
    }
}
