using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

/// <summary>
/// The version 2 to 3 migration. Version 3 adds a nullable <c>created_at_utc</c> in Unix milliseconds to
/// <c>journal_metadata</c>, <c>journal_stream</c>, <c>journal_operation_stream</c> and <c>journal_projection</c>.
/// A legacy row takes a time only where the database proves it and stays NULL otherwise.
/// </summary>
internal static partial class SqliteJournalSchema
{
    private static readonly (string Table, string AddColumn)[] VersionThreeColumns =
    {
        ("journal_metadata", "ALTER TABLE journal_metadata ADD COLUMN created_at_utc INTEGER;"),
        ("journal_stream", "ALTER TABLE journal_stream ADD COLUMN created_at_utc INTEGER;"),
        ("journal_operation_stream", "ALTER TABLE journal_operation_stream ADD COLUMN created_at_utc INTEGER;"),
        ("journal_projection", "ALTER TABLE journal_projection ADD COLUMN created_at_utc INTEGER;"),
    };

    /// <summary>
    /// A stream's creation time is its initialization snapshot's, and only while that snapshot has never been
    /// replaced (<c>through_version = 0</c>). An operation is never a source, because a later commit with no events
    /// has the same shape as an initialization. A receipt stream range was written with its operation, so it takes
    /// the operation's commit time. The version moves last.
    /// </summary>
    private const string VersionThreeBackfill = """
        UPDATE journal_stream SET created_at_utc =
            (SELECT s.created_at_utc FROM journal_snapshot s
             WHERE s.stream_key = journal_stream.stream_key AND s.through_version = 0);
        UPDATE journal_operation_stream SET created_at_utc =
            (SELECT o.committed_at_utc FROM journal_operation o
             WHERE o.operation_id = journal_operation_stream.operation_id);
        UPDATE journal_metadata
        SET schema_version = 3,
            updated_at_utc = CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)
        WHERE metadata_key = 1 AND schema_version = 2;
        """;

    internal static string VersionTwoSchemaSqlForTest => VersionTwoTables;

    internal static IReadOnlyList<string> VersionThreeColumnAddsSqlForTest
        => Array.ConvertAll(VersionThreeColumns, value => value.AddColumn);

    private static string VersionTwoTables => CreateVersionTwoTables(Tables);

    private static string CreateVersionTwoTables(string tables)
    {
        bool usesCrLf = tables.Contains("\r\n", StringComparison.Ordinal);
        string normalized = tables.ReplaceLineEndings("\n");
        string versionTwo = normalized
            .Replace(",\n    created_at_utc INTEGER);", ");", StringComparison.Ordinal)
            .Replace("\n    created_at_utc INTEGER,", string.Empty, StringComparison.Ordinal)
            .Replace("""
INSERT OR IGNORE INTO journal_metadata(metadata_key, schema_version, store_epoch, updated_at_utc, created_at_utc)
VALUES (1, 3, lower(hex(randomblob(16))), CAST(strftime('%s', 'now') AS INTEGER) * 1000, CAST(strftime('%s', 'now') AS INTEGER) * 1000);
""".ReplaceLineEndings("\n"), """
INSERT OR IGNORE INTO journal_metadata(metadata_key, schema_version, store_epoch, updated_at_utc)
VALUES (1, 2, lower(hex(randomblob(16))), CAST(strftime('%s', 'now') AS INTEGER) * 1000);
""".ReplaceLineEndings("\n"), StringComparison.Ordinal);
        return usesCrLf ? versionTwo.ReplaceLineEndings("\r\n") : versionTwo;
    }

    /// <summary>
    /// A version 2 journal whose tables each match either their version 2 shape or their version 3 shape. A table
    /// may already carry its version 3 column when the column adds were applied without the version move, and the
    /// migration then adds only what is missing.
    /// </summary>
    private static void ValidateVersionTwoObjects(IReadOnlyDictionary<string, string> actual)
    {
        IReadOnlyDictionary<string, string> versionTwo = ReadReferenceObjects(VersionTwoTables);
        IReadOnlyDictionary<string, string> versionThree = ReadReferenceObjects(Tables);
        if (actual.Count != versionTwo.Count)
            throw Mismatch("partial or contains unexpected journal objects");
        foreach ((string name, string expectedSql) in versionTwo)
        {
            if (!actual.TryGetValue(name, out string? actualSql)
                || !(StringComparer.Ordinal.Equals(actualSql, expectedSql)
                    || (versionThree.TryGetValue(name, out string? migratedSql) && StringComparer.Ordinal.Equals(actualSql, migratedSql))))
                throw Mismatch($"version 2 object '{name}' does not match the supported shape");
        }
    }

    /// <summary>
    /// Adds each missing column, backfills and moves the version to 3 in one immediate transaction. A version that is
    /// no longer 2 once the write lock is held was migrated by another connection, which is left as it is.
    /// </summary>
    private static void MigrateVersionTwo(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version FROM journal_metadata WHERE metadata_key = 1;";
        if (command.ExecuteScalar() is not 2L)
        {
            transaction.Rollback();
            return;
        }

        SqliteParameter table = command.Parameters.Add("$table", SqliteType.Text);
        foreach ((string name, string addColumn) in VersionThreeColumns)
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = 'created_at_utc';";
            table.Value = name;
            if (command.ExecuteScalar() is not 0L) continue;
            command.CommandText = addColumn;
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.CommandText = VersionThreeBackfill;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
