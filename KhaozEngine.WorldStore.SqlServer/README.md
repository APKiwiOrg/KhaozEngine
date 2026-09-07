# KhaozEngine.WorldStore.SqlServer

SQL Server / Azure SQL `IWorldStore` backend over `Microsoft.Data.SqlClient`. The production durable store for an
authoritative world; identical `IWorldStore` contract to the SQLite dev/test backend.

```csharp
using KhaozEngine.WorldStore.SqlServer;

IWorldStore store = new SqlServerWorldStore(
    "Server=tcp:my.database.windows.net,1433;Database=ruinborne;Authentication=Active Directory Default;Encrypt=True;");
await store.SaveAsync("player:42", bytes);
byte[]? loaded = await store.LoadAsync("player:42");
```

One `world_store([key], data, updated_at)` table, bootstrapped on construction; upsert via
`MERGE ... WITH (HOLDLOCK)`; raw parameterized async ADO.NET (no EF/ORM); a short-lived pooled connection per
operation. For dev/test use `KhaozEngine.WorldStore.Sqlite` against the same contract.

`SqlServerWorldStore` implements **`IEnumerableWorldStore`** (since 8.4.2): `EnumerateAsync(keyPrefix?)` streams
`WorldStoreEntry { Key, UpdatedAt, Size? }` records via a streaming SQL Server cursor, optionally filtered by key
prefix. Used by `ServerAdmin` for account enumeration and ban persistence.

`SqlServerWorldStore` also overrides **`SaveManyAsync`**: it opens ONE pooled connection for the whole batch
(instead of one per record) and upserts every item via a multi-row `MERGE ... USING (VALUES ...)` statement inside
a single transaction, chunked at 500 rows per statement to stay well under SQL Server's 2100-parameter-per-statement
ceiling. A batch larger than the chunk size issues multiple `MERGE` statements on the same connection and
transaction, so it is still one connection's worth of setup instead of one per record.

## Mutation journal

`SqlServerMutationJournalStore` implements `IMutationJournalStore`, `IMutationJournalMaintenance`, and the additive
`IMutationJournalAgeMaintenance` capability for durable player mutations. It uses serializable SQL transactions,
binary collations for stream and section identity, checksummed event and snapshot payloads, replay receipts,
projection cursors, compaction, operation retention, and store epoch rotation.

```csharp
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;

var journal = new SqlServerMutationJournalStore(
    new SqlServerMutationJournalStoreOptions(connectionString)
    {
        SchemaMode = SqlServerJournalSchemaMode.ValidateOnly,
        CommandTimeout = TimeSpan.FromSeconds(30),
        MinimumRetryHorizon = TimeSpan.FromHours(24),
        Limits = JournalLimits.Maximum,
    });
```

`AutoCreate` creates version two when no journal objects exist. It also migrates a valid version-one journal by
adding a database-owned operation-retention timestamp. Existing replay rows start a fresh retention horizon at
migration time, so upgrade cannot expire one early. A database default also stamps inserts from an already-running
version-one writer during rollout. A database trigger rejects operation deletion unless current maintenance opens
its transaction-local guard. A still-running version-one maintenance host therefore rolls back both its child and
parent deletes after migration. Restarted hosts must support version two. `ValidateOnly` performs no DDL and is the production mode when
the application principal does not have schema permissions. A partial, malformed, older, or newer journal schema
fails with `SchemaMismatch` and names the required migration.

The package embeds `JournalSchemaV2.sql` for fresh deployments and retains `JournalSchemaV1.sql` for controlled
upgrade tooling and compatibility tests. Deployments apply the version-two script before starting a validate-only
host. Initialization is serialized with a transaction-owned SQL application lock. Normal writes share a
maintenance gate, while compaction, replay retention, and epoch rotation take the exclusive side.

Use a migration identity with schema DDL rights to create version two or run one `AutoCreate` boot over version one.
A `ValidateOnly` runtime identity
does not need DDL. It needs database connect, catalog visibility for schema validation, membership in the `public`
fixed database role used by `sys.sp_getapplock`, and `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on every
`dbo.journal_*` table. Grant only the game host and controlled operators access to journal data. Prefer a managed
identity or a secret from the deployment secret store. Do not commit connection strings or print them in logs.

`CommandTimeout` applies to commands and schema locking. `MinimumRetryHorizon` prevents maintenance from deleting
replay rows which may still be retried. `Limits` can lower any core journal maximum. `TimeProvider` controls public
journal timestamps for deterministic hosts and tests. It does not control `PurgeOperationsByAgeAsync`, whose
retention timestamps and cutoff come from SQL Server. The older cutoff purge is also clipped to SQL Server UTC
minus `MinimumRetryHorizon`. Each operation uses a short-lived pooled SQL connection.

Back up every journal table as one unit. After a point-in-time restore, stop all journal hosts, rotate the epoch with
`IMutationJournalMaintenance.RotateStoreEpochAsync`, verify snapshot checksums and stream continuity, reconcile any
external consumer, then reopen traffic. Every prior projection cursor will return `ResetRequired`.

Use snapshot-only compaction in the first release. Pass no event prune boundary. Pruning needs a game retention
policy and proof that every durable external consumer has passed the boundary. Purge replay rows in bounded batches
with `PurgeOperationsByAgeAsync` and keep `MinimumRetryHorizon` at least as long as the maximum retry window for
clients and durable server causes.

Live provider tests require `KE_SQLSERVER_TEST_CONNSTRING`. Each test owns a unique stream prefix and cleanup
removes only rows under that prefix. Because cursor epoch rotation and operation retention are database-global,
the test fixture requires the connection string's `Initial Catalog` to contain the literal `-journal-test-` marker.
The guard runs before provider construction, schema validation, maintenance, or mutation.
