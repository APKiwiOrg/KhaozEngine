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

### Ids come from the edit, not from the caller

There is ONE allocation path with two sources, and which one runs is a property of the edit. An `Add` with
`definition_id` 0 is allocated one, an `Add` carrying a non-zero id keeps it, and only a bulk import into an
empty database writes the second kind. After every add has an id, the high-water marks are SEEDED from the
largest carried id per type, so the first ordinary add after an import does not allocate id 1 straight onto
an imported row. `ContentIdAllocationRecord.Seeds` is empty for an ordinary publish.

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

Both manifests are built from the version's chunk rows, the server one taking the server side where it exists
and the client one naming nothing for a type that has no client row. Each gets its own hash sub-domain, so a
head gating on one can never accidentally agree with a head gating on the other. The two minimum builds and
the format generation are INPUTS to the manifest hash rather than stamps beside it: raising a minimum build
without touching a row publishes a version with a different manifest hash and identical chunk hashes, so a
client re-reads one small manifest and downloads nothing.

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

`ContentVersionRecord` is one published version's row, carrying both manifest hashes, the consumer-supplied
minimum builds, the format generation, the publisher and the note. A published version is immutable from the
moment its transaction commits, so there is no sealed flag. Holding a version back from a restart is the
operator's PIN instead.

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
never a half-built bundle.

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
