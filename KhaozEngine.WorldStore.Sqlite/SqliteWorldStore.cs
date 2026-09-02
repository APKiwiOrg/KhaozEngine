using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using KhaozEngine.Sqlite;
using KhaozEngine.WorldStore;

namespace KhaozEngine.WorldStore.Sqlite;

/// <summary>Connection config for <see cref="SqliteWorldStore"/>. Inject the ADO.NET connection string
/// (for example <c>Data Source=world.db</c>); no other knobs. Pooling stays at the provider default while the
/// store is alive, and <see cref="SqliteWorldStore.Dispose"/> clears it so the file is not left open.</summary>
public sealed record SqliteWorldStoreOptions(string ConnectionString);

/// <summary>
/// SQLite-backed <see cref="IWorldStore"/> over Microsoft.Data.Sqlite. One <c>world_store(key, data, updated_at)</c>
/// table, bootstrapped on construction; upsert via <c>INSERT ... ON CONFLICT(key) DO UPDATE</c>; raw parameterized
/// async ADO.NET, no EF/ORM. The embedded dev/test and single-node backend.
/// <para>The connection, the operation gate and the dispose are <see cref="SqliteStoreConnection"/>'s, shared with
/// every other SQLite store in the engine. That is where the pool-clearing dispose lives, and why this store no
/// longer carries its own copy of it (#731). What stays here is the schema and the SQL.</para>
/// </summary>
public sealed class SqliteWorldStore : IWorldStore, IEnumerableWorldStore, IDisposable
{
    private const string Bootstrap =
        "CREATE TABLE IF NOT EXISTS world_store (" +
        "key TEXT PRIMARY KEY, data BLOB NOT NULL, updated_at INTEGER NOT NULL);";

    private readonly SqliteStoreConnection db;

    public SqliteWorldStore(SqliteWorldStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        db = new SqliteStoreConnection(options.ConnectionString, Bootstrap);
    }

    /// <summary>Convenience ctor taking the raw connection string.</summary>
    public SqliteWorldStore(string connectionString) : this(new SqliteWorldStoreOptions(connectionString)) { }

    public async Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        using SqliteStoreLease _ = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT data FROM world_store WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as byte[];   // absent row or DBNull -> null
    }

    public async Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        using SqliteStoreLease _ = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText =
            "INSERT INTO world_store (key, data, updated_at) VALUES ($k, $d, $t) " +
            "ON CONFLICT(key) DO UPDATE SET data = excluded.data, updated_at = excluded.updated_at;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$d", data);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Overrides the interface default loop: every item is upserted inside one transaction on the shared
    /// connection (still under the same lease every other operation takes, so this never races a concurrent
    /// operation on that connection), so a batch of N dirty records costs one round trip and one fsync instead of
    /// N.</summary>
    public async Task SaveManyAsync(IReadOnlyList<(string Key, byte[] Data)> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return;
        using SqliteStoreLease _ = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction tx = db.BeginTransaction();
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using SqliteCommand cmd = db.CreateCommand();
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

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        using SqliteStoreLease _ = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM world_store WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        using SqliteStoreLease _ = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM world_store WHERE key = $k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    public async IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(
        string? keyPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease _ = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand cmd = db.CreateCommand();
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

    // Escapes SQLite LIKE metacharacters so a supplied prefix matches literally (ESCAPE '\').
    internal static string LikeEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Closes the database through <see cref="SqliteStoreConnection"/>, which clears the provider's
    /// connection pool first so the OS handle on the file is genuinely released rather than parked (#713). Read that
    /// type for what a parked handle does to a store file on each platform.</summary>
    public void Dispose() => db.Dispose();
}
