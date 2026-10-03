# KhaozEngine.Replication

ECS entity replication for the authoritative-multiplayer stack: full-state snapshots, per-client
area-of-interest deltas over a reliable channel, and opt-in format 2 acknowledged rebuild for unreliable delivery.

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
  id **skips** it (forward-compatible), while an unknown built-in id stays a hard mismatch. Since 17.40.0 an
  extension `deserialize` closure reads through a reader bounded to EXACTLY that component's own framed payload
  (`FramedPayloadStream`, re-pointed per component and reused for a whole apply, so framing allocates nothing), so
  a codec that carries a declared length of its own can check it against `BaseStream.Length - Position` and a lying
  length cannot be satisfied out of the bytes of the components behind it. A built-in frame is unframed and has no
  such bound, so it still reads through the stream itself.
  `IsRegistered(ushort)` (since 17.38.0) asks whether this registry has a codec for an id, for a caller judging
  whether an id it read out of STORED bytes is one this build knows: cell-blob persistence uses it to retire a
  candidate parse of a blob whose wire generation was never recorded. `IsRegistered<T>(id, expectedChannels)`
  (since 20.14.1) additionally requires the exact component type and complete channel set. Extra flags do not
  match. This metadata read invokes no codec and does not verify the codec's byte implementation. Use it for
  startup contracts that require state to migrate without being replicated or persisted.
- **`ReplicationChannels`** (since 9.28.0) - an optional `[Flags]` argument to `Register<T>` declaring which of the
  four downstream consumers see a component's bytes: `Replicate` (client area-of-interest serving + border ghosts),
  `Persist` (cell persistence blob), `Migrate` (cell handoff), and `OwnerOnly` (a `Replicate` modifier: replicated
  ONLY to the client that owns the entity, never to another observer in AoI). Default is `Default` = `Replicate |
  Persist | Migrate` - the pre-9.28.0 behaviour where persisted == replicated == migrated - so existing
  registrations are unchanged and the wire stays byte-identical for them. This decouples what
  used to be one coupled path: a mob's server-only aggro table (`Persist | Migrate`, no `Replicate`) survives handoff
  + restart but never reaches a client, and a player's private inventory / exact HP (`Default | OwnerOnly`) reaches
  only its own client. The flags gate the **server (write) side** only; the client read side decodes whatever is on
  the wire (so channel flags on a client-built registry are ignored). A built-in id must keep `Default`, optionally
  with `OwnerOnly` (`ReplicationRegistry.BuiltinChannelsAllowed`), because its unframed encoding is the core
  protocol. `OwnerOnly` is the one modifier a built-in may carry: it decides only whether the WHOLE `[typeId][payload]`
  frame reaches a viewer and never changes the frame's bytes, so a viewer that is not sent it has nothing to skip and
  persistence and handoff still write it (the NetWorld `MovementOwnerState` feel timers use this). `OwnerOnly` also
  requires `Replicate`. Any other set throws at registration.
  Owner scoping costs at most one filtered copy per entity per tick: the owner is handed the entity's captured
  component set itself, and every other viewer one shared public view built on first use.
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
  whole-world delta variant (no AoI scoping): `Capture(world)` once per tick, then `WriteFor(slot, ownerNetId)` per
  client, each diffed against the projection last sent to that slot under the legacy reliable contract below. It is
  channel-aware like the others - only `Replicate` components are captured, and an `OwnerOnly` component reaches only
  the client whose player net id is `ownerNetId`. The slot stores the exact owner-scoped projection it sent, so an
  owner change removes the old owner's private components explicitly. Both length-prefix extension components.
- **`AoiDeltaReplicator`** (since 9.18.0) - the per-client, `NetId`-keyed, **area-of-interest-scoped** delta
  encoder: `ServerReplicator`'s delta compression with per-client interest filtering. Call `BeginTick()` once per
  server tick, then `WriteFor(slot, world, interestSet)` per client. Against the projection last sent to that slot it
  emits an entity that **entered** its interest set as a full spawn, one that **stayed and changed** as only its
  changed components, one that **left** (or despawned) as a removal, and an unchanged in-AoI entity as nothing.
  Presence is part of that last-sent projection, so an AoI edge needs no separate record. `Acknowledge(slot, seq)`
  is sequence-only diagnostics and never moves the diff basis. `Forget(slot)` drops a slot, as the legacy reliable
  contract below requires.
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
  `Apply` is full-state. **`ApplyDelta`** overlays a `ServerReplicator`/`AoiDeltaReplicator` delta on the live world.
  Under the legacy reliable contract each delta names the projection the receiver already holds, so the overlay is
  exact. The reader is unchanged: it still accepts a baseline at or before `LastAppliedSeq` and throws only for one
  ahead of it. That older-baseline overlay is not a reconstruction. It cannot restore a value, a component or an
  entity a newer delivered delta changed, so it is not loss recovery, and the legacy writers never produce it while
  the send commitment holds. Unreliable delivery uses format 2 and `ClientDeltaRebuild` below. A
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
  every other byte identical. A built-in whose payload carries its own length (`MoveProtocol.IdentityTypeId` is
  `[ushort byteLen][utf8 bytes]`, which `BuiltinBlobLayout` reports as its `LengthPrefixed` sentinel) cannot be
  described by an id-keyed resolver at all, so since 18.10.0 there is a second constructor taking a
  `Func<ushort, BinaryReader, int>`: it is called with the reader at the payload's first byte and may read a prefix
  to work the total out. Whatever it read is rewound before the payload is captured, so the frame keeps its prefix
  and still re-emits byte for byte. **`RetainedComponent`** is the opaque frame type shared by `TryApplyRetainingUnknown`
  (capture) and the `SnapshotWriter.WriteFiltered` retained-frames overload (re-emit).

## Legacy reliable delta contract

Both `WriteFor` methods diff against the exact viewer projection last sent to the slot. The header names that
projection's sequence, and the first serve after `Forget` is full state with baseline -1. Acknowledgements never
choose the basis, so a value that goes `1 -> 2 -> 1` while an ack is in flight still reaches the receiver as 1. The
wire body and the `ApplyDelta` reader are unchanged. What a caller can observe is the header: the baseline is now the
last sent sequence, and during an ack delay a delta carries only the changes since that send rather than repeating
every change since the last acknowledgement. That is a header and bandwidth difference, not a body format migration.

The obligations a standalone owner takes on:

- **Send commitment.** Every returned `WriteFor` payload is shipped exactly once, in order, over a reliable-ordered
  channel to the receiver it was built for.
- **Discarded or failed send.** A caller that discards a payload, sends it unreliably, changes session, replaces the
  receiver, or sees the send throw calls `Forget(slot)` and serves a fresh receiver world and
  `ClientReplicationView`, or disconnects. `Forget` alone cannot repair an old receiver: the legacy reader ignores
  `isNew`, and a full entity does not remove a component the receiver already holds.
- **Per-slot reset.** `Forget(slot)` (now also on `ServerReplicator`) clears that slot's last-sent state and its format
  2 state. It never touches the writer's shared sequence counter.
- **Global exhaustion.** The signed sequence is one counter per writer, shared by every slot. `int.MaxValue` is the
  last capture, after which `LegacySequenceExhausted` is true and `Capture` or `BeginTick` throws without scanning or
  incrementing. The owner then ends every connection the writer serves, including format 2 sessions on the same
  writer, and only after they have ended calls `ResetAfterLegacySequenceExhaustion()`. It throws before exhaustion,
  and clears the counter, the capture and every slot's legacy and format 2 state. The next capture is sequence 1 and
  every next serve is full state for a fresh receiver. Replication cannot end transports, so it cannot verify the
  precondition. The reset remembers no format 2 epoch, so the owner's epoch source must stay monotonic across it.

```csharp
// Standalone legacy owner: one writer, one reliable-ordered connection per slot.
var writer = new AoiDeltaReplicator(registry);

void ServeTick()
{
    if (writer.LegacySequenceExhausted)
    {
        foreach (int slot in connectedSlots) DisconnectAndForget(slot);   // fresh receivers only from here on
        writer.ResetAfterLegacySequenceExhaustion();
        return;
    }
    writer.BeginTick();
    foreach (int slot in connectedSlots)
    {
        byte[] delta = writer.WriteFor(slot, world, InterestOf(slot), PlayerNetIdOf(slot));
        try { connection[slot].SendReliableOrdered(delta); }        // a send commitment: exactly once, in order
        catch { DisconnectAndForget(slot); throw; }                  // the receiver can no longer be trusted
    }
}

void DisconnectAndForget(int slot)
{
    connection[slot].Disconnect();
    writer.Forget(slot);   // a later serve to a reused slot is full state for a fresh world and view
}
```

## Format 2 acknowledged rebuild

Format 2 is the opt-in unreliable contract. A writer slot diffs against a projection the receiver acknowledged, and
the receiver reconstructs the complete next projection privately before it publishes anything. A lost or reordered
packet or acknowledgement never strands a value, a component or an entity.

- **Identity and order.** `ReplicationPacketId(ulong Epoch, uint Sequence)`. An epoch is a nonzero stream identity the
  owner issues, strictly increasing and never reused for the owner's lifetime. A slot or receiver accepts only a
  greater replacement epoch. Within an epoch, `a` is newer than `b` exactly when the unsigned difference `a - b` is in
  1 to `0x7fffffff`, so the sequence wraps through `uint.MaxValue` to 0. Equality is a duplicate and a difference of
  exactly half the range is ambiguous (`DeltaRebuildFailure.SequenceAmbiguous`). The sequence is a send identity,
  never a simulation tick or a movement command id.
- **Writers.** `ServerReplicator` and `AoiDeltaReplicator` both expose `StartRebuild(slot, epoch, options)`,
  `BuildRebuildFor(...)`, `RecordRebuildSent(slot, id)`, `AcknowledgeRebuild(slot, id)` and
  `RebuildNeedsRepair(slot)`. They share the legacy capture (one capture per world per tick across legacy and format 2
  viewers) but never the legacy sequence. `BuildRebuildFor` retains one unsent candidate, a delta from the
  acknowledged baseline or a keyframe from empty state when `keyframe` is set or nothing is acknowledged. Retained
  projections are compact viewer-only copies taken after interest filtering and owner scoping, so a retained slot
  never holds a hidden owner's bytes. A failed build leaves the slot unchanged.
- **Send commitment.** `RecordRebuildSent` is called only after the packet was handed to transport, or after every
  chunk of a keyframe was. Nothing acknowledges an unsent candidate. A committed keyframe stays pinned until its
  exact acknowledgement.
- **Exact acknowledgement.** `AcknowledgeRebuild` promotes an id only when it is a committed send of the slot's current
  epoch, still retained and strictly newer than the current baseline. Stale, duplicate, future, pruned, never-sent,
  other-slot and retired-epoch ids are ignored. A dropped acknowledgement leaves the old baseline, from which later
  deltas still reconstruct.
- **Repair signal.** Check `RebuildNeedsRepair(slot)` before every build. It turns true once `NoAckSendWindow`
  committed sends (31 by default) went without a newer acknowledgement. The owner then starts a new epoch and sends one
  keyframe. A `DeltaRebuildException` names its `Failure`: `CapacityExceeded` means the projection can never fit and
  repair cannot help, while `RetentionPressure` and `SequenceAmbiguous` are fixed by a new epoch. Owner mismatch and
  `RecordRebuildSent` misuse are plain `InvalidOperationException` caller bugs, never repair triggers.
- **Receiver.** `ClientDeltaRebuild(registry, view, options)` composes the existing `ClientReplicationView`.
  `ExpectEpoch(epoch)` authorizes an epoch learned from a reliable source (a mode offer or a keyframe header), and an
  unreliable datagram can never establish one. `TryApply(world, body, ...)` classifies the fixed header, then the
  epoch, then the sequence, then the named baseline, and only then decodes the body, so a stale packet is never
  decoded. `DeltaRebuildResult.Accepted` means the projection was reconstructed from its exact retained baseline (or
  from empty state for a keyframe), validated, retained and published. `DuplicateOrStale` and `MissingBaseline` change
  nothing, and `MissingBaseline` names the missing id for a repair request. `Invalid` is told apart by `LastFailure`:
  `MalformedPacket` (malformed bytes or an unknown built-in) and `CapacityExceeded` are terminal, while
  `SequenceAmbiguous` is ignored like a stale packet. `LatestAcceptedId` and `AckTarget` name the newest accepted id.
  `Reset()` retires the current epoch, so a later `ExpectEpoch` must name a greater one.
- **What an acknowledgement means.** The exact immutable projection was reconstructed, validated and retained, not
  merely that a packet arrived. The live world is never a baseline, because presentation may have changed it.
- **Publication.** An accepted projection replaces each entity's whole replicated component set: absent net ids
  despawn, surviving ids keep their ECS entity, components the projection no longer holds are removed, and every
  known component is installed even when its bytes match the previous publication. Only components whose codec is on
  the `Replicate` channel are touched, so game-local and Persist- or Migrate-only components stay. Unknown extension
  frames are retained as opaque baseline bytes and skipped for publication. Publication shifts the presentation
  buffers once and records no interpolation sample. The caller stamps one with `RecordInterpolationSample` after
  `Accepted` on its own ingest path.
- **Limits.** `DeltaRebuildOptions` holds the approved defaults, which are review defaults rather than measured
  capacity: 32 retained projections (at least 4), 2 MiB of retained backing bytes, 64 KiB complete keyframe objects,
  1,024 entities and 16,384 component frames per projection (zero-byte tags and opaque extensions included), and a
  31-send no-ack window. The effective window is `min(NoAckSendWindow, MaxRetainedProjections - 1)`. Retained bytes
  are the full length of every distinct backing array reachable from a retained projection, pins and the candidate
  included. Client presentation buffers hold independent copies outside that budget. `EnvelopeBytes` is an
  uninterpreted per-keyframe charge, 0 for standalone use. `Validate()` names the first invalid property and requires
  `MaxRetainedPayloadBytes >= 4 * MaxKeyframeBytes`, enough for a new projection beside the receiver's three pins.
  That floor protects pins only. To keep every unacknowledged committed send available for promotion, budget at least
  `(effective window + 1)` projections of the largest size you expect, which is the most a writer slot holds at once.
  Below that, a round trip spanning several sends is pruned before its acknowledgement arrives and the window forces a
  repair every 31 sends. The defaults hold 32 projections of up to 64 KiB each.

```csharp
// Standalone format 2 owner: routine deltas on an unreliable channel, keyframes reliable, exact acks back.
var options = new DeltaRebuildOptions();            // validated defaults
var writer = new ServerReplicator(registry);
ulong nextEpoch = 1;                                // monotonic for the owner's lifetime, never reset
writer.StartRebuild(slot, nextEpoch++, options);

void ServeTick()
{
    writer.Capture(world);
    if (writer.RebuildNeedsRepair(slot)) writer.StartRebuild(slot, nextEpoch++, options);   // fresh epoch, keyframe next
    ReplicationDeltaPacket packet = writer.BuildRebuildFor(slot, ownerNetId);
    bool handed = packet.IsKeyframe
        ? connection.SendReliable(packet.Id.Epoch, packet.Bytes)     // the receiver calls ExpectEpoch from this
        : connection.TrySendUnreliable(packet.Bytes);
    if (handed) writer.RecordRebuildSent(slot, packet.Id);
}

void OnAck(ReplicationPacketId id) => writer.AcknowledgeRebuild(slot, id);

// Receiver, one per connection, rebuilt with a fresh world and view on reconnect.
var view = new ClientReplicationView(registry);
var receiver = new ClientDeltaRebuild(registry, view, options);

void OnKeyframe(ulong epoch, ReadOnlyMemory<byte> body)
{
    receiver.ExpectEpoch(epoch);
    OnPacket(body);
}

void OnPacket(ReadOnlyMemory<byte> body)
{
    switch (receiver.TryApply(world, body, out ReplicationPacketId id, out ReplicationPacketId? missing, out string? error))
    {
        case DeltaRebuildResult.Accepted:
            view.RecordInterpolationSample(now);
            connection.SendAck(id);
            break;
        case DeltaRebuildResult.MissingBaseline:
            connection.RequestKeyframe(missing!.Value);              // the last accepted projection stays visible
            break;
        case DeltaRebuildResult.Invalid when receiver.LastFailure != DeltaRebuildFailure.SequenceAmbiguous:
            connection.Disconnect(error);                            // malformed or over capacity: terminal
            break;
    }
}
```

`KhaozEngine.NetWorld` composes all of this behind config switches, with negotiation, envelopes, cadence, keyframe
chunking and typed disconnects. Use these types directly only for a custom host.

Transport-free: snapshots/deltas are plain `byte[]`, shipped via your `KhaozEngine.Netcode` session layer
(`NetServer.Broadcast` / `NetClient` data events). Depends on `KhaozEngine.Ecs` only.

Full-state (`SnapshotWriter`), whole-world delta (`ServerReplicator`), per-client AoI delta
(`AoiDeltaReplicator`) and format 2 acknowledged rebuild (`ClientDeltaRebuild` with either writer) all ship.
