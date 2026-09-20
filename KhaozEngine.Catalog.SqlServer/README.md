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

## Testing this backend

The engine's provider conformance suite and its publish crash-safety facts run against this backend only when
the environment variable `KE_CATALOG_SQLSERVER` holds a reachable connection string. CI has no SQL Server, so
every one of them SKIPS there and the same suite's SQLite run is what gates a push. Point the variable at a
throwaway database before trusting a change to this package.

For a dev, test or single-node deployment use `KhaozEngine.Catalog.Sqlite` against the same
`IContentAuthoringStore` contract.
