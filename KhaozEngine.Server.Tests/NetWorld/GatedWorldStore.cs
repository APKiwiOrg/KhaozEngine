using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// An <see cref="IWorldStore"/> that holds every load open until the test explicitly releases it, wrapping a real
/// inner store. Reproduces a genuinely-async backend (Ruinborne's SqlServerWorldStore) so a test can drive the exact
/// window a synchronous InMemoryWorldStore cannot: a periodic save or a leave that fires while a load-on-join is
/// still in flight. Saves pass straight through and are recorded, so a clobbering write is observable.
/// </summary>
internal sealed class GatedWorldStore : IWorldStore
{
    private readonly IWorldStore inner;
    private readonly List<TaskCompletionSource> loadGates = new();
    private readonly List<string> savedKeys = new();
    private int completedLoads;

    public GatedWorldStore(IWorldStore inner) => this.inner = inner;

    /// <summary>The wrapped store, for un-gated reads/writes in a test (e.g. pre-seeding or asserting stored bytes).</summary>
    public IWorldStore Inner => inner;

    /// <summary>Keys written through this wrapper. A pre-seed done straight on <see cref="Inner"/> is not counted.</summary>
    public IReadOnlyList<string> SavedKeys { get { lock (savedKeys) return savedKeys.ToArray(); } }

    /// <summary>How many loads are currently parked, waiting for <see cref="ReleaseLoads"/>.</summary>
    public int PendingLoads { get { lock (loadGates) return loadGates.Count; } }

    /// <summary>How many loads have returned from the inner store since the rig was built. A released load's
    /// continuation in the layer above runs a hop BEHIND this, so it is a "the read itself is done" marker to wait
    /// on, never a "the layer has finished reacting to it" one. The rows that need the latter wait on an apply or a
    /// drop instead, which a load returning null never produces.</summary>
    public int CompletedLoads => Volatile.Read(ref completedLoads);

    /// <summary>Completes exactly ONE parked load, the oldest still waiting (<paramref name="oldest"/> true) or the
    /// newest, and reports whether there was one. This is how a row drives the ORDER two loads outstanding under one
    /// account land in: <see cref="ReleaseLoads"/> completes them all at once and leaves the ordering of their
    /// continuations to the thread pool, which is fine only when a single load is parked.</summary>
    public bool ReleaseOneLoad(bool oldest = true)
    {
        TaskCompletionSource gate;
        lock (loadGates)
        {
            if (loadGates.Count == 0) return false;
            int i = oldest ? 0 : loadGates.Count - 1;
            gate = loadGates[i];
            loadGates.RemoveAt(i);
        }
        gate.SetResult();
        return true;
    }

    /// <summary>Completes every currently-parked load so its continuation can run.</summary>
    public void ReleaseLoads()
    {
        TaskCompletionSource[] gates;
        lock (loadGates) { gates = loadGates.ToArray(); loadGates.Clear(); }
        foreach (TaskCompletionSource g in gates) g.SetResult();
    }

    public async Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (loadGates) loadGates.Add(gate);
        await gate.Task.ConfigureAwait(false);
        byte[]? data = await inner.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref completedLoads);
        return data;
    }

    public Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        lock (savedKeys) savedKeys.Add(key);
        return inner.SaveAsync(key, data, cancellationToken);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => inner.ExistsAsync(key, cancellationToken);
}
