using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core.Pipeline;
using Azure.Storage.Queues;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Work queue payload and host encoding requirements.</summary>
public sealed class StorageQueueReportWorkQueueTests
{
    [Fact]
    public async Task Enqueue_SendsWorkItemJson()
    {
        var sender = new RecordingQueueSender();
        var queue = new StorageQueueReportWorkQueue(sender);
        var item = new ReportWorkItem(ReportWorkKind.AppendServerSnapshot, "01900000000070008000000000000001", 2);
        await queue.EnqueueAsync(item, default);
        Assert.Equal(item, ReportWorkItemJson.Parse(Assert.Single(sender.Messages)));
    }

    [Fact]
    public async Task StorageQueue_DelayedEnqueue_SendsVisibilityTimeout()
    {
        var sender = new RecordingQueueSender();
        var queue = new StorageQueueReportWorkQueue(sender);
        var item = new ReportWorkItem(ReportWorkKind.PurgeRevoked, "01900000000070008000000000000001");

        await queue.EnqueueAsync(item, TimeSpan.FromMinutes(12), default);
        await queue.EnqueueAsync(item, default);

        Assert.Equal(new TimeSpan?[] { TimeSpan.FromMinutes(12), null }, sender.VisibilityTimeouts);
        Assert.Equal(2, sender.Messages.Count);
        Assert.All(sender.Messages, message => Assert.Equal(item, ReportWorkItemJson.Parse(message)));
    }

    [Theory]
    [InlineData(720, 605520)]
    [InlineData(604800, 1209600)]
    public async Task StorageQueue_DelayedEnqueue_SendsSdkVisibilityTimeoutAndTimeToLive(int delaySeconds, int timeToLiveSeconds)
    {
        using var handler = new VisibilityTimeoutHandler();
        using var http = new HttpClient(handler);
        QueueClientOptions options = ReportQueueClientOptions.Create();
        options.Transport = new HttpClientTransport(http);
        options.Retry.MaxRetries = 0;
        var client = new QueueClient(new Uri("https://unused.invalid/work"), options);
        var queue = new StorageQueueReportWorkQueue(client);

        await queue.EnqueueAsync(new ReportWorkItem(ReportWorkKind.CreateIssue, "01900000000070008000000000000001"),
            TimeSpan.FromSeconds(delaySeconds), default);

        Assert.NotNull(handler.RequestUri);
        string[] queryParameters = handler.RequestUri.Query.TrimStart('?').Split('&');
        Assert.Contains($"visibilitytimeout={delaySeconds}", queryParameters);
        Assert.Contains($"messagettl={timeToLiveSeconds}", queryParameters);
    }

    [Fact]
    public async Task StorageQueue_ImmediateEnqueue_OmitsSdkVisibilityTimeoutAndTimeToLive()
    {
        using var handler = new VisibilityTimeoutHandler();
        using var http = new HttpClient(handler);
        QueueClientOptions options = ReportQueueClientOptions.Create();
        options.Transport = new HttpClientTransport(http);
        options.Retry.MaxRetries = 0;
        var client = new QueueClient(new Uri("https://unused.invalid/work"), options);
        var queue = new StorageQueueReportWorkQueue(client);

        await queue.EnqueueAsync(new ReportWorkItem(ReportWorkKind.CreateIssue, "01900000000070008000000000000001"), default);

        Assert.NotNull(handler.RequestUri);
        Assert.DoesNotContain("visibilitytimeout=", handler.RequestUri.Query);
        Assert.DoesNotContain("messagettl=", handler.RequestUri.Query);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(6048000000001L)]
    public async Task StorageQueue_RejectsNegativeOrOverSevenDayDelay(long ticks)
    {
        var sender = new RecordingQueueSender();
        var queue = new StorageQueueReportWorkQueue(sender);
        var item = new ReportWorkItem(ReportWorkKind.CreateIssue, "01900000000070008000000000000001");

        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            queue.EnqueueAsync(item, TimeSpan.FromTicks(ticks), default));

        Assert.Equal("visibleAfter", error.ParamName);
        Assert.Empty(sender.Messages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task StorageQueue_AcceptsZeroAndSevenDayDelays(int days)
    {
        var sender = new RecordingQueueSender();
        var queue = new StorageQueueReportWorkQueue(sender);
        var item = new ReportWorkItem(ReportWorkKind.CreateIssue, "01900000000070008000000000000001");

        await queue.EnqueueAsync(item, TimeSpan.FromDays(days), default);

        Assert.Equal(TimeSpan.FromDays(days), Assert.Single(sender.VisibilityTimeouts));
        Assert.Equal(item, ReportWorkItemJson.Parse(Assert.Single(sender.Messages)));
    }

    [Fact]
    public void QueueClientOptions_UseFunctionsBase64Encoding()
    {
        Assert.Equal(QueueMessageEncoding.Base64, ReportQueueClientOptions.Create().MessageEncoding);
    }

    sealed class VisibilityTimeoutHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("<QueueMessagesList><QueueMessage><MessageId>id</MessageId><InsertionTime>Sun, 18 Oct 2026 00:00:00 GMT</InsertionTime><ExpirationTime>Sun, 25 Oct 2026 00:00:00 GMT</ExpirationTime><PopReceipt>receipt</PopReceipt><TimeNextVisible>Sun, 18 Oct 2026 00:12:00 GMT</TimeNextVisible></QueueMessage></QueueMessagesList>"),
            });
        }
    }
}
