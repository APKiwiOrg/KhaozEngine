using System;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalExecutorTestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class MutationJournalForgetTests
{
    private const string Stream = "forget/player";
    private const string Other = "forget/loot";

    [Fact]
    public async Task EmptyStreamIsForgottenImmediatelyAndUnknownStreamIsHarmless()
    {
        MutationJournalExecutor executor = CreateExecutor(new ControlledStore());
        Assert.Equal(JournalStreamForgetStatus.Unknown, executor.RequestForgetStream(Stream));
        Assert.False(executor.ForgetStream(Stream));
        Seed(executor, Stream, 1);
        Seed(executor, Other, 9);

        Assert.Equal(JournalStreamForgetStatus.Forgotten, executor.RequestForgetStream(Stream));
        Assert.False(executor.TryGetAdmittedStream(Stream, out _));
        Assert.False(executor.TryGetAdmittedProjection(Stream, "bag", out _));
        Assert.False(executor.ForgetStream(Stream));
        AssertView(executor, Other, 0, 0, 0, 9, true);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task RunningAndQueuedWritesSurviveForgettingUntilTheFinalAcknowledgement()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store);
        Seed(executor, Stream, 1);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(1), Stream, 0, new[] { Write(Stream, 2) })).Status);
        ControlledStore.CommitCall first = await TakeCommit(store);
        Assert.False(executor.ForgetStream(Stream));
        AssertView(executor, Stream, 0, 1, 1, 2, false);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(2), Stream, 1, new[] { Write(Stream, 3) })).Status);

        Assert.Equal(JournalStreamForgetStatus.Deferred, executor.RequestForgetStream(Stream));
        AssertView(executor, Stream, 0, 2, 2, 3, false);
        Assert.Equal(1, store.CommitCallCount);
        first.Succeed(Applied(first.Commit));
        JournalCompletion firstCompletion = await TakeCompletionAsync(executor);
        Assert.Equal(Id(1), firstCompletion.OperationId);
        Assert.False(executor.ForgetStream(Stream));
        AssertView(executor, Stream, 0, 2, 2, 3, false);
        Assert.Equal(1, store.CommitCallCount);

        executor.AcknowledgeCompletion(firstCompletion.OperationId, JournalCompletionAcknowledgement.Handled);
        ControlledStore.CommitCall second = await TakeCommit(store);
        Assert.Equal(Id(2), second.Commit.Identity.OperationId);
        Assert.Equal(1, Assert.Single(second.Commit.StreamMutations).ExpectedVersion);
        AssertView(executor, Stream, 1, 2, 1, 3, false);
        second.Succeed(Applied(second.Commit));
        JournalCompletion secondCompletion = await TakeCompletionAsync(executor);
        Assert.False(executor.ForgetStream(Stream));
        AssertView(executor, Stream, 1, 2, 1, 3, false);

        executor.AcknowledgeCompletion(secondCompletion.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.False(executor.TryGetAdmittedStream(Stream, out _));
        Assert.False(executor.TryGetAdmittedProjection(Stream, "bag", out _));
        Assert.Equal(JournalStreamForgetStatus.Unknown, executor.RequestForgetStream(Stream));
        Assert.Equal(0, executor.Metrics.QueueOperations);
        Assert.Equal(2, store.CommitCallCount);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task ForgettingAQueuedStreamPreservesAtomicDispatchBehindAnotherStream()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store, workerCount: 2);
        Seed(executor, Stream, 1);
        Seed(executor, Other, 9);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(1), Other, 0)).Status);
        ControlledStore.CommitCall blocker = await TakeCommit(store);
        JournalCommit pair = Pair(1, 0);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(pair).Status);

        Assert.Equal(JournalStreamForgetStatus.Deferred, executor.RequestForgetStream(Stream));
        AssertView(executor, Stream, 0, 1, 1, 2, false);
        AssertView(executor, Other, 0, 2, 2, 8, false);
        Assert.Equal(1, store.CommitCallCount);
        Assert.Equal(JournalSubmissionStatus.VersionConflict, executor.Submit(Chained(Id(3), Stream, 0)).Status);
        blocker.Succeed(Applied(blocker.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);

        ControlledStore.CommitCall joint = await TakeCommit(store);
        Assert.Equal(pair.Identity.OperationId, joint.Commit.Identity.OperationId);
        Assert.Equal(2, joint.Commit.StreamMutations.Count);
        AssertView(executor, Stream, 0, 1, 1, 2, false);
        joint.Succeed(Applied(joint.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);

        Assert.False(executor.TryGetAdmittedStream(Stream, out _));
        AssertView(executor, Other, 2, 2, 0, 8, true);
        Assert.Equal(0, executor.Metrics.QueueOperations);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task ReseedingCancelsOnlyThatStreamsPendingForgetAndRetainsTheNewOwnerView()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store);
        Seed(executor, Stream, 1);
        Seed(executor, Other, 9);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Pair(0, 0)).Status);
        ControlledStore.CommitCall pair = await TakeCommit(store);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(3), Other, 1, new[] { Write(Other, 7) })).Status);
        Assert.Equal(JournalStreamForgetStatus.Deferred, executor.RequestForgetStream(Stream));
        Assert.False(executor.ForgetStream(Other));
        executor.SeedCommitted(Stream, 0, new[]
        {
            new JournalProjectionSection(Stream, "owner", 0, "owner.v1", 1, new byte[] { 99 }, DateTimeOffset.UnixEpoch),
        });

        AssertView(executor, Stream, 0, 1, 1, 2, false);
        pair.Succeed(Applied(pair.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);
        AssertView(executor, Stream, 1, 1, 0, 2, true);
        Assert.True(executor.TryGetAdmittedProjection(Stream, "owner", out JournalAdmittedSection? owner));
        Assert.Equal(new byte[] { 99 }, owner.Data.ToArray());
        Assert.True(owner.IsCommitted);

        ControlledStore.CommitCall tail = await TakeCommit(store);
        Assert.Equal(Id(3), tail.Commit.Identity.OperationId);
        AssertView(executor, Other, 1, 2, 1, 7, false);
        tail.Succeed(Applied(tail.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.False(executor.TryGetAdmittedStream(Other, out _));
        AssertView(executor, Stream, 1, 1, 0, 2, true);
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(JournalCommitStatus.VersionConflict, false)]
    [InlineData(JournalCommitStatus.OperationConflict, false)]
    [InlineData(JournalCommitStatus.Applied, true)]
    public async Task WithdrawnAndSupersededWorkMustAllBeAcknowledgedBeforeForgetting(JournalCommitStatus status, bool fatal)
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store);
        Seed(executor, Stream, 1);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(1), Stream, 0, new[] { Write(Stream, 2) })).Status);
        ControlledStore.CommitCall failed = await TakeCommit(store);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(2), Stream, 1, new[] { Write(Stream, 3) })).Status);
        Assert.Equal(JournalStreamForgetStatus.Deferred, executor.RequestForgetStream(Stream));
        if (fatal)
            failed.Fail(StoreFailure(JournalStoreFailureKind.CorruptData, JournalStoreFailureCertainty.CommittedDataUnreadable, Stream));
        else
            failed.Succeed(new JournalCommitResult(status));

        JournalCompletion failure = await TakeCompletionAsync(executor);
        JournalCompletion superseded = await TakeCompletionAsync(executor);
        Assert.Equal(Id(1), failure.OperationId);
        Assert.Equal(Id(2), superseded.OperationId);
        Assert.Equal(JournalCompletionKind.SupersededByFailure, superseded.Kind);
        Assert.Equal(Id(1), superseded.SupersededBy);
        Assert.Equal(new[] { Id(2) }, failure.Correction!.SupersededOperationIds);
        AssertView(executor, Stream, 0, 0, 0, 1, true);
        Assert.False(executor.ForgetStream(Stream));

        executor.AcknowledgeCompletion(superseded.OperationId, JournalCompletionAcknowledgement.Handled);
        AssertView(executor, Stream, 0, 0, 0, 1, true);
        Assert.Equal(1, executor.Metrics.QueueOperations);
        executor.AcknowledgeCompletion(failure.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.False(executor.TryGetAdmittedStream(Stream, out _));
        Assert.Equal(1, store.CommitCallCount);
        if (fatal)
        {
            Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(Chained(Id(3), Stream, 0)).Status);
            executor.ReleaseQuarantine(new[] { Stream });
        }
        await executor.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task QuarantinedAcknowledgementRetainsItsDependantsAndTheWholeRecoveryGroup()
    {
        var store = new ControlledStore();
        MutationJournalExecutor executor = CreateExecutor(store);
        Seed(executor, Stream, 1);
        Seed(executor, Other, 9);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Pair(0, 0)).Status);
        ControlledStore.CommitCall pair = await TakeCommit(store);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(3), Stream, 1, new[] { Write(Stream, 3) })).Status);
        Assert.False(executor.ForgetStream(Stream));
        Assert.False(executor.ForgetStream(Other));
        pair.Succeed(Applied(pair.Commit));
        JournalCompletion completion = await TakeCompletionAsync(executor);
        executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Quarantined);

        Assert.False(executor.TryGetAdmittedStream(Other, out _));
        AssertView(executor, Stream, 0, 0, 0, 1, true);
        JournalCompletion superseded = await TakeCompletionAsync(executor);
        Assert.Equal(Id(3), superseded.OperationId);
        Assert.Equal(JournalCompletionKind.SupersededByFailure, superseded.Kind);
        executor.AcknowledgeCompletion(superseded.OperationId, JournalCompletionAcknowledgement.Handled);
        Assert.False(executor.TryGetAdmittedStream(Stream, out _));
        Assert.Equal(1, store.CommitCallCount);
        Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(Chained(Id(4), Stream, 0)).Status);
        Assert.Equal(JournalSubmissionStatus.StreamBusy, executor.Submit(Chained(Id(5), Other, 0)).Status);
        Assert.Throws<InvalidOperationException>(() => executor.ReleaseQuarantine(new[] { Other }));
        executor.ReleaseQuarantine(new[] { Stream, Other });

        Seed(executor, Stream, 4);
        Assert.Equal(JournalSubmissionStatus.Accepted, executor.Submit(Chained(Id(6), Stream, 0)).Status);
        ControlledStore.CommitCall recovered = await TakeCommit(store);
        recovered.Succeed(Applied(recovered.Commit));
        executor.AcknowledgeCompletion((await TakeCompletionAsync(executor)).OperationId, JournalCompletionAcknowledgement.Handled);
        AssertView(executor, Stream, 1, 1, 0, 4, true);
        await executor.StopAsync(TimeSpan.Zero);
    }

    private static Guid Id(int suffix) => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 87, checked((byte)suffix));

    private static void Seed(MutationJournalExecutor executor, string stream, byte value)
        => executor.SeedCommitted(stream, 0, new[]
        {
            new JournalProjectionSection(stream, "bag", 0, "bag.v1", 1, new[] { value }, DateTimeOffset.UnixEpoch),
        });

    private static JournalProjectionWrite Write(string stream, byte value) => new(stream, "bag", "bag.v1", 1, new[] { value });

    private static JournalCommit Pair(long otherVersion, long streamVersion)
        => Build(Id(2), new[] { JournalTestData.Mutation(Other, otherVersion), JournalTestData.Mutation(Stream, streamVersion) },
            new[] { Write(Other, 8), Write(Stream, 2) }, Array.Empty<byte>());

    private static Task<ControlledStore.CommitCall> TakeCommit(ControlledStore store)
        => store.TakeCommitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static void AssertView(MutationJournalExecutor executor, string stream, long committed, long head, int count, byte value, bool isCommitted)
    {
        Assert.True(executor.TryGetAdmittedStream(stream, out JournalAdmittedStream? view));
        Assert.Equal((committed, head, count), (view.CommittedVersion, view.AdmittedHeadVersion, view.AdmittedUncommittedOperations));
        Assert.True(executor.TryGetAdmittedProjection(stream, "bag", out JournalAdmittedSection? section));
        Assert.Equal(new[] { value }, section.Data.ToArray());
        Assert.Equal(isCommitted, section.IsCommitted);
    }
}
