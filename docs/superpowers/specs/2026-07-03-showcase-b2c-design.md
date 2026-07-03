# In-app Networked Walk room, retire the networked samples (sub-project B2c)

Status: approved design, pre-plan.
Parent effort: consolidate every windowed demo into `KhaozEngine.Showcase`. B1 (2D rooms), B2a (3D
World room), B2b (village + post-fx) shipped. This is **B2c**, the last consolidation step: an in-app
networked-walk room that runs an authoritative server + a local client (+ scripted bots) all in one
process, then retires NetworkedWalkSample + NetworkedWalkServer. After this, the Run/Debug dropdown is
just `KhaozEngine.Showcase` plus the two headless heads (MmoServerSample, SnapshotSample).

## Problem

Networked play is still two separate windowed/headless apps you launch by hand (server + one or two
clients). Goal: fold it into the showcase as a `RoomNet` that spins up an authoritative server + a
local client + a couple of scripted bots entirely in-process, so entering the room "just works" and
demonstrates the full predict/replicate/reconcile netcode against moving remote players.

## Goals

1. A `RoomNet` room: an in-process `WorldServer` + a local `WorldClient` + 1-2 scripted bot clients,
   all stepped inline on the main thread over a loopback UDP socket.
2. The local player walks (predicted/reconciled); the bots patrol and appear as replicated remote
   players (animated characters, capsule fallback), over the shared terrain.
3. A small net-status HUD (connection state, RTT/loss, player count).
4. Clean teardown + re-entry (dispose all clients/server/sockets, free the shared Scene3D resources).
5. NetworkedWalkSample + NetworkedWalkServer retired; solution builds green; no engine version bump.

Non-goals (deferred / out): a real remote multiplayer session, sharding (single-cell WorldServer is
enough), MmoServerSample / SnapshotSample (they stay - headless reference server + test harness), any
engine change.

## Design

### RoomNet (in `KhaozEngine.Showcase`)

`public sealed class RoomNet : GameScene, IGameScene3D`, parameterless ctor, shared `Scene3D` injected
via `Init(Scene3D scene, Texture2D white, SpriteFont hud)` (same pattern as the other rooms). All
networking runs INLINE on the main thread - no background thread.

Fields: a `WorldServer` + its `LiteNetLibServerTransport`; a local `WorldClient` + its
`LiteNetLibClientTransport`; a `List` of bot `WorldClient`s (+ transports) each with a patrol state;
the terrain (`TerrainField`/`TerrainCollision`, the SAME preset the server + all clients use so ground
matches for prediction); the follow camera; the animated-character mesh (+ capsule fallback) +
`ReplicatedCharacterAnimators`; per-endpoint `FixedTickHost`s.

**Port selection:** bind `LiteNetLibServerTransport` on a fixed loopback port (a Showcase-specific one,
e.g. 47750, distinct from the standalone server's 47700). If the bind fails (a stale socket from a
just-left room), increment and retry a few times, so leaving and re-entering the room does not race the
OS releasing the port.

**OnEnter:** build terrain; create the server (`WorldServer(serverTransport, config, terrain.GroundHeight,
MoveTuning.Default, groundNormal: terrain.GroundNormal)`, a small `WorldServerConfig` with the 30 Hz
tick + spawn positions + MaxPlayers); create the local client (`WorldClient(clientTransport,
terrain.GroundHeight, MoveTuning.Default, config, token: "player", groundNormal: ...)`); create 1-2 bot
clients the same way with their own tokens + a patrol waypoint list; set `scene.CameraOverride`; load
the character mesh + animators (capsule fallback); prime a couple of `Poll`/`Tick` cycles so the
connections establish before the first frame.

**OnUpdate (inline step order):**
1. Server: `server.Poll()`, then `serverClock.Advance(dt, _ => server.Tick(TickSeconds))`.
2. Local client: `client.Poll(dt)`, `clientClock.Advance(dt, _ => client.SendInput(realCmd))`,
   `client.AdvancePresentation(dt)` - `realCmd` from WASD + run + camera yaw + jump.
3. Each bot: `bot.Poll(dt)`, `bot.SendInput(patrolCmd)` (from its waypoint state machine),
   `bot.AdvancePresentation(dt)`.
4. Read the local client's `Snapshot()` -> feed `ReplicatedCharacterAnimators` + set the camera target
   to the local entity.
5. Esc -> `Manager!.Pop()`.

**OnDraw3D:** draw the terrain (a fixed set of chunks or the streamer, matching NetworkedWalkSample's
approach), then a character per entity in the local `Snapshot()` (animated pose when loaded, else a
capsule), tinted local vs remote.

**OnDraw2D:** a net-status HUD line via the injected `_hud` font - `client.ConnectionState`, RTT +
packet-loss from `client.NetStats`, and the entity/player count.

**OnExit (teardown - the Scene3D is shared, and there are live sockets):** dispose every bot client +
its transport, the local client + its transport, and the server + its transport (order: clients first,
then server, so the server sees clean disconnects); unload the room's meshes (terrain chunks, character,
capsule); clear `scene.CameraOverride`. Guard against being called before OnEnter finished. A re-entered
room rebuilds fresh (fresh port bind).

**Register:** `Rooms.Add(("Networked walk", () => new RoomNet().Init(Scene, _white, small)))`.

RoomNet is its own scene (not a mode of Room3D): Room3D drives the player via a local
`CharacterController3D`, RoomNet drives it via the networked `WorldClient` predict/reconcile path. It
ports NetworkedWalkSample's client + adds the inline server + bots. Small terrain/chunk helpers may be
shared with Room3D if it falls out cleanly, but the scenes stay separate.

### Retirement

- Delete `NetworkedWalkSample/` and `NetworkedWalkServer/`.
- `KhaozEngine.slnx`: remove both `<Project>` lines.
- `.vscode/launch.json`: remove the 3 networked configs (server + 2 clients) and the "Networked Walk:
  server + 2 clients" compound.
- `README.md`: remove the "Networked" section (the server + client run block); the Showcase's rooms now
  include "Networked walk". Update the repo-layout block (drop `NetworkedWalkServer/ + NetworkedWalkSample/`).
- Grep for any remaining live reference to the deleted projects (docs, csproj, launch.json) and repoint
  or remove.

## Verification

- Build gate: `dotnet build KhaozEngine.slnx` green after the room + retirement (no dangling networked
  reference; the Showcase now references the netcode packages it needs - `KhaozEngine.NetWorld`,
  `KhaozEngine.Netcode.LiteNetLib`, `KhaozEngine.Locomotion`, `KhaozEngine.Simulation` - inferred from
  NetworkedWalkSample.csproj).
- Headless: `KE_SHOWCASE_ROOM="Networked" KE_MAX_FRAMES=60 dotnet run --project KhaozEngine.Showcase/...`
  exits 0 - the server + local client + bots stand up, connect over loopback, and step the full
  round-trip for ~2 seconds without crashing (60 frames gives the loopback connect + first snapshots
  time to complete; a 6-frame run may not finish connecting, so use ~60).
- Manual: enter the room, walk (WASD + mouse + jump), watch the 1-2 bots patrolling as replicated
  players, the net HUD shows Connected + RTT + player count; Esc to menu; RE-ENTER cleanly (fresh
  server binds, no leaked socket, 2D rooms still render).
- Full suite green.

## Concurrent-dev note

B2c touches shared hotspots: `KhaozEngine.slnx`, `.vscode/launch.json`, `README.md`, and (adds netcode
ProjectReferences to) `KhaozEngine.Showcase.csproj`. Before merging back: `git fetch`; if `main`
advanced, merge it in first and re-resolve those, rebuild the merged `.slnx`, then merge back clean. No
`<KhaozEngineVersion>` bump (sample-only). If the inline-server pattern needs an engine API that does
not exist (e.g. a clean steppable-server host the sample cannot express), STOP and raise it
(engine-first rule) rather than working around it.

## Follow-on

After B2c, the interactive consolidation is complete: `KhaozEngine.Showcase` is the single windowed app
(2D, GUI, input, mini-game, 3D world with a village, networked walk). Remaining engine-roadmap items
are independent of the showcase: visual fidelity #3b (water shader) and #3c (shadows + lighting). The
two headless heads (MmoServerSample, SnapshotSample) can optionally have their `.vscode` launch configs
stripped for a truly single-entry Run/Debug dropdown, without deleting the projects.
