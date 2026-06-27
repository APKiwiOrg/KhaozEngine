using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class SqlServerWorldStore : IWorldStore
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
}
