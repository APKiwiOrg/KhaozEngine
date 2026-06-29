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
