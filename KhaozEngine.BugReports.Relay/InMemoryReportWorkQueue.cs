using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>An <see cref="IReportWorkQueue"/> held in memory, taking visible items in enqueue order.</summary>
public sealed class InMemoryReportWorkQueue : IReportWorkQueue
{
    readonly object gate = new();
    readonly LinkedList<(ReportWorkItem Item, DateTimeOffset VisibleAt)> items = new();
    readonly TimeProvider time;

    /// <summary>Creates a queue using the system clock.</summary>
    public InMemoryReportWorkQueue() : this(TimeProvider.System) { }

    /// <summary>Creates a queue using <paramref name="time"/> for delayed item visibility.</summary>
    public InMemoryReportWorkQueue(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        this.time = time;
    }

    /// <summary>All items waiting in the queue, including those still hidden.</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return items.Count;
        }
    }

    /// <inheritdoc />
    public Task EnqueueAsync(ReportWorkItem item, CancellationToken cancellationToken)
        => EnqueueCoreAsync(item, TimeSpan.Zero, cancellationToken);

    /// <inheritdoc />
    public Task EnqueueAsync(ReportWorkItem item, TimeSpan visibleAfter, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(visibleAfter, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(visibleAfter, TimeSpan.FromDays(7));
        return EnqueueCoreAsync(item, visibleAfter, cancellationToken);
    }

    Task EnqueueCoreAsync(ReportWorkItem item, TimeSpan visibleAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            items.AddLast((item, time.GetUtcNow() + visibleAfter));
        return Task.CompletedTask;
    }

    /// <summary>Takes the oldest visible item, or answers false when none is visible.</summary>
    public bool TryDequeue([NotNullWhen(true)] out ReportWorkItem? item)
    {
        lock (gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            for (var node = items.First; node is not null; node = node.Next)
            {
                if (node.Value.VisibleAt > now) continue;
                item = node.Value.Item;
                items.Remove(node);
                return true;
            }
            item = null;
            return false;
        }
    }
}
