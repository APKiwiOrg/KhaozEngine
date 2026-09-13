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
    private readonly Channel<AdmittedJournalOperation> pending;
    private readonly ConcurrentQueue<JournalCompletion> completions = new();
    private readonly Dictionary<Guid, AdmittedJournalOperation> admitted = new();
    private readonly HashSet<string> reservedStreams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> quarantineByStream = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<string>> quarantineGroups = new();
    private readonly JournalAdmittedState admittedState;
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
        admittedState = new JournalAdmittedState(options.StreamQueueDepth);
        pending = Channel.CreateBounded<AdmittedJournalOperation>(new BoundedChannelOptions(options.OperationCapacity)
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
        JournalSubmission submission;

        lock (gate)
        {
            JournalSubmissionStatus status = Admit(owned, bytes, operationId, out AdmittedJournalOperation? operation);
            submission = operation is null
                ? new JournalSubmission(status, operationId, 0)
                : new JournalSubmission(status, operationId, bytes, AdmittedHeads(operation), ChangedSections(owned));
        }

        Metrics.RecordSubmission(submission.Status);
        return submission;
    }

    /// <summary>
    /// Declares what the store durably holds for one stream, which is what a consumer reads with
    /// <c>ReadProjectionsAsync</c> when it loads that stream. The executor layers admitted operations over it and
    /// advances it as commits are acknowledged <see cref="JournalCompletionAcknowledgement.Handled"/>. Seeding
    /// again replaces the baseline and rebuilds the layer, which is how a stream is resynced after a correction or
    /// a quarantine.
    /// </summary>
    public void SeedCommitted(string streamKey, long committedVersion, IReadOnlyList<JournalProjectionSection> sections)
    {
        string key = JournalValidation.StreamKey(streamKey, nameof(streamKey));
        JournalValidation.NonNegative(committedVersion, nameof(committedVersion));
        ArgumentNullException.ThrowIfNull(sections);
        JournalValidation.Maximum(sections.Count, JournalLimits.EngineMaximumProjectionSectionsPerStream, nameof(sections));
        foreach (JournalProjectionSection section in sections)
        {
            ArgumentNullException.ThrowIfNull(section);
            if (!StringComparer.Ordinal.Equals(section.StreamKey, key))
                throw new ArgumentException($"Section '{section.SectionName}' belongs to stream '{section.StreamKey}'.", nameof(sections));
        }
        lock (gate) admittedState.Seed(key, committedVersion, sections);
    }

    /// <summary>
    /// Drops a seeded stream's baseline, for a player who logged out. Returns false when the executor holds no
    /// view of it, and refuses while it still carries admitted operations.
    /// </summary>
    public bool ForgetStream(string streamKey)
    {
        string key = JournalValidation.StreamKey(streamKey, nameof(streamKey));
        lock (gate) return admittedState.Forget(key);
    }

    /// <summary>
    /// Reads one projection section as the executor currently sees it: the newest admitted uncommitted write over
    /// it, or the committed baseline when nothing is in flight. Synchronous and safe from the simulation thread.
    /// </summary>
    public bool TryGetAdmittedProjection(string streamKey, string sectionName, [NotNullWhen(true)] out JournalAdmittedSection? section)
    {
        string key = JournalValidation.StreamKey(streamKey, nameof(streamKey));
        string name = JournalValidation.Identity(sectionName, nameof(sectionName), JournalLimits.EngineMaximumIdentityCharacters);
        lock (gate) return admittedState.TryGetSection(key, name, out section);
    }

    /// <summary>Reads the whole admitted view of one stream, its versions and every section it currently holds.</summary>
    public bool TryGetAdmittedStream(string streamKey, [NotNullWhen(true)] out JournalAdmittedStream? stream)
    {
        string key = JournalValidation.StreamKey(streamKey, nameof(streamKey));
        lock (gate) return admittedState.TryGetStream(key, out stream);
    }

    public bool TryDequeueCompletion([NotNullWhen(true)] out JournalCompletion? completion)
    {
        lock (gate)
        {
            if (!completions.TryDequeue(out completion)) return false;
            if (admitted.TryGetValue(completion.OperationId, out AdmittedJournalOperation? operation))
                operation.CompletionDequeued = true;
        }
        return true;
    }

    public void AcknowledgeCompletion(Guid operationId, JournalCompletionAcknowledgement acknowledgement)
    {
        if (!Enum.IsDefined(acknowledgement)) throw new ArgumentOutOfRangeException(nameof(acknowledgement));
        var released = new List<AdmittedJournalOperation>();
        lock (gate)
        {
            if (!admitted.TryGetValue(operationId, out AdmittedJournalOperation? operation))
                throw new KeyNotFoundException($"Operation '{operationId}' is not admitted.");
            if (operation.Completion is null || !operation.CompletionDequeued)
                throw new InvalidOperationException("Only a dequeued terminal completion can be acknowledged.");

            if (acknowledgement == JournalCompletionAcknowledgement.Quarantined)
            {
                AddQuarantine(operation);
                if (!operation.Withdrawn) SupersedeDependants(operation);
            }

            bool promote = acknowledgement == JournalCompletionAcknowledgement.Handled
                && !operation.Withdrawn
                && operation.Completion.Result?.Receipt is not null;
            admittedState.Release(operation, promote, released);
            foreach (JournalStreamMutation stream in operation.Commit.StreamMutations)
                reservedStreams.Remove(stream.StreamKey);
            admitted.Remove(operationId);
            admittedBytes -= operation.OwnedByteCount;
            foreach (AdmittedJournalOperation next in released) Start(next);
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

    private JournalSubmissionStatus Admit(JournalCommit owned, int bytes, Guid operationId, out AdmittedJournalOperation? operation)
    {
        operation = null;
        if (stopping) return JournalSubmissionStatus.Stopping;
        if (admitted.ContainsKey(operationId) || HasQuarantinedStream(owned)) return JournalSubmissionStatus.StreamBusy;
        if (!owned.QueueBehindAdmitted && admittedState.HasBusyStream(owned)) return JournalSubmissionStatus.StreamBusy;
        if (admitted.Count >= options.OperationCapacity || bytes > options.OwnedByteCapacity - admittedBytes)
            return JournalSubmissionStatus.Backpressure;
        if (admittedState.Refusal(owned) is JournalSubmissionStatus refusal) return refusal;

        operation = new AdmittedJournalOperation(owned, nextSequence++, bytes, timeProvider.GetUtcNow());
        admitted.Add(operationId, operation);
        admittedBytes += bytes;
        foreach (JournalStreamMutation stream in owned.StreamMutations) reservedStreams.Add(stream.StreamKey);
        admittedState.Admit(operation);
        if (admittedState.CanStart(operation)) Start(operation);
        UpdateAdmissionGauges();
        return JournalSubmissionStatus.Accepted;
    }

    private void Start(AdmittedJournalOperation operation)
    {
        if (stopping || !operation.CanStart) return;
        operation.Started = true;
        if (!pending.Writer.TryWrite(operation))
            throw new InvalidOperationException("The bounded journal channel rejected reserved capacity.");
    }

    private async Task RunWorkerAsync()
    {
        await foreach (AdmittedJournalOperation operation in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            await ExecuteAsync(operation).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(AdmittedJournalOperation operation)
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

    private async Task<JournalOperationResolution?> ResolveUnknownAsync(AdmittedJournalOperation operation)
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

    private void Complete(AdmittedJournalOperation operation, JournalCommitResult result)
    {
        Metrics.RecordResult(result.Status);
        QueueCompletion(operation, result, null, quarantine: false);
    }

    private void CompleteFailure(AdmittedJournalOperation operation, Exception failure)
    {
        Metrics.RecordFailed();
        QueueCompletion(operation, null, failure, quarantine: true);
    }

    private void QueueCompletion(AdmittedJournalOperation operation, JournalCommitResult? result, Exception? failure, bool quarantine)
    {
        lock (gate)
        {
            if (operation.Completion is not null) return;
            if (quarantine) AddQuarantine(operation);

            JournalCorrection? correction = null;
            List<AdmittedJournalOperation>? superseded = null;
            if (IsTerminalFailure(result, failure))
            {
                superseded = new List<AdmittedJournalOperation>();
                correction = admittedState.Withdraw(operation, superseded);
                Metrics.RecordCorrection();
            }

            var completion = new JournalCompletion(operation.Commit, result, failure, correction);
            operation.Completion = completion;
            completions.Enqueue(completion);
            Metrics.CompletionQueued();

            if (superseded is not null && superseded.Count > 0)
            {
                Guid failedOperationId = operation.Commit.Identity.OperationId;
                foreach (AdmittedJournalOperation dependant in superseded) QueueSuperseded(dependant, failedOperationId);
                Metrics.RecordSuperseded(superseded.Count);
            }
            UpdateAdmissionGauges();
        }
    }

    private void QueueSuperseded(AdmittedJournalOperation dependant, Guid failedOperationId)
    {
        var completion = new JournalCompletion(dependant.Commit, null, null, null, failedOperationId);
        dependant.Completion = completion;
        completions.Enqueue(completion);
        Metrics.CompletionQueued();
    }

    private void SupersedeDependants(AdmittedJournalOperation operation)
    {
        var superseded = new List<AdmittedJournalOperation>();
        admittedState.Withdraw(operation, superseded);
        if (superseded.Count == 0) return;
        Guid failedOperationId = operation.Commit.Identity.OperationId;
        foreach (AdmittedJournalOperation dependant in superseded) QueueSuperseded(dependant, failedOperationId);
        Metrics.RecordSuperseded(superseded.Count);
    }

    private void AddQuarantine(AdmittedJournalOperation operation)
    {
        if (quarantineGroups.ContainsKey(operation.Commit.Identity.OperationId)) return;
        var streams = new HashSet<string>(operation.Commit.StreamMutations.Select(stream => stream.StreamKey), StringComparer.Ordinal);
        quarantineGroups.Add(operation.Commit.Identity.OperationId, streams);
        foreach (string stream in streams) quarantineByStream.Add(stream, operation.Commit.Identity.OperationId);
        Metrics.RecordQuarantined();
    }

    private bool HasQuarantinedStream(JournalCommit commit)
    {
        foreach (JournalStreamMutation stream in commit.StreamMutations)
            if (quarantineByStream.ContainsKey(stream.StreamKey)) return true;
        return false;
    }

    private void UpdateAdmissionGauges()
    {
        DateTimeOffset? oldest = null;
        DateTimeOffset? oldestUncommitted = null;
        long uncommitted = 0;
        foreach (AdmittedJournalOperation operation in admitted.Values)
        {
            if (oldest is null || operation.AdmittedAtUtc < oldest) oldest = operation.AdmittedAtUtc;
            if (operation.Completion is not null) continue;
            uncommitted++;
            if (oldestUncommitted is null || operation.AdmittedAtUtc < oldestUncommitted) oldestUncommitted = operation.AdmittedAtUtc;
        }
        Metrics.SetAdmissionGauges(admitted.Count, admittedBytes, reservedStreams.Count, oldest);
        Metrics.SetAdmittedGauges(uncommitted, admittedState.PeakStreamDepth, oldestUncommitted);
    }

    private JournalAdmittedStreamHead[] AdmittedHeads(AdmittedJournalOperation operation)
    {
        var heads = new JournalAdmittedStreamHead[operation.Commit.StreamMutations.Count];
        for (int i = 0; i < heads.Length; i++)
            heads[i] = new JournalAdmittedStreamHead(operation.Commit.StreamMutations[i].StreamKey, operation.AdmittedAfterVersions[i]);
        return heads;
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

    private static JournalProjectionSectionKey[] ChangedSections(JournalCommit commit)
    {
        var sections = new JournalProjectionSectionKey[commit.ProjectionWrites.Count];
        for (int i = 0; i < sections.Length; i++)
            sections[i] = new JournalProjectionSectionKey(commit.ProjectionWrites[i].StreamKey, commit.ProjectionWrites[i].SectionName);
        return sections;
    }

    private static bool IsTerminalFailure(JournalCommitResult? result, Exception? failure)
        => failure is not null || result?.Status is JournalCommitStatus.VersionConflict or JournalCommitStatus.OperationConflict;

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
        return new JournalCommit(
            identity,
            streams,
            projections,
            commit.ResultSchema,
            commit.ResultSchemaVersion,
            commit.ResultData.ToArray(),
            commit.PresentAtCommit,
            commit.QueueBehindAdmitted);
    }
}
