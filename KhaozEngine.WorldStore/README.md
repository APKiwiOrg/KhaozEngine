# KhaozEngine.WorldStore

Server-side persistence for an authoritative world. The package is dependency-free and contains two different
durability contracts:

- `IWorldStore` is an async keyed `byte[]` store for coarse checkpoints, transforms, cooldowns, authored world
  state, and other last-write-wins records.
- `IMutationJournalStore` is the commit-before-apply authority for crash-sensitive ownership and progression.
  It covers items, equipment, banks, currency, XP, quest milestones, durable rewards, and loot claims.

`BatchedWriter<T>` in `KhaozEngine.Persistence` remains a droppable telemetry queue. Neither `IWorldStore` nor
`BatchedWriter<T>` is an ownership authority.

## Checkpoint storage

`IWorldStore` exposes `LoadAsync`, `SaveAsync`, `SaveManyAsync`, `DeleteAsync`, and `ExistsAsync` over string keys
and byte arrays. `InMemoryWorldStore` is the thread-safe reference implementation for tests and local development.
`IEnumerableWorldStore` is the optional account-enumeration seam. Its `EnumerateAsync(keyPrefix?)` method streams
`WorldStoreEntry` values.

`SaveManyAsync` is a default interface member which loops over `SaveAsync`. A backend can override it with an
atomic batch. The engine SQLite and SQL Server backends do that. This is still checkpoint persistence. A host must
not grant ownership in memory and rely on a later checkpoint to make it durable.

`StatePersistence<TState>` wires checkpoint storage into a server lifecycle. It owns load-on-join, save-on-leave,
periodic dirty snapshots, per-key ordering, guest policy, validation quarantine, and rejoin hints. The game supplies
`IPersistenceHost<TState>`, `PersistenceBinding<TState>`, and `PersistenceCoreConfig`. `WorldPersistence` in
`KhaozEngine.NetWorld` and `TileWorldPersistence` in `KhaozEngine.TileWorld.Netcode` are its two bindings.

- `IPersistenceHost<TState>` supplies join, leave, state lookup, account lookup, and position-hint hooks from the
  server head.
- `PersistenceBinding<TState>` supplies `PositionOf`, `Encode`, `Decode`, and `Validate` for the game's record. A
  discrete binding can set `RestoreDistance` for its native coordinate type. Null preserves the continuous world's
  Euclidean `Vector3` behavior.
- `PersistenceCoreConfig` supplies cadence, key prefixes, guest policy, hint capacity, state hooks, and the
  dependency-free diagnostic sink.

`PrewarmHintsAsync(max = 0, ct)` fills the bounded rejoin hints at boot when the store implements
`IEnumerableWorldStore`. It decodes and validates each record before using its position, skips guests and quarantine
copies, and returns the number of seeded accounts. Call it on the server thread before polling starts because
`PositionHintCache` is not thread-safe.

Durable providers are opt-in sibling packages. `KhaozEngine.WorldStore.Sqlite` supplies `SqliteWorldStore` for
embedded development, tests, and single-node hosts. `KhaozEngine.WorldStore.SqlServer` supplies
`SqlServerWorldStore` for SQL Server and Azure SQL. Both implement `IEnumerableWorldStore` and override
`SaveManyAsync` with an atomic provider batch.

## Durable mutation journal

The journal namespace is `KhaozEngine.WorldStore.Journal`. A server validates a command against committed live
state, freezes one deterministic operation, submits it without waiting for storage, and applies it only after a
durable completion. A successful client response is never sent before commit.

Mutation completion is the durable commit boundary. Live state reflects committed results only. If a client
disconnects after commit, the mutation is still durable. While its operation receipt remains inside the configured
retention horizon, a retry with the same stable operation ID and frozen intent returns the original result without
applying the effect twice.

### Store API

`IMutationJournalStore` has seven methods:

| Method | Contract |
|---|---|
| `ResolveOperationAsync(identity, ct)` | Restart-safe lookup by operation ID and intent. Returns `NotFound`, `Replayed`, or `OperationConflict`. A retained replay carries the original receipt and result. |
| `InitializeAsync(initialization, ct)` | The only stream-creation path. Stores a version-zero snapshot and projections. Returns `Initialized`, `Replayed`, `ExistingStream`, or `OperationConflict`. |
| `CommitAsync(commit, ct)` | Atomically commits every touched stream, event, changed projection, receipt, and result. Returns `Applied`, `Replayed`, `VersionConflict`, or `OperationConflict`. |
| `LoadSnapshotAsync(streamKey, ct)` | Returns the current checked snapshot, or `null` for an absent stream. Callers must check `HasValidChecksum` before reduction. |
| `ReadEventsAsync(read, ct)` | Reads one bounded contiguous page. Returns `Success`, `SnapshotRequired`, or `NotFound`. |
| `ReadProjectionsAsync(query, ct)` | Reads one selected stream. Returns `Success`, `ResetRequired`, or `NotFound`. |
| `CompactAsync(compaction, ct)` | Replaces a verified snapshot and optionally prunes an eligible event prefix. Returns `Compacted`, `NotFound`, or `VersionConflict`. |

`IMutationJournalMaintenance` is a separate operational seam:

| Method | Contract |
|---|---|
| `PurgeOperationsAsync(purge, ct)` | Deletes a bounded batch of replay rows at or before the requested UTC cutoff. The configured minimum retry horizon is enforced against provider time. Events remain. |
| `RotateStoreEpochAsync(ct)` | Replaces the store epoch while writers are quiesced. Every cursor from the prior history then requires reset. |

The in-memory reference store implements both interfaces:

```csharp
using KhaozEngine.WorldStore.Journal;

IMutationJournalStore store = new InMemoryMutationJournalStore(
    limits: JournalLimits.Maximum,
    minimumRetryHorizon: TimeSpan.FromHours(24));
```

Use `KhaozEngine.WorldStore.Sqlite` for embedded and single-node storage. Use
`KhaozEngine.WorldStore.SqlServer` for SQL Server or Azure SQL.

### Request values

All request and result objects own their byte arrays. Constructors copy caller buffers. Byte properties return
copies through `ReadOnlyMemory<byte>`. Collections are copied, validated, and exposed read-only. Mutating the source
array or list after construction cannot change a fingerprint, capacity charge, stored operation, or returned value.

| Type | Meaning |
|---|---|
| `JournalOperationIdentity` | Stable `Guid` operation ID, authenticated scope, action kind, and normalized intent bytes. The same intent must keep all four values across retries. |
| `JournalInitialization` | Identity, one absent stream key, version-zero snapshot, version-zero projections, and stable result. |
| `JournalCommit` | Identity, one or more stream mutations, replacement projections, and stable result. `OwnedByteCount` is the executor admission charge. |
| `JournalStreamMutation` | Stream key, expected version, and ordered events. A zero-event mutation is a read constraint and cannot write projections. |
| `JournalEvent` | Game-owned event type, positive schema version, payload, and SHA-256 checksum. Event order is significant. |
| `JournalProjectionWrite` | Full replacement of one game-owned section at the stream's resulting version. It is not a patch or an event. |

Identity strings accept `[A-Za-z0-9._:/-]` only. Stream keys allow 256 ASCII characters. Scope, action, event type,
schema, and section names allow 128 ASCII characters. Schema versions are positive. Stream versions are
non-negative, with version zero reserved for initialization and the first event at version one.

`JournalCanonicalizer` is the one format implementation used by every backend. It exposes
`CreateIntentFingerprint`, `CreateCommitFingerprint`, `CreateInitializationFingerprint`, `ComputeSha256`, and
`VerifySha256`. `JournalFingerprint` exposes the format version, canonical bytes, and digest. Format version one
uses the `KJIF`, `KJEF`, and `KJNF` tagged binary envelopes. A game should not invent or persist its own substitute.

### Results and recovery values

| Type | Meaning |
|---|---|
| `JournalCommitReceipt` | Operation ID, database commit time, ordered stream ranges, result schema, result bytes, checksum, and replay flag. `HasValidResultChecksum` verifies the result. |
| `JournalStreamVersionRange` | Stream key plus before version, after version, and event count. Eventless constraints have equal versions and count zero. |
| `JournalCommitResult` | `JournalCommitStatus` plus a receipt for `Applied` or `Replayed`. |
| `JournalInitializeResult` | `JournalInitializeStatus` plus a receipt for `Initialized` or `Replayed`. |
| `JournalOperationResolution` | `JournalOperationResolutionStatus` plus a receipt only for `Replayed`. |
| `JournalSnapshot` | Snapshot schema, through version, bytes, checksum, creation time, and `HasValidChecksum`. |
| `JournalEventRead` | Stream key, exclusive `afterVersion`, optional fixed `throughVersion`, event cap, and byte cap. |
| `JournalEventPage` | Status, captured through version, ordered events, first and last version, returned bytes, and completion flag. |
| `JournalStoredEvent` | Stream and operation coordinates, event schema, payload, checksum, commit time, and `HasValidChecksum`. |
| `JournalProjectionQuery` | One stream key and an optional opaque cursor. |
| `JournalProjectionRead` | Status, captured head, changed sections, new cursor, and returned bytes. |
| `JournalProjectionSection` | Section identity, source version, schema, bytes, checksum, update time, and `HasValidChecksum`. |
| `JournalCompaction` | Complete snapshot through a version and an optional prune-through version no greater than it. |
| `JournalCompactionResult` | Status, prior snapshot version, stored snapshot version, and pruned event count. |
| `JournalOperationPurge` | UTC cutoff and positive batch limit. |
| `JournalOperationPurgeResult` | Scanned, deleted, ineligible, and oldest-retained readings. |

Recovery loads the latest snapshot, verifies it, then reads events in bounded pages. The first page captures a
fixed `ThroughVersion`. Every continuation repeats that value. A sequence gap, checksum failure, or unsupported
mandatory game schema blocks the session and quarantines its stream. `SnapshotRequired` means compaction removed
the requested prefix, so recovery restarts from the newer snapshot.

Projection cursors are opaque. They bind the store epoch, stream key, and captured head. A missing cursor returns
all current sections. A valid cursor returns only sections newer than its captured head. A cursor from another
epoch or stream, or one ahead of the current head, returns `ResetRequired` with all current sections and a new
cursor. `NotFound` clears the caller's cached model.

### Limits

`JournalLimits.Maximum` contains the engine maxima. Construct `JournalLimits` to configure every value downward.
Requests are rejected before I/O when a caller-visible limit is exceeded. A provider can raise
`ConstraintViolation` after locking stored projection metadata if the resulting stored section set would exceed
its configured limit.

| Limit | Engine maximum |
|---|---:|
| Streams per operation | 16 |
| Events per operation | 128 |
| Projection writes per operation | 64 |
| Projection sections per stream | 64 |
| Normalized intent | 64 KiB |
| Event payload | 256 KiB |
| Operation result | 64 KiB |
| Projection section | 2 MiB |
| Snapshot | 8 MiB |
| Events per read page | 2,048 |
| Aggregate commit bytes | 8 MiB |
| Aggregate event page bytes | 8 MiB |
| Aggregate projection bytes per stream | 8 MiB |
| Stream key | 256 ASCII characters |
| Other identity field | 128 ASCII characters |

### Failure contract

Expected races use result statuses. Provider, transport, schema, and corruption failures throw
`JournalStoreException` with three exhaustive dimensions:

- `Kind` is `Unavailable`, `Timeout`, `Deadlock`, `Cancelled`, `UnknownOutcome`, `CorruptData`,
  `SchemaMismatch`, or `ConstraintViolation`.
- `Certainty` is `DefinitelyNotCommitted`, `Unknown`, or `CommittedDataUnreadable`.
- `Scope` is `OperationStreams` with exact `StreamKeys`, or `WholeStore` with no stream keys.

`DefinitelyNotCommitted` availability, timeout, deadlock, and cancellation failures can retry the identical
frozen operation. `Unknown` means the commit may exist. Resolve the same identity, then retry the same frozen
operation only when resolution says `NotFound`. Never allocate a new operation ID or rebuild random values,
timestamps, ordering, expected versions, projections, or result bytes. `CommittedDataUnreadable` means stored
bytes failed verification. Quarantine the named streams and do not report success.

### Bounded executor

`MutationJournalExecutor` keeps storage latency off the simulation thread. Its constructor requires an
`IMutationJournalStore` and `JournalExecutorOptions`. Every host must set a positive worker count, operation
capacity, and owned-byte capacity. There is no unbounded default. Options also set the bounded transient retry
count and the initial and maximum backoff. Unknown outcomes continue through resolve and identical retry because
ending a retry budget cannot establish whether the commit happened.

| Member | Contract |
|---|---|
| `Submit(commit)` | Freezes and validates the complete commit. Returns `Accepted`, `StreamBusy`, `Backpressure`, or `Stopping`. It never waits on database I/O. |
| `TryDequeueCompletion(out completion)` | Lets the simulation thread drain terminal results. `JournalCompletion` carries the frozen commit and either a result or a fatal failure. |
| `AcknowledgeCompletion(id, acknowledgement)` | Accepts `Handled` or `Quarantined` after a dequeued completion. Only this releases admission bytes and stream reservations. |
| `ReleaseQuarantine(streamKeys)` | Reopens a fully recovered quarantine group atomically. Every stream quarantined by one operation must be released together. |
| `StopAsync(gracePeriod, ct)` | Stops admission and gives workers a bounded drain period. `JournalShutdownResult` lists every unresolved or unacknowledged operation and its total admitted bytes. |

Accepted work is never evicted. Pending and completed-but-unacknowledged work share the same operation and byte
capacity. Streams stay reserved while storage retries and until acknowledgement. A fatal completion quarantines
its streams. The host must reload and verify snapshot plus tail before calling `ReleaseQuarantine`.

Executor values are immutable host handoff records. `JournalSubmission` exposes `Status`, `OperationId`,
`AdmittedByteCount`, and `IsAccepted`. `JournalCompletion` exposes the operation ID, frozen commit, touched stream
keys, terminal result or failure, and `IsFatal`. `JournalShutdownResult` exposes ordered unresolved operation IDs and
their admitted byte total. `JournalCompletionAcknowledgement` contains `Handled` and `Quarantined`.

`MutationJournalExecutorMetrics` exposes submission and result counters, retry counts by failure kind, queue
operations and bytes, oldest pending age, reserved streams, unacknowledged completions, quarantine count, a commit
latency histogram, recovery tail readings, compaction lag, and projection latency and section readings. Payloads,
results, display names, and raw account IDs do not belong in logs or metric labels.

`GetRetryCount` reads one failure-kind counter. `GetCommitLatencyHistogram` returns a
`JournalCommitLatencyHistogram` whose upper bounds and bucket counts are copied read-only collections.
`RecordLoad` and `RecordProjectionRead` let host recovery and admin paths add their readings to the same metrics
surface.

### Host integration order

The game owns every method named `Game...` in this example. They stand for game validation, isolated reduction,
client routing, and recovery. The journal calls are the engine API.

```csharp
// On the simulation thread, after authenticating the command.
JournalCommit frozen = GameValidateAndFreeze(command, committedLiveState);
JournalSubmission submission = executor.Submit(frozen);

// At the start of a later frame, under a fixed completion budget.
while (executor.TryDequeueCompletion(out JournalCompletion? completion))
{
    if (completion.Failure is not null)
    {
        GameBlockAndRecover(completion.StreamKeys, completion.Failure);
        executor.AcknowledgeCompletion(
            completion.OperationId,
            JournalCompletionAcknowledgement.Quarantined);
        continue;
    }

    JournalCommitResult result = completion.Result!;
    if (result.Receipt is JournalCommitReceipt receipt)
    {
        // Verify every range starts at the live committed version.
        // Apply into isolated copies, then swap all affected states together.
        GameApplyOnlyContiguousCommittedResult(receipt, completion.Commit);
        GameReplyToClient(receipt.ResultData);
    }
    else
    {
        GameHandleNoWriteConflict(result.Status);
    }

    executor.AcknowledgeCompletion(
        completion.OperationId,
        JournalCompletionAcknowledgement.Handled);
}
```

The order is fixed: validate on the simulation thread, freeze deterministic identity and intent, submit without
blocking, drain at the start of a frame, apply only contiguous committed versions, reply, then acknowledge. A
replayed receipt already represented by live versions returns its original response without reducing its events
again while that receipt is retained. A gap forces journal catch-up before gameplay resumes. If isolated reduction
or the atomic live-state swap fails, acknowledge `Quarantined`, block every touched stream, and recover them as one
group.

### Multi-host restriction

The first release permits a multi-stream mutation only when one orchestration host can update or invalidate every
affected live session. Database atomicity does not update RAM in another process. A cross-host trade requires
durable committed-event delivery or a forced reload by the remote owner before either client receives success.

### Compaction and retention

The first release uses snapshot-only compaction. Pass `null` for `JournalCompaction.PruneThroughVersion`. No game
may set an event prune boundary until it supplies a retention policy and proves every durable external consumer has
passed that version. A verified snapshot must exist before any eligible prefix is removed.

Replay-row retention is separate from event retention. Configure a retry horizon for the longest interval in which
a client or durable server cause may retry the same intent. Never purge operation rows inside that horizon. After
purge, `ResolveOperationAsync` may return `NotFound` for a committed operation. Its events remain. Durable game-domain
state, expected-version checks, and consumed-source validation are the permanent duplicate defense after replay
rows expire. A consumed loot source must stay consumed.

After a point-in-time restore, quiesce all journal hosts, rotate the store epoch, verify snapshots and stream
continuity, reconcile external consumers, then reopen writers. This makes every old admin cursor return
`ResetRequired` instead of silently skipping restored history.

### Operations

Deploy and validate the provider schema before opening mutation traffic. Use a migration identity for DDL and a
restricted runtime identity for journal reads and writes. Provider READMEs list the required setup. Keep connection
strings and database credentials in the deployment secret store, prefer managed identity where available, and never
write secrets into source, logs, metrics, or crash reports.

Run snapshot verification and stream-continuity checks during restore drills. A checksum or sequence fault is not
skipped. Quarantine the stream, refuse the affected session, preserve the corrupt rows for diagnosis, restore or
rebuild from a verified snapshot and tail, then release the complete quarantine group. Do not hand-edit a stream
head past damaged data.

Export the executor and maintenance readings. Alert on sustained backpressure, increasing oldest pending age,
retry storms by failure kind, operation conflicts, version conflicts, unacknowledged completion growth, quarantined
streams, replay or checksum failures, recovery tail growth, compaction lag, projection latency, and selected-stream
projection failures. Use hashed stream identifiers and bounded-cardinality labels.

### Projection safety

Projection bytes are opaque game-owned server data. They can contain PII and are not browser DTOs. An admin endpoint
must authenticate and authorize the operator, audit the lookup, parse with a versioned game codec, enforce schema and
size bounds, redact fields the operator may not see, and return a shaped response. Never send raw projection bytes
to an untrusted browser.

Poll only the selected stream while its detail view is active. Do not add an all-player projection endpoint. Do not
publish inventory, bank, skills, or quest snapshots on simulation ticks. Online presence is separate transient
state and cannot overwrite a durable projection.
