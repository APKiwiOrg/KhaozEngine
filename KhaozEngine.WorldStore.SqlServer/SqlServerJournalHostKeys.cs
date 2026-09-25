using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

/// <summary>
/// The foreign keys a HOST table declares into the journal, read inside the reset's transaction before anything is
/// deleted.
/// <para>
/// <b>The schema check reads only the keys the journal's own tables declare.</b> A game table's key into
/// <c>dbo.journal_stream</c> is invisible to it. Declared <c>ON DELETE CASCADE</c> it would lose the game's rows
/// inside the reset, and declared <c>SET NULL</c> or <c>SET DEFAULT</c> it would have its column rewritten. None of
/// that is in the reset's counts, and the reset would commit.
/// </para>
/// <para>
/// A <c>NO ACTION</c> key fires nothing. A game row that still references a stream makes the delete itself fail, and
/// the reset rolls back as a whole with <c>ConstraintViolation</c>. A trigger needs no read here: the schema check
/// compares every trigger on a journal table, whatever its name, so a foreign one is refused as <c>SchemaMismatch</c>
/// before the reset's transaction opens.
/// </para>
/// </summary>
internal static class SqlServerJournalHostKeys
{
    /// <summary>
    /// Every foreign key a table outside the journal declares into a table the reset empties, whose delete action is
    /// anything but <c>NO ACTION</c>. A disabled key is reported too, since enabling it again is one statement.
    /// </summary>
    private const string KeysSql = """
        SELECT SCHEMA_NAME(pt.schema_id), pt.name, fk.name, rt.name, fk.delete_referential_action_desc
        FROM sys.foreign_keys fk
        JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
        JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
        WHERE fk.delete_referential_action <> 0
          AND rt.schema_id = SCHEMA_ID(N'dbo')
          AND rt.name IN (N'journal_stream', N'journal_event', N'journal_snapshot',
                          N'journal_projection', N'journal_operation', N'journal_operation_stream')
          AND NOT (pt.schema_id = SCHEMA_ID(N'dbo')
                   AND pt.name IN (N'journal_metadata', N'journal_stream', N'journal_event', N'journal_snapshot',
                                   N'journal_projection', N'journal_operation', N'journal_operation_stream'))
        ORDER BY 1, 2, 3;
        """;

    /// <summary>
    /// Refuses the reset when any host key's delete action would fire on its deletes. It runs inside the reset's
    /// transaction, under the exclusive maintenance lock, and before the first delete, so a refusal leaves the journal
    /// exactly as it stood.
    /// </summary>
    /// <param name="transaction">The reset's transaction.</param>
    /// <param name="commandTimeoutSeconds">The store's command timeout.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="JournalStoreException">A whole-store <c>SchemaMismatch</c> naming every key that would fire.</exception>
    internal static async Task RefuseFiringKeysAsync(
        SqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        SqlConnection connection = transaction.Connection
            ?? throw new InvalidOperationException("SQL transaction has no connection.");
        var firing = new List<string>();
        await using (SqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = KeysSql;
            command.CommandTimeout = commandTimeoutSeconds;
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string action = reader.GetString(4).Replace('_', ' ');
                firing.Add(FormattableString.Invariant(
                    $"host table '{reader.GetString(0)}.{reader.GetString(1)}' has foreign key '{reader.GetString(2)}' into journal table '{reader.GetString(3)}' declared ON DELETE {action}"));
            }
        }

        if (firing.Count > 0) throw Refusal(firing);
    }

    /// <summary>The refusal, naming every host key that would fire and what an operator can do about it.</summary>
    private static JournalStoreException Refusal(IReadOnlyList<string> firing)
        => new(
            JournalStoreFailureKind.SchemaMismatch,
            JournalStoreFailureCertainty.DefinitelyNotCommitted,
            JournalStoreFailureScope.WholeStore,
            null,
            FormattableString.Invariant(
                $"The SQL Server journal cannot be reset: {string.Join(", and ", firing)}. The reset deletes every row of the journal's data tables, which would fire that action and delete or rewrite the host's own rows. Nothing was deleted. Remove each such foreign key, or declare it ON DELETE NO ACTION and clear the host rows that reference the journal, then run the reset again."));
}
