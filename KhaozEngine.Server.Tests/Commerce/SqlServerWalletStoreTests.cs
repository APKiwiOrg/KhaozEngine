using System;
using System.Linq;
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

    /// <summary>The SQL Server row of <see cref="WalletStoreContract.Keys_and_account_ids_are_case_sensitive"/>,
    /// which the contract base cannot carry for this backend because its <c>[Fact]</c> rows cannot skip. Case
    /// sensitivity is the one wallet behaviour this backend does not get for free: without the binary collation
    /// the schema pins on its key columns, a database created with the usual case-insensitive default answers
    /// "claim-ABC" as a replay of "claim-abc" and swallows the second credit, while InMemory and SQLite apply
    /// both. Run it against a database whose DEFAULT collation is case-insensitive, which is the deployment this
    /// guards.</summary>
    [SqlServerFact]
    public async Task Keys_and_account_ids_are_case_sensitive()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();
        CreditResult lower = await store.CreditAsync(account, Currency, 5, "claim-abc", LedgerReason.Grant, null);
        CreditResult upper = await store.CreditAsync(account, Currency, 7, "claim-ABC", LedgerReason.Grant, null);
        Assert.True(lower.Applied);
        Assert.True(upper.Applied); // NOT a replay: the two keys differ by case, so they are two operations
        Assert.False(upper.Replayed);
        Assert.Equal(12, await store.GetBalanceAsync(account, Currency));
        Assert.Equal(2, (await store.GetLedgerAsync(account, Currency, 100)).Count);

        // Account ids the same way: two ids differing only by case are two wallets, not one.
        string stem = $"acct:{Guid.NewGuid():N}";
        AccountId lowerAccount = new($"{stem}-case");
        AccountId upperAccount = new($"{stem}-CASE");
        await store.CreditAsync(lowerAccount, Currency, 3, "k", LedgerReason.Grant, null);
        await store.CreditAsync(upperAccount, Currency, 4, "k", LedgerReason.Grant, null);
        Assert.Equal(3, await store.GetBalanceAsync(lowerAccount, Currency));
        Assert.Equal(4, await store.GetBalanceAsync(upperAccount, Currency));
    }

    /// <summary>Stress the atomic credit/debit update paths against a live server: many concurrent credits and
    /// debits, distinct idempotency keys, racing on the same account row under <c>IsolationLevel.Serializable</c>.
    /// The final balance must equal the net of applied ops and must never go negative. Only runs when
    /// <c>KE_COMMERCE_SQLSERVER</c> is set; compiles and skips cleanly otherwise.</summary>
    [SqlServerFact]
    public async Task Parallel_credits_and_debits_settle_to_the_net_balance()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();

        // Seed enough headroom that debits racing ahead of credits still cannot legitimately go negative;
        // any 'Insufficient' result here would mean debits are outrunning applied credits, not a real deficit.
        await store.CreditAsync(account, Currency, 1_000, "seed", LedgerReason.Grant, null);

        const int creditCount = 20;
        const int debitCount = 10;
        const long creditAmount = 3;
        const long debitAmount = 5;

        Task<CreditResult>[] credits = Enumerable.Range(0, creditCount)
            .Select(i => store.CreditAsync(account, Currency, creditAmount, $"stress-credit-{i}", LedgerReason.Grant, null))
            .ToArray();
        Task<DebitResult>[] debits = Enumerable.Range(0, debitCount)
            .Select(i => store.DebitAsync(account, Currency, debitAmount, $"stress-debit-{i}", LedgerReason.Spend, null))
            .ToArray();

        await Task.WhenAll(credits.Cast<Task>().Concat(debits.Cast<Task>()));

        Assert.All(credits, t => Assert.True(t.Result.Applied));
        Assert.All(debits, t => Assert.True(t.Result.Applied));

        long expected = 1_000 + creditCount * creditAmount - debitCount * debitAmount;
        long balance = await store.GetBalanceAsync(account, Currency);
        Assert.Equal(expected, balance);
        Assert.True(balance >= 0);
    }
}
