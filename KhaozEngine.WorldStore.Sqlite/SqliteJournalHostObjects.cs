using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

/// <summary>
/// The host objects a reset's deletes would fire, read inside the reset's transaction before anything is deleted.
/// <para>
/// <b>The schema check never looks outside the journal's own objects.</b> It reads the tables named <c>journal_</c>,
/// the indexes named <c>ix_journal_</c> and the triggers named <c>trg_journal_</c>, so a game table's foreign key
/// into a journal table, and a game trigger on one, are both invisible to it. A key declared <c>ON DELETE CASCADE</c>
/// would lose the game's rows inside the reset, one declared <c>SET NULL</c> or <c>SET DEFAULT</c> would have its
/// column rewritten, and a trigger would run its own writes. None of that is in the reset's counts, and the reset
/// would commit.
/// </para>
/// <para>
/// A <c>NO ACTION</c> or <c>RESTRICT</c> key fires nothing. A game row that still references a stream makes the
/// delete itself fail, and the reset rolls back as a whole with <c>ConstraintViolation</c>. This is the content
/// catalog reset's host key refusal, with the trigger added because a <c>DELETE</c> fires triggers where the
/// catalog's <c>DROP TABLE</c> does not.
/// </para>
/// </summary>
internal static class SqliteJournalHostObjects
{
    /// <summary>
    /// Every foreign key a non-journal table declares into a table the reset empties, with its delete action.
    /// <c>DISTINCT</c> folds the one row per column a composite key reports. SQLite names are case insensitive,
    /// so both sides compare in lower case.
    /// </summary>
    private const string KeysSql = """
        SELECT DISTINCT m.name, k."table", k.on_delete
        FROM sqlite_master AS m, pragma_foreign_key_list(m.name) AS k
        WHERE m.type = 'table'
          AND lower(m.name) NOT IN ('journal_metadata', 'journal_stream', 'journal_event', 'journal_snapshot',
                                    'journal_projection', 'journal_operation', 'journal_operation_stream')
          AND lower(k."table") IN ('journal_stream', 'journal_event', 'journal_snapshot',
                                   'journal_projection', 'journal_operation', 'journal_operation_stream')
          AND upper(k.on_delete) NOT IN ('NO ACTION', 'RESTRICT')
        ORDER BY m.name COLLATE BINARY, k."table" COLLATE BINARY, k.on_delete COLLATE BINARY;
        """;

    /// <summary>
    /// Every trigger on a table the reset empties that is not one of the journal's own. The schema check owns
    /// every trigger named <c>trg_journal_</c> and refuses one it does not expect, so the name is the boundary.
    /// </summary>
    private const string TriggersSql = """
        SELECT name, tbl_name
        FROM sqlite_master
        WHERE type = 'trigger'
          AND lower(tbl_name) IN ('journal_stream', 'journal_event', 'journal_snapshot',
                                  'journal_projection', 'journal_operation', 'journal_operation_stream')
          AND lower(name) NOT LIKE 'trg_journal_%'
        ORDER BY name COLLATE BINARY;
        """;

    /// <summary>
    /// Refuses the reset when a host key's delete action or a host trigger would fire on its deletes. It runs
    /// inside the reset's transaction, under the write lock, and before the first delete, so a refusal leaves the
    /// file exactly as it stood.
    /// </summary>
    /// <param name="connection">The reset's open connection.</param>
    /// <param name="transaction">The reset's transaction.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <exception cref="JournalStoreException">A whole-store <c>SchemaMismatch</c> naming every key and trigger that would fire.</exception>
    internal static async Task RefuseFiringObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var firing = new List<string>();
        using (SqliteCommand command = Command(connection, transaction, KeysSql))
        using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                firing.Add(FormattableString.Invariant(
                    $"host table '{reader.GetString(0)}' has a foreign key into journal table '{reader.GetString(1)}' declared ON DELETE {reader.GetString(2)}"));
        }

        using (SqliteCommand command = Command(connection, transaction, TriggersSql))
        using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                firing.Add(FormattableString.Invariant(
                    $"trigger '{reader.GetString(0)}' on journal table '{reader.GetString(1)}' is not the journal's own"));
        }

        if (firing.Count > 0) throw Refusal(firing);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>The refusal, naming every key and trigger that would fire and what an operator can do about them.</summary>
    private static JournalStoreException Refusal(IReadOnlyList<string> firing)
        => new(
            JournalStoreFailureKind.SchemaMismatch,
            JournalStoreFailureCertainty.DefinitelyNotCommitted,
            JournalStoreFailureScope.WholeStore,
            null,
            FormattableString.Invariant(
                $"The SQLite journal cannot be reset: {string.Join(", and ", firing)}. The reset deletes every row of the journal's data tables, which would fire that action or trigger and delete or rewrite rows outside the journal. Nothing was deleted. Remove each such foreign key, or declare it ON DELETE NO ACTION and clear the host rows that reference the journal, and drop each such trigger, then run the reset again."));
}
