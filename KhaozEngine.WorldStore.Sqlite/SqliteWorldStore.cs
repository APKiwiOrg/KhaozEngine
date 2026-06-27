using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

/// <summary>Connection config for <see cref="SqliteWorldStore"/>. Inject the ADO.NET connection string
/// (for example <c>Data Source=world.db</c>); no other knobs, pooling stays at the provider default.</summary>
public sealed record SqliteWorldStoreOptions(string ConnectionString);

/// <summary>
/// SQLite-backed <see cref="IWorldStore"/> over Microsoft.Data.Sqlite. One <c>world_store(key, data, updated_at)</c>
/// table, bootstrapped on construction; upsert via <c>INSERT ... ON CONFLICT(key) DO UPDATE</c>; raw parameterized
/// async ADO.NET, no EF/ORM. Holds one open connection (so an in-memory <c>Data Source=:memory:</c> string keeps its
/// data) and serializes operations with a semaphore, so SQLite never sees concurrent commands on the shared
/// connection. The embedded dev/test and single-node backend.
/// </summary>
public sealed class SqliteWorldStore : IWorldStore, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SqliteWorldStore(SqliteWorldStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connection = new SqliteConnection(options.ConnectionString);
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS world_store (" +
            "key TEXT PRIMARY KEY, data BLOB NOT NULL, updated_at INTEGER NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Convenience ctor taking the raw connection string.</summary>
    public SqliteWorldStore(string connectionString) : this(new SqliteWorldStoreOptions(connectionString)) { }

    public async Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT data FROM world_store WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result as byte[];   // absent row or DBNull -> null
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO world_store (key, data, updated_at) VALUES ($k, $d, $t) " +
                "ON CONFLICT(key) DO UPDATE SET data = excluded.data, updated_at = excluded.updated_at;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$d", data);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM world_store WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM world_store WHERE key = $k LIMIT 1;";
            cmd.Parameters.AddWithValue("$k", key);
            object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is not null;
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        connection.Dispose();
        gate.Dispose();
    }
}
