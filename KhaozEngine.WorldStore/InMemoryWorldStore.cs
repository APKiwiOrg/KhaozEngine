using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

/// <summary>
/// Thread-safe, dependency-free in-memory <see cref="IWorldStore"/> for tests and local dev. Defensively
/// copies on save and load so a caller mutating its array can't corrupt stored state (mirroring how a real
/// DB-backed store hands out independent rows). Not durable across process restarts.
/// </summary>
public sealed class InMemoryWorldStore : IWorldStore
{
    private readonly ConcurrentDictionary<string, byte[]> store = new();

    public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[]? copy = store.TryGetValue(key, out byte[]? data) ? (byte[])data.Clone() : null;
        return Task.FromResult(copy);
    }

    public Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        store[key] = (byte[])data.Clone();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Task.FromResult(store.TryRemove(key, out _));
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Task.FromResult(store.ContainsKey(key));
    }
}
