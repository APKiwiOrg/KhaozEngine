# KhaozEngine.NetWorld

Render-free networked-world layer that wires the [KhaozEngine.Locomotion](../KhaozEngine.Locomotion)
movement core to the authoritative netcode stack ([Netcode](../KhaozEngine.Netcode) +
[Replication](../KhaozEngine.Replication)).

- **`PlayerMoveSimulator`** (`ITickSimulator`) runs `CharacterMovement.Step` both server-authoritatively
  and inside client prediction, so the two stay in lockstep.
- **`WorldServer`** is a single-`World` authoritative movement server: a `NetServer` session layer spawns
  one player entity per connection, drains that client's queued `MoveCommand` each tick, runs the ground-
  clamped sim, and serves each client a per-area-of-interest snapshot (`SnapshotWriter.WriteFiltered` over
  an `InterestGrid`) prefixed with that client's net id + last-acked move seq.
- **`WorldClient`** wraps `NetClient` + `ClientReplicationView` + `ClientPrediction` and exposes
  `EntityRenderState[]` (local player predicted + reconciled, remotes from replicated positions).
- **`WorldPersistence`** (+ `WorldPersistenceConfig`, `PlayerRecord`) wires an
  [`IWorldStore`](../KhaozEngine.WorldStore) into the `WorldServer` lifecycle so the world survives a restart:
  load-on-join (spawn at the saved position, default if absent), save-on-leave, and a periodic snapshot of
  players dirty since their last save. Keyed `player:{accountId}`; backend-agnostic (only `IWorldStore` +
  `KhaozEngine.Serialization`). Pick a backend: `KhaozEngine.WorldStore.Sqlite` (dev/test) or
  `KhaozEngine.WorldStore.SqlServer` (prod / Azure SQL).

No render, window, or GPU dependency: the server is headless and the client glue is render-free (a sample
renders a capsule per `EntityRenderState`). This is the single-`World` slice of the MMO overworld;
multi-cell sharding folds in with world streaming later.
