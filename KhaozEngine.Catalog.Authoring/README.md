# KhaozEngine.Catalog.Authoring

The authoring half of the content catalog. `KhaozEngine.Catalog` is the READ side every consumer needs,
including a game client. This package is the WRITE side: the provider seam a database backend implements,
the one open draft, the edit vocabulary, the field-level audit, the published version record, families and
their id blocks, and the bundle a catalog is seeded from and exported to. It is carried by the `Server`
umbrella, and a game client does not need it.

It is pure .NET. There is no SQL here and no third-party dependency, and its only project reference is
`KhaozEngine.Catalog`. The SQLite and SQL Server backends are opt-in sibling packages that reference this
one, so a client never pulls a database dependency to decode a pack.

## The seam

`IContentAuthoringStore` is the one shape every backend implements. Its members cover schema initialization,
the version list and the operator pin, the open draft, publish and rollback, row and audit reads, id
allocation, families, and bulk import and export.

`IContentVersionRowSource` is an optional whole-version read capability implemented by the in-memory,
SQLite and SQL Server stores. `ReadVersionRowsAsync` returns every live revision of the store's registered
types, including retired rows, ordered by type id then definition id, with historical revision metadata
intact. Stored types outside the provider registry are omitted before their fields and rows are decoded.
Version 0 reads the active published version and ignores the open draft and operator pin. Consumers of the existing
`IContentAuthoringStore` contract can still use `ListRowsAsync` pages without implementing this capability.

`IContentTextAuthoringStore` is the optional text authoring companion, implemented by the same three stores.
It adds per-language display text to drafts, publishes, bundles, rollback and recovery. [Catalog text](#catalog-text)
documents it.

`IContentAuthoringStore` also INHERITS `KhaozEngine.Catalog`'s `IContentVersionDirectory`, which declares its two version reads, so
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

`ContentAuthoringSchemaMode` governs initialization in the durable providers:

| Mode | Schema | Type registrations |
|---|---|---|
| `AutoCreate` | Creates or migrates, then validates | Inserts new types and refreshes changed settings |
| `ValidateOnly` | Refuses empty, legacy or mismatched schema | Inserts new types and refreshes changed settings |
| `ValidateOnlyWithoutTypeSync` | Refuses empty, legacy or mismatched schema | Reads id/key compatibility and changes nothing |

`ValidateOnly` still writes registration rows and their update times when settings change.
Use `ValidateOnlyWithoutTypeSync` before a hosted upgrade preview. It accepts added registrations and
changed chunk slots, visibility or id ceilings without storing them, and refuses a conflicting stored
type id/key pairing. `ExportBundleAsync` and the upgrade preview then describe stored type settings,
omit registry-only additions from the baseline, and use this build's registered field schemas to read
rows. The planner also receives the current registry so it can compare the two declarations.

The new mode governs the open only. It does not block later writes, and `ReadPublishBaselineAsync`
can clear a stale draft freeze. Reopen with `ValidateOnly` or `AutoCreate` when type synchronization
is required for an apply or an authoring host.

`ContentAuthoringException` is the one exception this package throws. It carries the offending content type,
the definition id and a stable `Reason` token beside the message, and an operator's log line keys on the
reason rather than on the message. Where a throw site names its exact rule, `Refusal` carries a
`ContentAuthoringRefusal` member and the identifiers that member documents, beside the unchanged `Reason`
token. `Refusal` is null for an instance built through a constructor that takes none, so a caller reads
`Reason` then. The [bundle document refusals](#bundle-document-refusals) carry one.

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

The copy inherits the source row's `FamilyId`. A family fork allocates through `AllocateInFamilyAsync`, so its
id belongs to one of that family's blocks. A full family can reserve another block, and the copy need not occupy
the source row's block. A non-family fork uses the plain counter. The source keeps its id and new values, and
exactly one `MovedToLegacy` rule targets the live flagged copy.

This policy applies to newly allocated copies. A copy forked by 19.0.0 through 20.18.0 has a plain id and
still names the source row's family. It is never renumbered or repaired automatically, and a live store keeps
using it as stored. Re-importing its export is refused with `KEC0037` before any write. To move such a store,
remove the copy's `familyKey` from the exported bundle, then import. The copy keeps its id, key and
`MovedToLegacy` rule. Design section 6.3 and [#908](https://github.com/APKiwiOrg/KhaozEngine/issues/908) hold
the reasoning.

### The key shape rule

`ContentKeyShape` preserves the public authoring entry point for the key rule of contracts 5.3:
`Defect(string)` answers the first defect as a phrase or null, `Rule` is the whole rule as one sentence for a
message to append, and `MaxKeyLength` is the cap. It delegates to `ContentKeyRules` in `KhaozEngine.Catalog`,
which also supplies the validator's UTF-8 span check. String diagnostics retain their UTF-16 positions and
lengths, and runtime-key diagnostics retain their byte units. The rule has callers at three layers. The
validator's `KEC0001` sweep walks the rows a candidate already holds. A fork's copy key never
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
`ContentAuthoringException` carrying the `edit-target-collision` reason and
`EditTargetOperationCollision` in `Refusal`. `Type`, `Id`, `RowKey`, `StandingOperation` and
`RequestedOperation` describe the compared target and its two intents.

## Families and id blocks

Text bundle and draft states, version text snapshots, text candidates, publish baselines and plans,
rollback plans and upgrade plans expose their owned collections through read-only views. Neither a
list cast nor `ICollection.SyncRoot` exposes their validated storage. `ContentChangeSet.Edits` is a live
read-only view, so a retained view sees later edits without permitting writes around target validation.

`ContentFamily` is an author-declared grouping within one content type whose members are allocated ids from
contiguous ALIGNED blocks, so a membership test is two comparisons per block rather than a set lookup. The
block size is declared at creation, is a power of two between 16 and 65,536, and cannot be changed later,
because changing it would move every id in the family. When a block fills a second one is reserved and the
family carries an ordered `ContentFamilyBlock` list. `Blocks` is an owned read-only snapshot, so a list
cast or `ICollection.SyncRoot` access cannot replace a validated block or change the family's membership test.

A family is never deleted. It is retired like a definition.

`CreateFamilyAsync` commits the family, its first aligned block, both allocator high-water marks and the
`family-create` audit together. A refused reservation or failed audit leaves all four unchanged, so the same
key can be retried without wasting definition ids. The in-memory reference stages its audit before exposing
the family and marks under its gate. Later block reservations retain the allocator's reserve-before-issue
commit order.

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
refused. Both refusals are `ContentAuthoringException` carrying the unchanged `id-ceiling-exceeded`
`Reason` token. The typed members below describe the type, inclusive ceiling and high-water marks.
Retired rows count toward a ceiling, because ids are never reused and a retired row keeps the number it occupies.

### Allocation and change-set refusals

| `Refusal` | What it refuses | Identifiers |
|---|---|---|
| `AllocationCeilingExceeded` | A plain id range whose top exceeds the type's inclusive ceiling | `Type`, `Ceiling`, `IssuedThrough`, `ReservedThrough`, `RequestedCount` |
| `FamilyReservationCeilingExceeded` | A new aligned family block whose highest id exceeds the type's inclusive ceiling | `Type`, `FamilyKey`, `Ceiling`, `BaseId`, `HighestId`, `IssuedThrough`, `ReservedThrough` |
| `FamilyUnknown` | Allocation or block reservation by a family id the persistence does not hold | `FamilyId`, with `Type` left at its default because the family is unknown |
| `EditTargetOperationCollision` | A change-set target already held under a different edit operation | `Type`, `Id`, `RowKey`, `StandingOperation`, `RequestedOperation` |
| `TypeUnknown` | A diff, candidate, rollback or pack row whose type the registry does not declare | `Type`, with the existing `Id` retained where the row is known |

The plain ceiling is the declared `maxDefinitionId`, or 2,147,483,647 when no ceiling is declared.
`BaseId` and `HighestId` are 64-bit so a refused block beyond positive `int` space is reported without
truncation. `FamilyUnknown` keeps `unknown-family`, `EditTargetOperationCollision` keeps
`edit-target-collision`, and `TypeUnknown` keeps `unknown-type` in `Reason`. Other store and publish
refusals still expose their legacy tokens when they carry no typed `Refusal`.

`IContentIdPersistence` is the durable half the allocator sits on, and every `Commit` member on it commits
on its own. A backend implements it with its own transactions.

**Every `Commit` member compares inside the commit that writes it**, because two hosts allocate from one
catalog with no lock spanning either allocation, and a mark read in one call can be passed by a rival before
the next call writes. `CommitReservedThroughAsync` raises the reservation to at least a value and leaves a
higher one standing. `CommitIssueAsync` takes the next `count` plain ids only while the live reservation still
covers them and answers the first, or 0. `CommitFamilyBlockAsync` inserts a block only while the type's issued
mark is still below its base and answers null otherwise. `CommitFamilyIssueAsync` takes a block's next free id,
or answers 0 for a full block. `CommitCarriedThroughAsync` is the publish's seeding write and raises both marks
to at least a carried id. A 0 or a null means a rival allocation succeeded in between, so the allocator reads
the marks again and tries once more, and the ceiling check on every pass ends it when the id space runs out.

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
both publish the same draft: the second one's expectation is stale. The row publisher reports
`BaseVersionMoved`, with `ExpectedBaseVersion` and `ActualBaseVersion` beside the unchanged
`base-version-moved` token.

### Publication and rollback refusals

The candidate builders, row publisher, pack writer, rebuild and `ContentRollback.Refusal` carry these
`ContentAuthoringRefusal` members. Existing legacy tokens, content type and definition id remain intact.
A consumer reads the reason and identifiers, never message wording.

| `Refusal` | What it refuses | Identifiers |
|---|---|---|
| `BaseVersionMoved` | A row or text publish expecting another baseline version | `ExpectedBaseVersion`, `ActualBaseVersion` |
| `NoPendingDraft` | A row publish with no open draft carrying pending edits | None |
| `FrozenDraftLost` | A policy publish whose store no longer holds its draft frozen for this base | `ExpectedBaseVersion` |
| `CandidateRejected` | A candidate rejected by validation | The complete owned read-only `Findings` snapshot |
| `CandidateEditOperationUnsupported` | A candidate edit outside the four supported operations | `Type`, `Id`, `TypeKey`, `ActualOperation`, `EditOrdinal` |
| `CandidateForkFlagInvalid` | A candidate fork flag absent from its schema or not Bool | `Type`, `Id`, `TypeKey`, `FieldName`, `EditOrdinal` |
| `DefinitionIdDuplicate` | A carried id already occupied in the candidate, including a preview | `Type`, `Id`, `RowKey`, `EditOrdinal` |
| `FieldUnknown` | A candidate edit naming an undeclared schema field | `Type`, `Id`, `TypeKey`, `FieldName`, `EditOrdinal` |
| `RowUnknown` | An update or retire naming an absent candidate row | `Type`, `Id`, `RowKey`, `EditOrdinal` |
| `PackPointerStoreRequired` | A publish or rebuild target without a version-pointer half | `StoreType`, `MemberName` (`ContentPublishCommit` or `RunAsync`) |
| `VersionUnknown` | A rebuild naming a version the store does not hold | `VersionNumber` |
| `RetireIrreversible` | A rollback across a retire, code `KEC0039` | First blocker's `Type`, `Id`, `RowKey`, plus `TargetVersion`, `BlockerCount` and complete `Findings` |
| `RulePrefixShortened` | A plan with fewer rules than the held history | `ActualCount`, `MinimumCount` |
| `RulePrefixEntryMismatch` | A different rule at an already held sequence | One-based `RuleSequence` |
| `TextPlanOnRowOnlyRoute` | A row writer handed the row half of a text plan | `MemberName` (`WriteAsync`), `VersionNumber` |
| `TextChunkMissingOnRowOnlyRoute` | A row writer naming a text chunk absent from its pack store | `MemberName` (`WriteAsync`), `Language`, `TextHash` |

`CandidateRejected` is one rule regardless of how many findings the validator returns. Each
`ContentFinding.Code` is the stable identity of its finding, including codes a consumer policy supplies.
There is no singular finding-code identifier. Both legacy and typed finding-taking exception constructors
copy the complete list into a read-only snapshot, so later producer mutation cannot change the refusal.
Engine construction is fenced across Authoring, SQLite, SQL Server and Server.Admin. The public legacy
constructors still leave `Refusal` null for consumer instances, with their existing `Type`, `Id`, `Reason`,
`Findings` and inner cause intact. Structured constructors use a private initialization path rather than
calling a legacy constructor. Defensive candidate checks retain `ContentAuthoringException` and their legacy
`unknown-edit-operation` and `fork-flag-field` tokens, with the candidate members above. Sealed edit factories
admit only supported operations. Public publication runs fork preflight, while the internal candidate builder
can admit a fork directly and reports an invalid flag through the same structured carrier.

### Durable schema and reset refusals

Both durable providers retain their legacy `schema-mismatch`, `catalog-partial` and `draft-open` tokens
beside `ContentAuthoringException.Refusal`. SQLite also retains `host-foreign-key`. Store-wide `Type` and `Id` remain
at default and 0. Schema objects use the provider identity in `ObjectName`, such as
`index:ix_catalog_row_live` on SQLite and `check constraint:catalog_row.ck_catalog_row_retired` on
SQL Server. SQL Server row time columns retain the complete compared entry, such as
`row time column:catalog_row.created_at_utc|datetimeoffset|3|NULL`. This includes type, scale and
nullability without parsing a diagnostic string. A wrong SQLite table or column definition names its owning table.
Provider read failures keep their typed `InnerException` and the existing message text.

| Refusal | What it refuses | Identifiers |
|---|---|---|
| `SchemaMissing` | Validation of an absent catalog | `RequiredMigration` |
| `SchemaValidationMetadataMissing` | Schema validation or reset missing a required metadata value | `RequiredMigration` |
| `SchemaMetadataUnreadable` | Provider failure reading schema metadata | `RequiredMigration`, provider `InnerException` |
| `SchemaVersionMalformed` | A noninteger stored schema version | `MetadataType`, `RequiredMigration` |
| `SchemaVersionUnsupported` | A schema version this mode cannot use | `ActualVersion`, owned `SupportedVersions`, `RequiredMigration` |
| `SchemaForeignKeysDisabled` | SQLite validation without foreign key enforcement | `RequiredMigration` |
| `SchemaApplicationLockNotGranted` | SQL Server schema lock request returning a negative result | `ReturnCode`, `RequiredMigration` |
| `SchemaObjectMissing` | An absent schema object or required row time shape | `ObjectName`, `ExpectedVersion`, `RequiredMigration` |
| `SchemaObjectDefinitionMismatch` | A table or index differing from its declared shape | `ObjectName`, `ExpectedVersion`, `RequiredMigration` |
| `SchemaObjectUnexpected` | An undeclared schema object or row time shape | `ObjectName`, `ExpectedVersion`, `RequiredMigration` |
| `ResetTablesPartial` | Reset of incomplete tables with a readable version, without force | `StandingTableCount`, `ExpectedTableCount`, `ActualVersion`, `ForceRequired` |
| `ResetVersionUnreadable` | Reset without a readable version, without force | `StandingTableCount`, `ExpectedTableCount`, `ForceRequired` |
| `ResetVersionNewer` | Reset that would recreate an older schema | `ActualVersion`, inclusive `MaximumVersion` |
| `ResetDraftOpen` | Reset that would destroy a draft without force | `DraftCount` |
| `HostForeignKeyActionUnsafe` | Reset whose host key delete action would write host rows | Owned `HostObjects` of `ContentHostForeignKeyFinding` |

`SchemaValidationMetadataMissing` covers an absent metadata row or a NULL required metadata scalar and
includes the migration the schema check prescribes. Ordinary store
reads retain `SchemaMetadataMissing` with no identifiers. `ResetDraftOpen` reports draft rows rather than
the companion import's `DraftOpen` edit counts. A non-null reset scalar of the wrong CLR type reuses
`StoredScalarTypeMismatch` with `ExpectedScalarType` and `ActualScalarType`, for integer reads and the
post-recreation text epoch alike. Even the latter is an input check: SQLite's table inventory leaves views
and their INSTEAD OF triggers standing, and a metadata-shaped view can intercept the seed. A missing epoch
uses `SchemaValidationMetadataMissing`. SQL Server database DDL triggers can likewise alter the recreated
metadata table and install a seed trigger. Its post-recreation version and epoch reads therefore remain
input checks with the same refusal members, rather than internal invariants. The refusal rolls back trigger
writes as well as catalog changes. SQL Server schema locking carries the integer return code from
`sys.sp_getapplock`, including timeout, cancellation, deadlock and call errors, without parsing provider text.

`ContentHostForeignKeyFinding` names `HostTable`, `ReferencedTable` and the declared `DeleteAction` for every
unsafe key. Composite key columns with the same table pair and action are folded, as in the existing reset
diagnostics. No key name or schema is invented where the provider reports none. `SupportedVersions` and
`HostObjects` copy their inputs into fresh immutable snapshots. Both providers currently support schema
version 4 and names migration `catalog-v4-text-authoring`. A partial reset measures against 19 current tables.

### Store admission and confirmation refusals

The in-memory, SQLite and SQL Server stores carry these members alongside their legacy tokens. Row-boundary field checks reuse
`FieldUnknown` with the submitted batch's zero-based `EditOrdinal`. Family-id lookup reuses `FamilyUnknown`,
unknown registrations reuse `TypeUnknown`, and absent versions reuse `VersionUnknown`, including pinning.
Candidate rejection retains the complete `Findings` snapshot. Stale base checks reuse `BaseVersionMoved`.
A text commit whose frozen draft is absent reuses `FrozenDraftLost` with `ExpectedBaseVersion`.
Text apply reuses `TextBindingRowUnknown` and `TextLanguageUndeclared` with their existing identifier sets.

| `Refusal` | What it refuses | Identifiers |
|---|---|---|
| `FamilyDeclarationBlockSizeOutsideRange` | A store declaration outside the inclusive block-size range | `Type`, `FamilyKey`, `ActualBlockSize`, `MinimumBlockSize` (16), `MaximumBlockSize` (65,536) |
| `FamilyDeclarationBlockSizeNotPowerOfTwo` | An in-range store block size that is not a power of two | `Type`, `FamilyKey`, `ActualBlockSize` |
| `FamilyKeyDuplicate` | A family key already declared for the type | `Type`, `FamilyKey` |
| `DerivedMarkerNotAuthorable` | A row edit authoring a derived marker | `Type`, `Id`, `TypeKey`, `FieldName`, `EditOrdinal` |
| `BundleTypeDeclarationMismatch` | A bundle's key or chunk slots disagreeing with registration | `Type`, `ActualTypeKey`, `RegisteredTypeKey`, `ActualChunkSlots`, `RegisteredChunkSlots` |
| `BundleFamilyUnknown` | An imported row naming no carried family | `Type`, `Id`, `RowKey`, `FamilyKey` |
| `CatalogNotEmpty` | Import into a catalog that already published | `ActiveVersion` |
| `NextVersionMismatch` | A plan whose number is not the next after the highest held version | `RequestedVersion`, `HighestVersion`, `NextVersion` |
| `PackStoreMissing` | An operation requiring an absent pack target | `MemberName` |
| `PackUnreadable` | A pack read returning a refusal | `VersionNumber`, `ObjectHash`, stable `PackReaderReason` token |
| `PublishInProgress` | A draft write while publication holds the freeze | `MemberName`, `FrozenForBaseVersion` (full stored Int64 marker) |
| `UpgradeAlreadyRecorded` | A publish applying an upgrade already in the ledger | `UpgradeId` |
| `TextForkOnRowOnlyRoute` | A fork whose source text the row-only route cannot copy | `MemberName`, `TextRowType`, `TextRowId` |
| `TextBaseOnRowOnlyRoute` | A row-only rollback across recorded text | `MemberName`, `VersionNumber`, `RecordedLanguageCount`, `RecordedValueCount` |
| `TextManifestOnRowOnlyRoute` | A row-only plan naming text chunks other than the baseline's | `MemberName`, `VersionNumber`, `ActualCount`, `ExpectedCount` |
| `TextPlanEpochMismatch` | A text plan frozen in another store | Plan's `ExpectedEpoch`, store's `ActualEpoch` |
| `TextPlanFreezeMismatch` | A draft freeze that no longer names the active base | `ExpectedBaseVersion`, nullable freeze marker `ActualBaseVersion` |
| `TextPlanDraftMismatch` | A complete frozen draft differing from the plan's | `ExpectedBaseVersion` |
| `TextPlanBaselineMismatch` | Baseline text or languages differing from the plan's | `ExpectedBaseVersion` |
| `TextCloseRevisionMissing` | A text close absent from the store | `TextRowType`, `TextRowId`, `FieldName`, `Language`, `FromVersion` |
| `TextPlanRowUnknown` | A text commit revision without a live plan row | `TextRowType`, `TextRowId`, `FieldName`, `Language` |
| `SchemaMetadataMissing` | A durable store read missing its `catalog_metadata` singleton | None |
| `StoredScalarTypeMismatch` | A non-null stored scalar of the wrong CLR type | `ExpectedScalarType`, `ActualScalarType` |
| `TypeIdentityMismatch` | A durable registry pairing disagreeing with a stored id or key | Requested `Type`, `StoredTypeId` (full persisted Int64), `StoredTypeKey`, `RegisteredTypeKey` |
| `DraftRowDisappeared` | A draft singleton removed during its transaction | None |
| `StoredEditOperationUnknown` | A persisted edit operation outside the four supported operations | `Type`, `Id`, `ActualOperation`, persisted `StoredEditOrdinal` (Int64) |
| `DraftEditIdentityMissing` | A SQL Server draft-edit insert returning no identity | `Type`, `Id` |
| `FamilyIdentityMissing` | A SQL Server family insert returning no identity | `Type`, `FamilyKey` |
| `TransactionContended` | A SQL Server transaction refused with error 1205 or 3960 | `SqlErrorNumber` |

`TextRowType` and `TextRowId` identify rows without changing the existing store-wide legacy `Type` and
`Id` values of default and 0. Pinning an absent version retains its existing `unknown-type` legacy token
while `Refusal` reports `VersionUnknown`. A pack reader returns a stable token rather than an exception,
so `PackUnreadable` preserves that token without inventing an inner exception.
SQLite uses the same member and identifier set for each shared rule. Its missing metadata read and stored
type identity checks use the durable-store members above. No migration identifier is invented for a missing
singleton. Non-null malformed scalars use `StoredScalarTypeMismatch`, carrying the expected and actual CLR
types without copying the stored value. A host trigger can remove the draft inside its transaction, and malformed persisted values
can violate an otherwise correct operation `CHECK`. SQLite validates catalog DDL, not trigger behavior or
every stored value. These input failures use `DraftRowDisappeared` and `StoredEditOperationUnknown`.
`StoredEditOrdinal` is the database ordinal, distinct from a submitted batch's zero-based `EditOrdinal`.

The in-memory store keeps `TextProvenanceUnknown` with `VersionNumber` when a version has no complete
text record. A row-only check retains the legacy `text-unrepresented` token, while a snapshot read
retains `text-provenance-unknown`. An import losing the draft its row step just created remains a
separate invariant.

### Text producer and confirmation refusals

The text candidate and chunk builders check eligibility and strict byte bounds before encoding. Their reasons name the producer and keep one identifier set per member. The bundle reader
reports its own JSON paths. The defensive value-byte guard uses `TextProducerValueBytesExceeded` with row context.
A malformed Unicode finding keeps the original `ArgumentException` as its inner,
including its parameter name. No reason is inferred from prose.

| `Refusal` | What it refuses | Identifiers |
|---|---|---|
| `TextBindingRowUnknown` | Text binding without a single live row for its key | `Type`, `Id` 0, `RowKey` |
| `TextFinalRowUnknown` | Text whose final row is absent | `Type`, `Id`, `FieldName`, `Language` |
| `TextLanguageUndeclared` | Text whose language neither the baseline nor the draft declares | `Type`, `Id`, `FieldName`, `Language` |
| `TextTargetIneligible` | Text without a registered CLIENT type and a CLIENT localized marker | `Type`, `Id`, `FieldName`, `Language`, `TypeKey` when registered |
| `TextProducerMalformedUnicode` | An unpaired surrogate in a type key or marker field used to derive text keys | `Type`, `Id`, `FieldName`, `Language`, `MemberName` (`TypeKey` or `FieldName`), zero-based UTF-16 `Index` |
| `TextProducerWireTagBytesExceeded` | A producer wire tag above 35 ASCII bytes | Legacy `Type` default and `Id` 0, `Language`, `WireTag`, `ActualBytes`, `MaximumBytes` (35) |
| `TextProducerValueBytesExceeded` | A producer value above 8192 strict UTF-8 bytes | `Type`, `Id`, `FieldName`, `Language`, `ActualBytes`, `MaximumBytes` (8192) |
| `TextDerivedKeyByteCountMismatch` | A derived key re-encoding to a different byte length than measured | `Type`, `Id`, `FieldName`, `Language` |
| `TextKeyUtf8Invalid` | Invalid UTF-8 in the content key | `Type`, `Id`, `FieldName`, `Language` |
| `TextKeyBytesExceeded` | A derived key above 192 strict UTF-8 bytes | `Type`, `Id`, `FieldName`, `Language`, `ActualBytes`, `MaximumBytes` (192) |
| `TextKeyCollision` | Two values deriving one key in the same language | `DerivedKey`, canonical `Language` |
| `TextChunkBytesExceeded` | A language body above 16 MiB, including count and length prefixes | `Language`, `ActualBytes`, `MaximumBytes` (16,777,216) |
| `TextChunkHashMismatch` | A chunk hash different from the plan's regenerated values | `Language`, claimed `TextHash`, regenerated `ExpectedTextHash` |
| `TextChunkBytesMismatch` | Stored bytes different from the regenerated file | `Language`, `TextHash` |
| `TextReusedChunkUnrecorded` | Reuse of a language absent from the baseline | `Language`, baseline `VersionNumber`, `TextHash` |
| `TextReusedHashMismatch` | Reuse of a hash the baseline did not record | `Language`, baseline `VersionNumber`, claimed `TextHash`, recorded `ExpectedTextHash` |
| `TextReusedWireTagMismatch` | Reuse in another wire spelling than the baseline recorded | `Language`, baseline `VersionNumber`, `TextHash`, claimed `WireTag`, recorded `ExpectedWireTag` |
| `TextProvenanceUnknown` | A version without complete text provenance, including a missing in-memory language record | `VersionNumber` |

The key and body bounds are inclusive. Repeating the operation at the reported `MaximumBytes` succeeds
when that bound is the only failed rule. The sealed declaration and revision constructors already enforce
35-byte ASCII wire tags and 8,192-byte strict UTF-8 values. The wire-tag and measured-length re-encoding
guards retain `ContentAuthoringException` and the legacy `text-bounds` token, with their producer members above,
even when earlier admission makes them defensive. A derived key's nonempty lower bound follows from its
nonempty type and field plus two separators. Commit confirmation still checks reuse spelling because the
plan's public list facades can be changed after construction.

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

A successful commit consumes its draft and marker before any later sweep runs. Unfinished attempts use
`ContentPublishCommit.PublishAsync`'s `finally` to release only their expected base through
`IContentDraftFreezeStore` when supported. Legacy external stores retain the original unscoped cleanup.
Two things recover a marker nothing cleared, which is what a killed process leaves. A marker naming a
version the store has moved past is STALE, and
`ReadPublishBaselineAsync` clears it, which is the read every publish starts with. A marker naming the
version the store still stands at belongs to a publish that died before its commit. A later publish
overwrites that marker at step 1 and clears it on exit.

The same-version marker carries no publisher identity, so an edit or discard cannot tell a dead publisher
from a live one and never clears it. After confirming no publisher is live, a host can call
`IContentAuthoringStore.ClearDraftFreezeAsync` to release the marker and preserve every pending edit, so the
operator can continue editing or intentionally discard the draft. Run `catalog-publish` only when the current
draft is intentionally ready to publish, since it may commit that draft. Its step 1 replaces the marker and
its exit clears it.

Driving `ContentPublisher.PrepareAsync` on its own therefore leaves a frozen draft behind, deliberately: half
a publish is the state the marker describes. Call `ClearDraftFreezeAsync` when standing in for the commit.

### Ids come from the edit, not from the caller

There is ONE allocation path with two sources, and which one runs is a property of the edit. An `Add` with
`definition_id` 0 is allocated one, an `Add` carrying a non-zero id keeps it, and two paths write the second
kind and no others: a bulk import into an empty database, and a content upgrade adding a row the committed
bundle already names by id. Both carry the number because a game states a definition id as a code constant,
and the allocator's durable mark sits above the highest row id whenever an earlier publish was refused after
it reserved. Before allocating any new id, the high-water marks are SEEDED from the largest carried id per
type. A single ordinary `ApplyEditsAsync` and `PublishAsync` candidate can therefore combine `Import` and
`Add` for the same type in either edit order, preserving the imported ids and allocating plain additions above
the carried maximum. Additions still allocate in edit ordinal order, and new family blocks open above the
carried maximum. `ContentIdAllocationRecord.Seeds` is empty when a publish carries no ids.

The seeding raises each mark to the greater of its current value and the carried id, and the store makes that
comparison inside the same commit as the write. Two upgrade runners seed one type without a lock spanning
either publish, so a mark read first and written in a later call could be passed by the other runner's seed
in between, and writing the stale lower number would take a reservation back. A mark a rival already
raised past the carried id is left where it stands, and `Seeds` then omits that type.

A carried id is checked before the candidate is built, because step 3's seeding commits on its own and a
refusal after it would leave the marks raised for a version nobody published. The publish refuses a carried
id that a live or retired row already holds, one a second add in the same draft names, one over the type's
declared ceiling, and one that disagrees with the type's family blocks, under `KEC0036`, `KEC0042` and
`KEC0037`.

Allocation runs BEFORE validation, because `KEC0006` resolves references and `KEC0010` asks about family
membership, and neither can be asked of a row whose id does not exist yet.

A `Fork` inherits the SOURCE row's family. A family copy allocates within that family's blocks, reserving
another when needed, while a non-family copy uses the plain counter. The copy is
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

**The ordering is the whole crash-safety property.** Step 9 writes every chunk file, the remap rule chunk
and both manifest files to the pack store BEFORE the database transaction, at content-addressed names nothing
references yet, so a crash there leaves inert bytes and the old version. A hash the store already holds is
checked for with `ExistsAsync` and not rewritten, which is what makes a republish of an unchanged chunk free.
Two publishers may run step 9 at once, because every file it writes is named by its own content.

**The version pointer is written inside step 10, not step 9.** `ContentPublishCommit` hands its pointer half to
`CommitPublishAsync`, and the store writes the pointer as the last act before its transaction commits, once
the version number is confirmed. Two publishers can prepare the same number from the same base, for example two
replicas of different builds applying one upgrade, and a pointer written in step 9 let the one that went on to
lose overwrite the committed version's pointer with manifests that never committed. Before the pointer checks,
the boot served what that pointer named and the sweep kept what it named, so that was content served that never
committed and content deleted that did. Now a losing publisher is refused before it writes a pointer, and two
publishers of one version never write it at once. A crash after the pointer write and before the commit leaves a pointer for a
version that never committed, which keeps orphan files alive rather than deleting live ones, and the next
attempt at that number overwrites it.

The version POINTER at `versions/<n>` is the one object in a store not named by its own hash, and it is how a
content-addressed store answers what a version contains once the authoring database is out of reach.
`IPackVersionPointerStore` is its seam, separate from the read side's `IPackStore` for the same reason pruning
is: a read-only provider cannot be a half-working publish target, and that is a compile-time fact rather than
a runtime throw. `PackVersionPointers.Resolve` finds the half, and a store with none is refused at the top of
the publish rather than after the chunk files are already written.

Step 10 is `IContentAuthoringStore.CommitPublishAsync`, ONE transaction and the only member that moves the
active pointer. In order inside it: confirm the version number, insert the version row, apply every temporal
row change, append every remap rule at the sequence above the highest, insert every chunk row one per side
including the carried-forward ones, insert every audit row, delete the draft, move the active pointer, then
write the pack's version pointer through the `IPackVersionPointerStore` it was handed, or none when it was
handed null. A reader that sees the new active version sees every row, rule, chunk and audit entry of it,
because they committed together.

The draft delete is scoped to `ContentPublishPlan.FrozenEdits`, the change set step 1 read. The freeze is
what makes that the whole draft, so scoping it can only matter when the marker failed to hold, and that is
the point: an edit the publish never carried survives into the next draft rather than being deleted
unpublished.

Publish cleanup uses the optional `IContentDraftFreezeStore` companion on the in-memory, SQLite and SQL
Server stores. Its `ClearDraftFreezeAsync(expectedBaseVersion)` compares and releases the freeze marker
in one gate or transaction. Cleanup from an earlier version leaves a later frozen draft
untouched. A successful commit consumes its draft and owes no release, including when the following sweep
fails. The upgrade runner scopes its own failed attempt's release the same way.

The original `IContentAuthoringStore.ClearDraftFreezeAsync()` remains the unscoped recovery operation.
External stores and wrappers without the companion keep that legacy cleanup behavior, which cannot protect
a concurrent later draft. The companion scopes base versions, not publishers sharing the same draft.

**It CONFIRMS the version number rather than trusting it.** The plan digested its number into both manifest
hashes at step 8, so the transaction re-reads the highest published number and refuses with the
`base-version-moved` reason when the plan's is not the next one. A provider that leases a connection per call
holds no lock across steps 1 to 10, and this is the check that catches a base that moved underneath such a
plan. The same statement is made about the rule list, which has to be the plan's own prefix.

Step 11 is `ContentPackSweep`, the orphan sweep, which runs only after a SUCCESSFUL commit. **The validated
keep set is the union, over every durable version record the store knows, of its two manifest hashes and
every hash named inside either manifest**. `ContentPackClosure.ReadAsync` walks those hashes directly,
without consulting a mutable pointer again. It is defined against
the MANIFESTS and not against the chunk table, because the rule chunk sits at a reserved address outside any
type's id space and the text chunks are per language, so neither has a chunk row to hang on while both are
named by both manifests. A keep set read from the chunk table would delete them at the first publish and every
later boot would fail closed on an absent chunk, for every version, forever.

The publish and operator sweep pass every durable `ContentVersionRecord` to
`ContentPackSweep.RunValidatedAsync`.
Before listing any version or enumerating orphan objects, it resolves each pointer and compares BOTH manifest
hashes ordinally with that record. A missing pointer source, an absent or partial pointer, or either hash
disagreeing skips the sweep with `listing-failed` and deletes nothing. This protects live chunks when another
catalog has replaced a pointer at the same version number. A matching pointer still needs a complete listing.
After these checks, the durable record's immutable manifest hashes remain the authority for the whole keep
set. A pointer replaced between validation and listing cannot substitute another catalog's closure or cause
the record's live chunks to be deleted.
The sweep is SKIPPED when that listing fails for any reason. It is skipped again when the store implements no
pruning half.
`ContentPackSweepResult` carries the reason either way, because deleting nothing and deleting everything are
one keystroke apart and an operator reading a publish response deserves to know which happened.

The existing `RunAsync(IPackStore, IReadOnlyList<int>, CancellationToken)` entry point remains available for
compatibility. It trusts the pointer-backed listings without checking durable records, so its caller must
establish that those pointers name the authoritative manifests. Use `RunValidatedAsync` with version records
whenever they are available.

`ContentPublishCommit.SweepAsync` is the step as the publish runs it, and it is public so a test can drive
step 11 on its own. An OPERATOR reaches `ContentPackSweep` through the admin surface instead, because a
recovery sweep has no prepared `ContentPublisher` to build a commit around, and because the operator's sweep
is no longer the same operation: it refuses while a publish holds the draft frozen, and it records an audit
row naming who ran it.

### Consumer operator publication policy

A host can attach an `IContentPublishPolicy` to its operator surface. The engine admin helper takes it
through `CatalogAdminActionOptions.PublishPolicy`, captures it at registration and binds it to the
in-process `ContentPublishRequest.PublishPolicy`. HTTP JSON cannot supply or override this property.
The same original store, registry and publisher perform publication.

Custom operator surfaces use `ContentDraftCandidate.Build(baseline, changes, registry, policy)` for preview
and set the same host-owned policy on their publish request. The existing publisher evaluates it again on
the frozen baseline and candidate after the unchanged validator sweep, before encoding or commit.

```csharp
sealed class ArtifactPolicy(ContentTypeId guardedType, string guardedField) : IContentPublishPolicy
{
    public void Validate(ContentPublishPolicySnapshot baseline, ContentPublishPolicySnapshot candidate,
        ICollection<ContentFinding> findings)
    {
        foreach (ContentPublishPolicyRow row in candidate.Rows(guardedType))
        {
            if (baseline.TryGetRow(row.Type, row.Key, out var old)
                && old.GetValue(guardedField) != row.GetValue(guardedField))
            {
                findings.Add(row.Finding("artifact-rebuild-required", "Matching baked artifacts are required."));
            }
        }
    }
}
```

The example checks field changes on existing rows only. A consumer protecting a row's existence must
also compare membership in both snapshots and `IsRetired`, including additions and rows absent from the
target version.

The example's `guardedType` and `guardedField` are host-owned identifiers for a value a game's baked
artifacts depend on. Snapshot views contain detached rows, read-only collections and typed field values.
Byte payloads are copied on access. No store or mutable registry is supplied. Policies must be deterministic
and side-effect free. The initial baseline is an empty view at version zero. New ids at preview remain
provisional, so use the stable row key to identify a finding.

`row.Finding(code, reason)` supplies the existing finding shape with structured type, id and optional `Key`.
Policy codes are preserved without folding them into a type validator's message. A policy throw adds
`IContentPublishPolicy.FailureCode` (`KEC0044`) and refuses publication. A refusal leaves the active version
and complete draft unchanged and releases its freeze, like any other validation refusal.

Operator version selection also checks the registered policy. `catalog-pin` compares the current pin,
or the active version when no pin exists, with the requested target. Clearing the pin compares that
pinned version with the active version. Selecting the already effective version needs no policy check.
Changed selections load both complete published snapshots and evaluate the policy through
`ContentPublishPolicyEvaluation.Validate`, which checks only the consumer policy rather than running
publish-history validation backwards.

`IContentVersionSelectionStore.TrySetPinnedVersionAsync` atomically compares the checked
`ContentVersionSelection` (catalog epoch, active version and pin) and writes the hold and audit together.
The bundled providers implement it. If a snapshot cannot be read or verified, the conditional capability
is missing, or selection metadata moves during validation, the action refuses with
`IContentVersionSelectionStore.UnavailableCode` (`KEC0045`) and does not write the hold. Policy findings
and `KEC0044` exceptions use the existing structured refusal payload. Direct trusted server calls to
`SetPinnedVersionAsync` retain their recovery behavior. With no policy the operator action keeps its
existing direct setter path without reading snapshots.

HTTP `catalog-import` is an operator publication too. The bundled stores implement the optional
`IContentPolicyImportStore` capability, whose `ImportWithPolicyAsync(bundle, request)` requires a
`ContentPolicyImportRequest` carrying a non-null policy and the server's actor, operator and note. It uses
the existing seed staging, text handling and frozen publisher. The admin handler binds its configured
policy to this request, never a value from JSON. A custom store lacking this capability refuses operator
imports under `IContentPolicyImportStore.UnavailableReason` when a policy is configured. The admin
boundary carries this refusal on `ContentAuthoringException` with the following structured member.

| `Refusal` | What it refuses | Identifiers |
|---|---|---|
| `PolicyImportCapabilityMissing` | An operator import with a host policy and a store without policy import support | `RequiredCapability` is `typeof(IContentPolicyImportStore)` |

The legacy `Reason` stays `policy-import-unavailable`, with default `Type`, `Id` 0 and empty `Findings`.
A lower provider refusal passes through the operator adapter as the original exception, retaining its
`Refusal`, identifiers, findings and inner cause.

Trusted server calls to `ImportBundleAsync` and `ImportTextBundleAsync`, release tooling and upgrade
publication keep their existing paths without an operator policy. These paths can ship matching artifacts
and do not become an HTTP bypass. No configured policy preserves
the existing validation sequence, version numbering and published bytes.

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

**Text is version N's own.** Through `IContentTextAuthoringStore` the rebuild reads
`ReadTextSnapshotAsync(n)`, regenerates every `KECT` chunk in the wire spelling version N recorded, and compares
each hash and the language list with the recorded mapping before the digest check and before any write. The
text chunks are written after the row chunks and before the rule chunk and the manifests. A store without the
companion cannot read a version's text, so it rebuilds only a version whose baseline names no language and
refuses any other outright.

A legacy version with no complete text record is treated as text free only on a read-only proof: the rebuilt
no-language manifests digest to the recorded hashes, or verified stored manifests at the recorded hashes, in
the target or the store's own pack, name no language. Nothing records that proof.

The seven refusal reasons, all reported as `RefusalReason` on the result rather than thrown, are therefore
`server-manifest-mismatch`, `client-manifest-mismatch`, `rebuild-candidate-invalid` (the builders refused a
row, which is a caller's own side encoder leaving a `ServerOnly` field in the client bytes),
`text-chunk-mismatch` (a regenerated text chunk or the language list disagrees with the recorded mapping),
`text-values-invalid` (an ineligible marker, a missing row or a bound stops the regeneration),
`text-provenance-unknown` (no complete text record and no read-only proof) and `text-chunks-unsupported` (a
store without the companion whose baseline names a language).

## Rollback

`RollbackToAsync(targetVersion)` BUILDS A DRAFT rather than publishing one, so an operator reviews the diff
and publishes it. For every row live at both versions whose field set differs it emits an `Update` restoring
the target's values. For every row introduced AFTER the target it does nothing at all: the row keeps its id
and its values, which is the difference between a rollback and a restore.

**The version number keeps climbing throughout.** A rollback is never a return to an old number, which is the
same property that lets a durable page's version stamp be an ordering comparison.

**A row live at the target and RETIRED since is a flat refusal**, `KEC0039`, naming the row and the rule that
retired it when that rule is present. There is no un-retire branch. An ordinary publish appends exactly one
`Retired` rule for each retire. A provider baseline without a matching rule still refuses the rollback.
The way out is an ordinary `Add` under a NEW key carrying the old values, because a key is immutable once
published, and the retired row keeps its id and its bytes forever so a stored stack still decodes.

`ContentRollback.Prepare` is the plan behind it and `ContentRollbackPlan` is what a console renders: the
edits, the blockers with any matching rule, and one `KEC0039` finding per blocker. Each
`ContentRollbackBlocker.RuleKind` carries that rule's actual kind, or null when no rule matches. In that
case `RuleSequence` and `IntroducedIn` are 0. The original five-argument constructor and deconstruction
remain available.

`RollbackToAsync` is row-only. On a store with the text companion it refuses with `text-unrepresented` when the
current or the target version holds text or has no complete text record, and `RollbackTextToAsync` restores
rows and strings together, as [Catalog text](#catalog-text) describes.

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

`InMemoryContentAuthoringStore.SchemaVersion` is the public catalog schema version this engine build
targets, currently 4, matching both providers. Consumers use that constant for catalog preflight and deploy
compatibility checks. `IContentAuthoringStore.GetSchemaVersionAsync()` reports the schema held by the opened
store, which the in-memory store also reports as 4.

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
| A pending definition ordered below one the catalog already holds, or below one a recovered draft makes this run publish first | `KECU0015` | Two feature branches merging produces both. The pending one has not run, so it runs now, against the catalog as it stands. |

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

### The rules a planner follows

1. **Detect by identity, never by value.** A row present under the committed id and the committed key is
   present whatever its fields hold, because those fields may be operator tuning an upgrade has no business
   reverting.
2. **A value patch names the old shipped default it replaces.** A field already holding the new value is
   satisfied, a field holding the named old default is patched, and a field holding anything else belongs to
   an operator and is left exactly as it is.
3. **A LIST is appended to by element, never patched by value.** A tag list an operator has extended equals
   no default this build ever shipped, so a patch would leave that row unappended forever. `AppendTag` reads
   the list the row holds, and a list already carrying the element is satisfied while a list without it is
   written back with the element at the END. Every element already there keeps its position, nothing is
   sorted and nothing is deduplicated. A field whose schema kind is not a tag list is refused, and so is a
   tag id that is not a live tag row of the baseline or a tag row the same plan adds, which is exactly what
   the publish validator accepts in a list. A retired tag row is not one of them.
4. **A partial state is refused rather than completed.** Completing the remainder guesses which of the
   existing rows an operator owns, and that guess is unrecoverable once it publishes. Every verb counts
   toward the same satisfied and pending tallies, so a build saying "these six rows each get this tag, all
   six or none" states it as six appends and gets a refusal on a mixed catalog.
5. **One row is one edit.** A draft holds one pending intent per row, so the builder merges every patch and
   every append of one row into a single update carrying all the changed fields, in the order they were
   staged.

A staging a draft could not hold verbatim is an `ArgumentException` from the builder, which the runner
reports as an ordinary refusal: one field written twice under either `PatchField` or `AppendTag`, a row both
written and retired, and one row staged twice under either verb. None of them has a single answer the draft
could keep, and a plan that emitted two edits of one row would lose one at the store and then fail its own
recovery check for good. Appending to a row the same plan ADDS is refused as well, because the catalog holds
no such row yet and the values it is created with are the committed bundle's.

`ContentUpgradeChecks` and `ContentUpgradePlanBuilder` are those rules as code, generic over the types and
keys a caller passes. The checks are the ones the first hand-written catalog upgrade command proved: the
committed target bundle agrees with this build's registry, every baseline type is schema compatible with the
target, an identity is absent or present under BOTH the same id and the same key (anything else is a
conflict), and plain allocation will issue exactly the committed ids, contiguously from the type's current
highest row id. A type with id families is refused outright, because a family allocates from aligned blocks
and nothing here can prove which id a member would be issued.

Every check ANSWERS a refusal string rather than throwing, because the run reports catalog state to an
operator and a stack trace is not an instruction. A planner that throws `InvalidDataException`,
`MalformedDataException`, `ContentAuthoringException` or `ArgumentException` is still reported as a refusal,
which is the shape a hand-written check already has.

```csharp
var definition = new ContentUpgradeDefinition(
    id: "2026-09-add-recipe-type",
    order: 4,
    description: "adds the recipe type's rows",
    plan: context => new ContentUpgradePlanBuilder(context, ShippedBundle)
        .AddRows([new ContentUpgradeIdentity(recipeType, new ContentKey("iron_bar"))])
        .AppendTag(itemType, new ContentKey("iron_ore"), "tags", smeltableTagId)
        .RetireRow(recipeType, new ContentKey("iron_ingot"), ContentRetirePolicy.Placeholder)
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
   stands, and its definition runs FIRST, ahead of any pending upgrade ordered below it, because only that
   definition can publish the draft that is standing and every other one would read it as a rival
   publisher's. The jump is reported as `KECU0015` and changes nothing else: each definition is still planned
   against a baseline exported at the version active right then. It is never discarded first and a marker
   found standing over it is never cleared on sight: a marker naming the active version is a live publish or
   a dead one and nothing on the seam tells them apart, and a publish is the store's own recovery for a dead
   one. The publish path freezes the draft for itself and proves it again under that marker before taking it,
   which the two draft proofs below set out. Any other open draft is operator work: `OperatorDraftOpen`,
   draft untouched. The
   publish pre-flight asks the same question, so a draft that appears after this step is answered the same
   way, and only another runner's is waited out. What may be DISCARDED is a separate and weaker question,
   answered below under the two draft proofs.
6. A pin on another version is `PinnedElsewhere`. A pin on the active version does not block the publish, the
   pin is never moved, and the report carries a `PinHeld` diagnostic naming the version to repin to.
7. A supplied `ExpectedVersion` that is not the active version is `BaselineMoved`. It is checked again at
   EVERY later re-read of the active version, so a run that stood off and came back to a version a rival left
   stops rather than publishing onto a baseline nobody previewed. Only a version this run published itself is
   not a move.
8. Each pending definition runs in ascending order, after any definition whose draft step 5 recovered, and
   publishes as its OWN version, so history shows each
   upgrade separately and an interruption between two of them resumes at the second. The planner is handed a
   bundle exported at the version active right then, so the second definition sees the first one's result.
   The minimum builds rise to at least the running build's ordinals and never fall below the baseline's.
9. After ANY publish failure the run READS the ledger and reports the disposition the row holds, which is
   `Adopted` when a rival found the catalog already satisfied and published no version at all. It discards a
   draft only under one of the two proofs below, and a `publish-in-progress` refusal means a marker stands, so
   the run treats the publisher as live, leaves the draft alone and stands off. A publisher that died before
   commit leaves the same marker and needs the explicit recovery described above. A refusal over a draft that
   is NOT this plan says nothing about this plan, so that draft is resolved and the upgrade is tried again
   rather than blamed. An exception thrown after the commit point and a refusal before it look identical from
   outside, and only the ledger tells them apart.
10. `Preview` writes nothing. It plans the first pending definition exactly and says which disposition an
    APPLY would record, because a definition the catalog already carries publishes no version at all and a
    preview that called that a plan left an operator to find out by running the apply. The step state is
    `WouldPublish` when the apply would publish a new version with the change lines the step holds, and
    `WouldAdopt` when the content is already present and the apply would write one adopted ledger row and
    publish nothing. `ContentUpgradeStepResult.WouldRecord` answers the same thing in the ledger's own
    vocabulary, so the preview and the row the apply writes read alike. The rest are listed as pending
    carrying the reason they did not run, because a later plan depends on the published result of an
    earlier one.

Every refusal carries a stable `KECU` code beside a message naming the catalog, the upgrade id and the next
action, and `ContentUpgradeReport.WriteTo` renders every line through `ContentBoot.LinePrefix`.
`ContentUpgradeReport.ExitCode` is 0 on a success and `ContentBootResult.ContentFailureExitCode` otherwise.

### The two draft proofs

`ContentUpgradeDraftMatch` holds both, and they authorise different acts.

| Act | Proof | What it asks |
|---|---|---|
| Publish a draft this attempt did not write whole | `IsPlan` | The draft holds EXACTLY one definition's change set: the same count, one edit per planned target under the same type, operation, definition id and key, and the same payload on each. |
| Discard a draft that is in the way | `IsKnownWork` | EVERY edit the draft holds is, by that same identity and payload comparison, an edit of some plan this run computed. |

The known set is every plan the run computed during the run plus a fresh replan of each still-pending
definition. One edit outside it means the draft may hold work an operator authored, and the draft is left
exactly as it stands under `OperatorDraftOpen`. A draft that is exactly the plan of a definition still
PENDING is not discarded either, because that is the shape a rival holds between its own write and its own
publish, and it is waited out. A frozen draft is never discarded at all. So the discard proof is strictly
weaker than the publish proof, and it destroys nothing an operator had authored **as of the read it was
computed over**.

It exists for one draft: the one two runners' writes merged into. A write into the draft is not atomic with
the read that found none, and a store's write APPENDS into whatever draft is open, so a rival that reached
its own write inside that window leaves the single draft holding two definitions' edits under one actor and
one note. It is nobody's plan, so nobody may publish it, and without the second proof nobody could discard it
either: both runners read it as the other's live work and stood off until their patience ran out.

### What is closed and what is only narrowed

Three windows sit between a run's reads and its writes. One is closed and two are not.

1. **Publish. CLOSED, by a freeze and a re-proof.** Every publish the runner performs calls `FreezeDraftAsync`
   with the version the plan was computed against, re-reads the open draft under that marker, and publishes
   only when it is exactly this definition's plan on that base version. While the marker stands the store
   refuses `ApplyEditsAsync` and `DiscardDraftAsync`, so what was proved is what is published. The rule for
   the marker is the whole of it: the runner freezes only a draft it has just read as exactly its own plan,
   and it releases the marker on every attempt that froze and did not publish, including a cancelled token
   and a fault of a type this package does not report on, because a run that stops for an operator must not
   hand back a draft they can neither edit nor discard. What it releases is not always what it set.
   `FreezeDraftAsync` OVERWRITES and carries no identity, so a publisher that froze the same draft in the gap
   between the runner's read and the runner's own freeze has already lost its marker to the runner's. A rival
   RUNNER loses nothing by that, because its own re-proof under its own freeze refuses a contaminated draft
   exactly as this one did. The admin console's publish takes whatever is drafted with no plan proof at all,
   and that is the third residue below.
2. **Discard. NARROWED, not closed.** `DiscardDraftAsync` is refused while a freeze stands, so the marker
   that closes the publish window is the one thing that cannot guard this one, and the proof stays check then
   act. It is as narrow as the seam allows: the run re-reads the draft and re-proves `IsKnownWork` over
   exactly what that read returned, with nothing awaited between the read and the discard.
3. **Apply. NARROWED, not closed.** The ledger is re-read for this definition immediately before the write,
   and a recorded id is adopted rather than written again. The `ContentDraft` the write RETURNS decides what
   happens next, not the edits that went in, so a draft that is not exactly this plan, or one that opened on
   a different base version, is never published. A draft the run may not publish is RESOLVED before it is
   judged, so only a draft the run cannot prove is classified as a rival's to wait out or an operator's to
   report.

Three residues remain. The first two are one store round trip wide.

- An operator edit landing between the known-work proof and the discard it authorised is lost.
- An operator edit on the SAME target under the SAME operation as one of the run's planned edits, written
  into the window where the run had seen no draft, is replaced by the run's own apply. A change set holds one
  pending intent per row, so the second write of a target takes the first one's place.
- A CONSOLE publish that froze the same draft in the gap between the runner's read and the runner's own
  freeze loses its marker. The runner reads the draft as exactly its own plan, an operator edit lands, the
  console's publish freezes the draft, the runner freezes over that marker, re-reads, fails its re-proof on
  the changed draft and releases what stands. A further operator edit on a target inside the console
  publish's own frozen edit set is then accepted, and that publish's commit deletes it unpublished.

They are accepted rather than closed because of where the runner runs. A hosted upgrade runs in a maintenance
window with editing AND console publishing stopped, and a local automatic boot has no operator at the
keyboard at all. **A game whose admin console writes under the same actor string as its upgrade runner
weakens every proof here**, because the actor is the first half of each of them, so the upgrade actor must be
dedicated to the runner and used by nothing else.

Clearing a draft this way is informational rather than a failure: `KECU0016` names the edit count and the
version, and the upgrade is tried again against the re-read baseline. The worst an interleaving costs is a
replan and a burnt version number.

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

The patience is bounded twice. A catalog that never moves gives up after a little over thirty seconds, and
the ceiling standing behind it is four of those budgets, so a rival that keeps making progress can hold one
definition for about two minutes before the run fails. That is the worst case per pending definition, and a
run with no rival pays none of it.

### Host integration

- **Local arm.** Open under `AutoCreate`. Seed and call `RecordBaselineAsync` when the report is `NoCatalog`,
  otherwise run `Apply`, then perform the strict load. A failed report stops the host with the report text and
  its exit code.
- **Hosted arm.** A dependent server never upgrades implicitly. A deploy step runs the game's upgrade command
  in `Preview` after opening with `ValidateOnlyWithoutTypeSync`, so pre-drain validation does not rewrite
  the running server's type settings. After draining, reopen with `ValidateOnly` and run `Apply` with
  `ExpectedVersion` set to the version it previewed, before the server starts. A server booting against a
  catalog with pending upgrades refuses with the pending ids and the command to run.
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
generated. Explicit shape guards refuse malformed members rather than returning a half-built bundle, with
the non-text string-decoding caveat below. Shape includes MAGNITUDE, because JSON has one number type and a member the reader
wants as an integer can be handed `1.5`, an id past `int.MaxValue` or `1e308`. Type ids must also fit their
1 to 65,535 domain, and field kinds, visibility values and remap kinds must be known enum values. Each bad
value is refused naming the member, the same as a missing one. The refusal's `JsonPath` is that member's
rooted path, as the table below lists.

`ContentBundleJson.ReadFormatVersion` is the precheck for a consumer that must inspect the declared format
before reading or combining a bundle. It uses the same JSONC policy and format member rules as `Read`, so both
accept comments and trailing commas and both refuse malformed JSON, a nonobject root, a missing version or a
version not written as a 32-bit integer literal. The precheck returns an unsupported integer so the caller can compare or
report it. `Read` remains the operation that refuses a format version this build does not support.

This build reads and writes two formats. `ContentBundle.CurrentFormatVersion`, 1, is ROW-ONLY and text free by
contract, and a format 1 document carrying a `text` member is refused. `ContentBundle.TextFormatVersion`, 2,
adds the protected `text` section, `ContentBundle.TextState`, of declared languages and complete values, and a
format 2 document without it is refused. Any other format is refused whole with `bundle-format` and
`BundleVersionUnsupported`. `ImportBundleAsync` is the row-only route and imports format 1 only.
[Catalog text](#catalog-text) covers the text import and the export format rule.

### Bundle document refusals

An unmapped lower constructor rule keeps the bundle carrier, its original prefix and diagnostic, and
the constructor exception as its inner cause. `BundleConstructorRuleUnmapped` has no identifiers.

The explicit shape guards in `ContentBundleJson` and its text-section reader carry the `bundle-format`
token in `Reason` and a `ContentAuthoringRefusal` in `Refusal`. Messages remain developer prose, so branch on
these. Non-text strings still use `JsonElement.GetString` directly, so its unpaired-surrogate decoding faults
can escape as `InvalidOperationException`. The `TextMalformedUnicode` guarantee covers the text section.
`JsonPath` is rooted at `$` with zero-based indices, such as `$.types[0].fields[1].kind` or
`$.text.values[0].typeId`. A member-shape guard reports its branches in its own order: an absent member is
`BundleMemberRequired`, and a present member of another JSON kind is `BundleMemberKindInvalid`. The JSON kind
a member takes is fixed by its path. An integer that does not fit carries only its path, never a guessed
value. Every array element must be an object. Elements are checked as they are visited, so an earlier
member refusal still precedes a malformed later element. A negative source version is wrapped at the bundle
constructor, with its original argument exception retained as inner.

| Refusal | Refused rule | Identifiers |
| --- | --- | --- |
| `BundleJsonMalformed` | Text that is not a JSON document | `JsonPath`, `LineNumber`, `BytePositionInLine` as the reader reports them, each null when it reports none. The reader's `JsonException` is the inner exception |
| `BundleConstructorRuleUnmapped` | A lower constructor rule without a mapped reader finding | None, original constructor exception retained as inner |
| `BundleRootObjectRequired` | A root that is not an object | `JsonPath` (`$`), `ActualJsonKind` |
| `BundleVersionUnsupported` | A format version other than 1 or 2, on read, write or import | `ActualFormatVersion`, `SupportedFormatVersions` (1 and 2, an owned read-only snapshot) |
| `BundleUnexpectedTextSection` | A format 1 document carrying `text` | `JsonPath` (`$.text`), `ActualFormatVersion` (1) |
| `BundleTextSectionMissing` | Writing a format 2 bundle that lost its text section | `ActualFormatVersion` (2) |
| `BundleMemberRequired` | A required member is absent | `JsonPath` |
| `BundleMemberKindInvalid` | A member has another JSON kind, or an array element is not an object | `JsonPath`, `ActualJsonKind` |
| `BundleInt32Unrepresentable` | A number not written as a 32-bit integer literal, `formatVersion` included. `1.0` and `1e2` are refused | `JsonPath` |
| `BundleInt64Unrepresentable` | A number not written as a 64-bit integer literal (`familyId`, a row's `number`). `1.0` and `1e2` are refused | `JsonPath` |
| `BundleTypeIdOutsideRange` | A type id outside 1 to 65,535 | `JsonPath`, `ActualInteger`, `MinimumInteger` (1), `MaximumInteger` (65,535) |
| `BundleFieldKindUnknown` | An undefined `ContentFieldKind` | `JsonPath`, `ActualInteger` |
| `BundleVisibilityUnknown` | An undefined `ContentVisibility` | `JsonPath`, `ActualInteger` |
| `BundleRemapKindUnknown` | An undefined `RemapRuleKind` | `JsonPath`, `ActualInteger` |
| `BundleHexMalformed` | A bytes or payload string that is not hex, by odd length or a non-hex character. Either letter case reads | `JsonPath` |

A value a lower constructor refuses (`ContentFieldSchema`, `ContentFamily`, `RemapRule` or
`ContentFieldValue`) carries `bundle-format` and the `Refusal` of the rule the constructor's own typed finding
names, never one read from its message. The message keeps the constructor's own words behind the reader's
prefix, and the constructor's argument exception is the inner exception.

| Refusal | Refused rule | Identifiers |
| --- | --- | --- |
| `BundleFieldNameBlank` | A schema field name that is empty or white space | `JsonPath` (`$.types[n].fields[n].name`) |
| `BundleFieldNameDuplicate` | A schema field name an earlier field of the type declares | `JsonPath` of the later name, `FieldName` |
| `BundleReferenceTargetMissing` | A key reference with no content type key | `JsonPath` (`$.types[n].fields[n].referenceTarget`), `FieldName` |
| `BundleTagReferenceTargetInvalid` | A tag list whose reference target is not `tag`, compared ordinally | `JsonPath` (`$.types[n].fields[n].referenceTarget`), `FieldName` |
| `BundleUnexpectedReferenceTarget` | A reference target on any other kind, an empty string included | `JsonPath` (`$.types[n].fields[n].referenceTarget`), `FieldName`, `ActualKind` |
| `BundleScaledIntegerScaleInvalid` | A scaled int scale below 1 | `JsonPath` (`$.types[n].fields[n].scale`), `FieldName`, `ActualInteger`, `MinimumInteger` (1) |
| `BundleUnscaledKindScaleInvalid` | Any other kind with a scale that is not 1 | `JsonPath` (`$.types[n].fields[n].scale`), `FieldName`, `ActualKind`, `ActualInteger`, `MinimumInteger` and `MaximumInteger` (1) |
| `BundleFamilyKeyBlank` | A family key that is empty or white space | `JsonPath` (`$.families[n].familyKey`) |
| `FamilyBlockSizeOutsideRange` | A family block size outside 16 to 65,536 | `JsonPath` (`$.families[n].blockSize`), `FamilyKey`, `ActualBlockSize`, `MinimumBlockSize` (16), `MaximumBlockSize` (65,536) |
| `FamilyBlockSizeNotPowerOfTwo` | A family block size in that range that is not a power of two | `JsonPath` (`$.families[n].blockSize`), `FamilyKey`, `ActualBlockSize` |
| `BundleFamilyBlockAlignmentInvalid` | A block base id that is not a multiple of the family's block size | `JsonPath` (`$.families[n].blocks[n].baseId`), the base id as `ActualInteger` |
| `BundleRuleIntegerNegative` | A rule `sequence`, `introducedIn`, `fromId` or `toId` below 0 | `JsonPath` of that member, `ActualInteger`, `MinimumInteger` (0) |
| `BundleRulePayloadMalformed` | A rule payload that is not its kind's shape, or longer than the payload cap | `JsonPath` (`$.rules[n].payload`), `RuleKind`, the payload length as `ActualBytes` |
| `BundleMarkerValueAuthored` | A row field of the byte-free `LocalizedTextKey` kind that carries bytes instead of being absent | `JsonPath` (`$.rows[n].fields[n].bytes`) |

The text section uses the same member-shape refusals, including a missing `text` object, and maps its
constructors' typed findings with the original argument exception as the inner. Direct text constructors
keep their plain `ArgumentException` contracts. `StrictText` refuses an unpaired surrogate escape before a
constructor sees it and keeps that reader fault as the inner exception.

| Refusal | Refused rule | Identifiers |
| --- | --- | --- |
| `BundleLanguageIdentityDuplicate` | A repeated canonical language identity | `JsonPath` (`$.text.languages[n].language`) of the later declaration, `Language` |
| `BundleLanguageTagInvalid` | A language or wire tag outside the ASCII grammar of 1 to 35 bytes | `JsonPath` of the tag, `LanguageTag` as submitted |
| `BundleLanguageWireTagMismatch` | A wire tag normalizing to another identity | `JsonPath` (`$.text.languages[n].wireTag`), canonical `Language`, submitted `WireTag` |
| `BundleTextLanguageUndeclared` | A value naming an undeclared canonical language | `JsonPath` (`$.text.values[n].language`), `Language` |
| `BundleTextTargetDuplicate` | A repeated canonical target | `JsonPath` (`$.text.values[n]`) of the later value, `TextTarget` with its type, key, field and language |
| `BundleTextTargetFieldBlank` | A target with an empty or white space marker field | `JsonPath` (`$.text.values[n].field`) |
| `BundleTextTargetKeyEmpty` | A target with an empty content key | `JsonPath` (`$.text.values[n].key`) |
| `TextMalformedUnicode` | An unpaired surrogate escape in a text-section string | `JsonPath` of the string |
| `TextValueBytesExceeded` | A value above 8,192 strict UTF-8 bytes | `JsonPath` (`$.text.values[n].value`), `ActualBytes`, `MaximumBytes` (8,192) |

A repeated wire spelling always repeats its normalized language identity, so it is refused as
`BundleLanguageIdentityDuplicate`. A repeated target is checked before an undeclared language, matching the
section constructor's guard order.

An import runs through the ORDINARY publish and there is no second mechanism. It restores the families and
their blocks verbatim, including each family's `IsRetired` flag through family reads and later exports in
every store. It restamps the bundle's rules as the new line's, turns every row into an `Add` edit and publishes
the draft as version 1. `ContentEdit.Import` is the only factory that may name a definition id, and
it is also the only one that may say a row is ALREADY retired, because a bundle carries its retired rows and
already carries the rule that retired them. A refusal at any point AFTER the staging began resets the store to
the empty state it was required to start from, so nothing is left half seeded, and that covers the staging
itself: a row naming a family the bundle does not declare is refused while the edits are being built, with the
families and the id marks already written. A refusal BEFORE the staging (a store that already published, a
store with no pack target) resets nothing, because it wrote nothing and the store it is protecting is live.

## Catalog text

`IContentTextAuthoringStore : IContentAuthoringStore` is the opt-in companion that authors per-language display
text and publishes it as `KECT` chunks a client reads through `ContentStringCatalog`. The in-memory, SQLite and
SQL Server stores implement it. A store that implements only `IContentAuthoringStore` keeps every row-only
behavior, and a caller holding text input for it refuses with `text-operation-unavailable` before anything is
applied. The backend is authoritative throughout: a caller's empty list or rebuilt DTO is never proof that text
is absent, and every member that consumes or destroys state compares it with what the store holds inside its
own gate or transaction.

### Targets, values and languages

A `ContentTextTarget(type, key, fieldName, language)` names one string by the row's content key, a declared
marker field and a language. The localization key is derived, `ContentTextKey.Derive(typeKey, contentKey,
fieldName)`, and there is no key override. The type must be registered and CLIENT visible, and the field a
CLIENT-visible `ContentFieldKind.LocalizedTextKey` marker, or the store refuses with `text-target-ineligible`.
The row must be live at the active version, retired rows included, or added or forked by the draft, this batch
included, or the store refuses with `unknown-row`.

`ContentTextEdit.Set(target, value)` carries a complete value, and `Set("")` is a PRESENT empty value.
`ContentTextEdit.Remove(target)` makes the string absent, so a reader falls back. Values are stored exactly as
submitted, with no trim, case fold or Unicode normalization, and are measured as strict UTF-8.

`ContentTextLanguageTag` is the one tag grammar: ASCII letters, digits and hyphens of 1 to 35 bytes, no empty
segment, and a first segment that starts with a letter. Letters are lowered invariantly, so `EN-us` and `en-US`
are the one identity `en-us`. It is not BCP-47 registry validation and consults no installed culture, so a
private tag such as `qz-123` is legal and an underscore is refused rather than converted.

Declarations are sticky. A Set introduces its language when neither the base version nor the draft declares
it, and the introduction survives a later Remove of the same string, so the language still publishes as an
empty chunk. A Remove in a declared language is idempotent, and a Remove in an undeclared one is refused with
`text-language-undeclared`. There is no language command. A published language keeps its historical wire
spelling in every later version through `ContentTextLanguageDeclaration`, and a new one uses the canonical
spelling.

| Limit | Bound | Where it is refused |
|---|---|---|
| Value | 8,192 strict UTF-8 bytes, no unpaired surrogate | `ContentTextEdit.Set` throws `ArgumentException`, and publication refuses with `text-bounds` |
| Derived key | 192 strict UTF-8 bytes | The store refuses at apply and publication refuses with `text-bounds` |
| Field name | 64 characters | The store refuses at apply with `text-bounds` |
| Language tag | 35 ASCII bytes under the grammar above | `ContentTextTarget` throws `ArgumentException`, and publication checks each wire tag with `text-bounds` |
| Language body | 16 MiB uncompressed, entry counts and length varints included | Publication refuses with `text-bounds` before any buffer is allocated |

### Applying

`ApplyChangesAsync` applies one `ContentAuthoringChanges` batch of row edits and text intents to the open draft,
opening one against the active version when none is open. Every intent lands in one transaction with its
audit, or none does, so an add and its display name are one call:

```csharp
var text = (IContentTextAuthoringStore)store;
var key = new ContentKey("iron_sword");

ContentDraft draft = await text.ApplyChangesAsync(
    new ContentAuthoringChanges(
        [ContentEdit.Add(itemType, key, fields)],
        [
            ContentTextEdit.Set(new ContentTextTarget(itemType, key, "name", "en"), "Iron Sword"),
            ContentTextEdit.Set(new ContentTextTarget(itemType, key, "name", "fr"), "Epee de fer"),
        ]),
    actor: "admin-endpoint",
    operatorId: "oid:8f2c",
    note: "autumn pass");
```

`ContentAuthoringChanges` owns copies of both lists, and two text intents naming one canonical target in one
batch are an `ArgumentException`. Across batches the last intent for a target wins and keeps its first
ordinal. A frozen draft refuses with `publish-in-progress`, as `ApplyEditsAsync` does.

`ContentDraft.TextState` is the draft's `ContentDraftTextState`, its ordered intents and its language
introductions. `TextEditCount`, `LanguageIntroductionCount` and `TotalWorkCount` count them beside `EditCount`.
A null `TextState` means a row-only route built the draft, which says nothing about text. A row-only
`ApplyEditsAsync` keeps the held text and introductions untouched.

### Publishing

`ContentPublishCommit.PublishAsync`, and each store's own `PublishAsync`, publish through the companion on a
store that implements it, row-only work included, so every version such a store commits is text complete.
`FreezeChangesAsync` freezes the complete draft and reads the baseline rows and text in one step, and a draft
whose `TotalWorkCount` is 0 is refused. The plan is prepared once with final row ids, a text-only draft
prepares with zero row edits, and every held value is rechecked against the final rows and registry. A Fork's
copy carries the source row's strings.

Each declared language becomes one `KECT` chunk through the existing codec, empty languages included, with
entries sorted ordinally by their UTF-8 key and a repeated derived key refused. A language whose values did not
change reuses its recorded hash and writes nothing. Both manifests name every output language in ordinal wire
tag order. The text chunks are written before the row chunks, the rule chunk and the manifests, and
`CommitTextPublishAsync` then commits rows, rules, chunks, text revisions, every language mapping, the audit,
the version and the draft consumption in ONE transaction, after confirming the epoch, the base, the frozen rows,
the text and the declarations against the store's actual state. A disagreement is `text-state-mismatch`, and a
plan whose chunk hash its values do not regenerate is `text-chunk-mismatch`.

Text is temporal like rows. `ContentTextRevision` carries a value with its valid-from and replaced-in versions,
so a translation-only publish never rewrites the row it names. Each changed string writes one audit entry
carrying `ContentAuditEntry.LanguageTag`, with values rendered through `ContentTextAuditRendering`, which
abbreviates a long value on a whole character and marks it `[cut]`. The full value stays in text history.

### Reading a version's text

`ReadTextSnapshotAsync(n)` returns one exact committed version's `ContentVersionTextSnapshot`: the store epoch,
every revision visible at that version, retired rows included, and every recorded `ContentTextLanguage` with
its wire spelling and chunk hash, empty languages included. Version 0 is not a shortcut for the active version
and is refused, like a version the store does not hold.

Schema 4 records every new version as text complete. A version from before the migration has no record and is
read as empty only on a read-only proof that its manifests named no language: the verified stored manifests at
its recorded hashes, or no-language manifests rebuilt from its recorded chunks that digest to those hashes.
Nothing records the proof. Without it the read, the freeze, a rollback and an upgrade baseline refuse with
`text-provenance-unknown`, because unknown text is never empty.

### Discarding

`TryDiscardChangesAsync(expected, actor, operatorId)` deletes the open draft only when it is EXACTLY `expected`:
the same base, freeze state, rows, text intents and introductions. The comparison and the delete share one gate
or transaction, so a rival translation, introduction or freeze returns false with nothing deleted and no
discard audited. A draft a row-only route built is never a proof and always returns false.

### Import, export and rollback

`ImportTextBundleAsync` imports a complete bundle into an EMPTY store, rows, ids, families, rules, declarations
and values together, as one complete version 1. Format 1 imports as empty text. A format 2 bundle that lost its
section and any later format are refused with `bundle-format`, the later format with `Refusal`
`BundleVersionUnsupported`, a value naming a row the bundle does not carry
with `unknown-row`, and an ineligible target with `text-target-ineligible`, all before anything is reset or
staged. An open draft holding any row, text or language work refuses the import with `draft-open` and stays as
it was. A later refusal restores the empty store. Every declared language keeps its wire spelling, empty ones
included.

`ExportBundleAsync(n)` writes format 1 when version N declares no language, so a text-free catalog's export
stays byte-identical, and format 2 with every declared language and value otherwise, even with no value. The
version's text must be complete or proved, or the export refuses.

`RollbackTextToAsync(target, actor, operatorId, note)` builds one draft restoring the rows and strings of an
earlier version. Every string of a row live at the target reads as it did there, retired rows included, by a
Set where it differs and a Remove where the target held none. Every currently declared language stays declared,
so a language the target never had publishes empty. A row retired since the target still blocks with
`KEC0039`, no text intent unretires a row, and both versions need complete or proved text.

A content upgrade can author text. `ContentUpgradePlan.Changes(ContentAuthoringChanges, changeLines)` carries row
edits and text intents as one change set, text-only included, and `ContentUpgradePlan.TextEdits` exposes them.
`ContentUpgradeContext.BaselineText` is the baseline's complete text, format 1 reading as empty. On a companion
store the runner reads the exact baseline text, applies and freezes a text plan through the companion and
discards a draft only through `TryDiscardChangesAsync`. A store without the companion refuses a text plan before
anything is written.

### Row-only routes on a companion store

The row-only members keep their signatures and refuse with `text-unrepresented` rather than drop text the store
holds:

- `FreezeDraftAsync` and `CommitPublishAsync` refuse while the draft holds text intents or introductions, a plan
  that is the row half of a text plan, a Fork of a row that holds text, and a plan whose languages are not the
  ones the active version recorded.
- `DiscardDraftAsync` refuses a draft holding text intents or introductions.
- `RollbackToAsync` refuses when the current or the target version holds a value or a language, or has no
  complete text record.
- `ImportBundleAsync` refuses a bundle carrying a text section or any format other than 1. It also refuses
  before anything is staged when the open draft holds text intents or introductions.
- `ContentPublishCommit.WriteAsync` refuses the row half of a text plan and a plan naming a text chunk the pack
  store does not already hold.

### Text route refusals

`ContentAuthoringException.Refusal` distinguishes route admission failures without parsing the message.
The legacy `Reason`, `Type` and `Id` values stay unchanged. A text target checked before id binding retains
`Id` 0. Exact-version export retains its legacy default `Type` and `Id` 0 and names the absent row through
`TextRowType` and `TextRowId` instead.

| Refusal | What it refuses | Identifiers |
|---|---|---|
| `BundleTextSectionMissing` | Reading or writing format 2 after its text section was dropped | `ActualFormatVersion` |
| `DraftOpen` | Companion import while an open draft holds work | `MemberName`, `RowEditCount`, `TextEditCount`, `LanguageIntroductionCount` |
| `BundleTextRowUnknown` | A text target naming no row the bundle carries | `Type`, `Id` 0, `RowKey`, `FieldName`, `Language`, zero-based value `Index` |
| `TextExportRowUnknown` | A text revision whose row is not live in the exported version | `TextRowType`, `TextRowId`, `FieldName`, `Language`, `VersionNumber`, `MemberName` |
| `TypeUnknown` | An unregistered text target type | `Type`, `Id` 0 |
| `TextTargetFieldUnknown` | An undeclared text marker field | `Type`, `Id` 0, `TypeKey`, `FieldName`, `Language` |
| `TextTargetIneligible` | A type or field without the required CLIENT localized marker declaration | `Type`, `Id` 0, `TypeKey`, `FieldName`, `Language` |
| `TextFieldNameTooLong` | A text marker name above 64 UTF-16 code units | `Type`, `Id` 0, `TypeKey`, `FieldName`, `Language`, `ActualCharacters`, inclusive `MaximumCharacters` (64) |
| `TextKeyBytesExceeded` | A derived text key above 192 strict UTF-8 bytes | `Type`, `Id` 0, `FieldName`, `Language`, `ActualBytes`, inclusive `MaximumBytes` (192) |
| `TextPlanOnRowOnlyRoute` | The marked row half of a text plan on a row-only commit | `MemberName`, `VersionNumber` |
| `TextBundleOnRowOnlyRoute` | A text-bearing bundle on a row-only import | `MemberName` |
| `TextFormatOnRowOnlyRoute` | A bundle without text declaring any format other than 1 on a row-only import | `MemberName`, `ActualFormatVersion` |
| `TextDraftOnRowOnlyRoute` | Pending text a row-only operation cannot carry | `MemberName`, `TextEditCount`, `LanguageIntroductionCount` |
| `TextBaselineHiddenFromPlanner` | An upgrade baseline whose carried text disagrees with the exact version | `VersionNumber`, `CarriedLanguageCount`, `CarriedValueCount`, `RecordedLanguageCount`, `RecordedValueCount` |
| `FrozenDraftLost` | An upgrade publish whose successfully frozen draft is no longer open | `ExpectedBaseVersion` |

The field-name and key-byte limits are inclusive. A target at the reported limit succeeds when that is
its only failed rule. Companion provenance reads keep `TextProvenanceUnknown` with `VersionNumber` when
the version cannot be proved text complete. Upgrade write and freeze routes are reached only after
planning confirms the companion exists.

### Reason tokens

| Reason | Meaning |
|---|---|
| `text-target-ineligible` | The type or field is not CLIENT visible, or the field is not a localized text marker |
| `text-language-undeclared` | A Remove names a language neither the base version nor the draft declares |
| `text-bounds` | A derived key, a value, a field name, a language tag or a language body exceeds its bound |
| `text-unrepresented` | A row-only route would have to carry text it cannot represent |
| `text-state-mismatch` | A text commit or discard found state other than its plan or proof names |
| `text-operation-unavailable` | Text input reached a store that implements only `IContentAuthoringStore` |
| `text-provenance-unknown` | A version has no complete text record and no read-only proof that it held none |
| `text-chunk-mismatch` | A plan's or a rebuild's chunk hash does not regenerate from its values |
| `draft-open` | A text import found an open draft holding work |
| `bundle-format` | A bundle declares an unread format, or format 2 lost its text section |

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
