using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;

namespace KhaozEngine.BugReports.Relay.Azure;

internal sealed class QueueSenderAdapter : IReportQueueSender
{
    readonly QueueClient queue;

    public QueueSenderAdapter(QueueClient queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        this.queue = queue;
    }

    public async Task SendAsync(string text, CancellationToken ct)
        => await queue.SendMessageAsync(text, cancellationToken: ct).ConfigureAwait(false);
}
