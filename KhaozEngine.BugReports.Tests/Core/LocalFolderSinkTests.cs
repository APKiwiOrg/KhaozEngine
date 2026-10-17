using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class LocalFolderSinkTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ke-bugfolder-" + Guid.NewGuid().ToString("N"));

    string ReportDir => Path.Combine(root, TestBundles.ReportId);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        else if (File.Exists(root))
            File.Delete(root);
    }

    static BugReportSubmission Submission(TestBundles bundle)
        => new(TestBundles.ReportId, "player-1", bundle.Build().ToArray());

    Task<BugReportSubmitResult> Submit(TestBundles bundle)
        => new LocalFolderSink(root, openViewer: false).SubmitAsync(Submission(bundle), CancellationToken.None);

    static string LogSection(int lines)
        => JsonSerializer.Serialize(new { lines = Enumerable.Range(0, lines).Select(i => "line " + i).ToArray() });

    [Fact]
    public async Task Writes_bundle_extracted_entries_and_viewer()
    {
        BugReportSubmission submission = Submission(TestBundles.Default());

        BugReportSubmitResult result = await new LocalFolderSink(root, openViewer: false).SubmitAsync(submission, CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Sent, ReportIds.ReferenceCode(TestBundles.ReportId), null, null), result);
        Assert.Equal(submission.Bundle.ToArray(), File.ReadAllBytes(Path.Combine(ReportDir, "bundle.zip")));
        Assert.Equal(TestBundles.ReportId, BugReportJson.DeserializeManifest(File.ReadAllBytes(Path.Combine(ReportDir, "manifest.json"))).ReportId);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(ReportDir, "sections", "build.json")));
        Assert.Equal(TestBundles.Jpeg(4, 4), File.ReadAllBytes(Path.Combine(ReportDir, "clip", "000.jpg")));
        Assert.True(File.Exists(Path.Combine(ReportDir, "clip", "001.jpg")));
        Assert.True(File.Exists(Path.Combine(ReportDir, "clip", "index.json")));
        Assert.True(File.Exists(Path.Combine(ReportDir, "screenshot.png")));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));

        using JsonDocument data = BugReportViewerPageTests.Data(File.ReadAllText(Path.Combine(ReportDir, "viewer.html")));
        JsonElement page = data.RootElement;
        Assert.Equal(TestBundles.ReportId, page.GetProperty("manifest").GetProperty("reportId").GetString());
        Assert.Equal(2, page.GetProperty("clip").GetProperty("frames").GetArrayLength());
        Assert.Equal("{\n  \"a\": 1\n}", BugReportViewerPageTests.SectionText(data, "build"));
        Assert.Equal(BundleEntryNames.ScreenshotPng, page.GetProperty("media").GetProperty("screenshot").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("reporter").ValueKind);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("server").ValueKind);
    }

    [Fact]
    public async Task Invalid_bundle_is_rejected_and_nothing_is_written()
    {
        BugReportSubmitResult result = await Submit(TestBundles.Default().WithEntry("sections/build.json", Encoding.UTF8.GetBytes("[1]")));

        Assert.Equal(BugReportSubmitStatus.Rejected, result.Status);
        Assert.Null(result.Reference);
        Assert.Equal("section_invalid: sections/build.json", result.Error);
        Assert.False(Directory.Exists(ReportDir));
    }

    [Fact]
    public async Task Rejection_through_the_submitter_is_logged_once()
    {
        var logs = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true };
        options.Sinks.Add(logs);
        var submitter = new BugReportSubmitter(new LocalFolderSink(root, openViewer: false),
            new BugReportOfflineQueue(Path.Combine(root, "pending"), TimeProvider.System));

        BugReportSubmitResult result;
        using (Log.Use(new LogManager(options)))
            result = await submitter.SubmitAsync(Submission(TestBundles.Default().WithEntry("sections/build.json",
                Encoding.UTF8.GetBytes("[1]"))), CancellationToken.None);

        Assert.Equal(BugReportSubmitStatus.Rejected, result.Status);
        LogEntry warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warn);
        Assert.Contains(TestBundles.ReportId, warning.Message, StringComparison.Ordinal);
        Assert.Contains("section_invalid: sections/build.json", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_twice_overwrites_in_place_and_keeps_other_files()
    {
        await Submit(TestBundles.Default());
        string note = Path.Combine(ReportDir, "notes.txt");
        File.WriteAllText(note, "kept");

        BugReportSubmitResult result = await Submit(TestBundles.Default().WithEntry("sections/build.json", Encoding.UTF8.GetBytes("{\"a\":2}")));

        Assert.Equal(BugReportSubmitStatus.Sent, result.Status);
        Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(ReportDir, "sections", "build.json")));
        Assert.Equal("kept", File.ReadAllText(note));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Viewer_shows_the_last_fifty_log_lines()
    {
        await Submit(TestBundles.Default().WithEntry("sections/log.json", Encoding.UTF8.GetBytes(LogSection(60))));

        using JsonDocument data = BugReportViewerPageTests.Data(File.ReadAllText(Path.Combine(ReportDir, "viewer.html")));
        string[] tail = data.RootElement.GetProperty("logTail").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(Enumerable.Range(10, 50).Select(i => "line " + i), tail);
    }

    [Theory]
    [InlineData("{\"lines\":\"not a list\"}")]
    [InlineData("{\"other\":[\"x\"]}")]
    [InlineData("{\"lines\":[1,{\"a\":\"b\"},null]}")]
    public async Task Malformed_log_gives_an_empty_tail(string log)
    {
        BugReportSubmitResult result = await Submit(TestBundles.Default().WithEntry("sections/log.json", Encoding.UTF8.GetBytes(log)));

        Assert.Equal(BugReportSubmitStatus.Sent, result.Status);
        using JsonDocument data = BugReportViewerPageTests.Data(File.ReadAllText(Path.Combine(ReportDir, "viewer.html")));
        Assert.Equal(0, data.RootElement.GetProperty("logTail").GetArrayLength());
    }

    [Fact]
    public async Task Root_with_hash_and_percent_saves_and_opens_the_viewer_path()
    {
        string odd = Path.Combine(root, "bugs #1 100% done %41");
        Uri? opened = null;
        var sink = new LocalFolderSink(odd, openViewer: true, BugReportViewerPage.Render, (uri, _) =>
        {
            opened = uri;
            return Task.FromResult(true);
        });

        BugReportSubmitResult result = await sink.SubmitAsync(Submission(TestBundles.Default()), CancellationToken.None);

        Assert.Equal(BugReportSubmitStatus.Sent, result.Status);
        string viewer = Path.Combine(odd, TestBundles.ReportId, "viewer.html");
        Assert.True(File.Exists(viewer));
        Assert.NotNull(opened);
        Assert.True(opened.IsFile);
        Assert.Equal(viewer, opened.LocalPath);
    }

    [Fact]
    public async Task Write_failure_is_queued()
    {
        File.WriteAllText(root, "a file where the folder should be");

        BugReportSubmitResult result = await Submit(TestBundles.Default());

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, LocalFolderSink.WriteFailedError, null), result);
    }

    [Fact]
    public async Task Unexpected_failure_is_queued()
    {
        var sink = new LocalFolderSink(root, openViewer: false, render: _ => throw new InvalidOperationException("render failed"));

        BugReportSubmitResult result = await sink.SubmitAsync(Submission(TestBundles.Default()), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, LocalFolderSink.WriteFailedError, null), result);
    }

    [Fact]
    public async Task Failure_racing_the_callers_cancel_is_queued()
    {
        using var cancel = new CancellationTokenSource();
        var sink = new LocalFolderSink(root, openViewer: false, render: _ =>
        {
            cancel.Cancel();
            throw new IOException("disk");
        });

        BugReportSubmitResult result = await sink.SubmitAsync(Submission(TestBundles.Default()), cancel.Token);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, LocalFolderSink.WriteFailedError, null), result);
    }

    [Fact]
    public async Task Cancellation_seen_during_the_save_throws()
    {
        using var cancel = new CancellationTokenSource();
        var sink = new LocalFolderSink(root, openViewer: false, render: _ =>
        {
            cancel.Cancel();
            cancel.Token.ThrowIfCancellationRequested();
            return "";
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sink.SubmitAsync(Submission(TestBundles.Default()), cancel.Token));
    }

    [Fact]
    public async Task Caller_cancellation_throws_and_writes_nothing()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalFolderSink(root, openViewer: false).SubmitAsync(Submission(TestBundles.Default()), cancel.Token));
        Assert.False(Directory.Exists(ReportDir));
    }
}
