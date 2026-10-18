using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Queues;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

public sealed class PoisonQueueReceiveIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameClient_EmptyReceiveCannotStealUndecodableBatch_AndValidTailDrains(bool sameRequeuer)
    {
        using var harness = new ReceiveHarness(includeValidTail: true);
        var first = new PoisonQueueRequeuer(harness.Poison, harness.Work, logger: harness.Log);
        var second = sameRequeuer ? first : new PoisonQueueRequeuer(harness.Poison, harness.Work, logger: harness.Log);
        Task<int> drain = first.RequeueAsync(default);
        await harness.DecodingEntered.Task;
        Task<int> empty = second.RequeueAsync(default);
        try
        {
            Assert.Equal(1, harness.Handler.Receives);
            Assert.False(empty.IsCompleted);
        }
        finally
        {
            harness.ReleaseDecoding.SetResult();
        }

        Assert.Equal(0, await empty);
        Assert.Equal(1, await drain);
        Assert.Equal(4, harness.Handler.Receives);
        Assert.Equal(32, harness.Handler.Poison.Count);
        Assert.Equal(new ReportWorkItem(ReportWorkKind.CreateIssue, ReceiveHarness.ReportId, 1),
            ReportWorkItemJson.Parse(Assert.Single(harness.Handler.Work)));
        Assert.Equal(32, harness.Log.Warnings.Count);
        Assert.All(harness.Handler.Poison.Keys, id =>
            Assert.Single(harness.Log.Warnings, warning => warning.Contains("message " + id + " ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CancelledWaitingReceive_DoesNotReleaseTheActiveReceivesGate()
    {
        using var harness = new ReceiveHarness();
        var first = new PoisonQueueAdapter(harness.Poison);
        var second = new PoisonQueueAdapter(harness.Poison);
        Task<PoisonQueueBatch> active = first.ReceiveAsync(32, TimeSpan.FromHours(2), default);
        await harness.DecodingEntered.Task;
        Task<PoisonQueueBatch>? next = null;
        try
        {
            using var cancellation = new CancellationTokenSource();
            Task<PoisonQueueBatch> waiting = second.ReceiveAsync(32, TimeSpan.FromHours(2), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            next = second.ReceiveAsync(32, TimeSpan.FromHours(2), default);
            Assert.False(next.IsCompleted);
            Assert.Equal(1, harness.Handler.Receives);
        }
        finally
        {
            harness.ReleaseDecoding.SetResult();
        }

        PoisonQueueBatch batch = await active;
        Assert.Empty(batch.Messages);
        Assert.Equal(32, batch.UndecodableCount);
        PoisonQueueBatch empty = await next!;
        Assert.Empty(empty.Messages);
        Assert.Equal(0, empty.UndecodableCount);
        Assert.Equal(2, harness.Handler.Receives);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedActiveReceive_ReleasesGateForNextReceive(bool cancelActive)
    {
        using var harness = new ReceiveHarness(failFirstReceive: true);
        var adapter = new PoisonQueueAdapter(harness.Poison);
        using var cancellation = new CancellationTokenSource();
        Task<PoisonQueueBatch> active = adapter.ReceiveAsync(32, TimeSpan.FromHours(2), cancellation.Token);
        await harness.Handler.RequestEntered.Task;
        Task<PoisonQueueBatch> next = new PoisonQueueAdapter(harness.Poison).ReceiveAsync(32, TimeSpan.FromHours(2), default);
        if (cancelActive) cancellation.Cancel();
        harness.Handler.ReleaseRequest.SetResult();

        if (cancelActive)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        else
            await Assert.ThrowsAsync<RequestFailedException>(() => active);
        PoisonQueueBatch batch = await next;
        Assert.Empty(batch.Messages);
        Assert.Equal(0, batch.UndecodableCount);
        Assert.Equal(2, harness.Handler.Receives);
    }

    sealed class ReceiveHarness : IDisposable
    {
        public const string ReportId = "01900000000070008000000000000001";
        readonly HttpClient http;
        public ReceiveHandler Handler { get; }
        public PoisonQueueRequeuerTests.RecordingLogger Log { get; } = new();
        public QueueClient Poison { get; }
        public QueueClient Work { get; }
        public TaskCompletionSource DecodingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDecoding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReceiveHarness(bool includeValidTail = false, bool failFirstReceive = false)
        {
            Handler = new ReceiveHandler(includeValidTail, failFirstReceive);
            http = new HttpClient(Handler);
            QueueClientOptions options = ReportQueueClientOptions.Create(Log);
            options.Transport = new HttpClientTransport(http);
            options.Retry.MaxRetries = 0;
            options.MessageDecodingFailed += async args =>
            {
                if (args.ReceivedMessage?.MessageId != "bad-31") return;
                DecodingEntered.SetResult();
                await ReleaseDecoding.Task;
            };
            Poison = new QueueClient(new Uri("https://unused.invalid/poison"), options);
            Work = new QueueClient(new Uri("https://unused.invalid/work"), options);
        }

        public void Dispose() => http.Dispose();
    }

    sealed class ReceiveHandler : HttpMessageHandler
    {
        readonly bool includeValidTail;
        readonly bool failFirstReceive;
        int receives;
        public int Receives => Volatile.Read(ref receives);
        public Dictionary<string, string> Poison { get; } = Enumerable.Range(0, 32)
            .ToDictionary(i => "bad-" + i, _ => "private-undecodable-body!");
        public List<string> Work { get; } = new();
        public TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReceiveHandler(bool includeValidTail, bool failFirstReceive)
        {
            this.includeValidTail = includeValidTail;
            this.failFirstReceive = failFirstReceive;
            if (includeValidTail)
                Poison.Add("valid-id", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    ReportWorkItemJson.Serialize(new ReportWorkItem(ReportWorkKind.CreateIssue, ReceiveHarness.ReportId)))));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/poison/messages", path);
                int receive = Interlocked.Increment(ref receives);
                if (receive == 1 && failFirstReceive)
                {
                    RequestEntered.SetResult();
                    await ReleaseRequest.Task.WaitAsync(cancellationToken);
                    return Xml(HttpStatusCode.InternalServerError,
                        new XElement("Error", new XElement("Code", "InternalError"), new XElement("Message", "Receive failed")));
                }
                if (receive == 1)
                    return Xml(HttpStatusCode.OK, new XElement("QueueMessagesList", Poison.Take(32)
                        .Select(entry => Message(entry.Key, entry.Value))));
                if (receive == 3 && includeValidTail)
                    return Xml(HttpStatusCode.OK, new XElement("QueueMessagesList", Message("valid-id", Poison["valid-id"])));
                Assert.True(receive <= 4, "The drain continued after an empty receive.");
                return Xml(HttpStatusCode.OK, new XElement("QueueMessagesList"));
            }
            if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal("/poison/messages/valid-id", path);
                Assert.True(Poison.Remove("valid-id"));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/work/messages", path);
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Work.Add(Encoding.UTF8.GetString(Convert.FromBase64String(xml.Root!.Element("MessageText")!.Value)));
            return Xml(HttpStatusCode.Created, new XElement("QueueMessagesList", Message("work-id", "")));
        }

        static XElement Message(string id, string text) => new("QueueMessage",
            new XElement("MessageId", id), new XElement("PopReceipt", "receipt-" + id),
            new XElement("InsertionTime", "Sun, 18 Oct 2026 00:00:00 GMT"),
            new XElement("ExpirationTime", "Sun, 25 Oct 2026 00:00:00 GMT"),
            new XElement("TimeNextVisible", "Sun, 18 Oct 2026 02:00:00 GMT"),
            new XElement("DequeueCount", 1), new XElement("MessageText", text));

        static HttpResponseMessage Xml(HttpStatusCode status, XElement body)
            => new(status) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/xml") };
    }
}
