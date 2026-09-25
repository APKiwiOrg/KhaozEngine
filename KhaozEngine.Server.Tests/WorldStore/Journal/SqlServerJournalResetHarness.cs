using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The connection string, the seed and the raw reads every SQL Server reset fact asserts with.
/// <para>
/// A reset wipes the WHOLE journal, not one prefix, so these facts run only against a database whose name carries
/// the <c>-journal-test-</c> marker, checked before anything is opened, and only inside the serialized
/// <c>SQL Server mutation journal</c> collection, so no other journal fact is running when one of them resets.
/// </para>
/// </summary>
internal static class SqlServerJournalResetHarness
{
    /// <summary>The gated connection string, refused unless it names a dedicated journal test database.</summary>
    internal static string ConnectionString
        => SqlServerJournalTestDatabase.RequireDedicatedTestDatabase(
            Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING"));

    /// <summary>A store that creates the version-two journal when the test database has none yet.</summary>
    internal static SqlServerMutationJournalStore OpenStore()
        => new(new SqlServerMutationJournalStoreOptions(ConnectionString));

    /// <summary>A seeded journal under a fresh prefix. Rows other facts left behind stay and are counted too.</summary>
    internal static Task<JournalResetTestSupport.Seeded> SeedAsync()
        => JournalResetTestSupport.SeedAsync(OpenStore(), $"journal-reset-test/{Guid.NewGuid():N}/");

    /// <summary>
    /// Every <c>dbo</c> table whose name starts <c>journal_</c>, except <c>journal_metadata</c>. Read from
    /// <c>sys.tables</c> rather than listed here, so a data table added to the schema is counted.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> DataTablesAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo') AND name LIKE N'journal[_]%' AND name <> @metadata
            ORDER BY name;
            """;
        command.Parameters.Add("@metadata", SqlDbType.NVarChar, 128).Value = JournalResetTestSupport.MetadataTable;
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>Every data table's row count, in ordinal table order.</summary>
    internal static async Task<KeyValuePair<string, long>[]> CountsAsync()
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (string table in await DataTablesAsync())
        {
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.{table};";
            counts[table] = (long)(await command.ExecuteScalarAsync())!;
        }

        return JournalResetTestSupport.Ordered(counts);
    }

    /// <summary>
    /// The whole metadata table as one line per row, every column, so a reset that changes any value in it, adds a
    /// row or removes one reads differently.
    /// </summary>
    internal static async Task<string> MetadataAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT metadata_key, schema_version, store_epoch, updated_at_utc
            FROM dbo.journal_metadata ORDER BY metadata_key;
            """;
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(FormattableString.Invariant(
                $"{reader.GetByte(0)}|{reader.GetInt32(1)}|{reader.GetGuid(2):D}|{reader.GetFieldValue<DateTimeOffset>(3):O}"));
        return string.Join('\n', rows);
    }

    /// <summary>The epoch the metadata row stores.</summary>
    internal static async Task<Guid> EpochAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT store_epoch FROM dbo.journal_metadata WHERE metadata_key = 1;";
        return (Guid)(await command.ExecuteScalarAsync())!;
    }
}
