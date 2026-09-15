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
rows, select the affected chunks, encode and hash them, and build both manifests. Writing the files, the one
commit transaction and the sweep after it are steps 9 to 11 and land separately.

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

## The in-memory store

`InMemoryContentAuthoringStore` is a TEST AND TOOLING implementation of the whole seam, holding the catalog
in memory. A production host uses a provider, because nothing in it survives the process. It ships in this
package rather than in a test project because the draft, allocator, publish and admin-action suites all need
one and they sit in different assemblies.

It carries the constraints its provider siblings get from a `CHECK`, so a defect surfaces there rather than
at the first SQL run: a high-water mark never moves backwards, an issued mark never passes a reserved one,
and a family block is aligned to its own size.

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

## Rules this package will not bend

- **Reserve before issue.** A range is reserved durably BEFORE any id in it is issued. The worst a crash can
  do is skip a block of ids that were never issued, and it can never reissue one.
- **A published version is immutable and remap rules are append only.** There is no update path and no
  delete path for a rule in this seam or in any provider.
- **Ids are never reused.** A definition that leaves play is retired, and the row stays in the pack forever
  so a stored stack still decodes.
- **Keys are ordinal and immutable once published.** Both SQL backends pin their key columns to a binary
  collation, because a case-insensitive database default silently merged two rows once.
