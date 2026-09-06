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

`SqlServerMutationJournalStore` implements `IMutationJournalStore` and `IMutationJournalMaintenance` for
durable player mutations. It uses serializable SQL transactions, binary collations for stream and section
identity, checksummed event and snapshot payloads, replay receipts, projection cursors, compaction, operation
retention, and store epoch rotation.

```csharp
var journal = new SqlServerMutationJournalStore(
    new SqlServerMutationJournalStoreOptions(connectionString)
    {
        SchemaMode = SqlServerJournalSchemaMode.ValidateOnly,
        MinimumRetryHorizon = TimeSpan.FromHours(24),
    });
```

`AutoCreate` creates version one only when no journal objects exist. It is intended for fresh databases and
development. `ValidateOnly` performs no DDL and is the production mode when the application principal does not
have schema permissions. A partial, malformed, older, or newer journal schema fails with `SchemaMismatch` and
names the required migration.

The package embeds `JournalSchemaV1.sql`. Deployments may apply that script before starting a validate-only
host. Initialization is serialized with a transaction-owned SQL application lock. Normal writes share a
maintenance gate, while compaction, replay retention, and epoch rotation take the exclusive side.

Live provider tests require `KE_SQLSERVER_TEST_CONNSTRING`. Each test owns a unique stream prefix and cleanup
removes only rows under that prefix.
