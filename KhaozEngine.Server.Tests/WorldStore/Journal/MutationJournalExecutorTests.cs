using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalExecutorTestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class MutationJournalExecutorTests
{
    [Fact]
    public void ConstructionRequiresFinitePositiveCapacity()
    {
        var store = new ControlledStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => new MutationJournalExecutor(store, new JournalExecutorOptions(0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MutationJournalExecutor(store, new JournalExecutorOptions(1, 0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MutationJournalExecutor(store, new JournalExecutorOptions(1, 1, 0)));
        Assert.Throws<ArgumentNullException>(() => new MutationJournalExecutor(store, null!));
    }

    [Fact]
    public async Task AdmissionReservesEveryStreamAtomically()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, workerCount: 2, operationCapacity: 2);
        JournalCommit first = Commit(Guid.Parse("10000000-0000-0000-0000-000000000001"), "player/a", "player/b");
        JournalCommit overlaps = Commit(Guid.Parse("10000000-0000-0000-0000-000000000002"), "player/b", "player/c");
        JournalCommit disjoint = Commit(Guid.Parse("10000000-0000-0000-0000-000000000003"), "player/c");

        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(first).Status);
        Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(overlaps).Status);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(disjoint).Status);

        ControlledStore.CommitCall call1 = await store.TakeCommitAsync();
        ControlledStore.CommitCall call2 = await store.TakeCommitAsync();
        call1.Succeed(Applied(call1.Commit));
        call2.Succeed(Applied(call2.Commit));
        await WaitUntilAsync(() => executor.Metrics.UnacknowledgedCompletions == 2);
        AcknowledgeAll(executor);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task AdmissionRejectsCountAndBytePressureBeforeOwnershipTransfers()
    {
        var store = new ControlledStore();
        JournalCommit first = Commit(Guid.Parse("20000000-0000-0000-0000-000000000001"), new byte[] { 1, 2, 3 }, "player/a");
        MutationJournalExecutor countExecutor = CreateExecutor(store, operationCapacity: 1, byteCapacity: first.OwnedByteCount * 4L);

        Assert.Equal(JournalSubmissionStatus.Accepted, countExecutor.Submit(first).Status);
        JournalSubmission countRejected = countExecutor.Submit(Commit(Guid.Parse("20000000-0000-0000-0000-000000000002"), "player/b"));
        Assert.Equal(JournalSubmissionStatus.Backpressure, countRejected.Status);
        Assert.Equal(1, countExecutor.Metrics.QueueOperations);

        ControlledStore.CommitCall firstCall = await store.TakeCommitAsync();
        firstCall.Succeed(Applied(firstCall.Commit));
        await WaitUntilAsync(() => countExecutor.Metrics.UnacknowledgedCompletions == 1);
        AcknowledgeAll(countExecutor);
        await countExecutor.StopAsync(TimeSpan.Zero);

        var byteStore = new ControlledStore();
        MutationJournalExecutor byteExecutor = CreateExecutor(byteStore, operationCapacity: 2, byteCapacity: first.OwnedByteCount);
        Assert.Equal(JournalSubmissionStatus.Accepted, byteExecutor.Submit(first).Status);
        JournalSubmission byteRejected = byteExecutor.Submit(Commit(Guid.Parse("20000000-0000-0000-0000-000000000003"), "player/c"));
        Assert.Equal(JournalSubmissionStatus.Backpressure, byteRejected.Status);
        Assert.Equal(first.OwnedByteCount, byteExecutor.Metrics.QueueOwnedBytes);

        ControlledStore.CommitCall byteCall = await byteStore.TakeCommitAsync();
        byteCall.Succeed(Applied(byteCall.Commit));
        await WaitUntilAsync(() => byteExecutor.Metrics.UnacknowledgedCompletions == 1);
        AcknowledgeAll(byteExecutor);
        await byteExecutor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task TerminalCompletionRetainsCapacityAndReservationUntilAcknowledged()
    {
        var store = new ControlledStore();
        JournalCommit first = Commit(Guid.Parse("30000000-0000-0000-0000-000000000001"), "player/a");
        MutationJournalExecutor executor = CreateExecutor(store, operationCapacity: 1, byteCapacity: first.OwnedByteCount);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(first).Status);
        ControlledStore.CommitCall call = await store.TakeCommitAsync();
        call.Succeed(Applied(call.Commit));
        await WaitUntilAsync(() => executor.Metrics.UnacknowledgedCompletions == 1);

        Assert.Equal(1, executor.Metrics.QueueOperations);
        Assert.Equal(first.OwnedByteCount, executor.Metrics.QueueOwnedBytes);
        Assert.Equal(1, executor.Metrics.ReservedStreams);
        Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(Commit(Guid.Parse("30000000-0000-0000-0000-000000000002"), "player/a")).Status);
        Assert.Equal(JournalSubmissionStatus.Backpressure, executor.Submit(Commit(Guid.Parse("30000000-0000-0000-0000-000000000003"), "player/b")).Status);

        Assert.True(executor.TryDequeueCompletion(out JournalCompletion? completion));
        Assert.Equal(first.Identity.OperationId, completion.OperationId);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.Equal(0, executor.Metrics.QueueOperations);
        Assert.Equal(0, executor.Metrics.QueueOwnedBytes);
        Assert.Equal(0, executor.Metrics.ReservedStreams);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Commit(Guid.Parse("30000000-0000-0000-0000-000000000004"), "player/a")).Status);

        ControlledStore.CommitCall finalCall = await store.TakeCommitAsync();
        finalCall.Succeed(Applied(finalCall.Commit));
        await WaitUntilAsync(() => executor.Metrics.UnacknowledgedCompletions == 1);
        AcknowledgeAll(executor);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task QuarantinedAcknowledgementBlocksStreamsUntilAtomicRecovery()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store);
        JournalCommit first = Commit(Guid.Parse("40000000-0000-0000-0000-000000000001"), "player/a", "player/b");
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(first).Status);
        ControlledStore.CommitCall call = await store.TakeCommitAsync();
        call.Succeed(Applied(call.Commit));
        JournalCompletion completion = await TakeCompletionAsync(executor);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Quarantined);

        Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(Commit(Guid.Parse("40000000-0000-0000-0000-000000000002"), "player/a")).Status);
        Assert.Throws<InvalidOperationException>(() => executor.ReleaseQuarantine(new[] { "player/a", "player/c" }));
        Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(Commit(Guid.Parse("40000000-0000-0000-0000-000000000003"), "player/a")).Status);

        executor.ReleaseQuarantine(new[] { "player/a", "player/b" });
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Commit(Guid.Parse("40000000-0000-0000-0000-000000000004"), "player/a")).Status);
        ControlledStore.CommitCall recovered = await store.TakeCommitAsync();
        recovered.Succeed(Applied(recovered.Commit));
        await WaitUntilAsync(() => executor.Metrics.UnacknowledgedCompletions == 1);
        AcknowledgeAll(executor);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task DisjointStreamsProgressConcurrentlyThroughBoundedWorkers()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, workerCount: 2, operationCapacity: 2);

        executor.Submit(Commit(Guid.Parse("50000000-0000-0000-0000-000000000001"), "player/a"));
        executor.Submit(Commit(Guid.Parse("50000000-0000-0000-0000-000000000002"), "player/b"));

        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        ControlledStore.CommitCall second = await store.TakeCommitAsync();
        Assert.NotEqual(first.Commit.Identity.OperationId, second.Commit.Identity.OperationId);
        first.Succeed(Applied(first.Commit));
        second.Succeed(Applied(second.Commit));
        await WaitUntilAsync(() => executor.Metrics.UnacknowledgedCompletions == 2);
        AcknowledgeAll(executor);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task UnknownOutcomeResolvesThenRetriesTheSameFrozenRequestWhenAbsent()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, maximumTransientRetries: 0);
        executor.Submit(Commit(Guid.Parse("60000000-0000-0000-0000-000000000001"), "player/a"));

        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        first.Fail(StoreFailure(JournalStoreFailureKind.UnknownOutcome, JournalStoreFailureCertainty.Unknown, "player/a"));
        ControlledStore.ResolveCall resolve = await store.TakeResolveAsync();
        Assert.Same(first.Commit.Identity, resolve.Identity);
        resolve.Succeed(new JournalOperationResolution(JournalOperationResolutionStatus.NotFound));
        ControlledStore.CommitCall retry = await store.TakeCommitAsync();
        Assert.Same(first.Commit, retry.Commit);
        retry.Succeed(Applied(retry.Commit));

        JournalCompletion completion = await TakeCompletionAsync(executor);
        Assert.Equal(JournalCommitStatus.Applied, completion.Result!.Status);
        Assert.Equal(1, executor.Metrics.GetRetryCount(JournalStoreFailureKind.UnknownOutcome));
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task UnknownOutcomeResolutionCanReturnOriginalReplayWithoutAnotherCommit()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, maximumTransientRetries: 0);
        executor.Submit(Commit(Guid.Parse("61000000-0000-0000-0000-000000000001"), "player/a"));

        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        first.Fail(StoreFailure(JournalStoreFailureKind.Timeout, JournalStoreFailureCertainty.Unknown, "player/a"));
        ControlledStore.ResolveCall resolve = await store.TakeResolveAsync();
        resolve.Succeed(new JournalOperationResolution(JournalOperationResolutionStatus.Replayed, Receipt(first.Commit, isReplay: true)));

        JournalCompletion completion = await TakeCompletionAsync(executor);
        Assert.Equal(JournalCommitStatus.Replayed, completion.Result!.Status);
        Assert.Equal(1, executor.Metrics.Replayed);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task DefinitelyNotCommittedTransientFailureRetriesTheSameRequest()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, maximumTransientRetries: 1);
        executor.Submit(Commit(Guid.Parse("70000000-0000-0000-0000-000000000001"), "player/a"));

        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        first.Fail(StoreFailure(JournalStoreFailureKind.Deadlock, JournalStoreFailureCertainty.DefinitelyNotCommitted, "player/a"));
        ControlledStore.CommitCall retry = await store.TakeCommitAsync();
        Assert.Same(first.Commit, retry.Commit);
        retry.Succeed(Applied(retry.Commit));

        JournalCompletion completion = await TakeCompletionAsync(executor);
        Assert.Equal(1, executor.Metrics.GetRetryCount(JournalStoreFailureKind.Deadlock));
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task FirstRetryUsesInjectedClockDelayAndDeterministicJitter()
    {
        var store = new ControlledStore();
        var delay = new ControlledDelay();
        var options = new JournalExecutorOptions(
            workerCount: 1,
            operationCapacity: 1,
            ownedByteCapacity: 1_000_000,
            maximumTransientRetries: 1,
            initialRetryDelay: TimeSpan.FromMilliseconds(100),
            maximumRetryDelay: TimeSpan.FromSeconds(1));
        var executor = new MutationJournalExecutor(store, options, TimeProvider.System, delay.WaitAsync, static () => 0.25);
        JournalCommit submitted = Commit(Guid.Parse("71000000-0000-0000-0000-000000000001"), "player/a");
        executor.Submit(submitted);

        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        Assert.NotSame(submitted, first.Commit);
        first.Fail(StoreFailure(JournalStoreFailureKind.Unavailable, JournalStoreFailureCertainty.DefinitelyNotCommitted, "player/a"));
        ControlledDelay.DelayCall wait = await delay.TakeAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(75), wait.Duration);
        wait.Release();
        ControlledStore.CommitCall retry = await store.TakeCommitAsync();
        Assert.Same(first.Commit, retry.Commit);
        retry.Succeed(Applied(retry.Commit));

        JournalCompletion completion = await TakeCompletionAsync(executor);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task TransientRetryBudgetEndsInFatalCompletionWithoutLosingOwnership()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, maximumTransientRetries: 1);
        executor.Submit(Commit(Guid.Parse("72000000-0000-0000-0000-000000000001"), "player/a"));

        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        first.Fail(StoreFailure(JournalStoreFailureKind.Timeout, JournalStoreFailureCertainty.DefinitelyNotCommitted, "player/a"));
        ControlledStore.CommitCall retry = await store.TakeCommitAsync();
        JournalStoreException finalFailure = StoreFailure(
            JournalStoreFailureKind.Timeout,
            JournalStoreFailureCertainty.DefinitelyNotCommitted,
            "player/a");
        retry.Fail(finalFailure);

        JournalCompletion completion = await TakeCompletionAsync(executor);
        Assert.Same(finalFailure, completion.Failure);
        Assert.Equal(1, executor.Metrics.GetRetryCount(JournalStoreFailureKind.Timeout));
        Assert.Equal(1, executor.Metrics.QueueOperations);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        executor.ReleaseQuarantine(new[] { "player/a" });
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task NonRetryableResolutionFailureProducesOnlyFatalCompletion()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store);
        executor.Submit(Commit(Guid.Parse("73000000-0000-0000-0000-000000000001"), "player/a"));

        ControlledStore.CommitCall commit = await store.TakeCommitAsync();
        commit.Fail(StoreFailure(JournalStoreFailureKind.UnknownOutcome, JournalStoreFailureCertainty.Unknown, "player/a"));
        ControlledStore.ResolveCall resolve = await store.TakeResolveAsync();
        JournalStoreException schemaFailure = StoreFailure(
            JournalStoreFailureKind.SchemaMismatch,
            JournalStoreFailureCertainty.DefinitelyNotCommitted,
            "player/a");
        resolve.Fail(schemaFailure);

        JournalCompletion completion = await TakeCompletionAsync(executor);
        Assert.Same(schemaFailure, completion.Failure);
        Assert.Equal(0, executor.Metrics.OperationConflict);
        Assert.Equal(1, executor.Metrics.Failed);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        executor.ReleaseQuarantine(new[] { "player/a" });
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task CorruptCommittedDataDoesNotRetryAndProducesFatalCompletion()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, maximumTransientRetries: 5);
        executor.Submit(Commit(Guid.Parse("80000000-0000-0000-0000-000000000001"), "player/a"));

        ControlledStore.CommitCall call = await store.TakeCommitAsync();
        JournalStoreException failure = StoreFailure(
            JournalStoreFailureKind.CorruptData,
            JournalStoreFailureCertainty.CommittedDataUnreadable,
            "player/a");
        call.Fail(failure);

        JournalCompletion completion = await TakeCompletionAsync(executor);
        Assert.Same(failure, completion.Failure);
        Assert.True(completion.IsFatal);
        Assert.Equal(1, executor.Metrics.Failed);
        Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(Commit(Guid.Parse("80000000-0000-0000-0000-000000000002"), "player/a")).Status);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(Commit(Guid.Parse("80000000-0000-0000-0000-000000000003"), "player/a")).Status);
        executor.ReleaseQuarantine(new[] { "player/a" });
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task StopRejectsAdmissionAndReportsPendingAndUnacknowledgedInSubmissionOrder()
    {
        var store = new ControlledStore();
        var delay = new ControlledDelay();
        MutationJournalExecutor executor = CreateExecutor(store, workerCount: 2, operationCapacity: 2, delay: delay.WaitAsync);
        JournalCommit firstCommit = Commit(Guid.Parse("90000000-0000-0000-0000-000000000001"), "player/a");
        JournalCommit secondCommit = Commit(Guid.Parse("90000000-0000-0000-0000-000000000002"), "player/b");
        executor.Submit(firstCommit);
        executor.Submit(secondCommit);
        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        ControlledStore.CommitCall second = await store.TakeCommitAsync();
        first.Succeed(Applied(first.Commit));
        await WaitUntilAsync(() => executor.Metrics.UnacknowledgedCompletions == 1);

        Task<JournalShutdownResult> stopping = executor.StopAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(JournalSubmissionStatus.Stopping, executor.Submit(Commit(Guid.Parse("90000000-0000-0000-0000-000000000003"), "player/c")).Status);
        delay.ReleaseNext();
        JournalShutdownResult result = await stopping;

        Assert.Equal(new[] { firstCommit.Identity.OperationId, secondCommit.Identity.OperationId }, result.UnresolvedOperationIds);
        Assert.Equal(firstCommit.OwnedByteCount + secondCommit.OwnedByteCount, result.AdmittedByteCount);

        second.Succeed(Applied(second.Commit));
        await WaitUntilAsync(() => executor.Metrics.UnacknowledgedCompletions == 2);
        AcknowledgeAll(executor);
    }

    [Fact]
    public async Task StopDrainsAcceptedWorkButNeverAcknowledgesForHost()
    {
        var store = new ControlledStore();
        var delay = new ControlledDelay();
        MutationJournalExecutor executor = CreateExecutor(store, delay: delay.WaitAsync);
        JournalCommit commit = Commit(Guid.Parse("a0000000-0000-0000-0000-000000000001"), "player/a");
        executor.Submit(commit);
        ControlledStore.CommitCall call = await store.TakeCommitAsync();

        Task<JournalShutdownResult> stopping = executor.StopAsync(TimeSpan.FromMinutes(1));
        call.Succeed(Applied(call.Commit));
        JournalShutdownResult result = await stopping;

        Assert.Equal(new[] { commit.Identity.OperationId }, result.UnresolvedOperationIds);
        Assert.Equal(commit.OwnedByteCount, result.AdmittedByteCount);
        Assert.Equal(1, executor.Metrics.UnacknowledgedCompletions);
        AcknowledgeAll(executor);
    }

    [Fact]
    public async Task StopCancellationDoesNotCancelOrDiscardAdmittedCommit()
    {
        var store = new ControlledStore();
        var delay = new ControlledDelay();
        MutationJournalExecutor executor = CreateExecutor(store, delay: delay.WaitAsync);
        JournalCommit commit = Commit(Guid.Parse("b0000000-0000-0000-0000-000000000001"), "player/a");
        executor.Submit(commit);
        ControlledStore.CommitCall call = await store.TakeCommitAsync();
        Assert.False(call.CancellationToken.CanBeCanceled);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.StopAsync(TimeSpan.FromMinutes(1), cancelled.Token));
        call.Succeed(Applied(call.Commit));

        JournalCompletion completion = await TakeCompletionAsync(executor);
        Assert.Equal(commit.Identity.OperationId, completion.OperationId);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
    }

}
