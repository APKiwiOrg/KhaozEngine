using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay.Azure;

internal sealed record PoisonQueueMessage(string Id, string PopReceipt, string Text);

internal interface IPoisonQueue
{
    Task<IReadOnlyList<PoisonQueueMessage>> ReceiveAsync(int maxMessages, TimeSpan visibilityTimeout, CancellationToken ct);
    Task DeleteAsync(PoisonQueueMessage message, CancellationToken ct);
    Task SetVisibilityAsync(PoisonQueueMessage message, TimeSpan visibilityTimeout, CancellationToken ct);
}
