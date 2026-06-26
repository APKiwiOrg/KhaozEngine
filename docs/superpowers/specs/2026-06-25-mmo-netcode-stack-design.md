# MMO / authoritative-multiplayer netcode stack — program design

**Date:** 2026-06-25
**Status:** Program spec (the map). Each sub-project (0A…3H) gets its own spec + implementation plan when built.
**Scope:** the **server + netcode track** only. See "Scope boundaries" below.

This is a top-level architecture + decomposition for turning KhaozEngine from a single-player / small-session
framework into one that can host an authoritative, persistent-world, MMO-scale multiplayer game. It is
deliberately a *program* spec: it defines the layering, the package layout, the data-flow contract, the
engine-vs-infra boundary, and a dependency-ordered phase plan. It does **not** specify per-package internals;
those live in each phase's own spec.

Motivation: the 2026-06-25 engine review scored MMO readiness 1–4/10 across every subsystem. The hard
primitives exist (client prediction + reconciliation, deterministic RNG, the reliable/unreliable channel
split, an archetype ECS with change-tracking, a spatial hash). What is missing is the *runtime systems built
on top of them*. This program builds that missing layer.

## Goals

- An **authoritative client-server** multiplayer foundation: the server simulates, clients predict their own
  actor and interpolate everything else, and every client reconciles to server truth.
- Scale seams from day one: replication, interest management, zoning/sharding, and a DB-backed world store, so
  the same stack serves a 4-player co-op session and a many-hundred-player shared world.
- Engine-side **seams + abstractions + single-process reference implementations + a headless test harness**, so
  the netcode is unit-testable without sockets and a game can stand a server up from the reference template.

## Non-goals (explicit)

- **The visual MMO-world track is out of scope here.** Frustum culling, world streaming, LOD, a tilemap/chunk
  renderer, GPU/threaded particles — all flagged by the review — are a **separate rendering plan**. A playable
  MMO needs both tracks; mixing them produces one unbuildable mega-spec. This document is server/netcode only.
- **Deterministic lockstep is not the model.** See "Authority model".
- **Production infrastructure is not engine code.** See "Engine vs infra boundary".
- No matchmaking service, no social/guild systems, no economy/anti-cheat heuristics — those are game systems
  that sit on top of the seams this stack provides.

## Authority model: authoritative server + prediction (not lockstep)

The server owns the simulation and is the single source of truth. Clients:

1. **predict** their own controlled entity locally from input (reusing `ClientPrediction` /
   `RemoteCommandQueue`), then **reconcile** to authoritative snapshots (rebase + replay unacked commands —
   already implemented);
2. **interpolate** remote entities between received snapshots for smoothness.

Why not the existing SpaceGame-style deterministic lockstep:

- Lockstep has no authority — consistency depends on every client staying bit-identical *forever*; any drift
  diverges permanently ("things not where they're supposed to be"). Authoritative state self-corrects every
  snapshot.
- Lockstep waits on the slowest peer's input before stepping, so it feels laggy under real latency unless
  rollback is added. Prediction + interpolation hide latency by construction.
- Lockstep trusts all clients (no server-side validation) and makes late-join, persistence, and large shared
  worlds hard — all core MMO needs.

**Determinism is relaxed, not removed.** Because only the server simulates, cross-machine bit-identical
floating point is no longer required, so the heavy `DeterministicFpScope` + state-hash machinery is not a
prerequisite here. `DeterministicRng` stays valuable for seeded spawns/loot. (A bandwidth optimization in the
data-flow section optionally re-introduces *deterministic client-side sub-simulation* for cheap mass entities;
that is local and does not reintroduce the lockstep constraint.)

## Engine vs infra boundary

The engine ships seams, abstractions, single-process reference impls, and the test harness. Production
deployment implements those seams and is the game's / ops' responsibility.

| Concern | `[engine]` ships | `[infra]` implements |
|---|---|---|
| Transport | `INetTransport` seam, LiteNetLib binding, in-memory loopback | a hardened public-internet relay if wanted |
| Auth | `IConnectionAuthenticator` seam + dev no-op | real account/token auth service |
| World store | `IWorldStore`/`ICharacterStore` seam + SQLite reference impl | Postgres/cloud DB cluster, backups |
| Zoning | single-process multi-zone host + handoff protocol | multi-process/distributed zone servers, discovery |
| Gateway | `IZoneRouter` seam + in-process router | load-balanced gateway deployment, autoscaling |

## Architecture — layered packages

| Layer | Package (new / changed) | Responsibility | Key types | Depends on |
|---|---|---|---|---|
| 0 · Transport | `KhaozEngine.Netcode` (+ `.LiteNetLib`) | byte transport seam + deterministic test transport | `INetTransport`, `NetConnectionId`, `NetEvent`, `LiteNetLibTransport`, `LoopbackTransport` | Netcode.Abstractions |
| 1 · Session | `KhaozEngine.Netcode` | connection lifecycle, handshake, join/leave, per-connection state | `NetServer`, `NetClient`, `IConnectionAuthenticator`, `NetConnectionState` | Layer 0 |
| 2 · Sim host | `KhaozEngine.Simulation` (new code package; the code-free `Server` metapackage pulls it in) | headless fixed-tick authoritative loop, command drain, sim step | `FixedTickHost`, `ITickSimulator` (exists), `TickClock` | Ecs, Netcode |
| 3 · Replication | `KhaozEngine.Replication` (new) | entity identity + baseline/delta snapshot sync + client apply | `NetId`, `ReplicationRegistry`, `[Replicated]`, `SnapshotWriter`/`SnapshotReader`, `ClientWorldView` | Ecs, Netcode |
| 4 · Interest (AoI) | `KhaozEngine.Replication` (+ spatial upgrade in `Collision`) | per-client relevancy set + enter/leave diff | `IInterestManager`, `GridInterestManager`, `InterestSet` | Layer 3, Collision |
| 5 · Zoning / shard | `KhaozEngine.Sharding` (new) | world partition, cross-zone handoff, gateway, instancing | `CellCoord`, `CellSim`, `ShardHost`, `ICellLink` | Layers 1–4 |
| 6 · World store | `KhaozEngine.WorldStore` (new) | async DB-oriented persistence seam + dirty flush + recovery | `IWorldStore`, `ICharacterStore`, `UnitOfWork`, `SqliteWorldStore` | Persistence, Ecs |
| 7 · Template | reference sample on the `Server` metapackage | reference dedicated-server wiring 0–6 | sample project | all |

Quantization (bandwidth): `UnitAxisQuantizer` exists; add `PositionQuantizer`, `RotationQuantizer`, and a
`BitWriter`/`BitReader` in `KhaozEngine.Netcode`, used by the snapshot codec.

## Data flow — one server tick

```
clients ──cmd──▶ RemoteCommandQueue                          (per connection, seq-ordered, hostile-bounded)
                      │
            FixedTickHost.Tick(dt)        ◀── fixed accumulator, render-independent
                      │
        server steps ECS World (authoritative)               (validated game semantics)
                      │
        ECS change-tracking ⇒ dirty (_added/_changed/_removed)
                      │
        ┌──────────── per client ────────────┐
        │ InterestManager.Filter(viewpoint)  │              (AoI: only relevant entities)
        │ SnapshotWriter: baseline+delta     │              (delta vs that client's acked baseline)
        │ quantize + bit-pack                │
        └──────────── send ──────────────────┘
                      │
client: SnapshotReader ⇒ apply to ClientWorldView
        ├─ own entity  ⇒ ClientPrediction.Reconcile (rebase+replay)
        └─ remote ents ⇒ interpolate between snapshots
```

**Bandwidth strategy** (the thing that makes it scale): AoI culling (don't send what a client can't see) +
baseline/delta encoding (send only what changed) + quantization + **decoupling snapshot rate from tick rate**
(e.g. tick 30–60 Hz, snapshot 10–20 Hz per client). **Optional hybrid** for cheap deterministic mass entities
(e.g. projectile swarms): replicate the *spawn event* reliably and let clients run a local deterministic
sub-sim for the bodies, instead of replicating each body. This is local determinism, not lockstep.

## Required upgrades to existing systems (folded in, not separate)

The review found three pre-existing gaps that become blocking here:

1. **`GameApp` is variable-dt / render-coupled.** Layer 2 (`FixedTickHost`) provides the engine's first
   headless fixed-tick loop, promoting SpaceGame's proven `FixedStepRunDriver` accumulator. Prerequisite for
   any authoritative server.
2. **`SpatialHashGrid` is rebuild-per-tick and radius-query-only.** Layer 4 needs a **persistent,
   incrementally-updated** spatial index with radius *and* AABB queries (entities move, AoI subscribes per
   cell). This is a `KhaozEngine.Collision` upgrade, additive to the existing grid.
3. **The ECS is strictly single-threaded.** A server ticking thousands of entities will become CPU-bound on
   one core. Job-scheduled systems (read/write component access declarations + a parallel `ForEach`) are a
   **Phase 2/3 prerequisite**, scoped in its own sub-project because it touches the ECS core broadly. Phases 0–1
   do not require it.

## Security posture

Inherits `docs/SECURITY-BASELINE.md` and extends it for the authoritative model: the server validates *game
semantics* (not just wire hygiene), reusing the existing hostile-input bounding (`RemoteCommandQueue` slot/seq
caps, quantizer clamps). Add per-connection rate limiting and a message-size ceiling at Layer 1, and the
`IConnectionAuthenticator` seam for token/account checks (real auth is `[infra]`). Clients never write
authoritative state; `IWorldStore` is server-only.

## Phased decomposition

Each phase ships on the shared `<KhaozEngine5xVersion>` line with headless tests over `LoopbackTransport`.
Sub-projects within a phase that have no dependency between them can run as concurrent worktrees.

### Phase 0 — Foundations (parallelizable)
- **0A · Transport seam.** `INetTransport`, `NetConnectionId`, `NetEvent`; `LoopbackTransport` (deterministic,
  in-memory) + `LiteNetLibTransport` binding. *Acceptance:* two `NetServer`/`NetClient` stubs exchange bytes
  over loopback in a headless test; LiteNetLib binding round-trips on a live socket smoke. Unblocks everything.
- **0B · Fixed-tick sim host.** `FixedTickHost` accumulator (fixed dt, catch-up cap, no dt scaling), drains a
  command source and steps an `ITickSimulator`/ECS `World`, fully headless. *Acceptance:* deterministic tick
  count for a given elapsed-time sequence; runs with no window/GPU. Independently useful (single-player fixed
  sim too).

### Phase 1 — Sync core
- **1C · Entity replication.** `NetId`↔`Entity`, `ReplicationRegistry`/`[Replicated]`, `SnapshotWriter`/
  `SnapshotReader` (baseline+delta from ECS change-tracking), per-client acked baselines, `ClientWorldView`
  apply + remote interpolation, predicted-entity handoff to `ClientPrediction`. *Needs 0A.* *Acceptance:* a
  server world of N entities replicates to a client world over loopback; client state converges to server
  state; a mispredicted local entity reconciles. (Bandwidth/AoI not yet — full set sent.)
- **1D · Session lifecycle.** `NetServer`/`NetClient` handshake, join/leave events, per-connection state,
  `IConnectionAuthenticator` (dev no-op). *Needs 0A.* *Acceptance:* clients connect/disconnect cleanly, slots
  recycle, a rejected auth is refused, all headless.

### Phase 2 — MMO scale
- **2E · Interest management + spatial-index upgrade.** Persistent incremental spatial index (radius + AABB) in
  `Collision`; `IInterestManager` + `GridInterestManager`; per-client enter/leave relevancy diff feeding spawn/
  despawn to 1C. *Needs 1C.* *Acceptance:* a client receives only entities within its interest radius; crossing
  the boundary produces exactly one spawn/despawn; bandwidth scales with *visible* entities, not world size.
- **2F · World-store seam.** `IWorldStore`/`ICharacterStore` async API, `UnitOfWork`, periodic dirty-entity
  flush + crash recovery, `MigrationChain` reuse for stored blobs; `SqliteWorldStore` reference impl. *Needs
  0B.* *Acceptance:* server character/world state survives a process restart via SQLite; concurrent writes are
  transactional; load is async and off the tick thread.
- **(2X · ECS job scheduling)** — ✅ **DONE**, scoped + built separately as its own program
  (`docs/superpowers/specs/2026-06-26-ecs-parallel-job-system-design.md`): worker-pool seam + parallel cell ticks
  (`7.41.0`), parallel `ForEach` + read/write access declarations (`7.42.0`); the conditional system scheduler was
  benchmark-gated and de-scoped. Touched the ECS core as expected.

### Phase 3 — Topology
- **3G · Zoning / sharding / instancing.** `IZone`/`ZoneHost`, cross-zone `ZoneHandoff` (WorldSerializer subset
  transfer), `IZoneRouter` gateway, `InstanceManager` for ephemeral dungeon instances. Single-process multi-zone
  is `[engine]`; multi-process distribution is `[infra]` implementing the seams. *Needs 1C, 1D, 2E, 2F.*
  *Acceptance:* an entity crossing a zone boundary migrates with no state loss in a single-process two-zone
  host; a client is routed to the correct zone; an instance spins up and tears down.
- **3H · Dedicated-server template.** Reference headless server sample wiring 0–6 (the deferred testbed slots in
  here). *Needs all.* *Acceptance:* `dotnet run` stands up a server a thin client connects to and plays against,
  headless-testable end to end.

## Intended first adopter / live testbed: SpaceGame

The testbed was deferred at planning time, but SpaceGame is the natural fit and should be penciled in as the
**intended first adopter**:

- It already uses the netcode primitives (`ClientPrediction`, `RemoteCommandQueue`, `Netcode.LiteNetLib`,
  `IChannelSplittable` DTOs) and an ASP.NET side-service, so the wiring surface is familiar.
- Its pivot to a **2.5D Terraria/Cuphead-like** game moves it into exactly the genre authoritative-server is
  built for (persistent/shared world, join/leave, world persistence, co-op), and out of the bullet-hell regime
  where lockstep's input-only bandwidth won.
- Migrating it validates the engine layer against a real game per the engine-first rule.

This stays a **downstream project**, kicked off after Phase 1 (the sync core) ships — not part of the engine
plan itself, which stays game-agnostic. The migration repurposes `DeterministicRng` (seeded spawns/loot) and
largely retires the lockstep `DeterministicFpScope` + state-hash machinery (a simplification).

## Open questions / risks

- **Bandwidth at entity scale.** AoI + delta + quantization + the deterministic-sub-sim hybrid are the
  mitigations; needs measurement once 1C/2E land (no perf test exists in the repo today).
- **Hosting model.** Listen-server (a client hosts) vs dedicated-only changes auth and trust assumptions; the
  seams support both, but the reference template should pick one to demonstrate (proposal: dedicated-only).
- **ECS threading scope.** Whether 2X is needed depends on target entity counts per zone; decide with a
  benchmark before Phase 2.
- **Multi-process distribution** (true horizontal sharding across machines) is `[infra]`; the engine proves the
  protocol single-process. Distributed discovery/orchestration is out of engine scope.

## Cadence

This program spec is the map. Build proceeds phase by phase; **each sub-project (0A…3H, 2X) gets its own
`docs/superpowers/specs/` design + `plans/` implementation plan** when it starts, sized to a single worktree and
release. No code is written against this document directly.
