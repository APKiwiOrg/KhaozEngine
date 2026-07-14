using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Persistent <see cref="IBanStore"/> layered over an <see cref="IWorldStore"/> keyspace (keys
/// <c>ban:{accountId}</c>, value a forward-tolerant JSON record). Backend-agnostic: works over any IWorldStore
/// (Sqlite, SqlServer, in-memory). Keeps an in-memory cache so <see cref="IsBanned"/> stays synchronous for the
/// host-thread connect check, and writes through to the store on every mutate. Call <see cref="LoadAsync"/> once at
/// startup to hydrate the cache from the store (requires the store to implement <see cref="IEnumerableWorldStore"/>;
/// without it, persisted bans are invisible to <see cref="IsBanned"/> until re-added this session).
/// </summary>
public sealed class WorldStoreBanStore : IBanStore
{
    private const string KeyPrefix = "ban:";
    private readonly IWorldStore store;
    private readonly Func<DateTimeOffset> clock;
    private readonly ConcurrentDictionary<string, BanRecord> cache = new();

    public WorldStoreBanStore(IWorldStore store, Func<DateTimeOffset>? clock = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Hydrates the in-memory cache from the store. No-op if the store cannot enumerate.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (store is not IEnumerableWorldStore en) return;
        await foreach (WorldStoreEntry entry in en.EnumerateAsync(KeyPrefix, cancellationToken).ConfigureAwait(false))
        {
            byte[]? data = await store.LoadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (data is null) continue;
            BanRecord r = Decode(data);
            if (!string.IsNullOrEmpty(r.AccountId)) cache[r.AccountId] = r;
        }
    }

    public bool IsBanned(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return false;
        if (!cache.TryGetValue(accountId, out BanRecord r)) return false;
        if (r.Until is { } until && until <= clock()) { cache.TryRemove(accountId, out _); return false; }
        return true;
    }

    public async ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        var record = new BanRecord(accountId, reason ?? string.Empty, until);
        cache[accountId] = record;
        await store.SaveAsync(KeyPrefix + accountId, Encode(record), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        cache.TryRemove(accountId, out _);
        await store.DeleteAsync(KeyPrefix + accountId, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyCollection<BanRecord> ListBans()
    {
        DateTimeOffset now = clock();
        var live = new List<BanRecord>();
        foreach (BanRecord r in cache.Values)
        {
            if (r.Until is { } until && until <= now) { cache.TryRemove(r.AccountId, out _); continue; }
            live.Add(r);
        }
        return live;
    }

    // A settable DTO (not the record struct) keeps System.Text.Json round-tripping simple and forward-tolerant.
    // Internal (not private) so the source-generated NetWorldJsonContext can reference it, keeping ban encode/decode
    // reflection-free / NativeAOT-safe.
    internal sealed class BanDto
    {
        public string AccountId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTimeOffset? Until { get; set; }
    }

    private static byte[] Encode(in BanRecord r) =>
        JsonSerializer.SerializeToUtf8Bytes(new BanDto { AccountId = r.AccountId, Reason = r.Reason, Until = r.Until }, NetWorldJsonContext.Default.BanDto);

    private static BanRecord Decode(byte[] data)
    {
        BanDto? d = JsonSerializer.Deserialize(data, NetWorldJsonContext.Default.BanDto);
        return d is null ? default : new BanRecord(d.AccountId, d.Reason, d.Until);
    }
}
