using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The foreign keys a HOST table declares into the catalog, read before a reset drops anything.
/// <para>
/// <b>SQLite's <c>DROP TABLE</c> deletes every row before it drops the table</b> whenever foreign keys are on,
/// and that hidden delete fires the delete action of every key pointing at the table from another one.
/// Deferring the foreign key check does not stop it: an action is not a violation, so there is nothing left for
/// the commit to refuse. A host key declared <c>ON DELETE CASCADE</c> would lose its rows, and one declared
/// <c>SET NULL</c> or <c>SET DEFAULT</c> would have its column rewritten, and the reset would commit.
/// </para>
/// <para>
/// A <c>NO ACTION</c> or <c>RESTRICT</c> key fires nothing. A host row that still references the catalog is a
/// violation instead, and the reset fails and rolls back as a whole, which leaves both the catalog and the
/// host exactly as they stood.
/// </para>
/// </summary>
internal static class SqliteCatalogHostKeys
{
    /// <summary>
    /// Every foreign key of every table in the file, as its host table, the table it references and its delete
    /// action. <c>DISTINCT</c> folds the one row per column a composite key reports.
    /// </summary>
    const string KeysSql = """
        SELECT DISTINCT m.name, k."table", k.on_delete
        FROM sqlite_master AS m, pragma_foreign_key_list(m.name) AS k
        WHERE m.type = 'table'
        ORDER BY m.name COLLATE BINARY, k."table" COLLATE BINARY, k.on_delete COLLATE BINARY;
        """;

    /// <summary>
    /// Refuses the reset when any table outside the schema's inventory holds a foreign key into a table inside
    /// it whose delete action would fire on the drop. It runs inside the reset's transaction and before any
    /// drop, so a refusal leaves the file exactly as it stood.
    /// </summary>
    /// <param name="connection">The reset's open connection.</param>
    /// <param name="transaction">The reset's transaction.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ContentAuthoringException">A host key would fire, under reason <see cref="ContentAuthoringException.HostForeignKeyReason"/>.</exception>
    internal static async Task RefuseFiringKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var firing = new List<string>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = KeysSql;
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string host = reader.GetString(0);
                string referenced = reader.GetString(1);
                string action = reader.GetString(2);
                if (!SqliteCatalogSchemaInventory.Tables.Contains(host)
                    && SqliteCatalogSchemaInventory.Tables.Contains(referenced)
                    && Fires(action))
                {
                    firing.Add(FormattableString.Invariant(
                        $"host table '{host}' has a foreign key into catalog table '{referenced}' declared ON DELETE {action}"));
                }
            }
        }

        if (firing.Count > 0)
        {
            throw Refusal(firing);
        }
    }

    /// <summary>Whether a delete action writes to the host table when the row it references is deleted.</summary>
    static bool Fires(string action)
        => string.Equals(action, "CASCADE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "SET NULL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "SET DEFAULT", StringComparison.OrdinalIgnoreCase);

    /// <summary>The refusal, naming every host key that would fire and the remedy an operator can take.</summary>
    static ContentAuthoringException Refusal(IReadOnlyList<string> firing)
        => new(
            FormattableString.Invariant(
                $"The SQLite content catalog cannot be reset: {string.Join(", and ", firing)}. SQLite's DROP TABLE deletes every row of the table first, which would fire that action and delete or rewrite the host's own rows. Nothing was dropped. Remove that foreign key, or declare it ON DELETE NO ACTION and clear the host rows that reference the catalog, then run the reset again."),
            default,
            0,
            ContentAuthoringException.HostForeignKeyReason);
}
