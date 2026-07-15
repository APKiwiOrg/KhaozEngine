using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

/// <summary>Connection config for <see cref="SqlServerWorldStore"/>. Inject the ADO.NET connection string
/// (for example an Azure SQL connection string); pooling stays at the provider default.</summary>
public sealed record SqlServerWorldStoreOptions(string ConnectionString);

/// <summary>
/// SQL Server / Azure SQL <see cref="IWorldStore"/> over Microsoft.Data.SqlClient. One
/// <c>world_store([key], data, updated_at)</c> table, bootstrapped on construction; upsert via
/// <c>MERGE ... WITH (HOLDLOCK)</c> (race-safe single-row upsert); raw parameterized async ADO.NET, no EF/ORM.
/// Opens a short-lived pooled connection per operation (SqlClient pools by connection string). The production
/// backend; identical contract to the SQLite dev/test backend.
/// </summary>
public sealed class SqlServerWorldStore : IWorldStore, IEnumerableWorldStore
{
    private readonly string connectionString;

    public SqlServerWorldStore(SqlServerWorldStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connectionString = options.ConnectionString
            ?? throw new ArgumentException("ConnectionString is required.", nameof(options));
        EnsureSchema();
    }

    /// <summary>Convenience ctor taking the raw connection string.</summary>
    public SqlServerWorldStore(string connectionString) : this(new SqlServerWorldStoreOptions(connectionString)) { }

    private void EnsureSchema()
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "IF OBJECT_ID(N'dbo.world_store', N'U') IS NULL " +
            "CREATE TABLE dbo.world_store (" +
            "[key] NVARCHAR(450) NOT NULL PRIMARY KEY, " +
            "data VARBINARY(MAX) NOT NULL, " +
            "updated_at DATETIME2 NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    public async Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM dbo.world_store WHERE [key] = @k;";
        cmd.Parameters.AddWithValue("@k", key);
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is byte[] b ? b : null;   // absent or DBNull -> null
    }

    public async Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "MERGE dbo.world_store WITH (HOLDLOCK) AS t " +
            "USING (SELECT @k AS [key]) AS s ON t.[key] = s.[key] " +
            "WHEN MATCHED THEN UPDATE SET data = @d, updated_at = SYSUTCDATETIME() " +
            "WHEN NOT MATCHED THEN INSERT ([key], data, updated_at) VALUES (@k, @d, SYSUTCDATETIME());";
        cmd.Parameters.AddWithValue("@k", key);
        SqlParameter d = cmd.Parameters.Add("@d", SqlDbType.VarBinary, -1);   // -1 = MAX
        d.Value = data;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Rows per MERGE statement: 2 SQL parameters per row (key + data), kept well under SQL Server's 2100-parameter-
    // per-statement ceiling so an arbitrarily large dirty pass never fails to build a valid command. A batch beyond
    // this many items is issued as multiple MERGE statements on the SAME connection + transaction, still one round
    // trip's worth of network setup instead of one connection open per record.
    private const int MergeChunkSize = 500;

    /// <summary>Overrides the interface default loop: opens ONE pooled connection for the whole batch (instead of
    /// one per record) and upserts every item via a multi-row <c>MERGE ... USING (VALUES ...)</c> statement inside a
    /// single transaction, so a batch of N dirty records costs one connection + a handful of round trips instead of
    /// N connections.</summary>
    public async Task SaveManyAsync(IReadOnlyList<(string Key, byte[] Data)> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return;
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int offset = 0; offset < items.Count; offset += MergeChunkSize)
            {
                int count = Math.Min(MergeChunkSize, items.Count - offset);
                await using SqlCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var sql = new StringBuilder();
                sql.Append("MERGE dbo.world_store WITH (HOLDLOCK) AS t USING (VALUES ");
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.Append('(').Append('@').Append('k').Append(i).Append(',').Append('@').Append('d').Append(i).Append(')');
                }
                sql.Append(") AS s([key], data) ON t.[key] = s.[key] " +
                    "WHEN MATCHED THEN UPDATE SET data = s.data, updated_at = SYSUTCDATETIME() " +
                    "WHEN NOT MATCHED THEN INSERT ([key], data, updated_at) VALUES (s.[key], s.data, SYSUTCDATETIME());");
                cmd.CommandText = sql.ToString();
                for (int i = 0; i < count; i++)
                {
                    (string key, byte[] data) = items[offset + i];
                    cmd.Parameters.AddWithValue($"@k{i}", key);
                    SqlParameter d = cmd.Parameters.Add($"@d{i}", SqlDbType.VarBinary, -1);   // -1 = MAX
                    d.Value = data;
                }
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.world_store WHERE [key] = @k;";
        cmd.Parameters.AddWithValue("@k", key);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.world_store WHERE [key] = @k;";
        cmd.Parameters.AddWithValue("@k", key);
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    public async IAsyncEnumerable<WorldStoreEntry> EnumerateAsync(
        string? keyPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        if (string.IsNullOrEmpty(keyPrefix))
        {
            cmd.CommandText = "SELECT [key], updated_at, DATALENGTH(data) FROM dbo.world_store;";
        }
        else
        {
            cmd.CommandText = "SELECT [key], updated_at, DATALENGTH(data) FROM dbo.world_store WHERE [key] LIKE @p ESCAPE '\\';";
            cmd.Parameters.AddWithValue("@p", LikeEscape(keyPrefix) + "%");
        }
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string key = reader.GetString(0);
            var updated = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), TimeSpan.Zero);
            long? size = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2));
            yield return new WorldStoreEntry(key, updated, size);
        }
    }

    // Escapes SQL Server LIKE metacharacters (incl. the '[' set marker) so a supplied prefix matches literally.
    internal static string LikeEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
