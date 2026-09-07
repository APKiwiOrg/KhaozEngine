using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore.Journal;

public sealed class InMemoryMutationJournalStore : IMutationJournalStore, IMutationJournalAgeMaintenance
{
    private static readonly TimeSpan DefaultMinimumRetryHorizon = TimeSpan.FromHours(24);
    private readonly object gate = new();
    private readonly JournalLimits limits;
    private readonly TimeSpan minimumRetryHorizon;
    private readonly TimeProvider timeProvider;
    private readonly InMemoryJournalTestHook? testHook;
    private StoreState state;

    public InMemoryMutationJournalStore(
        JournalLimits? limits = null,
        TimeSpan? minimumRetryHorizon = null,
        TimeProvider? timeProvider = null)
        : this(limits ?? JournalLimits.Maximum, minimumRetryHorizon ?? DefaultMinimumRetryHorizon, timeProvider ?? TimeProvider.System, null)
    {
    }

    internal InMemoryMutationJournalStore(
        JournalLimits limits,
        TimeSpan minimumRetryHorizon,
        TimeProvider timeProvider,
        InMemoryJournalTestHook? testHook = null)
    {
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        if (minimumRetryHorizon < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumRetryHorizon));
        this.minimumRetryHorizon = minimumRetryHorizon;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.testHook = testHook;
        state = new StoreState(Guid.NewGuid(), new Dictionary<string, StreamState>(StringComparer.Ordinal), new Dictionary<Guid, OperationState>());
    }

    public Task<JournalOperationResolution> ResolveOperationAsync(JournalOperationIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        identity.Validate(limits);
        JournalFingerprint intent = JournalCanonicalizer.CreateIntentFingerprint(identity);
        byte[] intentFingerprint = intent.Digest.ToArray();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        lock (gate)
        {
            if (!state.Operations.TryGetValue(identity.OperationId, out OperationState? operation))
            {
                Invoke(JournalTestHookPhase.AfterOperationResolution);
                return Task.FromResult(new JournalOperationResolution(JournalOperationResolutionStatus.NotFound));
            }
            if (!FingerprintsMatch(operation.IntentFingerprint, intentFingerprint))
            {
                Invoke(JournalTestHookPhase.AfterOperationResolution);
                return Task.FromResult(new JournalOperationResolution(JournalOperationResolutionStatus.OperationConflict));
            }
            VerifyReceipt(operation.Receipt);
            Invoke(JournalTestHookPhase.AfterOperationResolution);
            return Task.FromResult(new JournalOperationResolution(JournalOperationResolutionStatus.Replayed, operation.Receipt));
        }
    }

    public Task<JournalInitializeResult> InitializeAsync(JournalInitialization initialization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        cancellationToken.ThrowIfCancellationRequested();
        initialization.Identity.Validate(limits);
        JournalFingerprint intent = JournalCanonicalizer.CreateIntentFingerprint(initialization.Identity);
        byte[] intentFingerprint = intent.Digest.ToArray();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        lock (gate)
        {
            bool resolved = TryResolve(initialization.Identity.OperationId, intentFingerprint, out OperationState? operation, out bool conflict);
            Invoke(JournalTestHookPhase.AfterOperationResolution);
            if (resolved)
                return Task.FromResult(conflict
                    ? new JournalInitializeResult(JournalInitializeStatus.OperationConflict)
                    : ReplayInitialization(operation!));
            initialization.Validate(limits);
            if (state.Streams.ContainsKey(initialization.AbsentStreamKey))
                return Task.FromResult(new JournalInitializeResult(JournalInitializeStatus.ExistingStream));

            DateTimeOffset now = timeProvider.GetUtcNow();
            var snapshot = new JournalSnapshot(
                initialization.AbsentStreamKey,
                0,
                initialization.SnapshotSchema,
                initialization.SnapshotSchemaVersion,
                initialization.SnapshotData.ToArray(),
                initialization.SnapshotChecksum.ToArray(),
                now);
            var projections = new Dictionary<string, JournalProjectionSection>(StringComparer.Ordinal);
            Invoke(JournalTestHookPhase.AfterHeadValidation);
            Invoke(JournalTestHookPhase.AfterEventWrites);
            foreach (JournalProjectionWrite write in initialization.ProjectionWrites)
                projections.Add(write.SectionName, CreateProjection(write, 0, now));
            Invoke(JournalTestHookPhase.AfterProjectionWrites);

            var stream = new StreamState(0, 0, snapshot, Array.Empty<JournalStoredEvent>(), projections, now);
            var range = new JournalStreamVersionRange(initialization.AbsentStreamKey, 0, 0, 0);
            var receipt = new JournalCommitReceipt(
                initialization.Identity.OperationId,
                now,
                new[] { range },
                initialization.ResultSchema,
                initialization.ResultSchemaVersion,
                initialization.ResultData.ToArray(),
                initialization.ResultChecksum.ToArray());
            JournalFingerprint execution = JournalCanonicalizer.CreateInitializationFingerprint(initialization);
            var storedOperation = new OperationState(intent.FormatVersion, intentFingerprint, execution.FormatVersion, execution.Digest.ToArray(), receipt);
            var streams = new Dictionary<string, StreamState>(state.Streams, StringComparer.Ordinal) { [initialization.AbsentStreamKey] = stream };
            var operations = new Dictionary<Guid, OperationState>(state.Operations) { [initialization.Identity.OperationId] = storedOperation };
            Invoke(JournalTestHookPhase.BeforeCommit);
            state = new StoreState(state.StoreEpoch, streams, operations);
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return Task.FromResult(new JournalInitializeResult(JournalInitializeStatus.Initialized, receipt));
        }
    }

    public Task<JournalCommitResult> CommitAsync(JournalCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        commit.Identity.Validate(limits);
        JournalFingerprint intent = JournalCanonicalizer.CreateIntentFingerprint(commit.Identity);
        byte[] intentFingerprint = intent.Digest.ToArray();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        lock (gate)
        {
            bool resolved = TryResolve(commit.Identity.OperationId, intentFingerprint, out OperationState? operation, out bool conflict);
            Invoke(JournalTestHookPhase.AfterOperationResolution);
            if (resolved)
                return Task.FromResult(conflict
                    ? new JournalCommitResult(JournalCommitStatus.OperationConflict)
                    : ReplayCommit(operation!));
            commit.Validate(limits);
            foreach (JournalStreamMutation mutation in commit.StreamMutations)
            {
                if (!state.Streams.TryGetValue(mutation.StreamKey, out StreamState? stream) || stream.HeadVersion != mutation.ExpectedVersion)
                    return Task.FromResult(new JournalCommitResult(JournalCommitStatus.VersionConflict));
            }
            Invoke(JournalTestHookPhase.AfterHeadValidation);

            DateTimeOffset now = timeProvider.GetUtcNow();
            var streams = new Dictionary<string, StreamState>(state.Streams, StringComparer.Ordinal);
            var ranges = new List<JournalStreamVersionRange>(commit.StreamMutations.Count);
            int operationOrdinal = 0;
            foreach (JournalStreamMutation mutation in commit.StreamMutations)
            {
                StreamState current = state.Streams[mutation.StreamKey];
                var events = new List<JournalStoredEvent>(current.Events);
                long version = current.HeadVersion;
                foreach (JournalEvent journalEvent in mutation.Events)
                {
                    version = checked(version + 1);
                    events.Add(new JournalStoredEvent(
                        mutation.StreamKey,
                        version,
                        commit.Identity.OperationId,
                        operationOrdinal++,
                        journalEvent.EventType,
                        journalEvent.EventSchemaVersion,
                        journalEvent.Payload.ToArray(),
                        journalEvent.PayloadChecksum.ToArray(),
                        now));
                }
                ranges.Add(new JournalStreamVersionRange(mutation.StreamKey, current.HeadVersion, version, mutation.Events.Count));
                DateTimeOffset updatedAtUtc = mutation.Events.Count == 0 ? current.UpdatedAtUtc : now;
                streams[mutation.StreamKey] = current with { HeadVersion = version, Events = events, UpdatedAtUtc = updatedAtUtc };
            }
            Invoke(JournalTestHookPhase.AfterEventWrites);

            foreach (JournalProjectionWrite write in commit.ProjectionWrites)
            {
                StreamState current = streams[write.StreamKey];
                var projections = new Dictionary<string, JournalProjectionSection>(current.Projections, StringComparer.Ordinal)
                {
                    [write.SectionName] = CreateProjection(write, current.HeadVersion, now),
                };
                VerifyProjectionLimits(write.StreamKey, projections);
                streams[write.StreamKey] = current with { Projections = projections, UpdatedAtUtc = now };
            }
            Invoke(JournalTestHookPhase.AfterProjectionWrites);

            var receipt = new JournalCommitReceipt(
                commit.Identity.OperationId,
                now,
                ranges,
                commit.ResultSchema,
                commit.ResultSchemaVersion,
                commit.ResultData.ToArray(),
                commit.ResultChecksum.ToArray());
            JournalFingerprint execution = JournalCanonicalizer.CreateCommitFingerprint(commit);
            var operations = new Dictionary<Guid, OperationState>(state.Operations)
            {
                [commit.Identity.OperationId] = new OperationState(intent.FormatVersion, intentFingerprint, execution.FormatVersion, execution.Digest.ToArray(), receipt),
            };
            Invoke(JournalTestHookPhase.BeforeCommit);
            state = new StoreState(state.StoreEpoch, streams, operations);
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return Task.FromResult(new JournalCommitResult(JournalCommitStatus.Applied, receipt));
        }
    }

    public Task<JournalSnapshot?> LoadSnapshotAsync(string streamKey, CancellationToken cancellationToken = default)
    {
        streamKey = JournalValidation.StreamKey(streamKey);
        cancellationToken.ThrowIfCancellationRequested();
        JournalValidation.Maximum(streamKey.Length, limits.StreamKeyCharacters, nameof(streamKey));
        lock (gate)
        {
            if (!state.Streams.TryGetValue(streamKey, out StreamState? stream)) return Task.FromResult<JournalSnapshot?>(null);
            if (!stream.Snapshot.HasValidChecksum) throw Corrupt(streamKey, "Stored snapshot checksum does not match its data.");
            return Task.FromResult<JournalSnapshot?>(stream.Snapshot);
        }
    }

    public Task<JournalEventPage> ReadEventsAsync(JournalEventRead read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        cancellationToken.ThrowIfCancellationRequested();
        read.Validate(limits);
        lock (gate)
        {
            if (!state.Streams.TryGetValue(read.StreamKey, out StreamState? stream))
                return Task.FromResult(new JournalEventPage(JournalEventPageStatus.NotFound, read.StreamKey, 0, Array.Empty<JournalStoredEvent>(), false));
            long throughVersion = read.ThroughVersion ?? stream.HeadVersion;
            if (throughVersion > stream.HeadVersion)
                throw new ArgumentOutOfRangeException(nameof(read), throughVersion, "Through version cannot exceed the current stream head.");
            if (read.AfterVersion < stream.RetainedFloor)
                return Task.FromResult(new JournalEventPage(JournalEventPageStatus.SnapshotRequired, read.StreamKey, throughVersion, Array.Empty<JournalStoredEvent>(), false));

            var page = new List<JournalStoredEvent>();
            int bytes = 0;
            bool stoppedByByteLimit = false;
            foreach (JournalStoredEvent storedEvent in stream.Events)
            {
                if (storedEvent.StreamVersion <= read.AfterVersion) continue;
                if (storedEvent.StreamVersion > throughVersion || page.Count == read.MaxEvents) break;
                int nextBytes = checked(bytes + storedEvent.Payload.Length);
                if (nextBytes > read.MaxBytes)
                {
                    stoppedByByteLimit = true;
                    break;
                }
                if (!storedEvent.HasValidChecksum) throw Corrupt(read.StreamKey, "Stored event checksum does not match its payload.");
                page.Add(storedEvent);
                bytes = nextBytes;
            }
            bool reachedThroughVersion = JournalValidation.ValidateEventPageContinuity(
                read.StreamKey,
                read.AfterVersion,
                throughVersion,
                page,
                page.Count == read.MaxEvents,
                stoppedByByteLimit);
            return Task.FromResult(new JournalEventPage(JournalEventPageStatus.Success, read.StreamKey, throughVersion, page, reachedThroughVersion));
        }
    }

    public Task<JournalProjectionRead> ReadProjectionsAsync(JournalProjectionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        JournalValidation.Maximum(query.StreamKey.Length, limits.StreamKeyCharacters, nameof(query));
        lock (gate)
        {
            if (!state.Streams.TryGetValue(query.StreamKey, out StreamState? stream))
                return Task.FromResult(new JournalProjectionRead(JournalProjectionReadStatus.NotFound, query.StreamKey, 0, Array.Empty<JournalProjectionSection>(), null));
            string cursor = JournalProjectionCursor.Encode(state.StoreEpoch, query.StreamKey, stream.HeadVersion);
            bool first = query.Cursor is null;
            bool valid = JournalProjectionCursor.TryDecode(query.Cursor, out Guid epoch, out string cursorStream, out long cursorHead);
            bool reset = !first && (!valid || epoch != state.StoreEpoch || !StringComparer.Ordinal.Equals(cursorStream, query.StreamKey) || cursorHead > stream.HeadVersion);
            long afterVersion = first || reset ? -1 : cursorHead;
            JournalProjectionSection[] sections = stream.Projections.Values
                .Where(value => value.SourceVersion > afterVersion)
                .OrderBy(value => value.SectionName, StringComparer.Ordinal)
                .ToArray();
            foreach (JournalProjectionSection section in sections)
                if (!section.HasValidChecksum) throw Corrupt(query.StreamKey, "Stored projection checksum does not match its data.");
            return Task.FromResult(new JournalProjectionRead(reset ? JournalProjectionReadStatus.ResetRequired : JournalProjectionReadStatus.Success, query.StreamKey, stream.HeadVersion, sections, cursor));
        }
    }

    public Task<JournalCompactionResult> CompactAsync(JournalCompaction compaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compaction);
        cancellationToken.ThrowIfCancellationRequested();
        compaction.Validate(limits);
        Invoke(JournalTestHookPhase.BeforeTransaction);
        lock (gate)
        {
            if (!state.Streams.TryGetValue(compaction.StreamKey, out StreamState? current))
                return Task.FromResult(new JournalCompactionResult(JournalCompactionStatus.NotFound, 0, 0, 0));
            long previousVersion = current.Snapshot.ThroughVersion;
            if (current.HeadVersion < compaction.ThroughVersion || compaction.ThroughVersion <= previousVersion)
                return Task.FromResult(new JournalCompactionResult(JournalCompactionStatus.VersionConflict, previousVersion, previousVersion, 0));
            Invoke(JournalTestHookPhase.AfterHeadValidation);

            DateTimeOffset now = timeProvider.GetUtcNow();
            var snapshot = new JournalSnapshot(
                compaction.StreamKey,
                compaction.ThroughVersion,
                compaction.SnapshotSchema,
                compaction.SnapshotSchemaVersion,
                compaction.SnapshotData.ToArray(),
                compaction.SnapshotChecksum.ToArray(),
                now);
            Invoke(JournalTestHookPhase.SnapshotWrittenBeforeVerification);
            if (!snapshot.HasValidChecksum) throw Corrupt(compaction.StreamKey, "Replacement snapshot checksum does not match its data.");
            Invoke(JournalTestHookPhase.SnapshotVerifiedBeforePrune);

            long retainedFloor = current.RetainedFloor;
            IReadOnlyList<JournalStoredEvent> events = current.Events;
            int pruned = 0;
            if (compaction.PruneThroughVersion is long pruneThrough)
            {
                var retained = new List<JournalStoredEvent>(current.Events.Count);
                foreach (JournalStoredEvent storedEvent in current.Events)
                {
                    if (storedEvent.StreamVersion <= pruneThrough) pruned++;
                    else retained.Add(storedEvent);
                }
                retainedFloor = Math.Max(retainedFloor, pruneThrough);
                events = retained;
            }
            var streams = new Dictionary<string, StreamState>(state.Streams, StringComparer.Ordinal)
            {
                [compaction.StreamKey] = current with { RetainedFloor = retainedFloor, Snapshot = snapshot, Events = events, UpdatedAtUtc = now },
            };
            Invoke(JournalTestHookPhase.BeforeCommit);
            state = new StoreState(state.StoreEpoch, streams, state.Operations);
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return Task.FromResult(new JournalCompactionResult(JournalCompactionStatus.Compacted, previousVersion, snapshot.ThroughVersion, pruned));
        }
    }

    public Task<JournalOperationPurgeResult> PurgeOperationsAsync(JournalOperationPurge purge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purge);
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        lock (gate)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset safeCutoff = now - minimumRetryHorizon;
            OperationState[] candidates = state.Operations.Values
                .Where(value => value.Receipt.CommittedAtUtc <= purge.CutoffUtc)
                .OrderBy(value => value.Receipt.CommittedAtUtc)
                .ThenBy(value => value.Receipt.OperationId)
                .Take(purge.MaxOperations)
                .ToArray();
            var operations = new Dictionary<Guid, OperationState>(state.Operations);
            int ineligible = 0;
            int deleted = 0;
            foreach (OperationState operation in candidates)
            {
                if (operation.Receipt.CommittedAtUtc > safeCutoff)
                {
                    ineligible++;
                    continue;
                }
                operations.Remove(operation.Receipt.OperationId);
                deleted++;
            }
            DateTimeOffset? oldest = operations.Count == 0 ? null : operations.Values.Min(value => value.Receipt.CommittedAtUtc);
            Invoke(JournalTestHookPhase.BeforeCommit);
            state = new StoreState(state.StoreEpoch, state.Streams, operations);
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            DateTimeOffset effectiveCutoff = purge.CutoffUtc < safeCutoff ? purge.CutoffUtc : safeCutoff;
            return Task.FromResult(new JournalOperationPurgeResult(candidates.Length, deleted, ineligible, oldest, now, effectiveCutoff));
        }
    }

    public Task<JournalOperationPurgeResult> PurgeOperationsByAgeAsync(
        JournalOperationAgePurge purge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(purge);
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        lock (gate)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            TimeSpan effectiveAge = purge.MinimumAge > minimumRetryHorizon ? purge.MinimumAge : minimumRetryHorizon;
            DateTimeOffset effectiveCutoff = SubtractOrMinimum(now, effectiveAge);
            OperationState[] candidates = state.Operations.Values
                .Where(value => value.Receipt.CommittedAtUtc <= effectiveCutoff)
                .OrderBy(value => value.Receipt.CommittedAtUtc)
                .ThenBy(value => value.Receipt.OperationId)
                .Take(purge.MaxOperations)
                .ToArray();
            var operations = new Dictionary<Guid, OperationState>(state.Operations);
            foreach (OperationState operation in candidates)
                operations.Remove(operation.Receipt.OperationId);
            DateTimeOffset? oldest = operations.Count == 0 ? null : operations.Values.Min(value => value.Receipt.CommittedAtUtc);
            Invoke(JournalTestHookPhase.BeforeCommit);
            state = new StoreState(state.StoreEpoch, state.Streams, operations);
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return Task.FromResult(new JournalOperationPurgeResult(
                candidates.Length,
                candidates.Length,
                0,
                oldest,
                now,
                effectiveCutoff));
        }
    }

    private static DateTimeOffset SubtractOrMinimum(DateTimeOffset value, TimeSpan duration)
        => duration > value - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : value - duration;

    public Task<Guid> RotateStoreEpochAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(JournalTestHookPhase.BeforeTransaction);
        lock (gate)
        {
            Guid epoch;
            do epoch = Guid.NewGuid(); while (epoch == state.StoreEpoch);
            Invoke(JournalTestHookPhase.BeforeCommit);
            state = new StoreState(epoch, state.Streams, state.Operations);
            Invoke(JournalTestHookPhase.AfterCommitBeforeResponse);
            return Task.FromResult(epoch);
        }
    }

    private bool TryResolve(Guid operationId, byte[] intentFingerprint, out OperationState? operation, out bool conflict)
    {
        if (!state.Operations.TryGetValue(operationId, out operation))
        {
            conflict = false;
            return false;
        }
        conflict = !FingerprintsMatch(operation.IntentFingerprint, intentFingerprint);
        if (!conflict) VerifyReceipt(operation.Receipt);
        return true;
    }

    private static JournalProjectionSection CreateProjection(JournalProjectionWrite write, long sourceVersion, DateTimeOffset now)
        => new(write.StreamKey, write.SectionName, sourceVersion, write.ProjectionSchema, write.ProjectionSchemaVersion, write.Data.ToArray(), write.DataChecksum.ToArray(), now);

    private void VerifyProjectionLimits(string streamKey, IReadOnlyDictionary<string, JournalProjectionSection> projections)
    {
        int bytes = 0;
        foreach (JournalProjectionSection projection in projections.Values) bytes = checked(bytes + projection.Data.Length);
        if (projections.Count <= limits.ProjectionSectionsPerStream && bytes <= limits.AggregateProjectionBytesPerStream) return;
        throw new JournalStoreException(
            JournalStoreFailureKind.ConstraintViolation,
            JournalStoreFailureCertainty.DefinitelyNotCommitted,
            JournalStoreFailureScope.OperationStreams,
            new[] { streamKey },
            "Projection replacement would exceed the configured per-stream limits.");
    }

    private static JournalInitializeResult ReplayInitialization(OperationState operation)
        => new(JournalInitializeStatus.Replayed, operation.Receipt);

    private static JournalCommitResult ReplayCommit(OperationState operation)
        => new(JournalCommitStatus.Replayed, operation.Receipt);

    private static void VerifyReceipt(JournalCommitReceipt receipt)
    {
        if (receipt.HasValidResultChecksum) return;
        throw new JournalStoreException(
            JournalStoreFailureKind.CorruptData,
            JournalStoreFailureCertainty.CommittedDataUnreadable,
            JournalStoreFailureScope.OperationStreams,
            receipt.Streams.Select(value => value.StreamKey).ToArray(),
            "Stored journal result checksum does not match its data.");
    }

    private static JournalStoreException Corrupt(string streamKey, string message)
        => new(
            JournalStoreFailureKind.CorruptData,
            JournalStoreFailureCertainty.CommittedDataUnreadable,
            JournalStoreFailureScope.OperationStreams,
            new[] { streamKey },
            message);

    private static bool FingerprintsMatch(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => left.Length == 32 && right.Length == 32 && CryptographicOperations.FixedTimeEquals(left, right);

    private void Invoke(JournalTestHookPhase phase) => testHook?.Invoke(phase);

    private sealed record StoreState(
        Guid StoreEpoch,
        IReadOnlyDictionary<string, StreamState> Streams,
        IReadOnlyDictionary<Guid, OperationState> Operations);

    private sealed record StreamState(
        long HeadVersion,
        long RetainedFloor,
        JournalSnapshot Snapshot,
        IReadOnlyList<JournalStoredEvent> Events,
        IReadOnlyDictionary<string, JournalProjectionSection> Projections,
        DateTimeOffset UpdatedAtUtc);

    private sealed record OperationState(
        ushort IntentFingerprintFormat,
        byte[] IntentFingerprint,
        ushort ExecutionFingerprintFormat,
        byte[] ExecutionFingerprint,
        JournalCommitReceipt Receipt);
}
