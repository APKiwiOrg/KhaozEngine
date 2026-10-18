using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class RelayIntakeRecoveryTests
{
    [Fact]
    public async Task Failed_enqueue_then_immediate_retry_leaves_one_issue()
    {
        var harness = FailingFirstEnqueue();
        await Assert.ThrowsAsync<IOException>(() => harness.IntakeAsync());
        Assert.NotNull(await harness.Records.LoadAcceptedAsync(RelayHarness.Id, CancellationToken.None));
        Assert.NotNull(await harness.Records.LoadReportAsync(RelayHarness.Id, CancellationToken.None));
        Assert.Equal(0, harness.Work.Count);

        Assert.Equal(202, (await harness.IntakeAsync()).StatusCode);
        Assert.Equal(1, harness.Work.Count);
        await WorkerFixtures.DrainAsync(harness);
        await WorkerFixtures.ProcessAsync(harness);
        Assert.Single(harness.GitHub.Issues);
    }

    [Fact]
    public async Task Submitter_sent_after_failed_enqueue_keeps_recoverable_relay_work()
    {
        var harness = FailingFirstEnqueue();
        using var handler = new RelayHandler(harness.Relay);
        using var http = new HttpClient(handler);
        var sink = new HttpRelaySink(http, new Uri("https://relay.example.test/"), () => "good")
        {
            RetryPolicy = HttpRelaySink.DefaultPolicy with { MaxAttempts = 1 },
        };
        string directory = Path.Combine(Path.GetTempPath(), "ke-relay-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var queue = new BugReportOfflineQueue(directory, harness.Clock);
            var submitter = new BugReportSubmitter(sink, queue);
            var submission = new BugReportSubmission(RelayHarness.Id, "discord:42", TestBundles.Valid(RelayHarness.Id));
            Assert.Equal(BugReportSubmitStatus.Queued, (await submitter.SubmitAsync(submission, CancellationToken.None)).Status);
            Assert.Equal(1, queue.Count);
            Assert.Equal(0, harness.Work.Count);

            Assert.Equal(BugReportSubmitStatus.Sent, (await submitter.SubmitAsync(submission, CancellationToken.None)).Status);
            Assert.Equal(0, queue.Count);
            Assert.Equal(1, harness.Work.Count);
            await WorkerFixtures.DrainAsync(harness);
            await WorkerFixtures.ProcessAsync(harness);
            Assert.Single(harness.GitHub.Issues);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Lost_index_race_queues_before_acknowledging_while_winner_enqueue_is_held()
    {
        var indexHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIndex = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueueHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnqueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int indexWrites = 0;
        int enqueues = 0;
        var harness = new RelayHarness(wrapMedia: inner => new WorkerMediaStore(inner)
        {
            BeforeWrite = async path =>
            {
                if (path.StartsWith("accepted/", StringComparison.Ordinal) && Interlocked.Increment(ref indexWrites) == 1)
                {
                    indexHeld.SetResult();
                    await releaseIndex.Task;
                }
            },
        }, wrapWork: inner => new EnqueueHook(inner, async () =>
        {
            if (Interlocked.Increment(ref enqueues) == 1)
            {
                enqueueHeld.SetResult();
                await releaseEnqueue.Task;
            }
        }));

        Task<RelayResponse> loser = harness.IntakeAsync();
        await indexHeld.Task;
        Task<RelayResponse> winner = harness.IntakeAsync();
        await enqueueHeld.Task;
        try
        {
            releaseIndex.SetResult();
            Assert.Equal(202, (await loser).StatusCode);
            Assert.False(winner.IsCompleted);
            Assert.Equal(1, harness.Work.Count);
            await WorkerFixtures.DrainAsync(harness);
        }
        finally
        {
            releaseEnqueue.SetResult();
            await winner;
        }
        await WorkerFixtures.DrainAsync(harness);
        Assert.Single(harness.GitHub.Issues);
    }

    static RelayHarness FailingFirstEnqueue()
    {
        int calls = 0;
        return new RelayHarness(wrapWork: inner => new EnqueueHook(inner, () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("Queue unavailable.");
            return Task.CompletedTask;
        }));
    }

    sealed class EnqueueHook(InMemoryReportWorkQueue inner, Func<Task> beforeEnqueue) : IReportWorkQueue
    {
        public async Task EnqueueAsync(ReportWorkItem item, CancellationToken cancellationToken)
        {
            await beforeEnqueue();
            await inner.EnqueueAsync(item, cancellationToken);
        }
    }

    sealed class RelayHandler(BugReportRelay relay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>();
            foreach (var header in request.Headers) headers.Add(header.Key, string.Join(",", header.Value));
            using Stream body = await request.Content!.ReadAsStreamAsync(cancellationToken);
            try
            {
                RelayResponse response = await relay.IntakeAsync(new RelayRequest(headers, body, request.Content.Headers.ContentLength), cancellationToken);
                return new HttpResponseMessage((HttpStatusCode)response.StatusCode) { Content = new StringContent(response.JsonBody!) };
            }
            catch (IOException)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
        }
    }
}
