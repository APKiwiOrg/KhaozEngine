using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class BugReportOfflineQueueTests : IDisposable
{
    static readonly DateTimeOffset Start = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    readonly string root = Path.Combine(Path.GetTempPath(), "ke-bugqueue-" + Guid.NewGuid().ToString("N"));
    readonly ManualTimeProvider time = new(Start);

    string Dir => Path.Combine(root, "pending");

    readonly List<FakeSink> sinks = [];

    // Every FakeSink a test scripts, so the teardown can assert none ran out of outcomes.
    FakeSink Sink()
    {
        var sink = new FakeSink();
        sinks.Add(sink);
        return sink;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Assert.DoesNotContain(sinks, sink => sink.RanOut);
    }

    // A canonical version 7 id whose ordinal order follows n.
    internal static string Id(int n) => "0192f1c2a3b47c4d8e9f0a1b2c3d" + n.ToString("x4", CultureInfo.InvariantCulture);

    static BugReportSubmission Submission(string id, string? subject, int bytes = 16)
    {
        byte[] bundle = new byte[bytes];
        bundle[0] = 0x50;
        bundle[^1] = 0x4B;
        return new BugReportSubmission(id, subject, bundle);
    }

    BugReportOfflineQueue Queue() => new(Dir, time);

    string[] JsonIds() => Directory.GetFiles(Dir, "*.json")
        .Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Enqueue_writes_zip_and_metadata_pair()
    {
        string id = Id(1);
        BugReportSubmission submission = Submission(id, null, bytes: 123);

        Queue().Enqueue(submission);

        Assert.Equal(submission.Bundle.ToArray(), File.ReadAllBytes(Path.Combine(Dir, id + ".zip")));
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, id + ".json")));
        Assert.Equal(id, meta.RootElement.GetProperty("reportId").GetString());
        Assert.Equal(JsonValueKind.Null, meta.RootElement.GetProperty("subject").ValueKind);
        Assert.Equal(Start, meta.RootElement.GetProperty("queuedAt").GetDateTimeOffset());
        Assert.Equal(123, meta.RootElement.GetProperty("bytes").GetInt64());
        Assert.Empty(Directory.GetFiles(Dir, "*.tmp"));
        Assert.Equal(1, Queue().Count);
    }

    [Fact]
    public void Fourth_report_evicts_the_oldest()
    {
        BugReportOfflineQueue queue = Queue();
        // Ids run against time order, so only queuedAt can pick the oldest.
        for (int t = 0; t < 4; t++)
        {
            queue.Enqueue(Submission(Id(10 - t), "a"));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(BugReportOfflineQueue.MaxReports, queue.Count);
        Assert.Equal([Id(7), Id(8), Id(9)], JsonIds());
        Assert.False(File.Exists(Path.Combine(Dir, Id(10) + ".zip")));
    }

    [Fact]
    public void Bytes_over_30_MB_evict_oldest_first()
    {
        BugReportOfflineQueue queue = Queue();
        const int TwelveMegabytes = 12 * 1024 * 1024;
        for (int t = 0; t < 3; t++)
        {
            queue.Enqueue(Submission(Id(t), "a", TwelveMegabytes));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(2, queue.Count);
        Assert.Equal([Id(1), Id(2)], JsonIds());
    }

    [Fact]
    public async Task Reports_older_than_7_days_expire()
    {
        BugReportOfflineQueue queue = Queue();
        queue.Enqueue(Submission(Id(1), "a"));
        time.Advance(TimeSpan.FromSeconds(1));
        queue.Enqueue(Submission(Id(2), "b"));
        time.Advance(BugReportOfflineQueue.MaxAge);

        var sink = Sink();
        int sent = (await queue.FlushAsync("a", sink, CancellationToken.None)).Sent;

        // Id(1) is 7 days and 1 s old. Id(2) is exactly 7 days old and stays.
        Assert.Equal(0, sent);
        Assert.Empty(sink.Submissions);
        Assert.Equal(1, queue.Count);
        Assert.Equal([Id(2)], JsonIds());

        time.Advance(TimeSpan.FromSeconds(1));
        await queue.FlushAsync("a", sink, CancellationToken.None);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Null_subject_binds_to_the_first_account_that_flushes()
    {
        BugReportOfflineQueue queue = Queue();
        queue.Enqueue(Submission(Id(1), null));
        var sink = Sink().Respond(BugReportSubmitStatus.Queued);

        Assert.Equal(0, (await queue.FlushAsync("a", sink, CancellationToken.None)).Sent);
        Assert.Equal(0, (await queue.FlushAsync("b", sink, CancellationToken.None)).Sent);

        BugReportSubmission only = Assert.Single(sink.Submissions);
        Assert.Equal(Id(1), only.ReportId);
        Assert.Equal("a", only.Subject);
        Assert.Equal(1, queue.Count);
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, Id(1) + ".json")));
        Assert.Equal("a", meta.RootElement.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Other_accounts_reports_are_left_alone()
    {
        BugReportOfflineQueue queue = Queue();
        queue.Enqueue(Submission(Id(1), "a"));
        var sink = Sink();

        Assert.Equal(0, (await queue.FlushAsync("b", sink, CancellationToken.None)).Sent);

        Assert.Empty(sink.Submissions);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Flush_stops_at_first_queued_result()
    {
        BugReportOfflineQueue queue = Queue();
        for (int t = 0; t < 3; t++)
        {
            queue.Enqueue(Submission(Id(10 - t), "a"));
            time.Advance(TimeSpan.FromSeconds(1));
        }
        var sink = Sink().Respond(BugReportSubmitStatus.Sent).Respond(BugReportSubmitStatus.Queued);

        int sent = (await queue.FlushAsync("a", sink, CancellationToken.None)).Sent;

        Assert.Equal(1, sent);
        Assert.Equal([Id(10), Id(9)], sink.Submissions.Select(s => s.ReportId));
        Assert.Equal([Id(8), Id(9)], JsonIds());
    }

    [Fact]
    public async Task Rejected_reports_are_deleted()
    {
        BugReportOfflineQueue queue = Queue();
        queue.Enqueue(Submission(Id(1), "a"));
        time.Advance(TimeSpan.FromSeconds(1));
        queue.Enqueue(Submission(Id(2), "a"));
        var sink = Sink().Respond(BugReportSubmitStatus.Rejected, "bundle_invalid").Respond(BugReportSubmitStatus.Sent);

        BugReportFlushResult flushed = await queue.FlushAsync("a", sink, CancellationToken.None);

        Assert.Equal(new BugReportFlushResult(1, 1), flushed);
        Assert.Equal(2, sink.Submissions.Count);
        Assert.Equal(0, queue.Count);
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public void Orphans_and_corrupt_metadata_are_cleaned()
    {
        BugReportOfflineQueue queue = Queue();
        queue.Enqueue(Submission(Id(5), "a"));
        File.WriteAllBytes(Path.Combine(Dir, Id(1) + ".zip"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(Dir, Id(2) + ".json"),
            "{\"reportId\":\"" + Id(2) + "\",\"subject\":null,\"queuedAt\":\"2026-10-17T12:00:00+00:00\",\"bytes\":3}");
        File.WriteAllBytes(Path.Combine(Dir, Id(3) + ".zip"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(Dir, Id(3) + ".json"), "{");
        File.WriteAllBytes(Path.Combine(Dir, Id(4) + ".zip.tmp"), [1, 2, 3]);
        // A bundle truncated after its metadata was written, as a power loss can leave it.
        File.WriteAllBytes(Path.Combine(Dir, Id(6) + ".zip"), [1]);
        File.WriteAllText(Path.Combine(Dir, Id(6) + ".json"),
            "{\"reportId\":\"" + Id(6) + "\",\"subject\":\"a\",\"queuedAt\":\"2026-10-17T12:00:00+00:00\",\"bytes\":3}");
        File.WriteAllText(Path.Combine(Dir, "notes.txt"), "keep");
        File.WriteAllBytes(Path.Combine(Dir, "readme.zip"), [1]);

        Assert.Equal(1, queue.Count);

        string[] left = Directory.GetFiles(Dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal([Id(5) + ".json", Id(5) + ".zip", "notes.txt", "readme.zip"], left);
    }

    [Fact]
    public void Missing_directory_counts_zero_without_creating_it()
    {
        Assert.Equal(0, Queue().Count);
        Assert.False(Directory.Exists(Dir));
    }

    [Fact]
    public void Scan_never_deletes_a_report_being_enqueued()
    {
        BugReportOfflineQueue queue = Queue();
        // Both threads run a fixed count, so an unfair lock cannot stretch the run. The scanner outlasts the writer.
        const int Writes = 30;
        const int Scans = 120;
        Exception? scannerFault = null;
        using var start = new Barrier(2);
        var scanner = new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int i = 0; i < Scans; i++)
                    _ = queue.Count;
            }
            catch (Exception ex)
            {
                scannerFault = ex;
            }
        });
        scanner.Start();
        try
        {
            start.SignalAndWait();
            for (int i = 0; i < Writes; i++)
            {
                string id = Id(i);
                queue.Enqueue(Submission(id, "a"));
                time.Advance(TimeSpan.FromSeconds(1));
                Assert.True(File.Exists(Path.Combine(Dir, id + ".zip")), id + ".zip was deleted");
                Assert.True(File.Exists(Path.Combine(Dir, id + ".json")), id + ".json was deleted");
            }
        }
        finally
        {
            scanner.Join();
        }
        if (scannerFault is not null)
            ExceptionDispatchInfo.Throw(scannerFault);
        Assert.Equal(BugReportOfflineQueue.MaxReports, queue.Count);
    }

    [Fact]
    public async Task Blank_subject_flush_sends_nothing()
    {
        BugReportOfflineQueue queue = Queue();
        queue.Enqueue(Submission(Id(1), null));
        var sink = Sink();

        Assert.Equal(0, (await queue.FlushAsync(null!, sink, CancellationToken.None)).Sent);
        Assert.Equal(0, (await queue.FlushAsync("", sink, CancellationToken.None)).Sent);
        Assert.Equal(0, (await queue.FlushAsync("  ", sink, CancellationToken.None)).Sent);

        Assert.Empty(sink.Submissions);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Throwing_sink_stops_the_flush_and_keeps_the_report()
    {
        BugReportOfflineQueue queue = Queue();
        queue.Enqueue(Submission(Id(1), "a"));
        time.Advance(TimeSpan.FromSeconds(1));
        queue.Enqueue(Submission(Id(2), "a"));
        // A cancellation the caller did not ask for is a sink fault too.
        var sink = Sink().Throw(new OperationCanceledException())
            .Respond(BugReportSubmitStatus.Sent).Respond(BugReportSubmitStatus.Sent);

        Assert.Equal(0, (await queue.FlushAsync("a", sink, CancellationToken.None)).Sent);
        Assert.Single(sink.Submissions);
        Assert.Equal(2, queue.Count);

        // The failed send released its claim, so the next flush delivers both.
        Assert.Equal(2, (await queue.FlushAsync("a", sink, CancellationToken.None)).Sent);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Send_of_a_vanished_bundle_returns_nothing()
    {
        BugReportOfflineQueue queue = Queue();
        queue.Enqueue(Submission(Id(1), "a"));
        File.Delete(Path.Combine(Dir, Id(1) + ".zip"));
        var sink = Sink();

        BugReportSubmitResult? result = await queue.SendOneAsync(Id(1), "a", sink, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public void Enqueue_refuses_a_non_canonical_id()
    {
        Assert.Throws<ArgumentException>(() => Queue().Enqueue(new BugReportSubmission("not-an-id", "a", Encoding.UTF8.GetBytes("x"))));
        Assert.False(Directory.Exists(Dir));
    }
}
