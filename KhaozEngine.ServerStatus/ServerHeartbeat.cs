using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// One liveness heartbeat the game server writes to the status DB on a timer: the instant it was taken and
/// the server build version. The status endpoint derives <see cref="ServerHealth"/> from the age of the
/// newest heartbeat (stale beyond a threshold, with no deploy in progress, means Down). This is the whole
/// row shape the engine defines - the DB schema and the upsert are the game's (see the package README).
/// </summary>
public readonly record struct ServerHeartbeat(DateTimeOffset TimestampUtc, string ServerVersion);

/// <summary>
/// Seam the game server implements to persist a liveness heartbeat. The engine ships only this contract (no
/// SQL): the game already owns SQL access and the one-table upsert against its status DB, and keeping the
/// seam dependency-free lets clients reference this package for the poller without dragging a database
/// driver in. A durable implementation lives in the game (or the game-template infra recipe), not the engine.
/// </summary>
public interface IServerHeartbeatSink
{
    /// <summary>
    /// Persists <paramref name="heartbeat"/> (an upsert of the single liveness row keyed by, typically, the
    /// server/shard id). Implementations decide their own error policy: <see cref="ServerHeartbeatService"/>
    /// contains any thrown exception rather than letting it break the server loop.
    /// </summary>
    Task WriteAsync(ServerHeartbeat heartbeat, CancellationToken cancellationToken = default);
}

/// <summary>No-op sink for local/dev runs and headless tests that don't care about persistence.</summary>
public sealed class NullServerHeartbeatSink : IServerHeartbeatSink
{
    /// <summary>Shared instance.</summary>
    public static readonly NullServerHeartbeatSink Instance = new();

    /// <inheritdoc />
    public Task WriteAsync(ServerHeartbeat heartbeat, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// In-memory sink that records the most recent heartbeat and a write count. The reference/test backend for
/// the seam, and a stand-in a status endpoint could read in a single-process test.
/// </summary>
public sealed class InMemoryServerHeartbeatSink : IServerHeartbeatSink
{
    private readonly object gate = new();
    private ServerHeartbeat? last;
    private int writeCount;

    /// <summary>The most recently written heartbeat, or null before the first write.</summary>
    public ServerHeartbeat? Last
    {
        get { lock (gate) { return last; } }
    }

    /// <summary>Total heartbeats written.</summary>
    public int WriteCount
    {
        get { lock (gate) { return writeCount; } }
    }

    /// <inheritdoc />
    public Task WriteAsync(ServerHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            last = heartbeat;
            writeCount++;
        }
        return Task.CompletedTask;
    }
}
