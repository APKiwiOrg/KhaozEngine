using System;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>
/// One accepted operation as the executor tracks it: the frozen commit, its place in admission order, the
/// versions it claims on each of its streams, and the terminal state it has reached.
/// </summary>
internal sealed class AdmittedJournalOperation
{
    internal AdmittedJournalOperation(JournalCommit commit, long sequence, int ownedByteCount, DateTimeOffset admittedAtUtc)
    {
        Commit = commit;
        Sequence = sequence;
        OwnedByteCount = ownedByteCount;
        AdmittedAtUtc = admittedAtUtc;
        AdmittedAfterVersions = new long[commit.StreamMutations.Count];
    }

    internal JournalCommit Commit { get; }
    internal long Sequence { get; }
    internal int OwnedByteCount { get; }
    internal DateTimeOffset AdmittedAtUtc { get; }

    /// <summary>The version each touched stream reaches once this operation commits, aligned with <see cref="JournalCommit.StreamMutations"/>.</summary>
    internal long[] AdmittedAfterVersions { get; }

    internal JournalCompletion? Completion { get; set; }
    internal bool CompletionDequeued { get; set; }

    /// <summary>True once the operation has been handed to a worker, so it may already be at the store.</summary>
    internal bool Started { get; set; }

    /// <summary>True once its admitted writes have been taken back out of the view, by its own failure or by one ahead of it.</summary>
    internal bool Withdrawn { get; set; }

    /// <summary>True while the operation is eligible to be dispatched, before any of the terminal states.</summary>
    internal bool CanStart => Completion is null && !Started && !Withdrawn;
}
