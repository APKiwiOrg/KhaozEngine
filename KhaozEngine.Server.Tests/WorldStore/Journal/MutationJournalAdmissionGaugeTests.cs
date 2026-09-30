using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalExecutorTestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class MutationJournalAdmissionGaugeTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Admission_and_queued_work_keep_the_first_uncommitted_age()
    {
        var store = new ControlledStore();
        var clock = new ManualTimeProvider(Start);
        MutationJournalExecutor executor = CreateExecutor(store, clock, workerCount: 1, operationCapacity: 4);
        JournalCommit first = Chained(Id(1), "gauges/player", 0);
        JournalCommit queued = Chained(Id(2), "gauges/player", 1);

        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(first).Status);
        ControlledStore.CommitCall firstCall = await store.TakeCommitAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(queued).Status);

        Assert.Equal(2, executor.Metrics.QueueOperations);
        Assert.Equal(2, executor.Metrics.AdmittedUncommittedOperations);
        Assert.Equal(TimeSpan.FromMinutes(2), executor.Metrics.OldestPendingAge);
        Assert.Equal(TimeSpan.FromMinutes(2), executor.Metrics.OldestAdmittedUncommittedAge);
        Assert.Equal(1, store.CommitCallCount);

        firstCall.Succeed(Applied(firstCall.Commit));
        executor.AcknowledgeCompletion(
            (await TakeCompletionAsync(executor)).OperationId,
            JournalCompletionAcknowledgement.Handled);
        ControlledStore.CommitCall queuedCall = await store.TakeCommitAsync();
        queuedCall.Succeed(Applied(queuedCall.Commit));
        executor.AcknowledgeCompletion(
            (await TakeCompletionAsync(executor)).OperationId,
            JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task Out_of_order_completion_and_acknowledgement_advance_each_oldest_gauge_independently()
    {
        var store = new ControlledStore();
        var clock = new ManualTimeProvider(Start);
        MutationJournalExecutor executor = CreateExecutor(store, clock, workerCount: 3, operationCapacity: 4);
        JournalCommit first = Commit(Id(1), "gauges/a");
        JournalCommit second = Commit(Id(2), "gauges/b");
        JournalCommit third = Commit(Id(3), "gauges/c");
        executor.Submit(first);
        clock.Advance(TimeSpan.FromMinutes(1));
        executor.Submit(second);
        clock.Advance(TimeSpan.FromMinutes(1));
        executor.Submit(third);
        ControlledStore.CommitCall[] calls =
        {
            await store.TakeCommitAsync(),
            await store.TakeCommitAsync(),
            await store.TakeCommitAsync(),
        };
        ControlledStore.CommitCall firstCall = calls.Single(call => call.Commit.Identity.OperationId == first.Identity.OperationId);
        ControlledStore.CommitCall secondCall = calls.Single(call => call.Commit.Identity.OperationId == second.Identity.OperationId);
        ControlledStore.CommitCall thirdCall = calls.Single(call => call.Commit.Identity.OperationId == third.Identity.OperationId);
        clock.Advance(TimeSpan.FromMinutes(1));

        secondCall.Succeed(Applied(secondCall.Commit));
        JournalCompletion secondCompletion = await TakeCompletionAsync(executor);
        Assert.Equal(second.Identity.OperationId, secondCompletion.OperationId);
        Assert.Equal(2, executor.Metrics.AdmittedUncommittedOperations);
        Assert.Equal(TimeSpan.FromMinutes(3), executor.Metrics.OldestAdmittedUncommittedAge);
        Assert.Equal(3, executor.Metrics.QueueOperations);
        Assert.Equal(TimeSpan.FromMinutes(3), executor.Metrics.OldestPendingAge);

        executor.AcknowledgeCompletion(secondCompletion.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.Equal(2, executor.Metrics.QueueOperations);
        Assert.Equal(TimeSpan.FromMinutes(3), executor.Metrics.OldestPendingAge);
        JournalShutdownResult shutdown = await executor.StopAsync(TimeSpan.Zero);
        Assert.Equal(
            new[] { first.Identity.OperationId, third.Identity.OperationId },
            shutdown.UnresolvedOperationIds);

        firstCall.Succeed(Applied(firstCall.Commit));
        JournalCompletion firstCompletion = await TakeCompletionAsync(executor);
        Assert.Equal(first.Identity.OperationId, firstCompletion.OperationId);
        Assert.Equal(1, executor.Metrics.AdmittedUncommittedOperations);
        Assert.Equal(TimeSpan.FromMinutes(1), executor.Metrics.OldestAdmittedUncommittedAge);
        Assert.Equal(2, executor.Metrics.QueueOperations);
        Assert.Equal(TimeSpan.FromMinutes(3), executor.Metrics.OldestPendingAge);

        executor.AcknowledgeCompletion(firstCompletion.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.Equal(1, executor.Metrics.QueueOperations);
        Assert.Equal(TimeSpan.FromMinutes(1), executor.Metrics.OldestPendingAge);

        thirdCall.Succeed(Applied(thirdCall.Commit));
        JournalCompletion thirdCompletion = await TakeCompletionAsync(executor);
        Assert.Equal(third.Identity.OperationId, thirdCompletion.OperationId);
        Assert.Equal(0, executor.Metrics.AdmittedUncommittedOperations);
        Assert.Equal(TimeSpan.Zero, executor.Metrics.OldestAdmittedUncommittedAge);
        executor.AcknowledgeCompletion(thirdCompletion.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.Equal(0, executor.Metrics.QueueOperations);
        Assert.Equal(TimeSpan.Zero, executor.Metrics.OldestPendingAge);
    }

    [Fact]
    public async Task Stop_cancellation_keeps_the_admitted_and_uncommitted_gauges()
    {
        var store = new ControlledStore();
        var clock = new ManualTimeProvider(Start);
        MutationJournalExecutor executor = CreateExecutor(store, clock, workerCount: 1, operationCapacity: 2);
        JournalCommit commit = Commit(Id(1), "gauges/cancelled");
        executor.Submit(commit);
        ControlledStore.CommitCall call = await store.TakeCommitAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.StopAsync(TimeSpan.FromMinutes(1), cancelled.Token));

        Assert.Equal(1, executor.Metrics.QueueOperations);
        Assert.Equal(1, executor.Metrics.AdmittedUncommittedOperations);
        Assert.Equal(TimeSpan.FromMinutes(5), executor.Metrics.OldestPendingAge);
        Assert.Equal(TimeSpan.FromMinutes(5), executor.Metrics.OldestAdmittedUncommittedAge);

        call.Succeed(Applied(call.Commit));
        executor.AcknowledgeCompletion(
            (await TakeCompletionAsync(executor)).OperationId,
            JournalCompletionAcknowledgement.Handled);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task Gauge_update_work_does_not_grow_with_the_admitted_count()
    {
        GaugeWork small = await MeasureGaugeWorkAsync(4);
        GaugeWork large = await MeasureGaugeWorkAsync(4_096);

        Assert.Equal(new GaugeWork(4, 4, 2), small);
        Assert.Equal(new GaugeWork(4_096, 4_096, 2), large);
    }

    private static async Task<GaugeWork> MeasureGaugeWorkAsync(int count)
    {
        var store = new ControlledStore();
        var clock = new ManualTimeProvider(Start);
        MutationJournalExecutor executor = CreateExecutor(store, clock, workerCount: 1, operationCapacity: count);
        for (int i = 0; i < count; i++)
            Assert.Equal(
                JournalSubmissionStatus.Accepted,
                executor.Submit(Chained(Id(i + 1), "gauges/scale", i)).Status);
        var measured = new GaugeWork(
            executor.Metrics.QueueOperations,
            executor.Metrics.AdmittedUncommittedOperations,
            executor.LastAdmissionGaugeInspectedOperations);

        ControlledStore.CommitCall first = await store.TakeCommitAsync();
        await executor.StopAsync(TimeSpan.Zero);
        first.Succeed(Applied(first.Commit));
        executor.AcknowledgeCompletion(
            (await TakeCompletionAsync(executor)).OperationId,
            JournalCompletionAcknowledgement.Handled);
        return measured;
    }

    private static MutationJournalExecutor CreateExecutor(
        IMutationJournalStore store,
        TimeProvider clock,
        int workerCount,
        int operationCapacity)
    {
        var options = new JournalExecutorOptions(
            workerCount,
            operationCapacity,
            10_000_000,
            streamQueueDepth: operationCapacity);
        return new MutationJournalExecutor(
            store,
            options,
            clock,
            static (_, _) => Task.CompletedTask,
            static () => 0.5);
    }

    private static Guid Id(int value) => new(value, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private readonly record struct GaugeWork(long Admitted, long Uncommitted, int Inspected);

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private long utcTicks = initial.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        internal void Advance(TimeSpan duration) => Interlocked.Add(ref utcTicks, duration.Ticks);
    }
}
