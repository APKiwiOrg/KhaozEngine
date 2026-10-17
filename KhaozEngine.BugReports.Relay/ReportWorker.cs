using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Merges validated snapshots and performs claimed GitHub issue and comment writes.</summary>
internal sealed class ReportWorker(BugReportRelayOptions options, IReportMediaStore media, ReportRecordStore records,
    ReportViewerWriter viewer, IGitHubIssueClient github, TimeProvider time, ILogger logger)
{
    readonly WorkClaim claims = new(media, time);
    readonly ReportIssueInputLoader loader = new(media);
    readonly ReportIssueComposer composer = new(options);

    public Task ProcessAsync(ReportWorkItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind switch
        {
            ReportWorkKind.CreateIssue => CreateIssueAsync(item.ReportId, ct),
            ReportWorkKind.AppendServerSnapshot => AppendSnapshotAsync(item.ReportId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unknown report work kind."),
        };
    }

    async Task CreateIssueAsync(string reportId, CancellationToken ct)
    {
        ReportRecord? record = await LoadAsync(reportId, ct).ConfigureAwait(false);
        if (record is null || record.IssueNumber is not null) return;
        int? epoch = await claims.TryAcquireAsync("issue", reportId, ct).ConfigureAwait(false);
        if (epoch is null)
        {
            record = await LoadAsync(reportId, ct).ConfigureAwait(false);
            if (record is null || record.IssueNumber is not null) return;
            throw new ReportClaimBusyException(reportId);
        }
        try
        {
            // A delivery can have held its first record read across another holder's completed lease.
            record = await LoadAsync(reportId, ct).ConfigureAwait(false);
            if (record is null || record.IssueNumber is not null) return;
            ServerSnapshotWire? snapshot = await LoadSnapshotAsync(record, warnIfMissing: false, ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                await WriteSnapshotAsync(record, snapshot, ct).ConfigureAwait(false);
                record = record with { ServerSnapshot = ServerSnapshotState.InBody };
                await records.SaveReportAsync(record, ct).ConfigureAwait(false);
            }
            IssueInput input = await loader.LoadAsync(record, snapshot, ct).ConfigureAwait(false);
            int number = await github.CreateIssueAsync(composer.ComposeIssue(input), ct).ConfigureAwait(false);
            await records.SaveReportAsync(record with { IssueNumber = number }, ct).ConfigureAwait(false);
        }
        catch
        {
            await claims.ReleaseAsync("issue", reportId, epoch.Value).ConfigureAwait(false);
            throw;
        }
    }

    async Task AppendSnapshotAsync(string reportId, CancellationToken ct)
    {
        ReportRecord? record = await LoadAsync(reportId, ct).ConfigureAwait(false);
        if (record is null) return;
        if (record.ServerSnapshot is ServerSnapshotState.InBody or ServerSnapshotState.Commented) return;
        if (record.IssueNumber is null) throw new ReportNotReadyException(reportId);
        int? epoch = await claims.TryAcquireAsync("comment", reportId, ct).ConfigureAwait(false);
        if (epoch is null)
        {
            record = await LoadAsync(reportId, ct).ConfigureAwait(false);
            if (record is null || record.ServerSnapshot is ServerSnapshotState.InBody or ServerSnapshotState.Commented) return;
            throw new ReportClaimBusyException(reportId);
        }
        try
        {
            record = await LoadAsync(reportId, ct).ConfigureAwait(false);
            if (record is null || record.ServerSnapshot is ServerSnapshotState.InBody or ServerSnapshotState.Commented) return;
            if (record.IssueNumber is null) throw new ReportNotReadyException(reportId);
            ServerSnapshotWire? snapshot = await LoadSnapshotAsync(record, warnIfMissing: true, ct).ConfigureAwait(false);
            if (snapshot is null) return;
            await WriteSnapshotAsync(record, snapshot, ct).ConfigureAwait(false);
            IssueInput input = await loader.LoadAsync(record, snapshot, ct).ConfigureAwait(false);
            await github.AddCommentAsync(record.IssueNumber.Value, composer.ComposeSnapshotComment(input), ct).ConfigureAwait(false);
            await records.SaveReportAsync(record with { ServerSnapshot = ServerSnapshotState.Commented }, ct).ConfigureAwait(false);
        }
        catch
        {
            await claims.ReleaseAsync("comment", reportId, epoch.Value).ConfigureAwait(false);
            throw;
        }
    }

    async Task<ReportRecord?> LoadAsync(string reportId, CancellationToken ct)
    {
        AcceptedReport? accepted = await records.LoadAcceptedAsync(reportId, ct).ConfigureAwait(false);
        if (accepted is null)
        {
            logger.Error("Unknown report " + reportId + ".");
            return null;
        }
        ReportRecord? record = await records.LoadReportAsync(reportId, ct).ConfigureAwait(false);
        if (record is null) logger.Error("Missing private record for report " + reportId + ".");
        return record;
    }

    async Task<ServerSnapshotWire?> LoadSnapshotAsync(ReportRecord record, bool warnIfMissing, CancellationToken ct)
    {
        ReadOnlyMemory<byte>? raw = await records.LoadPendingSnapshotAsync(record.ReportId, ct).ConfigureAwait(false);
        if (raw is null)
        {
            if (warnIfMissing) logger.Warn("Missing server snapshot for report " + record.ReportId + ".");
            return null;
        }
        ServerSnapshotWire snapshot = BugReportJson.DeserializeServerSnapshot(raw.Value.Span);
        if (!string.Equals(snapshot.Subject, record.Reporter.Subject, StringComparison.Ordinal))
        {
            logger.Warn("Server snapshot subject mismatch for report " + record.ReportId + ".");
            return null;
        }
        foreach (string key in snapshot.Sections.Keys)
        {
            if (BundleEntryNames.IsValidSectionKey(key)) continue;
            logger.Warn("Invalid server snapshot section key for report " + record.ReportId + ".");
            return null;
        }
        return snapshot;
    }

    // Both public outputs get the subject-free document. The authenticated wire never leaves the Pending area.
    async Task WriteSnapshotAsync(ReportRecord record, ServerSnapshotWire snapshot, CancellationToken ct)
    {
        byte[] json = PublicServerSnapshot.Serialize(snapshot);
        string path = ReportLayout.Entry(record.Folder, ReportLayout.ServerSnapshotFile);
        await media.PutAsync(ReportArea.Public, path, json, ReportLayout.HeadersFor(path), overwrite: true, ct).ConfigureAwait(false);
        await viewer.WriteAsync(record, json, ct).ConfigureAwait(false);
    }
}
