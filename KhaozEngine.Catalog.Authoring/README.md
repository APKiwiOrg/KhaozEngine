# KhaozEngine.Catalog.Authoring

The authoring half of the content catalog. `KhaozEngine.Catalog` is the READ side every consumer needs,
including a game client. This package is the WRITE side: the provider seam a database backend implements,
the one open draft, the edit vocabulary, the field-level audit, the published version record, families and
their id blocks, and the bundle a catalog is seeded from and exported to.

It is pure .NET. There is no SQL here and no third-party dependency, and its only project reference is
`KhaozEngine.Catalog`. The SQLite and SQL Server backends are opt-in sibling packages that reference this
one, so a client never pulls a database dependency to decode a pack.

## The seam

`IContentAuthoringStore` is the one shape every backend implements. Its members cover schema initialization,
the version list and the operator pin, the open draft, publish and rollback, row and audit reads, id
allocation, families, and bulk import and export.

It also INHERITS `KhaozEngine.Catalog`'s `IContentVersionDirectory`, which declares its two version reads, so
a host that boots off its authoring database assigns the store itself to `ContentBootOptions.Directory` and
writes no adapter.

```csharp
await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

ContentDraft draft = await store.ApplyEditsAsync(
    [
        ContentEdit.Update(itemType, 13, new ContentKey("stone_sword"), [new ContentFieldEdit("value", ContentFieldValue.OfNumber(ContentFieldKind.Int, 45))]),
        ContentEdit.Add(itemType, new ContentKey("iron_sword"), fields),
    ],
    actor: "admin-endpoint",
    operatorId: "oid:8f2c",
    note: "autumn price pass");

ContentPublishResult published = await store.PublishAsync(
    new ContentPublishRequest("admin-endpoint", "oid:8f2c", "autumn price pass", draft.BaseVersion));
```

`ContentAuthoringSchemaMode` is `AutoCreate` or `ValidateOnly`. `ValidateOnly` refuses an empty or
mismatched database rather than creating anything, which is what a production host sets so a typo in a
connection string cannot silently create a second empty catalog.

`ContentAuthoringException` is the one exception this package throws. It carries the offending content type,
the definition id and a stable `Reason` token beside the message, and an operator's log line keys on the
reason rather than on the message.

## The four edit operations

An edit names a TARGET, which is the content type, the definition id and the content key together, and it
stores the CHANGED FIELDS ONLY rather than the whole row. That is what lets the audit record a field-level
before and after with no extra table, and what makes two operators editing different fields of one row a
merge rather than a last-write-wins clobber.

| Operation | Built with | Effect at publish |
|---|---|---|
| `Add` | `ContentEdit.Add` | Allocates an id and writes a row valid from the new version. |
| `Update` | `ContentEdit.Update` | Closes the current row and writes a successor with the merged field set. |
| `Retire` | `ContentEdit.Retire` | Closes the current row, writes a retired successor, and appends a `Retired` remap rule. |
| `Fork` | `ContentEdit.Fork` | Allocates a NEW id, copies the source row onto it under the new key, sets the named flag field on the copy, applies the changed fields to the ORIGINAL, and appends a `MovedToLegacy` rule. |

The numbers behind the operations are durable: `Add` is 1, `Update` is 2, `Retire` is 3 and `Fork` is 4,
because a provider stores the number.

`ContentEdit.Import` is the one other factory, and it is the only path that may name its own definition id.
That is licensed into an EMPTY database only, which is what makes an adoption a no-op for stored player
data.

### Why `Fork` is one operation

`Fork` carries five things: the source id, the copy's new key, the `Bool` flag field to set on the copy, the
changed fields for the ORIGINAL, and no id for the copy, because ids are allocated at publish. It is applied
whole or refused whole.

It exists because the keep-legacy flow cannot be expressed without it. An author who changes a definition's
numbers chooses between rescaling everything players already own and preserving it. Preserving needs the OLD
definition to survive at a new id with a legacy flag set while the ORIGINAL id carries the new numbers.
Doing that as an add plus two updates gets the rows into the right shape but emits no remap rule, so nothing
tells a loaded page to move, and a publish that succeeded with the rule missing is the state nothing can
detect afterwards: the pages are already migrated and the original values are gone.

The flag field is the CALLER's and the engine does not name it. The engine checks only that the field exists
on the type's schema and is `Bool`, which keeps `Fork` a generic operation over the row model rather than a
feature of whichever package motivated it.

### The key shape rule

`ContentKeyShape` is the key rule of contracts 5.3 as one public predicate: `Defect(key)` answers the first
defect as a phrase or null, `Rule` is the whole rule as one sentence for a message to append, and
`MaxKeyLength` is the cap. It is public because the rule has callers at three layers and they are not one
call site. The validator's `KEC0001` sweep walks the rows a candidate already holds. A fork's copy key never
reaches that sweep, because the row it would go on does not exist until publish. And an admin surface has to
refuse an add's key BEFORE the edit enters the draft, since a draft carrying a malformed key is wedged: every
later validate reports it, every later publish refuses, and the only removal on this seam is a discard, which
takes every other pending edit with it.

The rule is deliberately not `ContentKey`'s own. A bad key has to reach the sweep intact, so that a bulk
import reports every one of them in a single pass rather than throwing on the first.

## The change set

`ContentChangeSet` is the ordered, deduplicated edit list that is the durable form of a draft. It holds ONE
pending intent per target, and it answers a second edit of an occupied target in two different ways:

- Same target, SAME operation: the newer edit replaces the older one in the slot it already holds, so a
  console that saves the same row twice updates the one edit rather than queueing two.
- Same target, DIFFERENT operation: refused. An update followed by a retire would otherwise silently flip
  the first edit's operation and drop its fields. An operator who wants both gets them in two publishes,
  which is also the order the audit will show.

`TryApply` reports the standing edit that refused the new one. `Apply` throws the same refusal as a
`ContentAuthoringException` carrying the `edit-target-collision` reason.

## Families and id blocks

`ContentFamily` is an author-declared grouping within one content type whose members are allocated ids from
contiguous ALIGNED blocks, so a membership test is two comparisons per block rather than a set lookup. The
block size is declared at creation, is a power of two between 16 and 65,536, and cannot be changed later,
because changing it would move every id in the family. When a block fills a second one is reserved and the
family carries an ordered `ContentFamilyBlock` list.

A family is never deleted. It is retired like a definition.

## The id allocator

`ContentIdAllocator` issues definition ids and it RESERVES BEFORE IT ISSUES. `ContentIdHighWater` is the
pair of durable numbers behind that: `ReservedThrough` is the highest id the store has promised not to hand
out twice, and `IssuedThrough` is the highest id actually stamped onto a row.

`AllocateAsync(type, count)` returns the FIRST id of a contiguous range, so the range is
`[first, first + count - 1]`. When the live reservation cannot cover the request, the allocator writes a new
reservation `ReserveBatch` ids ahead and COMMITS THAT WRITE ON ITS OWN before issuing anything from below it.
The order is the contract and the batch size is not: a crash between the two commits skips up to 1,024 ids
that were never issued, where the inverted order would hand the next boot an id already on a row.

`AllocateInFamilyAsync(familyId)` issues one id from the family's blocks in ordinal order and reserves a new
aligned block when every block is full. Reserving a block also ADVANCES the type's issued mark to the new
block's top, which is what keeps the plain counter from walking under the block and reissuing an id inside
it. The plain path therefore needs no knowledge of families at all.

A type may declare a `maxDefinitionId` at registration, and both paths refuse to cross it: a range that
would pass it is refused whole, a reservation stops at it, and a new block whose top would exceed it is
refused. The refusal is a `ContentAuthoringException` carrying the `id-ceiling-exceeded` reason and naming
the type, the ceiling and the high-water mark. Retired rows count toward a ceiling, because ids are never
reused and a retired row keeps the number it occupies.

`IContentIdPersistence` is the durable half the allocator sits on, and every `Commit` member on it commits
on its own. A backend implements it with its own transactions.

## The publish pipeline

`ContentPublisher` is publish, in order, with each step delegating to its named type. **Steps 1 to 8 write
nothing durable.** They freeze the draft, build the candidate, allocate ids, validate, compute the temporal
rows, select the affected chunks, encode and hash them, and build both manifests. `ContentPublishCommit` is
steps 9 to 11, the half that writes.

```csharp
var publisher = new ContentPublisher(store, idPersistence, registry);
ContentPublishPlan plan = await publisher.PrepareAsync(
    new ContentPublishRequest("admin-endpoint", "oid:8f2c", "autumn price pass", expectedBaseVersion: 12),
    baseline);

if (!plan.IsValid)
{
    // Every finding at once, so an operator fixes three problems in one round trip rather than three.
    return plan.Validation.Findings;
}
```

`ContentPublishPlan` carries everything the commit needs: the candidate snapshot, the id allocation record,
the row closes and inserts, the live row set, the appended remap rules, every chunk row with its hash, both
manifests with their hashes, and the two counts an operator reads the one-item-edit budget off. A plan is not
a publish. Nothing in it has been written, so a caller that drops it leaves the store exactly as it found it.

An INVALID plan stops where it failed and both its manifests are null, because encoding bytes for a version
nobody will publish is work for nothing.

### The baseline is handed in

`ContentPublishBaseline` is the base version as steps 2 to 8 need it: its number, its live rows, its rules,
its chunk rows, its languages and its two minimum builds. It is an argument rather than something the
publisher reads, and that is the crash-safety shape: the commit reads the base under the row lock it took at
step 1 and hands it down, so the version the candidate was built against and the version the transaction
commits against cannot differ.

`ContentPublishBaseline.Empty` is the empty database, which is the one case the validator is passed a null
previous snapshot. `ContentPublishBaseline.After(plan)` is the baseline the next publish sees.

`ContentPublishRequest.ExpectedBaseVersion` is optimistic concurrency and it is required. Two consoles cannot
both publish the same draft: the second one's expectation is stale and it is refused with both numbers named,
carrying the `base-version-moved` reason.

### The draft is frozen for the whole publish

Step 1 marks the open draft FROZEN for the base version it is publishing, through
`IContentAuthoringStore.FreezeDraftAsync`, and every write to the draft is refused while the marker stands:
`ApplyEditsAsync` and `DiscardDraftAsync` both throw with the `publish-in-progress` reason.
`ContentDraft.FrozenForBaseVersion` and `ContentDraft.IsFrozen` are how a console reads it. Without it a
second actor's edit lands in a change set the pipeline has already read, version N publishes without it, and
step 10 deletes the draft it was sitting in: an edit an operator saved that no version carries and no draft
still holds.

**The marker is durable rather than a held lock, and that is forced rather than chosen.** A publish spans
steps 1 to 10, step 9 writes the whole pack, and no provider holds a row lock across that: SQLite leases its
one connection per call, and SQL Server's Serializable transaction covers step 10 alone.

`ContentPublishCommit.PublishAsync` clears the marker in a `finally`, so a success, a refusal, a throw and a
cancellation all release the draft. Two things recover a marker nothing cleared, which is what a killed
process leaves. A marker naming a version the store has moved past is STALE, and
`ReadPublishBaselineAsync` clears it, which is the read every publish starts with. A marker naming the
version the store still stands at belongs to a publish that died before its commit, and the next publish's
step 1 overwrites it, because a publish is exactly what an operator does to recover.

Driving `ContentPublisher.PrepareAsync` on its own therefore leaves a frozen draft behind, deliberately: half
a publish is the state the marker describes. Call `ClearDraftFreezeAsync` when standing in for the commit.

### Ids come from the edit, not from the caller

There is ONE allocation path with two sources, and which one runs is a property of the edit. An `Add` with
`definition_id` 0 is allocated one, an `Add` carrying a non-zero id keeps it, and two paths write the second
kind and no others: a bulk import into an empty database, and a content upgrade adding a row the committed
bundle already names by id. Both carry the number because a game states a definition id as a code constant,
and the allocator's durable mark sits above the highest row id whenever an earlier publish was refused after
it reserved. After every add has an id, the high-water marks are SEEDED from the largest carried id per type,
so the first ordinary add after one of those does not allocate id 1 straight onto a row that already holds
it. `ContentIdAllocationRecord.Seeds` is empty for an ordinary publish.

A carried id is checked before the candidate is built, because step 3's seeding commits on its own and a
refusal after it would leave the marks raised for a version nobody published. The publish refuses a carried
id that a live or retired row already holds, one a second add in the same draft names, one over the type's
declared ceiling, and one that disagrees with the type's family blocks, under `KEC0036`, `KEC0042` and
`KEC0037`.

Allocation runs BEFORE validation, because `KEC0006` resolves references and `KEC0010` asks about family
membership, and neither can be asked of a row whose id does not exist yet.

A `Fork` allocates through the plain counter and its copy inherits the SOURCE row's family. The copy is
written first, so the `MovedToLegacy` rule appended last names a destination that is already live.

### Chunk selection and reuse

A chunk is `(typeId, chunkIndex)` where `chunkIndex = definitionId / chunkSlots`, and the affected set is
every chunk holding a row that entered at this version or was closed in it. Every chunk NOT in it keeps its
previous version's hash and is not encoded, compressed, hashed or written. That is what makes the download
after a one-item edit a small number rather than the whole pack, and it is the reason chunk identity is an id
RANGE rather than a row range.

The carry forward is PER SIDE. For an unaffected chunk every chunk row the previous version holds is copied
forward, one for a single-sided chunk and two when the type is `Client` with a per-field `ServerOnly`
override. Nothing recomputes a side from the type's default visibility, because the schema may have gained a
`ServerOnly` field at THIS version.

A chunk's rows are sorted ASCENDING BY ID before encoding and the hash is taken over the UNCOMPRESSED
canonical bytes, so a later engine build that compresses better produces the same chunk hash and a client
already holding the chunk fetches nothing.

### Two sides, two chunks, two manifests

`IContentRowSideEncoder` is how one row's body is written for one side, and `ContentSideRowEncoder` is the
engine's own: the server side is the row as authored, and the client side is the same row with every
`ServerOnly` field omitted. Omitted means written as ABSENT rather than skipped, because a row body is a
positional walk and a field that took no width would shift every field after it.

A `ServerOnly` TYPE produces one server chunk and no client chunk at all. A `Client` type produces one client
chunk, plus a server chunk whenever its schema marks a field `ServerOnly`, because then the two sides are
different bytes with different hashes.

`KEC0014` fires on exactly one thing: the client-side encoded bytes of a chunk still carrying a field the
schema marks `ServerOnly`. That is an ENCODER defect and never an authoring one, so nothing refuses a
`Client` type with a `ServerOnly` field, and the check reads the bytes back rather than trusting the encoder
that wrote them. A silent strip would be worse than the refusal, because then a field's absence on the client
would be indistinguishable from an authoring mistake.

Both manifests name every REGISTERED type, carrying no chunks for a type the version authored no rows for,
because boot refuses a version whose manifest does not name a type the build registers. The chunks under each
type are the version's chunk rows, the server manifest taking the server side where it exists and the client
one naming no chunk for a type that has no client row. The client manifest still omits a `ServerOnly` TYPE
outright, which is the separate rule of contracts 11.3 and is why it names fewer types rather than the same
list with empty entries. Each gets its own hash sub-domain, so a head gating on one can never accidentally
agree with a head gating on the other. The two minimum builds and the format generation are INPUTS to the
manifest hash rather than stamps beside it: raising a minimum build without touching a row publishes a
version with a different manifest hash and identical chunk hashes, so a client re-reads one small manifest
and downloads nothing.

`ContentPublishStep` is the point a publish can be interrupted at, and `ContentPublisher.OnStep` is the hook
a crash test throws from. Every value is declared and the steps after the manifest belong to the commit.

### Steps nine to eleven, the files first and then one transaction

`ContentPublishCommit` runs a whole publish: the pipeline for steps 1 to 8, then the three that write.

```csharp
var commit = new ContentPublishCommit(store, packStore, publisher);
ContentPublishResult published = await commit.PublishAsync(
    new ContentPublishRequest("admin-endpoint", "oid:8f2c", "autumn price pass", expectedBaseVersion: 12));
```

**The ordering is the whole crash-safety property.** Step 9 writes every chunk file, the remap rule chunk,
both manifest files and the version pointer to the pack store BEFORE the database transaction, at
content-addressed names nothing references yet, so a crash there leaves inert bytes and the old version. A
hash the store already holds is checked for with `ExistsAsync` and not rewritten, which is what makes a
republish of an unchanged chunk free. The pointer goes last of the four, so a crash part way through leaves a
version whose pointer is absent, which reads as a listing failure and skips the next sweep rather than
authorising it to delete on a partial view.

The version POINTER at `versions/<n>` is the one object in a store not named by its own hash, and it is how a
content-addressed store answers what a version contains once the authoring database is out of reach.
`IPackVersionPointerStore` is its seam, separate from the read side's `IPackStore` for the same reason pruning
is: a read-only provider cannot be a half-working publish target, and that is a compile-time fact rather than
a runtime throw. `PackVersionPointers.Resolve` finds the half, and a store with none is refused at the top of
the publish rather than after the chunk files are already written.

Step 10 is `IContentAuthoringStore.CommitPublishAsync`, ONE transaction and the only member that moves the
active pointer. In order inside it: confirm the version number, insert the version row, apply every temporal
row change, append every remap rule at the sequence above the highest, insert every chunk row one per side
including the carried-forward ones, insert every audit row, delete the draft, then move the active pointer
LAST. A reader that sees the new active version sees every row, rule, chunk and audit entry of it, because
they committed together.

The draft delete is scoped to `ContentPublishPlan.FrozenEdits`, the change set step 1 read. The freeze is
what makes that the whole draft, so scoping it can only matter when the marker failed to hold, and that is
the point: an edit the publish never carried survives into the next draft rather than being deleted
unpublished.

**It CONFIRMS the version number rather than trusting it.** The plan digested its number into both manifest
hashes at step 8, so the transaction re-reads the highest published number and refuses with the
`base-version-moved` reason when the plan's is not the next one. A provider that leases a connection per call
holds no lock across steps 1 to 10, and this is the check that catches a base that moved underneath such a
plan. The same statement is made about the rule list, which has to be the plan's own prefix.

Step 11 is `ContentPackSweep`, the orphan sweep, which runs only after a SUCCESSFUL commit. **The keep set is
the union, over every version the store knows, of that version's pointer, the two manifest hashes it holds and
every hash named inside either manifest**, which is exactly `IPackStore.ListAsync(v)`. It is defined against
the MANIFESTS and not against the chunk table, because the rule chunk sits at a reserved address outside any
type's id space and the text chunks are per language, so neither has a chunk row to hang on while both are
named by both manifests. A keep set read from the chunk table would delete them at the first publish and every
later boot would fail closed on an absent chunk, for every version, forever.

The sweep is SKIPPED when the store listing fails for any reason, and a pointer that is absent or unreadable
for any version IS a listing failure. It is skipped again when the store implements no pruning half.
`ContentPackSweepResult` carries the reason either way, because deleting nothing and deleting everything are
one keystroke apart and an operator reading a publish response deserves to know which happened.

`ContentPublishCommit.SweepAsync` is the step as the publish runs it, and it is public so a test can drive
step 11 on its own. An OPERATOR reaches `ContentPackSweep` through the admin surface instead, because a
recovery sweep has no prepared `ContentPublisher` to build a commit around, and because the operator's sweep
is no longer the same operation: it refuses while a publish holds the draft frozen, and it records an audit
row naming who ran it.

## Rebuilding a pack root

`ContentPackRebuild.RunAsync(store, registry, versionNumber, target, pointers)` writes one PUBLISHED version's
whole pack into a pack store again, out of the authoring store.

**It is for a server whose pack root does not outlive its process.** The authoring store keeps rows, rules and
hashes and never the BYTES, so a container that restarts onto an empty volume comes back to an empty
`IPackStore` while the store still names an active version, and `ContentBoot` refuses at step 3: the manifest
for that version is absent and there is nowhere to fetch it from. Everything needed to write those bytes again
is in the database, and this is the operation that does it. It reads rows through `ListRowsAsync` at the
version number, retired ones included, and the rule list through `ReadPublishBaselineAsync` filtered to
`IntroducedIn <= n`, so it needs no member the seam did not already have.

**The version to rebuild is the one the BOOT will load**, which is `ContentBoot.ResolveVersionAsync(options)`
on the same options the boot is handed, and NOT in general `GetActiveVersionAsync`. A version pinned in the
server's own config wins over everything, and an operator's pin in the database wins over the active version,
so a recovery that rebuilds the active version while either names another one fills the root with a pack the
boot never asks for, and the boot still refuses at step 3 with `manifest for version N absent`.

**It encodes rather than copies, through the SAME builders a publish uses.** The rows go through
`ContentChunkBuilder` against an empty baseline, so every chunk the version occupies is encoded again rather
than carried forward, and both manifests go through `ContentManifestBuilder` exactly as step 8 builds them.

**It verifies before it writes, and the verification is the two manifest digests the version row records.** The
canonical manifest text carries every chunk hash inline, so a match pins the whole closure and there is nothing
left for a per-chunk comparison to catch. Stored bytes are never compared, because a chunk's hash is over the
UNCOMPRESSED canonical bytes and the same rows compressed by a different build are a different FILE at the same
content address.

The write order is step 9's: every chunk, then the rule chunk, then the server manifest, then the client
manifest, then the version POINTER last. A crash part way through leaves inert content-addressed files and no
pointer, which reads as a listing failure and SKIPS the next sweep rather than authorising it to delete on a
partial view. A refusal writes nothing at all, not even a chunk, so the target is left as it was found.

**It is idempotent.** Every object goes in through the publish's own put-if-absent, so a second rebuild into
the same store reports `ObjectsWritten` 0 and `BytesWritten` 0 and rewrites only the pointer. `pointers` is
optional and defaults to `PackVersionPointers.Resolve(target)`, and a target with no pointer half is refused
before anything is read. `rowEncoder` is optional in the same way and defaults to
`ContentSideRowEncoder.Default`, which is `ContentPublisher`'s own parameter under its own name: a version
published through a custom side encoder is only reproducible through that same encoder, and a rebuild through
any other one refuses rather than filing bytes no version record describes.

It calls no publishing or editing member of the store, so it is safe to run against a live database with a
draft open. It is not strictly write free: the one side effect it can have is the baseline read clearing a
STALE freeze marker left by a publish that died, which is the recovery that read always performs.

**The known limit: rows are rehydrated against the CALLER's registry.** A field schema change, a `ChunkSlots`
change or a visibility change since the version was published moves the chunk bytes, the chunk set or the type
list, so both digests move with them and the rebuild refuses with `server-manifest-mismatch` or
`client-manifest-mismatch`, naming the digest it built and the one the version row holds. That is fail closed
by design: the alternative is filing a pack at addresses no version record describes, which every later boot
and every later sweep would then have to reason about. Recovering such a version means rebuilding with the
registry that version was published from.

**A version whose manifests would name TEXT is refused outright, with `text-chunks-unsupported`.** The write
puts chunks, the rule chunk, both manifests and the pointer, and never a text chunk, so a manifest naming a
language would name a KECT text chunk hash the rebuilt root does not hold. The digest comparison cannot catch
that on its own, because both rebuilt manifests are built from the same language list the recorded ones were
and name the same hashes either way, so the check is a separate one on the SNAPSHOT, ahead of the build and
ahead of any write. Rebuilding the text instead is not available: no store keeps a VERSION's text, so there is
nothing to encode a text chunk from, and that belongs with publishing text at all
(https://github.com/APKiwiOrg/KhaozEngine/issues/1000). Every provider publishes an empty language list today,
which the shared conformance suite pins, so no version any of them holds can reach this refusal.

The four refusal reasons, all reported as `RefusalReason` on the result rather than thrown, are therefore
`server-manifest-mismatch`, `client-manifest-mismatch`, `rebuild-candidate-invalid` (the builders refused a
row, which is a caller's own side encoder leaving a `ServerOnly` field in the client bytes) and
`text-chunks-unsupported`.

## Rollback

`RollbackToAsync(targetVersion)` BUILDS A DRAFT rather than publishing one, so an operator reviews the diff
and publishes it. For every row live at both versions whose field set differs it emits an `Update` restoring
the target's values. For every row introduced AFTER the target it does nothing at all: the row keeps its id
and its values, which is the difference between a rollback and a restore.

**The version number keeps climbing throughout.** A rollback is never a return to an old number, which is the
same property that lets a durable page's version stamp be an ordering comparison.

**A row live at the target and RETIRED since is a flat refusal**, `KEC0039`, naming the row and the rule that
retired it. There is no un-retire branch and there never was a reachable one: every retire appends exactly one
`Retired` rule, so a branch conditioned on no rule naming that id could not run. The way out is an ordinary
`Add` under a NEW key carrying the old values, because a key is immutable once published, and the retired row
keeps its id and its bytes forever so a stored stack still decodes.

`ContentRollback.Prepare` is the plan behind it and `ContentRollbackPlan` is what a console renders: the
edits, the blockers with the rule that produced each one, and one `KEC0039` finding per blocker.

## The diff

`ContentDiff.Between` is the field-level diff, computed over the per-field ROWS and never by comparing chunk
hashes. Two versions whose chunk hashes differ tell an operator that something changed somewhere in a slot
range of 256 ids, which is not an answer, and two versions whose hashes agree can still differ in the
server-only half of a field set.

Each `ContentDiffEntry` carries what happened to the definition and every field that differs, rendered as TEXT
through the field's kind. The rendering is the AUDIT's own, so an operator reading a diff and an operator
reading the audit row the publish wrote see the same string for the same value. `Removed` is the one operation
a publish can never produce, because a definition that leaves play is retired and its row stays in the pack
forever, and it appears only when the diff is asked the question backwards with an earlier version as the
destination.

`ContentDiff.ChunkSummary` is the download an edit would cost: per type, how many chunks the change touches
against how many the destination holds. That is the operator-facing half of the one-item-edit budget, read
before the publish rather than after it. A destination version number of null means the draft-applied
candidate, which is a row set with no number yet because it has not been published.

## The in-memory store

`InMemoryContentAuthoringStore` is a TEST AND TOOLING implementation of the whole seam, holding the catalog
in memory. A production host uses a provider, because nothing in it survives the process. It ships in this
package rather than in a test project because the draft, allocator, publish and admin-action suites all need
one and they sit in different assemblies.

It carries the constraints its provider siblings get from a `CHECK`, so a defect surfaces there rather than
at the first SQL run: a high-water mark never moves backwards, an issued mark never passes a reserved one,
and a family block is aligned to its own size.

**A gate is not a transaction, so every write that has to be atomic BUILDS and then APPLIES.** A provider
gets atomicity from one database transaction and this store has to construct it. `CommitPublishAsync` builds
the version row, the new row set, the chunk rows, the staged audit entries and the draft that survives into
locals, and then applies them in a tail of list writes and field assignments that cannot throw, so a failure
part way leaves the store exactly where it was rather than carrying a version row with the pointer unmoved.
The pin, the discard, the family create and the rollback render their audit entry before the change and
append it after, through `InMemoryContentAuditLog.Stage` and `Commit`, for the same reason: a change with no
audit row against it is indistinguishable from no change.

It PUBLISHES when it is handed an `IPackStore`, which is the second constructor argument and is optional: a
store built without one holds a draft and allocates ids and refuses to publish, because a publish writes files
before it writes rows. With one it answers the whole seam, `LoadSnapshotAsync` included, and that member reads
the version's pack back through `ContentPackReader` rather than rebuilding a snapshot from the row table,
deliberately, because what a server loads is the PACK and a store answering from its own rows could report a
version whose bytes are unreadable as healthy.

## Audit and versions

`ContentAuditEntry` is ONE audited field change. An update that changes three fields writes three entries
sharing an occurred-at, an actor, an operator and a note. The append is in the edit's own transaction and is
never best effort: a content edit with no audit row is indistinguishable from no edit.

`Actor` and `Operator` are two different facts and both columns stay. The actor is what the engine
AUTHENTICATED, which is a bearer token's holder rather than an identity. The operator is what the console
ASSERTED and the engine does not verify it. The operator field is documented as taking a STABLE identity: a
console passing a display name gets an audit trail that breaks when someone changes their name.

`AppendOperationalAuditAsync` is the one append on the seam that is NOT inside the transaction of the change
it describes, and it exists for a store-level operation: one that changes the store without changing any row.
A pack sweep is the first, because the deletions it makes live in the pack rather than in a table and no store
transaction spans them. A row written that way carries no type, no definition id and no key, and the number
the operation reports goes in its field name and value. Read it as "this happened", not as "this happened
atomically with its audit".

`ContentVersionRecord` is one published version's row, carrying both manifest hashes, the consumer-supplied
minimum builds, the format generation, the publisher and the note. A published version is immutable from the
moment its transaction commits, so there is no sealed flag. Holding a version back from a restart is the
operator's PIN instead.

## The content upgrade ledger

`IContentUpgradeLedger` is a SEPARATE seam from `IContentAuthoringStore`, whose member list is fixed. The
in-memory store and both providers implement it, and a provider holds it as the `catalog_content_upgrade`
table schema version 2 adds.

A `ContentUpgradeRecord` says which upgrade the catalog holds (`ContentUpgradeStamp`, a stable id of 1 to 128
ordinal characters and a positive order), how it came to hold it, and at which version:

- `Applied` means a runner published the upgrade's edits. The ledger row is written INSIDE the publish commit
  from `ContentPublishRequest.Upgrade`, so the version and its history entry land together or neither does,
  and a second publish of one upgrade id is refused whole with reason `upgrade-already-recorded`.
- `Adopted` means the content was already present, so nothing was published.
- `Baseline` means the catalog was seeded from a bundle that already carried the content.

`RecordUpgradeAsync` writes the other two, with the ledger row and one `content-upgrade` audit row in one
transaction. It refuses `Applied`, because only a publish commit can say a version was published, and
recording an id the ledger already holds is a no-op, so a crash between a seed and its baseline record is
resolved by running the record again. `ListUpgradesAsync` reads ascending by order and then by id.

Engine package versions and published version numbers are not migration history. A version number says how
many publishes happened, not which upgrades ran, and a convention over version notes cannot tell "applied,
then tuned back" from "never applied".

## Upgrade definitions and the runner

A game build registers a new content type or needs new rows. A fresh install seeds the current bundle and
works. An existing catalog still holds the older content, the strict runtime load refuses it correctly and
fail closed, and the host exits before it opens a socket. The lifecycle here is what turns that into an
ordinary step: versioned upgrade definitions shipped with the application, applied before the strict load,
with durable history in the ledger above.

**The engine owns the orchestration and a game owns only its definitions.** There is no game noun and no game
content anywhere in this package.

`ContentUpgradeDefinition` is a stable id, an `Order`, an operator-readable description, and a PLANNER. The
id is the ledger's primary key, so it is stable forever and never reused: renaming one reruns it against every
catalog in the field. `ContentUpgradeSet` is what a build ships, ordered ascending and validated whole at
construction, because two definitions under one id would have the ledger record one and skip the other
forever.

**The identity is the id and never the order.** An order decides which of two PENDING definitions runs first
and nothing else, so two shapes that look wrong are allowed and each one only adds an informational
diagnostic:

| What | Code | Why it is allowed |
|---|---|---|
| A shipped definition whose `Order` differs from the order the ledger recorded it under | `KECU0014` | The ledger holds the id, so the catalog already carries the upgrade and it never runs again. |
| A pending definition ordered below one the catalog already holds | `KECU0015` | Two feature branches merging produces it. The pending one has not run, so it runs now, in its own order, against the catalog as it stands. |

Two definitions sharing an id or an order in ONE set is still an `ArgumentException` at construction, because
that is a build shipping an ambiguity rather than a catalog carrying a history.

A planner is a pure function of `ContentUpgradeContext`, which carries the baseline version number, that
version exported as a whole `ContentBundle`, and the frozen registry. It performs no I/O and returns one of
three shapes:

| Shape | Built with | What the runner does |
|---|---|---|
| Changes | `ContentUpgradePlan.Changes(edits, changeLines)` | Publishes the edits as ONE version stamped with the definition's id. |
| Already satisfied | `ContentUpgradePlan.AlreadySatisfied(reason)` | Records the id as `Adopted`. No version is published. |
| Refused | `ContentUpgradePlan.Refused(reason)` | Stops the run. Nothing is changed by that definition. |

An empty edit list under `Changes` is an `ArgumentException`, because a planner with nothing to do is saying
one of the other two.

### The three rules a planner follows

1. **Detect by identity, never by value.** A row present under the committed id and the committed key is
   present whatever its fields hold, because those fields may be operator tuning an upgrade has no business
   reverting.
2. **A value patch names the old shipped default it replaces.** A field already holding the new value is
   satisfied, a field holding the named old default is patched, and a field holding anything else belongs to
   an operator and is left exactly as it is.
3. **A partial state is refused rather than completed.** Completing the remainder guesses which of the
   existing rows an operator owns, and that guess is unrecoverable once it publishes.

`ContentUpgradeChecks` and `ContentUpgradePlanBuilder` are those rules as code, generic over the types and
keys a caller passes. The checks are the ones the first hand-written catalog upgrade command proved: the
committed target bundle agrees with this build's registry, every baseline type is schema compatible with the
target, an identity is absent or present under BOTH the same id and the same key (anything else is a
conflict), and plain allocation will issue exactly the committed ids, contiguously from the type's current
highest row id. A type with id families is refused outright, because a family allocates from aligned blocks
and nothing here can prove which id a member would be issued.

Every check ANSWERS a refusal string rather than throwing, because the run reports catalog state to an
operator and a stack trace is not an instruction. A planner that throws `InvalidDataException`,
`ContentAuthoringException` or `ArgumentException` is still reported as a refusal, which is the shape a
hand-written check already has.

```csharp
var definition = new ContentUpgradeDefinition(
    id: "harvest-profiles",
    order: 4,
    description: "adds the harvest profile type's rows",
    plan: context => new ContentUpgradePlanBuilder(context, ShippedBundle)
        .AddRows([new ContentUpgradeIdentity(harvestProfile, new ContentKey("cow"))])
        .RetireRow(monsterDrop, new ContentKey("cow"), ContentRetirePolicy.Placeholder)
        .Build());

ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
    store, registry, new ContentUpgradeSet(definition),
    new ContentUpgradeOptions(ContentUpgradeMode.Apply, actor, operatorId, serverBuild, clientBuild));
```

### What the runner does, in order

1. A store implementing no `IContentUpgradeLedger` is `Unsupported`. No history means an upgrade reapplies
   its defaults over an operator's values on the next run.
2. No active version is `NoCatalog`. **The runner never seeds.** It is a SUCCESS, and a host that seeds its
   bundle calls `ContentUpgradeRunner.RecordBaselineAsync` straight after, which records every shipped
   definition as `Baseline`. A crash between the two is safe: the next run finds each definition satisfied and
   records it as `Adopted` instead.
3. A ledger id this build does not ship is `CatalogAheadOfBuild`, with nothing changed.
4. Pending means shipped and not in the ledger. None pending is `UpToDate` and writes **nothing**: no draft,
   no version, no audit row, no ledger row.
5. An open draft is the runner's OWN only when three things hold together: the actor matches, the note is
   `content upgrade <id>` naming a pending upgrade, and the draft's expanded edits are exactly what a fresh
   plan of that definition produces against the current active version. The actor and the note alone are not
   proof, because both stores keep the standing note when a writer passes none and neither rewrites the
   identity that opened the draft, so an operator's edit lands under both. Such a draft is PUBLISHED as it
   stands. Its freeze is never cleared and it is never discarded first: a marker naming the active version is
   a live publish or a dead one and nothing on the seam tells them apart, and a publish is the store's own
   recovery for a dead one. Any other open draft is operator work: `OperatorDraftOpen`, draft untouched. The
   publish pre-flight asks the same question, so a draft that appears after this step is answered the same
   way, and only another runner's is waited out.
6. A pin on another version is `PinnedElsewhere`. A pin on the active version does not block the publish, the
   pin is never moved, and the report carries a `PinHeld` diagnostic naming the version to repin to.
7. A supplied `ExpectedVersion` that is not the active version is `BaselineMoved`. It is checked again at
   EVERY later re-read of the active version, so a run that stood off and came back to a version a rival left
   stops rather than publishing onto a baseline nobody previewed. Only a version this run published itself is
   not a move.
8. Each pending definition runs in ascending order and publishes as its OWN version, so history shows each
   upgrade separately and an interruption between two of them resumes at the second. The planner is handed a
   bundle exported at the version active right then, so the second definition sees the first one's result.
   The minimum builds rise to at least the running build's ordinals and never fall below the baseline's.
9. After ANY publish failure the run READS the ledger and reports the disposition the row holds, which is
   `Adopted` when a rival found the catalog already satisfied and published no version at all. It discards
   its own draft only while that draft still passes step 5, and a `publish-in-progress` refusal means a rival
   is live, so the draft is left alone and the run stands off. An exception thrown after the commit point and
   a refusal before it look identical from outside, and only the ledger tells them apart.
10. `Preview` writes nothing. It plans the first pending definition exactly and lists the rest as pending,
    because a later plan depends on the published result of an earlier one.

Every refusal carries a stable `KECU` code beside a message naming the catalog, the upgrade id and the next
action, and `ContentUpgradeReport.WriteTo` renders every line through `ContentBoot.LinePrefix`.
`ContentUpgradeReport.ExitCode` is 0 on a success and `ContentBootResult.ContentFailureExitCode` otherwise.

Contention with a second runner is waited out rather than reported. Two replicas booting together is an
ordinary deployment and a boot that lost a race is a real outage, so a run stands off while another publish
holds the one draft and replans when it is free. The ledger's primary key is what makes standing off safe: an
upgrade that did land cannot be published a second time, and carried ids make two runners' plans for one
definition identical so the commit's version confirmation lets exactly one win.

The patience is spent on a catalog that is NOT MOVING rather than on a clock. Every wait reads the ledger,
the active version and the open draft, and a rival that moved any of them buys the attempt count back, so a
loaded machine cannot turn a correct run into a failure. A provider fault counts as contention only when the
provider calls it transient, so a permissions or connectivity failure costs one attempt and is reported as
`KECU0009` naming the upgrade, the operation and the next step.

### Host integration

- **Local arm.** Open under `AutoCreate`. Seed and call `RecordBaselineAsync` when the report is `NoCatalog`,
  otherwise run `Apply`, then perform the strict load. A failed report stops the host with the report text and
  its exit code.
- **Hosted arm.** A dependent server never upgrades implicitly. A deploy step runs the game's upgrade command
  in `Preview`, then in `Apply` with `ExpectedVersion` set to the version it previewed, before the server
  starts. A server booting against a catalog with pending upgrades refuses with the pending ids and the
  command to run.
- **Solo client.** A host that fails before listening hands its report to the client, which shows it on screen
  instead of retrying a join that cannot succeed. The launching mechanism is game owned.

## The bundle

`ContentBundle` is the whole catalog as one document: a format version, the type list with their schemas,
every live row with its id, key and fields, every family with its blocks, and the full remap rule list. It
is the seeding format and the lossless export format and there is only one of them. A bundle row's id is
OPTIONAL: named, it is imported with it, unnamed, it is allocated in row order.

Import works into an EMPTY database only. That single rule is the answer to a whole class of seeding defect,
where a seed that runs repeatedly against live data either reverts an operator's value on the next deploy or
is beaten forever by a stored row. A deployed database's values change through an edit and a publish and
through nothing else.

A lossless export is NOT a backup, and the difference is the version LINE. An import republishes at version
1, so the new database's history starts there. A durable page carries the version number it was stamped
with, and a rule applies to a stamp strictly older than its own version, so a page stamped 46 against a
database whose newest version is 1 is newer than every rule there is. When the version line must be
preserved, the path is an ordinary database restore of the authoring store, which is the provider's own
tooling and outside this engine.

`ContentBundleJson` is the document, written and read there and nowhere else. Every property is written
explicitly in a FIXED ORDER through a writer rather than a reflected serializer, because a bundle lands in a
repository beside the code it seeds and two exports of one version have to be the same bytes. Numbers are
written as numbers and bytes as lower hex, because a bundle is read and edited by hand as often as it is
generated. Reading is total for shape: a document this build cannot read is a refusal naming what was wrong,
never a half-built bundle. Shape includes MAGNITUDE, because JSON has one number type and a member the reader
wants as an integer can be handed `1.5`, an id past `int.MaxValue` or `1e308`. Type ids must also fit their
1 to 65,535 domain, and field kinds, visibility values and remap kinds must be known enum values. Each bad
value is refused naming the member, the same as a missing one.

An import runs through the ORDINARY publish and there is no second mechanism. It restores the families and
their blocks verbatim, restamps the bundle's rules as the new line's, turns every row into an `Add` edit and
publishes the draft as version 1. `ContentEdit.Import` is the only factory that may name a definition id, and
it is also the only one that may say a row is ALREADY retired, because a bundle carries its retired rows and
already carries the rule that retired them. A refusal at any point AFTER the staging began resets the store to
the empty state it was required to start from, so nothing is left half seeded, and that covers the staging
itself: a row naming a family the bundle does not declare is refused while the edits are being built, with the
families and the id marks already written. A refusal BEFORE the staging (a store that already published, a
store with no pack target) resets nothing, because it wrote nothing and the store it is protecting is live.

## Rules this package will not bend

- **Reserve before issue.** A range is reserved durably BEFORE any id in it is issued. The worst a crash can
  do is skip a block of ids that were never issued, and it can never reissue one.
- **A published version is immutable and remap rules are append only.** There is no update path and no
  delete path for a rule in this seam or in any provider. `ContentRulePrefix.Require` is that rule as a
  check every store runs inside its commit: the rules the store holds must be the plan's own prefix, WHOLE,
  the payload a retire writes its policy and destination in included.
- **Ids are never reused.** A definition that leaves play is retired, and the row stays in the pack forever
  so a stored stack still decodes.
- **Keys are ordinal and immutable once published.** Both SQL backends pin their key columns to a binary
  collation, because a case-insensitive database default silently merged two rows once.
