# KhaozEngine.WorldStore.Sqlite

SQLite `IWorldStore` backend over `Microsoft.Data.Sqlite`. The embedded, zero-infra dev/test and single-node
durable store for an authoritative world.

```csharp
using KhaozEngine.WorldStore.Sqlite;

IWorldStore store = new SqliteWorldStore("Data Source=world.db");
await store.SaveAsync("player:42", bytes);
byte[]? loaded = await store.LoadAsync("player:42");
```

One `world_store(key, data, updated_at)` table, bootstrapped on construction; upsert via
`INSERT ... ON CONFLICT(key) DO UPDATE`; raw parameterized async ADO.NET (no EF/ORM). Dispose the store to
close the connection. Disposing also clears the provider's connection pool for that connection, so the OS handle
on the database file is genuinely released rather than parked in the pool, and the file can be deleted, rotated
or exclusively opened straight after (since 17.41.0). For production / Azure SQL use
`KhaozEngine.WorldStore.SqlServer` against the same `IWorldStore` contract.

The connection, the operation gate and that dispose are `KhaozEngine.Sqlite`'s `SqliteStoreConnection`, shared
with every other SQLite store in the engine. Only the schema and the SQL live here. A game writing its own
SQLite-backed store should sit it on the same type rather than reimplementing the pool-clearing dispose.

`SqliteMutationJournalStore` implements `IMutationJournalStore` and `IMutationJournalMaintenance` on the same
connection lifecycle. It stores metadata and the restore epoch, stream heads, immutable events, current projection
sections, snapshots, replay receipts, and receipt stream ranges in normalized tables. Writes use one immediate
transaction under the connection lease. WAL mode, foreign keys, and a configurable busy timeout are enabled for
the held connection.

```csharp
using KhaozEngine.WorldStore.Sqlite;

using var journal = new SqliteMutationJournalStore("Data Source=journal.db");
```

The default `AutoCreate` schema mode creates a fresh version-one journal for development and new deployments.
Production hosts can select `SqliteJournalSchemaMode.ValidateOnly` through
`SqliteMutationJournalStoreOptions.SchemaMode` when the application identity has no DDL permission. Missing or
unsupported schemas fail startup with `SchemaMismatch` and name the required migration. Operation retention is
independent from event retention, so purging replay receipts leaves committed events in place.

`SqliteWorldStore` implements **`IEnumerableWorldStore`** (since 8.4.2): `EnumerateAsync(keyPrefix?)` streams
`WorldStoreEntry { Key, UpdatedAt, Size? }` records via a streaming SQLite cursor, optionally filtered by key
prefix. Used by `ServerAdmin` for account enumeration and ban persistence.

`SqliteWorldStore` also overrides **`SaveManyAsync`**: every item in the batch is upserted inside a single
transaction on the shared connection (still gated by the same semaphore as every other operation, so it never
races a concurrent call on that connection), so a batch of N dirty records costs one round trip and one fsync
instead of N.
