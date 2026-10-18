using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Live facts use marked, pre-created resources and delete only their own data.</summary>
public sealed class AzureLiveTests
{
    const string PublicContainer = "kebugtest-public";
    const string PendingContainer = "kebugtest-pending";
    const string WorkQueue = "kebugtest-work";
    const string QuotaTable = "kebugtestquota";
    static readonly ReportBlobHeaders Headers = new("application/json; charset=utf-8", "no-cache");

    [BugReportsAzureFact]
    public async Task Live_MediaStore_PassesConformance()
    {
        await using var suite = new LiveMediaConformance();
        await suite.Put_ThenGet_RoundTripsBytes();
        await suite.Put_WithoutOverwrite_ExistingPath_ReturnsFalseAndKeepsFirst();
        await suite.Put_WithoutOverwrite_ConcurrentCallers_ExactlyOneWins();
        await suite.Put_WithOverwrite_ReplacesAndReturnsTrue();
        await suite.Get_Absent_ReturnsNull();
        await suite.Get_OverMaxBytes_ThrowsInvalidDataException();
        await suite.Areas_AreIndependent();
        await suite.Exists_ReflectsPut();
    }

    [BugReportsAzureFact]
    public async Task Live_QuotaStore_PassesConformance()
    {
        await using var suite = new LiveQuotaConformance();
        await suite.Read_Absent_ReturnsNull();
        await suite.CreateOnly_WhenAbsent_WritesCountOne();
        await suite.CreateOnly_WhenPresent_ReturnsFalse();
        await suite.Write_WithCurrentVersion_SucceedsAndChangesVersion();
        await suite.Write_WithStaleVersion_ReturnsFalseAndKeepsCount();
    }

    [BugReportsAzureFact]
    public async Task Live_Queue_RoundTripsWorkItem()
    {
        var queue = new ReceiptTrackingQueueClient(ResourceUri("queue", WorkQueue), new DefaultAzureCredential(),
            ReportQueueClientOptions.Create());
        Assert.Empty((await queue.PeekMessagesAsync(1)).Value);
        var item = new ReportWorkItem(ReportWorkKind.CreateIssue, Guid.CreateVersion7().ToString("N"));
        string? messageId = null;
        string? receipt = null;
        try
        {
            await new StorageQueueReportWorkQueue(queue).EnqueueAsync(item, default);
            messageId = queue.Sent!.MessageId;
            receipt = queue.Sent.PopReceipt;
            QueueMessage received = (await queue.ReceiveMessageAsync()).Value;
            Assert.Equal(messageId, received.MessageId);
            receipt = received.PopReceipt;
            Assert.Equal(item, ReportWorkItemJson.Parse(received.Body.ToString()));
        }
        finally
        {
            if (messageId is not null && receipt is not null)
                await queue.DeleteMessageAsync(messageId, receipt);
        }
    }

    [BugReportsAzureFact]
    public async Task Live_Blob_StoresExplicitContentType()
    {
        BlobContainerClient container = Container(PublicContainer);
        BlobContainerClient pending = Container(PendingContainer);
        string path = "t" + Guid.NewGuid().ToString("N") + "/report.json";
        try
        {
            await new BlobReportMediaStore(container, pending).PutAsync(ReportArea.Public, path, new byte[] { 1 }, Headers, false, default);
            var properties = (await container.GetBlobClient(path).GetPropertiesAsync()).Value;
            Assert.Equal("application/json; charset=utf-8", properties.ContentType);
            Assert.Equal("no-cache", properties.CacheControl);
        }
        finally
        {
            await container.GetBlobClient(path).DeleteIfExistsAsync();
        }
    }

    static Uri ResourceUri(string service, string name)
    {
        if (!name.Contains(BugReportsAzureFactAttribute.ResourceMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("Live resources must carry the kebugtest marker.");
        string account = Environment.GetEnvironmentVariable(BugReportsAzureFactAttribute.EnvironmentVariable)
            ?? throw new InvalidOperationException("No test storage account was configured.");
        return new Uri($"https://{account}.{service}.core.windows.net/{name}");
    }

    static BlobContainerClient Container(string name) => new(ResourceUri("blob", name), new DefaultAzureCredential());

    // These subclasses are invoked only from the gated facts, so inherited [Fact] methods cannot bypass the gate.
    sealed class LiveMediaConformance : ReportMediaStoreConformance, IAsyncDisposable
    {
        readonly BlobContainerClient publicContainer = Container(PublicContainer);
        readonly BlobContainerClient pending = Container(PendingContainer);
        readonly ConcurrentDictionary<(ReportArea Area, string Path), byte> written = new();

        protected override IReportMediaStore CreateStore()
            => new PrefixedMedia(new BlobReportMediaStore(publicContainer, pending), written);

        public async ValueTask DisposeAsync()
        {
            foreach (var entry in written.Keys)
                await (entry.Area == ReportArea.Public ? publicContainer : pending).GetBlobClient(entry.Path).DeleteIfExistsAsync();
        }
    }

    sealed class PrefixedMedia(IReportMediaStore inner, ConcurrentDictionary<(ReportArea, string), byte> written) : IReportMediaStore
    {
        readonly string prefix = "t" + Guid.NewGuid().ToString("N") + "/";
        public Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content, ReportBlobHeaders headers, bool overwrite, CancellationToken cancellationToken)
        {
            written.TryAdd((area, prefix + path), 0);
            return inner.PutAsync(area, prefix + path, content, headers, overwrite, cancellationToken);
        }
        public Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes, CancellationToken cancellationToken)
            => inner.GetAsync(area, prefix + path, maxBytes, cancellationToken);
        public Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken)
            => inner.ExistsAsync(area, prefix + path, cancellationToken);
    }

    sealed class LiveQuotaConformance : ReportQuotaStoreConformance, IAsyncDisposable
    {
        readonly TableClient table;
        readonly HashSet<string> written = new(StringComparer.Ordinal);

        public LiveQuotaConformance()
        {
            Uri uri = ResourceUri("table", QuotaTable);
            table = new TableClient(new Uri(uri.GetLeftPart(UriPartial.Authority)), QuotaTable, new DefaultAzureCredential());
        }

        protected override IReportQuotaStore CreateStore() => new PrefixedQuota(new TableReportQuotaStore(table), written);

        public async ValueTask DisposeAsync()
        {
            foreach (string key in written)
            {
                try { await table.DeleteEntityAsync(key, "q", ETag.All); }
                catch (RequestFailedException failure) when (failure.Status == 404) { }
            }
        }
    }

    sealed class PrefixedQuota(IReportQuotaStore inner, HashSet<string> written) : IReportQuotaStore
    {
        readonly string prefix = "t" + Guid.NewGuid().ToString("N");
        public Task<QuotaCounter?> ReadAsync(string key, CancellationToken cancellationToken) => inner.ReadAsync(prefix + key, cancellationToken);
        public Task<bool> TryWriteAsync(string key, long count, string? expectedVersion, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            written.Add(prefix + key);
            return inner.TryWriteAsync(prefix + key, count, expectedVersion, expiresAt, cancellationToken);
        }
    }
}
