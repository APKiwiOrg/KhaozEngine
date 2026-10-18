using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Runs real SDK pipelines against an in-process HTTP handler, with no network or credentials.</summary>
public sealed class AdapterTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobUpload_SetsHeadersAndConditionalCreate(bool overwrite)
    {
        using var http = Http(async request =>
        {
            Assert.Equal(overwrite ? null : "*", request.Headers.IfNoneMatch.ToString() is "" ? null : request.Headers.IfNoneMatch.ToString());
            Assert.Equal("application/json; charset=utf-8", Assert.Single(request.Headers.GetValues("x-ms-blob-content-type")));
            Assert.Equal("no-cache", Assert.Single(request.Headers.GetValues("x-ms-blob-cache-control")));
            Assert.Equal(new byte[] { 1, 2 }, await request.Content!.ReadAsByteArrayAsync());
            return Response(HttpStatusCode.Created);
        });
        var adapter = new BlobContainerAdapter(Blob(http));
        Assert.True(await adapter.UploadAsync("a.json", new byte[] { 1, 2 }, new("application/json; charset=utf-8", "no-cache"), overwrite, default));
    }

    [Theory]
    [InlineData(404, "BlobNotFound")]
    [InlineData(404, "ContainerNotFound")]
    [InlineData(404, "UnknownFailure")]
    [InlineData(403, "AuthorizationFailure")]
    [InlineData(500, "InternalError")]
    public async Task BlobRead_OnlyMissingBlobIsAbsent(int status, string errorCode)
    {
        using var http = Http(_ => Task.FromResult(Response((HttpStatusCode)status, errorCode)));
        var adapter = new BlobContainerAdapter(Blob(http));
        if (status == 404 && errorCode == "BlobNotFound")
        {
            Assert.Null(await adapter.DownloadAsync("a.json", 10, default));
            Assert.False(await adapter.ExistsAsync("a.json", default));
        }
        else
        {
            var read = await Assert.ThrowsAsync<RequestFailedException>(() => adapter.DownloadAsync("a.json", 10, default));
            Assert.Equal(status, read.Status);
            Assert.Equal(errorCode, read.ErrorCode);
            var exists = await Assert.ThrowsAsync<RequestFailedException>(() => adapter.ExistsAsync("a.json", default));
            Assert.Equal(status, exists.Status);
            Assert.Equal(errorCode, exists.ErrorCode);
        }
    }

    [Fact]
    public async Task BlobExists_Present_ReturnsTrue()
    {
        using var http = Http(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            var response = Response(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(Array.Empty<byte>());
            return Task.FromResult(response);
        });
        Assert.True(await new BlobContainerAdapter(Blob(http)).ExistsAsync("a.json", default));
    }

    [Fact]
    public async Task BlobDownload_RefusesDeclaredLengthBeforeReadingBody()
    {
        using var stream = new UnreadableStream();
        using var http = Http(_ =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK);
            response.Content = new StreamContent(stream);
            response.Content.Headers.ContentLength = 20;
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new BlobContainerAdapter(Blob(http)).DownloadAsync("a.json", 19, default));
        Assert.False(stream.ReadAttempted);
    }

    [Fact]
    public async Task BlobDownload_WithinCapReadsBytes()
    {
        using var http = Http(_ =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(new byte[] { 1, 2, 3 });
            response.Content.Headers.ContentLength = 3;
            return Task.FromResult(response);
        });
        ReadOnlyMemory<byte>? bytes = await new BlobContainerAdapter(Blob(http)).DownloadAsync("a.json", 3, default);
        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes.Value.ToArray());
    }

    [Theory]
    [InlineData(409)]
    [InlineData(412)]
    public async Task BlobUpload_CreateConflictReturnsFalse_OverwriteFaultThrows(int status)
    {
        using var http = Http(_ => Task.FromResult(Response((HttpStatusCode)status)));
        var adapter = new BlobContainerAdapter(Blob(http));
        Assert.False(await adapter.UploadAsync("a.json", new byte[] { 1 }, new("text/plain", "no-cache"), false, default));
        await Assert.ThrowsAsync<RequestFailedException>(() => adapter.UploadAsync("a.json", new byte[] { 1 }, new("text/plain", "no-cache"), true, default));
    }

    [Fact]
    public async Task TableWrites_UseKeyCountExpiryAndConditionalReplace()
    {
        int requests = 0;
        using var http = Http(async request =>
        {
            string json = await request.Content!.ReadAsStringAsync();
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal("gd-20261018", root.GetProperty("PartitionKey").GetString());
            Assert.Equal("q", root.GetProperty("RowKey").GetString());
            Assert.Equal("7", root.GetProperty("Count").ToString());
            Assert.Equal("Edm.Int64", root.GetProperty("Count@odata.type").GetString());
            Assert.StartsWith("2026-10-19T00:00:00", root.GetProperty("ExpiresAt").GetString(), StringComparison.Ordinal);
            if (requests++ == 0)
                Assert.Equal(HttpMethod.Post, request.Method);
            else
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("\"version\"", request.Headers.IfMatch.ToString());
            }
            return Response(HttpStatusCode.NoContent);
        });
        var adapter = new QuotaTableAdapter(Table(http));
        var expiry = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        Assert.True(await adapter.AddAsync("gd-20261018", 7, expiry, default));
        Assert.True(await adapter.ReplaceAsync("gd-20261018", 7, "\"version\"", expiry, default));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task TableRead_Only404IsAbsent(int status)
    {
        using var http = Http(_ => Task.FromResult(Response((HttpStatusCode)status)));
        var adapter = new QuotaTableAdapter(Table(http));
        if (status == 404)
            Assert.Null(await adapter.GetAsync("key", default));
        else
            Assert.Equal(status, (await Assert.ThrowsAsync<RequestFailedException>(() => adapter.GetAsync("key", default))).Status);
    }

    [Fact]
    public async Task TableRead_MapsInt64CountAndETag()
    {
        using var http = Http(_ =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK);
            response.Content = new StringContent("""
                {"odata.etag":"\"version\"","PartitionKey":"key","RowKey":"q","Count@odata.type":"Edm.Int64","Count":"9223372036854775806"}
                """, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        });
        Assert.Equal(new QuotaCounter(9223372036854775806, "\"version\""), await new QuotaTableAdapter(Table(http)).GetAsync("key", default));
    }

    [Theory]
    [InlineData(409)]
    [InlineData(412)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task TableWrites_ClassifyOnlyTheirOwnConflicts(int status)
    {
        using var http = Http(_ => Task.FromResult(Response((HttpStatusCode)status)));
        var adapter = new QuotaTableAdapter(Table(http));
        if (status == 409)
            Assert.False(await adapter.AddAsync("key", 1, DateTimeOffset.UtcNow, default));
        else
            Assert.Equal(status, (await Assert.ThrowsAsync<RequestFailedException>(() => adapter.AddAsync("key", 1, DateTimeOffset.UtcNow, default))).Status);
        if (status is 404 or 412)
            Assert.False(await adapter.ReplaceAsync("key", 1, "\"version\"", DateTimeOffset.UtcNow, default));
        else
            Assert.Equal(status, (await Assert.ThrowsAsync<RequestFailedException>(() => adapter.ReplaceAsync("key", 1, "\"version\"", DateTimeOffset.UtcNow, default))).Status);
    }

    [Fact]
    public async Task TableReplace_MissingCounter_AgreesWithInMemoryStores()
    {
        using var http = Http(_ => Task.FromResult(Response(HttpStatusCode.NotFound)));
        var expiry = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        var stores = new IReportQuotaStore[]
        {
            new TableReportQuotaStore(Table(http)),
            new TableReportQuotaStore(new InMemoryQuotaTable()),
            new InMemoryReportQuotaStore(),
        };
        foreach (IReportQuotaStore store in stores)
            Assert.False(await store.TryWriteAsync("deleted", 2, "\"old-version\"", expiry, default));
    }

    [Fact]
    public async Task WorkQueue_LetsSdkEncodeExactlyOnce_AndRetainsReceiptForLiveCleanup()
    {
        var item = new ReportWorkItem(ReportWorkKind.CreateIssue, "01900000000070008000000000000001");
        using var http = Http(async request =>
        {
            string xml = await request.Content!.ReadAsStringAsync();
            var document = System.Xml.Linq.XDocument.Parse(xml);
            Assert.Equal(item, ReportWorkItemJson.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(document.Root!.Element("MessageText")!.Value))));
            var response = Response(HttpStatusCode.Created);
            response.Content = new StringContent("<QueueMessagesList><QueueMessage><MessageId>id</MessageId><InsertionTime>Sun, 18 Oct 2026 00:00:00 GMT</InsertionTime><ExpirationTime>Sun, 25 Oct 2026 00:00:00 GMT</ExpirationTime><PopReceipt>receipt</PopReceipt><TimeNextVisible>Sun, 18 Oct 2026 00:00:00 GMT</TimeNextVisible></QueueMessage></QueueMessagesList>");
            return response;
        });
        QueueClientOptions options = ReportQueueClientOptions.Create();
        options.Transport = new HttpClientTransport(http);
        options.Retry.MaxRetries = 0;
        var queue = new ReceiptTrackingQueueClient(new Uri("https://unused.invalid/work"), options);
        await new StorageQueueReportWorkQueue(queue).EnqueueAsync(item, default);
        Assert.NotNull(queue.Sent);
        Assert.Equal("id", queue.Sent.MessageId);
        Assert.Equal("receipt", queue.Sent.PopReceipt);
    }

    static BlobContainerClient Blob(HttpClient http)
    {
        var options = new BlobClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        return new BlobContainerClient(new Uri("https://unused.invalid/container"), options);
    }

    static TableClient Table(HttpClient http)
    {
        var options = new TableClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        return new TableClient(new Uri("https://unused.invalid/kebugtestquota"), options);
    }

    static HttpClient Http(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => new(new Handler(respond));
    static HttpResponseMessage Response(HttpStatusCode status, string errorCode = "TestFailure")
    {
        string json = (int)status >= 400
            ? System.Text.Json.JsonSerializer.Serialize(new { error = new { code = errorCode, message = "test failure" } })
            : "{}";
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.Add("x-ms-request-id", "test-request");
        if ((int)status >= 400) response.Headers.Add("x-ms-error-code", errorCode);
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"version\"");
        return response;
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }

    sealed class UnreadableStream : Stream
    {
        public bool ReadAttempted { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Body must not be read.");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
