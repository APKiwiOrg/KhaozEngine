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
        int deleted = 0;
        string? folder = report.Record?.Folder ?? report.Accepted?.Folder;
        if (folder is not null)
            foreach (string path in await media.ListAsync(ReportArea.Public, folder + "/", ct).ConfigureAwait(false))
                if (await media.DeleteAsync(ReportArea.Public, path, ct).ConfigureAwait(false)) deleted++;

        int? issueNumber = report.Record?.IssueNumber;
        if (issueNumber is not > 0)
        {
            string? claimed = await claims.FindResultAsync(ReportWorkKind.CreateIssue, id, ct).ConfigureAwait(false);
            issueNumber = int.TryParse(claimed, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0
                ? number : null;
        }
        ReportIssueRevocation issue = ReportIssueRevocation.NotCreated;
        Uri? issueUrl = null;
        if (issueNumber is not null)
        {
            try
            {
                GitHubIssueUpdateResult update = await github.UpdateIssueAsync(issueNumber.Value,
                    new GitHubIssueUpdate("Revoked bug report " + reference,
                        "This report was revoked at the reporter's request. Its files and private record are being deleted."), ct)
                    .ConfigureAwait(false);
                issue = update.Outcome switch
                {
                    GitHubIssueUpdateOutcome.Updated => ReportIssueRevocation.Redacted,
                    GitHubIssueUpdateOutcome.Gone => ReportIssueRevocation.Gone,
                    GitHubIssueUpdateOutcome.Moved => ReportIssueRevocation.Moved,
                    _ => throw new ArgumentOutOfRangeException(nameof(update), update.Outcome, "Unknown issue update outcome."),
                };
                issueUrl = update.IssueUrl;
            }
            catch (Exception exception) when (exception is GitHubApiException or HttpRequestException)
            {
                issue = ReportIssueRevocation.Failed;
                logger.Warn("Issue redaction failed for revoked report " + id + ". The purge will retry it.", exception);
            }
        }

        DateTimeOffset purgeAt = time.GetUtcNow() + PurgeDelay;
        await work.EnqueueAsync(new ReportWorkItem(ReportWorkKind.PurgeRevoked, id), PurgeDelay, ct).ConfigureAwait(false);
        return new ReportRevocationResult(ReportRevocationStatus.Revoked, id, reference, [], deleted, issue,
            issueNumber, issueUrl, purgeAt);
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
