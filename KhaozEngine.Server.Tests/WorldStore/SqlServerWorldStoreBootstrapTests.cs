using System;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

[Collection("SQL Server mutation journal")]
public sealed class SqlServerWorldStoreBootstrapTests
{
    private const string LockResource = "KhaozEngine.WorldStore.Schema";
    private static readonly DateTime LegacyTime = new(2026, 1, 1);
    private static string ConnectionString => SqlServerTableProbe.RequireMarkedDatabase(
        Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING"), "-journal-test-");

    [SqlServerFact]
    public async Task Two_constructors_on_an_empty_database_wait_before_creating_the_table()
    {
        string cs = ConnectionString;
        await SqlServerTableProbe.ExecuteAsync(cs, "DROP TABLE IF EXISTS dbo.world_store;");
        SqlServerWorldStore[] stores = await SqlServerSchemaBootstrapProbe.ConstructWhileHeldAsync(
            cs, LockResource, value => new SqlServerWorldStore(value), async () =>
            {
                Assert.Equal(DBNull.Value, await SqlServerSchemaBootstrapProbe.ScalarAsync(cs,
                    "SELECT OBJECT_ID(N'dbo.world_store', N'U');"));
            });

        await AssertFreshRowAsync(cs, stores[0]);
        Assert.Equal(new byte[] { 1, 2 }, await stores[1].LoadAsync("fresh"));
    }

    [SqlServerFact]
    public async Task Two_constructors_on_a_legacy_table_wait_before_widening_and_preserve_rows()
    {
        string cs = ConnectionString;
        await SqlServerTableProbe.ExecuteAsync(cs, """
            DROP TABLE IF EXISTS dbo.world_store;
            CREATE TABLE dbo.world_store ([key] NVARCHAR(450) NOT NULL PRIMARY KEY,
              data VARBINARY(MAX) NOT NULL, updated_at DATETIME2 NOT NULL);
            """);
        await SqlServerTableProbe.ExecuteAsync(cs,
            "INSERT INTO dbo.world_store VALUES (N'legacy', 0x0102, '2026-01-01T00:00:00');");
        SqlServerWorldStore[] stores = await SqlServerSchemaBootstrapProbe.ConstructWhileHeldAsync(
            cs, LockResource, value => new SqlServerWorldStore(value), async () =>
            {
                Assert.Null(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "world_store", "created_at"));
                Assert.Equal(LegacyTime, await SqlServerSchemaBootstrapProbe.ScalarAsync(cs,
                    "SELECT updated_at FROM dbo.world_store WHERE [key] = N'legacy';"));
            });

        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "world_store", "created_at"));
        Assert.Equal(new byte[] { 1, 2 }, await stores[0].LoadAsync("legacy"));
        Assert.Equal(((DateTime?)null, (DateTime?)LegacyTime), await TimesAsync(cs, "legacy"));
        DateTime before = await SqlServerTableProbe.ServerNowAsync(cs);
        await stores[1].SaveAsync("legacy", new byte[] { 3 });
        DateTime after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? created, DateTime? updated) = await TimesAsync(cs, "legacy");
        Assert.Null(created);
        Assert.InRange(updated ?? DateTime.MinValue, before, after);
        await AssertFreshRowAsync(cs, stores[0]);
    }

    private static async Task AssertFreshRowAsync(string cs, SqlServerWorldStore store)
    {
        DateTime before = await SqlServerTableProbe.ServerNowAsync(cs);
        await store.SaveAsync("fresh", new byte[] { 1, 2 });
        DateTime after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? created, DateTime? updated) = await TimesAsync(cs, "fresh");
        Assert.Equal(created, updated);
        Assert.InRange(created ?? DateTime.MinValue, before, after);
    }

    [SqlServerFact]
    public async Task A_widening_failure_preserves_the_legacy_table_and_releases_the_lock()
    {
        string cs = ConnectionString;
        await SqlServerTableProbe.ExecuteAsync(cs, """
            DROP TABLE IF EXISTS dbo.world_store;
            CREATE TABLE dbo.world_store ([key] NVARCHAR(450) NOT NULL PRIMARY KEY,
              data VARBINARY(MAX) NOT NULL, updated_at DATETIME2 NOT NULL);
            """);
        await SqlServerTableProbe.ExecuteAsync(cs,
            "INSERT INTO dbo.world_store VALUES (N'legacy', 0x0102, '2026-01-01T00:00:00');");
        await SqlServerTableProbe.ExecuteAsync(cs, """
            CREATE TRIGGER ke_world_bootstrap_refusal ON DATABASE FOR ALTER_TABLE AS
            BEGIN
                IF EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(128)') = N'world_store'
                    THROW 51001, N'Fixture refuses the world widening.', 1;
            END;
            """);
        try
        {
            SqlException failure = Assert.Throws<SqlException>(() => new SqlServerWorldStore(cs));
            Assert.Equal(51001, failure.Number);
            Assert.Null(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "world_store", "created_at"));
        }
        finally
        {
            await SqlServerTableProbe.ExecuteAsync(cs, "DROP TRIGGER ke_world_bootstrap_refusal ON DATABASE;");
        }

        var store = new SqlServerWorldStore(cs);
        Assert.Equal(new byte[] { 1, 2 }, await store.LoadAsync("legacy"));
        Assert.Equal(((DateTime?)null, (DateTime?)LegacyTime), await TimesAsync(cs, "legacy"));
    }

    private static Task<(DateTime?, DateTime?)> TimesAsync(string cs, string key)
        => SqlServerTableProbe.ReadPairAsync(cs,
            "SELECT created_at, updated_at FROM dbo.world_store WHERE [key] = @k;", ("@k", key));
}
