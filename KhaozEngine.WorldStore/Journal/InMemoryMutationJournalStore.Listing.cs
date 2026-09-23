using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore.Journal;

public sealed partial class InMemoryMutationJournalStore : IMutationJournalStreamListing
{
    public Task<JournalStreamPage> ListStreamsAsync(JournalStreamQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        query.Validate(limits);
        lock (gate)
        {
            List<JournalStreamEntry> rows = state.Streams
                .Where(pair => query.Includes(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(query.MaxStreams + 1)
                .Select(pair => new JournalStreamEntry(pair.Key, pair.Value.HeadVersion))
                .ToList();
            return Task.FromResult(JournalStreamPage.FromOrderedRows(rows, query));
        }
    }
}
