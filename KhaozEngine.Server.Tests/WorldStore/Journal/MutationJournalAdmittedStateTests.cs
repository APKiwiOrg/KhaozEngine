using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalExecutorTestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class MutationJournalAdmittedStateTests
{
    private const string Stream = "admitted/player";
    private const string Other = "admitted/loot";

    [Fact]
    public async Task QueuedOperationsReplaceStreamBusyAndCommitInAdmissionOrderWithContiguousVersions()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, operationCapacity: 4);
        JournalCommit[] chain =
        {
            Chained(Id(1), Stream, 0),
            Chained(Id(2), Stream, 1),
            Chained(Id(3), Stream, 2),
        };

        foreach (JournalCommit commit in chain)
        {
            JournalSubmission submission = executor.Submit(commit);
            Assert.Equal(JournalSubmissionStatus.Accepted, submission.Status);
        }
        Assert.Equal(3, executor.Metrics.AdmittedUncommittedOperations);
        Assert.Equal(0, executor.Metrics.StreamBusy);

        for (int i = 0; i < chain.Length; i++)
        {
            ControlledStore.CommitCall call = await store.TakeCommitAsync();
            Assert.Equal(chain[i].Identity.OperationId, call.Commit.Identity.OperationId);
            Assert.Equal(i, call.Commit.StreamMutations[0].ExpectedVersion);
            Assert.Equal(i + 1, store.CommitCallCount);
            call.Succeed(Applied(call.Commit));
            JournalCompletion completion = await TakeCompletionAsync(executor);

            Assert.Equal(i + 1, store.CommitCallCount);
            executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        }

        Assert.Equal(0, executor.Metrics.AdmittedUncommittedOperations);
        Assert.Equal(3, executor.Metrics.AdmittedUncommittedPeakPerStream);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task TheAdmittedViewShowsUncommittedWritesThenTheCommittedBaseline()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store);
        executor.SeedCommitted(Stream, 0, new[] { Section("bag", 0, 1) });

        Assert.True(executor.TryGetAdmittedProjection(Stream, "bag", out JournalAdmittedSection? seeded));
        Assert.Equal((true, 0L, (byte)1), (seeded.IsCommitted, seeded.SourceVersion, seeded.Data.Span[0]));

        JournalSubmission submission = executor.Submit(Chained(Id(1), Stream, 0, new[] { Write("bag", 2) }));
        Assert.Equal(JournalSubmissionStatus.Accepted, submission.Status);
        Assert.Equal(1, Assert.Single(submission.AdmittedStreams).AdmittedHeadVersion);
        Assert.Equal(Stream, submission.AdmittedStreams[0].StreamKey);
        JournalProjectionSectionKey changed = Assert.Single(submission.ChangedSections);
        Assert.Equal((Stream, "bag"), (changed.StreamKey, changed.SectionName));

        Assert.True(executor.TryGetAdmittedProjection(Stream, "bag", out JournalAdmittedSection? admitted));
        Assert.Equal((false, 1L, (byte)2), (admitted.IsCommitted, admitted.SourceVersion, admitted.Data.Span[0]));
        Assert.True(executor.TryGetAdmittedStream(Stream, out JournalAdmittedStream? beforeCommit));
        Assert.Equal((0L, 1L, 1), (beforeCommit.CommittedVersion, beforeCommit.AdmittedHeadVersion, beforeCommit.AdmittedUncommittedOperations));

        ControlledStore.CommitCall call = await store.TakeCommitAsync();
        call.Succeed(Applied(call.Commit));
        JournalCompletion completion = await TakeCompletionAsync(executor);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);

        Assert.True(executor.TryGetAdmittedProjection(Stream, "bag", out JournalAdmittedSection? committed));
        Assert.Equal((true, 1L, (byte)2), (committed.IsCommitted, committed.SourceVersion, committed.Data.Span[0]));
        Assert.True(executor.TryGetAdmittedStream(Stream, out JournalAdmittedStream? afterCommit));
        Assert.Equal((1L, 1L, 0), (afterCommit.CommittedVersion, afterCommit.AdmittedHeadVersion, afterCommit.AdmittedUncommittedOperations));

        Assert.True(executor.ForgetStream(Stream));
        Assert.False(executor.TryGetAdmittedProjection(Stream, "bag", out _));
        Assert.False(executor.ForgetStream(Stream));
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task AForcedTerminalFailureRollsTheViewBackAndSupersedesTheDependantsWithoutAStoreCall()
    {
        int armed = 0;
        using var reachedCommit = new SemaphoreSlim(0);
        using var releaseCommit = new ManualResetEventSlim(false);
        var store = new InMemoryMutationJournalStore(
            JournalLimits.Maximum,
            TimeSpan.FromHours(24),
            TimeProvider.System,
            new InMemoryJournalTestHook(phase =>
            {
                if (Volatile.Read(ref armed) != 1 || phase != JournalTestHookPhase.BeforeCommit) return;
                reachedCommit.Release();
                Assert.True(releaseCommit.Wait(TimeSpan.FromSeconds(30)));
                throw StoreFailure(JournalStoreFailureKind.CorruptData, JournalStoreFailureCertainty.CommittedDataUnreadable, Stream);
            }));
        await store.InitializeAsync(new JournalInitialization(
            Identity(Id(9)),
            Stream,
            "player.v1",
            1,
            new byte[] { 1 },
            new[] { Write("bag", 1) },
            "result.v1",
            1,
            Array.Empty<byte>()));

        MutationJournalExecutor executor = CreateExecutor(store, operationCapacity: 4);
        JournalProjectionRead loaded = await store.ReadProjectionsAsync(new JournalProjectionQuery(Stream));
        executor.SeedCommitted(Stream, loaded.HeadVersion, loaded.Sections);

        JournalCommit failing = Chained(Id(1), Stream, 0, new[] { Write("bag", 2) });
        JournalCommit second = Chained(Id(2), Stream, 1, new[] { Write("bag", 3) });
        JournalCommit third = Chained(Id(3), Stream, 2, new[] { Write("worn", 4) });
        Volatile.Write(ref armed, 1);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(failing).Status);
        await reachedCommit.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(second).Status);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(third).Status);
        releaseCommit.Set();

        JournalCompletion fatal = await TakeCompletionAsync(executor);
        Volatile.Write(ref armed, 0);
        Assert.Equal(failing.Identity.OperationId, fatal.OperationId);
        Assert.Equal(JournalCompletionKind.Fatal, fatal.Kind);
        Assert.NotNull(fatal.Correction);
        JournalCorrection correction = fatal.Correction;
        Assert.Equal(failing.Identity.OperationId, correction.FailedOperationId);
        Assert.Equal(new[] { Stream }, correction.StreamKeys);
        Assert.Equal(new[] { "bag", "worn" }, correction.SectionsToResync.Select(section => section.SectionName));
        Assert.Equal(new[] { second.Identity.OperationId, third.Identity.OperationId }, correction.SupersededOperationIds);

        Assert.True(executor.TryGetAdmittedProjection(Stream, "bag", out JournalAdmittedSection? rolledBack));
        Assert.Equal((true, 0L, (byte)1), (rolledBack.IsCommitted, rolledBack.SourceVersion, rolledBack.Data.Span[0]));
        Assert.False(executor.TryGetAdmittedProjection(Stream, "worn", out _));
        Assert.True(executor.TryGetAdmittedStream(Stream, out JournalAdmittedStream? view));
        Assert.Equal((0L, 0L, 0), (view.CommittedVersion, view.AdmittedHeadVersion, view.AdmittedUncommittedOperations));

        Assert.Empty((await store.ReadEventsAsync(new JournalEventRead(Stream, 0, null, 128, 1024 * 1024))).Events);
        foreach (JournalCommit never in new[] { failing, second, third })
            Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(never.Identity)).Status);

        foreach (JournalCommit superseded in new[] { second, third })
        {
            JournalCompletion completion = await TakeCompletionAsync(executor);
            Assert.Equal(superseded.Identity.OperationId, completion.OperationId);
            Assert.Equal(JournalCompletionKind.SupersededByFailure, completion.Kind);
            Assert.True(completion.IsSuperseded);
            Assert.Equal(failing.Identity.OperationId, completion.SupersededBy);
            Assert.Null(completion.Result);
            Assert.Null(completion.Failure);
        }

        Assert.Equal((1L, 2L), (executor.Metrics.Corrections, executor.Metrics.Superseded));
        executor.AcknowledgeCompletion(fatal.OperationId, JournalCompletionAcknowledgement.Handled);
        executor.AcknowledgeCompletion(second.Identity.OperationId, JournalCompletionAcknowledgement.Handled);
        executor.AcknowledgeCompletion(third.Identity.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.Equal(0, executor.Metrics.QueueOperations);
        executor.ReleaseQuarantine(new[] { Stream });
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task AVersionConflictCorrectsTheChainAndTheStreamStaysUsable()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, operationCapacity: 4);
        executor.SeedCommitted(Stream, 0, new[] { Section("bag", 0, 1) });
        JournalCommit failing = Chained(Id(1), Stream, 0, new[] { Write("bag", 2) });
        JournalCommit dependant = Chained(Id(2), Stream, 1, new[] { Write("bag", 3) });
        executor.Submit(failing);
        executor.Submit(dependant);

        ControlledStore.CommitCall call = await store.TakeCommitAsync();
        call.Succeed(new JournalCommitResult(JournalCommitStatus.VersionConflict));

        JournalCompletion conflicted = await TakeCompletionAsync(executor);
        Assert.Equal(JournalCompletionKind.Committed, conflicted.Kind);
        Assert.Equal(JournalCommitStatus.VersionConflict, conflicted.Result!.Status);
        Assert.NotNull(conflicted.Correction);
        JournalCorrection correction = conflicted.Correction;
        Assert.Equal(dependant.Identity.OperationId, Assert.Single(correction.SupersededOperationIds));
        Assert.True(executor.TryGetAdmittedProjection(Stream, "bag", out JournalAdmittedSection? rolledBack));
        Assert.Equal((true, (byte)1), (rolledBack.IsCommitted, rolledBack.Data.Span[0]));

        JournalCompletion superseded = await TakeCompletionAsync(executor);
        executor.AcknowledgeCompletion(conflicted.OperationId, JournalCompletionAcknowledgement.Handled);
        executor.AcknowledgeCompletion(superseded.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.Equal(1, store.CommitCallCount);

        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(4), Stream, 0)).Status);
        ControlledStore.CommitCall retried = await store.TakeCommitAsync();
        retried.Succeed(Applied(retried.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task PresentAtCommitOperationsStillQueueAndStillBlockWhatIsBehindThem()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, operationCapacity: 4);
        JournalCommit trade = Chained(Id(1), Stream, 0, presentAtCommit: true);
        JournalCommit behind = Chained(Id(2), Stream, 1);
        Assert.True(trade.PresentAtCommit);
        Assert.False(behind.PresentAtCommit);

        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(trade).Status);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(behind).Status);
        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        Assert.True(first.Commit.PresentAtCommit);
        Assert.Equal(1, store.CommitCallCount);

        first.Succeed(Applied(first.Commit));
        JournalCompletion completion = await TakeCompletionAsync(executor);
        Assert.Equal(1, store.CommitCallCount);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);

        ControlledStore.CommitCall second = await store.TakeCommitAsync();
        Assert.Equal(behind.Identity.OperationId, second.Commit.Identity.OperationId);
        second.Succeed(Applied(second.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task ThePerStreamDepthCapAnswersBackpressureAndTheOptOutStillAnswersStreamBusy()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, operationCapacity: 8, streamQueueDepth: 2);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(1), Stream, 0)).Status);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(2), Stream, 1)).Status);
        Assert.Equal(JournalSubmissionStatus.Backpressure, executor.Submit(Chained(Id(3), Stream, 2)).Status);
        Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(Refusing(Id(4), Stream)).Status);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(6), Other, 0)).Status);
        Assert.Equal((1L, 1L), (executor.Metrics.Backpressure, executor.Metrics.StreamBusy));
        Assert.Equal(0, executor.Metrics.AdmissionVersionConflict);

        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        first.Succeed(Applied(first.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(7), Stream, 2)).Status);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task ExpectedVersionIsCheckedAgainstTheAdmittedHeadRatherThanTheCommittedVersion()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, operationCapacity: 4);
        executor.SeedCommitted(Stream, 4, Array.Empty<JournalProjectionSection>());

        Assert.Equal(JournalSubmissionStatus.VersionConflict, executor.Submit(Chained(Id(1), Stream, 0)).Status);
        Assert.Equal(1, executor.Metrics.AdmissionVersionConflict);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(2), Stream, 4)).Status);
        Assert.Equal(JournalSubmissionStatus.VersionConflict, executor.Submit(Chained(Id(3), Stream, 4)).Status);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(4), Stream, 5)).Status);
        Assert.Equal(2, executor.Metrics.AdmissionVersionConflict);

        ControlledStore.CommitCall call = await store.TakeCommitAsync();
        call.Succeed(Applied(call.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);
        ControlledStore.CommitCall next = await store.TakeCommitAsync();
        next.Succeed(Applied(next.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.True(executor.TryGetAdmittedStream(Stream, out JournalAdmittedStream? view));
        Assert.Equal((6L, 6L), (view.CommittedVersion, view.AdmittedHeadVersion));
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task AMultiStreamOperationQueuesOnEveryStreamItTouches()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, workerCount: 2, operationCapacity: 4);
        JournalCommit pair = Build(
            Id(1),
            new[] { JournalTestData.Mutation(Stream), JournalTestData.Mutation(Other) },
            Array.Empty<JournalProjectionWrite>(),
            Array.Empty<byte>());
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(pair).Status);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(2), Stream, 1)).Status);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(3), Other, 1)).Status);

        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        Assert.Equal(pair.Identity.OperationId, first.Commit.Identity.OperationId);
        Assert.Equal(1, store.CommitCallCount);
        first.Succeed(Applied(first.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);

        ControlledStore.CommitCall[] released = { await store.TakeCommitAsync(), await store.TakeCommitAsync() };
        Assert.Equal(
            new[] { Id(2), Id(3) }.OrderBy(id => id),
            released.Select(call => call.Commit.Identity.OperationId).OrderBy(id => id));
        foreach (ControlledStore.CommitCall call in released) call.Succeed(Applied(call.Commit));
        await WaitUntilAsync(() => executor.Metrics.UnacknowledgedCompletions == 2);
        AcknowledgeAll(executor);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task ShutdownReportsQueuedButNeverStartedOperationsAsUnresolved()
    {
        var store = new ControlledStore();
        var delay = new ControlledDelay();
        MutationJournalExecutor executor = CreateExecutor(store, operationCapacity: 4, delay: delay.WaitAsync);
        JournalCommit started = Chained(Id(1), Stream, 0);
        JournalCommit queued = Chained(Id(2), Stream, 1);
        executor.Submit(started);
        executor.Submit(queued);
        ControlledStore.CommitCall call = await store.TakeCommitAsync();

        JournalShutdownResult result = await executor.StopAsync(TimeSpan.Zero);
        Assert.Equal(new[] { started.Identity.OperationId, queued.Identity.OperationId }, result.UnresolvedOperationIds);
        Assert.Equal(JournalSubmissionStatus.Stopping, executor.Submit(Chained(Id(3), Other, 0)).Status);

        call.Succeed(Applied(call.Commit));
        JournalCompletion completion = await TakeCompletionAsync(executor);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.Equal(1, store.CommitCallCount);
    }

    private static Guid Id(int suffix) => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 8, checked((byte)suffix));

    private static JournalOperationIdentity Identity(Guid operationId)
        => new(operationId, "world/account", "player.initialize", new byte[] { 3 });

    private static JournalProjectionWrite Write(string section, byte value)
        => new(Stream, section, $"{section}.v1", 1, new[] { value });

    private static JournalProjectionSection Section(string section, long sourceVersion, byte value)
        => new(Stream, section, sourceVersion, $"{section}.v1", 1, new[] { value }, DateTimeOffset.UnixEpoch);
}
