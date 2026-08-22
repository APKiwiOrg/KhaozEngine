using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using KhaozEngine.WorldStore;

namespace KhaozEngine.WorldStore.Sqlite;

/// <summary>Connection config for <see cref="SqliteWorldStore"/>. Inject the ADO.NET connection string
/// (for example <c>Data Source=world.db</c>); no other knobs. Pooling stays at the provider default while the
/// store is alive, and <see cref="SqliteWorldStore.Dispose"/> clears it so the file is not left open.</summary>
public sealed record SqliteWorldStoreOptions(string ConnectionString);

/// <summary>
/// SQLite-backed <see cref="IWorldStore"/> over Microsoft.Data.Sqlite. One <c>world_store(key, data, updated_at)</c>
/// table, bootstrapped on construction; upsert via <c>INSERT ... ON CONFLICT(key) DO UPDATE</c>; raw parameterized
/// async ADO.NET, no EF/ORM. Holds one open connection (so an in-memory <c>Data Source=:memory:</c> string keeps its
/// data) and serializes operations with a semaphore, so SQLite never sees concurrent commands on the shared
/// connection. Disposing closes that connection AND clears the provider's connection pool for it, so the file is
/// genuinely released rather than held open by a pooled handle. The embedded dev/test and single-node backend.
/// </summary>
public sealed class SqliteWorldStore : IWorldStore, IEnumerableWorldStore, IDisposable
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

    /// <summary>Overrides the interface default loop: every item is upserted inside one transaction on the shared
    /// connection (still gated by <see cref="gate"/>, so this never races a concurrent operation on that
    /// connection), so a batch of N dirty records costs one round trip and one fsync instead of N.</summary>
    public async Task SaveManyAsync(IReadOnlyList<(string Key, byte[] Data)> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using SqliteCommand cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO world_store (key, data, updated_at) VALUES ($k, $d, $t) " +
                    "ON CONFLICT(key) DO UPDATE SET data = excluded.data, updated_at = excluded.updated_at;";
                SqliteParameter pk = cmd.Parameters.Add("$k", SqliteType.Text);
                SqliteParameter pd = cmd.Parameters.Add("$d", SqliteType.Blob);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.Prepare();
                foreach ((string key, byte[] data) in items)
                {
                    pk.Value = key;
                    pd.Value = data;
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
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

    public async IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(
        string? keyPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = connection.CreateCommand();
            if (string.IsNullOrEmpty(keyPrefix))
            {
                cmd.CommandText = "SELECT key, updated_at, LENGTH(data) FROM world_store;";
            }
            else
            {
                cmd.CommandText = "SELECT key, updated_at, LENGTH(data) FROM world_store WHERE key LIKE $p ESCAPE '\\';";
                cmd.Parameters.AddWithValue("$p", LikeEscape(keyPrefix) + "%");
            }
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string key = reader.GetString(0);
                var updated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
                long size = reader.GetInt64(2);
                yield return new WorldStoreEntry(key, updated, size);
            }
        }
        finally { gate.Release(); }
    }

    // Escapes SQLite LIKE metacharacters so a supplied prefix matches literally (ESCAPE '\').
    internal static string LikeEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Closes the database, releasing the OS handle on the file rather than parking it in the provider's
    /// connection pool. <c>SqliteConnection.Dispose()</c> alone returns the native handle to that pool, which keeps
    /// the file open indefinitely: Windows then refuses to delete or exclusively open it, and POSIX unlinks it and
    /// hands the same live handle to the next store opened on that path, which serves the deleted database
    /// (#713). Clearing the pool first means this connection is never parked, and a pool clear cannot close a
    /// connection out from under a second live store on the same file, because an in-use connection is not idle in
    /// the pool and is only disposed when its own owner releases it.</summary>
    public void Dispose()
    {
        SqliteConnection.ClearPool(connection);
        connection.Dispose();
        gate.Dispose();
    }
}
