using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Commerce;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

public abstract class WalletStoreContract
{
    protected abstract IWalletStore NewStore();
    private static readonly AccountId A = new("acct:1");
    private static readonly CurrencyId C = new("shard");

    [Fact]
    public async Task Credit_moves_balance()
    {
        IWalletStore s = NewStore();
        CreditResult r = await s.CreditAsync(A, C, 5, "k1", LedgerReason.Grant, null);
        Assert.True(r.Applied);
        Assert.False(r.Replayed);
        Assert.Equal(5, r.NewBalance);
        Assert.Equal(5, await s.GetBalanceAsync(A, C));
    }

    [Fact]
    public async Task Credit_is_idempotent_by_key()
    {
        IWalletStore s = NewStore();
        await s.CreditAsync(A, C, 5, "dup", LedgerReason.Grant, null);
        CreditResult again = await s.CreditAsync(A, C, 5, "dup", LedgerReason.Grant, null);
        Assert.True(again.Replayed);
        Assert.Equal(5, again.NewBalance);
        Assert.Single(await s.GetLedgerAsync(A, C, 100));
    }

    [Fact]
    public async Task Debit_rejects_overspend_atomically()
    {
        IWalletStore s = NewStore();
        await s.CreditAsync(A, C, 3, "k", LedgerReason.Grant, null);
        DebitResult r = await s.DebitAsync(A, C, 10, "spend1", LedgerReason.Spend, null);
        Assert.True(r.Insufficient);
        Assert.False(r.Applied);
        Assert.Equal(3, await s.GetBalanceAsync(A, C));
        Assert.Single(await s.GetLedgerAsync(A, C, 100)); // only the credit
    }

    [Fact]
    public async Task Debit_is_idempotent_by_key()
    {
        IWalletStore s = NewStore();
        await s.CreditAsync(A, C, 10, "c", LedgerReason.Grant, null);
        await s.DebitAsync(A, C, 4, "spend", LedgerReason.Spend, null);
        DebitResult again = await s.DebitAsync(A, C, 4, "spend", LedgerReason.Spend, null);
        Assert.True(again.Replayed);
        Assert.Equal(6, again.NewBalance);
    }

    [Fact]
    public async Task Concurrent_distinct_credits_sum()
    {
        IWalletStore s = NewStore();
        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => s.CreditAsync(A, C, 1, $"k{i}", LedgerReason.Grant, null)));
        Assert.Equal(50, await s.GetBalanceAsync(A, C));
    }

    [Fact]
    public async Task Rejects_nonpositive_amount()
    {
        IWalletStore s = NewStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => s.CreditAsync(A, C, 0, "z", LedgerReason.Grant, null));
    }

    [Fact]
    public async Task GetLedger_rejects_negative_limit()
    {
        IWalletStore s = NewStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => s.GetLedgerAsync(A, C, -1));
    }

    [Fact]
    public async Task Same_key_different_accounts_do_not_collide()
    {
        IWalletStore s = NewStore();
        AccountId a2 = new("acct:2");
        CreditResult r1 = await s.CreditAsync(A, C, 5, "shared", LedgerReason.Grant, null);
        CreditResult r2 = await s.CreditAsync(a2, C, 7, "shared", LedgerReason.Grant, null);
        Assert.True(r1.Applied);
        Assert.True(r2.Applied);         // NOT a replay: different account
        Assert.False(r2.Replayed);
        Assert.Equal(5, await s.GetBalanceAsync(A, C));
        Assert.Equal(7, await s.GetBalanceAsync(a2, C));
    }
}
