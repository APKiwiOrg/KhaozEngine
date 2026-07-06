using System;
using System.Threading.Tasks;
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

/// <summary>
/// Focused money-critical coverage for <see cref="SqlServerWalletStore"/> against a real SQL Server / Azure SQL
/// database. Gated by <see cref="SqlServerFactAttribute"/> on <c>KE_COMMERCE_SQLSERVER</c>; skipped (not failed)
/// when unset, since <see cref="WalletStoreContract"/>'s <c>[Fact]</c> methods cannot conditionally skip. Each run
/// prefixes account ids with a fresh GUID so a shared test database does not collide across runs.
/// </summary>
public sealed class SqlServerWalletStoreTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("KE_COMMERCE_SQLSERVER");
    private static readonly CurrencyId Currency = new("shard");

    private static SqlServerWalletStore NewStore() => new(ConnectionString!);

    private static AccountId FreshAccount() => new($"acct:{Guid.NewGuid():N}");

    [SqlServerFact]
    public async Task Credit_moves_balance()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();
        CreditResult r = await store.CreditAsync(account, Currency, 5, "k1", LedgerReason.Grant, null);
        Assert.True(r.Applied);
        Assert.False(r.Replayed);
        Assert.Equal(5, r.NewBalance);
        Assert.Equal(5, await store.GetBalanceAsync(account, Currency));
    }

    [SqlServerFact]
    public async Task Replayed_key_credits_once()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();
        await store.CreditAsync(account, Currency, 5, "dup", LedgerReason.Grant, null);
        CreditResult again = await store.CreditAsync(account, Currency, 5, "dup", LedgerReason.Grant, null);
        Assert.True(again.Replayed);
        Assert.False(again.Applied);
        Assert.Equal(5, again.NewBalance);
        Assert.Equal(5, await store.GetBalanceAsync(account, Currency));
    }

    [SqlServerFact]
    public async Task Overspend_is_rejected_atomically_with_no_ledger_row()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();
        await store.CreditAsync(account, Currency, 3, "k", LedgerReason.Grant, null);
        DebitResult r = await store.DebitAsync(account, Currency, 10, "spend1", LedgerReason.Spend, null);
        Assert.True(r.Insufficient);
        Assert.False(r.Applied);
        Assert.Equal(3, await store.GetBalanceAsync(account, Currency));
        Assert.Single(await store.GetLedgerAsync(account, Currency, 100)); // only the credit
    }

    [SqlServerFact]
    public async Task Same_key_different_accounts_do_not_collide()
    {
        SqlServerWalletStore store = NewStore();
        AccountId a1 = FreshAccount();
        AccountId a2 = FreshAccount();
        CreditResult r1 = await store.CreditAsync(a1, Currency, 5, "shared", LedgerReason.Grant, null);
        CreditResult r2 = await store.CreditAsync(a2, Currency, 7, "shared", LedgerReason.Grant, null);
        Assert.True(r1.Applied);
        Assert.True(r2.Applied);   // NOT a replay: different account
        Assert.False(r2.Replayed);
        Assert.Equal(5, await store.GetBalanceAsync(a1, Currency));
        Assert.Equal(7, await store.GetBalanceAsync(a2, Currency));
    }
}
