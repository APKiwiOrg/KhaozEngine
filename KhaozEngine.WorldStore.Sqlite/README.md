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

`SqliteMutationJournalStore` implements `IMutationJournalStore`, `IMutationJournalMaintenance`, and the additive
`IMutationJournalAgeMaintenance` capability on the same connection lifecycle. It stores metadata and the restore
epoch, stream heads, immutable events, current projection sections, snapshots, replay receipts, and receipt stream
ranges in normalized tables. Writes use one immediate transaction under the connection lease. Auto-create enables
WAL only for an absent journal or after validating an existing supported schema. Validate-only verifies WAL without
changing the journal mode. Foreign keys and a configurable busy timeout are enabled for the held connection.

```csharp
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;

using var journal = new SqliteMutationJournalStore(
    new SqliteMutationJournalStoreOptions("Data Source=journal.db")
    {
        SchemaMode = SqliteJournalSchemaMode.AutoCreate,
        BusyTimeout = TimeSpan.FromSeconds(5),
        MinimumRetryHorizon = TimeSpan.FromHours(24),
        Limits = JournalLimits.Maximum,
    });
```

The default `AutoCreate` schema mode creates a fresh version-two journal for development and new deployments. It
also migrates a valid version-one journal by adding a database-owned operation-retention timestamp. Existing replay
rows start a fresh retention horizon at migration time, so upgrade cannot expire one early.
The migrated schema also stamps inserts from an already-running version-one writer with SQLite time. Restarted hosts
must support version two.
Production hosts can select `SqliteJournalSchemaMode.ValidateOnly` through
`SqliteMutationJournalStoreOptions.SchemaMode` when the application identity has no DDL permission. Missing or
unsupported schemas fail startup with `SchemaMismatch` and name the required migration. Version-two validation
checks the complete table and index definitions before normal store mutation. Operation retention is independent
from event retention, so purging replay receipts leaves committed events in place.

`BusyTimeout` controls how long the held connection waits on a locked database. `MinimumRetryHorizon` prevents
maintenance from deleting replay rows which may still be retried. `Limits` can lower any core journal maximum.
`TimeProvider` controls public journal timestamps for deterministic hosts and tests. It does not control
`PurgeOperationsByAgeAsync`, whose retention timestamps and cutoff come from SQLite. Dispose the journal to clear
the provider pool and release the database file.

The process identity needs read, write, create, lock, and rename access to the database, WAL, and shared-memory
files. In `ValidateOnly`, deploy the version-two schema with a controlled migration process before boot and keep the
runtime directory writable for SQLite transactions. A missing, partial, older, or newer schema stops startup with
`JournalStoreException` kind `SchemaMismatch`.

Keep every journal table in the same backup and restore unit. After restoring an older database, stop all journal
writers, call `IMutationJournalMaintenance.RotateStoreEpochAsync`, verify snapshot checksums and stream continuity,
then reopen traffic. Old projection cursors will return `ResetRequired`.

Use snapshot-only compaction in the first release. Pass no prune boundary. Event pruning needs a game retention
policy and proof that every durable external consumer has passed the boundary. Run replay-row purge in bounded
batches with `PurgeOperationsByAgeAsync` and never shorten `MinimumRetryHorizon` below the longest client or
server-cause retry window.

`SqliteWorldStore` implements **`IEnumerableWorldStore`** (since 8.4.2): `EnumerateAsync(keyPrefix?)` streams
`WorldStoreEntry { Key, UpdatedAt, Size? }` records via a streaming SQLite cursor, optionally filtered by key
prefix. Used by `ServerAdmin` for account enumeration and ban persistence.

`SqliteWorldStore` also overrides **`SaveManyAsync`**: every item in the batch is upserted inside a single
transaction on the shared connection (still gated by the same semaphore as every other operation, so it never
races a concurrent call on that connection), so a batch of N dirty records costs one round trip and one fsync
instead of N.
