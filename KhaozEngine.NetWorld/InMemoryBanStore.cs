using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.NetWorld;

/// <summary>Dependency-free in-memory <see cref="IBanStore"/>. Expiry is checked against an injectable clock
/// (default <see cref="DateTimeOffset.UtcNow"/>); expired entries are pruned lazily on read.</summary>
public sealed class InMemoryBanStore : IBanStore
{
    private readonly ConcurrentDictionary<string, BanRecord> bans = new();
    private readonly Func<DateTimeOffset> clock;

    public InMemoryBanStore(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public bool IsBanned(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return false;
        if (!bans.TryGetValue(accountId, out BanRecord r)) return false;
        if (r.Until is { } until && until <= clock()) { bans.TryRemove(accountId, out _); return false; }
        return true;
    }

    public ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        bans[accountId] = new BanRecord(accountId, reason ?? string.Empty, until);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        bans.TryRemove(accountId, out _);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyCollection<BanRecord> ListBans()
    {
        DateTimeOffset now = clock();
        var live = new List<BanRecord>();
        foreach (BanRecord r in bans.Values)
        {
            if (r.Until is { } until && until <= now) { bans.TryRemove(r.AccountId, out _); continue; }
            live.Add(r);
        }
        return live;
    }
}
