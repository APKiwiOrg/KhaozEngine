# Networked overworld design (shared movement core + `WorldClient` + a networked walk demo)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Program: MMO overworld render-scale track, sub-project 5 (the two halves meet)

## Context

Terrain (`7.43.0`), the walkable slice (`7.44.0`/`7.45.0`), and prop scatter + the asset pipeline
(`7.46.0`) are shipped — you walk a forested clearing solo, in-engine. Separately, the **server /
authoritative-multiplayer stack is complete** (`Netcode`, `Replication`, `Sharding`,
`MmoServerSample`). They have **never met**: every client-side piece so far is single-player local.
This sub-project connects them — the first real MMO moment, and the validation of the biggest
unproven assumption in the whole program (that the shipped netcode actually drives the new 3D
client). Goal: **two clients see each other walking the same terrain.**

Prior specs: `2026-06-27-terrain-system-design.md`, `2026-06-27-walkable-slice-design.md`,
`2026-06-27-prop-scatter-design.md`. Program reference repo:
`https://github.com/levy-street/world-of-claudecraft`.

### This is wiring, not building — the netcode already exists

- `ClientPrediction` (`Predict` + `Reconcile` + `PredictionSettings`) — client prediction +
  server reconciliation.
- `RemoteCommandQueue` — the client → server input/command channel.
- `ITickSimulator` / `IPredictedState` — the seams a movement sim plugs into (server authoritative
  and client prediction run the *same* sim).
- `ServerReplicator` / `ClientReplicationView` (`Apply`/`ApplyDelta`) + `InterestGrid` AoI.
- `NetServer` / `NetClient`, `LoopbackTransport` (tests) + LiteNetLib UDP transports (demo).
- `MmoServerSample` already stands up `FixedTickHost` + `NetServer` + `ShardHost`.
- `TerrainCollision.GroundHeight` (shipped) is the server's authoritative ground.

### Locked decisions (from brainstorming)

1. **Single authoritative `World`** (no multi-cell handoff yet) — multi-cell sharding folds in later
   with world streaming. Keeps this slice about the client↔server movement/replication path.
2. **Client prediction + reconciliation** via the shipped `ClientPrediction`.
3. **Real transport**: LiteNetLib on localhost for the demo; `LoopbackTransport` for headless tests.
4. **Shared render-free movement core**: extract the movement step into a new `KhaozEngine.Locomotion`
   leaf so the server sim and the client prediction run identical code.
5. **Standalone headless server + two client instances** on localhost (the real MMO model), not a
   client-hosted listen-server.
6. **Props are not replicated** — each client scatters them deterministically from the seed
   (identical everywhere), so only players consume bandwidth.
7. **Engine-first**: the movement core, the movement simulator, and the client glue are reusable
   engine features; only the demo server + windowed client are throwaway.

## Components

### 1. `KhaozEngine.Locomotion` — new render-free leaf (→ Foundation umbrella)

The shared movement core, depending only on `Primitives`/`System.Numerics`. Referenced by the
render-side controller, the server sim, and client prediction alike.

```csharp
public readonly struct MoveCommand { public Vector2 Move; public bool Run; public float CameraYaw; public float Dt; }
public struct PlayerMoveState : IPredictedState { public Vector3 Position; /* ... */ }

public static class CharacterMovement
{
    // pure step: apply a command, clamp Y to the ground delegate. Server + prediction both call this.
    public static PlayerMoveState Step(PlayerMoveState s, in MoveCommand cmd, Func<float,float,float> groundHeight, MoveTuning t);
}
```

`CharacterController3D` (Game.Render3D, shipped in the walkable slice) is **refactored to wrap
`CharacterMovement.Step`** so local feel and networked feel are the same code.

### 2. Networked-world layer — reusable engine package (render-free)

Holds both sides of the wiring (proposed `KhaozEngine.NetWorld`; the implementer may instead place
each side in an existing client/server netcode package if that edge is cleaner — but it must stay
render-free and reusable, not live in the sample). Depends on `Locomotion` + `Netcode` +
`Replication`.

- **`PlayerMoveSimulator : ITickSimulator`** (server): each tick, drain queued `MoveCommand`s per
  player from `RemoteCommandQueue`, run `CharacterMovement.Step` (ground-clamp via a
  `TerrainCollision`-backed delegate), write authoritative `PlayerMoveState`. Replicated by
  `ServerReplicator` + `InterestGrid`.
- **`WorldClient`** (client): wraps `NetClient` + `ClientReplicationView` + `ClientPrediction`.
  `Connect`; per frame: send the local `MoveCommand`, feed `ClientPrediction` (predict the local
  avatar via `CharacterMovement.Step`, reconcile against snapshots), `Apply` replication for remote
  entities, and expose `IReadOnlyList<EntityRenderState>` where
  `EntityRenderState { NetId Id; Vector3 Position; bool IsLocal; }`. Render-free; the sample renders it.

### 3. Demo — thin server Exe + networked windowed client (both `IsPackable=false`)

- **Server**: a headless single-`World` Exe — terrain (`TerrainPresets.Clearing()`), the
  `PlayerMoveSimulator` on a `FixedTickHost`, `NetServer` + `ServerReplicator` + `InterestGrid`,
  LiteNetLib, `AllowAllAuthenticator`. Spawns a player entity per connection.
- **Client**: a networked variant of `TerrainWalkSample` (e.g. a `--connect <host>` mode, or a
  `NetworkedWalkSample`). Builds the same terrain + deterministic prop scatter locally, runs a
  `WorldClient`, drives the local player via input → `MoveCommand`, renders a capsule per
  `EntityRenderState` (local predicted with `FollowCamera` on it; remote interpolated).
- **To see another player**: launch the server, then two client instances on localhost.

## Data flow

```
client input → MoveCommand → RemoteCommandQueue → NetServer
   → PlayerMoveSimulator (authoritative, ground-clamp via TerrainCollision) on FixedTickHost
   → ServerReplicator / InterestGrid → ClientReplicationView (remote) + ClientPrediction.Reconcile (local)
   → WorldClient.EntityRenderState[] → sample renders a capsule per entity
```

## Testing (headless, `LoopbackTransport`, no GPU/window)

- **`CharacterMovement.Step`** — command + ground delegate → expected position; deterministic.
- **`PlayerMoveSimulator`** — drains commands, ground-clamps, advances authoritative state per tick.
- **Round-trip over Loopback** — a client's `MoveCommand` moves its server entity and comes back via
  replication; **two clients each see the other's entity move**; `ClientPrediction` reconciles an
  injected misprediction (local converges to server).
- **`WorldClient`** — exposes the right `EntityRenderState` set (local flagged; remotes applied).

## Scope

### In scope

- `KhaozEngine.Locomotion` (new leaf): `CharacterMovement`, `MoveCommand`, `PlayerMoveState`,
  `MoveTuning`.
- Refactor `CharacterController3D` to wrap `CharacterMovement`.
- Networked-world layer: `PlayerMoveSimulator` + `WorldClient` + `EntityRenderState` (render-free,
  reusable).
- Thin headless server Exe + networked windowed client (samples, `IsPackable=false`).
- Headless tests (Loopback); LiteNetLib localhost for the manual two-client demo.
- Release: **minor** bump. Full doc sweep because a package is **added** (`Locomotion`, and the
  net-world package if a new one): README package catalog + repo-layout, `CLAUDE.md` package map +
  umbrella, `docs/CONSUMERS.md`, `docs/USING-KHAOZENGINE.md`, the 3 guard declarations,
  `CHANGELOG.md` + `CHANGENOTES.md`. End with boot commands for the server and the client.

### Out of scope (named so they are not forgotten)

- **Multi-cell sharding / handoff** — single `World` here; pairs with world streaming next.
- **Combat, chat, names/UI, NPCs/creatures.**
- **Persistence** (`WorldStore`) — players are ephemeral this slice.
- **Prop-as-entity** — props stay client-side deterministic scatter (no replication).
- **Auth** beyond `AllowAllAuthenticator`; **animation** (capsules; needs glTF clip playback).

## Engine-first placement (decisions)

- `CharacterMovement` core → **new `KhaozEngine.Locomotion` leaf** (Foundation). Confirmed.
- `PlayerMoveSimulator` + `WorldClient` → reusable render-free engine layer (proposed
  `KhaozEngine.NetWorld`, Server umbrella; implementer may merge into an existing client/server
  netcode package if cleaner — render-free + reusable is the requirement).
- Demo server + networked client → samples (throwaway). Standalone server + 2 clients run model.

## Open items to confirm during implementation

- Exact home/name of the net-world layer (new package vs into `Replication`/`Netcode`).
- `MoveTuning` defaults shared with the walkable slice's `CharacterController3D` (one source of truth).
- Tick rate, prediction settings, snapshot/AoI radius for a single small world (start from
  `MmoServerSample`'s values).
- Remote-player interpolation (snapshot buffer) — keep simple; the slice runs on localhost.
- Whether the networked client is a `--connect` mode of `TerrainWalkSample` or a separate sample.

## The overworld program (for orientation)

1. ✅ Asset foundation (folded into props). 2. ✅ Terrain. 3. ✅ Walkable slice. 4. ✅ Prop scatter.
5. **Networked overworld — this spec.**
6. World streaming + multi-cell sharding — load/unload chunks and hand players across cells (builds
   directly on this + the streaming-ready terrain/scatter).
7. Procedural dungeon generator — parallel track.
Later polish: PBR splat textures + water; glTF animation-clip playback → animated characters.
