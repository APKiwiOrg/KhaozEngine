using System;
using System.IO;
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

public sealed class ReportFolderReaderTests
{
    const string Id = RelayHarness.Id;
    const string Folder = "grimhollow/2026-10/" + Id + "-00000000000000000000000000000000";
    static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Read_SectionsShareOneTotalByteCap()
    {
        var media = new InMemoryReportMediaStore();
        ReportRecord record = Record("sections/a.json", "sections/b.json");
        await Put(media, "sections/a.json", new byte[BugReportLimits.MaxSectionBytesTotal - 1]);
        await Put(media, "sections/b.json", new byte[2]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new ReportFolderReader(media).ReadAsync(record, Ct));
    }

    [Fact]
    public async Task Read_SectionsExactlyAtTotalCap_Accepted()
    {
        var media = new InMemoryReportMediaStore();
        ReportRecord record = Record("sections/a.json", "sections/b.json");
        await Put(media, "sections/a.json", new byte[BugReportLimits.MaxSectionBytesTotal - 1]);
        await Put(media, "sections/b.json", new byte[1]);

        ReportFolderContents contents = await new ReportFolderReader(media).ReadAsync(record, Ct);

        Assert.Equal(new[] { "a", "b" }, contents.Sections.Select(s => s.Key));
        Assert.Equal(BugReportLimits.MaxSectionBytesTotal, contents.Sections.Sum(s => s.Json.Length));
        Assert.Null(contents.Clip);
        Assert.Empty(contents.LogTail);
    }

    [Fact]
    public async Task Read_ClipIndexOver64KiB_Throws()
    {
        var media = new InMemoryReportMediaStore();
        await Put(media, "clip/index.json", new byte[65537]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new ReportFolderReader(media).ReadAsync(Record("clip/index.json"), Ct));
    }

    [Fact]
    public async Task Read_ClipIndexUsesStrictWireParser()
    {
        var media = new InMemoryReportMediaStore();
        await Put(media, "clip/index.json", "{\"Frames\":[]}"u8.ToArray());

        await Assert.ThrowsAsync<JsonException>(() => new ReportFolderReader(media).ReadAsync(Record("clip/index.json"), Ct));
    }

    [Fact]
    public async Task Read_MissingListedEntry_Throws()
        => await Assert.ThrowsAsync<InvalidDataException>(() => new ReportFolderReader(new InMemoryReportMediaStore())
            .ReadAsync(Record("sections/build.json"), Ct));

    [Fact]
    public async Task Read_MissingLogSection_GivesEmptyTail()
    {
        ReportFolderContents contents = await new ReportFolderReader(new InMemoryReportMediaStore()).ReadAsync(Record(), Ct);
        Assert.Empty(contents.LogTail);
        Assert.Empty(contents.Sections);
        Assert.Null(contents.Clip);
    }

    [Fact]
    public async Task Read_LogTailUsesNewestFiftyLines()
    {
        var media = new InMemoryReportMediaStore();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { lines = Enumerable.Range(0, 60).Select(i => "line " + i) });
        await Put(media, "sections/log.json", json);

        ReportFolderContents contents = await new ReportFolderReader(media).ReadAsync(Record("sections/log.json"), Ct);

        Assert.Equal(Enumerable.Range(10, 50).Select(i => "line " + i), contents.LogTail);
        Assert.Equal(json, Assert.Single(contents.Sections).Json.ToArray());
    }

    [Fact]
    public async Task Viewer_WriteWithSnapshot_ReplacesPageWithoutReporterIdentity()
    {
        var media = new InMemoryReportMediaStore();
        var writer = new ReportViewerWriter(media);
        await writer.WriteAsync(Record(), null, Ct);
        await writer.WriteAsync(Record(), "{\"sections\":{\"server\":{\"tick\":42}}}"u8.ToArray(), Ct);

        string path = Folder + "/viewer.html";
        string html = Encoding.UTF8.GetString((await media.GetAsync(ReportArea.Public, path, 1024 * 1024, Ct))!.Value.Span);
        Assert.Contains("tick", html, StringComparison.Ordinal);
        Assert.DoesNotContain("discord:42", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Ash", html, StringComparison.Ordinal);
        Assert.Single(media.Paths(ReportArea.Public));
        Assert.Equal(new ReportBlobHeaders("text/html; charset=utf-8", "no-cache"), media.HeadersOf(ReportArea.Public, path));
    }

    static ReportRecord Record(params string[] entries)
    {
        var manifest = new BugReportManifest(1, Id, "grimhollow", BugReportKind.Bug, "Gameplay", "Fell through the floor",
            new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero),
            entries.Select(name => new BundleEntry(name, 1, new string('a', 64))).ToArray());
        return new ReportRecord(1, Id, RelayHarness.Reference, Folder, manifest.CapturedAt,
            new ReporterIdentity("discord:42", "Ash", null), manifest, null, ServerSnapshotState.None);
    }

    static Task<bool> Put(InMemoryReportMediaStore media, string name, byte[] bytes)
        => media.PutAsync(ReportArea.Public, Folder + "/" + name, bytes,
            new ReportBlobHeaders("application/json; charset=utf-8", "no-cache"), false, Ct);
}
