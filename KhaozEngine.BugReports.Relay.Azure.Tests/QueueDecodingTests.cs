using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Azure.Core.Pipeline;
using Azure.Storage.Queues;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Characterizes SDK decoding and proves mixed poison batches continue without exposing their bodies.</summary>
public sealed class QueueDecodingTests
{
    const string InvalidBody = "{\"kind\":\"createIssue\",\"private\":\"secret\"}";
    const string ReportId = "01900000000070008000000000000001";

    [Fact]
    public async Task UndecodablePoison_IsRetainedAndLogged_WhileValidMessageMoves()
    {
        var handler = new MixedBatchHandler();
        using var http = new HttpClient(handler);
        var log = new PoisonQueueRequeuerTests.RecordingLogger();
        QueueClientOptions options = ReportQueueClientOptions.Create(log);
        options.Transport = new HttpClientTransport(http);
        options.Retry.MaxRetries = 0;
        var poison = new QueueClient(new Uri("https://unused.invalid/poison"), options);
        var work = new QueueClient(new Uri("https://unused.invalid/work"), options);

        Assert.Equal(1, await new PoisonQueueRequeuer(poison, work).RequeueAsync(default));

        Assert.Equal("bad-id", Assert.Single(handler.Poison.Keys));
        Assert.Equal(new ReportWorkItem(ReportWorkKind.CreateIssue, ReportId, 1),
            ReportWorkItemJson.Parse(Assert.Single(handler.Work)));
        string warning = Assert.Single(log.Warnings);
        Assert.Contains("poison", warning, StringComparison.Ordinal);
        Assert.Contains("bad-id", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(InvalidBody, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(ReportId, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Base64WithoutDecodingHandler_ThrowsBeforeReturningMixedBatch()
    {
        var handler = new MixedBatchHandler();
        using var http = new HttpClient(handler);
        var options = new QueueClientOptions
        {
            MessageEncoding = QueueMessageEncoding.Base64,
            Transport = new HttpClientTransport(http),
        };
        options.Retry.MaxRetries = 0;
        var queue = new QueueClient(new Uri("https://unused.invalid/poison"), options);

        await Assert.ThrowsAsync<FormatException>(() => queue.ReceiveMessagesAsync(32, TimeSpan.FromHours(2)));

        Assert.Equal(2, handler.Poison.Count);
        Assert.Empty(handler.Work);
    }

    sealed class MixedBatchHandler : HttpMessageHandler
    {
        bool received;
        public Dictionary<string, string> Poison { get; } = new()
        {
            ["bad-id"] = InvalidBody,
            ["valid-id"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                ReportWorkItemJson.Serialize(new ReportWorkItem(ReportWorkKind.CreateIssue, ReportId)))),
        };
        public List<string> Work { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/poison/messages", path);
                var body = new XElement("QueueMessagesList", received ? Array.Empty<XElement>() :
                    Poison.Select(entry => Message(entry.Key, entry.Value)));
                received = true;
                return Xml(HttpStatusCode.OK, body);
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
