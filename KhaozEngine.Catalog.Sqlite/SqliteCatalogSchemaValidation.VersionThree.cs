using System;
using System.Collections.Generic;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The version 2 to version 3 migration: the row time columns added, the four publish-written tables
/// backfilled, and the version moved last, in ONE immediate transaction on the held connection.
/// </summary>
internal static partial class SqliteCatalogSchemaValidation
{
    /// <summary>
    /// Validates a version 2 catalog against the shapes its migration passes through and answers the version it
    /// stands at. When the objects do not match and the version has moved past 2 since it was read, another host
    /// migrated in between, through 3 to 4, which is answered with the moved version rather than refused.
    /// Otherwise the mismatch is refused.
    /// </summary>
    /// <param name="connection">The held connection.</param>
    /// <returns>2 when the database is still a valid version 2, or the version another host migrated it to.</returns>
    /// <remarks>Internal rather than private so a test can drive the migrated-in-between branch directly.</remarks>
    internal static long ValidateVersionTwoObjects(SqliteConnection connection)
    {
        long moved = 2;
        try
        {
            ValidateVersionTwoObjects(Read(() => ReadSchemaObjects(connection)));
            return 2;
        }
        catch (ContentAuthoringException) when ((moved = Read(() => ReadSchemaVersion(connection))) > 2)
        {
            return moved;
        }
    }

    /// <summary>
    /// A version 2 catalog whose every object matches a shape the migration passes through. A table may
    /// already carry some of its version 3 columns when the column adds were applied without the version move,
    /// and the migration then adds only what is missing. The adds are replayed in their own order into a
    /// throwaway database to find those shapes, so a column added out of that order is still a mismatch.
    /// </summary>
    /// <param name="actual">Every catalog object the open database holds.</param>
    static void ValidateVersionTwoObjects(IReadOnlyDictionary<string, string> actual)
    {
        IReadOnlyDictionary<string, HashSet<string>> shapes = ReadVersionTwoShapes();
        foreach ((string name, HashSet<string> accepted) in shapes)
        {
            if (!actual.TryGetValue(name, out string? actualSql))
            {
                throw Mismatch(FormattableString.Invariant($"missing object '{name}'"));
            }

            if (!accepted.Contains(actualSql))
            {
                throw Mismatch(FormattableString.Invariant(
                    $"carrying object '{name}', which does not match the shape version 2 declares"));
            }
        }

        foreach (string name in actual.Keys)
        {
            if (!shapes.ContainsKey(name))
            {
                throw Mismatch(FormattableString.Invariant(
                    $"carrying unexpected object '{name}', which version 2 does not declare"));
            }
        }
    }

    /// <summary>
    /// Adds each missing column, backfills and moves the version to 3, all in one immediate transaction. A
    /// version that is no longer 2 once the write lock is held was migrated by another host, and is left as it
    /// is, because a second backfill would overwrite a time a version 3 writer has stamped since.
    /// </summary>
    /// <param name="connection">The held connection.</param>
    static void MigrateVersionTwo(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version FROM catalog_metadata WHERE metadata_key = 1;";
        if (command.ExecuteScalar() is not 2L)
        {
            transaction.Rollback();
            return;
        }

        SqliteParameter table = command.Parameters.Add("$table", SqliteType.Text);
        SqliteParameter column = command.Parameters.Add("$column", SqliteType.Text);
        foreach ((string name, string added, string addColumn) in SqliteCatalogSchema.VersionThreeColumns)
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
            table.Value = name;
            column.Value = added;
            if (command.ExecuteScalar() is not 0L)
            {
                continue;
            }

            command.CommandText = addColumn;
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.CommandText = SqliteCatalogSchema.VersionThreeBackfill;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Every shape each version 2 object may stand in: the version 2 DDL, then the same objects after each
    /// column add in turn. The last of them is the version 3 shape.
    /// </summary>
    static IReadOnlyDictionary<string, HashSet<string>> ReadVersionTwoShapes()
    {
        using var reference = new SqliteConnection("Data Source=:memory:");
        reference.Open();
        var shapes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using SqliteCommand command = reference.CreateCommand();
        command.CommandText = SqliteCatalogSchema.VersionTwoTables;
        command.ExecuteNonQuery();
        Record(shapes, ReadSchemaObjects(reference));
        foreach ((_, _, string addColumn) in SqliteCatalogSchema.VersionThreeColumns)
        {
            command.CommandText = addColumn;
            command.ExecuteNonQuery();
            Record(shapes, ReadSchemaObjects(reference));
        }

        return shapes;
    }

    static void Record(Dictionary<string, HashSet<string>> shapes, IReadOnlyDictionary<string, string> objects)
    {
        foreach ((string name, string sql) in objects)
        {
            if (!shapes.TryGetValue(name, out HashSet<string>? accepted))
            {
                accepted = new HashSet<string>(StringComparer.Ordinal);
                shapes.Add(name, accepted);
            }

            accepted.Add(sql);
        }
    }
}
