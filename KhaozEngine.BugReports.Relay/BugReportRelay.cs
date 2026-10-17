using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Authenticates report intake and hands accepted reports to durable background work.</summary>
public sealed class BugReportRelay
{
    readonly RelayIntake intake;
    readonly ServerSnapshotIntake snapshots;
    readonly ReportWorker worker;

    /// <summary>Creates a relay over host-provided authentication, storage, queue and GitHub seams.</summary>
    /// <exception cref="ArgumentException">An option is outside the relay's supported limits.</exception>
    public BugReportRelay(BugReportRelayOptions options, IReporterAuthenticator authenticator, IReportMediaStore media,
        IReportWorkQueue work, IReportQuotaStore quotas, IGitHubIssueClient github, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(quotas);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(time);
        Validate(options);
        ILogger logger = options.Logger ?? Log.Get("BugReports.Relay");
        var records = new ReportRecordStore(media);
        var viewer = new ReportViewerWriter(media);
        intake = new RelayIntake(options, authenticator, work, records,
            new ReportQuotaGate(quotas, options, time, logger), new ReportBundleStorer(media, viewer), time);
        snapshots = new ServerSnapshotIntake(options, records, work, logger);
        worker = new ReportWorker(options, media, records, viewer, github, time, logger);
    }

    /// <summary>Authenticates, quota-checks, validates and stores one client bundle, then queues issue creation.</summary>
    /// <remarks>
    /// Resending an accepted report consumes quota because quota is checked before bundle validation and idempotency.
    /// A report without an issue is re-enqueued only after 15 minutes. See <see cref="ProcessWorkAsync"/> for the
    /// worker claim and crash-recovery behavior.
    /// </remarks>
    public Task<RelayResponse> IntakeAsync(RelayRequest request, CancellationToken cancellationToken)
        => intake.IntakeAsync(request, cancellationToken);

    /// <summary>Authenticates and stores the first server snapshot, then queues a late append when accepted.</summary>
    public Task<RelayResponse> ServerSnapshotIntakeAsync(string reportId, RelayRequest request, CancellationToken cancellationToken)
        => snapshots.RunAsync(reportId, request, cancellationToken);

    /// <summary>Processes durable issue work using create-only, ten-minute claims.</summary>
    /// <remarks>
    /// Duplicate deliveries during a claim lease do nothing. After expiry a new delivery may take over. A holder
    /// that creates an issue or comment and dies before saving its result can leave a duplicate on takeover.
    /// </remarks>
    public Task ProcessWorkAsync(ReportWorkItem item, CancellationToken cancellationToken)
        => worker.ProcessAsync(item, cancellationToken);

    static void Validate(BugReportRelayOptions options)
    {
        string? gameId = options.GameId;
        if (gameId is null || gameId.Length is < 1 or > BugReportLimits.MaxGameIdChars)
            throw new ArgumentException("The game id must match [a-z0-9-]{1,32}.", nameof(options));
        foreach (char c in gameId)
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                throw new ArgumentException("The game id must match [a-z0-9-]{1,32}.", nameof(options));
        if (options.ServiceKey is null || options.ServiceKey.Length < 32)
            throw new ArgumentException("The service key must hold at least 32 characters.", nameof(options));
        Uri? mediaBase = options.PublicMediaBase;
        if (mediaBase is null || !mediaBase.IsAbsoluteUri || mediaBase.AbsoluteUri.Length > 512
            || !mediaBase.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            || (mediaBase.Scheme != Uri.UriSchemeHttps && !mediaBase.IsLoopback))
            throw new ArgumentException("The public media base must be absolute, at most 512 characters, end in '/', and use HTTPS off loopback.", nameof(options));
        if (options.PerAccountPerHour <= 0 || options.PerAccountPerDay <= 0 || options.GlobalPerDay <= 0
            || options.MaxBundleBytes <= 0 || options.MaxBundleBytes > BugReportLimits.MaxBundleBytes)
            throw new ArgumentException("Every limit must be positive and the bundle cap must not exceed the format limit.", nameof(options));
        ArgumentNullException.ThrowIfNull(options.BlockedSubjects);
    }
}
