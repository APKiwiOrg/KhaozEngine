# KhaozEngine.Catalog.Sqlite

SQLite `IContentAuthoringStore` backend over `Microsoft.Data.Sqlite`. The embedded, zero-infra dev, test and
single-node authoring store for the content catalog. Opt-in: it is in no umbrella, and a game client never
links it, because the read side is `KhaozEngine.Catalog` and takes no database dependency at all.

```csharp
using KhaozEngine.Catalog.Sqlite;

using var store = new SqliteContentAuthoringStore(
    "Data Source=catalog.db", registry, packStore);

await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

await store.ApplyEditsAsync(
    [ContentEdit.Add(itemType, new ContentKey("iron_sword"), fields)],
    actor: "admin-endpoint",
    operatorId: "oid:8f2c",
    note: "autumn pass");

ContentPublishResult published = await store.PublishAsync(
    new ContentPublishRequest("admin-endpoint", "oid:8f2c", "autumn pass", expectedBaseVersion: 0));
```

The constructor opens the database and runs the bootstrap pragma. It does NOT touch the schema: that is
`InitializeAsync`, because whether an empty database may be created into is the caller's decision.

## The schema, and its two modes

Fifteen tables: `catalog_metadata`, `catalog_type`, `catalog_version`, `catalog_row`, `catalog_row_field`,
`catalog_family`, `catalog_family_block`, `catalog_id_high_water`, `catalog_draft`, `catalog_draft_edit`,
`catalog_draft_edit_field`, `catalog_audit`, `catalog_remap_rule`, `catalog_chunk` and
`catalog_content_upgrade`. Every key column is
`TEXT COLLATE BINARY`, because content keys compare ordinally and never case insensitively. Every size cap is
a `CHECK`. Every foreign key is declared and the bootstrap turns foreign key enforcement on, so a row can
never point at a version that does not exist.

`ContentAuthoringSchemaMode.AutoCreate` creates the schema when the database is empty and then validates it.
`ContentAuthoringSchemaMode.ValidateOnly` refuses an empty or mismatched database rather than creating
anything, which is what a production host sets so a typo in a connection string cannot silently create a
second empty catalog and serve it. A mismatch throws `ContentAuthoringException` with reason
`schema-mismatch`, naming the object and the migration `catalog-v2-content-upgrade-ledger`.

Schema version 2 adds `catalog_content_upgrade`, the content upgrade ledger behind `IContentUpgradeLedger`.
A version 1 file opened under `AutoCreate` is MIGRATED in place, in one transaction that adds that one table
and changes nothing else, so the rows, the history, the audit, the open draft, the pin and the store epoch all
survive it unchanged. Under `ValidateOnly` a version 1 file is refused instead, naming the migration. An
applied ledger row is written inside the publish commit, so a duplicate upgrade id refuses the whole publish.

`InitializeAsync` also writes the registry's types into `catalog_type`, which pins each type id to its key. A
rename and a reassignment are both refused, because either one repoints every row already stored under the
old pairing.

## What the tables hold, and what they do not

A row is TEMPORAL: `catalog_row` carries one row per row VERSION, with `valid_from_version` and a nullable
`replaced_in_version`, so "what did item 12 look like at version 40" is a query rather than an audit replay.
Its values live one per field in `catalog_row_field`, which is what makes the audit and the diff field level.

The encoded row blob is NOT stored. It is computed at publish through the type's codec, and the only thing
persisted about it is the chunk hash in `catalog_chunk`, one row per side, because a type with a server-only
field encodes two different runs of bytes for one id range.

`catalog_remap_rule` is append only. There is no `UPDATE` and no `DELETE` for it anywhere in this package.

## Publishing

A publish writes every pack file first, at content-addressed names nothing references yet, and then commits
ONE explicit transaction: the version row, every row close and insert, every appended rule, every chunk row,
every audit row, the draft delete, and the active pointer LAST. A crash at any moment leaves either the old
version or the new one and never a torn one.

Because the store leases its connection per call rather than holding a lock across the whole publish, the
transaction RE-READS the highest published version and refuses a plan whose base moved underneath it, with
reason `base-version-moved`. Two consoles cannot both publish the same draft.

What the re-read does NOT cover is the draft changing under a plan that already read it, so step 1 marks it
frozen in `catalog_draft.frozen_for_base_version` and `ApplyEditsAsync` and `DiscardDraftAsync` refuse while
the marker stands, with reason `publish-in-progress`. It is a durable column rather than the row lock spec
6.2 describes for the same reason the version is re-read: no lock this provider can take spans steps 1 to 10,
because step 9 writes the whole pack outside any lease. The publish clears the marker on every exit path, and
a marker naming a version the database has moved past is a dead publish's leftover that the next baseline
read clears. The draft delete at step 10 is scoped to the edits step 1 froze.

## Ids

`catalog_id_high_water` carries `reserved_through` and `issued_through` per type. A range is reserved durably
BEFORE any id in it is issued, in its own transaction, so a crash can skip ids that were never issued and can
never reissue one. Reserving a family block advances the type's issued mark to the block top in the same
transaction as the block insert, which is what keeps the plain counter from walking under the block.

## Lifecycle

The connection, the operation gate and the dispose are `KhaozEngine.Sqlite`'s `SqliteStoreConnection`, shared
with every other SQLite store in the engine: one held connection that is never pooled, commands serialized
behind a lease, and a dispose that closes it so the database file is genuinely released.
Dispose the store when you are done with it.

The lease is not re-entrant, so no member holds one across a call back into the store. `Data Source=:memory:`
works and keeps its data for the life of the store, which is what the tests use.

For a production or shared-authoring deployment use `KhaozEngine.Catalog.SqlServer` against the same
`IContentAuthoringStore` contract.
