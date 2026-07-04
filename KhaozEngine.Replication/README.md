# KhaozEngine.Replication

ECS entity replication for the authoritative-multiplayer stack: full-state snapshots and per-client
area-of-interest deltas.

- **`NetId`** - an `IComponent` identifying an entity across the wire.
- **`ReplicationRegistry`** - register each replicated component type with serialize/deserialize (and optional
  lerp) closures, keyed by a stable `ushort` type id. **Consumer extension components** register at ids at/above
  **`ReplicationRegistry.FirstExtensionTypeId`** (= 16, `IsExtension(id)`); ids `1..15` are reserved for engine
  built-ins. Extension components are length-prefixed on the wire, so a client whose registry never registered the
  id **skips** it (forward-compatible), while an unknown built-in id stays a hard mismatch.
- **`ReplicationChannels`** (since 9.28.0) - an optional `[Flags]` argument to `Register<T>` declaring which of the
  four downstream consumers see a component's bytes: `Replicate` (client area-of-interest serving + border ghosts),
  `Persist` (cell persistence blob), `Migrate` (cell handoff), and `OwnerOnly` (a `Replicate` modifier: replicated
  ONLY to the client that owns the entity, never to another observer in AoI). Default is `Default` = `Replicate |
  Persist | Migrate` - the pre-9.28.0 behaviour where persisted == replicated == migrated - so existing
  registrations and every built-in are unchanged and the wire stays byte-identical for them. This decouples what
  used to be one coupled path: a mob's server-only aggro table (`Persist | Migrate`, no `Replicate`) survives handoff
  + restart but never reaches a client, and a player's private inventory / exact HP (`Default | OwnerOnly`) reaches
  only its own client. The flags gate the **server (write) side** only; the client read side decodes whatever is on
  the wire (so channel flags on a client-built registry are ignored). A built-in id must keep `Default` (its unframed
  encoding is the core protocol) and `OwnerOnly` requires `Replicate` - either violation throws at registration.
  `SnapshotWriter` / `AoiDeltaReplicator` take the serving channel + an optional `ownerNetId` to scope `OwnerOnly`.
- **`SnapshotWriter`** - serialize a server `World`'s `NetId` entities (and their registered components) to an
  opaque `byte[]` snapshot (`Write` full-state, `WriteFiltered` per-client interest). **`ServerReplicator`** is the
  per-slot acked whole-world baseline/delta variant. Both length-prefix extension components.
- **`AoiDeltaReplicator`** (since 9.18.0) - the per-client, `NetId`-keyed, **area-of-interest-scoped** baseline+delta
  encoder: it fuses `ServerReplicator`'s acked-baseline delta compression with per-client interest filtering. Call
  `BeginTick()` once per server tick, then `WriteFor(slot, world, interestSet)` per client; against that client's
  acknowledged baseline it emits an entity that **entered** its interest set as a full spawn, one that **stayed and
  changed** as only its changed components, one that **left** (or despawned) as a removal, and an unchanged in-AoI
  entity as nothing. `Acknowledge(slot, seq)` advances the baseline; `Forget(slot)` drops a disconnected client.
  The wire is byte-identical to `ServerReplicator.WriteFor` (a full snapshot is the `baseline -1` delta), so
  `ClientReplicationView.ApplyDelta` decodes both. Keyed by `NetId` (not by owning cell), so a seamless cell handoff
  reads as a component delta, never a despawn+respawn. This is what `WorldServer`/`ShardedWorldServer`/`MmoServer`
  serve on the live path (see `KhaozEngine.NetWorld`).
- **`InterestGrid`** - a spatial-hash area-of-interest query (`Insert` / `Query(center, radius)`) used to compute a
  client's interest set for `WriteFiltered` / `AoiDeltaReplicator.WriteFor`.
- **`ClientReplicationView`** - apply a snapshot to a client `World`: spawn new entities, despawn gone ones,
  update the rest. Two render-smoothing paths: `Interpolate(world, alpha)` lerps registered components between the
  last two snapshots (the legacy estimate-and-ramp path), and the preferred **fixed-delay buffer** (since 9.23.0):
  `RecordInterpolationSample(t)` stamps each applied snapshot's interpolatable bytes into a per-component timestamped
  history, and `InterpolateAt(world, renderTime)` renders every component at `renderTime` by lerping the two buffered
  samples bracketing it by their true timestamps (clamp to the oldest before the buffer; HOLD at the newest past it,
  flagged via `WasHeldAtLastInterpolation(netId)`; single-sample renders that sample). This decouples presentation
  from the tick cadence and the render fps. An unregistered
  **extension** id (>= the floor) is skipped, so an older client tolerates a newer server's added component.
  `Apply` is full-state. **`ApplyDelta`** applies a `ServerReplicator`/`AoiDeltaReplicator` delta and is
  **self-healing**: a delta whose baseline is at or before `LastAppliedSeq` is a valid idempotent rebuild (the
  server builds from the client's last ACKED baseline, which lags whenever an ack is in flight or lost), so a
  dropped delta/ack needs no full resync; only a baseline AHEAD of `LastAppliedSeq` is a gap that throws. A
  `baseline -1` delta is a full snapshot (despawns tracked entities it omits). `Apply`/`ApplyDelta` throw on an
  otherwise-malformed or version-incompatible snapshot (an unregistered BUILT-IN type id from a newer core protocol,
  or a corrupt extension length); `TryApply`/`TryApplyDelta` (since 8.5.0) are the non-throwing variants - they
  return `false` + an error instead, so a skewed snapshot becomes a clean disconnect rather than an unhandled
  exception in the frame loop (`WorldClient` uses them).

Transport-free: snapshots/deltas are plain `byte[]`, shipped via your `KhaozEngine.Netcode` session layer
(`NetServer.Broadcast` / `NetClient` data events). Depends on `KhaozEngine.Ecs` only.

Full-state (`SnapshotWriter`), whole-world delta (`ServerReplicator`), and per-client AoI delta
(`AoiDeltaReplicator`) all ship. See `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`.
