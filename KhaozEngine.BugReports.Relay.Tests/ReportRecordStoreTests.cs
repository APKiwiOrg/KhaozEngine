using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class ReportRecordStoreTests
{
    const string Id = "0192f3a4b5c67d8e9f0a1b2c3d4e5f60";
    const string Folder = "grimhollow/2026-10/" + Id + "-0123456789abcdef0123456789abcdef";
    static readonly DateTimeOffset ReceivedAt = new(2026, 10, 17, 1, 2, 3, TimeSpan.Zero);
    static readonly ReportBlobHeaders Json = new("application/json; charset=utf-8", "no-cache");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RecordsPath_RoundTripsEveryField(int snapshotValue)
    {
        var media = new InMemoryReportMediaStore();
        var store = new ReportRecordStore(media);
        ReportRecord record = Record() with { IssueNumber = 42, ServerSnapshot = (ServerSnapshotState)snapshotValue };

        await store.SaveReportAsync(record, CancellationToken.None);
        ReportRecord? loaded = await store.LoadReportAsync(Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(record.FormatVersion, loaded.FormatVersion);
        Assert.Equal(record.ReportId, loaded.ReportId);
        Assert.Equal(record.Reference, loaded.Reference);
        Assert.Equal(record.Folder, loaded.Folder);
        Assert.Equal(record.ReceivedAt, loaded.ReceivedAt);
        Assert.Equal(record.Reporter, loaded.Reporter);
        Assert.Equal(record.Manifest.FormatVersion, loaded.Manifest.FormatVersion);
        Assert.Equal(record.Manifest.ReportId, loaded.Manifest.ReportId);
        Assert.Equal(record.Manifest.GameId, loaded.Manifest.GameId);
        Assert.Equal(record.Manifest.Kind, loaded.Manifest.Kind);
        Assert.Equal(record.Manifest.Category, loaded.Manifest.Category);
        Assert.Equal(record.Manifest.Description, loaded.Manifest.Description);
        Assert.Equal(record.Manifest.CapturedAt, loaded.Manifest.CapturedAt);
        Assert.Equal(record.Manifest.Entries, loaded.Manifest.Entries);
        Assert.Equal(record.IssueNumber, loaded.IssueNumber);
        Assert.Equal(record.ServerSnapshot, loaded.ServerSnapshot);
    }

    [Theory]
    [InlineData(0, "none")]
    [InlineData(1, "inBody")]
    [InlineData(2, "commented")]
    public async Task Report_JsonIsCamelCaseWithStringEnums(int snapshotValue, string name)
    {
        var media = new InMemoryReportMediaStore();
        var store = new ReportRecordStore(media);
        ReportRecord record = Record() with
        {
            Reporter = new ReporterIdentity("discord:42", "Ash", null),
            ServerSnapshot = (ServerSnapshotState)snapshotValue,
        };

        await store.SaveReportAsync(record, CancellationToken.None);
        ReadOnlyMemory<byte>? bytes = await media.GetAsync(ReportArea.Pending, "records/" + Id + ".json", 2 * 1024 * 1024, CancellationToken.None);

        Assert.NotNull(bytes);
        string json = Encoding.UTF8.GetString(bytes.Value.Span);
        Assert.Contains("\"formatVersion\":1", json);
        Assert.Contains("\"reportId\":\"" + Id + "\"", json);
        Assert.Contains("\"serverSnapshot\":\"" + name + "\"", json);
        Assert.Contains("\"issueNumber\":null", json);
        Assert.Contains("\"handle\":null", json);
        Assert.Contains("\"kind\":\"crash\"", json);
    }

    [Fact]
    public async Task SaveReport_WritesToPendingRecords_NeverPublic()
    {
        var media = new InMemoryReportMediaStore();
        var store = new ReportRecordStore(media);

        await store.SaveReportAsync(Record(), CancellationToken.None);

        Assert.Equal(new[] { "records/" + Id + ".json" }, media.Paths(ReportArea.Pending));
        Assert.Empty(media.Paths(ReportArea.Public));
        Assert.Equal(Json, media.HeadersOf(ReportArea.Pending, "records/" + Id + ".json"));
    }

    [Fact]
    public async Task SaveReport_OverwritesIssueAndSnapshotState()
    {
        var media = new InMemoryReportMediaStore();
        var store = new ReportRecordStore(media);
        ReportRecord record = Record();
        await store.SaveReportAsync(record, CancellationToken.None);

        await store.SaveReportAsync(record with { IssueNumber = 7, ServerSnapshot = ServerSnapshotState.Commented }, CancellationToken.None);
        ReportRecord? loaded = await store.LoadReportAsync(Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(7, loaded.IssueNumber);
        Assert.Equal(ServerSnapshotState.Commented, loaded.ServerSnapshot);
        Assert.Single(media.Paths(ReportArea.Pending));
    }

    [Fact]
    public async Task Accepted_CreateTwice_SecondReturnsFalseAndKeepsFirst()
    {
        var media = new InMemoryReportMediaStore();
        var store = new ReportRecordStore(media);
        var first = new AcceptedReport(Folder, "discord:42", ReceivedAt);
        var second = new AcceptedReport("g/2026-11/other", "discord:99", ReceivedAt.AddDays(1));

        Assert.True(await store.TryCreateAcceptedAsync(Id, first, CancellationToken.None));
        Assert.False(await store.TryCreateAcceptedAsync(Id, second, CancellationToken.None));

        Assert.Equal(first, await store.LoadAcceptedAsync(Id, CancellationToken.None));
        Assert.Equal(new[] { "accepted/" + Id + ".json" }, media.Paths(ReportArea.Pending));
        Assert.Empty(media.Paths(ReportArea.Public));
        Assert.Equal(Json, media.HeadersOf(ReportArea.Pending, "accepted/" + Id + ".json"));
        ReadOnlyMemory<byte>? bytes = await media.GetAsync(ReportArea.Pending, "accepted/" + Id + ".json", 4096, CancellationToken.None);
        Assert.NotNull(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes.Value);
        Assert.Equal(Folder, document.RootElement.GetProperty("folder").GetString());
        Assert.Equal("discord:42", document.RootElement.GetProperty("subject").GetString());
        Assert.Equal(ReceivedAt, document.RootElement.GetProperty("receivedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task PendingSnapshot_FirstWriteWins()
    {
        var media = new InMemoryReportMediaStore();
        var store = new ReportRecordStore(media);
        byte[] first = Encoding.UTF8.GetBytes("{\"sections\":{\"state\":{\"value\":1}}}");
        byte[] second = Encoding.UTF8.GetBytes("{\"sections\":{}}");

        Assert.True(await store.PutPendingSnapshotAsync(Id, first, CancellationToken.None));
        Assert.False(await store.PutPendingSnapshotAsync(Id, second, CancellationToken.None));
        ReadOnlyMemory<byte>? loaded = await store.LoadPendingSnapshotAsync(Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(first, loaded.Value.ToArray());
        Assert.Equal(new[] { "snapshots/" + Id + ".json" }, media.Paths(ReportArea.Pending));
        Assert.Empty(media.Paths(ReportArea.Public));
        Assert.Equal(Json, media.HeadersOf(ReportArea.Pending, "snapshots/" + Id + ".json"));
    }

    [Fact]
    public async Task Load_Absent_ReturnsNull()
    {
        var store = new ReportRecordStore(new InMemoryReportMediaStore());

        Assert.Null(await store.LoadReportAsync(Id, CancellationToken.None));
        Assert.Null(await store.LoadAcceptedAsync(Id, CancellationToken.None));
        Assert.Null(await store.LoadPendingSnapshotAsync(Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("records", 2 * 1024 * 1024)]
    [InlineData("accepted", 4096)]
    [InlineData("snapshots", 1024 * 1024)]
    public async Task Load_OverReadCap_ThrowsInvalidDataException(string prefix, int cap)
    {
        var media = new InMemoryReportMediaStore();
        var store = new ReportRecordStore(media);
        await media.PutAsync(ReportArea.Pending, prefix + "/" + Id + ".json", new byte[cap + 1], Json, false, CancellationToken.None);

        switch (prefix)
        {
            case "records":
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadReportAsync(Id, CancellationToken.None));
                break;
            case "accepted":
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAcceptedAsync(Id, CancellationToken.None));
                break;
            case "snapshots":
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadPendingSnapshotAsync(Id, CancellationToken.None));
                break;
        }
    }

    [Theory]
    [InlineData("records", 2 * 1024 * 1024)]
    [InlineData("accepted", 4096)]
    [InlineData("snapshots", 1024 * 1024)]
    public async Task Load_AtReadCap_Succeeds(string prefix, int cap)
    {
        var media = new InMemoryReportMediaStore();
        var store = new ReportRecordStore(media);
        byte[] payload = prefix switch
        {
            "records" => JsonSerializer.SerializeToUtf8Bytes(Record(), RelayJson.Options),
            "accepted" => JsonSerializer.SerializeToUtf8Bytes(new AcceptedReport(Folder, "discord:42", ReceivedAt), RelayJson.Options),
            _ => Encoding.UTF8.GetBytes("{}"),
        };
        byte[] padded = new byte[cap];
        payload.CopyTo(padded, 0);
        padded.AsSpan(payload.Length).Fill((byte)' ');
        await media.PutAsync(ReportArea.Pending, prefix + "/" + Id + ".json", padded, Json, false, CancellationToken.None);

        switch (prefix)
        {
            case "records":
                Assert.NotNull(await store.LoadReportAsync(Id, CancellationToken.None));
                break;
            case "accepted":
                Assert.NotNull(await store.LoadAcceptedAsync(Id, CancellationToken.None));
                break;
            case "snapshots":
                ReadOnlyMemory<byte>? loaded = await store.LoadPendingSnapshotAsync(Id, CancellationToken.None);
                Assert.NotNull(loaded);
                Assert.Equal(padded, loaded.Value.ToArray());
                break;
        }
    }

    [Theory]
    [InlineData("records", "null")]
    [InlineData("records", "{\"issueNumber\":1,\"issueNumber\":2}")]
    [InlineData("records", "{\"serverSnapshot\":\"None\"}")]
    [InlineData("records", "{\"serverSnapshot\":0}")]
    [InlineData("accepted", "null")]
    [InlineData("accepted", "{\"subject\":\"a\",\"subject\":\"b\"}")]
    public async Task Load_InvalidPrivateJson_ThrowsInsteadOfReturningAbsent(string prefix, string json)
    {
        var media = new InMemoryReportMediaStore();
        var store = new ReportRecordStore(media);
        await media.PutAsync(ReportArea.Pending, prefix + "/" + Id + ".json", Encoding.UTF8.GetBytes(json), Json, false, CancellationToken.None);

        if (prefix == "records")
            await Assert.ThrowsAsync<JsonException>(() => store.LoadReportAsync(Id, CancellationToken.None));
        else
            await Assert.ThrowsAsync<JsonException>(() => store.LoadAcceptedAsync(Id, CancellationToken.None));
    }

    static ReportRecord Record()
    {
        var manifest = new BugReportManifest(1, Id, "grimhollow", BugReportKind.Crash, "combat", "A crash near the gate",
            ReceivedAt.AddSeconds(-5), new[] { new BundleEntry("sections/build.json", 17, new string('a', 64)) });
        return new ReportRecord(1, Id, "R-ABC123", Folder, ReceivedAt,
            new ReporterIdentity("discord:42", "Ash", "ash-player"), manifest, null, ServerSnapshotState.None);
    }
}
