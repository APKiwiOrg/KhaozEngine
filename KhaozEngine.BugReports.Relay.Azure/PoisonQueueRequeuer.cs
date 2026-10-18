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
    static readonly TimeSpan RunDeadline = TimeSpan.FromHours(1);
    static readonly TimeSpan ReceiveVisibility = TimeSpan.FromHours(2);
    static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(30);
    readonly IPoisonQueue poison;
    readonly IReportQueueSender work;
    readonly int maxRequeues;
    readonly ILogger log;
    readonly TimeProvider time;

    /// <summary>
    /// Wraps existing queues. Hosts must build both the work and poison QueueClient instances with
    /// <see cref="ReportQueueClientOptions.Create"/>, matching the Azure Functions trigger's Base64 default.
    /// The requeuer owns receives on the poison client. Receives made outside it are not counted.
    /// A null clock uses <see cref="TimeProvider.System"/>. A null logger uses <c>Log.Get("BugReports.Relay")</c>.
    /// </summary>
    public PoisonQueueRequeuer(QueueClient poison, QueueClient work, int maxRequeues = 3, TimeProvider? time = null,
        ILogger? logger = null)
        : this(new PoisonQueueAdapter(poison), new QueueSenderAdapter(work), maxRequeues, logger, time) { }

    internal PoisonQueueRequeuer(IPoisonQueue poison, IReportQueueSender work, int maxRequeues = 3, ILogger? log = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(poison);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRequeues);
        this.poison = poison;
        this.work = work;
        this.maxRequeues = maxRequeues;
        this.log = log ?? Log.Get("BugReports.Relay");
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Moves eligible items, in batches of 32, until no visible messages remain and returns the moved count.
    /// Runs end after one hour, returning progress, inside a two-hour receive visibility lease. Only caller
    /// cancellation throws <see cref="OperationCanceledException"/>. Retained and unprocessed
    /// messages are made visible on exit. A successful send followed by a failed delete can deliver twice.
    /// </summary>
    public async Task<int> RequeueAsync(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(RunDeadline, time);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var held = new Dictionary<string, PoisonQueueMessage>(StringComparer.Ordinal);
        bool failed = false;
        bool deadlineReached = false;
        int moved = 0;
        try
        {
            while (true)
            {
                run.Token.ThrowIfCancellationRequested();
                PoisonQueueBatch batch = await poison.ReceiveAsync(32, ReceiveVisibility, run.Token).ConfigureAwait(false);
                if (batch.Messages.Count == 0 && batch.UndecodableCount == 0)
                    return moved;
                // Track the entire batch before processing, so a failed send releases its untouched tail too.
                foreach (PoisonQueueMessage message in batch.Messages)
                    held.Add(message.Id, message);
                foreach (PoisonQueueMessage message in batch.Messages)
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
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            deadlineReached = true;
            return moved;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            // The caller's cancellation must not prevent cleanup or hide the original failure.
            using var cleanup = new CancellationTokenSource(CleanupDeadline, time);
            Exception? releaseFailure = null;
            foreach (PoisonQueueMessage message in held.Values)
            {
                try { await poison.SetVisibilityAsync(message, TimeSpan.Zero, cleanup.Token).ConfigureAwait(false); }
                catch (Exception failure)
                {
                    if (failure is not OperationCanceledException || cancellationToken.IsCancellationRequested)
                        releaseFailure ??= failure;
                    log.Warn($"Poison message {message.Id} could not be made visible.");
                }
            }
            if (!failed && !deadlineReached && releaseFailure is not null)
                ExceptionDispatchInfo.Capture(releaseFailure).Throw();
        }
    }
}
