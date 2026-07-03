# KhaozEngine.Replication

Full-state ECS entity replication for the authoritative-multiplayer stack.

- **`NetId`** - an `IComponent` identifying an entity across the wire.
- **`ReplicationRegistry`** - register each replicated component type with serialize/deserialize (and optional
  lerp) closures, keyed by a stable `ushort` type id. **Consumer extension components** register at ids at/above
  **`ReplicationRegistry.FirstExtensionTypeId`** (= 16, `IsExtension(id)`); ids `1..15` are reserved for engine
  built-ins. Extension components are length-prefixed on the wire, so a client whose registry never registered the
  id **skips** it (forward-compatible), while an unknown built-in id stays a hard mismatch.
- **`SnapshotWriter`** - serialize a server `World`'s `NetId` entities (and their registered components) to an
  opaque `byte[]` snapshot (`Write` full-state, `WriteFiltered` per-client interest). **`ServerReplicator`** is the
  per-slot acked baseline/delta variant. Both length-prefix extension components.
- **`ClientReplicationView`** - apply a snapshot to a client `World`: spawn new entities, despawn gone ones,
  update the rest; `Interpolate` smooths registered components between the last two snapshots. An unregistered
  **extension** id (>= the floor) is skipped, so an older client tolerates a newer server's added component.
  `Apply`/`ApplyDelta` throw on an otherwise-malformed or version-incompatible snapshot (e.g. an unregistered
  BUILT-IN type id from a newer core protocol, or a corrupt extension length); `TryApply`/`TryApplyDelta` (since
  8.5.0) are the non-throwing variants - they return `false` + an error instead, so a skewed snapshot can be
  turned into a clean disconnect rather than an unhandled exception in the frame loop (`WorldClient` uses
  `TryApply`).

Transport-free: snapshots are plain `byte[]`, shipped via your `KhaozEngine.Netcode` session layer
(`NetServer.Broadcast` / `NetClient` data events). Depends on `KhaozEngine.Ecs` only.

Both full-state (`SnapshotWriter`) and baseline/delta (`ServerReplicator`) ship. See
`docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md` and the 1C plan.
