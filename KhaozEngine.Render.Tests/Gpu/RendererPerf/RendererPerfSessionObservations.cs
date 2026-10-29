using System;
using System.Collections.Immutable;
using System.Diagnostics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfSessionObservations
{
    static readonly string[] TelemetryKeys = ["clock.requestedMHz", "clock.observedMHz", "clock.pstate",
        "clock.utilizationPercent", "clock.temperatureC", "clock.powerWatts", "clock.throttlingReasons",
        "clock.memoryMHz", "clock.maximumGraphicsMHz"];

    internal static ImmutableDictionary<string, RendererPerfMetric> Telemetry(ImmutableDictionary<string, RendererPerfMetric> supplied)
    {
        var result = supplied.ToBuilder();
        foreach (string key in TelemetryKeys)
            if (!result.ContainsKey(key)) result.Add(key, new(null, Unit(key), "batch-boundary host sampler",
                RendererPerfMetricStatus.Unsupported, "No identified sampler supplied this observation."));
        return result.ToImmutable();
    }
    static string Unit(string key) => key switch
    {
        "clock.requestedMHz" or "clock.observedMHz" or "clock.memoryMHz" or "clock.maximumGraphicsMHz" => "MHz",
        "clock.utilizationPercent" => "percent",
        "clock.temperatureC" => "C",
        "clock.powerWatts" => "W",
        "clock.throttlingReasons" => "flags",
        _ => "state-id"
    };

    internal static RendererPerfMemoryReport Memory(IGpuDevice device)
    {
        using var process = Process.GetCurrentProcess();
        var metrics = ImmutableDictionary<string, RendererPerfMetric>.Empty
            .Add("process.workingSet.bytes", RendererPerfDriver.Value(process.WorkingSet64, "bytes", "Process.WorkingSet64, batch boundary"))
            .Add("process.private.bytes", RendererPerfDriver.Value(process.PrivateMemorySize64, "bytes", "Process.PrivateMemorySize64, batch boundary"))
            .Add("managed.live.bytes", RendererPerfDriver.Value(GC.GetTotalMemory(false), "bytes", "GC.GetTotalMemory(false), live heap estimate, no forced collection"));
        return new(device is IGpuResourceMemoryDiagnostics diagnostics ? [diagnostics.ReadMemorySnapshot()] : [], metrics);
    }
}
