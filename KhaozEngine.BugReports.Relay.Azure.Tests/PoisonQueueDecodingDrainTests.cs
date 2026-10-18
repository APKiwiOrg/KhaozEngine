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

public sealed class PoisonQueueDecodingDrainTests
{
    const string InvalidBody = "private-undecodable-body!";
    const string ReportId = "01900000000070008000000000000001";

    [Fact]
    public async Task UndecodableFullBatch_DrainsFollowingValidMessageInSameRun()
    {
        var handler = new DrainHandler(32, includeValid: true);
        using var http = new HttpClient(handler);
        var log = new PoisonQueueRequeuerTests.RecordingLogger();
        var clients = Clients(http, log);

        Assert.Equal(1, await new PoisonQueueRequeuer(clients.Poison, clients.Work, logger: log).RequeueAsync(default));

        Assert.Equal(3, handler.Receives);
        Assert.Equal(32, handler.Poison.Count);
        Assert.Equal(new ReportWorkItem(ReportWorkKind.CreateIssue, ReportId, 1),
            ReportWorkItemJson.Parse(Assert.Single(handler.Work)));
        Assert.Equal(32, log.Warnings.Count);
        foreach (string id in handler.Poison.Keys)
            Assert.Single(log.Warnings, warning => warning.Contains("message " + id + " ", StringComparison.Ordinal));
        Assert.All(log.Warnings, warning =>
        {
            Assert.Contains("poison", warning, StringComparison.Ordinal);
            Assert.DoesNotContain(InvalidBody, warning, StringComparison.Ordinal);
            Assert.DoesNotContain(ReportId, warning, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EmptyReceive_EndsDrainWithoutSendingOrLogging()
    {
        var handler = new DrainHandler(0, includeValid: false);
        using var http = new HttpClient(handler);
        var log = new PoisonQueueRequeuerTests.RecordingLogger();
        var clients = Clients(http, log);

        Assert.Equal(0, await new PoisonQueueRequeuer(clients.Poison, clients.Work, logger: log).RequeueAsync(default));

        Assert.Equal(1, handler.Receives);
        Assert.Empty(handler.Work);
        Assert.Empty(handler.Poison);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public async Task AnotherClientsDecodingFailure_DoesNotKeepEmptyPoisonDrainRunning()
    {
        var handler = new DrainHandler(0, includeValid: false);
        using var http = new HttpClient(handler);
        var log = new PoisonQueueRequeuerTests.RecordingLogger();
        var clients = Clients(http, log);
        Assert.Empty((await clients.Work.ReceiveMessagesAsync(32, TimeSpan.FromHours(2))).Value);

        Assert.Equal(0, await new PoisonQueueRequeuer(clients.Poison, clients.Work, logger: log).RequeueAsync(default));

        Assert.Equal(1, handler.Receives);
        Assert.Contains("work", Assert.Single(log.Warnings), StringComparison.Ordinal);
        Assert.Empty(handler.Work);
    }

    static (QueueClient Poison, QueueClient Work) Clients(HttpClient http, PoisonQueueRequeuerTests.RecordingLogger log)
    {
        QueueClientOptions options = ReportQueueClientOptions.Create(log);
        options.Transport = new HttpClientTransport(http);
        options.Retry.MaxRetries = 0;
        return (new QueueClient(new Uri("https://unused.invalid/poison"), options),
            new QueueClient(new Uri("https://unused.invalid/work"), options));
    }

    sealed class DrainHandler : HttpMessageHandler
    {
        readonly HashSet<string> hidden = new(StringComparer.Ordinal);
        public Dictionary<string, string> Poison { get; }
        public List<string> Work { get; } = new();
        public int Receives { get; private set; }

        public DrainHandler(int undecodableCount, bool includeValid)
        {
            Poison = Enumerable.Range(0, undecodableCount).ToDictionary(i => "bad-" + i, _ => InvalidBody);
            if (includeValid)
                Poison.Add("valid-id", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    ReportWorkItemJson.Serialize(new ReportWorkItem(ReportWorkKind.CreateIssue, ReportId)))));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/work/messages")
                    return Xml(HttpStatusCode.OK, new XElement("QueueMessagesList", Message("work-bad", InvalidBody)));
                Assert.Equal("/poison/messages", path);
                Assert.Contains("numofmessages=32", request.RequestUri.Query, StringComparison.Ordinal);
                Assert.Contains("visibilitytimeout=7200", request.RequestUri.Query, StringComparison.Ordinal);
                Assert.True(++Receives <= 3, "The drain continued after an empty receive.");
                var batch = Poison.Where(entry => !hidden.Contains(entry.Key)).Take(32).ToArray();
                foreach (var entry in batch)
                    hidden.Add(entry.Key);
                return Xml(HttpStatusCode.OK, new XElement("QueueMessagesList", batch.Select(entry => Message(entry.Key, entry.Value))));
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
