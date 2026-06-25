# KhaozEngine.Replication

Full-state ECS entity replication for the authoritative-multiplayer stack.

- **`NetId`** - an `IComponent` identifying an entity across the wire.
- **`ReplicationRegistry`** - register each replicated component type with serialize/deserialize (and optional
  lerp) closures, keyed by a stable `ushort` type id.
- **`SnapshotWriter`** - serialize a server `World`'s `NetId` entities (and their registered components) to an
  opaque `byte[]` snapshot.
- **`ClientReplicationView`** - apply a snapshot to a client `World`: spawn new entities, despawn gone ones,
  update the rest; `Interpolate` smooths registered components between the last two snapshots.

Transport-free: snapshots are plain `byte[]`, shipped via your `KhaozEngine.Netcode` session layer
(`NetServer.Broadcast` / `NetClient` data events). Depends on `KhaozEngine.Ecs` only.

Full-state first; baseline+delta is a later stage. See
`docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md` and the 1C plan.
