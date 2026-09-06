using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.WorldStore.Journal;

public enum JournalCompletionAcknowledgement
{
    Handled,
    Quarantined,
}

public sealed class JournalCompletion
{
    private readonly ReadOnlyCollection<string> streamKeys;

    internal JournalCompletion(JournalCommit commit, JournalCommitResult? result, Exception? failure)
    {
        Commit = commit;
        Result = result;
        Failure = failure;
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
