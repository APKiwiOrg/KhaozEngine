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

- `IPersistenceHost<TState>` supplies join, leave, state lookup, account lookup, durable-key lookup, and
  position-hint hooks from the server head. Existing hosts keep using the account ID through default interface
  members.
- `PersistenceBinding<TState>` supplies `PositionOf`, `Encode`, `Decode`, and `Validate` for the game's record. A
  discrete binding can set `RestoreDistance` for its native coordinate type. Null preserves the continuous world's
  Euclidean `Vector3` behavior.
- `PersistenceCoreConfig` supplies cadence, key prefixes, guest policy, hint capacity, state hooks, the optional
  `PersistenceKeyResolver`, and the dependency-free diagnostic sink. A capable host binds the resolver output once
  after authentication and before spawn. The core then uses that immutable key for load, save, prewarm, hints,
  quarantine, and in-flight ordering while retaining the authenticated account ID for stale-session checks.

Optional `PersistenceCoreConfig.LoadFallback` is a `PersistenceFallbackLoad` returning `Task<byte[]?>` in the
primary record format. It runs only after a successful primary read returns null, never for corrupt or invalid
primary records or read failures. Null preserves spawn-on-miss behavior. `PersistenceLoadRequest` captures the
original `Slot`, `AuthenticatedAccountId`, bound `PersistenceKey` and full primary `StoreKey` on the host thread.
The hook may continue off that thread and must use captured identities rather than live slot state. Converted
bytes follow normal decoding, session and seat guards, validation, quarantine, host-thread application and hints.
An accepted fallback stays dirty until its first successful primary save, including an unchanged leave or periodic
pass. Exceptions surface through `OnStoreError` and retain the load guard. Guests and boot hint prewarming never
invoke the hook. Legacy data is neither deleted nor retired by this seam.

`PrewarmHintsAsync(max = 0, ct)` fills the bounded rejoin hints at boot when the store implements
`IEnumerableWorldStore`. It decodes and validates each record before using its position, skips guests and quarantine
copies, and returns the number of seeded accounts. Call it on the server thread before polling starts because
`PositionHintCache` is not thread-safe.

Durable providers are opt-in sibling packages. `KhaozEngine.WorldStore.Sqlite` supplies `SqliteWorldStore` for
embedded development, tests, and single-node hosts. `KhaozEngine.WorldStore.SqlServer` supplies
`SqlServerWorldStore` for SQL Server and Azure SQL. Both implement `IEnumerableWorldStore` and override
`SaveManyAsync` with an atomic provider batch.

## Durable mutation journal

The journal namespace is `KhaozEngine.WorldStore.Journal`. A server validates a command against the admitted view
(committed state plus the operations already admitted on the stream), freezes one deterministic operation, submits
it without waiting for storage, and may present it on the submitting tick while the durable commit follows behind.
A commit built with `presentAtCommit: true` keeps the older contract, so nothing is shown and no client success is
sent before commit. That is the fleet rule for value moving between players.

Mutation completion is the durable commit boundary. Live state may run ahead of it by the admitted uncommitted
operations, and a failed commit corrects the view and supersedes the operations queued behind it. If a client
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

`IMutationJournalMaintenance` is a separate operational seam. Providers which can evaluate receipt age against
their own authoritative clock also implement the additive `IMutationJournalAgeMaintenance` capability:

| Method | Contract |
|---|---|
| `PurgeOperationsAsync(purge, ct)` | Deletes a bounded batch of replay rows at or before the requested UTC cutoff. SQL Server and SQLite clip that cutoff to database UTC minus the configured minimum retry horizon. Events remain. |
| `IMutationJournalAgeMaintenance.PurgeOperationsByAgeAsync(purge, ct)` | Deletes an oldest-first bounded batch by age. SQL Server and SQLite derive the cutoff from their database UTC clock and enforce the longer of the requested age or configured minimum retry horizon. Events remain. |
| `RotateStoreEpochAsync(ct)` | Replaces the store epoch while writers are quiesced. Every cursor from the prior history then requires reset. |

`IMutationJournalStreamListing` is an optional capability for operator tools that sweep a whole store, such as
a copy, a rehearsal, an audit, or a migration check. It keeps such a tool on the interface instead of on the
provider's tables. The in-memory, SQLite, and SQL Server stores implement it. Feature-detect it with
`store is IMutationJournalStreamListing`:

| Method | Contract |
|---|---|
| `ListStreamsAsync(query, ct)` | Reads one bounded page of stream keys, head versions, and creation and update times in ordinal key order, optionally restricted to an ordinal, case-sensitive key prefix. Listing never writes. |

```csharp
var query = new JournalStreamQuery(maxStreams: 500, keyPrefix: "player/");
while (true)
{
    JournalStreamPage page = await listing.ListStreamsAsync(query);
    foreach (JournalStreamEntry stream in page.Streams)
        await RehearseAsync(stream.StreamKey, stream.HeadVersion);
    if (page.ContinuationKey is null) break;
    query = new JournalStreamQuery(500, "player/", page.ContinuationKey);
}
```

`ContinuationKey` is the last key on a page when more streams follow and null when the listing is complete. Pass it
back as `afterStreamKey` with the same prefix. Pages are keyset continuations rather than one snapshot, so a stream
created ahead of the continuation key appears on a later page and one created behind it does not. A page holds at
most `JournalStreamQuery.MaximumStreamsPerPage` (1,000) streams.

Each `JournalStreamEntry` also carries `CreatedAtUtc` and `UpdatedAtUtc`, both nullable UTC times. The creation time
is when `InitializeAsync` created the stream. It is null for a stream older than journal schema version 3 whose
initialization snapshot compaction had already replaced, and for one an older writer created during the upgrade,
because nothing proves when those were created. The update time moves when the stream is initialized, when a commit
appends events to it, and when a compaction with a prune boundary runs over it. An eventless read constraint and a
snapshot-only compaction leave it alone. The in-memory, SQLite, and SQL Server stores always fill it, so it is null
only from the two-argument constructor a test fake may still use.

A sweep usually points at a source that must not be written to. Both SQL providers open read only with
`SchemaMode = ReadOnly` (`SqliteJournalSchemaMode.ReadOnly` or `SqlServerJournalSchemaMode.ReadOnly`). That mode
issues no DDL, reports a missing or older schema as `SchemaMismatch` instead of creating or migrating it, and makes
`InitializeAsync`, `CommitAsync`, `CompactAsync`, `PurgeOperationsAsync`, `PurgeOperationsByAgeAsync`, and
`RotateStoreEpochAsync` throw `NotSupportedException` before any I/O. Reads, operation resolution, and listing work
as usual. Do not hand a read only store to `MutationJournalExecutor`.

The in-memory reference store implements the store, both maintenance interfaces, and the listing:

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

Hot request reads use the additive `ReadOnlySpan<byte>` accessors without making another copy. The existing
`ReadOnlyMemory<byte>` properties keep their CLR shape and defensive-copy behavior. A span is a scoped read-only
view over the object's owned storage, so it cannot expose a mutable array or be retained on the heap.

| Type | Zero-copy accessors |
|---|---|
| `JournalOperationIdentity` | `NormalizedIntentSpan` |
| `JournalEvent` | `PayloadSpan`, `PayloadChecksumSpan` |
| `JournalProjectionWrite` | `DataSpan`, `DataChecksumSpan` |
| `JournalCommit` | `ResultDataSpan`, `ResultChecksumSpan` |
| `JournalCompaction` | `SnapshotDataSpan`, `SnapshotChecksumSpan` |

| Type | Meaning |
|---|---|
| `JournalOperationIdentity` | Stable `Guid` operation ID, authenticated scope, action kind, and normalized intent bytes. The same intent must keep all four values across retries. |
| `JournalInitialization` | Identity, one absent stream key, version-zero snapshot, version-zero projections, and stable result. |
| `JournalCommit` | Identity, one or more stream mutations, replacement projections, and stable result. `OwnedByteCount` is the executor admission charge. `PresentAtCommit` marks an operation the consumer must not present before its commit. `QueueBehindAdmitted` is true by default and false asks for the pre-queue `StreamBusy` answer instead. |
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
| `JournalOperationAgePurge` | Minimum receipt age and positive batch limit. |
| `JournalOperationPurgeResult` | Scanned, deleted, ineligible, oldest-retained, evaluation-time, and effective-cutoff readings. The two clock readings are populated by the age API. |
| `JournalResetResult` | The store epoch a journal reset kept, and the rows it deleted from each of the six data tables. `RowsDeleted` totals them and `Summary` is one operator line. |
| `JournalStreamQuery` | Page size from 1 through 1,000, optional key prefix, and optional exclusive `afterStreamKey` continuation. |
| `JournalStreamPage` | Streams in ordinal key order and a continuation key, which is null when the listing is complete. |
| `JournalStreamEntry` | Stream key, current head version, and the nullable UTC creation and update times. |

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

### Refusals

`JournalStoreException.Reason` is a nullable `JournalStoreRefusal` beside `Kind`, `Certainty`, `Scope` and
`StreamKeys`. The legacy constructor remains available for consumer-built exceptions and leaves `Reason` null.
The reason-taking overload accepts the same failure dimensions, stream keys, message and optional inner cause.
Stream keys remain sorted owned snapshots backed by a fresh boxed immutable array, even for immutable input.
Messages are developer prose. Engine construction is fenced across WorldStore, SQLite and SQL Server.
Reset context wrapping retains the original exception as its cause, every public identifier and all owned
collections, including stream keys. The completeness test names a new identifier if the copy drops it and
checks that caller mutation and collection casts cannot change either exception's snapshots.
Use the rule and its identifiers to distinguish the following refusals.

| Reason | What is refused | Additional identifiers |
|---|---|---|
| `SnapshotChecksumMismatch` | Stored snapshot data fails verification, including replacement verification before pruning. | `StreamKey` |
| `EventChecksumMismatch` | Stored event payload fails verification. | `StreamKey`, `StreamVersion` |
| `ProjectionChecksumMismatch` | Stored projection data fails verification. | `StreamKey`, `SectionName` |
| `ResultChecksumMismatch` | A stored operation result fails verification. | `OperationId`, existing `StreamKeys` |
| `EventSequenceGap` | An event does not have the next expected stream version. | `StreamKey`, `ExpectedVersion`, `ActualVersion` |
| `EventPageIncomplete` | A page exhausted its rows before the captured through version, without hitting its count or byte limit. | `StreamKey`, `ExpectedThroughVersion`, `ActualThroughVersion` |
| `ProjectionSectionCountExceeded` | Stored projections exceed the engine section cap, or replacement would exceed the configured stream cap. | `StreamKey`, `ActualCount`, `MaximumCount` |
| `ProjectionByteCountExceeded` | Stored projections exceed the engine byte cap, or replacement would exceed the configured stream cap. | `StreamKey`, `ActualBytes`, `MaximumBytes` |
| `ProviderOperationFailed` | SQLite or SQL Server failed a runtime provider operation. | `Stage`, typed `ProviderCode`, original `InnerException` |
| `CommitOutcomeUnknown` | A runtime commit completed or may have completed before its response failed. | `Stage`, original `InnerException` |
| `OperationCancelled` | A runtime operation was cancelled. | `Stage`, original `InnerException` |
| `ProviderClockUnavailable` | The provider did not return its UTC clock. | `ProviderName` |
| `ReplacementSnapshotVersionMismatch` | A replacement snapshot has the wrong through version before pruning. | `StreamKey`, `ExpectedVersion`, `ActualVersion` |
| `ResetWriteLockTimedOut` | SQLite reset could not take its write lock within the timeout. | `LockTimeout`, original `InnerException` |
| `StoredEpochInvalid` | The stored epoch cannot be read as a GUID. | Existing whole-store scope |
| `MetadataUpdateNotApplied` | Epoch rotation did not update exactly one metadata row, including a trigger that ignores the update. | Existing whole-store scope |
| `ReplacementSnapshotDataMismatch` | SQL Server replacement data differs from the requested bytes before pruning. | `StreamKey` |
| `TransportFailed` | A runtime I/O or socket failure before a transaction or after confirmed rollback. | `Stage`, original `InnerException` |
| `WriteFailedAfterRollback` | A non-provider commit failure after confirmed rollback. | `Stage`, original `InnerException` |
| `StreamSnapshotMissing` | A stream being compacted has no snapshot, including replacement verification before pruning. | `StreamKey` |
| `StoredStreamKeyInvalid` | A provider row has an invalid stream key. | `Index`, `ParameterName` (`streamKey`) |
| `StoredHeadVersionNegative` | A provider row has a negative head version. | `Index`, `ActualVersion` |
| `StoredTimestampNonUtc` | A provider row timestamp has a nonzero UTC offset. | `Index`, `FieldName` (`createdAtUtc` or `updatedAtUtc`) |
| `StoredStreamOrderInvalid` | The kept rows are duplicate or out of ordinal key order. | `Index` of the latter row |
| `SchemaMissing` | No schema exists in a mode that cannot create one. | `RequiredMigration` |
| `SchemaVersionUnsupported` | The refusing mode cannot use the declared schema version. | `ActualVersion`, `SupportedVersions`, `RequiredMigration` |
| `SchemaMetadataMissing` | The schema metadata row or version value is missing. | `RequiredMigration` |
| `SchemaVersionMalformed` | The schema version is not an integer. | `MetadataType`, `RequiredMigration` |
| `SchemaMetadataUnreadable` | SQLite could not read or validate schema metadata. | `RequiredMigration`, original `InnerException` |
| `SchemaForeignKeysDisabled` | The connection has foreign key enforcement disabled. | `RequiredMigration` |
| `SchemaJournalModeUnsupported` | A writer connection uses neither WAL nor memory mode. | `ActualMode`, `RequiredMode` (`wal`), `RequiredMigration` |
| `SchemaObjectInventoryMismatch` | The schema object count differs from the expected inventory. | `ExpectedVersion`, complete `MissingObjects` and `UnexpectedObjects`, `RequiredMigration` |
| `SchemaObjectMissing` | The inventory count matches but a required object is missing. | `ObjectName`, `ExpectedVersion`, `RequiredMigration` |
| `SchemaObjectDefinitionMismatch` | An object's definition differs from its supported shape. | `ObjectName`, `ExpectedVersion`, `RequiredMigration` |
| `HostForeignKeyActionUnsafe` | A reset would fire a host key's delete action. | Complete ordered `HostObjects` |
| `HostTriggerUnsafe` | A reset would fire a host trigger. | Complete ordered `HostObjects` |
| `DefinitionUnavailable` | SQL Server cannot expose an unencrypted definition. | `ObjectName`, `RequiredPermission` (`VIEW DEFINITION`) |
| `ApplicationLockNotGranted` | SQL Server did not grant an application lock. | `ReturnCode`, existing `Kind` distinguishes timeout, cancellation, deadlock and unavailable |
| `SchemaMetadataShapeInvalid` | SQL Server has no readable version column. | `RequiredMigration` |
| `SchemaColumnsMismatch` | SQL Server columns differ from the supported shape. | `ExpectedVersion`, complete sorted `MissingObjects` and `UnexpectedObjects`, `RequiredMigration` |
| `SchemaIndexesMismatch` | SQL Server indexes differ from the supported shape. | `ExpectedVersion`, complete sorted `MissingObjects` and `UnexpectedObjects`, `RequiredMigration` |
| `SchemaForeignKeysMismatch` | SQL Server foreign keys differ from the supported shape. | `ExpectedVersion`, complete sorted `MissingObjects` and `UnexpectedObjects`, `RequiredMigration` |
| `SchemaChecksMismatch` | SQL Server check constraints differ from the supported shape. | `ExpectedVersion`, complete sorted `MissingObjects` and `UnexpectedObjects`, `RequiredMigration` |
| `SchemaDefaultsMismatch` | SQL Server defaults differ from the supported shape. | `ExpectedVersion`, complete sorted `MissingObjects` and `UnexpectedObjects`, `RequiredMigration` |
| `SchemaTriggersMismatch` | SQL Server triggers differ from the supported shape. | `ExpectedVersion`, complete sorted `MissingObjects` and `UnexpectedObjects`, `RequiredMigration` |
| `SchemaEpochEmpty` | SQL Server metadata has an empty store epoch. | `RequiredMigration` |
| `SchemaCancelled` | The caller cancelled SQL Server schema admission or validation. | `Stage` |
| `SchemaProviderFailure` | SQL Server failed during schema admission or validation. | `Stage`, typed `ProviderCode`, original `InnerException` |
| `SchemaRollbackUnconfirmed` | A failed SQL Server schema initialization could not confirm rollback. | Existing unknown kind and certainty, original `InnerException` |

SQLite schema refusals name migration `sqlite-journal-v3-row-timestamps`. Unsupported versions report the supported
version set `[3]`. Object names include their kind, such as `table:journal_stream` or
`index:ix_journal_event_operation`. Inventory differences are ordinally sorted. A count mismatch is reported before
individual definitions, and schema metadata is read before object validation. Version two accepts each object's
version-two or already-migrated version-three shape.

`SupportedVersions`, `MissingObjects`, `UnexpectedObjects` and `HostObjects` copy their inputs into fresh boxed
immutable arrays. A SQLite reset lists all unsafe foreign keys in provider order by host table, referenced table
and delete action, followed by all unsafe triggers ordered by name. Its reason is the first finding's reason.
Each `JournalHostObjectFinding` carries its own `Reason` and `ReferencedTable`. A foreign key also carries
`HostTable` and `DeleteAction`, with `TriggerName` null. A trigger carries `TriggerName`, with `HostTable` and
`DeleteAction` null. The complete list survives when both kinds are present. SQL Server key findings also carry
`HostSchema` and `ForeignKeyName`, and are ordered by schema, host table and key name.

SQL Server shares the schema missing, metadata missing, malformed version and unsupported version members and their
identifier sets with SQLite. Its migration is `sqlserver-journal-v3-row-timestamps`. Shape-set members are distinct
from SQLite's inventory count and per-definition checks. Each difference retains the entire compared identity,
including types, lengths, nullability, collation, index columns, key actions, trust flags or normalized definitions.
Version-two column differences exclude optional columns already present in their exact version-three shape.
`SchemaVersionUnsupported.SupportedVersions` is `[3]` on version admission, or the compared version on the final
metadata recheck. `Stage` names writer connection open, transaction begin, initialization, read-only open or
read-only validation through `JournalOperationStage`. A cancelled provider call exposes cancellation, with no
`ProviderCode`. A provider failure retains its original `SqlException.Number` without parsing its message.

Runtime `Stage` distinguishes an operation without a captured transaction, transaction work, commit and the
post-commit response. SQLite `ProviderCode` is the original `SqliteException.SqliteErrorCode`, never parsed prose.
SQL Server runtime provider codes come directly from `SqlException.Number`. Unknown commits carry only the stage
rather than varying their identifier set by the inner exception type. SQL Server reset context retains the reason,
failure dimensions, every identifier and owned collection snapshot, with the original refusal as its inner cause.
Projection limits are inclusive. The engine read caps are 64 sections and 8 MiB per stream, while write limits
may be configured lower. Both paths use the same limit reasons and identifiers, with their existing failure kind
and certainty distinguishing stored corruption from a refused replacement. If count and bytes both exceed their
limits, section count is reported first. Stored-row indices are zero-based. Each row's key, head and timestamps are checked before page ordering,
including the extra row used to establish a continuation. Ordering applies only to the kept rows. These stored-row
refusals retain their original argument exception as the inner cause and have whole-store scope, without inventing
an affected stream key. The in-memory compaction's replacement snapshot checksum check is an internal invariant:
its sealed immutable compaction owns both the copied data and the checksum computed from that data.
SQLite's private operation-delete flag also has an internal overlap invariant. The connection lease serializes
its opens, no callback runs before opening it, and every admitted purge or reset closes it in `finally`.

A write call on a store opened read only throws `NotSupportedException` instead. That is a caller error rather than
a store failure, so it sits outside this contract and nothing was attempted.

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
| `Submit(commit)` | Freezes and validates the complete commit, then queues it behind whatever its streams already carry. Returns `Accepted`, `StreamBusy`, `Backpressure`, `VersionConflict`, or `Stopping`. It never waits on database I/O. |
| `SeedCommitted(streamKey, committedVersion, sections)` | Declares what the store durably holds for one stream, which is the `JournalProjectionRead` the consumer already performs when it loads that stream. Seeding again replaces the baseline, rebuilds the admitted layer and clears a pending forget request. |
| `RequestForgetStream(streamKey)` | Returns `JournalStreamForgetStatus.Unknown` when no view is held, `Forgotten` when an empty stream drops immediately, or `Deferred` until every admitted operation is acknowledged. |
| `ForgetStream(streamKey)` | Requests the same removal through the existing bool API. True only when forgotten immediately, or false when unknown or deferred. Busy streams defer instead of throwing. |
| `TryGetAdmittedProjection(streamKey, sectionName, out section)` | Reads one section as the executor sees it now, the newest admitted uncommitted write or the committed baseline. Synchronous and safe from the simulation thread. |
| `TryGetAdmittedStream(streamKey, out stream)` | Reads the whole view of one stream: committed version, admitted head version, in-flight operation count, and every section. |
| `TryDequeueCompletion(out completion)` | Lets the simulation thread drain terminal results. `JournalCompletion` carries the frozen commit and either a result, a fatal failure, or neither when it was superseded. |
| `AcknowledgeCompletion(id, acknowledgement)` | Accepts `Handled` or `Quarantined` after a dequeued completion. Only this releases admission bytes and stream reservations. |
| `ReleaseQuarantine(streamKeys)` | Reopens a fully recovered quarantine group atomically. Every stream quarantined by one operation must be released together. |
| `StopAsync(gracePeriod, ct)` | Stops admission and gives workers a bounded drain period. `JournalShutdownResult` lists every unresolved or unacknowledged operation and its total admitted bytes. |

Accepted work is never evicted. Pending and completed-but-unacknowledged work share the same operation and byte
capacity. Streams stay reserved while storage retries and until acknowledgement. A fatal completion quarantines
its streams. The host must reload and verify snapshot plus tail before calling `ReleaseQuarantine`, and re-seed
those streams before it submits against them again.

### Admitted state

Each stream carries an ordered queue of admitted operations. An operation is dispatched to the store only when it
is at the head of every one of its queues, which happens once each operation ahead of it has reached a terminal
result and been acknowledged, so one stream commits in admission order. `ExpectedVersion` is checked against the
admitted head version, which is the version the operation ahead will produce, so a queued chain is contiguous. A
mismatch is refused at admission with `VersionConflict`. `JournalExecutorOptions.StreamQueueDepth` caps one
stream's queue and answers `Backpressure` beyond it. `StreamBusy` still answers a quarantined stream and a commit
that set `QueueBehindAdmitted` to false.

Over that queue the executor keeps a live view of the projection sections: the committed baseline
`SeedCommitted` declared, with every admitted uncommitted `JournalProjectionWrite` layered over it in order. The
executor never reads the store to build it and advances the baseline as commits are acknowledged `Handled`. A
stream nobody seeded is still tracked while it has admitted operations, adopting the first operation's expected
version as its baseline and forgotten once its queue empties.

An owner leaving calls `RequestForgetStream` or `ForgetStream` even while work is queued, running or terminal but
unacknowledged. A deferred request retains the view and queue until the final acknowledgement, including every
superseded completion. It cancels no work and preserves admission order across all touched streams. A reconnecting
owner calls `SeedCommitted`, which clears that stream's pending request so the new owner's view survives the older
work draining. Forgetting does not release quarantine or replace the required recovery of its whole group.

A terminal failure (fatal, quarantined, `VersionConflict`, or `OperationConflict`) corrects the view. Every stream
the operation touched drops back to its committed baseline plus the operations still queued ahead of it, every
operation queued behind it is refused with a `SupersededByFailure` completion without ever reaching the store, and
the failed operation's own completion carries a `JournalCorrection`. Transient retries correct nothing: the
operation is still admitted and the store is still being asked.

Executor values are immutable host handoff records. `JournalSubmission` exposes `Status`, `OperationId`,
`AdmittedByteCount`, `IsAccepted`, `AdmittedStreams` (a `JournalAdmittedStreamHead` per touched stream) and
`ChangedSections` (a `JournalProjectionSectionKey` per replaced section). `JournalCompletion` exposes the operation
ID, frozen commit, touched stream keys, terminal result or failure, `IsFatal`, a `JournalCompletionKind` of
`Committed`, `Fatal` or `SupersededByFailure`, the `Correction` a failure carries, and `SupersededBy`.
`JournalCorrection` names the failed operation, the rolled-back streams, the sections to resync and the superseded
operation IDs. `JournalAdmittedSection` carries the section identity, bytes, source version and an `IsCommitted`
flag. `JournalAdmittedStream` carries the committed version, admitted head version, in-flight operation count and
sections. `JournalShutdownResult` exposes ordered unresolved operation IDs, including operations that queued and
never started, and their admitted byte total. `JournalCompletionAcknowledgement` contains `Handled` and
`Quarantined`.

Admitted uncommitted operations die with the process. A client-originated operation resubmits under the same
operation ID after reconnect. Set `PresentAtCommit` on anything whose early presentation could mint value across
accounts, which is a player trade or a contested loot claim.

`MutationJournalExecutorMetrics` exposes submission and result counters, retry counts by failure kind, queue
operations and bytes, oldest pending age, reserved streams, unacknowledged completions, quarantine count, admitted
uncommitted depth with its per-stream peak and oldest age, correction and superseded counts, a commit latency
histogram, recovery tail readings, compaction lag, and projection latency and section readings. Payloads,
results, display names, and raw account IDs do not belong in logs or metric labels.

The executor maintains all-admitted and uncommitted indexes in admission sequence. Admission appends to both.
Terminal completion removes only the uncommitted node, and acknowledgement removes the retained admitted node.
The live counts and oldest ages therefore update in constant work even when completions and acknowledgements arrive
out of order. `OldestPendingAge` includes a terminal completion until acknowledgement.
`AdmittedUncommittedPeakPerStream` is a lifetime high-water mark. It is not a live maximum, which would require a
depth-indexed structure or a scan across tracked streams.

`GetRetryCount` reads one failure-kind counter. `GetCommitLatencyHistogram` returns a
`JournalCommitLatencyHistogram` whose upper bounds and bucket counts are copied read-only collections.
`RecordLoad` and `RecordProjectionRead` let host recovery and admin paths add their readings to the same metrics
surface.

### Host integration order

The game owns every method named `Game...` in this example. They stand for game validation, isolated reduction,
client routing, and recovery. The journal calls are the engine API.

```csharp
// Once, when the stream is loaded.
JournalProjectionRead loaded = await journal.ReadProjectionsAsync(new JournalProjectionQuery(streamKey));
executor.SeedCommitted(streamKey, loaded.HeadVersion, loaded.Sections);

// On the simulation thread, after authenticating the command.
JournalCommit frozen = GameValidateAndFreeze(command, executor);
JournalSubmission submission = executor.Submit(frozen);
if (submission.IsAccepted && !frozen.PresentAtCommit)
    GamePresentNow(submission.AdmittedStreams, submission.ChangedSections);

// At the start of a later frame, under a fixed completion budget.
const int completionBudget = 32;
for (int drained = 0;
     drained < completionBudget && executor.TryDequeueCompletion(out JournalCompletion? completion);
     drained++)
{
    if (completion.Correction is JournalCorrection correction)
        GameResyncAndNotice(correction);

    if (completion.IsSuperseded)
    {
        // Nothing this operation described happened and it never reached the store.
        executor.AcknowledgeCompletion(
            completion.OperationId,
            JournalCompletionAcknowledgement.Handled);
        continue;
    }

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

The host owns `completionBudget`, keeps it positive, and selects it to fit the simulation tick budget.

The order is fixed: validate on the simulation thread against the admitted view, freeze deterministic identity and
intent, submit without blocking, present at admission unless the commit set `PresentAtCommit`, drain at the start
of a frame, apply only contiguous committed versions, reply, then acknowledge. A
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
rows expire. A consumed loot source must stay consumed. Production retention requires
`IMutationJournalAgeMaintenance` and uses `PurgeOperationsByAgeAsync`, which receives a duration rather than a host
timestamp. Durable providers record a
separate database-owned retention start and derive the effective cutoff from that same database clock. Public
receipt and event timestamps keep their existing `TimeProvider` semantics. The cutoff API remains for compatibility
with controlled callers. Durable providers clip its absolute cutoff to their database clock and retry horizon, but
the by-age API remains the production policy seam because it accepts the intended duration directly. Provider
delete guards reject operation-row deletion outside a current purge transaction. This makes a still-running
version-one maintenance host roll back its child and parent deletes after migration.

After a point-in-time restore, quiesce all journal hosts, rotate the store epoch, verify snapshots and stream
continuity, reconcile external consumers, then reopen writers. This makes every old admin cursor return
`ResetRequired` instead of silently skipping restored history.

### Journal reset

A release that wipes all game data empties the journal with `SqliteJournalReset.ResetAsync` or
`SqlServerJournalReset.ResetAsync`. Each takes a connection string, validates the version-three schema without creating
or migrating it, and deletes every row of `journal_stream`, `journal_event`, `journal_snapshot`,
`journal_projection`, `journal_operation`, and `journal_operation_stream` in one transaction. `journal_metadata` is
left exactly as it was, so the schema version, the store epoch, and the metadata row's times survive.

```csharp
JournalResetResult reset = await SqlServerJournalReset.ResetAsync(connectionString);
Console.WriteLine(reset.Summary);
```

The result names the rows deleted from each table and the epoch that was kept. A second reset answers zero for every
table. The reset opens the provider's operation delete guard itself, the way the purge does, and closes it again.
A game never names the guard.

The reset refuses contention. It takes the lock every journal writer holds for its own transaction, the SQLite
write lock or the SQL Server maintenance application lock, waits up to a lock timeout, and is then refused with a
whole-store `Timeout` that deleted nothing. An idle host holds no lock, so drain and stop every journal host, and
every client that may retry an operation, before a reset. Every replay receipt goes, so a retry of an operation
committed before the reset resolves `NotFound` and is not recognized as a replay.

The reset refuses to reach outside the journal. A game table's foreign key into a journal data table declared
`CASCADE`, `SET NULL`, or `SET DEFAULT` would fire on the deletes and delete or rewrite the game's own rows, and so
would a game trigger on a journal data table. The reset refuses either with a whole-store `SchemaMismatch` that names
it and deleted nothing. SQLite reports these as `HostForeignKeyActionUnsafe` or `HostTriggerUnsafe`, with every
finding in `HostObjects`. A `NO ACTION` key fires nothing: a game row that still references a stream fails the delete
instead, and the whole reset rolls back with `ConstraintViolation`. Clear those rows, or drop the key, first.

A projection cursor binds the store epoch, the stream key, and its captured head. The reset keeps the epoch, so
the store cannot tell a cursor taken before it from one taken after it. A read of a deleted stream returns
`NotFound`. After a stream is initialized again under the same key, an old cursor ahead of the new head returns
`ResetRequired`, but one at or below it reads as valid and skips every section at or below its captured head. Drop
every cursor held before the reset and read again with none. When a consumer cannot be reached, call
`RotateStoreEpochAsync` after the reset, and every old cursor then returns `ResetRequired`.

`InMemoryMutationJournalStore` has no reset, as the content catalog's in-memory store has none. Its journal lives as
long as the instance, so a new instance is the wipe, and it carries a new epoch.

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

## Deferred player placement

`IPersistenceHost<TState>.TrySetPlayerState` reports whether a loaded pose was admitted and published.
`TryResetToConfiguredSpawn` does the same for the reset after a rejected record. Existing hosts keep
their synchronous behavior through default implementations. Admission-aware hosts return false
without changing state while the destination is unavailable.

`StatePersistence<TState>` retains that record, session token and save guard for retry once per drain.
It applies no game blob, hint or baseline before placement succeeds. Every retry rechecks the session
and seat, so a deferred load cannot affect a recycled slot. A failed reset also retains its guard.
Flush finishes its actual IO without spinning on unresolved placement.
