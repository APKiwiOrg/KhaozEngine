# KhaozEngine.WorldStore

Server-side durable persistence seam for an authoritative world.

- **`IWorldStore`** - an async, keyed `byte[]` store (`LoadAsync`/`SaveAsync`/`DeleteAsync`/`ExistsAsync`),
  shaped for a **database** backend rather than the per-user local JSON save files of `KhaozEngine.Persistence`.
  The game serializes a character/zone/account record to bytes (via its own serializer) and persists it by key.
- **`InMemoryWorldStore`** - a thread-safe, dependency-free reference implementation for tests and local dev.

**Batched saves.** `IWorldStore.SaveManyAsync(IReadOnlyList<(string Key, byte[] Data)> items)` saves a whole set
of records in one logical operation instead of one round trip per record - the periodic dirty-save passes in
`KhaozEngine.NetWorld` (`WorldPersistence.SaveDirtyPass`, `CellPersistence.SaveDirtyPass`) call it once per pass
instead of once per dirty player/cell. It ships as a C# default interface member that loops `SaveAsync`, so every
existing `IWorldStore` implementation - including a consumer-owned one written before this member existed - keeps
compiling and behaving correctly unchanged. It just does not get the batching win until it overrides the member.
`InMemoryWorldStore` overrides it (one clock reading for the whole batch). `SqliteWorldStore` and
`SqlServerWorldStore` override it with a real single-round-trip batch (see their own READMEs). A backend that
overrides this member should make the batch atomic (all rows land or none do) when it reasonably can, so a caller
that treats a faulted `SaveManyAsync` as "nothing in this batch is durable yet, retry the whole batch" is correct
either way.

This core package is dependency-free (the seam + the in-memory reference). Durable backends are opt-in sibling
packages, each pulling its own DB driver so this core stays clean:

- **`KhaozEngine.WorldStore.Sqlite`** (`SqliteWorldStore`, Microsoft.Data.Sqlite) - embedded dev/test + single-node.
- **`KhaozEngine.WorldStore.SqlServer`** (`SqlServerWorldStore`, Microsoft.Data.SqlClient) - production / Azure SQL.

The save/load orchestration that wires an `IWorldStore` into the server lifecycle (load-on-join / save-on-leave /
periodic snapshot) is `WorldPersistence` in `KhaozEngine.NetWorld`.

**Account enumeration (since 8.4.2).** Stores can opt into **`IEnumerableWorldStore`**: `EnumerateAsync(keyPrefix?)`
streams `WorldStoreEntry { Key, UpdatedAt, Size? }` records. `InMemoryWorldStore`, `SqliteWorldStore`, and
`SqlServerWorldStore` all implement it. Feature-detect with `store is IEnumerableWorldStore`. The `ServerAdmin`
facade in `KhaozEngine.NetWorld` uses it for account listing and bans.

Part of the MMO netcode stack (Phase 2).
