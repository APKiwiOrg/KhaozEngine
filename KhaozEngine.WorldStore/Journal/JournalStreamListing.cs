using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>One bounded page request for <see cref="IMutationJournalStreamListing.ListStreamsAsync"/>. Streams are
/// ordered by ordinal stream key. <see cref="KeyPrefix"/> keeps keys that start with it, compared ordinally and
/// case-sensitively. <see cref="AfterStreamKey"/> is the exclusive continuation, normally the previous page's
/// <see cref="JournalStreamPage.ContinuationKey"/>.</summary>
public sealed class JournalStreamQuery
{
    public const int MaximumStreamsPerPage = 1_000;

    public JournalStreamQuery(int maxStreams, string? keyPrefix = null, string? afterStreamKey = null)
    {
        JournalValidation.Positive(maxStreams, nameof(maxStreams));
        JournalValidation.Maximum(maxStreams, MaximumStreamsPerPage, nameof(maxStreams));
        MaxStreams = maxStreams;
        KeyPrefix = keyPrefix is null
            ? string.Empty
            : JournalValidation.Identity(keyPrefix, nameof(keyPrefix), JournalLimits.EngineMaximumStreamKeyCharacters, allowEmpty: true);
        AfterStreamKey = afterStreamKey is null ? null : JournalValidation.StreamKey(afterStreamKey, nameof(afterStreamKey));
    }

    public int MaxStreams { get; }
    public string KeyPrefix { get; }
    public string? AfterStreamKey { get; }

    public void Validate(JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Maximum;
        JournalValidation.Maximum(KeyPrefix.Length, limits.StreamKeyCharacters, nameof(KeyPrefix));
        if (AfterStreamKey is not null)
            JournalValidation.Maximum(AfterStreamKey.Length, limits.StreamKeyCharacters, nameof(AfterStreamKey));
    }

    internal bool Includes(string streamKey)
        => streamKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
            && (AfterStreamKey is null || StringComparer.Ordinal.Compare(streamKey, AfterStreamKey) > 0);
}

/// <summary>One listed stream: its key and its current head version.</summary>
public sealed class JournalStreamEntry
{
    public JournalStreamEntry(string streamKey, long headVersion)
    {
        StreamKey = JournalValidation.StreamKey(streamKey);
        JournalValidation.NonNegative(headVersion, nameof(headVersion));
        HeadVersion = headVersion;
    }

    public string StreamKey { get; }
    public long HeadVersion { get; }
}

/// <summary>One page of streams in ordinal stream-key order. <see cref="ContinuationKey"/> is the last listed key
/// when more streams follow and null when the listing is complete. Pages are keyset continuations, not one
/// snapshot: a stream created ahead of the continuation key appears on a later page and one created behind it
/// does not.</summary>
public sealed class JournalStreamPage
{
    private readonly ReadOnlyCollection<JournalStreamEntry> streams;

    public JournalStreamPage(IReadOnlyList<JournalStreamEntry> streams, string? continuationKey)
    {
        JournalStreamEntry[] copy = JournalValidation.CopyItems(streams, JournalStreamQuery.MaximumStreamsPerPage, nameof(streams));
        for (int i = 1; i < copy.Length; i++)
        {
            if (StringComparer.Ordinal.Compare(copy[i - 1].StreamKey, copy[i].StreamKey) >= 0)
                throw new ArgumentException("Streams must be unique and ordered by ordinal stream key.", nameof(streams));
        }
        if (continuationKey is not null && (copy.Length == 0 || !StringComparer.Ordinal.Equals(continuationKey, copy[^1].StreamKey)))
            throw new ArgumentException("A continuation key must be the last listed stream key.", nameof(continuationKey));
        this.streams = Array.AsReadOnly(copy);
        ContinuationKey = continuationKey;
    }

    public IReadOnlyList<JournalStreamEntry> Streams => streams;
    public string? ContinuationKey { get; }

    /// <summary>Builds a page from up to <c>MaxStreams + 1</c> ordered rows. The extra row only proves that more
    /// streams follow, so it is dropped and the last kept key becomes the continuation.</summary>
    internal static JournalStreamPage FromOrderedRows(List<JournalStreamEntry> rows, JournalStreamQuery query)
    {
        if (rows.Count <= query.MaxStreams) return new JournalStreamPage(rows, null);
        rows.RemoveRange(query.MaxStreams, rows.Count - query.MaxStreams);
        return new JournalStreamPage(rows, rows[^1].StreamKey);
    }

    /// <summary><see cref="FromOrderedRows"/> for rows a provider read back. A stored key that is not a valid
    /// stream key, or rows out of ordinal order, mean the store cannot be listed, so they fail as corrupt data for
    /// the whole store.</summary>
    internal static JournalStreamPage FromStoredRows(IReadOnlyList<(string StreamKey, long HeadVersion)> rows, JournalStreamQuery query)
    {
        try
        {
            var entries = new List<JournalStreamEntry>(rows.Count);
            foreach ((string streamKey, long headVersion) in rows) entries.Add(new JournalStreamEntry(streamKey, headVersion));
            return FromOrderedRows(entries, query);
        }
        catch (ArgumentException exception)
        {
            throw new JournalStoreException(
                JournalStoreFailureKind.CorruptData,
                JournalStoreFailureCertainty.CommittedDataUnreadable,
                JournalStoreFailureScope.WholeStore,
                null,
                "Stored journal stream keys are invalid or out of order.",
                exception);
        }
    }
}

/// <summary>The key range a provider scans for a <see cref="JournalStreamQuery"/>. Stream keys are ASCII from a
/// closed set, so every key with the prefix sorts at or after the prefix and before the prefix whose last character
/// is incremented. That bound lets an ordered key index seek straight to the range.</summary>
internal readonly record struct JournalStreamKeyRange(string Lower, bool LowerInclusive, string? UpperExclusive)
{
    internal static JournalStreamKeyRange For(JournalStreamQuery query)
    {
        string prefix = query.KeyPrefix;
        string? upper = prefix.Length == 0 ? null : prefix[..^1] + (char)(prefix[^1] + 1);
        return query.AfterStreamKey is string after && StringComparer.Ordinal.Compare(after, prefix) >= 0
            ? new JournalStreamKeyRange(after, false, upper)
            : new JournalStreamKeyRange(prefix, true, upper);
    }
}
