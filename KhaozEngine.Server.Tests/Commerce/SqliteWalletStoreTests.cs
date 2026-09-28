using System;
using System.Threading.Tasks;
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.Sqlite;
using KhaozEngine.Tests.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

public sealed class SqliteWalletStoreTests : WalletStoreContract, IDisposable
{
    /// <summary>The wallet tables as every build before creation times created them.</summary>
    private const string LegacyTables = """
        CREATE TABLE wallet_ledger (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          account_id TEXT NOT NULL, currency_id TEXT NOT NULL, delta INTEGER NOT NULL,
          idempotency_key TEXT NOT NULL, reason INTEGER NOT NULL, source_ref TEXT NULL,
          post_balance INTEGER NOT NULL, created_at INTEGER NOT NULL);
        CREATE UNIQUE INDEX ux_ledger_idem ON wallet_ledger(account_id, currency_id, idempotency_key);
        CREATE INDEX ix_ledger_acct ON wallet_ledger(account_id, currency_id, id DESC);
        CREATE TABLE wallet_balance (
          account_id TEXT NOT NULL, currency_id TEXT NOT NULL, amount INTEGER NOT NULL,
          updated_at INTEGER NOT NULL, PRIMARY KEY(account_id, currency_id));
        CREATE TABLE grant_schedule (
          account_id TEXT NOT NULL, reward_id TEXT NOT NULL, next_available_utc INTEGER NOT NULL,
          PRIMARY KEY(account_id, reward_id));
        """;

    private const long LegacyUpdatedAt = 1_700_000_000_000;
    private const string Reward = "dailyShard";
    private static readonly CurrencyId Shard = new("shard");
    private static readonly AccountId Legacy = new("acct:legacy");

    // Shared in-memory SQLite kept alive by one open connection for the test's lifetime.
    // xUnit news a fresh class instance per [Fact], so each test gets its own uniquely named
    // in-memory database: no cross-test state leakage.
    private readonly SqliteWalletStore store = new($"Data Source=commerce_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");

    protected override IWalletStore NewStore() => store;

    public void Dispose() => store.Dispose();

    /// <summary>A balance row is created with equal creation and update times, and every later credit or debit moves
    /// the update time alone. A grant schedule row does the same through a claim, and a write that stores the
    /// instant already there changes nothing, so its update time stays.</summary>
    [Fact]
    public async Task Wallet_balance_and_grant_schedule_stamp_creation_and_update()
    {
        using var file = new SqliteScratchFile("ke-wallet-times-");
        using var backend = new SqliteWalletStore(file.ConnectionString);
        var account = new AccountId("acct:times");

        long before = SqliteScratchFile.NowMilliseconds();
        await backend.CreditAsync(account, Shard, 5, "first", LedgerReason.Grant, null);
        long after = SqliteScratchFile.NowMilliseconds();
        (long? created, long? updated) = BalanceTimes(file, account);
        Assert.Equal(updated, created);
        Assert.InRange(created ?? -1, before, after);

        await SqliteScratchFile.WaitForClockPastAsync(after);
        before = SqliteScratchFile.NowMilliseconds();
        await backend.CreditAsync(account, Shard, 2, "second", LedgerReason.Grant, null);
        after = SqliteScratchFile.NowMilliseconds();
        Assert.Equal(created, BalanceTimes(file, account).Created);
        Assert.InRange(BalanceTimes(file, account).Updated ?? -1, before, after);

        await SqliteScratchFile.WaitForClockPastAsync(after);
        before = SqliteScratchFile.NowMilliseconds();
        await backend.DebitAsync(account, Shard, 3, "spend", LedgerReason.Spend, null);
        after = SqliteScratchFile.NowMilliseconds();
        Assert.Equal(created, BalanceTimes(file, account).Created);
        Assert.InRange(BalanceTimes(file, account).Updated ?? -1, before, after);

        var claimer = new AccountId("acct:claimer");
        PeriodicGrant grant = new(new Wallet(backend, new InMemoryProductCatalog(Array.Empty<ProductDefinition>())),
            backend, TimeSpan.FromHours(24), Reward, Shard, 1);
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        before = SqliteScratchFile.NowMilliseconds();
        Assert.True((await grant.TryClaimAsync(claimer, t0)).Granted);
        after = SqliteScratchFile.NowMilliseconds();
        (long? scheduleCreated, long? scheduleUpdated) = ScheduleTimes(file, claimer);
        Assert.Equal(scheduleUpdated, scheduleCreated);
        Assert.InRange(scheduleCreated ?? -1, before, after);

        await SqliteScratchFile.WaitForClockPastAsync(after);
        before = SqliteScratchFile.NowMilliseconds();
        Assert.True((await grant.TryClaimAsync(claimer, t0.AddHours(25))).Granted);
        after = SqliteScratchFile.NowMilliseconds();
        (long? keptCreated, long? moved) = ScheduleTimes(file, claimer);
        Assert.Equal(scheduleCreated, keptCreated);
        Assert.InRange(moved ?? -1, before, after);

        await SqliteScratchFile.WaitForClockPastAsync(after);
        DateTimeOffset stored = (await backend.GetNextAvailableAsync(claimer, Reward))!.Value;
        await grant.ResetAsync(claimer, stored);
        Assert.Equal((scheduleCreated, moved), ScheduleTimes(file, claimer));
    }

    /// <summary>A file an older build wrote gains the nullable columns in place. Its rows keep a NULL creation time
    /// after later writes, because no write after the insert knows when the row was created, and the schedule row's
    /// unknown update time is filled only by a write that changes it.</summary>
    [Fact]
    public async Task Wallet_tables_from_an_older_build_gain_the_new_columns()
    {
        using var legacy = new SqliteScratchFile("ke-wallet-legacy-");
        legacy.Execute(LegacyTables +
            $"INSERT INTO wallet_balance VALUES ('{Legacy.Value}', '{Shard.Value}', 7, {LegacyUpdatedAt});" +
            $"INSERT INTO grant_schedule VALUES ('{Legacy.Value}', '{Reward}', {LegacyUpdatedAt});");

        using (var widened = new SqliteWalletStore(legacy.ConnectionString))
        {
            Assert.Equal((1, "INTEGER", false), legacy.Column("wallet_balance", "created_at"));
            Assert.Equal((1, "INTEGER", false), legacy.Column("grant_schedule", "created_at"));
            Assert.Equal((1, "INTEGER", false), legacy.Column("grant_schedule", "updated_at"));
            Assert.Equal(((long?)null, (long?)LegacyUpdatedAt), BalanceTimes(legacy, Legacy));
            Assert.Equal(((long?)null, (long?)null), ScheduleTimes(legacy, Legacy));

            await widened.CreditAsync(Legacy, Shard, 1, "after-upgrade", LedgerReason.Grant, null);
            Assert.Equal(8, await widened.GetBalanceAsync(Legacy, Shard));
            (long? created, long? updated) = BalanceTimes(legacy, Legacy);
            Assert.Null(created);
            Assert.NotEqual(LegacyUpdatedAt, updated);

            DateTimeOffset stored = DateTimeOffset.FromUnixTimeMilliseconds(LegacyUpdatedAt);
            await widened.SetNextAvailableAsync(Legacy, Reward, stored);
            Assert.Equal(((long?)null, (long?)null), ScheduleTimes(legacy, Legacy));

            long before = SqliteScratchFile.NowMilliseconds();
            await widened.SetNextAvailableAsync(Legacy, Reward, stored.AddDays(1));
            long after = SqliteScratchFile.NowMilliseconds();
            (long? scheduleCreated, long? scheduleUpdated) = ScheduleTimes(legacy, Legacy);
            Assert.Null(scheduleCreated);
            Assert.InRange(scheduleUpdated ?? -1, before, after);
        }

        using var reopened = new SqliteWalletStore(legacy.ConnectionString);
        Assert.Equal(1, legacy.Column("grant_schedule", "updated_at").Count);
    }

    /// <summary>Two processes opening one legacy file at once must not both add a column. The loser of that race
    /// would fail its open on a duplicate column, so the checks and the adds share one immediate transaction. It loops
    /// because a race that shows one time in ten is the one that reaches production.</summary>
    [Fact]
    public async Task Wallet_widening_survives_concurrent_openers()
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            using var legacy = new SqliteScratchFile("ke-wallet-race-");
            legacy.Execute(LegacyTables);
            await SqliteScratchFile.OpenConcurrentlyAsync(() => new SqliteWalletStore(legacy.ConnectionString), openers: 4);
            Assert.Equal(1, legacy.Column("wallet_balance", "created_at").Count);
            Assert.Equal(1, legacy.Column("grant_schedule", "created_at").Count);
            Assert.Equal(1, legacy.Column("grant_schedule", "updated_at").Count);
        }
    }

    private static (long? Created, long? Updated) BalanceTimes(SqliteScratchFile database, AccountId account)
        => database.ReadPair("SELECT created_at, updated_at FROM wallet_balance WHERE account_id = $a AND currency_id = $c;",
            ("$a", account.Value), ("$c", Shard.Value));

    private static (long? Created, long? Updated) ScheduleTimes(SqliteScratchFile database, AccountId account)
        => database.ReadPair("SELECT created_at, updated_at FROM grant_schedule WHERE account_id = $a AND reward_id = $r;",
            ("$a", account.Value), ("$r", Reward));
}

/// <summary>The SQLite row of <see cref="PeriodicGrantResetContract"/>. This backend stores the schedule instant as
/// unix MILLISECONDS, so a reset instant comes back truncated and the claim keys on the truncated ticks. Same
/// per-[Fact] instance and same in-memory database lifetime as the wallet rows above.</summary>
public sealed class SqlitePeriodicGrantResetTests : PeriodicGrantResetContract, IDisposable
{
    private readonly SqliteWalletStore store = new($"Data Source=commerce_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");

    protected override (IWalletStore Ledger, IGrantScheduleStore Schedules) NewBackend() => (store, store);

    public void Dispose() => store.Dispose();
}
