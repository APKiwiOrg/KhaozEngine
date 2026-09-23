using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The ONE place this provider says which tables belong to the catalog, shared by the reset, the validator
/// and the suites that assert on either.
/// <para>
/// <b>The set is DERIVED rather than transcribed.</b> <see cref="Tables"/> runs
/// <see cref="SqliteCatalogSchema.Tables"/> into a throwaway in-memory database and reads the names back, the
/// same way <c>SqliteCatalogSchemaValidation</c> builds its reference objects, so a table added to the schema
/// joins the inventory in the same commit as the DDL and nothing here has to be remembered.
/// </para>
/// <para>
/// <b>A name PATTERN cannot be the drop rule, and that is the whole reason this type exists.</b> In SQLite's
/// <c>LIKE</c> an underscore matches any single character, so <c>'catalog_%'</c> also matches a host's own
/// <c>catalogs</c> and <c>cataloguer</c>, and escaping the underscore still leaves a host table genuinely
/// named <c>catalog_overrides_by_host</c> indistinguishable from an engine table. Dropping by inventory means
/// the reset destroys exactly the tables this build declares and nothing else, whatever else the host keeps
/// in the same file.
/// </para>
/// <para>
/// <b>Version 1's set is derived the same way</b>, from <see cref="SqliteCatalogSchema.VersionOneTables"/>,
/// because a version 1 catalog is a WHOLE catalog the reset replaces rather than a partial one it refuses.
/// It is a subset of the current set, so the current set is still the one the drop intersects with.
/// </para>
/// </summary>
internal static class SqliteCatalogSchemaInventory
{
    /// <summary>
    /// The catalog table name rule for the statements that still read by PATTERN, with the underscore escaped
    /// so it means an underscore. This is a read filter and never a drop rule.
    /// </summary>
    internal const string TableNameClause = @"lower(name) LIKE 'catalog\_%' ESCAPE '\'";

    /// <summary>The named-index rule the validator reads its index objects with, underscores escaped.</summary>
    internal const string IndexNameClause =
        @"(lower(name) LIKE 'ix\_catalog\_%' ESCAPE '\' OR lower(name) LIKE 'ux\_catalog\_%' ESCAPE '\')";

    /// <summary>
    /// Every table this build's schema declares, read back from the script itself. Names compare case
    /// insensitively because SQLite resolves table names that way.
    /// </summary>
    internal static IReadOnlySet<string> Tables { get; } = Derive(SqliteCatalogSchema.Tables);

    /// <summary>Every table schema version 1 declared, read back from version 1's script the same way.</summary>
    internal static IReadOnlySet<string> VersionOneTables { get; } = Derive(SqliteCatalogSchema.VersionOneTables);

    /// <summary>
    /// The whole table set the given schema version declares, or null for a version this build holds no
    /// script for.
    /// </summary>
    /// <param name="version">The schema version a catalog's metadata row names.</param>
    internal static IReadOnlySet<string>? TablesAt(long version) => version switch
    {
        1 => VersionOneTables,
        SqliteCatalogSchema.CurrentVersion => Tables,
        _ => null,
    };

    /// <summary>
    /// Whether the standing tables are EXACTLY the set the given version declares, no more and no fewer,
    /// which is what makes a catalog whole rather than partial.
    /// </summary>
    /// <param name="standing">The inventory intersected with what stands, from <see cref="ReadExisting"/>.</param>
    /// <param name="version">The schema version the catalog's metadata row names.</param>
    internal static bool IsWhole(IReadOnlyList<string> standing, long version)
    {
        ArgumentNullException.ThrowIfNull(standing);

        if (TablesAt(version) is not { } declared || standing.Count != declared.Count)
        {
            return false;
        }

        for (int i = 0; i < standing.Count; i++)
        {
            if (!declared.Contains(standing[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The inventory INTERSECTED with what the database actually holds, in name order. An empty answer means
    /// the file carries no catalog table at all, and a short one means a partial catalog.
    /// </summary>
    /// <param name="connection">An open connection to the database.</param>
    /// <param name="transaction">The transaction to read under, or null.</param>
    internal static IReadOnlyList<string> ReadExisting(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name COLLATE BINARY;";
        var names = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(0);
            if (Tables.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    static IReadOnlySet<string> Derive(string script)
    {
        using var reference = new SqliteConnection("Data Source=:memory:");
        reference.Open();
        using (SqliteCommand create = reference.CreateCommand())
        {
            create.CommandText = script;
            create.ExecuteNonQuery();
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand command = reference.CreateCommand();

        // sqlite_sequence is SQLite's own, created behind the first AUTOINCREMENT column rather than by the
        // script, and it goes with the tables that own its rows. It is not the catalog's to drop.
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\';
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
