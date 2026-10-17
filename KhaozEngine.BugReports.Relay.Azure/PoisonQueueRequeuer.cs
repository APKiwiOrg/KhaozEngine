using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay.Azure;

/// <summary>Drains visible poison messages back to work, retaining malformed or exhausted items.</summary>
public sealed class PoisonQueueRequeuer
{
    readonly IPoisonQueue poison;
    readonly IReportQueueSender work;
    readonly int maxRequeues;
    readonly ILogger log;

    /// <summary>
    /// Wraps existing queues. Hosts must build both the work and poison QueueClient instances with
    /// <see cref="ReportQueueClientOptions.Create"/>, matching the Azure Functions trigger's Base64 default.
    /// </summary>
    public PoisonQueueRequeuer(QueueClient poison, QueueClient work, int maxRequeues = 3)
        : this(new PoisonQueueAdapter(poison), new QueueSenderAdapter(work), maxRequeues) { }

    internal PoisonQueueRequeuer(IPoisonQueue poison, IReportQueueSender work, int maxRequeues = 3, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(poison);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRequeues);
        this.poison = poison;
        this.work = work;
        this.maxRequeues = maxRequeues;
        this.log = log ?? Log.For<PoisonQueueRequeuer>();
    }

    /// <summary>
    /// Moves eligible items, in batches of 32, until no visible messages remain and returns the moved count.
    /// Runs cancel after one hour, inside a two-hour receive visibility lease. Retained and unprocessed
    /// messages are made visible on exit. A successful send followed by a failed delete can deliver twice.
    /// </summary>
    public async Task<int> RequeueAsync(CancellationToken cancellationToken)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.CancelAfter(TimeSpan.FromHours(1));
        var held = new Dictionary<string, PoisonQueueMessage>(StringComparer.Ordinal);
        bool failed = false;
        int moved = 0;
        try
        {
            while (true)
            {
                run.Token.ThrowIfCancellationRequested();
                IReadOnlyList<PoisonQueueMessage> batch = await poison.ReceiveAsync(32, TimeSpan.FromHours(2), run.Token).ConfigureAwait(false);
                if (batch.Count == 0)
                    return moved;
                // Track the entire batch before processing, so a failed send releases its untouched tail too.
                foreach (PoisonQueueMessage message in batch)
                    held.Add(message.Id, message);
                foreach (PoisonQueueMessage message in batch)
                {
                    run.Token.ThrowIfCancellationRequested();
                    ReportWorkItem item;
                    try { item = ReportWorkItemJson.Parse(message.Text); }
                    catch (FormatException)
                    {
                        log.Warn($"Poison message {message.Id} is malformed.");
                        continue;
                    }
                    if (item.Requeues >= maxRequeues)
                    {
                        log.Warn($"Poison message {message.Id} reached its requeue cap.");
                        continue;
                    }
                    await work.SendAsync(ReportWorkItemJson.Serialize(item with { Requeues = item.Requeues + 1 }), run.Token).ConfigureAwait(false);
                    await poison.DeleteAsync(message, run.Token).ConfigureAwait(false);
                    held.Remove(message.Id);
                    moved++;
                }
            }
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            // The caller's cancellation must not prevent cleanup or hide the original failure.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Exception? releaseFailure = null;
            foreach (PoisonQueueMessage message in held.Values)
            {
                try { await poison.SetVisibilityAsync(message, TimeSpan.Zero, cleanup.Token).ConfigureAwait(false); }
                catch (Exception failure)
                {
                    releaseFailure ??= failure;
                    log.Warn($"Poison message {message.Id} could not be made visible.");
                }
            }
            if (!failed && releaseFailure is not null)
                ExceptionDispatchInfo.Capture(releaseFailure).Throw();
        }
    }
}
