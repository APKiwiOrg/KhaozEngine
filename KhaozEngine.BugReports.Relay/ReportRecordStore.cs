using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Stores report records, accepted indices and raw pending snapshots in the private Pending area.</summary>
internal sealed class ReportRecordStore
{
    readonly IReportMediaStore media;

    public ReportRecordStore(IReportMediaStore media)
    {
        ArgumentNullException.ThrowIfNull(media);
        this.media = media;
    }

    /// <summary>Creates the accepted index only when absent, preserving the first accepted folder and owner.</summary>
    public Task<bool> TryCreateAcceptedAsync(string reportId, AcceptedReport accepted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        string path = ReportLayout.AcceptedIndex(reportId);
        return media.PutAsync(ReportArea.Pending, path, JsonSerializer.SerializeToUtf8Bytes(accepted, RelayJson.Options),
            ReportLayout.HeadersFor(path), overwrite: false, ct);
    }

    /// <summary>Loads the accepted index, or returns null when it is absent.</summary>
    public Task<AcceptedReport?> LoadAcceptedAsync(string reportId, CancellationToken ct) =>
        LoadPrivateAsync<AcceptedReport>(ReportLayout.AcceptedIndex(reportId), ReportLayout.MaxAcceptedBytes, ct);

    /// <summary>Claims a report id only when absent, preserving the first folder and reporter.</summary>
    public Task<bool> CreateReportAsync(ReportRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        string path = ReportLayout.ReportRecord(record.ReportId);
        return media.PutAsync(ReportArea.Pending, path, JsonSerializer.SerializeToUtf8Bytes(record, RelayJson.Options),
            ReportLayout.HeadersFor(path), overwrite: false, ct);
    }

    /// <summary>Writes or replaces the private report record, including its current issue and snapshot state.</summary>
    public async Task SaveReportAsync(ReportRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        string path = ReportLayout.ReportRecord(record.ReportId);
        await media.PutAsync(ReportArea.Pending, path, JsonSerializer.SerializeToUtf8Bytes(record, RelayJson.Options),
            ReportLayout.HeadersFor(path), overwrite: true, ct);
    }

    /// <summary>Loads the private record by report id, or returns null when the record is absent.</summary>
    public Task<ReportRecord?> LoadReportAsync(string reportId, CancellationToken ct) =>
        LoadPrivateAsync<ReportRecord>(ReportLayout.ReportRecord(reportId), ReportLayout.MaxRecordBytes, ct);

    /// <summary>Stores the raw pending snapshot only when absent, preserving the first write.</summary>
    public Task<bool> PutPendingSnapshotAsync(string reportId, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        string path = ReportLayout.PendingSnapshot(reportId);
        return media.PutAsync(ReportArea.Pending, path, body, ReportLayout.HeadersFor(path), overwrite: false, ct);
    }

    /// <summary>Loads the raw pending snapshot under its byte cap, or returns null when it is absent.</summary>
    public Task<ReadOnlyMemory<byte>?> LoadPendingSnapshotAsync(string reportId, CancellationToken ct) =>
        media.GetAsync(ReportArea.Pending, ReportLayout.PendingSnapshot(reportId), BugReportLimits.MaxServerSnapshotBytes, ct);

    async Task<T?> LoadPrivateAsync<T>(string path, int maxBytes, CancellationToken ct) where T : class
    {
        ReadOnlyMemory<byte>? bytes = await media.GetAsync(ReportArea.Pending, path, maxBytes, ct);
        if (bytes is null)
            return null;
        return JsonSerializer.Deserialize<T>(bytes.Value.Span, RelayJson.Options)
            ?? throw new JsonException("The private report document is null.");
    }
}
