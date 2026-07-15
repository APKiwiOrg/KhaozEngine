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
close the connection. For production / Azure SQL use `KhaozEngine.WorldStore.SqlServer` against the same
`IWorldStore` contract.

`SqliteWorldStore` implements **`IEnumerableWorldStore`** (since 8.4.2): `EnumerateAsync(keyPrefix?)` streams
`WorldStoreEntry { Key, UpdatedAt, Size? }` records via a streaming SQLite cursor, optionally filtered by key
prefix. Used by `ServerAdmin` for account enumeration and ban persistence.

`SqliteWorldStore` also overrides **`SaveManyAsync`**: every item in the batch is upserted inside a single
transaction on the shared connection (still gated by the same semaphore as every other operation, so it never
races a concurrent call on that connection), so a batch of N dirty records costs one round trip and one fsync
instead of N.
