using System;
using System.Collections.Generic;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The raw reads every SQLite reset fact asserts with, on their own connections so nothing the reset holds is
/// shared with the reading side.
/// </summary>
internal static class SqliteJournalResetHarness
{
    /// <summary>A stream key prefix the facts seed under.</summary>
    internal const string Prefix = "reset/";

    /// <summary>A seeded journal file, written through a store that is closed again before it returns.</summary>
    internal static async System.Threading.Tasks.Task<JournalResetTestSupport.Seeded> SeedAsync(
        SqliteJournalTestDatabase database,
        string path)
    {
        using SqliteMutationJournalStore store = database.Open(path);
        return await JournalResetTestSupport.SeedAsync(store, Prefix);
    }

    /// <summary>
    /// Every table the file holds whose name starts <c>journal_</c>, except <c>journal_metadata</c>. Read from
    /// <c>sqlite_master</c> rather than listed here, so a data table added to the schema is counted.
    /// </summary>
    internal static IReadOnlyList<string> DataTables(SqliteJournalTestDatabase database, string path)
    {
        using var connection = new SqliteConnection(database.ConnectionString(path));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name LIKE 'journal\_%' ESCAPE '\' AND name <> $metadata
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("$metadata", JournalResetTestSupport.MetadataTable);
        using SqliteDataReader reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>Every data table's row count, in ordinal table order.</summary>
    internal static KeyValuePair<string, long>[] Counts(SqliteJournalTestDatabase database, string path)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string table in DataTables(database, path))
            counts[table] = database.ScalarLong(path, $"SELECT COUNT(*) FROM \"{table}\";");
        return JournalResetTestSupport.Ordered(counts);
    }

    /// <summary>
    /// The whole metadata table as one line per row, every column, so a reset that changes any value in it, adds a
    /// row or removes one reads differently.
    /// </summary>
    internal static string Metadata(SqliteJournalTestDatabase database, string path)
    {
        using var connection = new SqliteConnection(database.ConnectionString(path));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT metadata_key, schema_version, store_epoch, updated_at_utc
            FROM journal_metadata ORDER BY metadata_key;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(FormattableString.Invariant(
                $"{reader.GetInt64(0)}|{reader.GetInt64(1)}|{reader.GetString(2)}|{reader.GetInt64(3)}"));
        return string.Join('\n', rows);
    }

    /// <summary>The epoch the metadata row stores, parsed the way the store parses it.</summary>
    internal static Guid Epoch(SqliteJournalTestDatabase database, string path)
        => Guid.Parse(database.ScalarText(path, "SELECT store_epoch FROM journal_metadata WHERE metadata_key = 1;"));
}
