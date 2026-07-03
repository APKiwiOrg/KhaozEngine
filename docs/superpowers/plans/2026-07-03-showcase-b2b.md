# Showcase B2b Implementation Plan (clearing + CC0 houses + post-fx)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the 3D World room a town (FlattenFeature clearing + 7 CC0 Quaternius buildings with baked collision), fold Render3DSample's post-fx toggles into the room, and retire Render3DSample.

**Architecture:** All changes are in `KhaozEngine.Showcase/Room3D.cs` + assets + csproj + the retirement of Render3DSample. Buildings are hand-placed (not streamed): loaded via `PropLoader`, drawn via `Scene3D.DrawProps`, collided via `PropCollisionFormat` baked `.coll` shapes added as physics statics. The clearing is a `FlattenFeature` appended to Room3D's terrain config.

**Tech Stack:** C# net10.0, `KhaozEngine.Terrain` (FlattenFeature/TerrainConfig), `KhaozEngine.Render3D`/`Terrain.Render3D` (DrawProps, Post), `KhaozEngine.Physics` (PropCollisionFormat, PhysicsShapeScale, AddStatic), xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-03-showcase-b2b-design.md`.
- Sample-only: **no `<KhaozEngineVersion>` bump, no CHANGELOG, no engine API change.** `IsPackable=false`. If a needed capability is missing from the engine, STOP and raise it (engine-first rule).
- Reference pattern (a DIFFERENT repo, read-only): `/Users/antonio/Ruinborne` - `Ruinborne.Core/RuinborneWorld.cs` (TownBuilding layout, FlattenFeature town), `Ruinborne.Core/RuinbornePhysics.cs` (`.coll` load + `AddPlacement` scaled statics), `Ruinborne.Client/assets/buildings/` (the CC0 assets + manifest). Do NOT add a dependency on Ruinborne; copy assets + mirror the pattern in the Showcase.
- Room3D already has: `_field` (TerrainField), `_terrain` (TerrainCollision, `.GroundHeight(x,z)`), `_physics` (BepuPhysicsWorld, `.AddStatic(PhysicsShape, Pose)`), `_scene` (Scene3D), the `AssetManifest.Load`+`PropLoader.LoadProp`+`_scene.LoadMesh` prop pattern, and an `OnExit` that disposes physics wholesale + unloads meshes.
- Back-to-menu on Esc unchanged. No em-dashes or semicolons in shipped prose.
- Solution builds green after every task; retirement leaves no dangling Render3DSample reference.
- Heavy concurrent dev: at retirement/merge integrate `origin/main` first, re-resolve `KhaozEngine.slnx`/`.vscode/launch.json`/`README.md`.
- Commit subjects: `showcase: ...`.

---

### Task 1: Copy the CC0 building assets into Showcase

**Files:**
- Copy into: `KhaozEngine.Showcase/assets/buildings/` (7 `.glb` + 7 `.coll`)
- Create: `KhaozEngine.Showcase/assets/buildings/buildings.manifest.json`, `.../CREDITS.md`
- Modify: `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj`

- [ ] **Step 1: Copy the render meshes + baked collision**

```bash
mkdir -p KhaozEngine.Showcase/assets/buildings
cd KhaozEngine.Showcase/assets/buildings
for id in inn bell_tower blacksmith house_1 house_2 house_3 well; do
  cp /Users/antonio/Ruinborne/Ruinborne.Client/assets/buildings/$id.glb .
  cp /Users/antonio/Ruinborne/Ruinborne.Client/assets/buildings/$id.coll .
done
cp /Users/antonio/Ruinborne/Ruinborne.Client/assets/buildings/CREDITS.md .
cd -
```
(Only the render `.glb` + baked `.coll` are needed - not the `.surf` walkable-heightmaps or `_collision.glb` re-bake sources.)

- [ ] **Step 2: Write a trimmed manifest**

Create `KhaozEngine.Showcase/assets/buildings/buildings.manifest.json` (heightMeters from Ruinborne's manifest: inn 6.5, bell_tower 9.0, blacksmith 5.0, house_1 4.8, house_2 4.8, house_3 3.0, well 2.2):

```json
{
  "_comment": "CC0 Quaternius Medieval Village buildings for the KhaozEngine.Showcase 3D World room. heightMeters is the real-world height PropLoader normalizes each mesh to. collisionShape is the baked compound-of-convex .coll. See CREDITS.md.",
  "props": [
    { "id": "inn",        "file": "inn.glb",        "heightMeters": 6.5, "source": "Quaternius Medieval Village (via levy-street/world-of-claudecraft)", "license": "CC0", "collisionShape": "inn.coll" },
    { "id": "bell_tower", "file": "bell_tower.glb", "heightMeters": 9.0, "source": "Quaternius Medieval Village (via levy-street/world-of-claudecraft)", "license": "CC0", "collisionShape": "bell_tower.coll" },
    { "id": "blacksmith", "file": "blacksmith.glb", "heightMeters": 5.0, "source": "Quaternius Medieval Village (via levy-street/world-of-claudecraft)", "license": "CC0", "collisionShape": "blacksmith.coll" },
    { "id": "house_1",    "file": "house_1.glb",    "heightMeters": 4.8, "source": "Quaternius Medieval Village (via levy-street/world-of-claudecraft)", "license": "CC0", "collisionShape": "house_1.coll" },
    { "id": "house_2",    "file": "house_2.glb",    "heightMeters": 4.8, "source": "Quaternius Medieval Village (via levy-street/world-of-claudecraft)", "license": "CC0", "collisionShape": "house_2.coll" },
    { "id": "house_3",    "file": "house_3.glb",    "heightMeters": 3.0, "source": "Quaternius Medieval Village (via levy-street/world-of-claudecraft)", "license": "CC0", "collisionShape": "house_3.coll" },
    { "id": "well",       "file": "well.glb",       "heightMeters": 2.2, "source": "Quaternius Medieval Village (via levy-street/world-of-claudecraft)", "license": "CC0", "collisionShape": "well.coll" }
  ]
}
```

Confirm the copied `CREDITS.md` correctly attributes CC0 Quaternius Medieval Village; trim any Ruinborne-specific wording.

- [ ] **Step 3: Copy-to-output in the csproj**

In `KhaozEngine.Showcase/KhaozEngine.Showcase.csproj`, in the assets ItemGroup:

```xml
    <None Include="assets/buildings/**" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 4: Build + confirm assets copy**

Run: `dotnet build KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (succeeds). Confirm `KhaozEngine.Showcase/bin/Debug/net10.0/assets/buildings/inn.glb` + `inn.coll` exist.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Showcase/assets/buildings KhaozEngine.Showcase/KhaozEngine.Showcase.csproj
git commit -m "showcase: add CC0 Quaternius Medieval Village building assets"
```

---

### Task 2: FlattenFeature town clearing + scatter hole

**Files:** Modify `KhaozEngine.Showcase/Room3D.cs`.

- [ ] **Step 1: Add the clearing to the terrain config**

Add town constants near the top of Room3D (a fixed town near spawn that fits inside BoundedClearing's ~38-radius rim disc and avoids its lake at ~(-12,-4)):

```csharp
// Town clearing: a flat plateau near spawn holding the hand-placed buildings. Tuned to sit inside
// BoundedClearing's rim disc and clear of its lake. Values are a starting point - adjust by playtest
// so buildings sit flat and inside the rim.
static readonly System.Numerics.Vector2 TownCenter = new(0f, 14f);
const float TownRadius = 18f, TownBlend = 0.25f;
```

Change Room3D.OnEnter's terrain build (currently `_field = new TerrainField(TerrainPresets.BoundedClearing())`) to append a `FlattenFeature`:

```csharp
var cfg = TerrainPresets.BoundedClearing();
float townHeight = /* the flat town height: sample the natural ground once at the town centre BEFORE flattening, or pick a fixed level like 1.5f */;
var feats = new System.Collections.Generic.List<ITerrainFeature>(cfg.Features) { new FlattenFeature(TownCenter.X, TownCenter.Y, TownRadius, townHeight, TownBlend) };
cfg.Features = feats.ToArray();
_field = new TerrainField(cfg);
```

For `townHeight`: build a throwaway `TerrainField(TerrainPresets.BoundedClearing())`, sample `.SampleHeight(TownCenter.X, TownCenter.Y)` (or use `TerrainCollision.GroundHeight` on it), use that as the flatten target so the plateau meets the surrounding ground smoothly. (Confirm the `TerrainField`/`TerrainConfig`/`FlattenFeature` API by reading the types - `cfg.Features` must be reassignable; if `TerrainConfig` is immutable, build the config from scratch mirroring `TerrainPresets.BoundedClearing` + the extra feature.)

- [ ] **Step 2: Exclude trees from the town**

Room3D builds the scatter with `ScatterConfig.ForestRing()`. Give that config a `ClearingRadius` = `TownRadius` and `ClearingCenter` = `TownCenter` so trees do not spawn in the town (read `ScatterConfig` for the exact field names - Ruinborne's `CreateScatterConfig` sets `ClearingRadius`/`ClearingCenter`). If `ForestRing()` returns a preset you cannot mutate, construct the scatter config with the clearing fields set.

- [ ] **Step 3: Build + smoke**

Run: `dotnet build KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (succeeds).
Run: `KE_SHOWCASE_ROOM="3D World" KE_MAX_FRAMES=6 dotnet run --project KhaozEngine.Showcase/KhaozEngine.Showcase.csproj` (exit 0 - the flattened terrain builds).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Showcase/Room3D.cs
git commit -m "showcase: Room3D FlattenFeature town clearing + tree-scatter hole"
```

---

### Task 3: Load, place, and render the buildings

**Files:** Modify `KhaozEngine.Showcase/Room3D.cs`.

**Interfaces:** Consumes `AssetManifest.Load`, `PropLoader.LoadProp`, `Scene3D.LoadMesh`, `PropPlacement`, `Scene3D.DrawProps(placements, meshDict, focus, radius)`.

- [ ] **Step 1: A building-layout list**

Add a `record struct TownBuilding(string Id, float X, float Z, float Yaw, float Scale)` and a builder placing all 7 around `TownCenter` inside the flat disc (starting layout - tune by playtest so none overlap and all sit inside `TownRadius * (1 - TownBlend)`):

```csharp
const float BuildingScale = 1.5f;
static System.Collections.Generic.IReadOnlyList<TownBuilding> CreateTownBuildings() => new[]
{
    new TownBuilding("inn",        TownCenter.X,        TownCenter.Y,        0.0f,  BuildingScale),
    new TownBuilding("well",       TownCenter.X + 6f,   TownCenter.Y - 3f,   0.0f,  BuildingScale),
    new TownBuilding("house_1",    TownCenter.X + 9f,   TownCenter.Y + 5f,  -2.2f,  BuildingScale),
    new TownBuilding("house_2",    TownCenter.X - 9f,   TownCenter.Y + 4f,   2.2f,  BuildingScale),
    new TownBuilding("house_3",    TownCenter.X + 8f,   TownCenter.Y - 8f,  -0.7f,  BuildingScale),
    new TownBuilding("blacksmith", TownCenter.X - 9f,   TownCenter.Y - 7f,   0.9f,  BuildingScale),
    new TownBuilding("bell_tower", TownCenter.X,        TownCenter.Y + 11f,  0.0f,  BuildingScale),
};
```

- [ ] **Step 2: Load meshes + build placements in OnEnter**

Add fields `_buildingMeshes` (`Dictionary<string, MeshHandle>`) and `_buildingPlacements` (`List<PropPlacement>`). In OnEnter (after the terrain/physics build):

```csharp
string bManifest = System.IO.Path.Combine(System.AppContext.BaseDirectory, "assets", "buildings", "buildings.manifest.json");
AssetManifest buildings = AssetManifest.Load(bManifest);
foreach (AssetEntry e in buildings.Props)
    _buildingMeshes[e.Id] = _scene.LoadMesh(PropLoader.LoadProp(e));
foreach (TownBuilding b in CreateTownBuildings())
    _buildingPlacements.Add(new PropPlacement(b.Id, b.X, _terrain.GroundHeight(b.X, b.Z), b.Z, b.Scale, b.Yaw, variant: 0));
```

(Confirm the `PropPlacement` ctor arg order/names by reading it - match how Room3D/PropRenderer already construct/consume it.)

- [ ] **Step 3: Draw the buildings in OnDraw3D**

Add, in `Room3D.OnDraw3D`:

```csharp
scene.DrawProps(_buildingPlacements, _buildingMeshes, _character.Position, BuildingDrawRadius);
```
with a `const float BuildingDrawRadius = 320f;` (buildings are few + always visible; match Ruinborne's high draw radius).

- [ ] **Step 4: Teardown in OnExit**

In OnExit, unload the building meshes + clear the lists:

```csharp
foreach (MeshHandle h in _buildingMeshes.Values) _scene.UnloadMesh(h);
_buildingMeshes.Clear();
_buildingPlacements.Clear();
```

- [ ] **Step 5: Build + smoke + manual**

Build succeeds; `KE_SHOWCASE_ROOM="3D World" KE_MAX_FRAMES=6 dotnet run ...` exits 0. Manual: the village renders in the clearing (buildings not yet solid - collision is Task 4).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Showcase/Room3D.cs
git commit -m "showcase: Room3D loads + places + renders the CC0 town buildings"
```

---

### Task 4: Building collision statics

**Files:** Modify `KhaozEngine.Showcase/Room3D.cs`.

**Interfaces:** Consumes `PropCollisionFormat.LoadDirectory(dir) -> IReadOnlyDictionary<string, PhysicsShape>`, `PhysicsShapeScale.Uniform(shape, scale)`, `BepuPhysicsWorld.AddStatic(PhysicsShape, Pose)`, `Pose`, `Quaternion`.

- [ ] **Step 1: Load the baked .coll shapes + add scaled statics**

In OnEnter (after the buildings are placed, before priming the ring), mirror Ruinborne's `RuinbornePhysics` pattern:

```csharp
string bDir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "assets", "buildings");
System.Collections.Generic.IReadOnlyDictionary<string, PhysicsShape> bShapes = PropCollisionFormat.LoadDirectory(bDir);
foreach (TownBuilding b in CreateTownBuildings())
    if (bShapes.TryGetValue(b.Id, out PhysicsShape shape))
    {
        PhysicsShape scaled = PhysicsShapeScale.Uniform(shape, b.Scale);
        _physics.AddStatic(scaled, new Pose(new System.Numerics.Vector3(b.X, _terrain.GroundHeight(b.X, b.Z), b.Z),
                                            Quaternion.CreateFromYawPitchRoll(b.Yaw, 0f, 0f)));
    }
```

(Read `PhysicsShapeScale.Uniform`, `PropCollisionFormat.LoadDirectory`, and `Pose`/`AddStatic` signatures to match exactly - Ruinborne's `AddPlacement`/`ScaleShape` is the reference. The physics world is disposed wholesale in OnExit, so these statics need no separate teardown.)

- [ ] **Step 2: Optionally register them in the F2 overlay**

If cheap, add the building statics to `_overlayStatics` so the F2 collision overlay shows the building proxies too (mirror how the blacksmith proxy is added at Room3D.cs ~172-174). If it complicates the overlay build, skip - not required.

- [ ] **Step 3: Build + smoke + manual (collision is the key check)**

Build succeeds; `KE_SHOWCASE_ROOM="3D World" KE_MAX_FRAMES=6 dotnet run ...` exits 0 (statics load + step without crashing). Manual: walk into a building wall - you are blocked, not walking through. F2 shows the building collision proxies (if Step 2 done).

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Showcase/Room3D.cs
git commit -m "showcase: Room3D building collision statics from baked .coll"
```

---

### Task 5: Fold Render3DSample post-fx (retro + palette) + OnExit reset

**Files:** Modify `KhaozEngine.Showcase/Room3D.cs`.

**Interfaces:** Consumes `Scene3D.Post` fields (`Quantize`/`Dither`/`Pixelated`/`CelBands`/`RenderWidth`/`RenderHeight`/`ActivePalette`), `Palettes.All`.

- [ ] **Step 1: Add the R (retro) + P (palette) toggles to OnUpdate**

Mirror `Render3DSample/Program.cs`'s R + P handlers (read them). In `Room3D.OnUpdate` alongside the existing outline/starfield/cel toggles:

```csharp
if (Manager!.Input.WasPressed(Key.R))
{
    bool on = !post.Quantize;
    post.Quantize = post.Dither = post.Pixelated = on;
    post.CelBands = on ? 4 : 0;
    post.RenderWidth = on ? 320 : 1920; post.RenderHeight = on ? 180 : 1080;
}
if (Manager!.Input.WasPressed(Key.P))
{
    _palIdx = (_palIdx + 1) % Palettes.All.Length;
    post.ActivePalette = Palettes.All[_palIdx];
}
```
Add an `int _palIdx = 2;` field (Render3DSample's default). Confirm the exact `Post` field names + `Palettes.All` against `Render3DSample/Program.cs` and `PixelPostProcessSettings`.

- [ ] **Step 2: Extend OnExit's Post reset**

Room3D.OnExit already resets the outline fields. Add resets for every field the new toggles mutate, back to `PixelPostProcessSettings` defaults (read the defaults: `Quantize=false`, `Dither=false`, `Pixelated=false`, `CelBands=0`, `RenderWidth`/`RenderHeight` to their defaults, `ActivePalette` to its default). So leaving the room never bleeds a retro/palette look under the menu:

```csharp
post.Quantize = false; post.Dither = false; post.Pixelated = false;
post.CelBands = 0;
post.RenderWidth = /* default */; post.RenderHeight = /* default */;
post.ActivePalette = /* PixelPostProcessSettings default palette */;
_palIdx = 2;
```

- [ ] **Step 3: Build + smoke + manual**

Build succeeds; smoke exits 0. Manual: in the 3D room, R toggles the retro/pixel look, P cycles palettes; Esc to menu leaves NO retro/palette bleed; re-enter is clean.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Showcase/Room3D.cs
git commit -m "showcase: Room3D retro + palette post-fx toggles (from Render3DSample) + OnExit reset"
```

---

### Task 6: Retire Render3DSample + integrate concurrent work

**Files:** Delete `Render3DSample/`; modify `KhaozEngine.slnx`, `.vscode/launch.json`, `README.md`.

- [ ] **Step 1: Integrate concurrent work FIRST**

```bash
git fetch
git log --oneline origin/main -1
```
If `origin/main` advanced, `git merge origin/main`, resolve `KhaozEngine.slnx`/`.vscode/launch.json`/`README.md`, rebuild the merged `.slnx`.

- [ ] **Step 2: Delete + deregister**

```bash
git rm -r Render3DSample
```
- `KhaozEngine.slnx`: remove the `Render3DSample` `<Project>` line.
- `.vscode/launch.json`: remove the "Run Render3DSample" config.

- [ ] **Step 3: README**

Remove the `Render3DSample` samples-table row, drop it from the repo-layout block, and remove the `Render3DSample --smoke` note (the whole "`Render3DSample` also takes `--smoke` ..." block). Fold its post-fx into the Showcase 3D-room description if useful (the 3D room now has O/A/C/R/P). Leave the networked/server/snapshot rows.

- [ ] **Step 4: Build gate + grep**

```bash
dotnet build KhaozEngine.slnx
grep -rn "Render3DSample" --include=*.json --include=*.md --include=*.slnx . | grep -v obj
```
Solution builds; grep returns no live references (CHANGELOG history + this branch's design/plan docs are fine; provenance comments referencing Render3DSample as the post-fx origin are acceptable).

- [ ] **Step 5: Full test + commit**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add -A
git commit -m "showcase: retire Render3DSample (post-fx folded into the 3D World room)"
```

Manual validation handoff (give the user this one-click boot command, do NOT run it yourself):

```bash
dotnet run --project /Users/antonio/KhaozEngine/.claude/worktrees/feature+showcase-b2b/KhaozEngine.Showcase/KhaozEngine.Showcase.csproj -c Debug
```

---

## Self-Review

**Spec coverage:**
- Goal 1 (clearing + scatter hole) -> Task 2. ✓
- Goal 2 (7 buildings placed, rendered, solid) -> Tasks 1 (assets) + 3 (place/render) + 4 (collision). ✓
- Goal 3 (post-fx fold + OnExit reset) -> Task 5. ✓
- Goal 4 (retire Render3DSample, builds green) -> Task 6. ✓
- Concurrent-dev integration -> Task 6 Step 1. ✓
- Non-goals (networked room, water/shadows, engine change, model-viewer room) -> absent. ✓

**Placeholder scan:** Task code is concrete except the tunable town/building coordinates + `townHeight`/`RenderWidth` defaults, which are explicitly marked "tune by playtest / read the default" - inherent to placing content against a live terrain, not a hidden gap. Each such spot names how to resolve it (sample the ground, read the field default).

**Type consistency:** `TownBuilding(Id,X,Z,Yaw,Scale)`, `_buildingMeshes`/`_buildingPlacements`, `PropPlacement` ctor, `PropCollisionFormat.LoadDirectory` -> `PhysicsShapeScale.Uniform` -> `_physics.AddStatic(shape, Pose)`, `post` = `_scene.Post`, `_palIdx` - used consistently across Tasks 3-5.
