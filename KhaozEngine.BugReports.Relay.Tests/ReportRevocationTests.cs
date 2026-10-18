using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Diagnostics;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportRevocationTests
{
    const string Id = RelayHarness.Id;
    const string Tombstone = "revoked/" + Id + ".json";
    static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue)]
    [InlineData(ReportWorkKind.AppendServerSnapshot)]
    public async Task Revoked_work_does_not_write_public_media_or_call_github(ReportWorkKind kind)
    {
        PublicFolderStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new PublicFolderStore(inner));
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        Assert.Contains(record.Manifest.Entries, entry => entry.Name.StartsWith("sections/", StringComparison.Ordinal));
        if (kind == ReportWorkKind.AppendServerSnapshot) await WorkerFixtures.ProcessAsync(harness);
        await WorkerFixtures.SendAsync(harness);
        await harness.Relay.RevokeAsync(Id, Ct);
        string[] publicPaths = store.Public.Paths(ReportArea.Public).ToArray();
        int writes = store.PublicWrites;

        await WorkerFixtures.ProcessAsync(harness, kind);

        Assert.Equal(publicPaths, store.Public.Paths(ReportArea.Public));
        Assert.Equal(writes, store.PublicWrites);
        Assert.Equal(kind == ReportWorkKind.CreateIssue ? 0 : 1, harness.GitHub.Issues.Count);
        Assert.Empty(harness.GitHub.Comments);
        var log = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Info, log.Level);
        Assert.Contains(Id, log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoked_snapshot_and_bundle_return_202_without_work_or_pending_snapshot()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.AcceptAsync(harness);
        await harness.Relay.RevokeAsync(Id, Ct);
        string[] publicPaths = harness.Media.Paths(ReportArea.Public).ToArray();

        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);
        Assert.Equal(202, (await harness.IntakeAsync()).StatusCode);

        Assert.Equal(0, harness.Work.Count);
        Assert.Null(await harness.Records.LoadPendingSnapshotAsync(Id, Ct));
        Assert.Equal(publicPaths, harness.Media.Paths(ReportArea.Public));
    }

    [Fact]
    public async Task Revoke_is_create_only_and_retains_the_first_revocation_time()
    {
        var harness = new RelayHarness();
        DateTimeOffset revokedAt = harness.Clock.GetUtcNow();
        await harness.Relay.RevokeAsync(Id, Ct);
        byte[] first = (await harness.Media.GetAsync(ReportArea.Pending, Tombstone, 4096, Ct))!.Value.ToArray();
        using JsonDocument json = JsonDocument.Parse(first);
        Assert.Equal(revokedAt, json.RootElement.GetProperty("revokedAt").GetDateTimeOffset());
        harness.Clock.Advance(TimeSpan.FromHours(1));

        await harness.Relay.RevokeAsync(Id, Ct);

        Assert.Equal(first, (await harness.Media.GetAsync(ReportArea.Pending, Tombstone, 4096, Ct))!.Value.ToArray());
        Assert.Single(harness.Media.Paths(ReportArea.Pending));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../bad")]
    [InlineData("0192F3A4B5C67D8E9F0A1B2C3D4E5F60")]
    public async Task Revoke_rejects_invalid_ids(string reportId)
    {
        var harness = new RelayHarness();
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Relay.RevokeAsync(reportId, Ct));
        Assert.Empty(harness.Media.Paths(ReportArea.Pending));
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue)]
    [InlineData(ReportWorkKind.AppendServerSnapshot)]
    public async Task Deleted_public_folder_is_tombstoned_without_republishing(ReportWorkKind kind)
    {
        PublicFolderStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new PublicFolderStore(inner));
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        Assert.NotEmpty(record.Manifest.Entries);
        if (kind == ReportWorkKind.AppendServerSnapshot) await WorkerFixtures.ProcessAsync(harness);
        await WorkerFixtures.SendAsync(harness);
        store.DeletePublicFolder();

        await WorkerFixtures.ProcessAsync(harness, kind);

        Assert.Empty(store.Public.Paths(ReportArea.Public));
        Assert.Equal(0, store.PublicWrites);
        Assert.Equal(kind == ReportWorkKind.CreateIssue ? 0 : 1, harness.GitHub.Issues.Count);
        Assert.Empty(harness.GitHub.Comments);
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, Tombstone, Ct));
        var log = Assert.Single(harness.Logger.Entries);
        Assert.Equal(LogLevel.Info, log.Level);
        Assert.Contains(Id, log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_after_folder_deletion_returns_202_without_private_write_or_enqueue()
    {
        PublicFolderStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new PublicFolderStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        store.DeletePublicFolder();

        Assert.Equal(202, (await WorkerFixtures.SendAsync(harness)).StatusCode);

        Assert.Equal(0, harness.Work.Count);
        Assert.Null(await harness.Records.LoadPendingSnapshotAsync(Id, Ct));
        Assert.True(await harness.Media.ExistsAsync(ReportArea.Pending, Tombstone, Ct));
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue)]
    [InlineData(ReportWorkKind.AppendServerSnapshot)]
    public async Task Revocation_during_snapshot_read_stops_public_writes_and_github(ReportWorkKind kind)
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        ReportRecord record = await WorkerFixtures.AcceptAsync(harness);
        if (kind == ReportWorkKind.AppendServerSnapshot) await WorkerFixtures.ProcessAsync(harness);
        await WorkerFixtures.SendAsync(harness);
        byte[] viewer = (await harness.Media.GetAsync(ReportArea.Public, record.Folder + "/viewer.html", 1024 * 1024, Ct))!.Value.ToArray();
        store.BeforeRead = path => path.StartsWith("snapshots/", StringComparison.Ordinal)
            ? harness.Relay.RevokeAsync(Id, Ct) : Task.CompletedTask;

        await WorkerFixtures.ProcessAsync(harness, kind);

        Assert.False(await harness.Media.ExistsAsync(ReportArea.Public, record.Folder + "/server.json", Ct));
        Assert.Equal(viewer, (await harness.Media.GetAsync(ReportArea.Public, record.Folder + "/viewer.html", 1024 * 1024, Ct))!.Value.ToArray());
        Assert.Equal(kind == ReportWorkKind.CreateIssue ? 0 : 1, harness.GitHub.Issues.Count);
        Assert.Empty(harness.GitHub.Comments);
        Assert.Single(harness.Logger.Entries);
    }

    [Fact]
    public async Task Revocation_during_issue_input_load_stops_github()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        store.BeforeRead = path => path.EndsWith("/sections/build.json", StringComparison.Ordinal)
            ? harness.Relay.RevokeAsync(Id, Ct) : Task.CompletedTask;

        await WorkerFixtures.ProcessAsync(harness);

        Assert.Empty(harness.GitHub.Issues);
        Assert.Single(harness.Logger.Entries);
    }

    [Fact]
    public async Task Revocation_during_progress_merge_logs_once_and_returns()
    {
        WorkerMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new WorkerMediaStore(inner));
        await WorkerFixtures.AcceptAsync(harness);
        await WorkerFixtures.SendAsync(harness);
        int reads = 0;
        store.BeforeRead = path => path.StartsWith("records/", StringComparison.Ordinal)
            && Interlocked.Increment(ref reads) == 3 ? harness.Relay.RevokeAsync(Id, Ct) : Task.CompletedTask;

        await WorkerFixtures.ProcessAsync(harness);

        Assert.Empty(harness.GitHub.Issues);
        Assert.Equal(LogLevel.Info, Assert.Single(harness.Logger.Entries).Level);
    }

    // Deletion belongs to the host. This store replaces only its public area, retaining all private state.
    sealed class PublicFolderStore(InMemoryReportMediaStore pending) : IReportMediaStore
    {
        public InMemoryReportMediaStore Public { get; private set; } = new();
        public int PublicWrites { get; private set; }

        public void DeletePublicFolder()
        {
            Public = new InMemoryReportMediaStore();
            PublicWrites = 0;
        }

        public Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content, ReportBlobHeaders headers,
            bool overwrite, CancellationToken cancellationToken)
        {
            if (area == ReportArea.Public) PublicWrites++;
            return Store(area).PutAsync(area, path, content, headers, overwrite, cancellationToken);
        }

        public Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes, CancellationToken cancellationToken)
            => Store(area).GetAsync(area, path, maxBytes, cancellationToken);

        public Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken)
            => Store(area).ExistsAsync(area, path, cancellationToken);

        InMemoryReportMediaStore Store(ReportArea area) => area == ReportArea.Public ? Public : pending;
    }
}
