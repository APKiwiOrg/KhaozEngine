# KhaozEngine.Catalog.SqlServer

SQL Server and Azure SQL `IContentAuthoringStore` backend over `Microsoft.Data.SqlClient`. The production and
shared-authoring store for the content catalog. Opt-in: it is in no umbrella, and a game client never links it,
because the read side is `KhaozEngine.Catalog` and takes no database dependency at all.

```csharp
using KhaozEngine.Catalog.SqlServer;

var store = new SqlServerContentAuthoringStore(
    "Server=.;Database=catalog;Integrated Security=true;TrustServerCertificate=true",
    registry,
    packStore);

await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

await store.ApplyEditsAsync(
    [ContentEdit.Add(itemType, new ContentKey("iron_sword"), fields)],
    actor: "admin-endpoint",
    operatorId: "oid:8f2c",
    note: "autumn pass");

ContentPublishResult published = await store.PublishAsync(
    new ContentPublishRequest("admin-endpoint", "oid:8f2c", "autumn pass", expectedBaseVersion: 0));
```

The constructor opens nothing and touches no schema: that is `InitializeAsync`, because whether a database
carrying no catalog table may be created into is the caller's decision. There is nothing to dispose, because
the store holds no connection between calls.

## The schema, and its two modes

Fifteen tables: `catalog_metadata`, `catalog_type`, `catalog_version`, `catalog_row`, `catalog_row_field`,
`catalog_family`, `catalog_family_block`, `catalog_id_high_water`, `catalog_draft`, `catalog_draft_edit`,
`catalog_draft_edit_field`, `catalog_audit`, `catalog_remap_rule`, `catalog_chunk` and
`catalog_content_upgrade`, shipped as the embedded resource `CatalogSchemaV2.sql`. The first fourteen are
version 1 and the ledger is what version 2 adds. Every key column is
`nvarchar(N) COLLATE Latin1_General_100_BIN2`, because
content keys compare ordinally and never case insensitively, and a SQL Server database default usually is case
insensitive. Every size cap is a `CHECK`, `LEN` for text and `DATALENGTH` for binary, and every foreign key is
declared, so a row can never point at a version that does not exist.

**Every constraint is named**, `ck_<table>_<what>`, `pk_<table>`, `fk_<table>_<what>`, `df_<table>_<column>`.
Validation compares NAMES against `sys.tables`, `sys.indexes`, `sys.check_constraints`, `sys.foreign_keys`
and `sys.default_constraints`, so an unnamed constraint (SQL Server would generate a per-database name for it)
could not be verified at all.

**Every parameter carries its column's SQL type.** A write binds through the binder named for the column
(`BindInt`, `BindBigInt`, `BindText`, `BindLargeText`, `BindBlob`, `BindTime`) rather than letting SqlClient
read a type off the value, because a null value has no type to read and SqlClient falls back to `nvarchar` for
it. SQL Server refuses nvarchar into `varbinary(max)` outright, so an absent field payload could not be written
at all, and the conversions it does allow are implicit and silent. `SqlServerCatalogParameterTypeTests` cross
checks every binding in the provider against the embedded DDL, with no instance needed.

`ContentAuthoringSchemaMode.AutoCreate` creates the schema when the database carries no catalog table and then
validates it, under an exclusive application lock inside one transaction, so two hosts starting at once do not
race. `ContentAuthoringSchemaMode.ValidateOnly` refuses an empty or mismatched database rather than creating
anything, which is what a production host sets so a typo in a connection string cannot silently create a second
empty catalog and serve it. A mismatch throws `ContentAuthoringException` with reason `schema-mismatch`, naming
the object and the migration `catalog-v2-content-upgrade-ledger`.

Schema version 2 adds `catalog_content_upgrade`, the content upgrade ledger behind `IContentUpgradeLedger`.
`CatalogSchemaV2.sql` is what a fresh create runs, so a new database is version 2 directly, and
`CatalogSchemaV1.sql` ships beside it as the operator's record of the shape the migration moves. A version 1
database opened under `AutoCreate` is migrated behind the same application lock the create takes, in one
transaction that adds that one table and changes nothing else. Under `ValidateOnly` it is refused instead,
naming the migration. An applied ledger row is written inside the publish commit, so a duplicate upgrade id
refuses the whole publish.

`InitializeAsync` also writes the registry's types into `catalog_type`, which pins each type id to its key. A
rename and a reassignment are both refused, because either one repoints every row already stored under the old
pairing.

## Replacing the catalog: `SqlServerCatalogReset`

A game that ships its client content pack inside the client build cuts that pack from a fresh store, so the
pack is always content version 1. The connect door compares the version NUMBER as well as the manifest hash,
and the number is inside the hashed manifest, so such a game cannot publish its server store forward to
version 2. It has to REPLACE the store from its committed bundle on every content release, and
`ImportBundleAsync` refuses a store that has published anything. `SqlServerCatalogReset` is the way back to an
importable store.

```csharp
ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
    migrationConnectionString,
    actor: "release-runner",
    operatorId: "oid:8f2c",
    note: "autumn pass",
    force: false);

Console.WriteLine(reset.Summary);          // one operator line, also filed in the new catalog_audit

var store = new SqlServerContentAuthoringStore(connectionString, registry, packStore);
await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
await store.ImportBundleAsync(bundle, "release-runner", "oid:8f2c", "autumn pass");   // version 1 again
```

It drops every catalog object and recreates the schema from the same embedded DDL the initializer creates
from, in ONE transaction. Either the catalog is replaced or it is exactly as it stood, which matters because
a half-dropped catalog refuses the next open outright: the initializer creates only when it counts zero
catalog tables and validates every object by name otherwise.

**The drop names the schema's own INVENTORY, not a name pattern.** It is the same set
`SqlServerCatalogSchemaDriftTests` pins against `CatalogSchemaV2.sql`, intersected with what `sys.tables`
holds, so a table added to the schema is dropped without anyone having to remember it here and a table this
build does not declare is never touched. A pattern could not do that job: a host table named
`catalog_overrides_by_host` matches every name rule an engine could write while belonging to nobody here. Keep
whatever tables you like in the same database. The reset destroys exactly the tables the schema declares. A
host table whose name really starts with `catalog_` survives a reset like any other, but the store's own open
refuses it as an object the schema does not declare, so keep host tables outside that prefix.

**A host foreign key INTO the catalog fails the reset.** SQL Server refuses to drop a table any foreign key
references, whatever the key's delete action, so the reset fails with SQL error 3726 and rolls back as a whole,
leaving the catalog and the host's rows exactly as they stood. The SQLite reset refuses the same host shape
before it drops anything, because there a drop would fire the key's action. Keep host references to the
catalog out of foreign keys.

Drop and recreate rather than `DELETE`, because a delete leaves the `IDENTITY` marks on `catalog_family`,
`catalog_draft_edit` and `catalog_audit` where they stood, and the next family created after a reimport would
land above the bundle's ids.

It is a separate type taking its own connection string rather than a member on `IContentAuthoringStore`,
because a reset is DDL and the everyday authoring path is DML. A production application login should not hold
DDL at all, so the reset runs under the migration credential and the authoring seam keeps the surface it had.

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
still dropped, and the summary says so. A catalog whose schema version cannot be read, because its metadata
row is gone, is the same case whatever stands, every table included: refused under `catalog-partial` without
`force` and repaired with it.

**The schema version decides the rest, whenever it can be read.** A catalog at an OLDER schema version than
this build writes is reset like any other and comes back at this build's version, because the recreate runs
this build's script: a whole version 1 catalog, without the `catalog_content_upgrade` table version 2 added,
is a whole catalog rather than a partial one, and needs no `force`. A catalog at a NEWER schema version is
refused with `schema-mismatch` before anything is dropped, force or no force, because recreating an older
schema over it would move the database backwards. The refusal names the remedy, which is a reset from a build
that writes that version. The result carries both numbers, `PriorSchemaVersion` and `SchemaVersion`, and
`reset.Summary` says both.

`actor`, `operatorId` and `note` are checked against the caps `dbo.catalog_audit` declares (1 to 128, 128 and
1024 characters) BEFORE the transaction opens, and an argument outside them is an `ArgumentException` with
nothing dropped. The insert that would otherwise catch it is the reset's LAST statement. The lengths are
measured the way `LEN` measures them, which ignores trailing spaces.

**The store epoch is NEW.** The recreate mints one, the same as a first create, and that is deliberate: a
reset store shares no history with the one it replaced, so a durable page stamped against the old epoch must
not be taken for a page of this one.

**The reset writes one audit row and it is the first row in the new store.** `catalog_audit` is dropped with
everything else, so the reset cannot record itself in the old one. The table carries no foreign key to
`catalog_version` and defaults every target column, so a row naming no version is a shape the schema already
accepts. It holds the action `reset`, the actor, the operator, the note, and `reset.Summary` in
`before_value`.

**The pack store is NOT touched.** The reset knows nothing about a pack root, and a pack root left
standing under a replaced catalog still holds a `versions/<n>` pointer naming the manifest of the content that
was there before. A caller that replaces content at the same version number must therefore CLEAR its pack
root or REBUILD it. The import that follows a reset overwrites the pointer only in the pack store it was handed,
so any other root a server boots from keeps the old one. `ContentPackRebuild.RunAsync` writes one published
version's whole pack out of the store's own rows and rules, verified against the manifest digests the version
row records. Do not leave that to the boot to notice.
`ContentCatalogResetResult` carries the ACTIVE version's server and client manifest hashes, which is the last moment
they can be read, so a caller can tell an old pack root from a new one. A store pinned below its active version
served the pin, which the result does not report.

## What the tables hold, and what they do not

A row is TEMPORAL: `catalog_row` carries one row per row VERSION, with `valid_from_version` and a nullable
`replaced_in_version`, so "what did item 12 look like at version 40" is a query rather than an audit replay.
Its values live one per field in `catalog_row_field`, which is what makes the audit and the diff field level.

The encoded row blob is NOT stored. It is computed at publish through the type's codec, and the only thing
persisted about it is the chunk hash in `catalog_chunk`, one row per side, because a type with a server-only
field encodes two different runs of bytes for one id range.

`catalog_remap_rule` is append only. There is no `UPDATE` and no `DELETE` for it anywhere in this package, and
no delete-guard trigger standing in for one: that guard exists to permit a retention sweep and there is no
retention sweep here.

## Concurrency, and what a contended write looks like

A fresh pooled `SqlConnection` per call, no in-process semaphore. The SQLite backend serializes in process
behind one held connection, which is correct for the single-node case it exists for. This backend exists for
the shared case, where the second console is in another process, so the database does the serializing instead:
every write runs in an `IsolationLevel.Serializable` transaction.

A publish writes every pack file first, at content-addressed names nothing references yet, and then commits ONE
transaction: the version row, every row close and insert, every appended rule, every chunk row, every audit
row, the draft delete, and the active pointer LAST. A crash at any moment leaves either the old version or the
new one and never a torn one. Inside that transaction the commit RE-READS the highest published version and
refuses a plan whose base moved, with reason `base-version-moved`.

Serializable covers step 10 alone, so what holds the DRAFT across steps 1 to 10 is a durable marker instead:
step 1 writes `catalog_draft.frozen_for_base_version`, and `ApplyEditsAsync` and `DiscardDraftAsync` refuse
while it stands, with reason `publish-in-progress`. The publish clears it on every exit path, a marker naming
a version the database has moved past is a dead publish's leftover that the next baseline read clears, and
the draft delete at step 10 is scoped to the edits step 1 froze.

**A serialization failure carries that same reason.** SQL error 1205 (deadlock victim) and 3960 (snapshot
update conflict) both mean what a moved base version means to a caller: the plan was built over a state that is
no longer there, nothing was written, and the remedy is to re-read the baseline and prepare again. A separate
reason token would make one condition two things for a console to handle.

## Ids

`catalog_id_high_water` carries `reserved_through` and `issued_through` per type. A range is reserved durably
BEFORE any id in it is issued, in its own transaction, so a crash can skip ids that were never issued and can
never reissue one. Reserving a family block advances the type's issued mark to the block top in the same
transaction as the block insert, which is what keeps the plain counter from walking under the block.

`catalog_family.family_id` and `catalog_draft_edit.edit_ordinal` are `IDENTITY(1,1)`. The only place identity is
turned off is the family restore inside a bundle import, which keeps the bundle's own family ids so a row's
family membership survives the import.

## Permissions

`AutoCreate` needs DDL rights plus `EXECUTE` on `sys.sp_getapplock`. `ValidateOnly` needs only `SELECT` on the
`sys` catalog views plus the ordinary read and write rights on the fifteen tables, which is what a production
application login should have.

`SqlServerCatalogReset.ResetAsync` needs DDL rights plus `EXECUTE` on both `sys.sp_executesql` and
`sys.sp_getapplock`, so give it the migration credential rather than the application login. It takes the SAME
exclusive application lock the schema create takes, on the same resource name, as the first statement of its
transaction, and so does the version 1 migration. A lock one side holds and the other does not is not a lock:
without it a reset could drop every catalog table while a starting host was half way through creating or
migrating them. The schema modification locks each
statement takes for itself do not cover that, because they serialize one statement at a time and not the
sequence.

## Testing this backend

The engine's provider conformance suite and its publish crash-safety facts run against this backend only when
the environment variable `KE_CATALOG_SQLSERVER` holds a reachable connection string. CI has no SQL Server, so
every one of them SKIPS there and the same suite's SQLite run is what gates a push. Point the variable at a
throwaway database before trusting a change to this package.

For a dev, test or single-node deployment use `KhaozEngine.Catalog.Sqlite` against the same
`IContentAuthoringStore` contract.
