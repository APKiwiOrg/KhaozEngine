using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

public sealed partial class SqliteMutationJournalStore : IMutationJournalStreamListing
{
    public async Task<JournalStreamPage> ListStreamsAsync(JournalStreamQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        query.Validate(limits);
        JournalStreamKeyRange range = JournalStreamKeyRange.For(query);
        using SqliteStoreLease lease = await db.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand command = CreateCommand(null, ListStreamsSql(range));
            Add(command, "$lower", range.Lower);
            if (range.UpperExclusive is string upper) Add(command, "$upper", upper);
            Add(command, "$limit", query.MaxStreams + 1);
            var rows = new List<(string StreamKey, long HeadVersion)>();
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    rows.Add((reader.GetString(0), reader.GetInt64(1)));
            }
            return JournalStreamPage.FromStoredRows(rows, query);
        }
        catch (SqliteException exception)
        {
            throw MapProviderFailure(exception, Array.Empty<string>(), transactionStarted: false, commitStarted: false, rollbackConfirmed: false);
        }
    }

    private static string ListStreamsSql(JournalStreamKeyRange range)
        => (range.LowerInclusive, range.UpperExclusive is null) switch
        {
            (true, true) => """
                SELECT stream_key, current_version FROM journal_stream
                WHERE stream_key >= $lower
                ORDER BY stream_key COLLATE BINARY LIMIT $limit;
                """,
            (true, false) => """
                SELECT stream_key, current_version FROM journal_stream
                WHERE stream_key >= $lower AND stream_key < $upper
                ORDER BY stream_key COLLATE BINARY LIMIT $limit;
                """,
            (false, true) => """
                SELECT stream_key, current_version FROM journal_stream
                WHERE stream_key > $lower
                ORDER BY stream_key COLLATE BINARY LIMIT $limit;
                """,
            (false, false) => """
                SELECT stream_key, current_version FROM journal_stream
                WHERE stream_key > $lower AND stream_key < $upper
                ORDER BY stream_key COLLATE BINARY LIMIT $limit;
                """,
        };
}
