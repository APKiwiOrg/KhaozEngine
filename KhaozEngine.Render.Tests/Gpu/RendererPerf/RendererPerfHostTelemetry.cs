using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// One identified host snapshot every 64 ReadInfo boundaries. Never called from a frame bracket.
internal sealed partial class RendererPerfHostTelemetry(string directory)
{
    const string Query = "pci.device_id,driver_version,clocks.applications.graphics,clocks.current.graphics,pstate,utilization.gpu,temperature.gpu,power.draw,clocks_throttle_reasons.active";
    ImmutableDictionary<string, RendererPerfMetric> _last = ImmutableDictionary<string, RendererPerfMetric>.Empty;
    int _boundary;

    internal ImmutableDictionary<string, RendererPerfMetric> Read()
    {
        if (_boundary++ % 64 != 0) return _last;
        long at = Stopwatch.GetTimestamp();
        string? raw = null, failure = null;
        try
        {
            raw = RendererPerfHostProcess.Run("nvidia-smi", "--query-gpu=" + Query, "--format=csv,noheader,nounits");
            if (raw.Contains('\n')) throw new InvalidOperationException("The telemetry sampler requires exactly one physical GPU.");
            string[] fields = raw.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 9 || !fields[0].Equals("0x1EB810DE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sampler PCI identity is not the independently admitted native T4.");
            var result = ImmutableDictionary.CreateBuilder<string, RendererPerfMetric>();
            void Add(string key, string value, string unit, bool hex = false)
            {
                double number = 0;
                bool available = hex ? ulong.TryParse(value.AsSpan(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                if (available && hex) number = Convert.ToUInt64(value, 16);
                result.Add(key, available && double.IsFinite(number)
                    ? RendererPerfDriver.Value(number, unit, "nvidia-smi query, fixed 64-boundary sampler")
                    : new(null, unit, "nvidia-smi query", RendererPerfMetricStatus.Unsupported, "Driver reported " + value));
            }
            Add("clock.requestedMHz", fields[2], "MHz");
            Add("clock.observedMHz", fields[3], "MHz");
            Add("clock.pstate", fields[4].StartsWith('P') ? fields[4][1..] : fields[4], "state-id");
            Add("clock.utilizationPercent", fields[5], "percent");
            Add("clock.temperatureC", fields[6], "C");
            Add("clock.powerWatts", fields[7], "W");
            Add("clock.throttlingReasons", fields[8], "flags", hex: true);
            result.Add("clock.observedAtTicks", RendererPerfDriver.Value(at, "ticks", "Stopwatch, host sampler acquisition start"));
            _last = result.ToImmutable();
        }
        catch (Exception error)
        {
            failure = error.Message;
            _last = ImmutableDictionary<string, RendererPerfMetric>.Empty.Add("clock.sampler.failure",
                new(null, "state", "nvidia-smi fixed 64-boundary sampler", RendererPerfMetricStatus.Unsupported, failure));
        }
        File.AppendAllText(Path.Combine(directory, "host-telemetry.jsonl"), JsonSerializer.Serialize(new
        { boundary = _boundary - 1, atTicks = at, frequency = Stopwatch.Frequency, query = Query, raw, failure }) + "\n");
        return _last;
    }
}
