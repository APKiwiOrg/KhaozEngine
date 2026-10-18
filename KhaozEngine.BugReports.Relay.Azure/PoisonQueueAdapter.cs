using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;

namespace KhaozEngine.BugReports.Relay.Azure;

internal sealed class PoisonQueueAdapter : IPoisonQueue
{
    readonly QueueClient queue;

    public PoisonQueueAdapter(QueueClient queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        this.queue = queue;
    }

    public async Task<PoisonQueueBatch> ReceiveAsync(int maxMessages, TimeSpan visibilityTimeout, CancellationToken ct)
    {
        SemaphoreSlim gate = ReportQueueDecodingFailures.ReceiveGate(queue);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Discard failures from earlier receives performed outside this adapter.
            ReportQueueDecodingFailures.Take(queue);
            var messages = (await queue.ReceiveMessagesAsync(maxMessages, visibilityTimeout, ct).ConfigureAwait(false)).Value;
            var result = new PoisonQueueMessage[messages.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = new PoisonQueueMessage(messages[i].MessageId, messages[i].PopReceipt, messages[i].Body.ToString());
            return new PoisonQueueBatch(result, ReportQueueDecodingFailures.Take(queue));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(PoisonQueueMessage message, CancellationToken ct)
        => await queue.DeleteMessageAsync(message.Id, message.PopReceipt, ct).ConfigureAwait(false);

    public async Task SetVisibilityAsync(PoisonQueueMessage message, TimeSpan visibilityTimeout, CancellationToken ct)
        => await queue.UpdateMessageAsync(message.Id, message.PopReceipt, visibilityTimeout: visibilityTimeout, cancellationToken: ct)
            .ConfigureAwait(false);
}
