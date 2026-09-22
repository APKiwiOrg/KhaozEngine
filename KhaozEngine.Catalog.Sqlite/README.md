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

## Replacing the catalog: `SqliteCatalogReset`

A game that ships its client content pack inside the client build cuts that pack from a fresh store, so the
pack is always content version 1. The connect door compares the version NUMBER as well as the manifest hash,
and the number is inside the hashed manifest, so such a game cannot publish its server store forward to
version 2. It has to REPLACE the store from its committed bundle on every content release, and
`ImportBundleAsync` refuses a store that has published anything. `SqliteCatalogReset` is the way back to an
importable store.

```csharp
ContentCatalogResetResult reset = await SqliteCatalogReset.ResetAsync(
    "Data Source=catalog.db",
    actor: "release-runner",
    operatorId: "oid:8f2c",
    note: "autumn pass",
    force: false);

Console.WriteLine(reset.Summary);          // one operator line, also filed in the new catalog_audit

using var store = new SqliteContentAuthoringStore("Data Source=catalog.db", registry, packStore);
await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
await store.ImportBundleAsync(bundle, "release-runner", "oid:8f2c", "autumn pass");   // version 1 again
```

It drops every catalog object and recreates the schema from the same DDL the initializer creates from, in ONE
transaction. Either the catalog is replaced or it is exactly as it stood, which matters because a half-dropped
catalog refuses the next open outright: the initializer creates only when it counts zero catalog tables and
validates every object by name otherwise.

**The drop names the schema's own INVENTORY, not a name pattern.** The inventory is derived by running the
same DDL into a throwaway in-memory database and reading the table names back, so a table added to the schema
is dropped without anyone having to remember it here, and a table this build does not declare is never
touched. A pattern could not do that job: in SQLite's `LIKE` an underscore matches any single character, so
`catalog_%` also matches a host's own `catalogs` and `cataloguer`, and escaping the underscore still leaves a
host table genuinely named `catalog_overrides_by_host` indistinguishable from an engine table. Keep whatever
tables you like in the same file. The reset destroys exactly the tables the schema declares. A host table
whose name really starts with `catalog_` survives a reset like any other, but the store's own open refuses it
as an object the schema does not declare, so keep host tables outside that prefix.

**A host foreign key INTO the catalog is read before anything is dropped.** With foreign keys on, SQLite's
`DROP TABLE` deletes every row of the table before it drops it, and that hidden delete fires the delete action
of every key pointing at the table. Deferring the foreign key check does not stop an action from firing, and
an action is not a violation the commit would refuse. So a host key declared `ON DELETE CASCADE`, `SET NULL`
or `SET DEFAULT` would delete or rewrite the host's own rows, and the reset refuses it with reason
`host-foreign-key` and a sentence naming the host table and the catalog table it references, before it drops
anything and whatever `force` says. A `NO ACTION` or `RESTRICT` key fires nothing. A host row still
referencing the catalog fails the reset at its commit instead, and the whole reset rolls back. Keep host
references to the catalog out of foreign keys, or declare them `NO ACTION` and clear the referencing rows
before a reset.

Drop and recreate rather than `DELETE`, because a delete leaves the `sqlite_sequence` marks behind the
`AUTOINCREMENT` columns on `catalog_family`, `catalog_draft_edit` and `catalog_audit` where they stood, and
the next family created after a reimport would land above the bundle's ids. Dropping a table takes its
`sqlite_sequence` row with it.

It is a separate type taking its own connection string rather than a member on `IContentAuthoringStore`,
because a reset is DDL and the everyday authoring path is DML. A production deployment should not give its
application role DDL at all, so the reset runs under the migration credential and the authoring seam keeps the
surface it had. It opens its own connection, so the connection string has to name a durable database: a plain
`Data Source=:memory:` database belongs to the connection that opened it, so the reset would open a second
empty one, create a schema into it and throw both away. Under `Cache=Shared` an in-memory database is shared
by NAME for as long as one connection to it stays open, and a reset does reach the store its holder is using.

An open draft is refused with reason `draft-open`, because the reset would destroy unpublished authoring with
nothing left afterwards that says what it held. Pass `force: true` to take it anyway.

**A database carrying NONE of the schema's tables is not an error.** The reset creates the schema through the
same script, in the same transaction, with the same audit row, and returns a result saying nothing stood. The
scripted release path is reset then import, so the first release against a new database takes that branch
rather than being refused for a migration that exists only as this script.

**A database carrying SOME of them is a half-finished deletion**, which no store can open and no read can
describe. Without `force` it is refused with reason `catalog-partial` and a sentence naming the remedy. With
`force` the reset drops what is left, recreates the schema and returns a result whose `PriorState` is
`Unreadable`, because there is nothing truthful it can put in the version and the hashes. What stood is
still dropped, and the summary says so.

**The schema version decides the rest, whenever it can be read.** A catalog at an OLDER schema version than
this build writes is reset like any other and comes back at this build's version, because the recreate runs
this build's script: a whole version 1 catalog, without the `catalog_content_upgrade` table version 2 added,
is a whole catalog rather than a partial one, and needs no `force`. A catalog at a NEWER schema version is
refused with `schema-mismatch` before anything is dropped, force or no force, because recreating an older
schema over it would move the database backwards. The refusal names the remedy, which is a reset from a build
that writes that version. The result carries both numbers, `PriorSchemaVersion` and `SchemaVersion`, and
`reset.Summary` says both.

`actor`, `operatorId` and `note` are checked against the caps `catalog_audit` declares (1 to 128, 128 and
1024 characters) BEFORE the transaction opens, and an argument outside them is an `ArgumentException` with
nothing dropped. The insert that would otherwise catch it is the reset's LAST statement.

**The store epoch is NEW.** The recreate mints one, the same as a first create, and that is deliberate: a
reset store shares no history with the one it replaced, so a durable page stamped against the old epoch must
not be taken for a page of this one.

**The reset writes one audit row and it is the first row in the new store.** `catalog_audit` is dropped with
everything else, so the reset cannot record itself in the old one. The table carries no foreign key to
`catalog_version` and defaults every target column, so a row naming no version is a shape the schema already
accepts. It holds the action `reset`, the actor, the operator, the note, and `reset.Summary` in
`before_value`.

**One writer at a time.** SQLite takes one writer per database file and the reset is a writer that wants
every table in it, so a reset against a file another connection is writing to waits for the busy timeout the
connection string names and then fails with the provider's busy error, having changed nothing. There is no
queue and no partial outcome. The remedy is an operator one, stop the writers, so set a SHORT
`Default Timeout` on the reset's own connection string rather than letting a release runner block on the
provider's default.

**The pack store on disk is NOT touched.** The reset knows nothing about a pack root, and a pack root left
standing under a replaced catalog still holds a `versions/<n>` pointer naming the manifest of the content that
was there before. A caller that replaces content at the same version number must therefore CLEAR its pack
root or REBUILD it. The import that follows a reset overwrites the pointer only in the pack store it was handed,
so any other root a server boots from keeps the old one. `ContentPackRebuild.RunAsync` writes one published
version's whole pack out of the store's own rows and rules, verified against the manifest digests the version
row records. Do not leave that to the boot to notice.
`ContentCatalogResetResult` carries the server and client manifest hashes that STOOD, which is the last moment
they can be read, so a caller can tell an old pack root from a new one.

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
with every other SQLite store in the engine: one held connection, commands serialized behind a lease, and a
dispose that clears the provider's connection pool before closing so the database file is genuinely released.
Dispose the store when you are done with it.

The lease is not re-entrant, so no member holds one across a call back into the store. `Data Source=:memory:`
works and keeps its data for the life of the store, which is what the tests use.

For a production or shared-authoring deployment use `KhaozEngine.Catalog.SqlServer` against the same
`IContentAuthoringStore` contract.
