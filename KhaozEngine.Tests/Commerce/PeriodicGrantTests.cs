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
    public async Task Double_claim_at_same_instant_credits_once()
    {
        (PeriodicGrant grant, Wallet wallet) = Build();
        await grant.TryClaimAsync(A, T0);
        await grant.TryClaimAsync(A, T0.AddDays(1)); // available again
        await grant.TryClaimAsync(A, T0.AddDays(1)); // same scheduled instant, replayed
        Assert.Equal(2, await wallet.BalanceAsync(A, Shard));
    }
}
