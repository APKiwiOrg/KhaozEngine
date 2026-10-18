using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Merges validated snapshots and performs claimed GitHub issue and comment writes.</summary>
internal sealed class ReportWorker(BugReportRelayOptions options, IReportMediaStore media, ReportRecordStore records,
    ReportViewerWriter viewer, IGitHubIssueClient github, TimeProvider time, ILogger logger, ReportRevocation revocation)
{
    readonly WorkClaim claims = new(media, time);
    readonly ReportIssueInputLoader loader = new(media);
    readonly ReportIssueComposer composer = new(options);
    const string CommentedResult = "commented";

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
        const ReportWorkKind kind = ReportWorkKind.CreateIssue;
        ReportRecord? record = await LoadAsync(reportId, ct).ConfigureAwait(false);
        if (record is null || record.IssueNumber is not null) return;
        if (await claims.FindResultAsync(kind, reportId, ct).ConfigureAwait(false) is { } result)
        {
            record = await LoadAsync(reportId, ct).ConfigureAwait(false);
            if (record is null || record.IssueNumber is not null) return;
            await SaveProgressAsync(reportId, ParseIssueNumber(reportId, result), ServerSnapshotState.None, ct).ConfigureAwait(false);
            return;
        }
        int? epoch = await claims.TryAcquireAsync(kind, reportId, ct).ConfigureAwait(false);
        if (epoch is null)
        {
            record = await LoadAsync(reportId, ct).ConfigureAwait(false);
            if (record is null || record.IssueNumber is not null) return;
            throw new ReportClaimBusyException(reportId);
        }
        int number;
        try
        {
            // A delivery can have held its first record read across another holder's completed lease.
            record = await LoadAsync(reportId, ct).ConfigureAwait(false);
            if (record is null || record.IssueNumber is not null) return;
            ServerSnapshotWire? snapshot = await LoadSnapshotAsync(record, warnIfMissing: false, ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                if (!await WriteSnapshotAsync(record, snapshot, ct).ConfigureAwait(false)) return;
                record = record with { ServerSnapshot = ServerSnapshotState.InBody };
                if (!await SaveProgressAsync(reportId, null, ServerSnapshotState.InBody, ct).ConfigureAwait(false)) return;
            }
            IssueInput input = await loader.LoadAsync(record, snapshot, ct).ConfigureAwait(false);
            if (await revocation.IsRevokedAsync(reportId, ct, record.Folder).ConfigureAwait(false)) return;
            number = await github.CreateIssueAsync(composer.ComposeIssue(input), ct).ConfigureAwait(false);
        }
        catch
        {
            await claims.ReleaseAsync(kind, reportId, epoch.Value).ConfigureAwait(false);
            throw;
        }
        // The issue exists from here on. A failure leaves the claim unreleased, so only the result marker or
        // lease expiry can move the report forward, never an immediate second create.
        await claims.CompleteAsync(kind, reportId, epoch.Value, number.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await SaveProgressAsync(reportId, number, ServerSnapshotState.None, ct).ConfigureAwait(false);
    }

    async Task AppendSnapshotAsync(string reportId, CancellationToken ct)
    {
        const ReportWorkKind kind = ReportWorkKind.AppendServerSnapshot;
        ReportRecord? record = await LoadAsync(reportId, ct).ConfigureAwait(false);
        if (record is null) return;
        if (record.ServerSnapshot is ServerSnapshotState.InBody or ServerSnapshotState.Commented) return;
        if (record.IssueNumber is null) throw new ReportNotReadyException(reportId);
        if (await claims.FindResultAsync(kind, reportId, ct).ConfigureAwait(false) is { } result)
        {
            record = await LoadAsync(reportId, ct).ConfigureAwait(false);
            if (record is null || record.ServerSnapshot is ServerSnapshotState.InBody or ServerSnapshotState.Commented) return;
            if (result != CommentedResult) throw new InvalidDataException("The comment result marker is invalid for report " + reportId + ".");
            await SaveProgressAsync(reportId, null, ServerSnapshotState.Commented, ct).ConfigureAwait(false);
            return;
        }
        int? epoch = await claims.TryAcquireAsync(kind, reportId, ct).ConfigureAwait(false);
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
            if (!await WriteSnapshotAsync(record, snapshot, ct).ConfigureAwait(false)) return;
            IssueInput input = await loader.LoadAsync(record, snapshot, ct).ConfigureAwait(false);
            if (await revocation.IsRevokedAsync(reportId, ct, record.Folder).ConfigureAwait(false)) return;
            await github.AddCommentAsync(record.IssueNumber.Value, composer.ComposeSnapshotComment(input), ct).ConfigureAwait(false);
        }
        catch
        {
            await claims.ReleaseAsync(kind, reportId, epoch.Value).ConfigureAwait(false);
            throw;
        }
        await claims.CompleteAsync(kind, reportId, epoch.Value, CommentedResult).ConfigureAwait(false);
        await SaveProgressAsync(reportId, null, ServerSnapshotState.Commented, ct).ConfigureAwait(false);
    }

    async Task<bool> SaveProgressAsync(string reportId, int? issueNumber, ServerSnapshotState snapshot, CancellationToken ct)
    {
        ReportRecord? current = await LoadAsync(reportId, ct).ConfigureAwait(false);
        if (current is null) return false;
        int? mergedIssue = current.IssueNumber ?? issueNumber;
        ServerSnapshotState mergedSnapshot = current.ServerSnapshot == ServerSnapshotState.None ? snapshot : current.ServerSnapshot;
        if (mergedIssue == current.IssueNumber && mergedSnapshot == current.ServerSnapshot) return true;
        await records.SaveReportAsync(current with { IssueNumber = mergedIssue, ServerSnapshot = mergedSnapshot }, ct).ConfigureAwait(false);
        return true;
    }

    static int ParseIssueNumber(string reportId, string result) =>
        int.TryParse(result, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0
            ? number
            : throw new InvalidDataException("The issue result marker is invalid for report " + reportId + ".");

    async Task<ReportRecord?> LoadAsync(string reportId, CancellationToken ct)
    {
        if (await revocation.IsRevokedAsync(reportId, ct).ConfigureAwait(false)) return null;
        AcceptedReport? accepted = await records.LoadAcceptedAsync(reportId, ct).ConfigureAwait(false);
        if (accepted is null)
        {
            logger.Error("Unknown report " + reportId + ".");
            return null;
        }
        ReportRecord? record = await records.LoadReportAsync(reportId, ct).ConfigureAwait(false);
        if (record is null) logger.Error("Missing private record for report " + reportId + ".");
        else if (await revocation.IsRevokedAsync(reportId, ct, record.Folder).ConfigureAwait(false)) return null;
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
    async Task<bool> WriteSnapshotAsync(ReportRecord record, ServerSnapshotWire snapshot, CancellationToken ct)
    {
        byte[] json = PublicServerSnapshot.Serialize(snapshot);
        string path = ReportLayout.Entry(record.Folder, ReportLayout.ServerSnapshotFile);
        if (await revocation.IsRevokedAsync(record.ReportId, ct, record.Folder).ConfigureAwait(false)) return false;
        await media.PutAsync(ReportArea.Public, path, json, ReportLayout.HeadersFor(path), overwrite: true, ct).ConfigureAwait(false);
        return await viewer.WriteAsync(record, json, ct).ConfigureAwait(false);
    }
}
