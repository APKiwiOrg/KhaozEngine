# Showcase B2c Implementation Plan (in-app networked walk room)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `RoomNet` room that runs an authoritative `WorldServer` + a local `WorldClient` + scripted bot clients all in-process (inline on the main thread, loopback UDP), then retire NetworkedWalkSample + NetworkedWalkServer.

**Architecture:** `RoomNet : GameScene, IGameScene3D`. OnUpdate steps the server (`Poll`+`Tick`), the local client (`Poll`+`SendInput`+`AdvancePresentation`), and each bot client, all on the main thread. Renders the local client's `Snapshot()` (local + replicated bots). NetworkedWalkSample stays as the port source until the final retirement task.

**Tech Stack:** C# net10.0, `KhaozEngine.NetWorld` (WorldServer/WorldClient/EntityRenderState), `KhaozEngine.Netcode.LiteNetLib` (transports), `KhaozEngine.Locomotion` (MoveCommand/MoveTuning), `KhaozEngine.Simulation`, `KhaozEngine.Game.Render3D`, `KhaozEngine.Terrain(.Render3D)`, xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-03-showcase-b2c-design.md`.
- Sample-only: **no `<KhaozEngineVersion>` bump, no CHANGELOG, no engine API change.** `IsPackable=false`. If the inline-server pattern needs an engine API that does not exist, STOP and raise it (engine-first rule).
- **Everything runs inline on the main thread** - NO background thread. Server + all clients are stepped in RoomNet.OnUpdate.
- Server + all clients MUST use the SAME terrain `GroundHeight`/`GroundNormal` delegate + `MoveTuning.Default` (bit-identical prediction).
- Port source (still present until Task 6): `NetworkedWalkSample/Program.cs` (client rendering + animator bridge) and `NetworkedWalkServer/Program.cs` (server construction + the Poll/Tick loop shape). Port mapping: those are `GameApp3D`/headless apps; in RoomNet, `Scene`/`sc` -> injected `_scene`, app members -> `Manager!.*`.
- Back-to-menu on Esc via `Manager!.Pop()`. No em-dashes or semicolons in shipped prose.
- Solution builds green after every task; retirement leaves no dangling networked reference.
- Confirmed signatures: `WorldServer(INetTransport, WorldServerConfig, Func<float,float,float> groundHeight, MoveTuning, Func<float,float,Vector3>? groundNormal = null, ...)`; `WorldClient(INetTransport, Func<float,float,float> groundHeight, MoveTuning, WorldClientConfig?, byte[]? token, Func<float,float,Vector3>? groundNormal, ...)` with `Poll(float dt)`, `SendInput(in MoveCommand)`, `AdvancePresentation(float)`, `Snapshot() -> IReadOnlyList<EntityRenderState>`, `LocalRenderState`, `NetStats`, `ConnectionState`, `Dispose()`; `MoveCommand(Vector2 move, bool run, float cameraYaw, bool jump=false)`; `EntityRenderState { NetId Id, Vector3 Position, bool IsLocal, bool Grounded, float VerticalVelocity }`; `LiteNetLibServerTransport(int port, ...)`, `LiteNetLibClientTransport(string host, int port, ...)`. Read each before use to confirm.
- Heavy concurrent dev: at retirement/merge integrate `origin/main` first, re-resolve `.slnx`/`.vscode/launch.json`/`README.md`.
- Commit subjects: `showcase: ...`.

---

### Task 1: csproj netcode refs + RoomNet skeleton

**Files:**
- Modify: `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (add 4 ProjectReferences)
- Modify: `KhaozEngine.Showcase/ShowcaseApp.cs` (register RoomNet)
- Create: `KhaozEngine.Showcase/RoomNet.cs` (skeleton)

- [ ] **Step 1: Add the netcode ProjectReferences**

In `KhaozEngine.Showcase.csproj` (it already has Game/Game.Render3D/Render3D/Terrain/Terrain.Render3D/Windowing/Physics), add:

```xml
    <ProjectReference Include="../KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj" />
    <ProjectReference Include="../KhaozEngine.Locomotion/KhaozEngine.Locomotion.csproj" />
    <ProjectReference Include="../KhaozEngine.Netcode.LiteNetLib/KhaozEngine.Netcode.LiteNetLib.csproj" />
    <ProjectReference Include="../KhaozEngine.Simulation/KhaozEngine.Simulation.csproj" />
```

- [ ] **Step 2: Register the room + write the skeleton**

In `ShowcaseApp.OnLoad`, after the 3D World room registration:

```csharp
Rooms.Add(("Networked walk", () => new RoomNet().Init(Scene, _white, small)));
```

Create `KhaozEngine.Showcase/RoomNet.cs`:

```csharp
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>Networked walk room: an authoritative WorldServer + a local WorldClient + scripted bot clients,
    /// all running in-process on the main thread over a loopback UDP socket. Demonstrates the predict / replicate /
    /// reconcile netcode against moving remote players without launching a separate server. Renders through the
    /// showcase's shared Scene3D (injected via Init). Esc returns to the menu.</summary>
    public sealed class RoomNet : GameScene, IGameScene3D
    {
        Scene3D _scene = null!;
        Texture2D _white = null!;
        SpriteFont _hud = null!;

        public RoomNet Init(Scene3D scene, Texture2D white, SpriteFont hud)
        {
            _scene = scene; _white = white; _hud = hud;
            return this;
        }

        public override void OnEnter() { /* Task 2+: terrain + server + client + bots */ }

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }
            // Task 2+: step server + clients here.
        }

        public void OnDraw3D(Scene3D scene) { /* Task 2+: terrain + characters */ }

        public override void OnDraw2D(SpriteBatch batch) { /* Task 5: net HUD */ }

        public override void OnExit() { /* Task 5/6: dispose clients + server + transports, free meshes */ }
    }
}
```

- [ ] **Step 3: Build + smoke**

Run: `dotnet build KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (succeeds).
Run: `KE_MAX_FRAMES=3 dotnet run --project KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (exit 0; menu lists a "Networked walk" row).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Showcase/KhaozEngine.Showcase.csproj KhaozEngine.Showcase/ShowcaseApp.cs KhaozEngine.Showcase/RoomNet.cs
git commit -m "showcase: netcode refs + empty RoomNet skeleton registered"
```

---

### Task 2: Terrain + inline server + local client (walkable)

Port NetworkedWalkSample's terrain/client + NetworkedWalkServer's server construction into RoomNet, INLINE. **Read both `NetworkedWalkSample/Program.cs` and `NetworkedWalkServer/Program.cs`** (both present).

**Files:** Modify `KhaozEngine.Showcase/RoomNet.cs`.

**Interfaces:** Consumes `TerrainField`/`TerrainCollision`/`TerrainPresets` (use the SAME preset NetworkedWalkServer + NetworkedWalkSample use so client + server ground match), `WorldServer`+`WorldServerConfig`+`LiteNetLibServerTransport`, `WorldClient`+`WorldClientConfig`+`LiteNetLibClientTransport`, `MoveTuning.Default`, `MoveCommand`, `FollowCamera3D`+`FollowCameraController`, `FixedTickHost`, `MeshPrimitives.Capsule`, `EntityRenderState`.

- [ ] **Step 1: OnEnter - build terrain + server + local client**

Add fields (terrain, `_serverTransport`/`_server`/`_serverClock`, `_clientTransport`/`_client`/`_clientClock`, `_camera`/`_camController`, `_capsule`, a `_port`). In OnEnter:
- Build the terrain (`TerrainField(TerrainPresets.Clearing())` - match NetworkedWalkServer's preset) + `TerrainCollision`.
- **Bind the server on a loopback port** with a small retry: try `new LiteNetLibServerTransport(port)` starting at 47750, incrementing on failure up to a few attempts, so a re-entered room does not race a stale socket. Store the bound port.
- Create the server: `new WorldServer(serverTransport, new WorldServerConfig { TickSeconds = 1f/30f, MaxPlayers = 8, SpawnPosition = slot => new Vector3(48f + slot*4f, 0f, 24f) }, terrain.GroundHeight, MoveTuning.Default, groundNormal: terrain.GroundNormal)`. (Match the config fields NetworkedWalkServer uses; single-cell `WorldServer`, not `ShardedWorldServer`.)
- Create the local client: `new WorldClient(new LiteNetLibClientTransport("127.0.0.1", _port), terrain.GroundHeight, MoveTuning.Default, new WorldClientConfig { TickSeconds = 1f/30f }, token: Encoding.UTF8.GetBytes("player"), groundNormal: terrain.GroundNormal)`.
- Camera: `FollowCamera3D` + `FollowCameraController`, `_scene.CameraOverride = _camera` (mirror NetworkedWalkSample).
- `_capsule = _scene.LoadMesh(MeshPrimitives.Capsule(...))` (fallback visual).
- Prime a few `server.Poll()` + `client.Poll(1f/30f)` cycles (a short loop) so the loopback connection establishes before the first rendered frame.

Match NetworkedWalkServer's `WorldServer` ctor args + NetworkedWalkSample's `WorldClient` ctor args EXACTLY (read them). Read `LiteNetLibServerTransport` to learn how a bind failure surfaces (exception vs flag) so the retry is correct.

- [ ] **Step 2: OnUpdate - step server then local client**

```csharp
var m = Manager!;
if (m.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }
// Server tick (authoritative).
_server.Poll();
_serverClock.Advance(dt, _ => _server.Tick(1f / 30f));
// Local client: input -> predict -> transmit, then presentation.
_client.Poll(dt);
Vector2 move = /* WASD, as NetworkedWalkSample builds it */;
bool run = /* Shift */; bool jump = /* Space latched per tick */;
var cmd = new MoveCommand(move, run, _camera.Yaw, jump);
_clientClock.Advance(dt, _ => _client.SendInput(cmd));
_client.AdvancePresentation(dt);
// Camera follows the local entity.
_camera.Target = _client.LocalRenderState.Position; // read LocalRenderState / LocalPosition (confirm the member)
_camController.Update(m.Input, dt);
_camera.AspectRatio = (float)m.FrameWidth / m.FrameHeight;
```
Port the exact input-building + jump-latching from NetworkedWalkSample.

- [ ] **Step 3: OnDraw3D - terrain + a capsule per entity**

Draw the terrain (port NetworkedWalkSample's terrain-chunk draw), then a capsule per entity in `_client.Snapshot()` at its position (feet offset), tinted local vs remote. (Animated characters are Task 4; capsules first.)

- [ ] **Step 4: Build + smoke (the netcode round-trip)**

Run: `dotnet build KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (succeeds).
Run: `KE_SHOWCASE_ROOM="Networked" KE_MAX_FRAMES=60 dotnet run --project KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` - exit 0. 60 frames gives the loopback connect + first snapshots time to complete. If the run prints the client's connection state, confirm it reaches Connected/Joined. Report the result.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Showcase/RoomNet.cs
git commit -m "showcase: RoomNet inline server + local client (walkable, capsule render)"
```

---

### Task 3: Scripted bot clients (replication visible)

**Files:** Modify `KhaozEngine.Showcase/RoomNet.cs`.

- [ ] **Step 1: A bot client + patrol**

Add a small `sealed class NetBot` holding a `WorldClient` + `LiteNetLibClientTransport` + a waypoint list + current-target index. `NetBot.Step(float dt)`: `client.Poll(dt)`, compute a `MoveCommand` toward the current waypoint (move vector = normalized XZ to target, run false, yaw toward the target, no jump; advance to the next waypoint when close), `client.SendInput(cmd)` at fixed tick (its own FixedTickHost), `client.AdvancePresentation(dt)`. `NetBot.Dispose()` disposes client + transport.

In RoomNet: create 2 bots in OnEnter (each `new WorldClient(new LiteNetLibClientTransport("127.0.0.1", _port), ..., token: "bot1"/"bot2", ...)`) with distinct patrol loops (a few waypoints around the spawn area). Step both in OnUpdate after the local client.

- [ ] **Step 2: Build + smoke + manual**

Build succeeds; `KE_SHOWCASE_ROOM="Networked" KE_MAX_FRAMES=90` exits 0 (server + 3 clients step; the bots move). Manual: enter the room, see 2 other capsules patrolling (replicated to your local client via the netcode).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Showcase/RoomNet.cs
git commit -m "showcase: RoomNet scripted bot clients (patrol, replicated remote players)"
```

---

### Task 4: Animated characters (replace capsules)

**Files:** Modify `KhaozEngine.Showcase/RoomNet.cs`.

Port NetworkedWalkSample's animated-character path: load the skinned glTF + clips (from `assets/character/Player.glb`, resolved from `AppContext.BaseDirectory` - it lives in the Showcase assets), the `ReplicatedCharacterAnimators` bridge that maps the `Snapshot()` entities to per-entity poses, and the `Scene3D.DrawSkinned` render per entity (capsule fallback if the asset fails). Read NetworkedWalkSample's `_animators`/`CharacterSample`/`ReplicatedCharacterAnimators.Update` + its OnDraw3D character loop and port it verbatim, mapping `sc`->`_scene`, app members->`Manager!.*`.

- [ ] **Step 1: Port the animator bridge + skinned draw**
- [ ] **Step 2: Build + smoke + manual** - build succeeds; `KE_SHOWCASE_ROOM="Networked" KE_MAX_FRAMES=90` exits 0 (report whether the character loaded); manual: you + the bots render as animated avatars, walking.
- [ ] **Step 3: Commit** - `git commit -m "showcase: RoomNet animated characters per replicated entity (from NetworkedWalkSample)"`

---

### Task 5: Net HUD + OnExit teardown + clean re-entry

**Files:** Modify `KhaozEngine.Showcase/RoomNet.cs`.

- [ ] **Step 1: Net HUD in OnDraw2D**

Draw a status line via `_hud`: `_client.ConnectionState` (Connected/Connecting/...), RTT + packet-loss from `_client.NetStats` (read `ClientNetStats` for the member names), and the entity count from `_client.Snapshot().Count`. Keep it one or two lines, top-left.

- [ ] **Step 2: OnExit teardown (shared Scene3D + live sockets)**

Dispose in order - clients first (so the server sees clean disconnects), then the server:
```csharp
if (!_built) return; _built = false;
foreach (NetBot b in _bots) b.Dispose();
_bots.Clear();
_client?.Dispose();          // disposes its owned transport
_server?.Dispose();          // disposes its owned transport, releasing the UDP port
_scene.UnloadMesh(_capsule);
if (_animated) _scene.UnloadSkinnedMesh(_characterMesh);
// unload any terrain chunk meshes RoomNet uploaded
_scene.CameraOverride = null;
```
Confirm `WorldClient.Dispose`/`WorldServer.Dispose` release their transports (read them; if a transport is caller-owned, dispose it explicitly). Add a `_built` guard so OnExit is safe before OnEnter finishes. On re-entry OnEnter rebinds a fresh port (Task 2's retry), so a not-yet-released socket does not block re-entry.

- [ ] **Step 3: Build + smoke + manual (re-entry is the key check)**

Build succeeds; `KE_SHOWCASE_ROOM="Networked" KE_MAX_FRAMES=90` exits 0. Manual: HUD shows Connected + RTT + count; Esc to menu; RE-ENTER the networked room and confirm it rebuilds (server rebinds, clients reconnect, bots move) with no leaked socket / port-in-use error; visit a 2D room after and confirm it renders.

- [ ] **Step 4: Commit** - `git commit -m "showcase: RoomNet net HUD + OnExit teardown for clean re-entry"`

Manual validation handoff (give the user this one-click boot command, do NOT run it yourself):

```bash
dotnet run --project /Users/antonio/KhaozEngine/.claude/worktrees/feature+showcase-b2c/KhaozEngine.Showcase/KhaozEngine.Showcase.csproj -c Debug
```

---

### Task 6: Retire NetworkedWalkSample + NetworkedWalkServer + integrate concurrent

**Files:** Delete `NetworkedWalkSample/`, `NetworkedWalkServer/`; modify `KhaozEngine.slnx`, `.vscode/launch.json`, `README.md`.

- [ ] **Step 1: Integrate concurrent work FIRST**

```bash
git fetch
git log --oneline origin/main -1
```
If `origin/main` advanced, `git merge origin/main`, resolve `.slnx`/`.vscode/launch.json`/`README.md`, rebuild the merged `.slnx`.

- [ ] **Step 2: Delete + deregister**

```bash
git rm -r NetworkedWalkSample NetworkedWalkServer
```
- `KhaozEngine.slnx`: remove both `<Project>` lines.
- `.vscode/launch.json`: remove the 3 networked configs (server + 2 clients) AND the "Networked Walk: server + 2 clients" compound.

- [ ] **Step 3: README**

Remove the "Networked" run block (server + client). Note the Showcase's rooms now include "Networked walk". Update the repo-layout block (drop `NetworkedWalkServer/ + NetworkedWalkSample/`). Leave MmoServerSample + SnapshotSample.

- [ ] **Step 4: Build gate + grep**

```bash
dotnet build KhaozEngine.slnx
grep -rn "NetworkedWalk" --include=*.json --include=*.md --include=*.slnx --include=*.csproj . | grep -v obj
```
Solution builds; grep returns no live references (CHANGELOG history + this branch's design/plan docs + provenance comments referencing NetworkedWalkSample as the port origin are acceptable).

- [ ] **Step 5: Full test + commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add -A
git commit -m "showcase: retire NetworkedWalkSample + NetworkedWalkServer (folded into the networked walk room)"
```

---

## Self-Review

**Spec coverage:**
- Goal 1 (in-process server + local client + bots inline) -> Tasks 2 + 3. ✓
- Goal 2 (local predicted player + replicated bots, animated) -> Tasks 2 (walk) + 3 (bots) + 4 (animated). ✓
- Goal 3 (net HUD) -> Task 5. ✓
- Goal 4 (teardown + re-entry) -> Task 5. ✓
- Goal 5 (retire networked samples, builds green) -> Task 6. ✓
- Concurrent-dev integration -> Task 6 Step 1. ✓
- Non-goals (sharding, MmoServer/Snapshot, engine change, version bump) -> absent. ✓

**Placeholder scan:** Task 1 + retirement are concrete. The server/client wiring (Task 2) gives exact ctor shapes from the Global Constraints + names the two port sources to read for the input-building / connection details; the render + animator ports (Tasks 3-4) reference NetworkedWalkSample as the authoritative source (present until Task 6) with the port mapping. The one genuinely tunable bit (loopback port + retry) is described with its mechanism.

**Type consistency:** `RoomNet.Init(Scene3D, Texture2D, SpriteFont)`, `_server`/`_client`/`_bots`, `WorldServer`/`WorldClient` ctor shapes + `MoveCommand`/`EntityRenderState` from Global Constraints, `NetBot.Step/Dispose`, `_scene` for the shared Scene3D, `Manager!.*` for app members - used consistently across Tasks 1-5.
