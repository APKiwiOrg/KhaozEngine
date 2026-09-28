using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>
/// Direct reads and writes against the database a SQL Server store under test uses, for the columns the store never
/// returns and the older table shapes it must widen. Shared by the world store and commerce SQL Server facts. Every
/// time here is the database clock, which is the clock those stores stamp with.
/// </summary>
internal static class SqlServerTableProbe
{
    /// <summary>
    /// Returns <paramref name="connectionString"/> when its <c>Initial Catalog</c> contains
    /// <paramref name="marker"/>, and throws otherwise. A fact that drops a store's table calls this first, so it
    /// cannot run against a database that was not created for it.
    /// </summary>
    public static string RequireMarkedDatabase(string? connectionString, string marker)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("A SQL Server connection string is required.");
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.Contains(marker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"This fact drops a store table, so Initial Catalog must contain '{marker}'.");
        return connectionString;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The first two columns of the single row <paramref name="sql"/> selects, each null when NULL.</summary>
    public static async Task<(DateTime? First, DateTime? Second)> ReadPairAsync(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The row the fact reads does not exist.");
        return (reader.IsDBNull(0) ? null : reader.GetDateTime(0), reader.IsDBNull(1) ? null : reader.GetDateTime(1));
    }

    /// <summary>Whether the <c>DATETIME2</c> column <c>dbo.<paramref name="table"/>.<paramref name="column"/></c>
    /// accepts NULL, or null when the table has no such <c>DATETIME2</c> column.</summary>
    public static async Task<bool?> ColumnIsNullableAsync(string connectionString, string table, string column)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.is_nullable FROM sys.columns AS c " +
            "WHERE c.object_id = OBJECT_ID(N'dbo.' + @table, N'U') AND c.name = @column AND TYPE_NAME(c.system_type_id) = N'datetime2';";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        object? result = await command.ExecuteScalarAsync();
        return result is bool nullable ? nullable : null;
    }

    /// <summary>The database clock, <c>SYSUTCDATETIME()</c>.</summary>
    public static async Task<DateTime> ServerNowAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SYSUTCDATETIME();";
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Returns once the database clock reads later than <paramref name="instant"/>, so the next write
    /// cannot share a stamp with the last.</summary>
    public static async Task WaitForServerClockPastAsync(string connectionString, DateTime instant)
    {
        while (await ServerNowAsync(connectionString) <= instant) await Task.Delay(1);
    }
}
