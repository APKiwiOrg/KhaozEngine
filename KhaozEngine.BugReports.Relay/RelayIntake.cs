using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>The ordered trust boundary for client report uploads.</summary>
internal sealed class RelayIntake
{
    static readonly TimeSpan ReenqueueAfter = TimeSpan.FromMinutes(15);
    readonly BugReportRelayOptions options;
    readonly IReporterAuthenticator authenticator;
    readonly IReportWorkQueue work;
    readonly ReportRecordStore records;
    readonly ReportQuotaGate quotas;
    readonly ReportBundleStorer storer;
    readonly TimeProvider time;
    readonly HashSet<string> blockedSubjects;

    public RelayIntake(BugReportRelayOptions options, IReporterAuthenticator authenticator, IReportWorkQueue work,
        ReportRecordStore records, ReportQuotaGate quotas, ReportBundleStorer storer, TimeProvider time)
    {
        this.options = options;
        this.authenticator = authenticator;
        this.work = work;
        this.records = records;
        this.quotas = quotas;
        this.storer = storer;
        this.time = time;
        blockedSubjects = new HashSet<string>(options.BlockedSubjects, StringComparer.Ordinal);
    }

    public async Task<RelayResponse> IntakeAsync(RelayRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var headers = new RequestHeaderReader(request.Headers);
        string? token = headers.BearerToken();
        if (token is null) return RelayResponses.Unauthorized();
        ReporterIdentity? reporter = await authenticator.AuthenticateAsync(token, ct).ConfigureAwait(false);
        if (reporter is null) return RelayResponses.Unauthorized();
        if (blockedSubjects.Contains(reporter.Subject)) return RelayResponses.Error(403, "blocked");
        QuotaDecision quota = await quotas.TryConsumeAsync(reporter.Subject, ct).ConfigureAwait(false);
        if (!quota.Allowed) return RelayResponses.TooManyRequests(quota.RetryAfter);

        string? key = headers.Get(RelayHeaders.IdempotencyKey);
        if (key is null || !ReportIds.IsValid(key)) return RelayResponses.Error(400, "bad_request");
        using MemoryStream? body = await CappedBody.ReadAsync(request.Body, request.ContentLength, options.MaxBundleBytes, ct)
            .ConfigureAwait(false);
        if (body is null) return RelayResponses.Error(413, "too_large");
        BundleValidationResult validation = BundleValidator.Validate(body);
        if (!validation.IsValid) return RelayResponses.Error(422, validation.Error!);
        BugReportManifest manifest = validation.Manifest!;
        if (!string.Equals(manifest.ReportId, key, StringComparison.Ordinal)) return RelayResponses.Error(400, "id_mismatch");
        if (!string.Equals(manifest.GameId, options.GameId, StringComparison.Ordinal)) return RelayResponses.Error(422, "game_mismatch");

        AcceptedReport? accepted = await records.LoadAcceptedAsync(key, ct).ConfigureAwait(false);
        if (accepted is not null) return await AcceptedAsync(key, accepted, ct).ConfigureAwait(false);
        ReportRecord? record = await records.LoadReportAsync(key, ct).ConfigureAwait(false);
        if (record is not null) return await CompleteAcceptanceAsync(record, ct).ConfigureAwait(false);

        record = await storer.StoreAsync(body, manifest, reporter, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (!await records.CreateReportAsync(record, ct).ConfigureAwait(false))
            record = await records.LoadReportAsync(key, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The winning report record is missing.");
        return await CompleteAcceptanceAsync(record, ct).ConfigureAwait(false);
    }

    async Task<RelayResponse> CompleteAcceptanceAsync(ReportRecord record, CancellationToken ct)
    {
        string key = record.ReportId;
        if (!await records.TryCreateAcceptedAsync(key, new AcceptedReport(record.Folder, record.Reporter.Subject, record.ReceivedAt), ct).ConfigureAwait(false))
        {
            AcceptedReport winner = await records.LoadAcceptedAsync(key, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The winning accepted index is missing.");
            return await AcceptedAsync(key, winner, ct).ConfigureAwait(false);
        }
        await work.EnqueueAsync(new ReportWorkItem(ReportWorkKind.CreateIssue, key), ct).ConfigureAwait(false);
        return RelayResponses.Accepted(key);
    }

    async Task<RelayResponse> AcceptedAsync(string reportId, AcceptedReport accepted, CancellationToken ct)
    {
        ReportRecord? record = await records.LoadReportAsync(reportId, ct).ConfigureAwait(false);
        if (record is { IssueNumber: null } && time.GetUtcNow() - accepted.ReceivedAt > ReenqueueAfter)
            await work.EnqueueAsync(new ReportWorkItem(ReportWorkKind.CreateIssue, reportId), ct).ConfigureAwait(false);
        return RelayResponses.Accepted(reportId);
    }
}
