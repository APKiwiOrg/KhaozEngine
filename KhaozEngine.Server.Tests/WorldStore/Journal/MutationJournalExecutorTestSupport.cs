using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

internal static class MutationJournalExecutorTestSupport
{
    internal static MutationJournalExecutor CreateExecutor(
        ControlledStore store,
        int workerCount = 1,
        int operationCapacity = 4,
        long byteCapacity = 1_000_000,
        int maximumTransientRetries = 3,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var options = new JournalExecutorOptions(
            workerCount,
            operationCapacity,
            byteCapacity,
            maximumTransientRetries,
            TimeSpan.Zero,
            TimeSpan.Zero);
        return new MutationJournalExecutor(
            store,
            options,
            TimeProvider.System,
            delay ?? ((_, _) => Task.CompletedTask),
            static () => 0.5);
    }

    internal static JournalCommit Commit(Guid operationId, params string[] streams)
        => Commit(operationId, Array.Empty<byte>(), streams);

    internal static JournalCommit Commit(Guid operationId, byte[] result, params string[] streams)
    {
        var identity = new JournalOperationIdentity(operationId, "world/account", "bank.deposit", new byte[] { 7, 8 });
        JournalStreamMutation[] mutations = streams.Select(stream => JournalTestData.Mutation(stream)).ToArray();
        return new JournalCommit(identity, mutations, Array.Empty<JournalProjectionWrite>(), "result.v1", 1, result);
    }

    internal static JournalCommitResult Applied(JournalCommit commit)
        => new(JournalCommitStatus.Applied, Receipt(commit));

    internal static JournalCommitReceipt Receipt(JournalCommit commit, bool isReplay = false)
    {
        JournalStreamVersionRange[] ranges = commit.StreamMutations
            .Select(stream => new JournalStreamVersionRange(stream.StreamKey, stream.ExpectedVersion, stream.ExpectedVersion + stream.Events.Count, stream.Events.Count))
            .ToArray();
        return new JournalCommitReceipt(
            commit.Identity.OperationId,
            DateTimeOffset.UnixEpoch,
            ranges,
            commit.ResultSchema,
            commit.ResultSchemaVersion,
            commit.ResultData.ToArray(),
            isReplay);
    }

    internal static JournalStoreException StoreFailure(
        JournalStoreFailureKind kind,
        JournalStoreFailureCertainty certainty,
        params string[] streams)
        => new(kind, certainty, JournalStoreFailureScope.OperationStreams, streams, "controlled failure");

    internal static async Task<JournalCompletion> TakeCompletionAsync(MutationJournalExecutor executor)
    {
        await WaitUntilAsync(() => executor.Metrics.UnacknowledgedCompletions > 0);
        Assert.True(executor.TryDequeueCompletion(out JournalCompletion? completion));
        return completion;
    }

    internal static void AcknowledgeAll(MutationJournalExecutor executor)
    {
        while (executor.TryDequeueCompletion(out JournalCompletion? completion))
            executor.AcknowledgeCompletion(completion.OperationId, JournalCompletionAcknowledgement.Handled);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100_000; i++)
        {
            if (condition()) return;
            await Task.Yield();
        }
        Assert.Fail("The deterministic asynchronous condition did not become true.");
    }

    internal sealed class ControlledDelay
    {
        private readonly Channel<DelayCall> waits = Channel.CreateUnbounded<DelayCall>();

        internal Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var call = new DelayCall(duration);
            waits.Writer.TryWrite(call);
            return call.Task.WaitAsync(cancellationToken);
        }

        internal void ReleaseNext()
        {
            Assert.True(waits.Reader.TryRead(out DelayCall? call));
            call.Release();
        }

        internal ValueTask<DelayCall> TakeAsync() => waits.Reader.ReadAsync();

        internal sealed class DelayCall
        {
            private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal DelayCall(TimeSpan duration) => Duration = duration;
            internal TimeSpan Duration { get; }
            internal Task Task => completion.Task;
            internal void Release() => completion.SetResult();
        }
    }

    internal sealed class ControlledStore : IMutationJournalStore
    {
        private readonly Channel<CommitCall> commits = Channel.CreateUnbounded<CommitCall>();
        private readonly Channel<ResolveCall> resolves = Channel.CreateUnbounded<ResolveCall>();
        private int commitCallCount;
        private int resolveCallCount;

        public Task<JournalCommitResult> CommitAsync(JournalCommit commit, CancellationToken cancellationToken = default)
        {
            var call = new CommitCall(commit, cancellationToken);
            Interlocked.Increment(ref commitCallCount);
            commits.Writer.TryWrite(call);
            return call.Task;
        }

        public Task<JournalOperationResolution> ResolveOperationAsync(JournalOperationIdentity identity, CancellationToken cancellationToken = default)
        {
            var call = new ResolveCall(identity, cancellationToken);
            Interlocked.Increment(ref resolveCallCount);
            resolves.Writer.TryWrite(call);
            return call.Task;
        }

        internal int CommitCallCount => Volatile.Read(ref commitCallCount);
        internal int ResolveCallCount => Volatile.Read(ref resolveCallCount);
        internal ValueTask<CommitCall> TakeCommitAsync() => commits.Reader.ReadAsync();
        internal ValueTask<ResolveCall> TakeResolveAsync() => resolves.Reader.ReadAsync();

        public Task<JournalInitializeResult> InitializeAsync(JournalInitialization initialization, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<JournalSnapshot?> LoadSnapshotAsync(string streamKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<JournalEventPage> ReadEventsAsync(JournalEventRead read, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<JournalProjectionRead> ReadProjectionsAsync(JournalProjectionQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<JournalCompactionResult> CompactAsync(JournalCompaction compaction, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        internal sealed class CommitCall
        {
            private readonly TaskCompletionSource<JournalCommitResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal CommitCall(JournalCommit commit, CancellationToken cancellationToken)
            {
                Commit = commit;
                CancellationToken = cancellationToken;
            }

            internal JournalCommit Commit { get; }
            internal CancellationToken CancellationToken { get; }
            internal Task<JournalCommitResult> Task => completion.Task;
            internal void Succeed(JournalCommitResult result) => completion.SetResult(result);
            internal void Fail(Exception exception) => completion.SetException(exception);
        }

        internal sealed class ResolveCall
        {
            private readonly TaskCompletionSource<JournalOperationResolution> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal ResolveCall(JournalOperationIdentity identity, CancellationToken cancellationToken)
            {
                Identity = identity;
                CancellationToken = cancellationToken;
            }

            internal JournalOperationIdentity Identity { get; }
            internal CancellationToken CancellationToken { get; }
            internal Task<JournalOperationResolution> Task => completion.Task;
            internal void Succeed(JournalOperationResolution result) => completion.SetResult(result);
            internal void Fail(Exception exception) => completion.SetException(exception);
        }
    }
}
