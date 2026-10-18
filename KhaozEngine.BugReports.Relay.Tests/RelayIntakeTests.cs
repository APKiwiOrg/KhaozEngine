using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class RelayIntakeTests
{
    const string Id = RelayHarness.Id;
    static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Intake_MissingAuthorization_401_BodyNeverRead()
    {
        var harness = new RelayHarness();
        using var body = new ThrowingStream();
        RelayResponse response = await harness.IntakeAsync(RelayHarness.Request(authorization: null, body: body));
        AssertError(response, 401, "unauthorized");
        Assert.Equal("Bearer", response.Headers["WWW-Authenticate"]);
        Assert.Equal(0, body.ReadCalls);
        Assert.Null(harness.Authenticator.LastToken);
        await AssertQuotaUntouched(harness);
    }

    [Theory]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    [InlineData("Bearer   ")]
    [InlineData("Bearerish good")]
    public async Task Intake_NonBearerOrEmptyToken_401(string authorization)
    {
        using var body = new ThrowingStream();
        var harness = new RelayHarness();
        AssertError(await harness.IntakeAsync(RelayHarness.Request(authorization: authorization, body: body)), 401, "unauthorized");
        Assert.Equal(0, body.ReadCalls);
        Assert.Null(harness.Authenticator.LastToken);
    }

    [Fact]
    public async Task Intake_UnknownToken_401_BodyNeverRead()
    {
        var harness = new RelayHarness();
        using var body = new ThrowingStream();
        AssertError(await harness.IntakeAsync(RelayHarness.Request(authorization: "Bearer expired", body: body)), 401, "unauthorized");
        Assert.Equal(0, body.ReadCalls);
        await AssertQuotaUntouched(harness);
    }

    [Fact]
    public async Task Intake_AuthenticatorThrows_Propagates_BodyNeverRead()
    {
        var harness = new RelayHarness();
        var failure = new IOException("Authentication unavailable");
        harness.Authenticator.Failure = failure;
        using var body = new ThrowingStream();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => harness.IntakeAsync(RelayHarness.Request(body: body))));
        Assert.Equal(0, body.ReadCalls);
        await AssertQuotaUntouched(harness);
    }

    [Fact]
    public async Task Intake_HeaderNamesInAnyCase_AndTrimmedBearer_Accepted()
    {
        var harness = new RelayHarness();
        using var body = new MemoryStream(TestBundles.Valid(Id));
        var request = new RelayRequest(new Dictionary<string, string>
        {
            ["authorization"] = "bEaReR   good  ",
            ["IDEMPOTENCY-KEY"] = Id,
        }, body, body.Length);
        AssertAccepted(await harness.IntakeAsync(request));
        Assert.Equal("good", harness.Authenticator.LastToken);
    }

    [Fact]
    public async Task Intake_BlockedSubject_403_BodyNeverRead_QuotaUntouched()
    {
        var harness = new RelayHarness(o => o with { BlockedSubjects = new HashSet<string> { "discord:42" } });
        using var body = new ThrowingStream();
        AssertError(await harness.IntakeAsync(RelayHarness.Request(body: body)), 403, "blocked");
        Assert.Equal(0, body.ReadCalls);
        await AssertQuotaUntouched(harness);
    }

    [Fact]
    public async Task Intake_BlockedSubjects_UsesOrdinalComparison()
    {
        var harness = new RelayHarness(o => o with
        {
            BlockedSubjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DISCORD:42" },
        });
        AssertAccepted(await harness.IntakeAsync());
    }

    [Fact]
    public async Task Intake_OverHourlyQuota_429WithRetryAfter_BodyNeverRead()
    {
        var harness = new RelayHarness();
        for (int i = 0; i < 5; i++)
            AssertError(await harness.IntakeAsync(RelayHarness.Request(id: null, body: new ThrowingStream())), 400, "bad_request");
        using var body = new ThrowingStream();
        RelayResponse response = await harness.IntakeAsync(RelayHarness.Request(body: body));
        AssertError(response, 429, "quota");
        Assert.Equal("3477", response.Headers["Retry-After"]);
        Assert.Equal(0, body.ReadCalls);
    }

    [Fact]
    public async Task Intake_ContentLengthOverCap_413_BodyNeverRead()
    {
        var harness = new RelayHarness(o => o with { MaxBundleBytes = 64 });
        using var body = new ThrowingStream();
        AssertError(await harness.IntakeAsync(RelayHarness.Request(body: body, contentLength: 65)), 413, "too_large");
        Assert.Equal(0, body.ReadCalls);
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    public async Task Intake_StreamOverCap_413_ReadsAtMostCapPlusOne(long? declaredLength)
    {
        var harness = new RelayHarness(o => o with { MaxBundleBytes = 64 });
        using var body = new ThrowingStream(new byte[1024]);
        AssertError(await harness.IntakeAsync(RelayHarness.Request(body: body, contentLength: declaredLength)), 413, "too_large");
        Assert.Equal(65, body.BytesRead);
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
    }

    [Fact]
    public async Task Intake_ExactlyAtConfiguredCap_Accepted()
    {
        byte[] bundle = TestBundles.Valid(Id);
        var harness = new RelayHarness(o => o with { MaxBundleBytes = bundle.Length });
        using var body = new ThrowingStream(bundle);
        AssertAccepted(await harness.IntakeAsync(RelayHarness.Request(body: body, contentLength: bundle.Length)));
        Assert.Equal(bundle.Length, body.BytesRead);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-report")]
    public async Task Intake_MissingOrInvalidIdempotencyKey_400_BodyNeverRead(string? key)
    {
        using var body = new ThrowingStream();
        AssertError(await new RelayHarness().IntakeAsync(RelayHarness.Request(id: key, body: body)), 400, "bad_request");
        Assert.Equal(0, body.ReadCalls);
    }

    [Fact]
    public async Task Intake_IdempotencyKeyDiffersFromManifest_400()
    {
        var harness = new RelayHarness();
        AssertError(await harness.IntakeAsync(RelayHarness.Request(id: "0192f3a4b5c67d8e9f0a1b2c3d4e5f61")), 400, "id_mismatch");
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
    }

    [Fact]
    public async Task Intake_TraversalEntry_422()
    {
        var harness = new RelayHarness();
        AssertError(await harness.IntakeAsync(RelayHarness.Request(TestBundles.Traversal())), 422, "entry_not_allowed: ../escape.json");
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
    }

    [Fact]
    public async Task Intake_ScreenshotWithBadMagic_422()
    {
        byte[] bundle = TestBundles.WithEntryBytes(TestBundles.Valid(Id), "screenshot.png", "GIF89a..."u8.ToArray());
        AssertError(await new RelayHarness().IntakeAsync(RelayHarness.Request(bundle)), 422, "image_invalid: screenshot.png");
    }

    [Fact]
    public async Task Intake_GameMismatch_422()
        => AssertError(await new RelayHarness().IntakeAsync(RelayHarness.Request(TestBundles.Valid(Id, "other-game"))), 422, "game_mismatch");

    [Fact]
    public async Task Intake_Valid_202_StoresAndEnqueues()
    {
        var harness = new RelayHarness();
        byte[] bundle = TestBundles.Valid(Id);
        AssertAccepted(await harness.IntakeAsync(RelayHarness.Request(bundle)));
        AcceptedReport accepted = Assert.IsType<AcceptedReport>(await harness.Records.LoadAcceptedAsync(Id, Ct));
        Assert.StartsWith("grimhollow/2026-10/" + Id + "-", accepted.Folder, StringComparison.Ordinal);
        Assert.Equal("discord:42", accepted.Subject);
        Assert.Equal(harness.Clock.GetUtcNow(), accepted.ReceivedAt);
        using var zip = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using Stream input = entry.Open();
            using var expected = new MemoryStream();
            await input.CopyToAsync(expected);
            ReadOnlyMemory<byte>? stored = await harness.Media.GetAsync(ReportArea.Public, accepted.Folder + "/" + entry.FullName, bundle.Length, Ct);
            Assert.NotNull(stored);
            Assert.Equal(expected.ToArray(), stored.Value.ToArray());
        }
        Assert.Equal(zip.Entries.Count + 1, harness.Media.Paths(ReportArea.Public).Count);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Public, accepted.Folder + "/viewer.html", Ct));
        Assert.Equal(new[] { "accepted/" + Id + ".json", "records/" + Id + ".json" }, harness.Media.Paths(ReportArea.Pending));
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? work));
        Assert.Equal(new ReportWorkItem(ReportWorkKind.CreateIssue, Id), work);
        Assert.Equal(0, harness.Work.Count);
        Assert.Empty(harness.GitHub.Issues);
    }

    [Fact]
    public async Task Intake_StoredBlobs_HaveExplicitContentTypes()
    {
        var harness = new RelayHarness();
        await harness.IntakeAsync();
        foreach (string path in harness.Media.Paths(ReportArea.Public))
        {
            ReportBlobHeaders expected = Path.GetExtension(path) switch
            {
                ".json" => new("application/json; charset=utf-8", "no-cache"),
                ".html" => new("text/html; charset=utf-8", "no-cache"),
                ".png" => new("image/png", "public, max-age=86400"),
                ".jpg" => new("image/jpeg", "public, max-age=86400"),
                _ => throw new InvalidOperationException(path),
            };
            Assert.Equal(expected, harness.Media.HeadersOf(ReportArea.Public, path));
        }
    }

    [Fact]
    public async Task Intake_RecordIdentityComesFromTokenNotBundle()
    {
        var harness = new RelayHarness();
        await harness.IntakeAsync();
        ReportRecord record = Assert.IsType<ReportRecord>(await harness.Records.LoadReportAsync(Id, Ct));
        Assert.Equal(new ReporterIdentity("discord:42", "Ash", null), record.Reporter);
        Assert.Equal(harness.Clock.GetUtcNow(), record.ReceivedAt);
        Assert.NotEqual(record.Manifest.CapturedAt, record.ReceivedAt);
        Assert.Null(record.IssueNumber);
        Assert.Equal(ServerSnapshotState.None, record.ServerSnapshot);
        Assert.Equal(RelayHarness.Reference, record.Reference);
    }

    [Fact]
    public async Task Intake_PublicFolderHoldsNoReporterIdentity()
    {
        var harness = new RelayHarness();
        await harness.IntakeAsync();
        foreach (string path in harness.Media.Paths(ReportArea.Public))
        {
            ReadOnlyMemory<byte>? bytes = await harness.Media.GetAsync(ReportArea.Public, path, (int)BugReportLimits.MaxBundleBytes, Ct);
            string content = Encoding.UTF8.GetString(bytes!.Value.Span);
            Assert.DoesNotContain("discord:42", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Ash", content, StringComparison.Ordinal);
            Assert.False(path.EndsWith("/report.json", StringComparison.Ordinal));
        }
        using JsonDocument viewer = await ViewerData(harness);
        Assert.Equal(JsonValueKind.Null, viewer.RootElement.GetProperty("reporter").ValueKind);
    }

    [Fact]
    public async Task Intake_ViewerContainsSectionsClipAndLogTail()
    {
        var harness = new RelayHarness();
        await harness.IntakeAsync();
        using JsonDocument viewer = await ViewerData(harness);
        JsonElement data = viewer.RootElement;
        Assert.Equal(3, data.GetProperty("clip").GetProperty("frames").GetArrayLength());
        Assert.Equal(3, data.GetProperty("sections").EnumerateObject().Count());
        Assert.Contains("0.12.3", data.GetProperty("sections").GetProperty("build").GetString(), StringComparison.Ordinal);
        Assert.Equal(new[] { "connected", "tick 42" }, data.GetProperty("logTail").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Intake_SameIdTwice_SameReference_NoSecondFolder()
    {
        var harness = new RelayHarness();
        RelayResponse first = await harness.IntakeAsync();
        string[] publicPaths = harness.Media.Paths(ReportArea.Public).ToArray();
        string[] privatePaths = harness.Media.Paths(ReportArea.Pending).ToArray();
        RelayResponse second = await harness.IntakeAsync();
        AssertAccepted(second);
        Assert.Equal(first.JsonBody, second.JsonBody);
        Assert.Equal(publicPaths, harness.Media.Paths(ReportArea.Public));
        Assert.Equal(privatePaths, harness.Media.Paths(ReportArea.Pending));
    }

    [Fact]
    public async Task Intake_UnfinishedDuplicate_EnqueuesImmediately()
    {
        var harness = new RelayHarness();
        await harness.IntakeAsync();
        Assert.True(harness.Work.TryDequeue(out _));
        AssertAccepted(await harness.IntakeAsync());
        Assert.Equal(1, harness.Work.Count);
    }

    [Fact]
    public async Task Intake_OldUnfinishedDuplicate_Enqueues()
    {
        var harness = new RelayHarness();
        await harness.IntakeAsync();
        Assert.True(harness.Work.TryDequeue(out _));
        harness.Clock.Advance(TimeSpan.FromHours(1));
        AssertAccepted(await harness.IntakeAsync());
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? work));
        Assert.Equal(new ReportWorkItem(ReportWorkKind.CreateIssue, Id), work);
        Assert.Equal(0, harness.Work.Count);
    }

    [Fact]
    public async Task Intake_AcceptedWithoutRecord_202WithoutEnqueue()
    {
        var harness = new RelayHarness();
        await harness.Records.TryCreateAcceptedAsync(Id,
            new AcceptedReport("grimhollow/2026-10/" + Id + "-00000000000000000000000000000000", "discord:42", harness.Clock.GetUtcNow().AddDays(-1)), Ct);
        AssertAccepted(await harness.IntakeAsync());
        Assert.Equal(0, harness.Work.Count);
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
        Assert.Null(await harness.Records.LoadReportAsync(Id, Ct));
    }

    [Fact]
    public async Task Intake_DuplicateAfterIssueCreated_DoesNotEnqueue()
    {
        var harness = new RelayHarness();
        await harness.IntakeAsync();
        Assert.True(harness.Work.TryDequeue(out _));
        ReportRecord record = (await harness.Records.LoadReportAsync(Id, Ct))!;
        await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
        harness.Clock.Advance(TimeSpan.FromHours(1));
        AssertAccepted(await harness.IntakeAsync());
        Assert.Equal(0, harness.Work.Count);
        Assert.Equal(7, (await harness.Records.LoadReportAsync(Id, Ct))!.IssueNumber);
    }

    [Fact]
    public async Task Intake_ResendThreeDaysLater_SameReference_NothingNewStored()
    {
        var harness = new RelayHarness();
        RelayResponse first = await harness.IntakeAsync();
        ReportRecord record = (await harness.Records.LoadReportAsync(Id, Ct))!;
        await harness.Records.SaveReportAsync(record with { IssueNumber = 7 }, Ct);
        var paths = harness.Media.Paths(ReportArea.Public).ToArray();
        ReadOnlyMemory<byte>? savedRecord = await harness.Media.GetAsync(ReportArea.Pending, "records/" + Id + ".json", ReportLayout.MaxRecordBytes, Ct);
        Assert.True(harness.Work.TryDequeue(out _));
        harness.Clock.Advance(TimeSpan.FromDays(3));
        RelayResponse response = await harness.IntakeAsync();
        AssertAccepted(response);
        Assert.Equal(first.JsonBody, response.JsonBody);
        Assert.Equal(paths, harness.Media.Paths(ReportArea.Public));
        Assert.Equal(savedRecord!.Value.ToArray(), (await harness.Media.GetAsync(ReportArea.Pending, "records/" + Id + ".json", ReportLayout.MaxRecordBytes, Ct))!.Value.ToArray());
        Assert.Equal(0, harness.Work.Count);
    }

    [Fact]
    public async Task Intake_ConcurrentSameId_OneRecordWins_NoOverwrite()
    {
        RelayHarness harness = null!;
        ReportRecord winner = null!;
        byte[] savedWinner = null!;
        harness = new RelayHarness(wrapMedia: inner => new HookedMediaStore(inner, "records/", async () =>
        {
            Assert.Null(await harness.Records.LoadReportAsync(Id, Ct));
            AssertAccepted(await harness.IntakeAsync());
            winner = (await harness.Records.LoadReportAsync(Id, Ct))! with
            {
                IssueNumber = 7,
                ServerSnapshot = ServerSnapshotState.Commented,
            };
            await harness.Records.SaveReportAsync(winner, Ct);
            savedWinner = (await inner.GetAsync(ReportArea.Pending, "records/" + Id + ".json", ReportLayout.MaxRecordBytes, Ct))!.Value.ToArray();
            Assert.True(harness.Work.TryDequeue(out _));
        }));

        AssertAccepted(await harness.IntakeAsync(RelayHarness.Request(authorization: "Bearer other")));

        ReportRecord record = Assert.IsType<ReportRecord>(await harness.Records.LoadReportAsync(Id, Ct));
        AcceptedReport accepted = Assert.IsType<AcceptedReport>(await harness.Records.LoadAcceptedAsync(Id, Ct));
        Assert.Single(harness.Media.Paths(ReportArea.Pending), path => path.StartsWith("records/", StringComparison.Ordinal));
        Assert.Equal(winner.Folder, record.Folder);
        Assert.Equal(new ReporterIdentity("discord:42", "Ash", null), record.Reporter);
        Assert.Equal(7, record.IssueNumber);
        Assert.Equal(ServerSnapshotState.Commented, record.ServerSnapshot);
        Assert.Equal(savedWinner, (await harness.Media.GetAsync(ReportArea.Pending, "records/" + Id + ".json", ReportLayout.MaxRecordBytes, Ct))!.Value.ToArray());
        Assert.Equal(new AcceptedReport(winner.Folder, winner.Reporter.Subject, winner.ReceivedAt), accepted);
        Assert.Equal(0, harness.Work.Count);
        Assert.Equal(2, harness.Media.Paths(ReportArea.Public).Count(path => path.EndsWith("/viewer.html", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Intake_RecordWithoutAcceptedIndex_CompletesIndexAndEnqueues()
    {
        var harness = new RelayHarness();
        ReportRecord original = IssueFixtures.Minimal().Report;
        await harness.Records.SaveReportAsync(original, Ct);
        byte[] savedRecord = (await harness.Media.GetAsync(ReportArea.Pending, "records/" + Id + ".json", ReportLayout.MaxRecordBytes, Ct))!.Value.ToArray();

        AssertAccepted(await harness.IntakeAsync(RelayHarness.Request(authorization: "Bearer other")));

        Assert.Equal(new AcceptedReport(original.Folder, original.Reporter.Subject, original.ReceivedAt),
            await harness.Records.LoadAcceptedAsync(Id, Ct));
        Assert.Equal(savedRecord, (await harness.Media.GetAsync(ReportArea.Pending, "records/" + Id + ".json", ReportLayout.MaxRecordBytes, Ct))!.Value.ToArray());
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? work));
        Assert.Equal(new ReportWorkItem(ReportWorkKind.CreateIssue, Id), work);
        Assert.Equal(0, harness.Work.Count);
    }

    [Fact]
    public async Task Intake_AcceptedIndexLostRace_ReturnsWinnersReference()
    {
        var media = new InMemoryReportMediaStore();
        ReportRecord? winner = null;
        var harness = new RelayHarness(wrapMedia: inner =>
        {
            media = inner;
            return new HookedMediaStore(inner, "accepted/", async () =>
            {
                var store = new ReportRecordStore(inner);
                ReportRecord own = (await store.LoadReportAsync(Id, Ct))!;
                winner = own with
                {
                    Folder = "grimhollow/2026-10/" + Id + "-00000000000000000000000000000000",
                    Reporter = new ReporterIdentity("discord:7", "Bo", "bo#2"),
                    IssueNumber = 7,
                };
                await store.SaveReportAsync(winner, Ct);
                Assert.True(await store.TryCreateAcceptedAsync(Id, new AcceptedReport(winner.Folder, "discord:7", own.ReceivedAt), Ct));
            });
        });
        AssertAccepted(await harness.IntakeAsync());
        Assert.NotNull(winner);
        Assert.Equal(winner.Folder, (await harness.Records.LoadAcceptedAsync(Id, Ct))!.Folder);
        Assert.Equal(winner.Reporter, (await harness.Records.LoadReportAsync(Id, Ct))!.Reporter);
        Assert.Equal(7, (await harness.Records.LoadReportAsync(Id, Ct))!.IssueNumber);
        Assert.Equal(0, harness.Work.Count);
        Assert.NotEmpty(media.Paths(ReportArea.Public));
    }

    [Fact]
    public async Task Intake_ResendStillConsumesQuotaAndValidatesBody()
    {
        var harness = new RelayHarness(o => o with { PerAccountPerHour = 2 });
        await harness.IntakeAsync();
        AssertError(await harness.IntakeAsync(RelayHarness.Request(TestBundles.Traversal())), 422, "entry_not_allowed: ../escape.json");
        using var body = new ThrowingStream();
        AssertError(await harness.IntakeAsync(RelayHarness.Request(body: body)), 429, "quota");
        Assert.Equal(0, body.ReadCalls);
        Assert.Equal(1, harness.Work.Count);
    }

    [Fact]
    public async Task Intake_CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var harness = new RelayHarness();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.IntakeAsync(ct: cancellation.Token));
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Constructor_ShortServiceKey_Throws(int length)
        => Assert.Throws<ArgumentException>(() => new RelayHarness(o => o with { ServiceKey = new string('s', length) }));

    [Theory]
    [InlineData("http://media.example.test/reports/")]
    [InlineData("/reports/")]
    [InlineData("https://media.example.test/reports")]
    public void Constructor_InvalidMediaBase_Throws(string value)
        => Assert.Throws<ArgumentException>(() => new RelayHarness(o => o with { PublicMediaBase = new Uri(value, UriKind.RelativeOrAbsolute) }));

    [Theory]
    [InlineData("http://localhost/reports/")]
    [InlineData("http://127.0.0.1/reports/")]
    [InlineData("http://[::1]/reports/")]
    public void Constructor_HttpLoopback_Accepted(string value)
        => Assert.NotNull(new RelayHarness(o => o with { PublicMediaBase = new Uri(value) }).Relay);

    [Theory]
    [InlineData("")]
    [InlineData("Game")]
    [InlineData("game_id")]
    [InlineData("abcdefghijklmnopqrstuvwxyz1234567")]
    public void Constructor_InvalidGameId_Throws(string id)
        => Assert.Throws<ArgumentException>(() => new RelayHarness(o => o with { GameId = id }));

    [Theory]
    [InlineData(0, 20, 100, 1024L)]
    [InlineData(5, -1, 100, 1024L)]
    [InlineData(5, 20, 0, 1024L)]
    [InlineData(5, 20, 100, 0L)]
    [InlineData(5, 20, 100, 16777217L)]
    public void Constructor_InvalidLimits_Throws(int hourly, int daily, int global, long bytes)
        => Assert.Throws<ArgumentException>(() => new RelayHarness(o => o with
        {
            PerAccountPerHour = hourly,
            PerAccountPerDay = daily,
            GlobalPerDay = global,
            MaxBundleBytes = bytes,
        }));

    static void AssertError(RelayResponse response, int status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
        Assert.NotNull(response.JsonBody);
        Assert.Equal(error, BugReportJson.DeserializeRelayError(Encoding.UTF8.GetBytes(response.JsonBody)).Error);
    }

    static void AssertAccepted(RelayResponse response)
    {
        Assert.Equal(202, response.StatusCode);
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
        Assert.NotNull(response.JsonBody);
        Assert.Equal(new IntakeAccepted(Id, RelayHarness.Reference), BugReportJson.DeserializeIntakeAccepted(Encoding.UTF8.GetBytes(response.JsonBody)));
    }

    static async Task AssertQuotaUntouched(RelayHarness harness)
    {
        foreach (QuotaWindow window in QuotaWindows.For("discord:42", harness.Clock.GetUtcNow(), harness.Options))
            Assert.Null(await harness.Quotas.ReadAsync(window.Key, Ct));
    }

    static async Task<JsonDocument> ViewerData(RelayHarness harness)
    {
        string folder = (await harness.Records.LoadAcceptedAsync(Id, Ct))!.Folder;
        ReadOnlyMemory<byte>? bytes = await harness.Media.GetAsync(ReportArea.Public, folder + "/viewer.html", (int)BugReportLimits.MaxBundleBytes, Ct);
        string html = Encoding.UTF8.GetString(bytes!.Value.Span);
        const string opening = "<script type=\"application/json\" id=\"report-data\">";
        int start = html.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += opening.Length;
        return JsonDocument.Parse(html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)]);
    }
}
