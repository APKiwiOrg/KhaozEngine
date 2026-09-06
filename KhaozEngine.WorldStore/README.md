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

**The player-persistence core.** `StatePersistence<TState>` is the save/load orchestration that wires an
`IWorldStore` into a server head's lifecycle: load-on-join, save-on-leave, the periodic dirty pass, the per-session
load guard, the per-key write ordering a rejoin waits behind, quarantine of a record that fails validation, the
guest policy, and the bounded rejoin hints (`PositionHintCache`, plus the `PositionHintProvider` a head installs).
It is generic over the head's own state and knows nothing about a record:

- **`IPersistenceHost<TState>`** - the head's side of the seam (`PlayerJoined` / `PlayerLeaving` / `SetPlayerState` /
  `JoinedSlots` / `TryGetAccountId` / `TryGetPlayerState`, plus the join-seed pair `SetPositionHintProvider` +
  `TryGetConfiguredSpawn` as default interface methods).
- **`PersistenceBinding<TState>`** - the required delegates that define the movement model: `PositionOf`, `Encode`,
  `Decode` (a `RecordDecoder<TState>`), and `Validate`. A discrete binding can set `RestoreDistance` to compare in
  its native coordinate type. Null preserves the continuous world's Euclidean `Vector3` behavior.
- **`PersistenceCoreConfig`** - the machinery's tunables (interval, key prefixes, guest policy, hint capacity, the
  quiet-restore distance), the three game-state hooks keyed by slot and resolved key, and `Diagnostic`, the sink the
  core's own log lines go out through. That sink is why this package can carry the core and stay dependency-free: a
  head wires it to its own logging.

**`PrewarmHintsAsync(max = 0, ct)`** fills the rejoin hints from the store at boot, newest record first, and
returns how many accounts it seeded. The hints are memory-only, so without it the first rejoin of every account
after a process restart falls back to the head's configured spawn and takes the restore teleport the seed exists
to remove. It needs an `IEnumerableWorldStore` (below) and is a no-op returning 0 on any other store. Every record
is put through the binding's `Decode` and `Validate` first, because the join builds the player ON the hint and
nothing else validates a hint, so a record the load path would quarantine must never become one. Guest keys and
quarantine copies are skipped. Call it on the server thread before the head starts polling and await it:
`PositionHintCache` is not thread-safe.

The two bindings that ship are `WorldPersistence` in `KhaozEngine.NetWorld` (float, over `PlayerRecord`) and
`TileWorldPersistence` in `KhaozEngine.TileWorld.Netcode` (tile, over `TilePlayerRecord`). Both keep their own
config type and public surface, so a game pinned to either is unaffected.

**Account enumeration (since 8.4.2).** Stores can opt into **`IEnumerableWorldStore`**: `EnumerateAsync(keyPrefix?)`
streams `WorldStoreEntry { Key, UpdatedAt, Size? }` records. `InMemoryWorldStore`, `SqliteWorldStore`, and
`SqlServerWorldStore` all implement it. Feature-detect with `store is IEnumerableWorldStore`. The `ServerAdmin`
facade in `KhaozEngine.NetWorld` uses it for account listing and bans.

Part of the MMO netcode stack (Phase 2).
