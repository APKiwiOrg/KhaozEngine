using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Commerce;

/// <summary>In-process transactional wallet store: dependency-free reference + test backend. A single
/// lock makes credit/debit atomic so semantics match the SQL backends.</summary>
public sealed class InMemoryWalletStore : IWalletStore, IGrantScheduleStore
{
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<(string, string), long> balances = new();
    private readonly Dictionary<(string account, string currency, string key), LedgerEntry> byKey = new();
    private readonly List<LedgerEntry> ledger = new();
    private readonly Dictionary<long, long> balanceAfter = new();
    private readonly Dictionary<(string, string), DateTimeOffset> schedules = new();
    private long nextId = 1;

    public InMemoryWalletStore(Func<DateTimeOffset>? clock = null)
        => this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public Task<CreditResult> CreditAsync(AccountId account, CurrencyId currency, long amount,
        string idempotencyKey, LedgerReason reason, string? sourceRef, CancellationToken ct = default)
    {
        Require(amount, idempotencyKey);
        lock (gate)
        {
            if (byKey.TryGetValue((account.Value, currency.Value, idempotencyKey), out LedgerEntry prior))
                return Task.FromResult(new CreditResult(false, true, BalanceAfter(prior)));
            long bal = Bal(account, currency) + amount;
            Set(account, currency, bal);
            Append(account, currency, amount, idempotencyKey, reason, sourceRef, bal);
            return Task.FromResult(new CreditResult(true, false, bal));
        }
    }

    public Task<DebitResult> DebitAsync(AccountId account, CurrencyId currency, long amount,
        string idempotencyKey, LedgerReason reason, string? sourceRef, CancellationToken ct = default)
    {
        Require(amount, idempotencyKey);
        lock (gate)
        {
            if (byKey.TryGetValue((account.Value, currency.Value, idempotencyKey), out LedgerEntry prior))
                return Task.FromResult(new DebitResult(false, true, false, BalanceAfter(prior)));
            long bal = Bal(account, currency);
            if (bal < amount)
                return Task.FromResult(new DebitResult(false, false, true, bal));
            bal -= amount;
            Set(account, currency, bal);
            Append(account, currency, -amount, idempotencyKey, reason, sourceRef, bal);
            return Task.FromResult(new DebitResult(true, false, false, bal));
        }
    }

    public Task<long> GetBalanceAsync(AccountId account, CurrencyId currency, CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult(Bal(account, currency));
    }

    public Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(AccountId account, CurrencyId currency,
        int limit, CancellationToken ct = default)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
        {
            IReadOnlyList<LedgerEntry> rows = ledger
                .Where(e => e.Account.Equals(account) && e.Currency.Equals(currency))
                .OrderByDescending(e => e.Id).Take(limit).ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<DateTimeOffset?> GetNextAvailableAsync(AccountId account, string rewardId, CancellationToken ct = default)
    {
        lock (gate)
            return Task.FromResult(schedules.TryGetValue((account.Value, rewardId), out DateTimeOffset v) ? v : (DateTimeOffset?)null);
    }

    public Task SetNextAvailableAsync(AccountId account, string rewardId, DateTimeOffset nextUtc, CancellationToken ct = default)
    {
        lock (gate) { schedules[(account.Value, rewardId)] = nextUtc; return Task.CompletedTask; }
    }

    private static void Require(long amount, string key)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Idempotency key required.", nameof(key));
    }

    private long Bal(AccountId a, CurrencyId c) => balances.TryGetValue((a.Value, c.Value), out long v) ? v : 0;
    private void Set(AccountId a, CurrencyId c, long v) => balances[(a.Value, c.Value)] = v;

    // The running balance stored on the row equals the post-op balance, so a replay returns it directly.
    private long BalanceAfter(LedgerEntry e) => balanceAfter[e.Id];

    private void Append(AccountId a, CurrencyId c, long delta, string key,
        LedgerReason reason, string? src, long postBalance)
    {
        LedgerEntry e = new(nextId++, a, c, delta, key, reason, src, clock());
        ledger.Add(e);
        byKey[(a.Value, c.Value, key)] = e;
        balanceAfter[e.Id] = postBalance;
    }
}
