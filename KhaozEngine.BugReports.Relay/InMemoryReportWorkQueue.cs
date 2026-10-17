using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>A first-in first-out <see cref="IReportWorkQueue"/> held in memory, for tests and local runs.</summary>
public sealed class InMemoryReportWorkQueue : IReportWorkQueue
{
    readonly object gate = new();
    readonly Queue<ReportWorkItem> items = new();

    /// <summary>Items waiting in the queue.</summary>
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
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            items.Enqueue(item);
        return Task.CompletedTask;
    }

    /// <summary>Takes the oldest item, or answers false when the queue is empty.</summary>
    public bool TryDequeue([NotNullWhen(true)] out ReportWorkItem? item)
    {
        lock (gate)
            return items.TryDequeue(out item);
    }
}
