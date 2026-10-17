using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Diagnostics;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ServerSnapshotIntakeTests
{
    const string Id = RelayHarness.Id;
    static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Snapshot_MissingServiceKey_403_BodyNeverRead()
    {
        var harness = new RelayHarness();
        using var body = new ThrowingStream();
        WorkerFixtures.AssertError(await harness.Relay.ServerSnapshotIntakeAsync(Id,
            WorkerFixtures.Request(serviceKey: null, body: body), Ct), 403, "forbidden");
        Assert.Equal(0, body.ReadCalls);
        Assert.Empty(harness.Media.Paths(ReportArea.Pending));
    }

    [Fact]
    public async Task Snapshot_WrongServiceKey_403_BodyNeverRead()
    {
        var harness = new RelayHarness();
        using var body = new ThrowingStream();
        WorkerFixtures.AssertError(await harness.Relay.ServerSnapshotIntakeAsync("invalid",
            WorkerFixtures.Request(serviceKey: new string('x', 32), body: body), Ct), 403, "forbidden");
        Assert.Equal(0, body.ReadCalls);
    }

    [Fact]
    public async Task Snapshot_ServiceKeyHeaderAnyCase_Accepted()
    {
        var harness = new RelayHarness();
        Assert.Equal(202, (await harness.Relay.ServerSnapshotIntakeAsync(Id,
            WorkerFixtures.Request(header: "x-RePoRt-SeRvIcE-kEy"), Ct)).StatusCode);
        Assert.Null(harness.Authenticator.LastToken);
        foreach (QuotaWindow window in QuotaWindows.For("discord:42", harness.Clock.GetUtcNow(), harness.Options))
            Assert.Null(await harness.Quotas.ReadAsync(window.Key, Ct));
    }

    [Fact]
    public async Task Snapshot_InvalidReportId_400()
    {
        using var body = new ThrowingStream();
        WorkerFixtures.AssertError(await new RelayHarness().Relay.ServerSnapshotIntakeAsync("invalid",
            WorkerFixtures.Request(body: body), Ct), 400, "bad_request");
        Assert.Equal(0, body.ReadCalls);
    }

    [Fact]
    public async Task Snapshot_OverOneMiB_413()
    {
        var harness = new RelayHarness();
        using var body = new ThrowingStream(new byte[BugReportLimits.MaxServerSnapshotBytes + 100]);
        WorkerFixtures.AssertError(await harness.Relay.ServerSnapshotIntakeAsync(Id,
            WorkerFixtures.Request(body: body, length: 1), Ct), 413, "too_large");
        Assert.Equal(BugReportLimits.MaxServerSnapshotBytes + 1, body.BytesRead);
        Assert.Empty(harness.Media.Paths(ReportArea.Pending));
    }

    [Fact]
    public async Task Snapshot_DeclaredLengthOverCap_413_BodyNeverRead()
    {
        using var body = new ThrowingStream();
        WorkerFixtures.AssertError(await new RelayHarness().Relay.ServerSnapshotIntakeAsync(Id,
            WorkerFixtures.Request(body: body, length: BugReportLimits.MaxServerSnapshotBytes + 1), Ct), 413, "too_large");
        Assert.Equal(0, body.ReadCalls);
    }

    [Fact]
    public async Task Snapshot_ExactlyOneMiB_Accepted()
    {
        byte[] json = BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot());
        byte[] body = new byte[BugReportLimits.MaxServerSnapshotBytes];
        Array.Fill(body, (byte)' ');
        json.CopyTo(body, 0);
        var harness = new RelayHarness();
        Assert.Equal(202, (await harness.Relay.ServerSnapshotIntakeAsync(Id,
            WorkerFixtures.Request(body, length: body.Length), Ct)).StatusCode);
        Assert.Equal(body, (await harness.Records.LoadPendingSnapshotAsync(Id, Ct))!.Value.ToArray());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"FormatVersion\":1}")]
    public async Task Snapshot_MalformedOrNonCanonicalJson_400(string json)
        => WorkerFixtures.AssertError(await new RelayHarness().Relay.ServerSnapshotIntakeAsync(Id,
            WorkerFixtures.Request(Encoding.UTF8.GetBytes(json)), Ct), 400, "bad_request");

    [Fact]
    public async Task Snapshot_DuplicateWireProperty_400()
    {
        string json = Encoding.UTF8.GetString(BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot()));
        json = json.Insert(1, "\"formatVersion\":1,");
        WorkerFixtures.AssertError(await new RelayHarness().Relay.ServerSnapshotIntakeAsync(Id,
            WorkerFixtures.Request(Encoding.UTF8.GetBytes(json)), Ct), 400, "bad_request");
    }

    [Fact]
    public async Task Snapshot_RouteAndBodyIdsDiffer_400()
        => WorkerFixtures.AssertError(await WorkerFixtures.SendAsync(new RelayHarness(), WorkerFixtures.Snapshot() with
        {
            ReportId = "0192f3a4b5c67d8e9f0a1b2c3d4e5f61",
        }), 400, "bad_request");

    [Fact]
    public async Task Snapshot_WrongFormatVersion_400()
        => WorkerFixtures.AssertError(await WorkerFixtures.SendAsync(new RelayHarness(),
            WorkerFixtures.Snapshot() with { FormatVersion = 2 }), 400, "bad_request");

    [Fact]
    public async Task Snapshot_EmptySubject_400()
        => WorkerFixtures.AssertError(await WorkerFixtures.SendAsync(new RelayHarness(),
            WorkerFixtures.Snapshot(subject: "")), 400, "bad_request");

    [Fact]
    public async Task Snapshot_BeforeBundle_StoredPending_NothingEnqueued_202()
    {
        var harness = new RelayHarness();
        RelayResponse response = await WorkerFixtures.SendAsync(harness);
        Assert.Equal(202, response.StatusCode);
        Assert.Equal(new IntakeAccepted(Id, RelayHarness.Reference),
            BugReportJson.DeserializeIntakeAccepted(Encoding.UTF8.GetBytes(response.JsonBody!)));
        Assert.Equal(0, harness.Work.Count);
        Assert.Equal(BugReportJson.SerializeServerSnapshot(WorkerFixtures.Snapshot()),
            (await harness.Records.LoadPendingSnapshotAsync(Id, Ct))!.Value.ToArray());
        Assert.Empty(harness.Media.Paths(ReportArea.Public));
    }

    [Fact]
    public async Task Snapshot_AfterBundle_SubjectMatches_StoredAndAppendEnqueued()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);
        Assert.True(harness.Work.TryDequeue(out ReportWorkItem? item));
        Assert.Equal(new ReportWorkItem(ReportWorkKind.AppendServerSnapshot, Id), item);
        Assert.Equal(0, harness.Work.Count);
    }

    [Fact]
    public async Task Snapshot_AfterBundle_SubjectMismatch_NothingStored_202_WarningWithoutSubjects()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness, WorkerFixtures.Snapshot(subject: "DISCORD:42"))).StatusCode);
        Assert.Null(await harness.Records.LoadPendingSnapshotAsync(Id, Ct));
        Assert.Equal(0, harness.Work.Count);
        var warning = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Warn, warning.Level);
        Assert.Contains(Id, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("discord", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_Twice_FirstWriteWins()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.SendAsync(harness);
        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness, WorkerFixtures.Snapshot(tick: 99))).StatusCode);
        Assert.Equal(1, harness.Work.Count);
        ServerSnapshotWire pending = BugReportJson.DeserializeServerSnapshot(
            (await harness.Records.LoadPendingSnapshotAsync(Id, Ct))!.Value.Span);
        Assert.Equal(42, pending.Sections["server"].GetProperty("tick").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_RacingCreateIssue_IsMergedOrAppendedExactlyOnce(bool initiallyAccepted)
    {
        RelayHarness harness = null!;
        harness = new RelayHarness(wrapMedia: inner => new HookedMediaStore(inner, "snapshots/", async () =>
        {
            if (!initiallyAccepted) await WorkerFixtures.AcceptAsync(harness);
            await WorkerFixtures.ProcessAsync(harness);
        }));
        if (initiallyAccepted) await WorkerFixtures.AcceptAsync(harness);

        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);
        await WorkerFixtures.DrainAsync(harness);

        Assert.DoesNotContain("server.json", Assert.Single(harness.GitHub.Issues).Body, StringComparison.Ordinal);
        Assert.Contains("server.json", Assert.Single(harness.GitHub.Comments).Body, StringComparison.Ordinal);
        Assert.Equal(ServerSnapshotState.Commented, (await WorkerFixtures.RecordAsync(harness)).ServerSnapshot);
    }

    [Fact]
    public async Task Snapshot_StoreFault_Propagates()
    {
        var failure = new IOException("Store unavailable");
        var harness = new RelayHarness(wrapMedia: inner => new HookedMediaStore(inner, "snapshots/", () => throw failure));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WorkerFixtures.SendAsync(harness)));
    }
}
