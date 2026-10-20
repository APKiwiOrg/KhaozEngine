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
    readonly BugReportRelayOptions options;
    readonly IReporterAuthenticator authenticator;
    readonly IReportMediaStore media;
    readonly IReportWorkQueue work;
    readonly ReportRecordStore records;
    readonly ReportQuotaGate quotas;
    readonly ReportBundleStorer storer;
    readonly TimeProvider time;
    readonly ReportRevocation revocation;
    readonly HashSet<string> blockedSubjects;

    public RelayIntake(BugReportRelayOptions options, IReporterAuthenticator authenticator, IReportMediaStore media, IReportWorkQueue work,
        ReportRecordStore records, ReportQuotaGate quotas, ReportBundleStorer storer, TimeProvider time, ReportRevocation revocation)
    {
        this.options = options;
        this.authenticator = authenticator;
        this.media = media;
        this.work = work;
        this.records = records;
        this.quotas = quotas;
        this.storer = storer;
        this.time = time;
        this.revocation = revocation;
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

        if (await revocation.IsRevokedAsync(key, ct).ConfigureAwait(false)) return RelayResponses.Accepted(key);
        AcceptedReport? accepted = await records.LoadAcceptedAsync(key, ct).ConfigureAwait(false);
        if (accepted is not null)
        {
            if (await revocation.IsRevokedAsync(key, ct, accepted.Folder).ConfigureAwait(false)) return RelayResponses.Accepted(key);
            return await AcceptedAsync(key, ct).ConfigureAwait(false);
        }
        ReportRecord? record = await records.LoadReportAsync(key, ct).ConfigureAwait(false);
        if (record is not null)
        {
            if (await revocation.IsRevokedAsync(key, ct, record.Folder).ConfigureAwait(false)) return RelayResponses.Accepted(key);
            return await CompleteAcceptanceAsync(record, ct).ConfigureAwait(false);
        }

        record = await storer.StoreAsync(body, manifest, reporter, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (!await records.CreateReportAsync(record, ct).ConfigureAwait(false))
        {
            foreach (string path in await media.ListAsync(ReportArea.Public, record.Folder + "/", ct).ConfigureAwait(false))
                await media.DeleteAsync(ReportArea.Public, path, ct).ConfigureAwait(false);
            record = await records.LoadReportAsync(key, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The winning report record is missing.");
        }
        return await CompleteAcceptanceAsync(record, ct).ConfigureAwait(false);
    }

    async Task<RelayResponse> CompleteAcceptanceAsync(ReportRecord record, CancellationToken ct)
    {
        string key = record.ReportId;
        string referencePath = ReportLayout.ReferenceIndex(record.Reference, key);
        await media.PutAsync(ReportArea.Pending, referencePath, "{}"u8.ToArray(),
            ReportLayout.HeadersFor(referencePath), overwrite: true, ct).ConfigureAwait(false);
        if (!await records.TryCreateAcceptedAsync(key, new AcceptedReport(record.Folder, record.Reporter.Subject, record.ReceivedAt), ct).ConfigureAwait(false))
        {
            _ = await records.LoadAcceptedAsync(key, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("The winning accepted index is missing.");
            return await AcceptedAsync(key, ct).ConfigureAwait(false);
        }
        await work.EnqueueAsync(new ReportWorkItem(ReportWorkKind.CreateIssue, key), ct).ConfigureAwait(false);
        return RelayResponses.Accepted(key);
    }

    async Task<RelayResponse> AcceptedAsync(string reportId, CancellationToken ct)
    {
        ReportRecord? record = await records.LoadReportAsync(reportId, ct).ConfigureAwait(false);
        if (record is { IssueNumber: null } && !await revocation.IsRevokedAsync(reportId, ct, record.Folder).ConfigureAwait(false))
            await work.EnqueueAsync(new ReportWorkItem(ReportWorkKind.CreateIssue, reportId), ct).ConfigureAwait(false);
        return RelayResponses.Accepted(reportId);
    }
}
