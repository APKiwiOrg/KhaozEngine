using System.Threading.Tasks;
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
    public void QueueClientOptions_UseFunctionsBase64Encoding()
    {
        Assert.Equal(QueueMessageEncoding.Base64, ReportQueueClientOptions.Create().MessageEncoding);
    }
}
