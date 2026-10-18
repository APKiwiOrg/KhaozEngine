using System;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Only the specified service statuses become benign answers.</summary>
public sealed class AdapterClassificationTests
{
    [Theory]
    [InlineData(409, true)]
    [InlineData(412, true)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    public void IsConflict(int status, bool expected) => Assert.Equal(expected, AzureStatus.IsConflict(status));

    [Theory]
    [InlineData(404, true)]
    [InlineData(403, false)]
    [InlineData(500, false)]
    public void IsAbsent(int status, bool expected) => Assert.Equal(expected, AzureStatus.IsAbsent(status));

    [Fact]
    public void Constructors_NullClient_Throw()
    {
        var blob = new BlobContainerClient(new Uri("https://unused.invalid/container"));
        var queue = new QueueClient(new Uri("https://unused.invalid/queue"), new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        Assert.Throws<ArgumentNullException>(() => new BlobReportMediaStore(null!, blob));
        Assert.Throws<ArgumentNullException>(() => new BlobReportMediaStore(blob, null!));
        Assert.Throws<ArgumentNullException>(() => new TableReportQuotaStore((TableClient)null!));
        Assert.Throws<ArgumentNullException>(() => new StorageQueueReportWorkQueue((QueueClient)null!));
        Assert.Throws<ArgumentNullException>(() => new PoisonQueueRequeuer(null!, queue));
        Assert.Throws<ArgumentNullException>(() => new PoisonQueueRequeuer(queue, null!));
    }
}
