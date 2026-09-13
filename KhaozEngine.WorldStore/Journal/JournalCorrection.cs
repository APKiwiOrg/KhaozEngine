using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>
/// What one terminally failed operation took back out of the admitted view. The consumer resyncs the named
/// sections from the committed record and tells the players behind the superseded operations that nothing
/// happened.
/// </summary>
public sealed class JournalCorrection
{
    private readonly ReadOnlyCollection<string> streamKeys;
    private readonly ReadOnlyCollection<JournalProjectionSectionKey> sectionsToResync;
    private readonly ReadOnlyCollection<Guid> supersededOperationIds;

    internal JournalCorrection(
        Guid failedOperationId,
        string[] streamKeys,
        JournalProjectionSectionKey[] sectionsToResync,
        Guid[] supersededOperationIds)
    {
        FailedOperationId = failedOperationId;
        this.streamKeys = Array.AsReadOnly(streamKeys);
        this.sectionsToResync = Array.AsReadOnly(sectionsToResync);
        this.supersededOperationIds = Array.AsReadOnly(supersededOperationIds);
    }

    /// <summary>The operation whose terminal failure caused the rollback.</summary>
    public Guid FailedOperationId { get; }

    /// <summary>Every stream rolled back to its committed baseline, ordinal ascending.</summary>
    public IReadOnlyList<string> StreamKeys => streamKeys;

    /// <summary>Every projection section whose admitted bytes were withdrawn, ordinal ascending by stream then section.</summary>
    public IReadOnlyList<JournalProjectionSectionKey> SectionsToResync => sectionsToResync;

    /// <summary>Every operation refused behind the failure, in admission order. Each one also receives its own superseded completion.</summary>
    public IReadOnlyList<Guid> SupersededOperationIds => supersededOperationIds;
}
