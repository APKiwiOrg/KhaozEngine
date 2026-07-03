# 3D World room - port TerrainWalkSample into KhaozEngine.Showcase (sub-project B2a)

Status: approved design, pre-plan.
Parent effort: consolidate every windowed/interactive demo into the one `KhaozEngine.Showcase` app.
B1 (hub + 2D/GUI/Input/MiniGame rooms) shipped. This is **B2a**: the walkable single-player 3D
World room. Follow-ons: **B2b** (clearing + CC0 houses + post-fx, retire Render3DSample) and **B2c**
(networked walk room with an in-process server, retire NetworkedWalkSample + NetworkedWalkServer).

## Problem

The showcase hub currently hosts only 2D rooms (`GameApp` + 2D `GameScene`s). The 3D demos
(TerrainWalkSample, Render3DSample) are still separate windowed apps. Goal: fold the walkable streamed
3D overworld into the showcase as a `Room3D`, retiring TerrainWalkSample. This also converts the hub
to a 3D-capable app so B2b/B2c can build on it.

## Goals

1. `ShowcaseApp` becomes a `GameApp3D` (shared `Render3DSurface`/`Scene3D` + a 3D pass before the 2D
   HUD), without disturbing the four existing 2D rooms.
2. A `Room3D : GameScene, IGameScene3D` that faithfully ports TerrainWalkSample's walkable world:
   streamed terrain, character controller + animated avatar (capsule fallback), follow camera,
   Bepu physics, prop scatter + collision, the procedural textured stone block, the F2 collision
   overlay, HUD, and the existing render toggles.
3. Retire TerrainWalkSample without breaking the networked samples that share its assets.
4. Solution builds green; the app honors `KE_MAX_FRAMES` for headless smoke.

Non-goals (deferred): the `FlattenFeature` town clearing + CC0 houses + fuller Render3DSample post-fx
(B2b), the networked room (B2c), and any engine version bump (sample-only, unless a genuine engine
gap surfaces - then pause per the engine-first rule).

## Design

### App: GameApp3D

`ShowcaseApp` changes its base from `GameApp` to `GameApp3D` (`KhaozEngine.Game.Render3D`) and adds:

```csharp
protected override void OnDraw3D(Scene3D scene) => _scenes.Draw3D(scene);
```

`GameApp3D` owns the `Render3DSurface`, calls `Scene.Begin()`, runs `OnDraw3D`, then `Surface.Render`,
before the existing 2D pass. `SceneManager.Draw3D` dispatches only to visible scenes implementing
`IGameScene3D`, so the menu and the four 2D rooms render no 3D (their opaque 2D backdrops cover the
empty 3D frame). The 2D rooms are otherwise unchanged.

`csproj` gains `ProjectReference`s: `Game.Render3D`, `Render3D`, `Terrain`, `Terrain.Render3D`,
`Physics`, `Physics.Bepu` (inferred from TerrainWalkSample.csproj).

### Room3D (port of TerrainWalkSample)

`public sealed class Room3D : GameScene, IGameScene3D`, parameterless ctor, wired via the established
`Init(...)` pattern. The shared `Scene3D` is injected (ShowcaseApp passes its `Scene`), plus the white
texture + a HUD font:

```csharp
public Room3D Init(Scene3D scene, Texture2D white, SpriteFont hud);
```

Lifecycle maps TerrainWalkSample's phases onto scene hooks:

- **`OnEnter` (was OnLoad)**: build the world into the shared `Scene3D` -
  `TerrainField(TerrainPresets.BoundedClearing())` (a contained rim-walled area - a better bounded
  showcase than endless streaming), `TerrainCollision`, `BepuPhysicsWorld`, load the prop kit
  (`AssetManifest`/`PropLoader`) + bake collisions, `CharacterController3D` + the animated character
  (skinned glTF + clips, capsule fallback), `FollowCamera3D` + `FollowCameraController` set as
  `scene.CameraOverride`, the procedural textured stone block
  (`MeshOps.WithTangents`+`PropMaterialPresets.Procedural`), the blacksmith collision proxy
  (`PropCollisionFormat.Read`) + the F2 `CollisionShapeOverlay`, the splat terrain material, then
  `Scene3DChunkSink` + `TerrainStreamer` and prime the initial ring.
- **`OnUpdate`**: `_physics.Step`, `_character.Update`, animated-character update, `_streamer.Update`,
  camera sync/aspect, the render-debug toggles TerrainWalkSample already has (outline / starfield /
  cel), F2 overlay toggle, and **Esc -> `Manager!.Pop()`** back to the menu.
- **`OnDraw3D(scene)`**: chunk sink draw, platform box, textured prop, character (skinned or capsule),
  collision overlay.
- **`OnDraw2D(batch)`**: the collision-legend HUD.
- **`OnExit` (critical - the `Scene3D` is shared across rooms)**: dispose the streamer + physics,
  unload the loaded ring + any room-loaded meshes, **clear `scene.CameraOverride`**, and **reset
  `scene.Post` to defaults**, so the menu/2D rooms render cleanly and re-entering the room rebuilds
  from scratch. (TerrainWalkSample did this teardown in `OnUnload`; here it must happen on every room
  exit, not just app shutdown.)

Register in `ShowcaseApp.OnLoad`: `Rooms.Add(("3D World (walk)", () => new Room3D().Init(Scene, _white, small)));`

Each logical piece stays in its own region/helper so the file is navigable; if Room3D grows unwieldy,
split the world-build helpers (terrain, physics/props, character, streaming) into a small
`Room3DWorld` helper that Room3D owns.

### Assets (move, do not duplicate) + keep the networked samples building

TerrainWalkSample's assets are shared: `NetworkedWalkSample.csproj` includes
`../TerrainWalkSample/assets/props/**` and `.../character/**` via `LinkBase`. So:

- Move `TerrainWalkSample/assets/*` (the CC0 prop kit + `props.manifest.json`, the CC0 character
  `Player.glb` + CREDITS, `blacksmith_proxy.coll`) into `KhaozEngine.Showcase/assets/`, with the
  matching `<None Include=... CopyToOutputDirectory ...>` items + CREDITS in `KhaozEngine.Showcase.csproj`.
- Repoint `NetworkedWalkSample.csproj`'s two `LinkBase` includes to `../KhaozEngine.Showcase/assets/props/**`
  and `../KhaozEngine.Showcase/assets/character/**`. `NetworkedWalkServer` has no asset references.

Showcase becomes the canonical home of these CC0 assets.

### Retirement

- Delete `TerrainWalkSample/`.
- `KhaozEngine.slnx`: remove the TerrainWalkSample entry (keep Showcase, Render3DSample, the
  networked/server/snapshot entries).
- `.vscode/launch.json`: remove the two TerrainWalkSample configs (endless + bounded). The Showcase
  config already exists.
- `README.md` "Running the samples": drop the TerrainWalkSample row; the Showcase entry now lists a
  "3D World (walk)" room among its rooms. Leave Render3DSample (retired in B2b) and the
  networked/server/snapshot rows.

## Verification

- Headless unit test: `ShowcaseMenuTests` still green; the menu now lists five rooms (add/confirm the
  "3D World (walk)" entry in the registered-rooms assertion if one exists, else the model test is
  unaffected).
- Build gate: `dotnet build KhaozEngine.slnx` green after the move + retirement (NetworkedWalkSample
  still builds against the relocated assets; no dangling TerrainWalkSample reference anywhere).
- Smoke: `KE_MAX_FRAMES=<n> dotnet run --project KhaozEngine.Showcase/...` boots the menu, renders n
  frames, exits 0 (the shared 3D surface renders an empty scene under the menu).
- Manual: enter "3D World (walk)", walk (WASD + mouse orbit + scroll zoom + shift run), toggle the F2
  collision overlay and the render toggles, Esc back to the menu, and RE-ENTER to confirm the world
  rebuilds cleanly (no stale camera, no leaked ring, 2D rooms still render normally after visiting 3D).

## Concurrent-dev note

Heavy parallel dev is in flight. B2a touches shared hotspots: `KhaozEngine.slnx`, `.vscode/launch.json`,
`README.md`, and `NetworkedWalkSample.csproj`. Before merging back: `git fetch`; if `main`/`origin/main`
advanced, merge it into this branch first and re-resolve those files, rebuild the merged `.slnx`, then
merge back clean. No `<KhaozEngineVersion>` bump here (sample-only), so the version line will not collide.
If porting reveals a missing/awkward engine 3D API (something TerrainWalkSample could do only because it
WAS the app, not a scene), pause and raise it per the engine-first rule rather than working around it in
the sample.

## Follow-on

- **B2b**: enrich Room3D with a `FlattenFeature` town clearing + hand-placed CC0 Quaternius Medieval
  Village houses (copied from Ruinborne, baked `.coll` collision) + the fuller Render3DSample post-fx
  toggle set (cel/outline/palette/starfield/retro); retire Render3DSample.
- **B2c**: a Networked Walk room that starts an in-process authoritative server (background thread over
  a loopback socket) and connects a local `WorldClient` to it, reusing Room3D's world/render;
  retire NetworkedWalkSample + NetworkedWalkServer. (SnapshotSample and MmoServerSample stay separate:
  a headless test harness and a headless reference server, not windowed demos.)
