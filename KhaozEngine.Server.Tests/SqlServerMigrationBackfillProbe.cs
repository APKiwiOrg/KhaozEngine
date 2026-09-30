using System;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Tests;

/// <summary>Counts updates of a seeded row, including backfills that write identical values.</summary>
internal sealed class SqlServerMigrationBackfillProbe : IDisposable
{
    private readonly string connectionString;
    private readonly string table = "test_schema_backfill_" + Guid.NewGuid().ToString("N");

    internal SqlServerMigrationBackfillProbe(string connectionString, string migratedTable, string rowFilter)
    {
        this.connectionString = connectionString;
        try
        {
            Execute($"CREATE TABLE dbo.{table} (backfill_update int NOT NULL);");
            Execute($"""
                CREATE TRIGGER dbo.{table}_trigger ON dbo.{migratedTable} AFTER UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.{table}(backfill_update)
                    SELECT 1 FROM inserted WHERE {rowFilter};
                END;
                """);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int Updates
    {
        get
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using SqlCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM dbo.{table};";
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    internal string TriggerName => table + "_trigger";

    public void Dispose() => Execute($"""
        DROP TRIGGER IF EXISTS dbo.{table}_trigger;
        DROP TABLE IF EXISTS dbo.{table};
        """);

    private void Execute(string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
