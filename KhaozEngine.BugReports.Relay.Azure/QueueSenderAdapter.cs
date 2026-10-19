using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;

namespace KhaozEngine.BugReports.Relay.Azure;

internal sealed class QueueSenderAdapter : IReportQueueSender
{
    const int DeliveryRetentionDays = 7;
    readonly QueueClient queue;

    public QueueSenderAdapter(QueueClient queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        this.queue = queue;
    }

    public async Task SendAsync(string text, TimeSpan? visibilityTimeout, CancellationToken ct)
    {
        if (visibilityTimeout is null)
            await queue.SendMessageAsync(text, cancellationToken: ct).ConfigureAwait(false);
        else
        {
            TimeSpan delay = TimeSpan.FromSeconds(Math.Ceiling(visibilityTimeout.Value.TotalSeconds));
            await queue.SendMessageAsync(text, visibilityTimeout: delay,
                timeToLive: delay + TimeSpan.FromDays(DeliveryRetentionDays), cancellationToken: ct).ConfigureAwait(false);
        }
    }
}
