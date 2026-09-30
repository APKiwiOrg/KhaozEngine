using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.WorldStore.SqlServer;

public sealed partial class SqlServerMutationJournalStore : IMutationJournalStreamListing
{
    public async Task<JournalStreamPage> ListStreamsAsync(JournalStreamQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        query.Validate(limits);
        JournalStreamKeyRange range = JournalStreamKeyRange.For(query);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqlCommand command = CreateCommand(connection, null, ListStreamsSql(range));
            Add(command, "@limit", query.MaxStreams + 1);
            Add(command, "@streamLower", range.Lower);
            if (range.UpperExclusive is string upper) Add(command, "@streamUpper", upper);
            var rows = new List<JournalStoredStreamRow>();
            await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(new JournalStoredStreamRow(
                        reader.GetString(0),
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2).ToOffset(TimeSpan.Zero),
                        reader.GetFieldValue<DateTimeOffset>(3).ToOffset(TimeSpan.Zero)));
                }
            }
            return JournalStreamPage.FromStoredRows(rows, query);
        }
        catch (SqlException exception)
        {
            throw MapCommandFailure(
                exception.Number,
                exception,
                cancellationToken,
                Array.Empty<string>(),
                false,
                false,
                false);
        }
        catch (OperationCanceledException exception)
        {
            throw Cancelled(Array.Empty<string>(), false, false, true, exception);
        }
    }

    private static string ListStreamsSql(JournalStreamKeyRange range)
        => (range.LowerInclusive, range.UpperExclusive is null) switch
        {
            (true, true) => """
                SELECT TOP (@limit) stream_key, current_version, created_at_utc, updated_at_utc FROM dbo.journal_stream
                WHERE stream_key >= @streamLower
                ORDER BY stream_key;
                """,
            (true, false) => """
                SELECT TOP (@limit) stream_key, current_version, created_at_utc, updated_at_utc FROM dbo.journal_stream
                WHERE stream_key >= @streamLower AND stream_key < @streamUpper
                ORDER BY stream_key;
                """,
            (false, true) => """
                SELECT TOP (@limit) stream_key, current_version, created_at_utc, updated_at_utc FROM dbo.journal_stream
                WHERE stream_key > @streamLower
                ORDER BY stream_key;
                """,
            (false, false) => """
                SELECT TOP (@limit) stream_key, current_version, created_at_utc, updated_at_utc FROM dbo.journal_stream
                WHERE stream_key > @streamLower AND stream_key < @streamUpper
                ORDER BY stream_key;
                """,
        };
}
