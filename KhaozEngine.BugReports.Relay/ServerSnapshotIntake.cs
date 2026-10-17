using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Authenticates and stores the first server snapshot, then schedules any required late append.</summary>
internal sealed class ServerSnapshotIntake(BugReportRelayOptions options, ReportRecordStore records,
    IReportWorkQueue work, ILogger logger)
{
    public async Task<RelayResponse> RunAsync(string reportId, RelayRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ServiceKeyCheck.Matches(options.ServiceKey, new RequestHeaderReader(request.Headers).Get(RelayHeaders.ServiceKey)))
            return RelayResponses.Error(403, "forbidden");
        if (!ReportIds.IsValid(reportId)) return RelayResponses.Error(400, "bad_request");
        using MemoryStream? body = await CappedBody.ReadAsync(request.Body, request.ContentLength,
            BugReportLimits.MaxServerSnapshotBytes, ct).ConfigureAwait(false);
        if (body is null) return RelayResponses.Error(413, "too_large");
        ReadOnlyMemory<byte> json = body.GetBuffer().AsMemory(0, (int)body.Length);
        ServerSnapshotWire snapshot;
        try
        {
            snapshot = BugReportJson.DeserializeServerSnapshot(json.Span);
        }
        catch (JsonException)
        {
            return RelayResponses.Error(400, "bad_request");
        }
        if (snapshot.FormatVersion != BugReportFormat.Version || snapshot.ReportId != reportId || string.IsNullOrEmpty(snapshot.Subject))
            return RelayResponses.Error(400, "bad_request");

        AcceptedReport? accepted = await records.LoadAcceptedAsync(reportId, ct).ConfigureAwait(false);
        if (accepted is not null && !string.Equals(accepted.Subject, snapshot.Subject, StringComparison.Ordinal))
        {
            logger.Warn("Server snapshot subject mismatch for report " + reportId + ".");
            return RelayResponses.Accepted(reportId);
        }
        bool wrote = await records.PutPendingSnapshotAsync(reportId, json, ct).ConfigureAwait(false);
        accepted ??= await records.LoadAcceptedAsync(reportId, ct).ConfigureAwait(false);
        if (accepted is not null)
        {
            if (!wrote)
            {
                // A resend repairs a lost append enqueue even while CreateIssue is in flight. The append worker
                // waits for the issue number, and comment claims make a repeated append harmless.
                ReportRecord? record = await records.LoadReportAsync(reportId, ct).ConfigureAwait(false);
                if (record is null || record.ServerSnapshot != ServerSnapshotState.None)
                    return RelayResponses.Accepted(reportId);
            }
            await work.EnqueueAsync(new ReportWorkItem(ReportWorkKind.AppendServerSnapshot, reportId), ct).ConfigureAwait(false);
        }
        return RelayResponses.Accepted(reportId);
    }
}
