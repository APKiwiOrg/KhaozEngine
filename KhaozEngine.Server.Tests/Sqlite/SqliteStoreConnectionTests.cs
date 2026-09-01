using System;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Sqlite;

/// <summary>
/// The shared lifecycle type itself, which is what the two engine stores (and any consumer store sitting its own
/// schema on top) now inherit their dispose discipline from instead of copying it. Deriving from the release
/// contract is the point: the handle-freeing behaviour is proven once, where it lives.
/// </summary>
public sealed class SqliteStoreConnectionFileLifetimeTests : SqliteStoreFileLifetimeContract<SqliteStoreConnection>
{
    internal const string Bootstrap = "CREATE TABLE IF NOT EXISTS probe (k TEXT PRIMARY KEY);";

    protected override string FileStem => "ke-sqlite-conn-life-";

    protected override SqliteStoreConnection Open(string connectionString) =>
        new(connectionString, Bootstrap);

    protected override async Task WriteAsync(SqliteStoreConnection store)
    {
        using SqliteStoreLease _ = await store.EnterAsync();
        using SqliteCommand cmd = store.CreateCommand();
        cmd.CommandText = "INSERT INTO probe (k) VALUES ('written');";
        await cmd.ExecuteNonQueryAsync();
    }

    protected override async Task<bool> HasTheWriteAsync(SqliteStoreConnection store)
    {
        using SqliteStoreLease _ = await store.EnterAsync();
        using SqliteCommand cmd = store.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM probe WHERE k = 'written';";
        return await cmd.ExecuteScalarAsync() is not null;
    }
}

/// <summary>The gate half of the lifecycle: one lease at a time, released by disposing it.</summary>
public sealed class SqliteStoreConnectionGateTests
{
    [Fact]
    public async Task The_gate_admits_one_lease_at_a_time()
    {
        using var db = new SqliteStoreConnection("Data Source=:memory:", SqliteStoreConnectionFileLifetimeTests.Bootstrap);
        SqliteStoreLease held = await db.EnterAsync();
        Task<SqliteStoreLease> queued = db.EnterAsync();
        Assert.False(queued.IsCompleted);
        held.Dispose();
        (await queued).Dispose();
    }

    [Fact]
    public void A_bootstrap_runs_once_at_construction()
    {
        using var db = new SqliteStoreConnection("Data Source=:memory:", SqliteStoreConnectionFileLifetimeTests.Bootstrap);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'probe';";
        Assert.Equal("probe", cmd.ExecuteScalar());
    }
}
