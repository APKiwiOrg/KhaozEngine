# Walkable overworld slice design (`FollowCamera3D` + `CharacterController3D` + `TerrainWalkSample`)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Program: MMO overworld render-scale track, sub-project 2 of 6 (walk on the terrain)

## Context

Sub-project 1 (terrain) shipped at `7.43.0` (`KhaozEngine.Terrain` +
`KhaozEngine.Terrain.Render3D`; spec `docs/superpowers/specs/2026-06-27-terrain-system-design.md`).
This sub-project makes that terrain **walkable**: the first time the engine runs in a window and you
move a character through your own world, instead of looking at Blender renders. It validates terrain
*feel* in-engine (scale, slope, ground-clamp) before investing in streaming or props.

Reference repo for the wider program: `https://github.com/levy-street/world-of-claudecraft`.

### Existing engine pieces (reused, not rebuilt)

- **Windowed-sample pattern**: `GuiSample` / `Render2DSample` / `SnapshotSample` / `MiniGame` are
  windowed `Exe` samples using `AppWindow.Run` + `Scene3D`. The new sample models on those.
- **Terrain render hooks** (shipped): `Scene3D.LoadTerrainChunk` / `DrawTerrainChunk` / `PickLod`.
- **Camera precedent**: `IsoCamera3D` + `IsoCameraController` (Render3D). Hardpoint consumes these
  (`HardpointGame.cs` — iso/strategy pan + scroll-zoom + `Frame` + `ScreenToGround`/`WorldToScreen`).
  That is a *different archetype* from a third-person follow camera (it pans a static board; it does
  not orbit behind a moving target), so there is nothing to promote — but the new follow camera is
  built as a **sibling** of `IsoCamera3D`, reusing its view/projection math, `Forward`/`Eye`,
  `AspectRatio`, and `ScreenToGround`/`WorldToScreen`, and `FollowCameraController` mirrors
  `IsoCameraController`'s input pattern (reads the `InputState` snapshot, scroll-zoom, drag-orbit,
  press-origin-correct).
- **Input rule**: only `AppWindow` touches the windowing input statics; everything else reads the
  immutable `InputState` snapshot via `InputManager`. The new controllers follow this.

### Locked decisions (from brainstorming)

1. **Third-person follow camera** (orbit behind a moving character, mouse-look). MMO default.
2. **Greybox capsule character** (the 1.8 m reference shape). The engine has **no glTF
   keyframe-clip playback** (only skinning + `ProceduralChainSolver`), so a real character cannot
   walk-cycle yet. An animated character is a later sub-project that first needs a glTF-animation
   engine feature.
3. **Local / direct movement** in the sample — no netcode. Wiring movement through the authoritative
   server is a later concern; this sample only validates terrain feel.
4. **Fixed chunk grid** around the origin — no streaming.
5. **Engine-first placement** (reusable, not bespoke in the sample): follow camera in `Render3D`,
   character controller in `Game.Render3D`; the sample app is the only throwaway part.

## Components

### `FollowCamera3D` + `FollowCameraController` — `KhaozEngine.Render3D`

Sibling of `IsoCamera3D` / `IsoCameraController`.

- **`FollowCamera3D`** (pure math, headless-testable): a target position, `Yaw`, `Pitch`,
  `Distance`; computes `Eye` = target − (orbit direction × distance) + height offset, and the
  view/projection matrices. Reuses `IsoCamera3D`'s conventions (`AspectRatio`, `Forward`, `Eye`,
  `ScreenToGround`/`WorldToScreen`). Pitch clamped to a sane range; distance clamped to
  `[MinDistance, MaxDistance]`.
- **`FollowCameraController`**: takes the `InputState` snapshot + `dt`; mouse drag updates
  `Yaw`/`Pitch`, scroll updates `Distance`, each clamped. No input statics. Mirrors
  `IsoCameraController`.

### `CharacterController3D` — `KhaozEngine.Game.Render3D`

Terrain-agnostic locomotion. **Does not reference `KhaozEngine.Terrain`.**

```csharp
public sealed class CharacterController3D
{
    public Vector3 Position { get; }
    public float WalkSpeed, RunSpeed;
    // ground/slope supplied as delegates so the controller stays terrain-agnostic
    public void Update(in InputState input, float dt, float cameraYaw,
                       Func<float,float,float> groundHeight,
                       Func<float,float,Vector3>? groundNormal = null);
}
```

- WASD moves on the XZ plane **relative to `cameraYaw`** (forward = camera's look direction projected
  onto XZ); diagonals normalized; walk/run speed (shift = run).
- Each frame, `Position.Y` is clamped to `groundHeight(x, z)` (with an optional capsule-half-height
  offset so feet sit on the ground).
- Optional: reject a step whose `groundNormal` slope exceeds a `MaxSlope` (kept simple — slide or
  stop, no physics).

### `TerrainWalkSample` — new windowed Exe (`IsPackable=false`)

The only throwaway part. Modeled on `GuiSample`.

- On start: build a fixed N×N grid (e.g. 7×7) of terrain chunks around the origin from
  `TerrainPresets.Clearing()` via `Scene3D.LoadTerrainChunk`; wrap the field in `TerrainCollision`.
- Spawn the 1.8 m capsule (from `MeshPrimitives`) on the ground at origin.
- Per frame (`AppWindow.Run`): `InputManager.Update(input)` →
  `CharacterController3D.Update(input, dt, camera.Yaw, terrain.GroundHeight)` →
  `FollowCameraController.Update(input, dt)` with the camera target = character position →
  draw the terrain chunks (`DrawTerrainChunk`) + the capsule.
- Controls: WASD move, mouse-drag orbit, scroll zoom, shift to run.
- Ends with a one-click boot command for the user (windowed manual feel-check).

## Data flow

```
AppWindow → InputState → InputManager
   → CharacterController3D(input, dt, cam.Yaw, terrain.GroundHeight)  → character position
   → FollowCameraController(input, dt) → FollowCamera3D(target = character)  → view/proj
   → Scene3D: DrawTerrainChunk[]  + capsule
```

## Testing (headless, in `KhaozEngine.Tests`)

- **FollowCamera3D**: target + yaw + pitch + distance → expected `Eye`/view matrix; pitch clamps at
  its limits; distance clamps at `Min`/`Max`. Camera always looks at the target.
- **FollowCameraController**: drag delta changes yaw/pitch by the expected amount; scroll changes
  distance; all clamped; no movement on no input.
- **CharacterController3D**: WASD produces camera-relative XZ motion; diagonal normalized; idle = no
  move; speed scales with `dt`; run > walk; `Position.Y` equals the ground delegate each frame;
  (optional) a step past `MaxSlope` is rejected.
- No GPU device in tests. The windowed sample is not unit-tested; the camera + controller are.

## Scope

### In scope

- `FollowCamera3D` + `FollowCameraController` (`Render3D`).
- `CharacterController3D` (`Game.Render3D`).
- `TerrainWalkSample` windowed app (`IsPackable=false`).
- Headless tests for camera + controller.
- Release: **minor** version bump (additive public API in two existing packages — no new *package*,
  so no package-catalog churn). Update `Directory.Build.props`, `CHANGELOG.md` + `CHANGENOTES.md`,
  the 3 guard declarations, and `docs/USING-KHAOZENGINE.md` (a usage section for the follow camera +
  character controller). `dotnet pack` → `local-feed`, tag, push. End with the sample boot command.

### Out of scope (named so they are not forgotten)

- **Animation / walk-cycle** — needs a glTF animation-clip-playback engine feature first (a future
  sub-project; the capsule is static).
- **Netcode-driven movement** — the sample is local; authoritative-server movement comes when the
  client is wired to the netcode stack.
- **Chunk streaming** — fixed grid here; streaming is sub-project 3.
- **Prop / obstacle collision** — terrain ground-clamp only.
- **Jump / gravity / physics** beyond ground-clamp.

## Open items to tune during implementation

- Default camera distance / pitch limits / orbit + zoom sensitivity (expose as fields; tune by feel).
- Capsule half-height offset so feet sit exactly on the ground.
- Walk/run speeds (start ~3 m/s walk, ~6 m/s run; tune).
- Chunk grid size (7×7 of the shipped chunk size is a starting point).
- Input bindings (WASD + mouse-drag + scroll + shift) — confirm against `InputManager` key/button API.

## The overworld program (for orientation)

1. Asset/render foundation — glTF kit ingest + scale-normalize/validate + GPU instancing + LOD.
2. ✅ Terrain — shipped `7.43.0`.
3. **Walkable slice — this spec.**
4. World streaming / culling — load/unload chunks around the player, wired to `Sharding`.
5. Prop scatter — coordinate-hash scatter onto terrain, instanced.
6. Procedural dungeon generator — parallel track.

(Character + camera built here are the reusable basis for the later world-client glue.)
