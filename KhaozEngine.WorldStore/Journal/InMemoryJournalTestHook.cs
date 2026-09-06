using System;

namespace KhaozEngine.WorldStore.Journal;

internal enum JournalTestHookPhase
{
    BeforeTransaction,
    AfterOperationResolution,
    AfterHeadValidation,
    AfterEventWrites,
    AfterProjectionWrites,
    BeforeCommit,
    AfterCommitBeforeResponse,
    SnapshotWrittenBeforeVerification,
    SnapshotVerifiedBeforePrune,
}

internal sealed class InMemoryJournalTestHook
{
    private readonly Action<JournalTestHookPhase> callback;

    internal InMemoryJournalTestHook(Action<JournalTestHookPhase> callback)
        => this.callback = callback ?? throw new ArgumentNullException(nameof(callback));

    internal void Invoke(JournalTestHookPhase phase) => callback(phase);
}
