using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportRevokeTests
{
    const string Id = RelayHarness.Id;
    const string Reference = RelayHarness.Reference;
    const string OtherId = "0193f3a4b5c67d8e9f0a1b2c3d4e5f60";
    static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Revoke_ByReference_DeletesPublicFolderRedactsIssueAndSchedulesPurge()
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        int publicCount = (await harness.Media.ListAsync(ReportArea.Public, record.Folder + "/", Ct)).Count;
        DateTimeOffset now = harness.Clock.GetUtcNow();
        string unrelated = record.Folder + "-other/keep.json";
        await harness.Media.PutAsync(ReportArea.Public, unrelated, "{}"u8.ToArray(), ReportLayout.HeadersFor(unrelated), false, Ct);
        harness.GitHub.OnUpdate = async _ =>
        {
            Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, ReportLayout.Revoked(Id), Ct));
            Assert.Empty(await harness.Media.ListAsync(ReportArea.Public, record.Folder + "/", Ct));
            Assert.Equal(0, harness.Work.Count);
            return new GitHubIssueUpdateResult(GitHubIssueUpdateOutcome.Updated, new Uri("https://github.com/example/reports/issues/7"));
        };

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(" r-ymwqvo ", Ct);

        Assert.Equal(ReportRevocationStatus.Revoked, result.Status);
        Assert.Equal(Id, result.ReportId);
        Assert.Equal(Reference, result.Reference);
        Assert.Empty(result.CandidateIds);
        Assert.True(publicCount > 0);
        Assert.Equal(publicCount, result.DeletedPublicBlobs);
        Assert.Empty(await harness.Media.ListAsync(ReportArea.Public, record.Folder + "/", Ct));
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Public, unrelated, Ct));
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, ReportLayout.Revoked(Id), Ct));
        var update = Assert.Single(harness.GitHub.Updates);
        Assert.Equal(7, update.IssueNumber);
        Assert.Equal("Revoked bug report R-YMWQV0", update.Update.Title);
        Assert.Equal("This report was revoked at the reporter's request. Its files and private record are being deleted.", update.Update.Body);
        Assert.Equal(ReportIssueRevocation.Redacted, result.Issue);
        Assert.Equal(7, result.IssueNumber);
        Assert.Equal(new Uri("https://github.com/example/reports/issues/7"), result.IssueUrl);
        Assert.Equal(now.AddMinutes(12), result.PurgeAt);
        Assert.Equal(1, harness.Work.Count);
        Assert.False(harness.Work.TryDequeue(out _));
        harness.Clock.Advance(TimeSpan.FromMinutes(12) - TimeSpan.FromTicks(1));
        Assert.False(harness.Work.TryDequeue(out _));
        harness.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? item));
        Assert.Equal(new ReportWorkItem(ReportWorkKind.PurgeRevoked, Id), item);
    }

    [Theory]
    [InlineData(Id)]
    [InlineData("0192F3A4B5C67D8E9F0A1B2C3D4E5F60")]
    public async Task Revoke_ById_AndUppercaseId_Resolve(string input)
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(input, Ct);

        Assert.Equal(ReportRevocationStatus.Revoked, result.Status);
        Assert.Equal(Id, result.ReportId);
        Assert.Equal(Reference, result.Reference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoke_BeforeIssueCreation_AnswersNotCreated(bool removeRecord)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        if (removeRecord) await harness.Media.DeleteAsync(ReportArea.Pending, ReportLayout.ReportRecord(Id), Ct);

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(Reference, Ct);

        Assert.Equal(ReportRevocationStatus.Revoked, result.Status);
        Assert.Equal(ReportIssueRevocation.NotCreated, result.Issue);
        Assert.Null(result.IssueNumber);
        Assert.Null(result.IssueUrl);
        Assert.True(result.DeletedPublicBlobs > 0);
        Assert.Empty(await harness.Media.ListAsync(ReportArea.Public, record.Folder + "/", Ct));
        Assert.Empty(harness.GitHub.Updates);
    }

    [Theory]
    [InlineData("R-000000")]
    [InlineData(OtherId)]
    public async Task Revoke_Unknown_AnswersNotFound(string input)
    {
        var harness = new RelayHarness();

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(input, Ct);

        Assert.Equal(ReportRevocationStatus.NotFound, result.Status);
        Assert.Empty(harness.Media.Paths(ReportArea.Pending));
        Assert.Equal(0, harness.Work.Count);
        Assert.Empty(harness.GitHub.Updates);
    }

    [Fact]
    public async Task Revoke_SharedCode_AnswersAmbiguousWithBothIds()
    {
        var harness = new RelayHarness();
        await SeedAmbiguityAsync(harness);
        string[] pending = harness.Media.Paths(ReportArea.Pending).ToArray();
        string[] published = harness.Media.Paths(ReportArea.Public).ToArray();

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(Reference, Ct);

        Assert.Equal(ReportRevocationStatus.Ambiguous, result.Status);
        Assert.Equal(new[] { Id, OtherId }, result.CandidateIds.Order(StringComparer.Ordinal));
        Assert.Equal(pending, harness.Media.Paths(ReportArea.Pending));
        Assert.Equal(published, harness.Media.Paths(ReportArea.Public));
        Assert.Equal(0, harness.Work.Count);
        Assert.Empty(harness.GitHub.Updates);
    }

    [Fact]
    public async Task Revoke_StaleReferenceCandidate_IsDiscarded()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await SeedReferenceAsync(harness, OtherId);

        Assert.Equal(ReportRevocationStatus.Revoked, (await harness.Relay.RevokeReportAsync(Reference, Ct)).Status);
        Assert.False(await harness.Media.ExistsAsync(ReportArea.Pending, ReportLayout.Revoked(OtherId), Ct));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("R-??????")]
    public async Task Revoke_Garbage_AnswersInvalid(string input)
    {
        var harness = new RelayHarness();

        Assert.Equal(ReportRevocationStatus.Invalid, (await harness.Relay.RevokeReportAsync(input, Ct)).Status);
        Assert.Empty(harness.Media.Paths(ReportArea.Pending));
        Assert.Equal(0, harness.Work.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoke_GitHubFails_StillRevokedWithFailed(bool networkFailure)
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        harness.GitHub.OnUpdate = _ => Task.FromException<GitHubIssueUpdateResult>(networkFailure
            ? new HttpRequestException("offline") : new GitHubApiException(500, false, null, "failed"));

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(Id, Ct);

        Assert.Equal(ReportRevocationStatus.Revoked, result.Status);
        Assert.Equal(ReportIssueRevocation.Failed, result.Issue);
        Assert.Equal(7, result.IssueNumber);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, ReportLayout.Revoked(Id), Ct));
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
        Assert.Equal(1, harness.Work.Count);
    }

    [Theory]
    [InlineData(GitHubIssueUpdateOutcome.Gone, ReportIssueRevocation.Gone, null)]
    [InlineData(GitHubIssueUpdateOutcome.Moved, ReportIssueRevocation.Moved, "https://github.com/example/moved/issues/9")]
    public async Task Revoke_GitHubOutcome_IsReturned(GitHubIssueUpdateOutcome outcome, ReportIssueRevocation expected, string? url)
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        harness.GitHub.OnUpdate = _ => Task.FromResult(new GitHubIssueUpdateResult(outcome, url is null ? null : new Uri(url)));

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(Id, Ct);

        Assert.Equal(expected, result.Issue);
        Assert.Equal(url, result.IssueUrl?.AbsoluteUri);
        Assert.Equal(1, harness.Work.Count);
    }

    [Theory]
    [InlineData(null, "17", 17)]
    [InlineData(0, "17", 17)]
    [InlineData(9, "17", 9)]
    [InlineData(null, "0", null)]
    [InlineData(null, "-3", null)]
    [InlineData(null, "invalid", null)]
    public async Task Revoke_IssueNumber_UsesRecordThenPositiveClaimResult(int? stored, string claimed, int? expected)
    {
        var harness = new RelayHarness();
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        await harness.Records.SaveReportAsync(record with { IssueNumber = stored }, Ct);
        var claims = new WorkClaim(harness.Media, harness.Clock);
        int epoch = (await claims.TryAcquireAsync(ReportWorkKind.CreateIssue, Id, Ct))!.Value;
        await claims.CompleteAsync(ReportWorkKind.CreateIssue, Id, epoch, claimed);

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(Id, Ct);

        Assert.Equal(expected, result.IssueNumber);
        Assert.Equal(expected is null ? ReportIssueRevocation.NotCreated : ReportIssueRevocation.Redacted, result.Issue);
        if (expected is not null) Assert.Equal(expected, Assert.Single(harness.GitHub.Updates).IssueNumber);
        else Assert.Empty(harness.GitHub.Updates);
    }

    [Fact]
    public async Task Revoke_Cancellation_Propagates()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        harness.GitHub.OnUpdate = _ => throw new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.Relay.RevokeReportAsync(Id, Ct));

        Assert.Equal(0, harness.Work.Count);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, ReportLayout.Revoked(Id), Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoke_Repeat_BeforePurge_IsSafe(bool acceptedOnly)
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        if (acceptedOnly) await harness.Media.DeleteAsync(ReportArea.Pending, ReportLayout.ReportRecord(Id), Ct);
        await harness.Relay.RevokeReportAsync(Id, Ct);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(Reference, Ct);

        Assert.Equal(ReportRevocationStatus.Revoked, result.Status);
        Assert.Equal(0, result.DeletedPublicBlobs);
        Assert.Equal(harness.Clock.GetUtcNow().AddMinutes(12), result.PurgeAt);
        Assert.Equal(2, harness.Work.Count);
    }

    [Theory]
    [InlineData(Id)]
    [InlineData(Reference)]
    public async Task Revoke_TombstoneOnly_AnswersAlreadyRevokedWithoutChanges(string input)
    {
        var harness = new RelayHarness();
        await harness.Relay.RevokeAsync(Id, Ct);
        await SeedReferenceAsync(harness, Id);
        byte[] tombstone = (await harness.Media.GetAsync(ReportArea.Pending, ReportLayout.Revoked(Id), 4096, Ct))!.Value.ToArray();
        string[] pending = harness.Media.Paths(ReportArea.Pending).ToArray();

        ReportRevocationResult result = await harness.Relay.RevokeReportAsync(input, Ct);

        Assert.Equal(ReportRevocationStatus.AlreadyRevoked, result.Status);
        Assert.Equal(Id, result.ReportId);
        Assert.Equal(Reference, result.Reference);
        Assert.Null(result.PurgeAt);
        Assert.Null(result.Issue);
        Assert.Equal(0, result.DeletedPublicBlobs);
        Assert.Equal(pending, harness.Media.Paths(ReportArea.Pending));
        Assert.Equal(tombstone, (await harness.Media.GetAsync(ReportArea.Pending, ReportLayout.Revoked(Id), 4096, Ct))!.Value.ToArray());
        Assert.Equal(0, harness.Work.Count);
        Assert.Empty(harness.GitHub.Updates);
    }

    [Theory]
    [InlineData("revoked", 200)]
    [InlineData("alreadyRevoked", 200)]
    [InlineData("notFound", 404)]
    [InlineData("ambiguous", 409)]
    [InlineData("invalid", 400)]
    public async Task RevokeRequest_MapsStatusesAndRejectsBadBodies(string status, int httpStatus)
    {
        var harness = new RelayHarness();
        string input = status == "invalid" ? "../x" : Reference;
        if (status == "revoked") await WorkerFixtures.AcceptAsync(harness);
        if (status == "alreadyRevoked")
        {
            await harness.Relay.RevokeAsync(Id, Ct);
            await SeedReferenceAsync(harness, Id);
        }
        if (status == "ambiguous") await SeedAmbiguityAsync(harness);

        RelayResponse response = await harness.Relay.RevokeRequestAsync(Request(JsonSerializer.Serialize(new { report = input })), Ct);

        Assert.Equal(httpStatus, response.StatusCode);
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
        using JsonDocument json = JsonDocument.Parse(response.JsonBody!);
        Assert.Equal(status, json.RootElement.GetProperty("status").GetString());
        if (status == "revoked") Assert.Equal("notCreated", json.RootElement.GetProperty("issue").GetString());
        if (status == "ambiguous") Assert.Equal(2, json.RootElement.GetProperty("candidateIds").GetArrayLength());
    }

    [Theory]
    [InlineData("{", 400, "bad_request")]
    [InlineData("{\"report\":1}", 400, "bad_request")]
    [InlineData("{\"report\":\"x\",\"y\":1}", 400, "bad_request")]
    [InlineData("[]", 400, "bad_request")]
    [InlineData("{}", 400, "bad_request")]
    [InlineData("null", 400, "bad_request")]
    [InlineData("{\"report\":null}", 400, "bad_request")]
    [InlineData("{\"report\":\"x\",\"report\":\"y\"}", 400, "bad_request")]
    [InlineData("oversized", 413, "too_large")]
    public async Task RevokeRequest_MapsStatusesAndRejectsBadBodies_BadBody(string body, int status, string error)
    {
        var harness = new RelayHarness();
        if (body == "oversized") body = new string(' ', 1025);

        WorkerFixtures.AssertError(await harness.Relay.RevokeRequestAsync(Request(body), Ct), status, error);
        Assert.Empty(harness.Media.Paths(ReportArea.Pending));
        Assert.Equal(0, harness.Work.Count);
    }

    [Fact]
    public async Task RevokeRequest_Exactly1024Bytes_IsAccepted()
    {
        var harness = new RelayHarness();
        string body = "{\"report\":\"R-000000\"}".PadRight(1024);

        Assert.Equal(404, (await harness.Relay.RevokeRequestAsync(Request(body), Ct)).StatusCode);
    }

    static RelayRequest Request(string body) => new(new System.Collections.Generic.Dictionary<string, string>(),
        new MemoryStream(Encoding.UTF8.GetBytes(body)), null);

    static Task<bool> SeedReferenceAsync(RelayHarness harness, string id)
    {
        string path = ReportLayout.ReferenceIndex(Reference, id);
        return harness.Media.PutAsync(ReportArea.Pending, path, "{}"u8.ToArray(), ReportLayout.HeadersFor(path), false, Ct);
    }

    static async Task SeedAmbiguityAsync(RelayHarness harness)
    {
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        Assert.True(ReportIds.IsValid(OtherId));
        Assert.Equal(Reference, ReportIds.ReferenceCode(OtherId));
        await harness.Records.CreateReportAsync(record with { ReportId = OtherId }, Ct);
        await SeedReferenceAsync(harness, OtherId);
    }
}
