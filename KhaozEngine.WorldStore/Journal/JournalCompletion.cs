using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

public enum JournalCompletionAcknowledgement
{
    Handled,
    Quarantined,
}

/// <summary>Which of the three terminal shapes a completion is.</summary>
public enum JournalCompletionKind
{
    /// <summary>The store answered. <see cref="JournalCompletion.Result"/> carries the outcome.</summary>
    Committed,

    /// <summary>The operation failed fatally. <see cref="JournalCompletion.Failure"/> carries the fault and the streams are quarantined.</summary>
    Fatal,

    /// <summary>
    /// The operation was queued behind an operation that failed terminally and was refused without ever reaching
    /// the store. It carries neither a result nor a failure. Nothing it described happened.
    /// </summary>
    SupersededByFailure,
}

public sealed class JournalCompletion
{
    private readonly ReadOnlyCollection<string> streamKeys;

    internal JournalCompletion(
        JournalCommit commit,
        JournalCommitResult? result,
        Exception? failure,
        JournalCorrection? correction = null,
        Guid? supersededBy = null)
    {
        Commit = commit;
        Result = result;
        Failure = failure;
        Correction = correction;
        SupersededBy = supersededBy;
        Kind = supersededBy is not null
            ? JournalCompletionKind.SupersededByFailure
            : failure is not null ? JournalCompletionKind.Fatal : JournalCompletionKind.Committed;
        var keys = new string[commit.StreamMutations.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = commit.StreamMutations[i].StreamKey;
        streamKeys = Array.AsReadOnly(keys);
    }

    public Guid OperationId => Commit.Identity.OperationId;
    public JournalCommit Commit { get; }
    public IReadOnlyList<string> StreamKeys => streamKeys;
    public JournalCommitResult? Result { get; }
    public Exception? Failure { get; }
    public bool IsFatal => Failure is not null;
    public JournalCompletionKind Kind { get; }

    /// <summary>
    /// Present when this operation's terminal failure withdrew admitted state. It names the streams rolled back,
    /// the sections to resync and the operations refused behind it.
    /// </summary>
    public JournalCorrection? Correction { get; }

    /// <summary>The failed operation that superseded this one, present only on a <see cref="JournalCompletionKind.SupersededByFailure"/> completion.</summary>
    public Guid? SupersededBy { get; }

    public bool IsSuperseded => Kind == JournalCompletionKind.SupersededByFailure;
}

public sealed class JournalShutdownResult
{
    private readonly ReadOnlyCollection<Guid> unresolvedOperationIds;

    internal JournalShutdownResult(Guid[] unresolvedOperationIds, long admittedByteCount)
    {
        this.unresolvedOperationIds = Array.AsReadOnly((Guid[])unresolvedOperationIds.Clone());
        AdmittedByteCount = admittedByteCount;
    }

    public IReadOnlyList<Guid> UnresolvedOperationIds => unresolvedOperationIds;
    public long AdmittedByteCount { get; }
}
