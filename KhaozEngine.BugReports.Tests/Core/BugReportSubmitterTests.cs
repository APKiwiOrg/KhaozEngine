using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class BugReportSubmitterTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ke-bugsubmit-" + Guid.NewGuid().ToString("N"));
    readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    string Dir => Path.Combine(root, "pending");

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    static string Id(int n) => BugReportOfflineQueueTests.Id(n);

    static BugReportSubmission Submission(int n, string? subject) => new(Id(n), subject, new byte[] { 0x50, 0x4B, 3, 4 });

    readonly InMemorySink logs = new();

    IDisposable CaptureLogs()
    {
        var options = new LoggerOptions { Synchronous = true };
        options.Sinks.Add(logs);
        return Log.Use(new LogManager(options));
    }

    LogEntry SingleWarning() => Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warn);

    [Fact]
    public async Task Report_is_written_ahead_before_sending()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        var gate = new TaskCompletionSource<BugReportSubmitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new FakeSink().Block(gate);
        var submitter = new BugReportSubmitter(sink, queue);

        Task<BugReportSubmitResult> submit = submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);

        Assert.False(submit.IsCompleted);
        Assert.Equal(1, queue.Count);
        BugReportSubmitResult sent = FakeSink.Result(Submission(1, "a"), BugReportSubmitStatus.Sent);
        gate.SetResult(sent);
        Assert.Equal(sent, await submit);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Queued_result_stays_on_disk()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        var sink = new FakeSink().Respond(BugReportSubmitStatus.Queued, "network").Respond(BugReportSubmitStatus.Sent);
        var submitter = new BugReportSubmitter(sink, queue);

        BugReportSubmitResult result = await submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);

        Assert.Equal(BugReportSubmitStatus.Queued, result.Status);
        Assert.Equal("network", result.Error);
        Assert.Equal(1, queue.Count);

        // The submit released its claim, so a later flush delivers the report.
        Assert.Equal(new BugReportFlushResult(1, 0), await submitter.FlushAsync("a", CancellationToken.None));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Signed_out_submission_never_calls_the_sink()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        var sink = new FakeSink();
        var submitter = new BugReportSubmitter(sink, queue);

        BugReportSubmitResult result = await submitter.SubmitAsync(Submission(1, null), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "signed_out", null), result);
        Assert.Empty(sink.Submissions);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Unwritable_queue_never_throws()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Dir, "a file where the queue directory should be");
        var queue = new BugReportOfflineQueue(Dir, time);
        var sink = new FakeSink().Respond(BugReportSubmitStatus.Queued, "network").Respond(BugReportSubmitStatus.Sent);
        var submitter = new BugReportSubmitter(sink, queue);

        BugReportSubmitResult queued = await submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);
        BugReportSubmitResult sent = await submitter.SubmitAsync(Submission(2, "a"), CancellationToken.None);

        Assert.Equal(BugReportSubmitStatus.Queued, queued.Status);
        Assert.Equal("queue_failed", queued.Error);
        Assert.Equal(BugReportSubmitStatus.Sent, sent.Status);
        Assert.Null(sent.Error);
        Assert.Equal(2, sink.Submissions.Count);
        Assert.Equal(0, queue.Count);
        Assert.Equal(new BugReportFlushResult(0, 0), await submitter.FlushAsync("a", CancellationToken.None));
    }

    [Fact]
    public async Task Flush_during_submit_does_not_send_twice()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        var gate = new TaskCompletionSource<BugReportSubmitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new FakeSink().Block(gate);
        var submitter = new BugReportSubmitter(sink, queue);

        Task<BugReportSubmitResult> submit = submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);
        BugReportFlushResult flushed = await submitter.FlushAsync("a", CancellationToken.None);
        gate.SetResult(FakeSink.Result(Submission(1, "a"), BugReportSubmitStatus.Sent));
        await submit;

        Assert.Equal(default, flushed);
        Assert.Single(sink.Submissions);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Queued_result_for_a_pruned_report_leaves_nothing()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        var gate = new TaskCompletionSource<BugReportSubmitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new FakeSink().Block(gate);
        var submitter = new BugReportSubmitter(sink, queue);

        Task<BugReportSubmitResult> submit = submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);
        for (int n = 2; n <= 4; n++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            queue.Enqueue(Submission(n, "a"));
        }
        gate.SetResult(FakeSink.Result(Submission(1, "a"), BugReportSubmitStatus.Queued));
        BugReportSubmitResult result = await submit;

        Assert.Equal(BugReportSubmitStatus.Queued, result.Status);
        Assert.Equal(3, queue.Count);
        Assert.False(File.Exists(Path.Combine(Dir, Id(1) + ".zip")));
        Assert.False(File.Exists(Path.Combine(Dir, Id(1) + ".json")));
    }

    [Fact]
    public async Task Caller_cancellation_keeps_the_report_and_releases_it()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        using var caller = new CancellationTokenSource();
        var sink = new FakeSink().Hang(caller.Token).Respond(BugReportSubmitStatus.Sent);
        var submitter = new BugReportSubmitter(sink, queue);

        Task<BugReportSubmitResult> submit = submitter.SubmitAsync(Submission(1, "a"), caller.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);

        Assert.Equal(1, queue.Count);
        Assert.Equal(new BugReportFlushResult(1, 0), await submitter.FlushAsync("a", CancellationToken.None));
    }

    [Fact]
    public async Task Throwing_sink_returns_a_result_and_keeps_the_report()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        var sink = new FakeSink().Throw(new InvalidOperationException("boom")).Throw(new OperationCanceledException())
            .Respond(BugReportSubmitStatus.Sent).Respond(BugReportSubmitStatus.Sent);
        var submitter = new BugReportSubmitter(sink, queue);

        BugReportSubmitResult faulted = await submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        BugReportSubmitResult canceled = await submitter.SubmitAsync(Submission(2, "a"), CancellationToken.None);

        var expected = new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, BugReportSubmitter.SinkFailedError, null);
        Assert.Equal(expected, faulted);
        Assert.Equal(expected, canceled);
        Assert.Equal("sink_failed", BugReportSubmitter.SinkFailedError);
        Assert.Equal(2, queue.Count);
        Assert.Equal(new BugReportFlushResult(2, 0), await submitter.FlushAsync("a", CancellationToken.None));
    }

    [Fact]
    public async Task Throwing_sink_without_a_queue_is_queue_failed()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Dir, "a file where the queue directory should be");
        var sink = new FakeSink().Throw(new InvalidOperationException("boom"));
        var submitter = new BugReportSubmitter(sink, new BugReportOfflineQueue(Dir, time));

        BugReportSubmitResult result = await submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, BugReportSubmitter.QueueFailedError, null), result);
    }

    [Fact]
    public async Task Signed_out_submit_with_an_unwritable_queue_is_queue_failed()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Dir, "a file where the queue directory should be");
        var sink = new FakeSink();
        var submitter = new BugReportSubmitter(sink, new BugReportOfflineQueue(Dir, time));

        BugReportSubmitResult result = await submitter.SubmitAsync(Submission(1, null), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "queue_failed", null), result);
        Assert.Empty(sink.Submissions);
    }

    [Fact]
    public async Task Duplicate_submit_while_in_flight_keeps_the_first_claim()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        var gate = new TaskCompletionSource<BugReportSubmitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new FakeSink().Block(gate);
        var submitter = new BugReportSubmitter(sink, queue);

        Task<BugReportSubmitResult> first = submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);
        BugReportSubmitResult second = await submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, BugReportSubmitter.InFlightError, null), second);
        // The first claim still holds, so a flush does not send the report again.
        Assert.Equal(new BugReportFlushResult(0, 0), await submitter.FlushAsync("a", CancellationToken.None));
        gate.SetResult(FakeSink.Result(Submission(1, "a"), BugReportSubmitStatus.Sent));
        Assert.Equal(BugReportSubmitStatus.Sent, (await first).Status);
        Assert.Single(sink.Submissions);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Rejected_send_is_logged_once_with_its_id_and_error()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        var sink = new FakeSink().Respond(BugReportSubmitStatus.Rejected, "blocked");
        var submitter = new BugReportSubmitter(sink, queue);

        BugReportSubmitResult result;
        using (CaptureLogs())
            result = await submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);

        Assert.Equal(BugReportSubmitStatus.Rejected, result.Status);
        Assert.Equal(0, queue.Count);
        LogEntry warning = SingleWarning();
        Assert.Contains(Id(1), warning.Message, StringComparison.Ordinal);
        Assert.Contains("blocked", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relay_validation_reject_is_logged()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.UnprocessableEntity, "{\"error\":\"hash_mismatch\"}");
        var sink = new HttpRelaySink(new HttpClient(handler), new Uri("https://relay.example/"), () => "tok");
        var submitter = new BugReportSubmitter(sink, new BugReportOfflineQueue(Dir, time));

        BugReportSubmitResult result;
        using (CaptureLogs())
            result = await submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Rejected, null, "hash_mismatch", null), result);
        Assert.Contains("hash_mismatch", SingleWarning().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_direct_send_is_logged()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Dir, "a file where the queue directory should be");
        var sink = new FakeSink().Respond(BugReportSubmitStatus.Rejected, "blocked");
        var submitter = new BugReportSubmitter(sink, new BugReportOfflineQueue(Dir, time));

        using (CaptureLogs())
            await submitter.SubmitAsync(Submission(1, "a"), CancellationToken.None);

        Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warn && entry.Message.Contains("blocked", StringComparison.Ordinal)
            && entry.Message.Contains(Id(1), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flush_counts_and_logs_rejections()
    {
        var queue = new BugReportOfflineQueue(Dir, time);
        queue.Enqueue(Submission(1, "a"));
        time.Advance(TimeSpan.FromSeconds(1));
        queue.Enqueue(Submission(2, null));
        var sink = new FakeSink().Respond(BugReportSubmitStatus.Rejected, "blocked").Respond(BugReportSubmitStatus.Sent);
        var submitter = new BugReportSubmitter(sink, queue);

        BugReportFlushResult flushed;
        using (CaptureLogs())
            flushed = await submitter.FlushAsync("a", CancellationToken.None);

        Assert.Equal(new BugReportFlushResult(1, 1), flushed);
        Assert.Equal(0, queue.Count);
        Assert.Contains(Id(1), SingleWarning().Message, StringComparison.Ordinal);
    }
}
