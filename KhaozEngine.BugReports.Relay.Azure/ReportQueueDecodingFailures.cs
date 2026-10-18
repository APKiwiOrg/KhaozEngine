using System.Runtime.CompilerServices;
using System.Threading;
using Azure.Storage.Queues;

namespace KhaozEngine.BugReports.Relay.Azure;

/// <summary>Tracks omitted receive messages by client without extending a client's lifetime.</summary>
internal static class ReportQueueDecodingFailures
{
    static readonly ConditionalWeakTable<QueueClient, Counter> counts = new();

    internal static void Record(QueueClient queue)
        => Interlocked.Increment(ref counts.GetValue(queue, static _ => new Counter()).Value);

    internal static int Take(QueueClient queue)
        => counts.TryGetValue(queue, out Counter? counter) ? Interlocked.Exchange(ref counter.Value, 0) : 0;

    sealed class Counter
    {
        public int Value;
    }
}
