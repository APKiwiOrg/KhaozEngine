using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Resolves report revocations, removes public media and schedules delayed private cleanup.</summary>
internal sealed class ReportRevoker(IReportMediaStore media, ReportRecordStore records, ReportRevocation revocation,
    IReportWorkQueue work, IGitHubIssueClient github, TimeProvider time, ILogger logger)
{
    readonly WorkClaim claims = new(media, time);

    /// <summary>The claim lease plus a two-minute margin before private state may be purged.</summary>
    public static TimeSpan PurgeDelay => TimeSpan.FromMinutes(WorkClaim.LeaseMinutes + 2);

    /// <summary>Resolves one id or reference, revokes the report and schedules its private purge.</summary>
    public async Task<ReportRevocationResult> RevokeAsync(string idOrReference, CancellationToken ct)
    {
        string input = idOrReference?.Trim() ?? string.Empty;
        string? id = null;
        string reference;
        var matches = new List<ResolvedReport>();
        if (input.StartsWith("R-", StringComparison.OrdinalIgnoreCase))
        {
            if (!ReportLayout.TryNormalizeReference(input, out reference))
                return Result(ReportRevocationStatus.Invalid);
            string prefix = ReportLayout.ReferencePrefix(reference);
            foreach (string path in await media.ListAsync(ReportArea.Pending, prefix, ct).ConfigureAwait(false))
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal)) continue;
                string candidateId = path[prefix.Length..^".json".Length];
                if (!ReportIds.IsValid(candidateId)) continue;
                ResolvedReport? candidate = await LoadAsync(candidateId, ct).ConfigureAwait(false);
                if (candidate is not null) matches.Add(candidate);
            }
        }
        else
        {
            id = input.ToLowerInvariant();
            if (!ReportIds.IsValid(id)) return Result(ReportRevocationStatus.Invalid);
            reference = ReportIds.ReferenceCode(id);
            ResolvedReport? candidate = await LoadAsync(id, ct).ConfigureAwait(false);
            if (candidate is not null) matches.Add(candidate);
        }

        if (matches.Count == 0) return Result(ReportRevocationStatus.NotFound, id, reference);
        if (matches.Count > 1)
            return Result(ReportRevocationStatus.Ambiguous, reference: reference,
                candidates: matches.Select(candidate => candidate.Id).Order(StringComparer.Ordinal).ToArray());
        ResolvedReport report = matches[0];
        id = report.Id;
        if (report.Revoked && report.Record is null && report.Accepted is null)
            return Result(ReportRevocationStatus.AlreadyRevoked, id, reference);

        await revocation.RevokeAsync(id, ct).ConfigureAwait(false);
        int deleted = await DeletePublicFolderAsync(report.Record?.Folder ?? report.Accepted?.Folder, ct).ConfigureAwait(false);
        int? issueNumber = await FindIssueNumberAsync(report.Record, id, ct).ConfigureAwait(false);
        ReportIssueRevocation issue;
        Uri? issueUrl = null;
        try
        {
            (issue, issueUrl) = await RedactIssueAsync(issueNumber, reference, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is GitHubApiException or HttpRequestException
            || exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            issue = ReportIssueRevocation.Failed;
            logger.Warn("Issue redaction failed for revoked report " + id + ". The purge will retry it.", exception);
        }
        if (issueNumber is > 0) issueUrl ??= github.IssuePageUrl(issueNumber.Value);

        DateTimeOffset purgeAt = time.GetUtcNow() + PurgeDelay;
        await work.EnqueueAsync(new ReportWorkItem(ReportWorkKind.PurgeRevoked, id), PurgeDelay, ct).ConfigureAwait(false);
        return new ReportRevocationResult(ReportRevocationStatus.Revoked, id, reference, [], deleted, issue,
            issueNumber, issueUrl, purgeAt);
    }

    /// <summary>Repeats public cleanup and redaction before deleting a revoked report's private state.</summary>
    public async Task PurgeAsync(string reportId, CancellationToken ct)
    {
        if (!await media.ExistsAsync(ReportArea.Pending, ReportLayout.Revoked(reportId), ct).ConfigureAwait(false)) return;
        ReportRecord? record = await records.LoadReportAsync(reportId, ct).ConfigureAwait(false);
        AcceptedReport? accepted = record is null ? await records.LoadAcceptedAsync(reportId, ct).ConfigureAwait(false) : null;
        await DeletePublicFolderAsync(record?.Folder ?? accepted?.Folder, ct).ConfigureAwait(false);

        record = await records.LoadReportAsync(reportId, ct).ConfigureAwait(false);
        int? issueNumber = await FindIssueNumberAsync(record, reportId, ct).ConfigureAwait(false);
        var (issue, issueUrl) = await RedactIssueAsync(issueNumber, ReportIds.ReferenceCode(reportId), ct).ConfigureAwait(false);
        if (issue == ReportIssueRevocation.Redacted && issueNumber is int number)
            logger.Warn("Redacted issue " + number.ToString(CultureInfo.InvariantCulture) + " during purge of revoked report " + reportId + ".");
        else if (issue == ReportIssueRevocation.Moved && issueNumber is int movedNumber)
            logger.Warn("Issue " + movedNumber.ToString(CultureInfo.InvariantCulture) + " moved to " + issueUrl
                + " during purge of revoked report " + reportId + ". Operator deletion is required.");

        await media.DeleteAsync(ReportArea.Pending, ReportLayout.ReportRecord(reportId), ct).ConfigureAwait(false);
        await media.DeleteAsync(ReportArea.Pending, ReportLayout.AcceptedIndex(reportId), ct).ConfigureAwait(false);
        await media.DeleteAsync(ReportArea.Pending, ReportLayout.PendingSnapshot(reportId), ct).ConfigureAwait(false);
        await claims.DeleteAllAsync(ReportWorkKind.CreateIssue, reportId, ct).ConfigureAwait(false);
        await claims.DeleteAllAsync(ReportWorkKind.AppendServerSnapshot, reportId, ct).ConfigureAwait(false);
    }

    async Task<int> DeletePublicFolderAsync(string? folder, CancellationToken ct)
    {
        int deleted = 0;
        if (folder is not null)
            foreach (string path in await media.ListAsync(ReportArea.Public, folder + "/", ct).ConfigureAwait(false))
                if (await media.DeleteAsync(ReportArea.Public, path, ct).ConfigureAwait(false)) deleted++;
        return deleted;
    }

    async Task<int?> FindIssueNumberAsync(ReportRecord? record, string reportId, CancellationToken ct)
    {
        if (record?.IssueNumber is > 0) return record.IssueNumber;
        string? claimed = await claims.FindResultAsync(ReportWorkKind.CreateIssue, reportId, ct).ConfigureAwait(false);
        return int.TryParse(claimed, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0 ? number : null;
    }

    async Task<(ReportIssueRevocation Issue, Uri? Url)> RedactIssueAsync(int? issueNumber, string reference, CancellationToken ct)
    {
        if (issueNumber is null) return (ReportIssueRevocation.NotCreated, null);
        GitHubIssueUpdateResult update = await github.UpdateIssueAsync(issueNumber.Value,
            new GitHubIssueUpdate("Revoked bug report " + reference,
                "This report was revoked at the reporter's request. Its files and private record are being deleted."), ct)
            .ConfigureAwait(false);
        ReportIssueRevocation issue = update.Outcome switch
        {
            GitHubIssueUpdateOutcome.Updated => ReportIssueRevocation.Redacted,
            GitHubIssueUpdateOutcome.Gone => ReportIssueRevocation.Gone,
            GitHubIssueUpdateOutcome.Moved => ReportIssueRevocation.Moved,
            _ => throw new ArgumentOutOfRangeException(nameof(update), update.Outcome, "Unknown issue update outcome."),
        };
        return (issue, update.IssueUrl);
    }

    async Task<ResolvedReport?> LoadAsync(string id, CancellationToken ct)
    {
        ReportRecord? record = await records.LoadReportAsync(id, ct).ConfigureAwait(false);
        AcceptedReport? accepted = await records.LoadAcceptedAsync(id, ct).ConfigureAwait(false);
        bool revoked = await media.ExistsAsync(ReportArea.Pending, ReportLayout.Revoked(id), ct).ConfigureAwait(false);
        return record is null && accepted is null && !revoked ? null : new ResolvedReport(id, record, accepted, revoked);
    }

    static ReportRevocationResult Result(ReportRevocationStatus status, string? id = null, string? reference = null,
        IReadOnlyList<string>? candidates = null) => new(status, id, reference, candidates ?? [], 0, null, null, null, null);

    sealed record ResolvedReport(string Id, ReportRecord? Record, AcceptedReport? Accepted, bool Revoked);
}
