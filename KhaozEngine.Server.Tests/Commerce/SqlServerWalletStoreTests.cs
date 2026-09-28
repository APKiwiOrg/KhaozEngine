using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.SqlServer;
using KhaozEngine.Tests.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

/// <summary>
/// Focused money-critical coverage for <see cref="SqlServerWalletStore"/> against a real SQL Server / Azure SQL
/// database. Gated by <see cref="SqlServerFactAttribute"/> on <c>KE_COMMERCE_SQLSERVER</c>; skipped (not failed)
/// when unset, since <see cref="WalletStoreContract"/>'s <c>[Fact]</c> methods cannot conditionally skip. Each run
/// prefixes account ids with a fresh GUID so a shared test database does not collide across runs.
/// <para>This is the only class that touches the wallet tables, and facts in one class never run at once, so the fact
/// that rebuilds <c>dbo.wallet_balance</c> and <c>dbo.grant_schedule</c> in their older shape cannot pull them from
/// under another.</para>
/// </summary>
public sealed class SqlServerWalletStoreTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("KE_COMMERCE_SQLSERVER");
    private static readonly CurrencyId Currency = new("shard");
    private const string Reward = "dailyShard";
    private static readonly DateTime LegacyUpdatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime LegacyNextAvailable = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

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
    public async Task Credit_replay_returns_historical_balance()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();
        await store.CreditAsync(account, Currency, 5, "dup", LedgerReason.Grant, null);
        await store.CreditAsync(account, Currency, 2, "later", LedgerReason.Grant, null);
        CreditResult again = await store.CreditAsync(account, Currency, 5, "dup", LedgerReason.Grant, null);
        Assert.True(again.Replayed);
        Assert.False(again.Applied);
        Assert.False(again.Conflict);
        Assert.Equal(5, again.NewBalance);
        Assert.Equal(7, await store.GetBalanceAsync(account, Currency));
        Assert.Equal(2, (await store.GetLedgerAsync(account, Currency, 100)).Count);
    }

    [SqlServerFact]
    public async Task Debit_replay_returns_historical_balance()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();
        await store.CreditAsync(account, Currency, 10, "seed", LedgerReason.Grant, null);
        await store.DebitAsync(account, Currency, 4, "dup", LedgerReason.Spend, null);
        await store.CreditAsync(account, Currency, 2, "later", LedgerReason.Grant, null);

        DebitResult again = await store.DebitAsync(account, Currency, 4, "dup", LedgerReason.Spend, null);

        Assert.False(again.Applied);
        Assert.True(again.Replayed);
        Assert.False(again.Conflict);
        Assert.False(again.Insufficient);
        Assert.Equal(6, again.NewBalance);
        Assert.Equal(8, await store.GetBalanceAsync(account, Currency));
        Assert.Equal(3, (await store.GetLedgerAsync(account, Currency, 100)).Count);
    }

    [SqlServerFact]
    public async Task Credit_key_reuse_with_different_intent_conflicts()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();
        await store.CreditAsync(account, Currency, 5, "dup", LedgerReason.Grant, null);
        await store.CreditAsync(account, Currency, 2, "later", LedgerReason.Grant, null);

        CreditResult differentAmount = await store.CreditAsync(
            account, Currency, 6, "dup", LedgerReason.Grant, null);
        CreditResult differentReason = await store.CreditAsync(
            account, Currency, 5, "dup", LedgerReason.Adjustment, null);

        AssertCreditConflict(differentAmount, 5);
        AssertCreditConflict(differentReason, 5);
        Assert.Equal(7, await store.GetBalanceAsync(account, Currency));
        Assert.Equal(2, (await store.GetLedgerAsync(account, Currency, 100)).Count);
    }

    [SqlServerFact]
    public async Task Debit_key_reuse_with_different_intent_conflicts()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();
        await store.CreditAsync(account, Currency, 10, "seed", LedgerReason.Grant, null);
        await store.DebitAsync(account, Currency, 4, "dup", LedgerReason.Spend, null);
        await store.CreditAsync(account, Currency, 2, "later", LedgerReason.Grant, null);

        DebitResult differentAmount = await store.DebitAsync(
            account, Currency, 3, "dup", LedgerReason.Spend, null);
        DebitResult differentReason = await store.DebitAsync(
            account, Currency, 4, "dup", LedgerReason.Adjustment, null);

        AssertDebitConflict(differentAmount, 6);
        AssertDebitConflict(differentReason, 6);
        Assert.Equal(8, await store.GetBalanceAsync(account, Currency));
        Assert.Equal(3, (await store.GetLedgerAsync(account, Currency, 100)).Count);
    }

    [SqlServerFact]
    public async Task Key_reuse_across_credit_and_debit_conflicts()
    {
        SqlServerWalletStore store = NewStore();
        AccountId creditFirst = FreshAccount();
        await store.CreditAsync(creditFirst, Currency, 4, "same", LedgerReason.Adjustment, null);

        DebitResult debitConflict = await store.DebitAsync(
            creditFirst, Currency, 4, "same", LedgerReason.Adjustment, null);

        AssertDebitConflict(debitConflict, 4);
        Assert.Equal(4, await store.GetBalanceAsync(creditFirst, Currency));
        Assert.Single(await store.GetLedgerAsync(creditFirst, Currency, 100));

        AccountId debitFirst = FreshAccount();
        await store.CreditAsync(debitFirst, Currency, 10, "seed", LedgerReason.Grant, null);
        await store.DebitAsync(debitFirst, Currency, 4, "same", LedgerReason.Adjustment, null);

        CreditResult creditConflict = await store.CreditAsync(
            debitFirst, Currency, 4, "same", LedgerReason.Adjustment, null);

        AssertCreditConflict(creditConflict, 6);
        Assert.Equal(6, await store.GetBalanceAsync(debitFirst, Currency));
        Assert.Equal(2, (await store.GetLedgerAsync(debitFirst, Currency, 100)).Count);
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

    /// <summary>The SQL Server row of <see cref="PeriodicGrantResetContract"/>, which the contract base cannot carry
    /// for this backend because its <c>[Fact]</c> rows cannot skip. The reset writes through this backend's MERGE and
    /// the claim that follows keys on the DATETIME2 instant it reads back, so this is the one place that proves the
    /// round trip survives a real server rather than a dictionary.</summary>
    [SqlServerFact]
    public async Task Reset_reopens_the_reward_against_a_retained_ledger()
    {
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();
        Wallet wallet = new(store, new InMemoryProductCatalog(Array.Empty<ProductDefinition>()));
        PeriodicGrant grant = new(wallet, store, TimeSpan.FromHours(24), "dailyShard", Currency, 1);
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True((await grant.TryClaimAsync(account, t0)).Granted);
        Assert.False((await grant.TryClaimAsync(account, t0.AddHours(1))).Granted);

        await grant.ResetAsync(account, t0.AddHours(2));

        Assert.True((await grant.TryClaimAsync(account, t0.AddHours(2))).Granted);
        Assert.Equal(2, await wallet.BalanceAsync(account, Currency));
    }

    /// <summary>Stress the atomic credit/debit update paths against a live server: many concurrent credits and
    /// debits, distinct idempotency keys, racing on the same account row under <c>IsolationLevel.Serializable</c>.
    /// This regresses the missing-receipt range lock against balance-row lock cycle, which surfaced as error 1205.
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

    /// <summary>A balance row is created with equal creation and update times, and every later credit or debit moves
    /// the update time alone. A grant schedule row does the same through a claim, and a write that stores the
    /// instant already there changes nothing, so its update time stays. Every time is the database clock the store
    /// stamps with.</summary>
    [SqlServerFact]
    public async Task Wallet_balance_and_grant_schedule_stamp_creation_and_update()
    {
        string cs = ConnectionString!;
        SqlServerWalletStore store = NewStore();
        AccountId account = FreshAccount();

        DateTime before = await SqlServerTableProbe.ServerNowAsync(cs);
        await store.CreditAsync(account, Currency, 5, "first", LedgerReason.Grant, null);
        DateTime after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? created, DateTime? updated) = await BalanceTimesAsync(cs, account);
        Assert.Equal(updated, created);
        Assert.InRange(created ?? DateTime.MinValue, before, after);

        await SqlServerTableProbe.WaitForServerClockPastAsync(cs, after);
        before = await SqlServerTableProbe.ServerNowAsync(cs);
        await store.CreditAsync(account, Currency, 2, "second", LedgerReason.Grant, null);
        after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? keptCreated, DateTime? moved) = await BalanceTimesAsync(cs, account);
        Assert.Equal(created, keptCreated);
        Assert.InRange(moved ?? DateTime.MinValue, before, after);

        await SqlServerTableProbe.WaitForServerClockPastAsync(cs, after);
        before = await SqlServerTableProbe.ServerNowAsync(cs);
        await store.DebitAsync(account, Currency, 3, "spend", LedgerReason.Spend, null);
        after = await SqlServerTableProbe.ServerNowAsync(cs);
        (keptCreated, moved) = await BalanceTimesAsync(cs, account);
        Assert.Equal(created, keptCreated);
        Assert.InRange(moved ?? DateTime.MinValue, before, after);

        AccountId claimer = FreshAccount();
        PeriodicGrant grant = new(new Wallet(store, new InMemoryProductCatalog(Array.Empty<ProductDefinition>())),
            store, TimeSpan.FromHours(24), Reward, Currency, 1);
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        before = await SqlServerTableProbe.ServerNowAsync(cs);
        Assert.True((await grant.TryClaimAsync(claimer, t0)).Granted);
        after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? scheduleCreated, DateTime? scheduleUpdated) = await ScheduleTimesAsync(cs, claimer);
        Assert.Equal(scheduleUpdated, scheduleCreated);
        Assert.InRange(scheduleCreated ?? DateTime.MinValue, before, after);

        await SqlServerTableProbe.WaitForServerClockPastAsync(cs, after);
        before = await SqlServerTableProbe.ServerNowAsync(cs);
        Assert.True((await grant.TryClaimAsync(claimer, t0.AddHours(25))).Granted);
        after = await SqlServerTableProbe.ServerNowAsync(cs);
        (keptCreated, moved) = await ScheduleTimesAsync(cs, claimer);
        Assert.Equal(scheduleCreated, keptCreated);
        Assert.InRange(moved ?? DateTime.MinValue, before, after);

        await SqlServerTableProbe.WaitForServerClockPastAsync(cs, after);
        DateTimeOffset stored = (await store.GetNextAvailableAsync(claimer, Reward))!.Value;
        await grant.ResetAsync(claimer, stored);
        Assert.Equal((scheduleCreated, moved), await ScheduleTimesAsync(cs, claimer));
    }

    /// <summary>Tables an older build created gain the nullable columns in place. Their rows keep a NULL creation
    /// time after later writes, and the schedule row's unknown update time is filled only by a write that changes it.
    /// The fact drops and rebuilds both tables with the key collation they always had, so it runs only against a
    /// database named for commerce tests.</summary>
    [SqlServerFact]
    public async Task Wallet_tables_from_an_older_build_gain_the_new_columns()
    {
        string cs = SqlServerTableProbe.RequireMarkedDatabase(ConnectionString, "-commerce-test-");
        AccountId legacy = FreshAccount();
        await SqlServerTableProbe.ExecuteAsync(cs, $"""
            IF OBJECT_ID(N'dbo.wallet_balance', N'U') IS NOT NULL DROP TABLE dbo.wallet_balance;
            IF OBJECT_ID(N'dbo.grant_schedule', N'U') IS NOT NULL DROP TABLE dbo.grant_schedule;
            CREATE TABLE dbo.wallet_balance (
              account_id NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
              currency_id NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
              amount BIGINT NOT NULL,
              updated_at DATETIME2 NOT NULL, PRIMARY KEY(account_id, currency_id));
            CREATE TABLE dbo.grant_schedule (
              account_id NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
              reward_id NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
              next_available_utc DATETIME2 NOT NULL,
              PRIMARY KEY(account_id, reward_id));
            INSERT INTO dbo.wallet_balance VALUES (N'{legacy.Value}', N'{Currency.Value}', 7, '2026-01-01T00:00:00');
            INSERT INTO dbo.grant_schedule VALUES (N'{legacy.Value}', N'{Reward}', '2026-01-02T00:00:00');
            """);

        SqlServerWalletStore widened = NewStore();
        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "wallet_balance", "created_at"));
        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "grant_schedule", "created_at"));
        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "grant_schedule", "updated_at"));
        Assert.Equal(((DateTime?)null, (DateTime?)LegacyUpdatedAt), await BalanceTimesAsync(cs, legacy));
        Assert.Equal(((DateTime?)null, (DateTime?)null), await ScheduleTimesAsync(cs, legacy));

        await widened.CreditAsync(legacy, Currency, 1, "after-upgrade", LedgerReason.Grant, null);
        Assert.Equal(8, await widened.GetBalanceAsync(legacy, Currency));
        (DateTime? created, DateTime? updated) = await BalanceTimesAsync(cs, legacy);
        Assert.Null(created);
        Assert.NotEqual(LegacyUpdatedAt, updated);

        DateTimeOffset stored = new(LegacyNextAvailable, TimeSpan.Zero);
        await widened.SetNextAvailableAsync(legacy, Reward, stored);
        Assert.Equal(((DateTime?)null, (DateTime?)null), await ScheduleTimesAsync(cs, legacy));

        DateTime before = await SqlServerTableProbe.ServerNowAsync(cs);
        await widened.SetNextAvailableAsync(legacy, Reward, stored.AddDays(1));
        DateTime after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? scheduleCreated, DateTime? scheduleUpdated) = await ScheduleTimesAsync(cs, legacy);
        Assert.Null(scheduleCreated);
        Assert.InRange(scheduleUpdated ?? DateTime.MinValue, before, after);

        _ = NewStore();
        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "grant_schedule", "updated_at"));
    }

    private static Task<(DateTime? First, DateTime? Second)> BalanceTimesAsync(string cs, AccountId account)
        => SqlServerTableProbe.ReadPairAsync(cs,
            "SELECT created_at, updated_at FROM dbo.wallet_balance WHERE account_id = @a AND currency_id = @c;",
            ("@a", account.Value), ("@c", Currency.Value));

    private static Task<(DateTime? First, DateTime? Second)> ScheduleTimesAsync(string cs, AccountId account)
        => SqlServerTableProbe.ReadPairAsync(cs,
            "SELECT created_at, updated_at FROM dbo.grant_schedule WHERE account_id = @a AND reward_id = @r;",
            ("@a", account.Value), ("@r", Reward));

    private static void AssertCreditConflict(CreditResult result, long historicalBalance)
    {
        Assert.False(result.Applied);
        Assert.False(result.Replayed);
        Assert.True(result.Conflict);
        Assert.Equal(historicalBalance, result.NewBalance);
    }

    private static void AssertDebitConflict(DebitResult result, long historicalBalance)
    {
        Assert.False(result.Applied);
        Assert.False(result.Replayed);
        Assert.False(result.Insufficient);
        Assert.True(result.Conflict);
        Assert.Equal(historicalBalance, result.NewBalance);
    }
}
