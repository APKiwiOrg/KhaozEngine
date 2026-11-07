using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed partial class RendererPerfHostTelemetry
{
    internal void Register(RendererPerfPublicationWriter writer, RendererPerfUnitBudget budget)
    {
        if (_boundary != 0) writer.RegisterProducedFile("host-telemetry.jsonl", RendererPerfArtifactProducers.Schema("host-telemetry.jsonl"), budget);
    }
    sealed record Row(int Boundary, long AtTicks, long Frequency, string Query, string? Raw, string? Failure);
    internal static void Verify(ReadOnlySpan<byte> bytes, RendererPerfReport? report = null)
    {
        int boundary = 0;
        long previousAt = -1;
        var observations = new HashSet<double>();
        bool held = report?.GpuClock.State == RendererPerfGpuClockState.Verified;
        var retained = held ? report!.Batches.GroupBy(b => b.Telemetry["clock.observedAtTicks"].Value!.Value)
            .ToDictionary(g => g.Key, g => g.First().Telemetry) : null;
        while (!bytes.IsEmpty)
        {
            int newline = bytes.IndexOf((byte)'\n');
            if (newline < 0) throw new InvalidOperationException("Host telemetry has an incomplete row.");
            var row = RendererPerfManifest.ReadJson<Row>(bytes[..newline], RendererPerfReportJson.MaximumBytes);
            if (row.Boundary != boundary || row.AtTicks <= previousAt || row.Frequency <= 0 || row.Query != Query
                || string.IsNullOrWhiteSpace(row.Raw) && string.IsNullOrWhiteSpace(row.Failure))
                throw new InvalidOperationException("Host telemetry identity, cadence or support cause disagrees with its producer.");
            var sample = row.Failure is null && row.Raw is { } raw ? Decode(raw, row.AtTicks) : null;
            if (held)
            {
                if (sample is null || RendererPerfGpuClockPolicy.Refusal(sample, report!.Manifest.Execution.LockedGraphicsClockMHz!.Value) is not null
                    || !retained!.TryGetValue(row.AtTicks, out var batch)
                    || new[] { "clock.observedMHz", "clock.throttlingReasons" }.Any(key => sample[key].Value != batch[key].Value)
                    || report.Batches[0].Frequency != row.Frequency)
                    throw new RendererPerfProtocolException(RendererPerfGpuClockPolicy.Reason, "Raw host telemetry does not certify the retained graphics-clock lock.");
            }
            observations.Add(row.AtTicks);
            previousAt = row.AtTicks;
            boundary = checked(boundary + 64); bytes = bytes[(newline + 1)..];
        }
        if (boundary == 0) throw new InvalidOperationException("Host telemetry is empty.");
        if (held && !observations.SetEquals(retained!.Keys))
            throw new RendererPerfProtocolException(RendererPerfGpuClockPolicy.Reason, "Raw host telemetry is missing a retained clock observation.");
    }
}
