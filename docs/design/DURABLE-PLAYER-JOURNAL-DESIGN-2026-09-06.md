# Durable player mutation journal design

**Status:** R1 code targets KhaozEngine 18.31.0. The in-memory, SQLite, SQL Server, recovery, process-kill,
scaling, benchmark, and packaging gates passed. Publication is authoritative only when the remote `v18.31.0` tag
and package exist. Current API and operations guidance lives in the
[`KhaozEngine.WorldStore` package README](../../KhaozEngine.WorldStore/README.md) and
[`USING-KHAOZENGINE.md`](../USING-KHAOZENGINE.md). This document keeps the rationale. Engine program
[#835](https://github.com/APKiwiOrg/KhaozEngine/issues/835), first consumer
[Grimhollow #137](https://github.com/APKiwiOrg/Grimhollow/issues/137), sibling adoption
[Ruinborne #467](https://github.com/APKiwiOrg/Ruinborne/issues/467).

## 1. Decision

KhaozEngine adds a durable mutation journal beside `IWorldStore`. It is the authority for
crash-sensitive MMO state such as item ownership, equipment, banks, currency, experience, quest progress,
and durable loot claims.

The server remains the gameplay authority. It validates an action against server-owned state, submits the
resulting mutation to the journal, and applies that result to live state only after the database commit is
known. The client does not become authoritative, and a successful response means the mutation is durable.

The journal stores small ordered events and only the projection sections changed by each mutation. It does
not serialize every connected player on a tick. Admin tools read a selected player's committed projection
from the database and request only sections newer than their cursor.

`IWorldStore` remains the right primitive for coarse checkpoints, transforms, cooldowns, authored world
state, and other last-write-wins records. `BatchedWriter<T>` remains the right primitive for non-critical
logs which may drop under pressure. Neither primitive can be the authority for item ownership.

## 2. Why this exists

Grimhollow currently puts skills, bag, worn equipment, bank, quests, and health into one opaque player blob.
The engine checks it every persistence pass and Grimhollow requests a save every 30 seconds. Normal leave
and graceful shutdown save again, but a process loss can roll accepted state back to the last checkpoint.
The admin inspection publisher also rebuilds every online player's full detail document on every 6 Hz tick.
At 1,000 players that fixed scan performs roughly 2.1 million slot and skill reads per second before JSON
allocation and transport.

Ruinborne is further ahead. Inventory and equipment are relational and most inventory operations commit in
SQL transactions before the new bag is sent to the client. The remaining gap is the operation boundary.
Loot pickup can commit the item and then lose the reply, kill rewards span several independent writes, and
the forensic economy ledger uses a drop-oldest batcher. Those pieces cannot prove one durable outcome after
a crash or retry.

Both games need the same infrastructure facts:

- an operation has one stable identity
- replaying the same operation within the receipt-retention horizon cannot apply it twice
- reusing an identity for different input is an explicit conflict
- every stream advances monotonically
- one operation can commit across several streams atomically
- an unknown database outcome is retried with the same identity
- accepted work is never silently dropped
- recovery starts from a checked snapshot and replays an ordered tail
- admin reads do not touch the simulation hot path

Those facts belong in the engine. Event meanings, reducers, balance rules, and player-facing errors remain in
the game.

## 3. Goals and non-goals

### 3.1 Goals

- Exactly one durable effect for an operation submitted one or many times within its retention window.
- Durable source state which independently prevents duplicate ownership after idempotency records age out.
- Atomic single-player and multi-player mutations.
- Strict per-stream ordering with optimistic version checks.
- A bounded asynchronous executor whose tick cost scales with submitted and completed mutations.
- In-memory, SQLite, and SQL Server implementations with one conformance suite.
- Snapshot, replay, and crash-safe compaction.
- Versioned opaque projection sections suitable for selected-player admin reads.
- An explicit migration path from existing `IWorldStore` blobs and relational character rows.
- Metrics and failure injection strong enough to operate the feature in production.

### 3.2 Non-goals

- A generic gameplay event model. Games name and encode their own events.
- A client-side inventory authority or optimistic client grant.
- A replacement for every `IWorldStore` record.
- A message broker, analytics warehouse, or permanent human-readable audit product.
- Cross-database atomic transactions.
- Active-active writes to one player across eventually consistent regions.
- Direct arbitrary SQL callbacks inside an engine transaction.
- Tick snapshots of inventory, banks, skills, or quests.

## 4. Ownership boundary

### 4.1 Engine owns

- operation IDs and canonical request fingerprints
- per-stream versions and event sequence allocation
- retention-bounded idempotent replay and identity conflict detection
- atomic commit across one or more streams
- projection section writes in the same transaction
- snapshot storage, checksums, replay pages, and compaction
- the bounded commit executor, retry policy, stream reservations, and completion queue
- backend schemas and the shared conformance contract
- persistence metrics and structured failure categories

### 4.2 Game owns

- authoritative validation and balance rules
- event names, payloads, schemas, and reducers
- result payloads returned to gameplay code
- stream naming below the engine's limits
- which projection sections a mutation replaces
- client command identity and durable server-side cause identity
- player-facing retry or unavailable messages through localization
- existing relational read models downstream of the journal
- admin authorization, audit, and presentation

### 4.3 Authority versus durability

The host is authoritative because it decides whether an action is legal and what it means. The journal makes
that decision durable. Host authority alone does not protect an in-memory grant from process loss.

An ownership-changing action follows this order:

```text
tick validates intent against committed server state
  -> game builds deterministic events, result, and changed projections
  -> executor accepts the operation and reserves every touched stream
  -> database commits all rows in one transaction
  -> completion is drained at the start of a later host frame
  -> game applies the committed result to live state
  -> server acknowledges success to the client
```

The simulation thread never waits on database latency. A player with a reserved stream cannot start another
mutation against that stream until completion. The game may retain the later intent and revalidate it after
the first completion. This keeps live state, expected versions, and committed state aligned without an
off-thread gameplay reducer.

Live state carries the committed version of every stream it represents. A completion applies its events only
when its receipt begins at the live version and advances it contiguously. While the operation receipt is retained,
a replay whose resulting version is already reflected in live state returns the original response but does not
reduce the events a second time. A receipt ahead of live state by more than its own event range forces a journal
catch-up before gameplay resumes.
This rule prevents a late duplicate response from rolling live state back or granting twice.

### 4.4 Durability classes

Games classify state deliberately rather than sending every tick field through the journal:

- commit-before-apply: item ownership, bank and equipment changes, currency, XP, levels, quest milestones,
  durable rewards, and loot-source claims
- checkpointable: position, facing, cooldowns, and combat health when the game has an explicit restart policy
- transient: open panels, current hover or target, interpolation state, and connection presence

Checkpointable state can remain in `IWorldStore` or use coarse journal events at meaningful boundaries such as
combat end and logout. It is not allowed to share authority with commit-before-apply sections. Admin screens
label checkpoint age for those fields and use a separate presence source when truly live transient state is
needed. This keeps high-frequency combat and movement off the ownership commit path without pretending their
latest in-memory value survived a host loss.

## 5. Core contract

The contract lives in `KhaozEngine.WorldStore.Journal`. The existing WorldStore backend packages implement it,
so no database driver enters the dependency-free core package.

The names below are binding for the implementation plan. Small constructor or collection-shape adjustments
are allowed when tests show a clearer .NET API, but the status and atomicity semantics are not optional.

```csharp
public interface IMutationJournalStore
{
    Task<JournalOperationResolution> ResolveOperationAsync(
        JournalOperationIdentity identity,
        CancellationToken cancellationToken = default);

    Task<JournalInitializeResult> InitializeAsync(
        JournalInitialization initialization,
        CancellationToken cancellationToken = default);

    Task<JournalCommitResult> CommitAsync(
        JournalCommit commit,
        CancellationToken cancellationToken = default);

    Task<JournalSnapshot?> LoadSnapshotAsync(
        string streamKey,
        CancellationToken cancellationToken = default);

    Task<JournalEventPage> ReadEventsAsync(
        JournalEventRead read,
        CancellationToken cancellationToken = default);

    Task<JournalProjectionRead> ReadProjectionsAsync(
        JournalProjectionQuery query,
        CancellationToken cancellationToken = default);

    Task<JournalCompactionResult> CompactAsync(
        JournalCompaction compaction,
        CancellationToken cancellationToken = default);
}

public interface IMutationJournalMaintenance
{
    Task<JournalOperationPurgeResult> PurgeOperationsAsync(
        JournalOperationPurge purge,
        CancellationToken cancellationToken = default);

    Task<Guid> RotateStoreEpochAsync(
        CancellationToken cancellationToken = default);
}

public interface IMutationJournalAgeMaintenance : IMutationJournalMaintenance
{
    Task<JournalOperationPurgeResult> PurgeOperationsByAgeAsync(
        JournalOperationAgePurge purge,
        CancellationToken cancellationToken = default);
}
```

`JournalOperationIdentity` contains a `Guid` operation ID, an authenticated scope, an action kind, and opaque
normalized intent bytes. The game defines the intent bytes once. The engine validates and fingerprints the
complete identity. Scope binds the request to its tenant, world, account, or server cause. Action kind prevents
two eventless commands with the same payload from becoming one identity.

`ResolveOperationAsync` is the restart-safe replay path. It looks up the operation ID before gameplay rebuilds
an old execution. `NotFound` permits current-state validation and a new `JournalCommit`. While the operation row is
retained, a matching identity returns `Replayed` with the original receipt, result, and per-stream version ranges.
A different identity returns `OperationConflict`. No caller needs to reconstruct old expected versions,
projections, random rolls, or result bytes merely to learn that a retained prior attempt committed.

`JournalInitialization` contains an operation identity, one absent stream key, a version-zero snapshot, zero
or more version-zero projection sections, and a stable result. It is the only operation which creates a
stream. Two initializers racing for one stream produce one winner. While the operation row is retained, replaying
the winner returns its original receipt. A different initializer sees `ExistingStream` and must load the journal
rather than overwrite it.

`JournalInitializeStatus` has `Initialized`, `Replayed`, `ExistingStream`, and `OperationConflict`. An existing
stream is never treated as a successful replay unless the operation ID and intent fingerprint match. Stream
version zero is the baseline and the first appended event receives version one.

`JournalCommit` contains:

- one operation identity
- one or more stream mutations, each with a stream key and expected version
- zero or more ordered events per stream
- zero or more replacement projection sections tied to a touched stream and its resulting version
- a result schema ID, result schema version, and opaque result bytes

A stream mutation with no events is a read constraint. It allows a durable business rejection to be recorded
against the exact committed state that was validated. It does not advance the stream head, cannot write a
projection, and records equal before and after versions in the receipt. A normal successful mutation appends
at least one event.

Each event has an event type, event schema version, and opaque payload. Event order within one stream mutation
is significant. Stream mutations and projection sections are canonicalized by ordinal key order before the
engine computes the execution fingerprint. Length prefixes are part of the canonical bytes. Duplicate stream
keys or duplicate section names in one request are rejected before I/O.

The engine computes two SHA-256 fingerprints. The intent fingerprint covers the operation identity and decides
replay versus `OperationConflict`. The execution fingerprint covers the exact commit or initialization
envelope, including the intent fingerprint, affected state bytes, projections, and result. It verifies the exact
bytes chosen by the committing host. Neither covers database timestamps because those are assigned at commit.
Callers cannot supply either fingerprint. Every backend uses the core canonicalizer.

The full request must be deterministic across retries. Local timestamps, fresh random values, and newly ordered
collections cannot be regenerated after an unknown outcome. Such values are allocated once as part of the
client intent or durable server cause, then reused byte for byte.

### 5.1 Commit results

`JournalCommitStatus` has exactly four states:

- `Applied`: the transaction committed now
- `Replayed`: a retained operation with the same ID and intent fingerprint already committed, with the original
  receipt and result
- `VersionConflict`: no operation row exists and at least one expected stream version differs, with no writes
- `OperationConflict`: the operation ID exists with another intent fingerprint, with no writes

`Applied` and `Replayed` return the same `JournalCommitReceipt`: operation ID, commit timestamp, resulting
before version, after version, and event count for every touched stream, result schema, result schema version,
and exact result bytes. An eventless range has equal before and after versions and an event count of zero.

`JournalOperationResolutionStatus` has `NotFound`, `Replayed`, and `OperationConflict`. Only `Replayed` carries
a receipt. Every path which returns stored result bytes, including resolution, commit replay, and initialization
replay, verifies the stored result checksum first.

Invalid keys, duplicate request members, and configured size-limit violations throw before I/O. Database
unavailability, timeout, cancellation after admission, corrupt stored bytes, and unknown commit outcome use a
typed `JournalStoreException`. They are not represented as `VersionConflict` and must not be translated into a
player-visible success.

Only committed operations get an operation row. A version conflict can therefore be revalidated and retried
with the same operation identity and a newly canonicalized execution. If a prior attempt actually committed,
the retained stored intent fingerprint decides replay versus operation conflict and its original execution wins.

### 5.2 Identity sources

An operation ID must survive every retry which could describe the same intent:

- user actions use a client-generated random `Guid` with authenticated account scope, action kind, and stable
  normalized intent bytes
- server-triggered rewards use an ID stored with their durable cause
- a loot claim uses its own claimant-intent ID and carries the durable loot-source ID in its intent
- a transfer uses one operation ID across both player streams

Client IDs are untrusted input. Authentication decides which streams may be named, and request size limits are
applied before database admission.

Operation rows guarantee replay during a configured retry horizon. Domain state remains the lifetime defense.
For example, a loot source permanently records its claimant or consumed state. Purging an old operation row
must not make the source claimable again.

### 5.3 Fingerprint format

Fingerprint format version one is a tagged binary envelope, not runtime object serialization:

- four ASCII magic bytes, `KJIF` for intent, `KJEF` for commit execution, or `KJNF` for initialization
- a two-byte unsigned format version in big-endian order
- fields in ascending two-byte unsigned tag order
- each field as tag, four-byte unsigned big-endian byte length, then bytes
- `Guid` values in RFC 4122 network byte order
- integers in two's-complement big-endian order at their declared width
- counts as four-byte unsigned big-endian values
- strings as strict UTF-8 without BOM, with invalid UTF-16 rejected and no Unicode normalization
- optional fields as a one-byte zero or one marker followed by the value when present
- byte payloads as a four-byte length followed by exact bytes

Intent tags are operation ID at 1, authenticated scope at 2, action kind at 3, and normalized intent bytes at
4. A normal commit execution uses `KJEF` and tags intent fingerprint at 1, sorted stream mutations at 2, sorted
projection writes at 3, result schema at 4, result schema version at 5, and result bytes at 6. A stream entry is
stream key, expected version, event count, then events in game order. An event is type, schema version, and
payload. A projection is stream key, section name, schema, schema version, and data. Nested entries use the same
tag-length-value rule.

Initialization uses the distinct `KJNF` magic and tags intent fingerprint at 1, absent stream key at 2,
snapshot schema at 3, snapshot schema version at 4, snapshot bytes at 5, sorted version-zero projections at 6,
result schema at 7, result schema version at 8, and result bytes at 9. The stored operation kind is `Commit` or
`Initialization`, matching the execution magic. Golden vectors cover all three envelopes.

Identity strings accepted by the store use printable ASCII from `[A-Za-z0-9._:/-]`. Payloads remain arbitrary
bytes and can encode Unicode game data. The operation row stores intent and execution fingerprint format
versions beside both 32-byte hashes. Golden byte and digest vectors are shared by every backend. A rolling
upgrade supports every format version whose operation rows remain inside retention.

### 5.4 Byte ownership

Every journal value type owns its bytes. Public constructors deep-copy intent, event, projection, result, and
snapshot buffers. They check counts, references, individual lengths, and aggregate lengths before allocating
copies, then perform content validation and canonicalization only against the private copies. The executor
accounts those owned bytes at admission and never retains a caller-owned mutable array. Store implementations
copy again only where an ADO.NET provider requires ownership beyond the call. Read APIs return immutable values
backed by private copies, never provider buffers or internal in-memory-store arrays.

## 6. Transaction model

Every backend implements this logical transaction:

1. Freeze the request bytes, canonicalize it, and compute both fingerprints.
2. Lock or reserve the operation ID.
3. If it exists, compare intent fingerprints and return `Replayed` or `OperationConflict`.
4. Lock touched stream heads in ordinal stream-key order.
5. Compare every expected version. Return `VersionConflict` without writes if any differ.
6. Allocate consecutive event versions and append every event.
7. Advance each stream head by its event count. Leave an eventless read constraint unchanged.
8. Replace only the supplied projection sections at their exact resulting stream versions. Reject a projection
   tied to an eventless stream mutation.
9. Insert the operation receipt and its per-stream result versions.
10. Commit once.

No result is visible before step 10. No subset is allowed to survive a failed transaction.

SQL Server uses a serializable transaction and `UPDLOCK, HOLDLOCK` key-range locks. Key columns use
`Latin1_General_100_BIN2` so all backends agree on ordinal case-sensitive identity. Deadlock victims are known
to have rolled back and retry the whole transaction with the same operation ID and frozen request under a
small bounded backend policy.

SQLite uses `SqliteStoreConnection` and one immediate transaction under its existing connection lease. The
in-memory backend uses one lock around its model and clones caller byte arrays at both boundaries.

A successful commit followed by a lost response is an unknown outcome, not a failed action. While the operation
receipt is retained, retrying the same operation returns `Replayed` with the original result. After operation-row
purge, `ResolveOperationAsync` may return `NotFound`, but the retry protocol still uses the same stable ID and frozen
intent. Operation purge leaves events in place. Durable domain state plus expected-version and consumed-source
validation are the permanent duplicate defense. Generating a fresh ID after an unknown outcome is a bug.

Operation resolution verifies `result_sha256` before returning a replay. A failed result checksum is permanent
corruption. Every stream listed by the stored receipt is quarantined until operator repair or a verified replay
rebuild resolves it.

### 6.1 Failure contract

`JournalStoreException` carries three exhaustive fields:

- `Kind`: `Unavailable`, `Timeout`, `Deadlock`, `Cancelled`, `UnknownOutcome`, `CorruptData`, `SchemaMismatch`,
  or `ConstraintViolation`
- `Certainty`: `DefinitelyNotCommitted`, `Unknown`, or `CommittedDataUnreadable`
- `Scope`: `OperationStreams` with exact stream keys, or `WholeStore`

Retry behavior is derived in the core, not chosen independently by each backend:

| Provider situation | Kind | Certainty | Executor action |
|---|---|---|---|
| Cannot open connection or begin transaction | `Unavailable` | `DefinitelyNotCommitted` | Retry same operation |
| Deadlock victim with confirmed rollback | `Deadlock` | `DefinitelyNotCommitted` | Retry same operation |
| Timeout before transaction begins | `Timeout` | `DefinitelyNotCommitted` | Retry same operation |
| Timeout or broken connection while commit outcome is unavailable | `UnknownOutcome` | `Unknown` | Resolve, then retry same operation if absent |
| Cancellation before any transaction work | `Cancelled` | `DefinitelyNotCommitted` | Retry or return to a direct caller |
| Checksum or stored sequence failure | `CorruptData` | `CommittedDataUnreadable` | Quarantine affected streams |
| Unsupported database schema | `SchemaMismatch` | `DefinitelyNotCommitted` | Stop the store and require migration |
| Provider constraint outside the four normal statuses, with rollback confirmed | `ConstraintViolation` | `DefinitelyNotCommitted` | Quarantine and report a code defect |

At any point after transaction start, a successful rollback makes the outcome `DefinitelyNotCommitted` while
preserving the original failure kind. A cancellation, timeout, or connection loss whose rollback cannot be
confirmed is `UnknownOutcome`. Any failure while sending or awaiting commit is also `UnknownOutcome` unless the
provider proves the transaction rolled back.

Provider conformance tests pin each available error mapping. Once `MutationJournalExecutor` returns `Accepted`,
caller cancellation no longer owns the operation and no public cancellation token can abandon it. Graceful
shutdown controls admission and drain. Forced process termination relies on stable identity and restart
resolution.

## 7. Storage schema

Backend names follow each provider's naming conventions. The logical schema is:

### `journal_metadata`

- one well-known primary key
- `schema_version`
- `store_epoch`, a random `Guid`
- `updated_at_utc`

The store epoch identifies the current restored history and is part of every admin cursor.

### `journal_stream`

- `stream_key`, primary key
- `current_version`, non-negative 64-bit integer
- `updated_at_utc`

### `journal_event`

- `stream_key`
- `stream_version`
- `operation_id`
- `operation_ordinal`
- `event_type`
- `event_schema_version`
- `payload`
- `payload_sha256`
- `committed_at_utc`
- primary key on `(stream_key, stream_version)`
- index on `operation_id`

### `journal_operation`

- `operation_id`, primary key
- `operation_kind`
- `intent_fingerprint_format`
- `intent_fingerprint`, exactly 32 bytes
- `execution_fingerprint_format`
- `execution_fingerprint`, exactly 32 bytes
- `result_schema`
- `result_schema_version`
- `result_data`
- `result_sha256`
- `committed_at_utc`
- index on `(committed_at_utc, operation_id)`

### `journal_operation_stream`

- `operation_id`
- `stream_key`
- `before_version`
- `after_version`
- `event_count`
- primary key on `(operation_id, stream_key)`

### `journal_snapshot`

- `stream_key`, primary key
- `through_version`
- `snapshot_schema`
- `snapshot_schema_version`
- `data`
- `data_sha256`
- `created_at_utc`

### `journal_projection`

- `stream_key`
- `section_name`
- `source_version`
- `projection_schema`
- `projection_schema_version`
- `data`
- `data_sha256`
- `updated_at_utc`
- primary key on `(stream_key, section_name)`
- index on `(stream_key, source_version)`

The SQL Server package ships an idempotent version-one create script plus a schema-version check. Automatic
creation is allowed for development and fresh deployments. A detected older incompatible schema fails boot
with the required migration named. Production deployments can use validate-only mode so the application does
not need DDL permission.

There is no foreign key from retained events to `journal_operation`. Event rows keep their operation ID after
an expired replay record is purged. `journal_operation_stream` is deleted before its parent operation inside
the maintenance transaction.

Maintenance methods take an exclusive store-maintenance gate. `RotateStoreEpochAsync` is allowed only while
normal writers are quiesced and records the new random epoch in the metadata row before traffic resumes.

### 7.1 Initial limits

Limits are validated in the core and configurable downward by a host:

| Limit | Engine maximum |
|---|---:|
| Streams per operation | 16 |
| Events per operation | 128 |
| Projection writes per operation | 64 |
| Normalized intent bytes | 64 KiB |
| Event payload | 256 KiB |
| Operation result | 64 KiB |
| Projection section | 2 MiB |
| Snapshot | 8 MiB |
| Events per read page | 2,048 |
| Aggregate commit bytes | 8 MiB |
| Aggregate event read page | 8 MiB |
| Aggregate projections per stream | 8 MiB |
| Projection sections per stream | 64 |
| Stream key | 256 ASCII characters |
| Type, schema, scope, action, or section name | 128 ASCII characters |

The intent limit applies before both commit and `ResolveOperationAsync`, so a replay lookup cannot copy or hash
an unbounded client identity. The 256-character stream key plus 128-character section name consumes at most
768 bytes in a SQL Server
`NVARCHAR` composite key, below its 900-byte primary-key limit. Maximum-length and non-ASCII rejection are
backend conformance cases. Aggregate limits prevent the larger per-field maxima from combining into an
unbounded allocation or SQL transaction. A commit also verifies that its replacement sections leave the
stream at no more than 64 sections and 8 MiB total. Request-local size failures throw before I/O. A stored-state
projection limit discovered only after locking current section metadata rolls back and raises
`ConstraintViolation` with `DefinitelyNotCommitted`. Grimhollow and Ruinborne fit far below these limits.

## 8. Recovery and compaction

Recovery never performs an unbounded journal read:

1. Load the latest snapshot, if present, and verify its checksum.
2. Start at `snapshot.through_version`, or zero for an initialized empty snapshot.
3. The first event read captures one immutable `throughVersion` from the stream head.
4. Read ordered event pages with an exclusive lower version, that same `throughVersion`, and fixed count and
   byte limits.
5. Verify continuity, payload checksum, schema support, and the captured final version.
6. Reduce each event through game code.
7. Refuse the player session if any gap, unknown mandatory schema, or corrupt payload remains.

`JournalEventPage` reports the immutable `throughVersion`, first and last returned versions, returned bytes,
and whether that version has been reached. Every continuation must repeat `throughVersion`, and the backend
queries only events at or below it. A read after compaction which starts below the retained floor returns
`SnapshotRequired` instead of pretending the missing prefix is an empty page. Recovery then restarts from the
new snapshot.

An absent stream is not an empty existing stream. Games either initialize a new character from authored
defaults or migrate a validated legacy record to a version-zero baseline.

`CompactAsync` receives a complete snapshot through version N, its checksum is computed by the engine, and an
optional prune-through version no greater than N. In one transaction it verifies the stream head is at least
N, replaces an older snapshot, and deletes eligible events only after the new snapshot row exists. A crash
therefore leaves either the old snapshot and old tail, or the new snapshot and its remaining tail.

R1 defaults to snapshot-only compaction. Event pruning is enabled only when the game supplies an operation-row
retention policy and proves all durable external consumers have passed N. Projection sections are not rebuilt
from pruned events during an admin read.

Operation retention uses `IMutationJournalAgeMaintenance.PurgeOperationsByAgeAsync`. A production purge accepts a
minimum age and bounded batch size. Durable providers record a separate retention start with the database clock,
derive the cutoff from that clock inside the purge transaction, enforce the longer of caller age and configured
minimum retry horizon, and select through the retention-time index. Version-one rows migrate with retention starting
at migration time, so an upgrade cannot expire a replay receipt early. Public receipt timestamps keep their existing
host `TimeProvider` meaning. The cutoff-based API remains for compatibility with controlled callers.
Replay retention is independent of event retention
because events deliberately keep operation IDs without a foreign key. The transaction deletes
`journal_operation_stream` rows before their operation rows while leaving any retained events untouched. The
purge reports scanned, deleted, and ineligible counts plus its oldest retained timestamp, database evaluation time,
and effective cutoff. Metrics expose
retention age and backlog. Conformance pins the no-younger-than-horizon rule and bounded deletion order.

Snapshot cadence is based on events and bytes, not wall-clock player scans. Suggested starting thresholds are
1,000 events or 4 MiB of tail data for a player stream. A low-activity player creates no checkpoint traffic.

## 9. Bounded asynchronous executor

`MutationJournalExecutor` lives in the dependency-free core beside the store contract. It accepts a complete
`JournalCommit`, not a gameplay delegate. Game validation and reduction stay on the simulation thread.

Admission returns one of:

- `Accepted`, with the operation held until the simulation acknowledges its terminal completion
- `StreamBusy`, when any touched stream already has an admitted mutation
- `Backpressure`, when the configured operation or byte capacity is full
- `Stopping`, after shutdown drain starts

An accepted operation is never evicted. Transient or unknown store failures retry the same operation with
bounded exponential backoff and jitter. While it retries, all touched streams remain reserved. This is the
fail-closed choice. New work eventually receives `Backpressure`, while already accepted ownership changes are
not converted into memory-only success or silently lost.

A non-retryable storage fault such as corrupt committed bytes or an unsupported store schema quarantines the
touched streams and emits a fatal completion. The game disconnects or blocks those players without applying
the mutation. Unknown outcomes are always retryable. They never take the fatal path merely because a retry
budget elapsed.

The executor has bounded worker concurrency and a thread-safe completion queue. Pending work and completed but
unacknowledged work share the same configured operation and byte capacity. A terminal database result does not
release stream reservations or capacity. The host drains at most its configured completion budget at the start
of each tick, then calls `AcknowledgeCompletion` with `Handled` or `Quarantined` for each operation. `Handled`
means the terminal result was safely processed, whether it was applied, replayed, or a no-write conflict.
`Quarantined` means live application or stored-data verification failed and the affected streams are blocked.
Only acknowledgement releases the operation's stream reservations and bytes.

The game applies a completion by reducing into isolated copies of every affected live state, checking receipt
version continuity, then swapping all copies into the live registry as one tick step. If reduction or swap
fails, it acknowledges `Quarantined`, blocks the streams, and reloads from snapshot plus tail before accepting
more work. It never continues from a partly mutated live object. While the operation receipt is retained, a replay
already represented by the live versions needs no reduction and can be acknowledged after returning its original
response.

Submission and completion work are O(mutations), not O(connected players). No code path serializes a full
player merely to discover whether it changed. Each host must configure both operation-count and owned-byte
capacity. Construction rejects zero or missing capacity rather than choosing an unsafe production default.

Shutdown stops admission, drains accepted operations for a configured grace period, and reports every
unresolved operation ID. A forced process loss is still safe because the client action or durable server cause
can submit the same ID after restart. The server must not acknowledge unresolved operations during shutdown.

Required metrics:

- queue operations, bytes, and oldest age
- accepted, stream-busy, backpressure, and stopping counts
- applied, replayed, version-conflict, and operation-conflict counts
- commit latency histogram and retry count by failure category
- in-flight stream count and completion backlog
- replayed events per load, tail bytes, and compaction lag
- projection read latency and returned-section count

Payloads, result bytes, and raw account IDs are not logged. Operators get operation IDs, provider error codes,
hashed stream identifiers, and metric dimensions with bounded cardinality.

## 10. Admin projections

Projection bytes are game-owned replacement documents. They are not events and are never patched in place.
One mutation writes only the sections it changed. A Grimhollow player uses these initial sections:

- `profile`: display name, last durable health checkpoint, checkpoint time, and durable account-facing metadata
- `skills`: skill IDs, XP, derived levels, and any durable skill points
- `bag`: slot, item ID, and quantity
- `worn`: equipment slot, item ID, and quantity
- `bank`: bank slot, item ID, and quantity
- `quests`: quest ID and durable state

Item names, unlock labels, and tuning values remain config-backed. The admin app can join or resolve item IDs
against its current catalog without copying names into every player mutation.

`ReadProjectionsAsync` targets one stream. Its cursor is opaque to the UI and binds the store epoch, stream key,
and last captured head. The first request has no cursor and returns every section at one captured head. Later
requests return only sections whose `source_version` is greater than the cursor head, plus a new cursor even
when no sections changed.

Head capture and projection selection happen in one consistent read transaction. SQL Server takes a shared
serializable lock on the stream head before selecting its sections, SQLite uses its connection lease, and the
in-memory backend uses its model lock. A writer therefore cannot replace a projection row between those two
reads. The per-stream 64-section and 8 MiB aggregate limits keep the single response bounded without a paging
snapshot protocol.

If the supplied cursor belongs to another epoch or stream, or its version is ahead of the current head, the
read returns `ResetRequired` with every current section and a new cursor. An absent stream returns `NotFound`.
The UI replaces its cached model on reset and clears it on not found. A point-in-time database restore must
rotate `store_epoch` through the maintenance API before writers reopen. Restore runbooks quiesce journal hosts,
rotate the epoch, verify stream continuity, reconcile any external systems, then resume traffic.

The Grimhollow admin UI polls a selected account every three seconds while its detail panel is visible. It
stops when the panel is hidden, the browser tab is inactive, or the operator selects another account. Manual
refresh remains available. No endpoint returns every player's inventory or bank.

Online presence is separate transient state. The current player list moves to join, leave, and host-lifecycle
deltas rather than per-tick full detail publication. A multi-host deployment may use a per-host lease plus
session rows, so crash expiry costs one heartbeat per host rather than one heartbeat per player. Presence is
never allowed to overwrite durable projections.

## 11. Rare drops and multi-player mutations

A rare drop is not first created as a claimable in-memory object. The kill operation writes the death outcome,
reward result, and a new durable loot-source ID before the drop is shown. If the commit fails, the death and
drop have not happened from the durable world's perspective.

Claiming the drop is one operation across the loot-source stream and player stream:

- the claimant supplies or receives a stable claim-intent operation ID distinct from the loot-source ID
- the loot source expects its unclaimed version and records claimant plus consumed state
- the player expects its current version and records the bag or bank grant
- the changed `bag` or `bank` projection is replaced
- the result says exactly what was granted

One transaction commits all of it. Concurrent claims produce one applied operation and conflicts for the
losers. A lost reply replays the winner while its operation receipt is retained. The source remains consumed after
operation-row retention expires.

A player trade follows the same shape across both player streams and any escrow stream. The engine locks the
ordinal stream keys, so two opposite-direction trades cannot acquire locks in opposite order. Conservation is
a game invariant pinned by tests over the before state, events, and reduced after state.

## 12. Grimhollow adoption

Grimhollow is the first code consumer after the engine release.

### 12.1 Migration

On login, the server first looks for `grimhollow/player/{accountId}` in the journal. If absent, it loads and
validates the existing `player:{accountId}` world-store blob, maps it to the current game state, and calls
`InitializeAsync` with a version-zero snapshot plus all admin projection sections. A race returns one baseline.
The loser loads that baseline.

R2 uses a fenced stop-the-world cutover. Deployment enters maintenance, drains every old host, and verifies no
old process holds a session. Coarse movement state moves to a new key before the database enables a fence which
rejects every later write to legacy `player:` keys. Only then may new hosts initialize journal streams. The
stable legacy read and initialization do not need a cross-store transaction because no legacy writer can pass
the fence. A stale old host test proves its final save is rejected after cutover.

Once initialized, the journal owns skills, bag, worn equipment, bank, quests, and any health boundary the game
classifies as durable. The old blob must not write those sections again. The existing world-store path may
continue to own movement and other coarse state, or carry a compatibility marker and journal version for
diagnostics.

There is no unsafe deployment rollback after the first migrated write. The database fence prevents an older
binary from resuming mutation authority over the legacy blob. Rollback uses maintenance mode or a
forward-compatible reader shipped with the deployment. The rollout records migrated-player count, fence
state, active binary versions, and rejected stale-writer count.

### 12.2 Mutation routes

The adoption covers every current durable mutation path:

- character initialization and starter items
- loot creation and pickup
- shop buy and sell
- bank deposit and withdrawal
- equip and remove
- item consumption or destruction
- XP awards, level changes, and tree outcomes
- damage, death, and respawn health if health remains durable
- quest state when quest content arrives
- admin corrections through an audited server command

Each route receives a stable operation ID and a deterministic reducer. The server sends updated state only from
an applied or replayed receipt. SQL outage leaves the action pending or produces a localized temporary failure
before admission. It never grants first and hopes a 30-second save succeeds.

### 12.3 Admin replacement

Delete `PlayerInspectionPublisher` and the `player-states` all-player detail action. Add one authenticated,
audited selected-account projection endpoint with a version cursor. Keep skill configuration inspection as a
separate config-backed endpoint because success percentages, tree lives, XP values, and unlock requirements
are tuning data rather than player state.

The admin detail view merges the returned changed sections into its local model. It clearly shows committed
version and last update time. Inventory, worn equipment, bank, skills, health, and quests therefore stay near
live without adding a simulation tick payload.

## 13. Ruinborne adoption scope

Ruinborne gets a committed game-side adoption design in this program, but its gameplay migration follows after
Grimhollow proves the released engine contract.

Ruinborne initializes a version-zero journal snapshot from `player_character` and `character_inventory`. It
does not manufacture historical events. Existing relational rows remain game-owned materialized read models
during adoption, each guarded by a source journal version so an asynchronous projector can apply replacement
state idempotently. Gameplay recovery reads the journal snapshot and tail, not a potentially lagging projection.
Commit completion updates the compatibility rows in normal operation. Login recovery and a partitioned audit
sweep compare their source versions with journal heads and repair any crash gap. They are transition aids, not
the exact near-live admin source. Ruinborne's adoption design decides whether to retire them or add a dedicated
game-owned change feed before calling the migration complete.

The later gameplay implementation routes these boundaries through the journal:

- starter character and item initialization
- kill reward as one XP, gold, level, and point operation
- loot creation and claim with a durable source ID
- consume, destroy, bag move, equip, unequip, and attribute consequences
- two-player transfer with canonical stream order
- later shop purchase and player trade
- admin edits through an audited authoritative command

The existing `world_store` transform and ability-cooldown checkpoint remains separate. Memory-only combat,
buff, and selected-weapon caches are reviewed individually and move only if the game decides they must survive
a crash. The current batched economy ledger stays a best-effort forensic sink and cannot decide ownership.

Ruinborne's admin app reads exact current ownership from the journal's synchronous section projections during
adoption. Existing relational screens may remain where their materializer is current, but they show source
journal version and projection freshness. Any lag or materializer failure is visible rather than silently
presenting an old bank as current.

## 14. Failure invariants

The implementation is not ready to ship until the following are executable tests or production probes:

| Fault or race | Required outcome |
|---|---|
| Submit one operation 100 times within the retention horizon | One event set, one version advance, one original result |
| Reuse an operation ID with changed intent | `OperationConflict`, no writes |
| Restart retries committed intent without old execution bytes while its receipt is retained | Resolution returns the original receipt and result |
| Matching intent is rebuilt with different execution while its receipt is retained | Original execution returns as `Replayed` |
| Eventless rejection commits | Stream before and after versions stay equal, with no projection write |
| Kill before transaction starts | Source remains claimable or action remains uncommitted |
| Kill after event insert but before commit | No event, projection, or operation row survives |
| Kill after projection write but before commit | No partial projection survives |
| Commit succeeds and reply is lost while its receipt is retained | Same-ID retry returns `Replayed` with no duplicate |
| Server dies after commit but before live apply | Restart replay reconstructs the committed state |
| Late replay arrives after newer live state while its receipt is retained | Original response returns without reducing the old events again |
| Completion queue fills before tick drain | Same admission byte bound applies, with no released stream reservation |
| Live reducer throws part way | Isolated copies are discarded and all touched streams quarantine |
| SQL is unavailable | Ownership action is not acknowledged or applied only in RAM |
| Provider reports failure at each transaction boundary | Kind and certainty match the shared mapping |
| Two players claim one source | Exactly one claimant, item count conserved |
| Two opposite transfers race | No deadlock leak, one ordered outcome per committed operation |
| Snapshot process dies before commit | Old snapshot and complete old tail remain readable |
| Snapshot and prune commit | New snapshot plus remaining tail reconstruct exactly once |
| New events arrive during recovery paging | Every page stops at the first captured `throughVersion` |
| Projection read races a commit | Current or next poll observes it without skipping a section |
| Database restore crosses an admin cursor | Rotated store epoch forces a full projection reset |
| Admin consumer is down | Journal commits continue and the next selected-player read returns current sections |
| Caller mutates source arrays after admission | Stored fingerprints, bytes, and capacity remain unchanged |
| Maximum Unicode key shapes hit SQL Server | Core rejects non-ASCII or over-limit keys before I/O |
| Rolling binary retries an old fingerprint format | Golden old-version resolver returns the original operation |
| Operation purge cutoff is too young | Purge refuses it and deletes no replay rows |
| Stored operation result is corrupted | Replay quarantines receipt streams and returns no success |
| Stale Grimhollow host saves after migration fence | Database rejects the legacy player write |
| 10,000 players are idle | No player serialization or inventory reads occur on ticks |
| One percent of players mutate | Tick work follows submitted and completed mutation count |

Backend conformance runs against in-memory, temporary SQLite, and a real SQL Server instance. SQL Server tests
cover duplicate-key races, lock ordering, deadlock retry, transaction rollback, binary collation, and unknown
outcomes. A deterministic faulting wrapper injects every listed boundary. A process-level harness kills a child
server at the commit checkpoints and verifies recovery from a fresh process.

Reducer tests stay in each game. They prove event determinism, schema upgrades, ownership conservation, and
that a projection section exactly represents the reduced committed state.

## 15. Performance model

Idle cost is zero journal writes and zero serialization. A mutation writes:

- one small operation row
- one operation-stream row per touched stream
- one or a few event rows
- one stream-head update per touched stream
- only the projection sections changed

The normal Grimhollow item pickup updates a loot source, one player stream, and one `bag` section. It does not
rewrite `bank`, `skills`, `quests`, or every other player.

The first release must include a benchmark and soak harness reporting commits per second, p50, p95, and p99
latency, allocation per operation, queue saturation, and recovery throughput. Required scenarios are one-stream
grants, two-stream claims, two-player transfers, duplicate replays, and selected-player projection polling.
Release notes report the measured hardware and database rather than promising a universal player count.

Scale-out relies on normal account-to-host session routing so one player's live state has one primary host.
The stream version check remains the database-level defense against a split session or stale host. A region may
write only to a database primary which provides the transaction and lock semantics above.

An operation which touches sessions owned by different hosts needs game-level committed-event delivery before
both live copies can advance. R1 permits multi-stream operations only when one orchestration host can update or
invalidate every affected live session. A later cross-host trade must add durable delivery or force the remote
owner to reload before either client receives success. Database atomicity alone is not claimed to update RAM in
another process.

## 16. Security and operations

- Admin projection endpoints keep the existing authenticated admin boundary and audit every lookup.
- Player commands never accept an arbitrary stream key from the client.
- Payload and result codecs validate schema, lengths, counts, enum domains, and item IDs before reduction.
- Stored bytes use database encryption at rest and transport encryption supplied by the deployment.
- Logs redact payloads, result bytes, display names, and raw account IDs.
- Database backup and point-in-time restore include all journal tables as one recovery unit.
- Restore drills verify snapshot checksums, stream continuity, and projection rebuildability.
- Schema changes are additive first. Destructive cleanup waits until every deployed game reader supports it.
- Operation retention is explicit per game and never shorter than the client retry horizon.

Alerts cover sustained backpressure, oldest pending age, retry storms, operation conflicts, version conflicts,
projection lag, replay failures, checksum failures, and compaction lag. A checksum or sequence gap quarantines
the affected stream and refuses login. It is not skipped to keep the player moving.

## 17. Rollout

### R1. Engine contract and backends (targets 18.31.0)

- core records, canonical fingerprinting, validation, and in-memory model
- shared conformance and fault-injection suite
- SQLite backend and schema
- SQL Server backend, schema script, and live integration tests
- bounded executor and metrics
- snapshot, replay, projection reads, and safe compaction
- package READMEs and consumer documentation
- version and changelog target 18.31.0
- local-feed pack validation
- remote `v18.31.0` tag and package existence establish publication state

### R2. Grimhollow migration and proof

- pin the released engine
- migrate legacy blobs to version-zero journal snapshots
- route all current durable gameplay mutations through the executor
- create durable loot sources
- replace tick inspection with selected-player database projection polling
- add process-kill, unknown-outcome, SQL-outage, and load tests
- deploy behind an explicit migration gate, observe, then remove the legacy state writer

### R3. Ruinborne design handoff

- commit the game-side adoption design against Ruinborne's exact tables and mutation services
- map every route to an operation identity and stream set
- specify versioned materializer changes and rollout gates
- leave gameplay changes tracked in
  [Ruinborne #467](https://github.com/APKiwiOrg/Ruinborne/issues/467)

R3 documentation may proceed beside R2. Ruinborne gameplay code waits until the Grimhollow proof and the engine
release are available.

## 18. Alternatives rejected

### Keep the 30-second whole-player checkpoint

Rejected because it admits acknowledged progress loss on process failure and performs work based on connected
player count rather than mutation count.

### Save the whole blob after every mutation

Rejected because it removes most of the crash window but keeps large write amplification, weak concurrent
composition, no operation replay, and no atomic multi-player transaction.

### Await SQL directly inside the fixed tick

Rejected because database latency and outages would become simulation stalls. Commit completion crosses a
queue back to a later frame instead.

### Use `BatchedWriter<T>` as an event journal

Rejected because its drop-oldest overflow and poison-row salvage are correct for telemetry and explicitly
wrong for authoritative ownership.

### Let every game invent relational write-through services

Rejected as the only answer because ordering, fingerprints, idempotent retry, failure semantics, compaction,
and backend parity would drift across sibling MMOs. Games can keep relational read models downstream.

### Put arbitrary game SQL callbacks inside the engine transaction

Rejected because it exposes provider-specific connections through the dependency-free seam, prevents backend
parity, and lets game code violate transaction ordering. Opaque replacement projections cover the synchronous
admin read need. Rich relational models remain idempotent downstream projections.

### Add Kafka or another broker first

Rejected for R1 because it adds an operational system without removing the need for a transactional database
authority. A future outbox consumer can publish committed journal events without changing the authority model.

## 19. Proof of completion

The program is complete when:

- all three backends pass the same journal contract
- every failure invariant in section 14 has automated evidence
- no accepted operation can be dropped by executor pressure or shutdown
- Grimhollow grants no durable player state before commit
- a killed Grimhollow process cannot duplicate or erase a proved rare-item claim
- Grimhollow's tick has no full-player inspection or dirty-discovery scan for journal-owned state
- the admin app reads one selected player's changed projection sections from the database
- the engine package is released and pinned by Grimhollow
- Ruinborne has a committed, issue-linked adoption design against its current schema
