# KhaozEngine.Replication

ECS entity replication for the authoritative-multiplayer stack: full-state snapshots and per-client
area-of-interest deltas.

- **`NetId`** - an `IComponent` identifying an entity across the wire. 64-bit since 10.0.0 (was a 32-bit `int`), under
  a node-prefix scheme: the high 16 bits are a node/allocator id (0 for a single-process server), the low 48 bits a
  per-node counter (`NetId.Node` / `NetId.Counter`). Node 0 ids are numerically the old counter (1, 2, 3, …).
- **`NetIdAllocator`** (since 10.0.0) - the single place ids are allocated, replacing the raw `++int` the servers used.
  `Next()` hands out the next id for its node; `NextValue` is the packed high-water to persist; `EnsureNextAtLeast(long)`
  resumes above a restored id (never lowers, ignores a different node's value). `Pack(node, counter)` / `NodeOf` /
  `CounterOf` expose the packing. A future multi-process layer gives each node a distinct prefix, so two nodes allocate
  collision-free without recycling (2^48 per node).
- **`ReplicationRegistry`** - register each replicated component type with serialize/deserialize (and optional
  lerp) closures, keyed by a stable `ushort` type id. **Consumer extension components** register at ids at/above
  **`ReplicationRegistry.FirstExtensionTypeId`** (= 16, `IsExtension(id)`); ids `1..15` are reserved for engine
  built-ins. Extension components are length-prefixed on the wire, so a client whose registry never registered the
  id **skips** it (forward-compatible), while an unknown built-in id stays a hard mismatch.
  `IsRegistered(ushort)` (since 17.38.0) asks whether this registry has a codec for an id, for a caller judging
  whether an id it read out of STORED bytes is one this build knows: cell-blob persistence uses it to retire a
  candidate parse of a blob whose wire generation was never recorded.
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
  Every client-serving path honours the channels: `SnapshotWriter` / `AoiDeltaReplicator` take the serving channel +
  an optional `ownerNetId` to scope `OwnerOnly`, and `ServerReplicator.Capture` captures only the `Replicate` channel
  while `ServerReplicator.WriteFor(slot, ownerNetId)` scopes `OwnerOnly` per client (a Persist-/Migrate-only server
  component never reaches any of them).
  - **Footgun - `Persist` without `Migrate`:** a component registered `Persist` but not `Migrate` survives a server
    restart (it is in the cell's persist blob) yet is dropped the instant its entity crosses a cell boundary (handoff
    captures only the `Migrate` channel), so on a seamless sharded world it silently vanishes when the entity walks
    into the next cell. Durable state a player/entity carries around wants BOTH (`Persist | Migrate`, i.e. `Default`).
    Use `Persist` alone only for state that is genuinely bound to the cell rather than the entity.
- **`SnapshotWriter`** - serialize a server `World`'s `NetId` entities (and their registered components) to an
  opaque `byte[]` snapshot (`Write` full-state, `WriteFiltered` per-client interest). A `WriteFiltered` overload
  (since 9.33.0) also re-emits per-entity opaque `RetainedComponent` extension frames after the registered ones (the
  write side of cell-blob retain-and-rewrite). For a hot per-tick server it also has an **indexed** form:
  `WriteFiltered(WorldSnapshotIndex, SnapshotScratch, ...)` resolves the interest / border set off a
  **`WorldSnapshotIndex`** (a reusable `NetId` -> entity index over one world, `Rebuild(world)` once per tick, shared
  across every filtered snapshot targeting that world) in `O(setCount)` instead of a full-world `ForEach`, and encodes
  through a reusable **`SnapshotScratch`** stream so only the returned wire array is allocated. `WriteSingle` is the
  one-entity fast path for a caller that already holds the entity handle (an authority handoff capturing one crossing).
  All three are byte-identical to the full-scan `WriteFiltered` (entities stay in world `ForEach` order). **`ServerReplicator`** is the
  per-slot acked whole-world baseline/delta variant (no AoI scoping): `Capture(world)` once per tick, then
  `WriteFor(slot, ownerNetId)` per client. It is channel-aware like the others - only `Replicate` components are
  captured, and an `OwnerOnly` component reaches only the client whose player net id is `ownerNetId`. Both
  length-prefix extension components.
- **`AoiDeltaReplicator`** (since 9.18.0) - the per-client, `NetId`-keyed, **area-of-interest-scoped** baseline+delta
  encoder: it fuses `ServerReplicator`'s acked-baseline delta compression with per-client interest filtering. Call
  `BeginTick()` once per server tick, then `WriteFor(slot, world, interestSet)` per client; against that client's
  acknowledged baseline it emits an entity that **entered** its interest set as a full spawn, one that **stayed and
  changed** as only its changed components, one that **left** (or despawned) as a removal, and an unchanged in-AoI
  entity as nothing. `Acknowledge(slot, seq)` advances the baseline; `Forget(slot)` drops a disconnected client.
  The wire is byte-identical to `ServerReplicator.WriteFor` (a full snapshot is the `baseline -1` delta), so
  `ClientReplicationView.ApplyDelta` decodes both. Keyed by `NetId` (not by owning cell), so a seamless cell handoff
  reads as a component delta, never a despawn+respawn. This is what `WorldServer`/`ShardedWorldServer`/`MmoServer`
  serve on the live path (see `KhaozEngine.NetWorld`). `WriteFor` captures the whole world once per tick, shared
  across every client served from that world (not once per client), into one pooled buffer, so allocation drops
  sharply at high client counts while the wire stays byte-identical. On top of that shared capture the per-client
  projection is `O(interestSet)`: it resolves each client's interest set off the capture in `O(1)` per entity and
  re-orders the selection by capture position, rather than walking the whole capture per client - so per-client cost
  scales with the client's area of interest, not the world population, with the wire unchanged.
- **`InterestGrid`** - a spatial-hash area-of-interest query (`Insert` / `Query(center, radius)`) used to compute a
  client's interest set for `WriteFiltered` / `AoiDeltaReplicator.WriteFor`.
- **`ClientReplicationView`** - apply a snapshot to a client `World`: spawn new entities, despawn gone ones,
  update the rest. Two render-smoothing paths: `Interpolate(world, alpha)` lerps registered components between the
  last two snapshots (the legacy estimate-and-ramp path), and the preferred **fixed-delay buffer** (since 9.23.0):
  `RecordInterpolationSample(t, excludeNetId?)` stamps each applied snapshot's interpolatable bytes into a per-component
  timestamped history, and `InterpolateAt(world, renderTime, excludeNetId?)` renders every component at `renderTime` by
  lerping the two buffered samples bracketing it by their true timestamps (clamp to the oldest before the buffer; HOLD
  at the newest past it, flagged via `WasHeldAtLastInterpolation(netId)`; single-sample renders that sample). This
  decouples presentation from the tick cadence and the render fps. The optional `excludeNetId` skips one entity in both
  calls (the local, predicted avatar): it renders from prediction, so its client-world position must stay the
  last-received authoritative value (the reconcile basis), never a fixed-delay interpolated one - passing the local net
  id keeps a post-teleport static local player from feeding a stale basis back into reconcile. When it changes (a
  reconnect assigns a new local id) the new id's stale buffer is dropped. **`SnapInterpolationToNewest(netId)`** (since 10.67.0) drops all but the
  newest buffered sample for one entity, so `InterpolateAt` cuts to it instead of lerping across a discontinuity - the
  netcode layer calls it when an entity teleports (keyed off its replicated teleport epoch) so a remote teleport does
  not streak across the world. An unregistered
  **extension** id (>= the floor) is skipped, so an older client tolerates a newer server's added component.
  `Apply` is full-state. **`ApplyDelta`** applies a `ServerReplicator`/`AoiDeltaReplicator` delta and is
  **self-healing**: a delta whose baseline is at or before `LastAppliedSeq` is a valid idempotent rebuild (the
  server builds from the client's last ACKED baseline, which lags whenever an ack is in flight or lost), so a
  dropped delta/ack needs no full resync; only a baseline AHEAD of `LastAppliedSeq` is a gap that throws. A
  `baseline -1` delta is a full snapshot (despawns tracked entities it omits). `Apply`/`ApplyDelta` throw on an
  otherwise-malformed or version-incompatible snapshot (an unregistered BUILT-IN type id from a newer core protocol,
  or a corrupt extension length); `TryApply`/`TryApplyDelta` (since 8.5.0) are the non-throwing variants - they
  return `false` + an error instead, so a skewed snapshot becomes a clean disconnect rather than an unhandled
  exception in the frame loop (`WorldClient` uses them). **`TryApplyRetainingUnknown`** (since 9.33.0) is the
  persistence-restore variant: it applies non-throwing AND collects every unknown **extension** frame it would
  otherwise skip as a raw `RetainedComponent` (net id + type id + payload), so the caller (cell persistence) can
  retain and re-persist it verbatim instead of silently dropping data at rest under a registry downgrade.
- **`SnapshotBlobReader` / `SnapshotBlobWriter`** (since 9.33.0) - walk and rebuild the snapshot wire format
  (`[count][per entity: netId + (typeId,[len],payload).. + 0]`) into structured entities/components, so a cell-blob
  migration can map / drop / transform per-component payloads without hand-parsing the stream. Extension frames
  (id >= the floor) are length-prefixed and self-describing; a built-in (unframed) frame is walkable only when the
  reader is given a `builtinPayloadLength` resolver for the OLD layout it targets (else it throws rather than
  mis-parsing). A well-formed blob round-trips byte-identically, so a migration that touches one component leaves
  every other byte identical. **`RetainedComponent`** is the opaque frame type shared by `TryApplyRetainingUnknown`
  (capture) and the `SnapshotWriter.WriteFiltered` retained-frames overload (re-emit).

Transport-free: snapshots/deltas are plain `byte[]`, shipped via your `KhaozEngine.Netcode` session layer
(`NetServer.Broadcast` / `NetClient` data events). Depends on `KhaozEngine.Ecs` only.

Full-state (`SnapshotWriter`), whole-world delta (`ServerReplicator`), and per-client AoI delta
(`AoiDeltaReplicator`) all ship.
