using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>Optional capability on an <see cref="IMutationJournalStore"/>: list stored streams in bounded pages.
/// A store that cannot list does not implement it. Consumers feature-detect with
/// <c>store is IMutationJournalStreamListing</c>.</summary>
public interface IMutationJournalStreamListing
{
    /// <summary>Reads one page of streams in ordinal stream-key order. Listing never writes.</summary>
    Task<JournalStreamPage> ListStreamsAsync(JournalStreamQuery query, CancellationToken cancellationToken = default);
}
