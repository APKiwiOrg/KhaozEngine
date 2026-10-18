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
    public Task Live_Media_Put_ThenGet_RoundTripsBytes()
        => RunMedia(suite => suite.Put_ThenGet_RoundTripsBytes());

    [BugReportsAzureFact]
    public Task Live_Media_Put_WithoutOverwrite_ExistingPath_ReturnsFalseAndKeepsFirst()
        => RunMedia(suite => suite.Put_WithoutOverwrite_ExistingPath_ReturnsFalseAndKeepsFirst());

    [BugReportsAzureFact]
    public Task Live_Media_Put_WithoutOverwrite_ConcurrentCallers_ExactlyOneWins()
        => RunMedia(suite => suite.Put_WithoutOverwrite_ConcurrentCallers_ExactlyOneWins());

    [BugReportsAzureFact]
    public Task Live_Media_Put_WithOverwrite_ReplacesAndReturnsTrue()
        => RunMedia(suite => suite.Put_WithOverwrite_ReplacesAndReturnsTrue());

    [BugReportsAzureFact]
    public Task Live_Media_Get_Absent_ReturnsNull()
        => RunMedia(suite => suite.Get_Absent_ReturnsNull());

    [BugReportsAzureFact]
    public Task Live_Media_Get_OverMaxBytes_ThrowsInvalidDataException()
        => RunMedia(suite => suite.Get_OverMaxBytes_ThrowsInvalidDataException());

    [BugReportsAzureFact]
    public Task Live_Media_Areas_AreIndependent()
        => RunMedia(suite => suite.Areas_AreIndependent());

    [BugReportsAzureFact]
    public Task Live_Media_Exists_ReflectsPut()
        => RunMedia(suite => suite.Exists_ReflectsPut());

    [BugReportsAzureFact]
    public Task Live_Quota_Read_Absent_ReturnsNull()
        => RunQuota(suite => suite.Read_Absent_ReturnsNull());

    [BugReportsAzureFact]
    public Task Live_Quota_CreateOnly_WhenAbsent_WritesCountOne()
        => RunQuota(suite => suite.CreateOnly_WhenAbsent_WritesCountOne());

    [BugReportsAzureFact]
    public Task Live_Quota_CreateOnly_WhenPresent_ReturnsFalse()
        => RunQuota(suite => suite.CreateOnly_WhenPresent_ReturnsFalse());

    [BugReportsAzureFact]
    public Task Live_Quota_Write_WithCurrentVersion_SucceedsAndChangesVersion()
        => RunQuota(suite => suite.Write_WithCurrentVersion_SucceedsAndChangesVersion());

    [BugReportsAzureFact]
    public Task Live_Quota_Write_WithStaleVersion_ReturnsFalseAndKeepsCount()
        => RunQuota(suite => suite.Write_WithStaleVersion_ReturnsFalseAndKeepsCount());

    static async Task RunMedia(Func<LiveMediaConformance, Task> fact)
    {
        await using var suite = new LiveMediaConformance();
        await fact(suite);
    }

    static async Task RunQuota(Func<LiveQuotaConformance, Task> fact)
    {
        await using var suite = new LiveQuotaConformance();
        await fact(suite);
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
