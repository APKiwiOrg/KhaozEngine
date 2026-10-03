using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The version 3 to version 4 migration, <c>catalog-v4-text-authoring</c>: the four empty text tables, the two
/// nullable columns and the version moved last, in ONE immediate transaction on the held connection. A
/// failure anywhere leaves a version 3 database, which the next open migrates again.
/// </summary>
internal static partial class SqliteCatalogSchemaValidation
{
    /// <summary>
    /// Validates a version 3 catalog against version 3's exact shape and answers the version it stands at.
    /// When the objects do not match and the version has moved to 4 since it was read, another host migrated
    /// in between, which is answered with 4 rather than refused. Otherwise the mismatch is refused.
    /// </summary>
    /// <param name="connection">The held connection.</param>
    /// <returns>3 when the database is still a valid version 3, or 4 when another host migrated it.</returns>
    static long ValidateVersionThreeObjects(SqliteConnection connection)
    {
        try
        {
            ValidateSchemaObjects(
                Read(() => ReadSchemaObjects(connection)), SqliteCatalogSchema.VersionThreeTables, 3);
            return 3;
        }
        catch (ContentAuthoringException)
            when (Read(() => ReadSchemaVersion(connection)) == SqliteCatalogSchema.CurrentVersion)
        {
            return SqliteCatalogSchema.CurrentVersion;
        }
    }

    /// <summary>
    /// Adds the version 4 tables and columns and moves the version to 4, in one immediate transaction. A
    /// version that is no longer 3 once the write lock is held was migrated by another host, and is left as
    /// it is.
    /// </summary>
    /// <param name="connection">The held connection.</param>
    static void MigrateVersionThree(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version FROM catalog_metadata WHERE metadata_key = 1;";
        if (command.ExecuteScalar() is not 3L)
        {
            transaction.Rollback();
            return;
        }

        command.CommandText = SqliteCatalogSchema.MigrateToVersionFour;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
