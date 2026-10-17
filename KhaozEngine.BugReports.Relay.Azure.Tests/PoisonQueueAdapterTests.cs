using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core.Pipeline;
using Azure.Storage.Queues;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Exercises the real queue SDK through an in-process transport, including decoded bodies and receipts.</summary>
public sealed class PoisonQueueAdapterTests
{
    [Fact]
    public async Task ReceiveDeleteAndRelease_PreserveIdsReceiptsAndVisibility()
    {
        using var http = new HttpClient(new Handler());
        QueueClientOptions options = ReportQueueClientOptions.Create();
        options.Transport = new HttpClientTransport(http);
        options.Retry.MaxRetries = 0;
        var adapter = new PoisonQueueAdapter(new QueueClient(new Uri("https://unused.invalid/poison"), options));
        var messages = await adapter.ReceiveAsync(32, TimeSpan.FromHours(2), default);
        Assert.Collection(messages,
            first => Assert.Equal(new PoisonQueueMessage("first", "receipt-first", "unencoded text"), first),
            second => Assert.Equal(new PoisonQueueMessage("second", "receipt-second", "malformed-json"), second));
        await adapter.DeleteAsync(messages[0], default);
        await adapter.SetVisibilityAsync(messages[1], TimeSpan.Zero, default);
    }

    sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string query = request.RequestUri.Query;
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/poison/messages", path);
                Assert.Contains("numofmessages=32", query, StringComparison.Ordinal);
                Assert.Contains("visibilitytimeout=7200", query, StringComparison.Ordinal);
                response.StatusCode = HttpStatusCode.OK;
                response.Content = new StringContent("""
                    <QueueMessagesList>
                      <QueueMessage><MessageId>first</MessageId><InsertionTime>Sun, 18 Oct 2026 00:00:00 GMT</InsertionTime><ExpirationTime>Sun, 25 Oct 2026 00:00:00 GMT</ExpirationTime><PopReceipt>receipt-first</PopReceipt><TimeNextVisible>Sun, 18 Oct 2026 02:00:00 GMT</TimeNextVisible><DequeueCount>1</DequeueCount><MessageText>dW5lbmNvZGVkIHRleHQ=</MessageText></QueueMessage>
                      <QueueMessage><MessageId>second</MessageId><InsertionTime>Sun, 18 Oct 2026 00:00:00 GMT</InsertionTime><ExpirationTime>Sun, 25 Oct 2026 00:00:00 GMT</ExpirationTime><PopReceipt>receipt-second</PopReceipt><TimeNextVisible>Sun, 18 Oct 2026 02:00:00 GMT</TimeNextVisible><DequeueCount>1</DequeueCount><MessageText>bWFsZm9ybWVkLWpzb24=</MessageText></QueueMessage>
                    </QueueMessagesList>
                    """, Encoding.UTF8, "application/xml");
            }
            else if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal("/poison/messages/first", path);
                Assert.Contains("popreceipt=receipt-first", query, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/poison/messages/second", path);
                Assert.Contains("popreceipt=receipt-second", query, StringComparison.Ordinal);
                Assert.Contains("visibilitytimeout=0", query, StringComparison.Ordinal);
                response.Headers.Add("x-ms-popreceipt", "new-receipt");
                response.Headers.Add("x-ms-time-next-visible", "Sun, 18 Oct 2026 00:00:00 GMT");
            }
            return Task.FromResult(response);
        }
    }
}
