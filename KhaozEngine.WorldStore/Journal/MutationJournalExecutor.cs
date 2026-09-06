using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore.Journal;

public sealed class MutationJournalExecutor
{
    private readonly object gate = new();
    private readonly IMutationJournalStore store;
    private readonly JournalExecutorOptions options;
    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Func<double> retryJitter;
    private readonly Channel<AdmittedOperation> pending;
    private readonly ConcurrentQueue<JournalCompletion> completions = new();
    private readonly Dictionary<Guid, AdmittedOperation> admitted = new();
    private readonly HashSet<string> reservedStreams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> quarantineByStream = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<string>> quarantineGroups = new();
    private readonly Task[] workers;
    private bool stopping;
    private long nextSequence;
    private long admittedBytes;

    public MutationJournalExecutor(IMutationJournalStore store, JournalExecutorOptions options, TimeProvider? timeProvider = null)
        : this(
            store,
            options,
            timeProvider ?? TimeProvider.System,
            (delay, cancellationToken) => Task.Delay(delay, timeProvider ?? TimeProvider.System, cancellationToken),
            Random.Shared.NextDouble)
    {
    }

    internal MutationJournalExecutor(
        IMutationJournalStore store,
        JournalExecutorOptions options,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<double> retryJitter)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        this.retryJitter = retryJitter ?? throw new ArgumentNullException(nameof(retryJitter));
        Metrics = new MutationJournalExecutorMetrics(timeProvider);
        pending = Channel.CreateBounded<AdmittedOperation>(new BoundedChannelOptions(options.OperationCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = options.WorkerCount == 1,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        workers = new Task[options.WorkerCount];
        for (int i = 0; i < workers.Length; i++) workers[i] = RunWorkerAsync();
    }

    public MutationJournalExecutorMetrics Metrics { get; }

    public JournalSubmission Submit(JournalCommit commit)
    {
        JournalCommit owned = Freeze(commit);
        int bytes = owned.OwnedByteCount;
        Guid operationId = owned.Identity.OperationId;
        JournalSubmissionStatus status;

        lock (gate)
        {
            if (stopping)
            {
                status = JournalSubmissionStatus.Stopping;
            }
            else if (admitted.ContainsKey(operationId) || HasBlockedStream(owned))
            {
                status = JournalSubmissionStatus.StreamBusy;
            }
            else if (admitted.Count >= options.OperationCapacity || bytes > options.OwnedByteCapacity - admittedBytes)
            {
                status = JournalSubmissionStatus.Backpressure;
            }
            else
            {
                var operation = new AdmittedOperation(owned, nextSequence++, bytes, timeProvider.GetUtcNow());
                admitted.Add(operationId, operation);
                admittedBytes += bytes;
                foreach (JournalStreamMutation stream in owned.StreamMutations) reservedStreams.Add(stream.StreamKey);
                UpdateAdmissionGauges();
                if (!pending.Writer.TryWrite(operation))
                    throw new InvalidOperationException("The bounded journal channel rejected reserved capacity.");
                status = JournalSubmissionStatus.Accepted;
            }
        }

        Metrics.RecordSubmission(status);
        return new JournalSubmission(status, operationId, status == JournalSubmissionStatus.Accepted ? bytes : 0);
    }

    public bool TryDequeueCompletion([NotNullWhen(true)] out JournalCompletion? completion)
    {
        lock (gate)
        {
            if (!completions.TryDequeue(out completion)) return false;
            if (admitted.TryGetValue(completion.OperationId, out AdmittedOperation? operation))
                operation.CompletionDequeued = true;
        }
        return true;
    }

    public void AcknowledgeCompletion(Guid operationId, JournalCompletionAcknowledgement acknowledgement)
    {
        if (!Enum.IsDefined(acknowledgement)) throw new ArgumentOutOfRangeException(nameof(acknowledgement));
        lock (gate)
        {
            if (!admitted.TryGetValue(operationId, out AdmittedOperation? operation))
                throw new KeyNotFoundException($"Operation '{operationId}' is not admitted.");
            if (operation.Completion is null || !operation.CompletionDequeued)
                throw new InvalidOperationException("Only a dequeued terminal completion can be acknowledged.");

            if (acknowledgement == JournalCompletionAcknowledgement.Quarantined)
                AddQuarantine(operation);
            foreach (JournalStreamMutation stream in operation.Commit.StreamMutations)
                reservedStreams.Remove(stream.StreamKey);
            admitted.Remove(operationId);
            admittedBytes -= operation.OwnedByteCount;
            UpdateAdmissionGauges();
        }
        Metrics.CompletionAcknowledged();
    }

    public void ReleaseQuarantine(IReadOnlyList<string> recoveredStreamKeys)
    {
        ArgumentNullException.ThrowIfNull(recoveredStreamKeys);
        if (recoveredStreamKeys.Count == 0) throw new ArgumentException("At least one recovered stream is required.", nameof(recoveredStreamKeys));
        string[] keys = new string[recoveredStreamKeys.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = JournalValidation.StreamKey(recoveredStreamKeys[i], nameof(recoveredStreamKeys));
        Array.Sort(keys, StringComparer.Ordinal);
        for (int i = 1; i < keys.Length; i++)
            if (StringComparer.Ordinal.Equals(keys[i - 1], keys[i])) throw new ArgumentException("Recovered stream keys must be unique.", nameof(recoveredStreamKeys));

        lock (gate)
        {
            var requested = new HashSet<string>(keys, StringComparer.Ordinal);
            var groups = new HashSet<Guid>();
            foreach (string key in keys)
            {
                if (!quarantineByStream.TryGetValue(key, out Guid groupId))
                    throw new InvalidOperationException($"Stream '{key}' is not quarantined.");
                if (reservedStreams.Contains(key))
                    throw new InvalidOperationException($"Stream '{key}' still has an admitted operation.");
                groups.Add(groupId);
            }
            foreach (Guid groupId in groups)
                if (!quarantineGroups[groupId].IsSubsetOf(requested))
                    throw new InvalidOperationException("Recovery must release every stream quarantined by an operation.");

            foreach (Guid groupId in groups)
            {
                foreach (string key in quarantineGroups[groupId]) quarantineByStream.Remove(key);
                quarantineGroups.Remove(groupId);
            }
        }
    }

    public async Task<JournalShutdownResult> StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        if (gracePeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        lock (gate)
        {
            if (!stopping)
            {
                stopping = true;
                pending.Writer.TryComplete();
            }
        }

        if (gracePeriod > TimeSpan.Zero)
        {
            Task drained = Task.WhenAll(workers);
            Task grace = delayAsync(gracePeriod, cancellationToken);
            Task winner = await Task.WhenAny(drained, grace).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return SnapshotShutdown();
    }

    private async Task RunWorkerAsync()
    {
        await foreach (AdmittedOperation operation in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            await ExecuteAsync(operation).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(AdmittedOperation operation)
    {
        int transientRetries = 0;
        while (true)
        {
            try
            {
                DateTimeOffset started = timeProvider.GetUtcNow();
                JournalCommitResult result;
                try
                {
                    result = await store.CommitAsync(operation.Commit, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    Metrics.RecordCommitLatency(timeProvider.GetUtcNow() - started);
                }
                Complete(operation, result);
                return;
            }
            catch (JournalStoreException failure) when (failure.Certainty == JournalStoreFailureCertainty.Unknown)
            {
                Metrics.RecordRetry(failure.Kind);
                JournalOperationResolution? resolution;
                try
                {
                    resolution = await ResolveUnknownAsync(operation).ConfigureAwait(false);
                }
                catch (Exception resolutionFailure)
                {
                    CompleteFailure(operation, resolutionFailure);
                    return;
                }
                if (resolution is null)
                {
                    await DelayRetryAsync(transientRetries++).ConfigureAwait(false);
                    continue;
                }
                Complete(operation, FromResolution(resolution));
                return;
            }
            catch (JournalStoreException failure) when (IsRetryable(failure) && transientRetries < options.MaximumTransientRetries)
            {
                Metrics.RecordRetry(failure.Kind);
                await DelayRetryAsync(transientRetries).ConfigureAwait(false);
                transientRetries++;
            }
            catch (Exception failure)
            {
                CompleteFailure(operation, failure);
                return;
            }
        }
    }

    private async Task<JournalOperationResolution?> ResolveUnknownAsync(AdmittedOperation operation)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                JournalOperationResolution resolution = await store.ResolveOperationAsync(operation.Commit.Identity, CancellationToken.None).ConfigureAwait(false);
                return resolution.Status == JournalOperationResolutionStatus.NotFound ? null : resolution;
            }
            catch (JournalStoreException failure) when (failure.Certainty == JournalStoreFailureCertainty.Unknown || IsRetryable(failure))
            {
                Metrics.RecordRetry(failure.Kind);
                await DelayRetryAsync(attempt++).ConfigureAwait(false);
            }
        }
    }

    private void Complete(AdmittedOperation operation, JournalCommitResult result)
    {
        Metrics.RecordResult(result.Status);
        QueueCompletion(operation, new JournalCompletion(operation.Commit, result, null), quarantine: false);
    }

    private void CompleteFailure(AdmittedOperation operation, Exception failure)
    {
        Metrics.RecordFailed();
        QueueCompletion(operation, new JournalCompletion(operation.Commit, null, failure), quarantine: true);
    }

    private void QueueCompletion(AdmittedOperation operation, JournalCompletion completion, bool quarantine)
    {
        lock (gate)
        {
            if (operation.Completion is not null) return;
            operation.Completion = completion;
            if (quarantine) AddQuarantine(operation);
            completions.Enqueue(completion);
            Metrics.CompletionQueued();
        }
    }

    private void AddQuarantine(AdmittedOperation operation)
    {
        if (quarantineGroups.ContainsKey(operation.Commit.Identity.OperationId)) return;
        var streams = new HashSet<string>(operation.Commit.StreamMutations.Select(stream => stream.StreamKey), StringComparer.Ordinal);
        quarantineGroups.Add(operation.Commit.Identity.OperationId, streams);
        foreach (string stream in streams) quarantineByStream.Add(stream, operation.Commit.Identity.OperationId);
        Metrics.RecordQuarantined();
    }

    private bool HasBlockedStream(JournalCommit commit)
    {
        foreach (JournalStreamMutation stream in commit.StreamMutations)
            if (reservedStreams.Contains(stream.StreamKey) || quarantineByStream.ContainsKey(stream.StreamKey)) return true;
        return false;
    }

    private void UpdateAdmissionGauges()
    {
        DateTimeOffset? oldest = null;
        foreach (AdmittedOperation operation in admitted.Values)
            if (oldest is null || operation.AdmittedAtUtc < oldest) oldest = operation.AdmittedAtUtc;
        Metrics.SetAdmissionGauges(admitted.Count, admittedBytes, reservedStreams.Count, oldest);
    }

    private JournalShutdownResult SnapshotShutdown()
    {
        lock (gate)
        {
            Guid[] ids = admitted.Values.OrderBy(operation => operation.Sequence).Select(operation => operation.Commit.Identity.OperationId).ToArray();
            return new JournalShutdownResult(ids, admittedBytes);
        }
    }

    private async Task DelayRetryAsync(int attempt)
    {
        double exponential = Math.Pow(2, Math.Min(attempt, 30));
        double baseMilliseconds = Math.Min(options.MaximumRetryDelay.TotalMilliseconds, options.InitialRetryDelay.TotalMilliseconds * exponential);
        double sample = Math.Clamp(retryJitter(), 0, 1);
        var delay = TimeSpan.FromMilliseconds(baseMilliseconds * (0.5 + sample));
        await delayAsync(delay, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool IsRetryable(JournalStoreException failure)
        => failure.Certainty == JournalStoreFailureCertainty.DefinitelyNotCommitted
            && failure.Kind is JournalStoreFailureKind.Unavailable
                or JournalStoreFailureKind.Timeout
                or JournalStoreFailureKind.Deadlock
                or JournalStoreFailureKind.Cancelled;

    private static JournalCommitResult FromResolution(JournalOperationResolution resolution)
        => resolution.Status switch
        {
            JournalOperationResolutionStatus.Replayed => new JournalCommitResult(JournalCommitStatus.Replayed, resolution.Receipt),
            JournalOperationResolutionStatus.OperationConflict => new JournalCommitResult(JournalCommitStatus.OperationConflict),
            _ => throw new InvalidOperationException("An absent resolution cannot complete an operation."),
        };

    private static JournalCommit Freeze(JournalCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        commit.Validate();
        var identity = new JournalOperationIdentity(
            commit.Identity.OperationId,
            commit.Identity.AuthenticatedScope,
            commit.Identity.ActionKind,
            commit.Identity.NormalizedIntent.ToArray());
        JournalStreamMutation[] streams = commit.StreamMutations.Select(stream => new JournalStreamMutation(
            stream.StreamKey,
            stream.ExpectedVersion,
            stream.Events.Select(value => new JournalEvent(value.EventType, value.EventSchemaVersion, value.Payload.ToArray())).ToArray())).ToArray();
        JournalProjectionWrite[] projections = commit.ProjectionWrites.Select(value => new JournalProjectionWrite(
            value.StreamKey,
            value.SectionName,
            value.ProjectionSchema,
            value.ProjectionSchemaVersion,
            value.Data.ToArray())).ToArray();
        return new JournalCommit(identity, streams, projections, commit.ResultSchema, commit.ResultSchemaVersion, commit.ResultData.ToArray());
    }

    private sealed class AdmittedOperation
    {
        internal AdmittedOperation(JournalCommit commit, long sequence, int ownedByteCount, DateTimeOffset admittedAtUtc)
        {
            Commit = commit;
            Sequence = sequence;
            OwnedByteCount = ownedByteCount;
            AdmittedAtUtc = admittedAtUtc;
        }

        internal JournalCommit Commit { get; }
        internal long Sequence { get; }
        internal int OwnedByteCount { get; }
        internal DateTimeOffset AdmittedAtUtc { get; }
        internal JournalCompletion? Completion { get; set; }
        internal bool CompletionDequeued { get; set; }
    }
}
