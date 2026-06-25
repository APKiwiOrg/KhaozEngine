# MMO Phase 1C — Entity replication (full-state first) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`).

**Goal:** Replicate a server ECS `World`'s entities + selected components to a client `World`: the server serializes a full-state snapshot each tick, the client applies it (spawn new, despawn gone, update existing), and interpolatable components are smoothed between snapshots. Staged per the chosen approach — **full-state now, baseline+delta later (1C-b)**.

**Architecture:** New `KhaozEngine.Replication` package depending on **`KhaozEngine.Ecs` only** (snapshots are opaque `byte[]`; the game ships them over its `NetServer`/`NetClient` from 1D — replication stays transport-free and headless-testable). A `ReplicationRegistry` erases each replicated component type `T` into serialize/deserialize closures over the public `World` API (`TryGet<T>`/`Set<T>`), keyed by a stable `ushort` type id. `NetId` is a tiny `IComponent` identifying an entity across the wire; the client keeps a `NetId`→`Entity` map.

**Tech Stack:** net10.0, C#, xUnit. `System.IO.BinaryWriter/Reader` over `MemoryStream` for the wire (compactness/quantization is a 1C-b concern). New package `KhaozEngine.Replication`.

**Spec:** `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md` (Layer 3). Builds on Phase 0 + 1D (same Phase 1 batch; **no version bump until the batch publishes** — per the standing batch policy).

---

## Snapshot wire format (full-state)

```
[entityCount : int32]
  per entity:
    [netId : int32]
    repeated while next type id != 0:
      [typeId : uint16 (>0)][component bytes — exactly what the codec wrote]
    [typeId : uint16 == 0]   // end-of-entity terminator
```

No per-component length prefix: each codec's read consumes exactly what its write produced. Type id `0` is reserved as the terminator, so registered ids start at `1`.

**Client apply (full-state semantics):** every netId present in the snapshot is spawned-if-new and updated; any netId in the client map but absent from the snapshot is despawned (full-state ⇒ absent means gone).

---

## File structure

**New package `KhaozEngine.Replication`:**
- `KhaozEngine.Replication.csproj` (refs `KhaozEngine.Ecs`)
- `NetId.cs` — `struct NetId : IComponent { int Value }`.
- `ReplicationRegistry.cs` — register `<T>(typeId, write, read, lerp?)`; holds serialize/deserialize/lerp closures.
- `SnapshotWriter.cs` — `Write(World) -> byte[]` over entities that have a `NetId`.
- `ClientReplicationView.cs` — `Apply(World, byte[])` (spawn/despawn/update) + `NetId→Entity` map; `Interpolate(World, float alpha)` for lerp-registered components.

**Tests (`KhaozEngine.Tests/Replication/`):** `ReplicationRoundTripTests.cs`, `ReplicationInterpolationTests.cs`.

**Modified:** `KhaozEngine.slnx`, `KhaozEngine.Tests.csproj`, `KhaozEngine.Server.csproj` (umbrella) — add the package.

---

## Tasks

- [ ] **T1 — Scaffold `KhaozEngine.Replication`** (csproj refs Ecs; slnx; Tests ref; Server umbrella ref). Build-verify empty package. Commit.
- [ ] **T2 — `NetId` + `ReplicationRegistry`.** Closures: `TrySerialize(World,Entity,BinaryWriter)->bool` (writes `[typeId][data]` if present), `Deserialize(World,Entity,BinaryReader)` (reads + `Set<T>`), optional `Lerp`. Test: register two component types, look up by id. Commit.
- [ ] **T3 — `SnapshotWriter.Write(World)`.** `ForEach<NetId>` → per entity write netId, each present codec, terminator. Test against a known world → byte length/shape. Commit.
- [ ] **T4 — `ClientReplicationView.Apply`.** Parse; spawn-if-new (set `NetId`), update; despawn absent. Tests: server→client convergence; a new entity spawns; a removed entity despawns. Commit.
- [ ] **T5 — Interpolation.** Registry `Lerp` closures; the view double-buffers each interpolatable component's previous+current parsed value per netId; `Interpolate(World, alpha)` writes `Lerp(prev,cur,alpha)`. Test: two snapshots, alpha=0.5 yields midpoint. Commit.
- [ ] **(1C-b, deferred)** baseline+delta encoding (per-client acked baselines via ECS change-tracking), bit-packing/quantization. Separate plan.

## Definition of done (1C-a)

- A server `World` of NetId entities round-trips to a client `World` over a `byte[]` snapshot: new entities spawn, gone entities despawn, shared entities update — asserted headlessly.
- Interpolatable components (those with a registered `Lerp`) interpolate between the last two snapshots at a render alpha.
- New `KhaozEngine.Replication` package wired into the solution, tests, and the Server umbrella. Committed locally; folded into the batched Phase 1 release later.
