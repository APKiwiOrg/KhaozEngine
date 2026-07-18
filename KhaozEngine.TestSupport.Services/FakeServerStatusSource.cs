using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.ServerStatus;

namespace KhaozEngine.Tests.ServerStatus;

/// <summary>
/// In-memory <see cref="IServerStatusSource"/> for headless poller tests: serves a scripted sequence of
/// results (a report on success, null on a miss), repeating the last scripted result once the queue drains,
/// and counts fetches. No sockets, no real HTTP.
/// </summary>
public sealed class FakeServerStatusSource : IServerStatusSource
{
    private readonly Queue<ServerStatusReport?> results = new();
    private ServerStatusReport? lastResult;

    public int FetchCount { get; private set; }

    /// <summary>Enqueues one result to return from the next fetch (null = a transport miss).</summary>
    public void Enqueue(ServerStatusReport? result) => results.Enqueue(result);

    public Task<ServerStatusReport?> FetchAsync(CancellationToken cancellationToken = default)
    {
        FetchCount++;
        if (results.Count > 0)
        {
            lastResult = results.Dequeue();
        }
        return Task.FromResult(lastResult);
    }
}
