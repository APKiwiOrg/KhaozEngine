using System;
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
}
