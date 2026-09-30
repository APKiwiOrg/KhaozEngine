using System;
using System.Threading.Tasks;
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.SqlServer;
using KhaozEngine.Tests.WorldStore;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

[CollectionDefinition("SQL Server wallet", DisableParallelization = true)]
public sealed class SqlServerWalletCollection;

[Collection("SQL Server wallet")]
public sealed class SqlServerWalletStoreBootstrapTests
{
    private const string LockResource = "KhaozEngine.Commerce.Schema";
    private static readonly CurrencyId Currency = new("shard");
    private static readonly DateTime LegacyTime = new(2026, 1, 1);
    private static string ConnectionString => SqlServerTableProbe.RequireMarkedDatabase(
        Environment.GetEnvironmentVariable("KE_COMMERCE_SQLSERVER"), "-commerce-test-");

    [SqlServerFact]
    public async Task Two_constructors_on_an_empty_database_wait_before_creating_tables_and_indexes()
    {
        string cs = ConnectionString;
        await DropTablesAsync(cs);
        SqlServerWalletStore[] stores = await SqlServerSchemaBootstrapProbe.ConstructWhileHeldAsync(
            cs, LockResource, value => new SqlServerWalletStore(value), async () =>
            {
                Assert.Equal(0, await SqlServerSchemaBootstrapProbe.ScalarAsync(cs, """
                    SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo')
                      AND name IN (N'wallet_ledger', N'wallet_balance', N'grant_schedule');
                    """));
            });

        Assert.Equal(2, await SqlServerSchemaBootstrapProbe.ScalarAsync(cs, """
            SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.wallet_ledger')
              AND name IN (N'ux_ledger_idem', N'ix_ledger_acct');
            """));
        Assert.Equal(true, await SqlServerSchemaBootstrapProbe.ScalarAsync(cs, """
            SELECT is_unique FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.wallet_ledger')
              AND name = N'ux_ledger_idem';
            """));
        Assert.Equal(0, await SqlServerSchemaBootstrapProbe.ScalarAsync(cs, """
            SELECT COUNT(*) FROM sys.columns WHERE object_id IN
              (OBJECT_ID(N'dbo.wallet_ledger'), OBJECT_ID(N'dbo.wallet_balance'), OBJECT_ID(N'dbo.grant_schedule'))
              AND name IN (N'account_id', N'currency_id', N'idempotency_key', N'reward_id')
              AND collation_name <> N'Latin1_General_100_BIN2';
            """));
        await AssertFreshRowsAsync(cs, stores[0]);
        Assert.Equal(5, await stores[1].GetBalanceAsync(new AccountId("fresh"), Currency));
    }

    [SqlServerFact]
    public async Task Two_constructors_on_legacy_tables_wait_before_widening_and_preserve_rows()
    {
        string cs = ConnectionString;
        await DropTablesAsync(cs);
        await SqlServerTableProbe.ExecuteAsync(cs, """
            CREATE TABLE dbo.wallet_balance (
              account_id NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
              currency_id NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
              amount BIGINT NOT NULL, updated_at DATETIME2 NOT NULL, PRIMARY KEY(account_id, currency_id));
            CREATE TABLE dbo.grant_schedule (
              account_id NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
              reward_id NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
              next_available_utc DATETIME2 NOT NULL, PRIMARY KEY(account_id, reward_id));
            """);
        await SqlServerTableProbe.ExecuteAsync(cs, """
            INSERT INTO dbo.wallet_balance VALUES (N'legacy', N'shard', 7, '2026-01-01T00:00:00');
            INSERT INTO dbo.grant_schedule VALUES (N'legacy', N'daily', '2026-01-01T00:00:00');
            """);

        SqlServerWalletStore[] stores = await SqlServerSchemaBootstrapProbe.ConstructWhileHeldAsync(
            cs, LockResource, value => new SqlServerWalletStore(value), async () =>
            {
                Assert.Null(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "wallet_balance", "created_at"));
                Assert.Null(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "grant_schedule", "created_at"));
                Assert.Null(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "grant_schedule", "updated_at"));
                Assert.Equal(DBNull.Value, await SqlServerSchemaBootstrapProbe.ScalarAsync(cs,
                    "SELECT OBJECT_ID(N'dbo.wallet_ledger', N'U');"));
            });

        var legacy = new AccountId("legacy");
        Assert.Equal(7, await stores[0].GetBalanceAsync(legacy, Currency));
        Assert.Equal(new DateTimeOffset(LegacyTime, TimeSpan.Zero), await stores[1].GetNextAvailableAsync(legacy, "daily"));
        Assert.Equal(((DateTime?)null, (DateTime?)LegacyTime), await BalanceTimesAsync(cs, "legacy"));
        Assert.Equal(((DateTime?)null, (DateTime?)null), await ScheduleTimesAsync(cs, "legacy"));
        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "wallet_balance", "created_at"));
        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "grant_schedule", "created_at"));
        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "grant_schedule", "updated_at"));

        DateTime before = await SqlServerTableProbe.ServerNowAsync(cs);
        await stores[0].CreditAsync(legacy, Currency, 1, "after-upgrade", LedgerReason.Grant, null);
        await stores[1].SetNextAvailableAsync(legacy, "daily", new DateTimeOffset(LegacyTime, TimeSpan.Zero).AddDays(1));
        DateTime after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? created, DateTime? updated) = await BalanceTimesAsync(cs, "legacy");
        Assert.Null(created);
        Assert.InRange(updated ?? DateTime.MinValue, before, after);
        (created, updated) = await ScheduleTimesAsync(cs, "legacy");
        Assert.Null(created);
        Assert.InRange(updated ?? DateTime.MinValue, before, after);
        await AssertFreshRowsAsync(cs, stores[0]);
    }

    private static async Task AssertFreshRowsAsync(string cs, SqlServerWalletStore store)
    {
        var account = new AccountId("fresh");
        DateTime before = await SqlServerTableProbe.ServerNowAsync(cs);
        await store.CreditAsync(account, Currency, 5, "fresh", LedgerReason.Grant, null);
        await store.SetNextAvailableAsync(account, "daily", DateTimeOffset.UnixEpoch);
        DateTime after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? created, DateTime? updated) = await BalanceTimesAsync(cs, "fresh");
        Assert.Equal(created, updated);
        Assert.InRange(created ?? DateTime.MinValue, before, after);
        (created, updated) = await ScheduleTimesAsync(cs, "fresh");
        Assert.Equal(created, updated);
        Assert.InRange(created ?? DateTime.MinValue, before, after);
    }

    [SqlServerFact]
    public async Task A_widening_failure_rolls_back_prior_tables_and_indexes_and_releases_the_lock()
    {
        string cs = ConnectionString;
        await DropTablesAsync(cs);
        await SqlServerTableProbe.ExecuteAsync(cs, """
            CREATE TABLE dbo.grant_schedule (
              account_id NVARCHAR(200) NOT NULL, reward_id NVARCHAR(200) NOT NULL,
              next_available_utc DATETIME2 NOT NULL, PRIMARY KEY(account_id, reward_id));
            """);
        await SqlServerTableProbe.ExecuteAsync(cs,
            "INSERT INTO dbo.grant_schedule VALUES (N'legacy', N'daily', '2026-01-01T00:00:00');");
        await SqlServerTableProbe.ExecuteAsync(cs, """
            CREATE TRIGGER ke_wallet_bootstrap_refusal ON DATABASE FOR ALTER_TABLE AS
            BEGIN
                IF EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(128)') = N'grant_schedule'
                    THROW 51001, N'Fixture refuses the grant widening.', 1;
            END;
            """);
        try
        {
            SqlException failure = Assert.Throws<SqlException>(() => new SqlServerWalletStore(cs));
            Assert.Equal(51001, failure.Number);
            Assert.Equal(DBNull.Value, await SqlServerSchemaBootstrapProbe.ScalarAsync(cs,
                "SELECT OBJECT_ID(N'dbo.wallet_ledger', N'U');"));
            Assert.Equal(DBNull.Value, await SqlServerSchemaBootstrapProbe.ScalarAsync(cs,
                "SELECT OBJECT_ID(N'dbo.wallet_balance', N'U');"));
            Assert.Null(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "grant_schedule", "created_at"));
        }
        finally
        {
            await SqlServerTableProbe.ExecuteAsync(cs, "DROP TRIGGER ke_wallet_bootstrap_refusal ON DATABASE;");
        }

        var store = new SqlServerWalletStore(cs);
        Assert.Equal(new DateTimeOffset(LegacyTime, TimeSpan.Zero),
            await store.GetNextAvailableAsync(new AccountId("legacy"), "daily"));
        Assert.Equal(((DateTime?)null, (DateTime?)null), await ScheduleTimesAsync(cs, "legacy"));
    }

    private static Task DropTablesAsync(string cs) => SqlServerTableProbe.ExecuteAsync(cs, """
        DROP TABLE IF EXISTS dbo.wallet_ledger;
        DROP TABLE IF EXISTS dbo.wallet_balance;
        DROP TABLE IF EXISTS dbo.grant_schedule;
        """);

    private static Task<(DateTime?, DateTime?)> BalanceTimesAsync(string cs, string account)
        => SqlServerTableProbe.ReadPairAsync(cs,
            "SELECT created_at, updated_at FROM dbo.wallet_balance WHERE account_id = @a AND currency_id = N'shard';", ("@a", account));

    private static Task<(DateTime?, DateTime?)> ScheduleTimesAsync(string cs, string account)
        => SqlServerTableProbe.ReadPairAsync(cs,
            "SELECT created_at, updated_at FROM dbo.grant_schedule WHERE account_id = @a AND reward_id = N'daily';", ("@a", account));
}
