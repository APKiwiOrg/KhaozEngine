using System;
using System.Threading.Tasks;
using KhaozEngine.Commerce;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

/// <summary>
/// Backend-independent behaviour of <see cref="PeriodicGrant.ResetAsync"/>, run against every shipped store the
/// same way <see cref="WalletStoreContract"/> runs the wallet rules. The reset rides the schedule store's own write
/// path (a dictionary write in memory, an upsert on SQLite, a MERGE on SQL Server) and the claim that follows keys
/// on the instant it reads BACK, so the round trip is per-backend behaviour and not a property of the grant alone.
/// SQLite truncates the instant to milliseconds, SQL Server keeps DATETIME2 ticks, and the claim must grant either
/// way. The SQL Server row lives in <see cref="SqlServerWalletStoreTests"/> as a <c>[SqlServerFact]</c>, because a
/// <c>[Fact]</c> here cannot conditionally skip.
/// </summary>
public abstract class PeriodicGrantResetContract
{
    /// <summary>One backend instance serving as both the wallet ledger and the grant schedule, which is what all
    /// three shipped stores are. Returned as the two seams so a consumer that splits them still fits.</summary>
    protected abstract (IWalletStore Ledger, IGrantScheduleStore Schedules) NewBackend();

    private static readonly CurrencyId Shard = new("shard");
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // A fresh account per test so a shared database does not carry state between runs.
    private static AccountId FreshAccount() => new($"acct:{Guid.NewGuid():N}");

    private (PeriodicGrant Grant, Wallet Wallet) Build()
    {
        (IWalletStore ledger, IGrantScheduleStore schedules) = NewBackend();
        Wallet wallet = new(ledger, new InMemoryProductCatalog(Array.Empty<ProductDefinition>()));
        return (new PeriodicGrant(wallet, schedules, TimeSpan.FromHours(24), "dailyShard", Shard, 1), wallet);
    }

    /// <summary>The wiped-progress case the reset exists for: the reward is re-opened mid-interval and the next
    /// claim grants, against a wallet ledger that still holds the first claim's bootstrap sentinel.</summary>
    [Fact]
    public async Task Reset_reopens_the_reward_against_a_retained_ledger()
    {
        (PeriodicGrant grant, Wallet wallet) = Build();
        AccountId account = FreshAccount();

        Assert.True((await grant.TryClaimAsync(account, T0)).Granted);
        Assert.False((await grant.TryClaimAsync(account, T0.AddHours(1))).Granted);  // still inside the interval

        await grant.ResetAsync(account, T0.AddHours(2));

        PeriodicGrantResult again = await grant.TryClaimAsync(account, T0.AddHours(2));
        Assert.True(again.Granted);
        Assert.Equal(2, await wallet.BalanceAsync(account, Shard));
    }

    /// <summary>A reset repeated at the same instant is still ONE re-grant: the claim keys on that instant's ticks,
    /// so the second claim is an idempotent replay. This is what a double-clicked admin reset must do.</summary>
    [Fact]
    public async Task Repeating_a_reset_at_one_instant_is_a_single_regrant()
    {
        (PeriodicGrant grant, Wallet wallet) = Build();
        AccountId account = FreshAccount();

        Assert.True((await grant.TryClaimAsync(account, T0)).Granted);

        await grant.ResetAsync(account, T0.AddHours(2));
        Assert.True((await grant.TryClaimAsync(account, T0.AddHours(2))).Granted);
        await grant.ResetAsync(account, T0.AddHours(2));
        Assert.False((await grant.TryClaimAsync(account, T0.AddHours(2))).Granted);

        Assert.Equal(2, await wallet.BalanceAsync(account, Shard));
    }

    /// <summary>A reset dated forward denies the claim until that instant arrives, so a reset is a re-open and not
    /// an unconditional payout.</summary>
    [Fact]
    public async Task Reset_dated_forward_holds_the_claim_until_the_instant_arrives()
    {
        (PeriodicGrant grant, Wallet wallet) = Build();
        AccountId account = FreshAccount();

        Assert.True((await grant.TryClaimAsync(account, T0)).Granted);
        await grant.ResetAsync(account, T0.AddDays(7));

        Assert.False((await grant.TryClaimAsync(account, T0.AddDays(6))).Granted);
        Assert.Equal(1, await wallet.BalanceAsync(account, Shard));

        Assert.True((await grant.TryClaimAsync(account, T0.AddDays(7))).Granted);
        Assert.Equal(2, await wallet.BalanceAsync(account, Shard));
    }
}

public sealed class InMemoryPeriodicGrantResetTests : PeriodicGrantResetContract
{
    protected override (IWalletStore Ledger, IGrantScheduleStore Schedules) NewBackend()
    {
        InMemoryWalletStore store = new();
        return (store, store);
    }
}
