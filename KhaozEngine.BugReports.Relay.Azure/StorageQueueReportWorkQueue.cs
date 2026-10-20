using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;

namespace KhaozEngine.BugReports.Relay.Azure;

/// <summary>Sends relay work JSON through a host-owned Azure Storage queue client.</summary>
public sealed class StorageQueueReportWorkQueue : IReportWorkQueue
{
    readonly IReportQueueSender sender;

    /// <summary>
    /// Wraps an existing work queue. Hosts must build the work and poison QueueClient instances with
    /// <see cref="ReportQueueClientOptions.Create"/>, matching the Azure Functions trigger's Base64 default.
    /// </summary>
    public StorageQueueReportWorkQueue(QueueClient queue) : this(new QueueSenderAdapter(queue)) { }

    internal StorageQueueReportWorkQueue(IReportQueueSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        this.sender = sender;
    }

    /// <inheritdoc />
    public Task EnqueueAsync(ReportWorkItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return sender.SendAsync(ReportWorkItemJson.Serialize(item), null, cancellationToken);
    }

    /// <inheritdoc />
    public Task EnqueueAsync(ReportWorkItem item, TimeSpan visibleAfter, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(visibleAfter, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(visibleAfter, TimeSpan.FromDays(7));
        cancellationToken.ThrowIfCancellationRequested();
        return sender.SendAsync(ReportWorkItemJson.Serialize(item), visibleAfter, cancellationToken);
    }
}
