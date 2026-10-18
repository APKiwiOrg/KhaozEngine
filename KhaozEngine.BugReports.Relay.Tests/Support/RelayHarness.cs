using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal sealed class RelayHarness
{
    public const string Id = "0192f3a4b5c67d8e9f0a1b2c3d4e5f60";
    public const string Reference = "R-YMWQV0";
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero));
    public InMemoryReportMediaStore Media { get; } = new();
    public InMemoryReportWorkQueue Work { get; } = new();
    public InMemoryReportQuotaStore Quotas { get; } = new();
    public FakeAuthenticator Authenticator { get; } = new();
    public RecordingGitHubIssueClient GitHub { get; } = new();
    public RecordingLogger Logger { get; } = new();
    public ReportRecordStore Records => new(Media);
    public BugReportRelayOptions Options { get; }
    public BugReportRelay Relay { get; }

    public static BugReportRelayOptions DefaultOptions() => new()
    {
        GameId = "grimhollow",
        GameDisplayName = "Grimhollow",
        RepositoryOwner = "example",
        RepositoryName = "reports",
        Labels = new[] { "kind/bug" },
        PublicMediaBase = new Uri("https://media.example.test/reports/"),
        ServiceKey = new string('s', 32),
    };

    public RelayHarness(Func<BugReportRelayOptions, BugReportRelayOptions>? configure = null,
        Func<InMemoryReportMediaStore, IReportMediaStore>? wrapMedia = null,
        Func<InMemoryReportWorkQueue, IReportWorkQueue>? wrapWork = null)
    {
        Options = (configure?.Invoke(DefaultOptions()) ?? DefaultOptions()) with { Logger = Logger };
        Relay = new BugReportRelay(Options, Authenticator, wrapMedia?.Invoke(Media) ?? Media,
            wrapWork?.Invoke(Work) ?? Work, Quotas, GitHub, Clock);
    }

    public static RelayRequest Request(byte[]? bundle = null, string? authorization = "Bearer good", string? id = Id,
        Stream? body = null, long? contentLength = null)
    {
        var headers = new Dictionary<string, string>();
        if (authorization is not null) headers.Add("Authorization", authorization);
        if (id is not null) headers.Add(RelayHeaders.IdempotencyKey, id);
        return new RelayRequest(headers, body ?? new MemoryStream(bundle ?? TestBundles.Valid(Id), writable: false), contentLength);
    }

    public Task<RelayResponse> IntakeAsync(RelayRequest? request = null, CancellationToken ct = default)
        => Relay.IntakeAsync(request ?? Request(), ct);
}
