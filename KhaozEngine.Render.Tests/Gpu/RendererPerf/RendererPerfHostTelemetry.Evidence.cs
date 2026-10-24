using System;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed partial class RendererPerfHostTelemetry
{
    internal void Register(RendererPerfPublicationWriter writer, RendererPerfUnitBudget budget)
    {
        if (_boundary != 0) writer.RegisterProducedFile("host-telemetry.jsonl", RendererPerfArtifactProducers.Schema("host-telemetry.jsonl"), budget);
    }
    sealed record Row(int Boundary, long AtTicks, long Frequency, string Query, string? Raw, string? Failure);
    internal static void Verify(ReadOnlySpan<byte> bytes)
    {
        int boundary = 0;
        while (!bytes.IsEmpty)
        {
            int newline = bytes.IndexOf((byte)'\n');
            if (newline < 0) throw new InvalidOperationException("Host telemetry has an incomplete row.");
            var row = RendererPerfManifest.ReadJson<Row>(bytes[..newline], RendererPerfReportJson.MaximumBytes);
            if (row.Boundary != boundary || row.AtTicks < 0 || row.Frequency <= 0 || row.Query != Query
                || string.IsNullOrWhiteSpace(row.Raw) && string.IsNullOrWhiteSpace(row.Failure))
                throw new InvalidOperationException("Host telemetry identity, cadence or support cause disagrees with its producer.");
            boundary = checked(boundary + 64); bytes = bytes[(newline + 1)..];
        }
        if (boundary == 0) throw new InvalidOperationException("Host telemetry is empty.");
    }
}
