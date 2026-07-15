using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

/// <summary>
/// Thread-safe, dependency-free in-memory <see cref="IWorldStore"/> for tests and local dev. Defensively
/// copies on save and load so a caller mutating its array can't corrupt stored state. Tracks a per-key
/// last-write timestamp (from an injectable clock) so it can also satisfy <see cref="IEnumerableWorldStore"/>.
/// Not durable across process restarts.
/// </summary>
public sealed class InMemoryWorldStore : IWorldStore, IEnumerableWorldStore
{
    private readonly record struct Entry(byte[] Data, DateTimeOffset UpdatedAt);
    private readonly ConcurrentDictionary<string, Entry> store = new();
    private readonly Func<DateTimeOffset> clock;

    /// <summary>The default clock is <see cref="DateTimeOffset.UtcNow"/>; inject a fixed clock in tests.</summary>
    public InMemoryWorldStore(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[]? copy = store.TryGetValue(key, out Entry e) ? (byte[])e.Data.Clone() : null;
        return Task.FromResult(copy);
    }

    public Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        store[key] = new Entry((byte[])data.Clone(), clock());
        return Task.CompletedTask;
    }

    /// <summary>Overrides the interface default loop: writes every item under a single clock reading, so a batch
    /// gets one consistent <c>UpdatedAt</c> instead of one per item.</summary>
    public Task SaveManyAsync(IReadOnlyList<(string Key, byte[] Data)> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        DateTimeOffset now = clock();
        foreach ((string key, byte[] data) in items)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(data);
            store[key] = new Entry((byte[])data.Clone(), now);
        }
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

    public async IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(
        string? keyPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (KeyValuePair<string, Entry> kv in store)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(keyPrefix) && !kv.Key.StartsWith(keyPrefix, StringComparison.Ordinal)) continue;
            yield return new WorldStoreEntry(kv.Key, kv.Value.UpdatedAt, kv.Value.Data.Length);
        }
        await Task.CompletedTask;
    }
}
