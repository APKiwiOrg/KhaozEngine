using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Commerce;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

public class PeriodicGrantTests
{
    private static readonly AccountId A = new("acct:1");
    private static readonly CurrencyId Shard = new("shard");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (PeriodicGrant grant, Wallet wallet) Build()
    {
        InMemoryWalletStore store = new();
        Wallet wallet = new(store, new InMemoryProductCatalog(Array.Empty<ProductDefinition>()));
        PeriodicGrant grant = new(wallet, store, TimeSpan.FromHours(24), "dailyShard", Shard, 1);
        return (grant, wallet);
    }

    [Fact]
    public async Task First_claim_available_immediately_then_waits_a_day()
    {
        (PeriodicGrant grant, Wallet wallet) = Build();
        PeriodicGrantResult first = await grant.TryClaimAsync(A, T0);
        Assert.True(first.Granted);
        Assert.Equal(1, await wallet.BalanceAsync(A, Shard));

        PeriodicGrantResult tooSoon = await grant.TryClaimAsync(A, T0.AddHours(5));
        Assert.False(tooSoon.Granted);
        Assert.Equal(1, await wallet.BalanceAsync(A, Shard));
    }

    [Fact]
    public async Task Long_absence_yields_a_single_grant_non_stacking()
    {
        (PeriodicGrant grant, Wallet wallet) = Build();
        await grant.TryClaimAsync(A, T0);
        PeriodicGrantResult afterWeek = await grant.TryClaimAsync(A, T0.AddDays(7));
        Assert.True(afterWeek.Granted);
        Assert.Equal(2, await wallet.BalanceAsync(A, Shard)); // one, not seven
    }

    [Fact]
    public async Task Claim_after_schedule_advanced_does_not_grant_again()
    {
        (PeriodicGrant grant, Wallet wallet) = Build();
        await grant.TryClaimAsync(A, T0);
        await grant.TryClaimAsync(A, T0.AddDays(1)); // available again
        await grant.TryClaimAsync(A, T0.AddDays(1)); // schedule already advanced past this instant
        Assert.Equal(2, await wallet.BalanceAsync(A, Shard));
    }

    [Fact]
    public async Task Reclaim_of_same_scheduled_instant_credits_once_and_reports_not_granted()
    {
        InMemoryWalletStore store = new();
        Wallet wallet = new(store, new InMemoryProductCatalog(Array.Empty<ProductDefinition>()));
        PeriodicGrant grant = new(wallet, store, TimeSpan.FromHours(24), "dailyShard", Shard, 1);

        // Seed a persisted schedule so this exercises the steady-state path, not the bootstrap claim (whose
        // key is a fixed per-(account, reward) sentinel, not the instant's ticks). Here the claim reads a
        // stored instant and keys on its ticks.
        await store.SetNextAvailableAsync(A, "dailyShard", T0);

        PeriodicGrantResult first = await grant.TryClaimAsync(A, T0);
        Assert.True(first.Granted);
        Assert.Equal(1, await wallet.BalanceAsync(A, Shard));

        // Simulate a second claim that read the SAME pre-advance schedule instant:
        // rewind the schedule store to T0 so the next claim recomputes the identical
        // idempotency key ({rewardId}:{account}:{T0.UtcTicks}).
        await store.SetNextAvailableAsync(A, "dailyShard", T0);

        PeriodicGrantResult replay = await grant.TryClaimAsync(A, T0);
        Assert.False(replay.Granted);                       // wallet detected the duplicate key
        Assert.Equal(1, await wallet.BalanceAsync(A, Shard)); // credited once, not twice
    }

    [Fact]
    public async Task First_claim_credits_once_when_the_schedule_write_is_not_visible_to_the_second_read()
    {
        // The bootstrap race, modelled with no threads: a schedule store whose write is never visible to a
        // second read (it returns null both times), so both first claims take the bootstrap path. Two claims
        // a tick apart must still credit once. The wallet idempotency key is the guard, so the bootstrap key
        // cannot depend on the caller's serverNowUtc, or two concurrent first claims derive two keys and both
        // credit (real currency duplication).
        AlwaysNullScheduleStore schedules = new();
        InMemoryWalletStore store = new();
        Wallet wallet = new(store, new InMemoryProductCatalog(Array.Empty<ProductDefinition>()));
        PeriodicGrant grant = new(wallet, schedules, TimeSpan.FromHours(24), "dailyShard", Shard, 1);

        PeriodicGrantResult a = await grant.TryClaimAsync(A, T0);
        PeriodicGrantResult b = await grant.TryClaimAsync(A, T0.AddTicks(1));

        Assert.Equal(1, await wallet.BalanceAsync(A, Shard)); // one grant, not two
        Assert.True(a.Granted);
        Assert.False(b.Granted);                             // the duplicate first claim is a replay, not a grant
    }

    [Fact]
    public async Task Concurrent_first_claims_credit_exactly_once()
    {
        // Drive the real concurrency window deterministically, many iterations to guard both directions:
        // two first-ever claims a tick apart both park at the schedule read and resolve to the same
        // pre-write snapshot (null), so neither's write is visible to the other's read, then they race for
        // the store lock on the credit. Exactly one grant must land.
        for (int i = 0; i < 20; i++)
        {
            InMemoryWalletStore store = new();
            Wallet wallet = new(store, new InMemoryProductCatalog(Array.Empty<ProductDefinition>()));
            GatedGrantScheduleStore gated = new(store);
            PeriodicGrant grant = new(wallet, gated, TimeSpan.FromHours(24), "dailyShard", Shard, 1);

            Task<PeriodicGrantResult> t1 = grant.TryClaimAsync(A, T0);
            Task<PeriodicGrantResult> t2 = grant.TryClaimAsync(A, T0.AddTicks(1));

            Assert.Equal(2, gated.PendingReads);             // both parked at the schedule read, before any write
            await gated.ReleaseReadsAsync(A, "dailyShard");  // both observe the same null snapshot -> both bootstrap

            PeriodicGrantResult[] results = await Task.WhenAll(t1, t2);

            Assert.Equal(1, await wallet.BalanceAsync(A, Shard)); // exactly one grant credited
            Assert.Equal(1, results.Count(r => r.Granted));       // exactly one claim reports Granted
        }
    }

    /// <summary>The idempotency key is three colon-separated segments, so the FIRST one has to be colon-free or the
    /// split is ambiguous and two different (rewardId, account) pairs can address one key. The account segment does
    /// not need the same rule: it sits between the reward and a suffix that is itself colon-free ("bootstrap" or a
    /// decimal tick count), so the first and last colons bound it however many it contains. Rejecting here rather
    /// than escaping is what keeps the encoding byte-identical for every key already in a wallet ledger, and the
    /// fleet's account ids ("acct:1") are full of colons.</summary>
    [Theory]
    [InlineData("daily:extra")]
    [InlineData(":daily")]
    [InlineData("daily:")]
    public void Reward_id_carrying_the_key_separator_is_rejected(string rewardId)
    {
        InMemoryWalletStore store = new();
        Wallet wallet = new(store, new InMemoryProductCatalog(Array.Empty<ProductDefinition>()));
        Assert.Throws<ArgumentException>(
            () => new PeriodicGrant(wallet, store, TimeSpan.FromHours(24), rewardId, Shard, 1));
    }

    /// <summary>The other half of that choice: a colon-bearing ACCOUNT id keeps producing the exact key it always
    /// did, so an upgraded server still reads its own ledger. An escaping encoding would have rewritten every one of
    /// these and handed every account one more bootstrap grant on the changeover.</summary>
    [Fact]
    public async Task A_colon_bearing_account_id_keys_exactly_as_it_did_before()
    {
        InMemoryWalletStore store = new();
        Wallet wallet = new(store, new InMemoryProductCatalog(Array.Empty<ProductDefinition>()));
        PeriodicGrant grant = new(wallet, store, TimeSpan.FromHours(24), "dailyShard", Shard, 1);

        Assert.True((await grant.TryClaimAsync(A, T0)).Granted);

        LedgerEntry row = Assert.Single(await store.GetLedgerAsync(A, Shard, 10));
        Assert.Equal("dailyShard:acct:1:bootstrap", row.IdempotencyKey);
    }

    /// <summary>The hazard #208 names, and the reset that answers it, in one test. A wipe that DELETES the schedule
    /// row while the wallet ledger survives sends the next claim down the bootstrap path, where it replays the
    /// retained sentinel and is denied with no error. <see cref="PeriodicGrant.ResetAsync"/> writes a row instead,
    /// so the bootstrap path stays unreachable and the claim keys on the written instant.</summary>
    [Fact]
    public async Task A_deleted_schedule_row_denies_the_regrant_and_a_reset_restores_it()
    {
        InMemoryWalletStore store = new();
        ClearableScheduleStore schedules = new(store);
        Wallet wallet = new(store, new InMemoryProductCatalog(Array.Empty<ProductDefinition>()));
        PeriodicGrant grant = new(wallet, schedules, TimeSpan.FromHours(24), "dailyShard", Shard, 1);

        Assert.True((await grant.TryClaimAsync(A, T0)).Granted);

        // The wipe: schedule row gone, wallet ledger retained.
        schedules.Clear(A, "dailyShard");
        PeriodicGrantResult denied = await grant.TryClaimAsync(A, T0.AddDays(3));
        Assert.False(denied.Granted);                        // the silent denial
        Assert.Equal(1, await wallet.BalanceAsync(A, Shard));

        await grant.ResetAsync(A, T0.AddDays(3));
        PeriodicGrantResult regranted = await grant.TryClaimAsync(A, T0.AddDays(3));
        Assert.True(regranted.Granted);
        Assert.Equal(2, await wallet.BalanceAsync(A, Shard));
    }

    /// <summary>An <see cref="IGrantScheduleStore"/> that can DELETE a row, which no shipped backend exposes and
    /// which is exactly the consumer-side wipe #208 is about.</summary>
    private sealed class ClearableScheduleStore : IGrantScheduleStore
    {
        private readonly Dictionary<(string, string), DateTimeOffset> rows = new();
        private readonly IGrantScheduleStore inner;

        public ClearableScheduleStore(IGrantScheduleStore inner) => this.inner = inner;

        public void Clear(AccountId account, string rewardId) => rows.Remove((account.Value, rewardId));

        public Task<DateTimeOffset?> GetNextAvailableAsync(AccountId account, string rewardId, CancellationToken ct = default)
            => Task.FromResult(rows.TryGetValue((account.Value, rewardId), out DateTimeOffset v) ? v : (DateTimeOffset?)null);

        public Task SetNextAvailableAsync(AccountId account, string rewardId, DateTimeOffset nextUtc, CancellationToken ct = default)
        {
            rows[(account.Value, rewardId)] = nextUtc;
            return inner.SetNextAvailableAsync(account, rewardId, nextUtc, ct);
        }
    }

    /// <summary>A schedule store whose write is never visible to a later read: models the bootstrap window
    /// where a concurrent first claim's <c>SetNextAvailableAsync</c> has not landed yet, so both reads see null.</summary>
    private sealed class AlwaysNullScheduleStore : IGrantScheduleStore
    {
        public Task<DateTimeOffset?> GetNextAvailableAsync(AccountId account, string rewardId, CancellationToken ct = default)
            => Task.FromResult<DateTimeOffset?>(null);

        public Task SetNextAvailableAsync(AccountId account, string rewardId, DateTimeOffset nextUtc, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>Wraps an <see cref="IGrantScheduleStore"/> and parks every <see cref="GetNextAvailableAsync"/>
    /// read until the test releases them together, all resolving to a single inner snapshot taken at release.
    /// That pins the bootstrap race deterministically: two concurrent first claims observe the same pre-write
    /// state, so the faster claim's write cannot change what the slower claim's read returns. Writes pass
    /// straight through (last-write-wins). Same TaskCompletionSource idiom as GatedWorldStore.</summary>
    private sealed class GatedGrantScheduleStore : IGrantScheduleStore
    {
        private readonly IGrantScheduleStore inner;
        private readonly List<TaskCompletionSource<DateTimeOffset?>> readGates = new();

        public GatedGrantScheduleStore(IGrantScheduleStore inner) => this.inner = inner;

        /// <summary>How many reads are currently parked, waiting for <see cref="ReleaseReadsAsync"/>.</summary>
        public int PendingReads { get { lock (readGates) return readGates.Count; } }

        /// <summary>Snapshots the inner value once and completes every parked read with that same snapshot.</summary>
        public async Task ReleaseReadsAsync(AccountId account, string rewardId, CancellationToken ct = default)
        {
            DateTimeOffset? snapshot = await inner.GetNextAvailableAsync(account, rewardId, ct).ConfigureAwait(false);
            TaskCompletionSource<DateTimeOffset?>[] gates;
            lock (readGates) { gates = readGates.ToArray(); readGates.Clear(); }
            foreach (TaskCompletionSource<DateTimeOffset?> g in gates) g.SetResult(snapshot);
        }

        public Task<DateTimeOffset?> GetNextAvailableAsync(AccountId account, string rewardId, CancellationToken ct = default)
        {
            TaskCompletionSource<DateTimeOffset?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (readGates) readGates.Add(gate);
            return gate.Task;
        }

        public Task SetNextAvailableAsync(AccountId account, string rewardId, DateTimeOffset nextUtc, CancellationToken ct = default)
            => inner.SetNextAvailableAsync(account, rewardId, nextUtc, ct);
    }
}
