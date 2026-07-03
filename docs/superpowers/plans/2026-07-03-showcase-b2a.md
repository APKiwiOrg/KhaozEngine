# Showcase B2a Implementation Plan (3D World room)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fold TerrainWalkSample's walkable streamed 3D overworld into `KhaozEngine.Showcase` as a `Room3D`, convert the hub to `GameApp3D`, and retire TerrainWalkSample without breaking the networked samples.

**Architecture:** `ShowcaseApp` becomes a `GameApp3D` (shared `Scene3D` + a 3D pass before the 2D HUD). A `Room3D : GameScene, IGameScene3D` ports TerrainWalkSample, driven by the shared `Scene3D` injected via `Init(...)`. TerrainWalkSample stays as the live port source until the final retirement task.

**Tech Stack:** C# net10.0, `KhaozEngine.Game.Render3D` (GameApp3D/IGameScene3D/CharacterController3D/AnimatedCharacter), `KhaozEngine.Render3D` (Scene3D/FollowCamera3D/post-fx), `KhaozEngine.Terrain(.Render3D)` (field/streamer/splat), `KhaozEngine.Physics(.Bepu)`, xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-03-showcase-b2a-design.md`.
- Sample-only: **no `<KhaozEngineVersion>` bump, no CHANGELOG, no engine API change.** `IsPackable=false`. If the port needs an engine API that does not exist (something TerrainWalkSample could only do as the app, not a scene), STOP and raise it (engine-first rule) rather than hacking around it in the sample.
- **Port mapping** (applies to every port task): TerrainWalkSample is a `GameApp3D`, so it uses `Scene`/`sc` (the app's Scene3D), `Input`, `FrameWidth`/`FrameHeight`, `Viewport` directly. In `Room3D` these become: the **injected `_scene`** field (from `Init`, = ShowcaseApp's `Scene`); `Manager!.Input`; `Manager!.FrameWidth`/`FrameHeight`; `Manager!.Viewport`. TerrainWalkSample's `OnLoad` -> `Room3D.OnEnter`; `OnUpdate` -> `OnUpdate`; `OnDraw3D` -> `OnDraw3D`; `OnDraw2D` -> `OnDraw2D`; `OnUnload` teardown -> `OnExit`.
- Room3D uses the fixed `TerrainPresets.BoundedClearing()` preset (no command-line `bounded` arg).
- Back-to-menu on Esc via `Manager!.Pop()`.
- No em-dashes or semicolons in shipped prose (comments/README/docs).
- The solution must build green (`dotnet build KhaozEngine.slnx`) after every task; NetworkedWalkSample must keep building after the asset move (Task 2) and the retirement (Task 7).
- Heavy concurrent dev: at retirement/merge integrate `origin/main` first and re-resolve `KhaozEngine.slnx`, `.vscode/launch.json`, `README.md`, `NetworkedWalkSample.csproj`.
- Commit subjects: `showcase: ...`.

---

### Task 1: ShowcaseApp -> GameApp3D + 3D csproj refs + empty Room3D registered

**Files:**
- Modify: `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (add 6 ProjectReferences)
- Modify: `KhaozEngine.Showcase/ShowcaseApp.cs` (base -> GameApp3D, OnDraw3D, register Room3D)
- Create: `KhaozEngine.Showcase/Room3D.cs` (skeleton)

**Interfaces:**
- Consumes: `GameApp3D` (`Scene`, `OnDraw3D(Scene3D)`), `SceneManager.Draw3D`, `IGameScene3D`, `Scene3D`.
- Produces: `Room3D : GameScene, IGameScene3D` with `Room3D Init(Scene3D scene, Texture2D white, SpriteFont hud)`.

- [ ] **Step 1: Add the 3D ProjectReferences**

In `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj`, add to the `ProjectReference` ItemGroup (keep the existing Windowing/Render2D/Gui/Audio/Game):

```xml
    <ProjectReference Include="../KhaozEngine.Game.Render3D/KhaozEngine.Game.Render3D.csproj" />
    <ProjectReference Include="../KhaozEngine.Render3D/KhaozEngine.Render3D.csproj" />
    <ProjectReference Include="../KhaozEngine.Terrain/KhaozEngine.Terrain.csproj" />
    <ProjectReference Include="../KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj" />
    <ProjectReference Include="../KhaozEngine.Physics/KhaozEngine.Physics.csproj" />
    <ProjectReference Include="../KhaozEngine.Physics.Bepu/KhaozEngine.Physics.Bepu.csproj" />
```

- [ ] **Step 2: Convert ShowcaseApp to GameApp3D + register the room**

In `KhaozEngine.Showcase/ShowcaseApp.cs`: change `public sealed class ShowcaseApp : GameApp` to `: GameApp3D`. Add the 3D pass override (near `OnDraw2D`):

```csharp
protected override void OnDraw3D(Scene3D scene) => _scenes.Draw3D(scene);
```

Add `using KhaozEngine.Render3D;` if needed. In `OnLoad`, register the 3D room after the mini-game row (Scene is the GameApp3D-provided Scene3D):

```csharp
Rooms.Add(("3D World (walk)", () => new Room3D().Init(Scene, _white, small)));
```

- [ ] **Step 3: Write the Room3D skeleton**

Create `KhaozEngine.Showcase/Room3D.cs`:

```csharp
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>The walkable streamed 3D overworld, ported from TerrainWalkSample into a room. Renders through the
    /// showcase's shared Scene3D (injected via Init, since a GameScene cannot reach the app's 3D surface). Builds
    /// its world in OnEnter and tears it down in OnExit (the Scene3D is shared with the other rooms, so it must
    /// leave no camera override or loaded ring behind). Esc returns to the menu.</summary>
    public sealed class Room3D : GameScene, IGameScene3D
    {
        Scene3D _scene = null!;
        Texture2D _white = null!;
        SpriteFont _hud = null!;

        public Room3D Init(Scene3D scene, Texture2D white, SpriteFont hud)
        {
            _scene = scene; _white = white; _hud = hud;
            return this;
        }

        public override void OnEnter()
        {
            // Task 3+: build terrain/streaming/camera/physics/character/props into _scene here.
        }

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }
            // Task 3+: physics + character + streamer + camera + toggles here.
        }

        public void OnDraw3D(Scene3D scene)
        {
            // Task 3+: chunk sink + props + character + overlay here.
        }

        public override void OnDraw2D(SpriteBatch batch)
        {
            // Task 5: HUD here.
        }

        public override void OnExit()
        {
            // Task 6: dispose streamer + physics, clear _scene.CameraOverride, reset _scene.Post.
        }
    }
}
```

- [ ] **Step 4: Build + smoke**

Run: `dotnet build KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (succeeds).
Run: `KE_MAX_FRAMES=3 dotnet run --project KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (exits 0).
Manual (optional now): the menu lists a 5th row "3D World (walk)"; entering it shows an empty 3D frame; Esc returns.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Showcase/KhaozEngine.Showcase.csproj KhaozEngine.Showcase/ShowcaseApp.cs KhaozEngine.Showcase/Room3D.cs
git commit -m "showcase: ShowcaseApp -> GameApp3D + empty Room3D skeleton registered"
```

---

### Task 2: Move shared assets into Showcase + keep NetworkedWalkSample building

**Files:**
- Move: `TerrainWalkSample/assets/*` -> `KhaozEngine.Showcase/assets/*`
- Modify: `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (copy-to-output items)
- Modify: `NetworkedWalkSample/NetworkedWalkSample.csproj` (repoint LinkBase)
- Modify: `TerrainWalkSample/TerrainWalkSample.csproj` (repoint its own asset includes so it still builds until Task 7)

- [ ] **Step 1: Move the asset files (preserve git history)**

```bash
git mv TerrainWalkSample/assets KhaozEngine.Showcase/assets
```

This moves `assets/props/**` (CC0 prop kit + `props.manifest.json` + CREDITS + `.surf`), `assets/character/**` (Player.glb + CREDITS), and `assets/blacksmith_proxy.coll`.

- [ ] **Step 2: Add copy-to-output items to Showcase.csproj**

In `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj`, add an ItemGroup (mirror TerrainWalkSample's):

```xml
  <ItemGroup>
    <None Include="assets/props/**" CopyToOutputDirectory="PreserveNewest" />
    <None Include="assets/character/**" CopyToOutputDirectory="PreserveNewest" />
    <None Include="assets/blacksmith_proxy.coll" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Repoint NetworkedWalkSample's LinkBase includes**

In `NetworkedWalkSample/NetworkedWalkSample.csproj`, change the two asset includes from `../TerrainWalkSample/assets/...` to `../KhaozEngine.Showcase/assets/...`:

```xml
    <None Include="../KhaozEngine.Showcase/assets/props/**" CopyToOutputDirectory="PreserveNewest" LinkBase="assets/props" />
    <None Include="../KhaozEngine.Showcase/assets/character/**" CopyToOutputDirectory="PreserveNewest" LinkBase="assets/character" />
```

- [ ] **Step 4: Keep TerrainWalkSample building until Task 7**

`git mv` removed `TerrainWalkSample/assets`, so TerrainWalkSample's own `<None Include="assets/...">` items now match nothing (its runtime asset load would fail). Repoint TerrainWalkSample's three asset includes in `TerrainWalkSample/TerrainWalkSample.csproj` to the moved location so it still runs until it is deleted in Task 7:

```xml
    <None Include="../KhaozEngine.Showcase/assets/props/**" CopyToOutputDirectory="PreserveNewest" LinkBase="assets/props" />
    <None Include="../KhaozEngine.Showcase/assets/character/**" CopyToOutputDirectory="PreserveNewest" LinkBase="assets/character" />
    <None Include="../KhaozEngine.Showcase/assets/blacksmith_proxy.coll" CopyToOutputDirectory="PreserveNewest" LinkBase="assets" />
```

- [ ] **Step 5: Build the affected projects**

```bash
dotnet build KhaozEngine.Showcase/KhaozEngine.Showcase.csproj
dotnet build NetworkedWalkSample/NetworkedWalkSample.csproj
dotnet build TerrainWalkSample/TerrainWalkSample.csproj
```
All three succeed. Confirm the assets land in each `bin/.../assets/props`, `.../character`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "showcase: move shared CC0 assets into KhaozEngine.Showcase; repoint NetworkedWalkSample + TerrainWalkSample"
```

---

### Task 3: Room3D - terrain + streaming + camera

Port TerrainWalkSample's terrain/streaming/camera into Room3D. **Read `TerrainWalkSample/Program.cs` as the source of truth** (it is still present).

**Files:** Modify `KhaozEngine.Showcase/Room3D.cs`.

**Interfaces:** Consumes `TerrainField`/`TerrainPresets.BoundedClearing`/`TerrainCollision`, `Scene3DChunkSink`/`TerrainStreamer`/`StreamerConfig`/`ScatterConfig`, `TerrainMaterialPresets.Procedural`/`Scene3D.LoadTerrainMaterial`, `FollowCamera3D`/`FollowCameraController`, `Scene3D.CameraOverride`.

- [ ] **Step 1: Port the terrain + streaming + camera build into OnEnter**

Port these TerrainWalkSample `OnLoad` pieces into `Room3D.OnEnter`, applying the Global-Constraints port mapping (`sc`/`Scene` -> `_scene`; app members -> `Manager!.*`):
- `_field = new TerrainField(TerrainPresets.BoundedClearing())` and `_terrain = new TerrainCollision(_field)`.
- Splat material: `var mat = _scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural())`.
- `FollowCamera3D` + `FollowCameraController`; set `_scene.CameraOverride = _camera`.
- `Scene3DChunkSink(_scene, _field, ScatterConfig.ForestRing(), _propMeshes, chunkSize, propDrawRadius, mat, ...)` and `TerrainStreamer(StreamerConfig.Default, _sink)`, then prime the initial ring (the same `while` loop TerrainWalkSample uses).
  For this task use an **empty** `_propMeshes` dictionary and no physics yet (props + physics come in Task 5); pass whatever the sink ctor requires with physics/collisionShapes null if the overload allows, or use the sink overload TerrainWalkSample uses with an empty prop-mesh dict.

Add the fields (`_field`, `_terrain`, `_camera`, `_camController`, `_sink`, `_streamer`, `_propMeshes`).

- [ ] **Step 2: Port the per-frame camera + streamer into OnUpdate + OnDraw3D**

- `OnUpdate`: after the Esc check, `_camController.Update(Manager!.Input, dt)` (match TerrainWalkSample's call), `_streamer.Update(<focus>, dt)`, camera target/aspect sync. Without a character yet, drive the streamer focus + camera target from the camera position (temporary; Task 4 switches focus to the character).
- `OnDraw3D(scene)`: `_sink.Draw(<focus>)`.

- [ ] **Step 2b: Run it fails-safe** - Run `dotnet build KhaozEngine.Showcase/KhaozEngine.Showcase.csproj`; expected: succeeds.

- [ ] **Step 3: Smoke + manual**

Run: `KE_MAX_FRAMES=3 dotnet run --project KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (exits 0).
Manual: enter "3D World (walk)"; a streamed terrain renders; Esc returns to menu.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Showcase/Room3D.cs
git commit -m "showcase: Room3D terrain + streaming + follow camera"
```

---

### Task 4: Room3D - physics + character controller + animated avatar

**Files:** Modify `KhaozEngine.Showcase/Room3D.cs`.

**Interfaces:** Consumes `BepuPhysicsWorld`, `CharacterController3D`, `AnimatedCharacter` (+ the skinned-glTF load `TryLoadAnimatedCharacter` pattern), `MeshPrimitives.Capsule` (fallback), `_scene.DrawSkinned`/`_scene.Draw`.

- [ ] **Step 1: Port physics + character build into OnEnter**

Port TerrainWalkSample's physics + character build (its `OnLoad` lines for `BepuPhysicsWorld`, `CharacterController3D` + initial settle, `TryLoadAnimatedCharacter`, the fallback `_capsule` mesh). Load the character asset from the moved path `assets/character/Player.glb` (relative to the exe, resolved via `AppContext.BaseDirectory` the same way TerrainWalkSample does). Switch the `Scene3DChunkSink` construction from Task 3 to pass the real `_physics` (and `collisionShapes` in Task 5).

- [ ] **Step 2: Port character update + draw**

- `OnUpdate`: `_physics.Step(dt)`, `_character.Update(Manager!.Input, dt, _camera.Yaw, _terrain.GroundHeight, _terrain.GroundNormal, _physics)`, the animated-character update from position delta + grounded state, then set the streamer focus + camera target to `_character.Position` (replacing Task 3's temporary camera-driven focus).
- `OnDraw3D`: draw the character - `_scene.DrawSkinned(_characterMesh, _animChar.Pose, model, Color.White)` when loaded, else `_scene.Draw(_capsule, ...)` - matching TerrainWalkSample.

- [ ] **Step 3: Build + smoke + manual**

Build succeeds; `KE_MAX_FRAMES=3` smoke exits 0. Manual: enter the room, the character stands on the terrain; WASD moves, mouse-drag orbits, scroll zooms, Shift runs; the avatar animates (or a capsule if the asset fails).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Showcase/Room3D.cs
git commit -m "showcase: Room3D physics + character controller + animated avatar"
```

---

### Task 5: Room3D - props + textured stone block + collision overlay + HUD + toggles

**Files:** Modify `KhaozEngine.Showcase/Room3D.cs`.

**Interfaces:** Consumes `AssetManifest`/`PropLoader`/`PropCollisionBake`, `MeshOps.WithTangents`+`PropMaterialPresets.Procedural`+`MeshOps.ScaleUv`, `PropCollisionFormat.Read`, `CollisionShapeOverlay`, `Scene3D.Post` toggles.

- [ ] **Step 1: Port props + collision + textured prop + platform**

Port TerrainWalkSample's remaining `OnLoad`: prop-kit load (`AssetManifest.Load("assets/props/props.manifest.json")` resolved from `AppContext.BaseDirectory`), `PropLoader.LoadProp` + `PropCollisionBake.Bake` per prop into `_propMeshes` + `collisionShapes` (feed these to the `Scene3DChunkSink` from Task 4), the hand-placed platform box + its `BoxShape` static, the procedural textured stone block (`_scene.LoadMesh(MeshOps.WithTangents(MeshOps.ScaleUv(MeshPrimitives.Box(1.5f), 3f)), PropMaterialPresets.Procedural())`), and the blacksmith collision proxy (`PropCollisionFormat.Read("assets/blacksmith_proxy.coll")`) added as a static + built into `_collisionOverlay`.

- [ ] **Step 2: Port the overlay + HUD + render toggles**

- `OnUpdate`: the F2 collision-overlay toggle and the render-debug toggles (outline / starfield / cel) TerrainWalkSample reads, operating on `_scene.Post`.
- `OnDraw3D`: draw the platform, textured prop, and (when enabled) `_collisionOverlay.Draw(_scene)`.
- `OnDraw2D`: the collision-legend HUD (use the injected `_hud` font + `_white`).

- [ ] **Step 3: Build + smoke + manual**

Build succeeds; smoke exits 0. Manual: props scatter with collision, the textured stone block shows albedo+normal, F2 toggles the collision overlay + legend, the render toggles work.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Showcase/Room3D.cs
git commit -m "showcase: Room3D props + textured prop + collision overlay + HUD + toggles"
```

---

### Task 6: Room3D - OnExit teardown + clean re-entry

**Files:** Modify `KhaozEngine.Showcase/Room3D.cs`.

- [ ] **Step 1: Implement OnExit teardown**

In `Room3D.OnExit`, tear down everything the room built into the shared `Scene3D`, so the menu/2D rooms render cleanly and re-entering rebuilds from scratch:
- Dispose `_streamer` and `_sink` (both `IDisposable`), and `_physics`.
- Unload the collision overlay (`_collisionOverlay.Unload(...)` as TerrainWalkSample's OnUnload does).
- `_scene.CameraOverride = null;` (drop the follow camera so the default camera returns).
- Reset `_scene.Post` to defaults (new `PixelPostProcessSettings()` or the field-by-field reset of the toggles the room changed: `Outline`/`Starfield`/`CelBands`/`Quantize`/`Dither`/`Pixelated`/`RenderScale`), so a starfield/cel left on does not bleed under the menu.
- Null the per-enter fields so a re-entered room rebuilds fresh (guard `OnExit` against being called before `OnEnter` completed).

- [ ] **Step 2: Build + smoke + manual (re-entry is the key check)**

Build succeeds; smoke exits 0. Manual: enter the 3D room, walk, toggle overlay/post-fx, Esc to menu (menu renders normally, no starfield/camera bleed), then RE-ENTER the 3D room and confirm it rebuilds cleanly (terrain + character present, no doubled meshes, no leftover state). Enter a 2D room after visiting 3D and confirm it renders normally.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Showcase/Room3D.cs
git commit -m "showcase: Room3D OnExit teardown for clean re-entry (shared Scene3D)"
```

Manual validation handoff (give the user this one-click boot command, do NOT run it yourself):

```bash
dotnet run --project /Users/antonio/KhaozEngine/.claude/worktrees/feature+showcase-b2a/KhaozEngine.Showcase/KhaozEngine.Showcase.csproj -c Debug
```

---

### Task 7: Retire TerrainWalkSample + integrate concurrent work

**Files:** Delete `TerrainWalkSample/`; modify `KhaozEngine.slnx`, `.vscode/launch.json`, `README.md`.

- [ ] **Step 1: Integrate concurrent work FIRST**

```bash
git fetch
git log --oneline origin/main -1
```
If `origin/main` advanced, `git merge origin/main` and resolve hotspots (`KhaozEngine.slnx`, `.vscode/launch.json`, `README.md`, `NetworkedWalkSample.csproj`). Re-run `dotnet build KhaozEngine.slnx` on the merged result.

- [ ] **Step 2: Delete TerrainWalkSample**

```bash
git rm -r TerrainWalkSample
```

- [ ] **Step 3: Deregister from the solution + launch configs**

- `KhaozEngine.slnx`: remove the `TerrainWalkSample` `<Project>` line.
- `.vscode/launch.json`: remove the two TerrainWalkSample configs ("endless terrain" + "bounded clearing + rim wall").

- [ ] **Step 4: Update the README**

In `README.md` "Running the samples": drop the `TerrainWalkSample` row; note the Showcase's rooms now include "3D World (walk)". Update the `KE_MAX_FRAMES` example if it referenced TerrainWalkSample (point it at KhaozEngine.Showcase). Leave Render3DSample + the networked/server/snapshot rows.

- [ ] **Step 5: Build the whole solution + grep for dangling refs**

```bash
dotnet build KhaozEngine.slnx
dotnet build NetworkedWalkSample/NetworkedWalkSample.csproj
grep -rn "TerrainWalkSample" --include=*.json --include=*.md --include=*.csproj --include=*.slnx . | grep -v obj
```
Expected: solution + NetworkedWalkSample build; grep returns no live references (CHANGELOG history + this branch's own design/plan docs are fine).

- [ ] **Step 6: Full test + commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add -A
git commit -m "showcase: retire TerrainWalkSample (folded into the 3D World room)"
```

---

## Self-Review

**Spec coverage:**
- Goal 1 (GameApp3D hub) -> Task 1. ✓
- Goal 2 (Room3D faithful port: terrain/streaming/camera, physics/character/avatar, props/textured-prop/overlay/HUD/toggles, teardown) -> Tasks 3-6. ✓
- Goal 3 (retire TerrainWalkSample without breaking networked samples) -> Task 2 (asset move + repoint) + Task 7 (retire + build gate). ✓
- Goal 4 (builds green + KE_MAX_FRAMES smoke) -> per-task build+smoke, Task 7 build gate + full test. ✓
- Shared-Scene3D lifecycle / clean re-entry -> Task 6. ✓
- Concurrent-dev integration -> Task 7 Step 1. ✓
- Non-goals (clearing/houses/post-fx = B2b, networked = B2c, version bump) -> absent. ✓

**Placeholder scan:** Task 1/2/7 carry concrete code + commands. Port tasks (3-6) name the exact TerrainWalkSample source sections, the Room3D hooks to map them onto, and the port mapping (Global Constraints). The sample is the authoritative source and stays present until Task 7 - acceptable for a faithful port.

**Type consistency:** `Room3D.Init(Scene3D, Texture2D, SpriteFont)`, `_scene` used everywhere TerrainWalkSample used `sc`/`Scene`, `Manager!.Input`/`FrameWidth` for app members, `IGameScene3D.OnDraw3D(Scene3D)`, `ShowcaseApp.OnDraw3D -> _scenes.Draw3D(scene)` - consistent across Tasks 1-6.
