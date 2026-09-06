using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class InMemoryMutationJournalStoreTests : MutationJournalStoreConformance
{
    protected override MutationJournalStoreHarness CreateStore(TimeSpan? minimumRetryHorizon = null)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMutationJournalStore(
            JournalLimits.Maximum,
            minimumRetryHorizon ?? TimeSpan.FromHours(24),
            clock);
        return new MutationJournalStoreHarness(store, store, clock.GetUtcNow, clock.Advance, operationId =>
        {
            CorruptResultChecksum(store, operationId);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Commit_invokes_all_transaction_boundary_hooks()
    {
        var phases = new List<JournalTestHookPhase>();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryMutationJournalStore(JournalLimits.Maximum, TimeSpan.Zero, clock, new InMemoryJournalTestHook(phases.Add));

        await store.InitializeAsync(Initialization(Operation(1), "player/a"));
        phases.Clear();
        await store.CommitAsync(Commit(Operation(2), Mutation("player/a", 0, Event(1)), new[] { Projection("player/a", "bag", 2) }));

        Assert.Equal(
            new[]
            {
                JournalTestHookPhase.BeforeTransaction,
                JournalTestHookPhase.AfterOperationResolution,
                JournalTestHookPhase.AfterHeadValidation,
                JournalTestHookPhase.AfterEventWrites,
                JournalTestHookPhase.AfterProjectionWrites,
                JournalTestHookPhase.BeforeCommit,
                JournalTestHookPhase.AfterCommitBeforeResponse,
            },
            phases);
    }

    [Fact]
    public Task Failure_after_snapshot_write_keeps_prior_snapshot_and_tail()
        => FailedCompactionKeepsPriorState(JournalTestHookPhase.SnapshotWrittenBeforeVerification);

    [Fact]
    public Task Failure_before_event_prune_keeps_prior_snapshot_and_tail()
        => FailedCompactionKeepsPriorState(JournalTestHookPhase.SnapshotVerifiedBeforePrune);

    private async Task FailedCompactionKeepsPriorState(JournalTestHookPhase failurePhase)
    {
        bool armed = false;
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var hook = new InMemoryJournalTestHook(phase =>
        {
            if (armed && phase == failurePhase) throw new InjectedJournalFailure();
        });
        var store = new InMemoryMutationJournalStore(JournalLimits.Maximum, TimeSpan.Zero, clock, hook);
        await store.InitializeAsync(Initialization(Operation(1), "player/a", Bytes(10)));
        await store.CommitAsync(Commit(Operation(2), Mutation("player/a", 0, Event(1), Event(2))));
        armed = true;

        await Assert.ThrowsAsync<InjectedJournalFailure>(() => store.CompactAsync(new JournalCompaction("player/a", 1, "player.v2", 2, Bytes(20), 1)));

        Assert.Equal(Bytes(10), (await store.LoadSnapshotAsync("player/a"))!.Data.ToArray());
        JournalEventPage page = await store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024));
        Assert.Equal(2, page.Events.Count);
    }

    private static void CorruptResultChecksum(InMemoryMutationJournalStore store, Guid operationId)
    {
        FieldInfo stateField = typeof(InMemoryMutationJournalStore).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object state = stateField.GetValue(store)!;
        object operations = state.GetType().GetProperty("Operations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(state)!;
        object operation = operations.GetType().GetProperty("Item")!.GetValue(operations, new object[] { operationId })!;
        JournalCommitReceipt receipt = (JournalCommitReceipt)operation.GetType().GetProperty("Receipt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(operation)!;
        byte[] checksum = (byte[])typeof(JournalCommitReceipt).GetField("resultChecksum", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(receipt)!;
        checksum[0] ^= 0xff;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        internal ManualTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class InjectedJournalFailure : Exception;
}
