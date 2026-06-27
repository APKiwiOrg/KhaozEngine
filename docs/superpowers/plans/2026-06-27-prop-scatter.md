# Prop scatter + asset pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Forest the walkable terrain: a deterministic coordinate-hash prop scatter + a glTF asset pipeline (scale-normalize + the 1.8 m validation guard) + GPU-instanced rendering, so you walk through the clearing prototyped in Blender.

**Architecture:** Three additive surfaces in three existing packages. `KhaozEngine.Render3D` gets the asset pipeline (`AssetManifest` + `PropLoader.Normalize`/`LoadProp` + `PropValidation`) beside `GltfLoader`. `KhaozEngine.Terrain` (render-free leaf) gets `PropScatter.Generate` returning `PropPlacement[]` via `TerrainNoise.Hash2`. `KhaozEngine.Terrain.Render3D` gets the instanced render helper (`PropRenderer.Queue` + `Scene3D.DrawProps`) that distance-culls and queues `SceneInstances.Add`. The `TerrainWalkSample` loads a small committed CC0 Quaternius kit through the pipeline and scatters it around the clearing.

**Tech Stack:** net10.0, C#, SharpGLTF (existing), System.Text.Json (shared framework), xUnit, no new package dependency, no new NuGet package.

## Global Constraints

- One **minor** version bump only: `7.45.0` -> `7.46.0` in `Directory.Build.props` `<KhaozEngine5xVersion>`. Additive public API in three EXISTING packages (Render3D, Terrain, Terrain.Render3D). No new package, so no package-catalog churn.
- TDD: every new behaviour ships with a headless test in `KhaozEngine.Tests`. NO GPU device in tests (assert against `SceneInstances`/CPU mesh data only).
- Input rule: only `AppWindow` touches windowing input; nothing here touches input statics (scatter + pipeline are pure; the sample reads `Input` via the existing `GameApp3D` snapshot path).
- Stay in scope. Do NOT build: a meshopt decoder (require decompressed glTF), mesh-LOD/impostors, PBR splat textures, prop/obstacle collision, chunk streaming, animated props.
- No em-dashes anywhere (commits, docs, comments).
- Determinism: scatter placement for a coordinate depends only on `(cell, seed)`, identical regardless of area tiling (streaming-ready). Reuse `TerrainNoise.Hash2`.
- Parity target: `ScatterConfig` defaults reproduce the greybox forest ring from `tools/blender/make_clearing_greybox.py` (step 4.5 m, clearing radius 26 m, keep 0.55, scale 0.8-1.35, off-mountain at height > 6 m).
- Assets: a few CC0 Quaternius decompressed `.glb` + `props.manifest.json` + `CREDITS.md` committed under the sample (~1-2 MB, plain commit, no LFS).

## File structure

- Create `KhaozEngine.Render3D/Models/AssetManifest.cs` — `AssetManifest` + `AssetEntry` (JSON parse, relative-path resolve).
- Create `KhaozEngine.Render3D/Models/PropLoader.cs` — `PropLoader` (`Normalize`/`LoadProp`/`LoadPropWithMaterial`) + `PropValidation`.
- Create `KhaozEngine.Terrain/PropScatter.cs` — `PropScatter` + `PropPlacement` + `ScatterConfig` + `BiomeScatterRule` + `PropKind` + `RectArea`.
- Create `KhaozEngine.Terrain.Render3D/PropRenderer.cs` — `PropRenderer.Queue` + `Scene3D.DrawProps` extension.
- Create tests: `KhaozEngine.Tests/Render3D/AssetManifestTests.cs`, `KhaozEngine.Tests/Render3D/PropLoaderTests.cs`, `KhaozEngine.Tests/Terrain/PropScatterTests.cs`, `KhaozEngine.Tests/Terrain/PropRendererTests.cs`.
- Modify `TerrainWalkSample/TerrainWalkSample.csproj` (copy assets) + `TerrainWalkSample/Program.cs` (load + scatter + draw).
- Create `TerrainWalkSample/assets/props/{pine_*,oak_*,rock_*}.glb` + `props.manifest.json` + `CREDITS.md`.
- Modify docs: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`.

---

### Task 1: AssetManifest + AssetEntry (Render3D)

**Files:**
- Create: `KhaozEngine.Render3D/Models/AssetManifest.cs`
- Test: `KhaozEngine.Tests/Render3D/AssetManifestTests.cs`

**Interfaces:**
- Produces:
  - `readonly struct AssetEntry { string Id; string File; float HeightMeters; string Source; string License; }`
  - `sealed class AssetManifest { IReadOnlyList<AssetEntry> Props; static AssetManifest Parse(string json, string? baseDir = null); static AssetManifest Load(string path); AssetEntry? Find(string id); }`
  - `File` is resolved: if the raw JSON `file` is relative and a `baseDir` is known, `File` = `Path.Combine(baseDir, raw)`; else the raw value.

JSON shape (root object with a `props` array):
```json
{ "props": [ { "id": "pine_a", "file": "pine_a.glb", "heightMeters": 14, "source": "Quaternius", "license": "CC0" } ] }
```

- [ ] Step 1: failing test `Parse_ReadsEntries` (two entries, asserts Id/HeightMeters/Source/License), `Load_ResolvesRelativeFileAgainstManifestDir`, `Parse_GarbledJson_Throws`, `Find_ReturnsEntryOrNull`.
- [ ] Step 2: run, expect compile failure (types missing).
- [ ] Step 3: implement with `System.Text.Json` (`JsonSerializer.Deserialize` to a private DTO; tolerant camelCase via `PropertyNameCaseInsensitive`). `Parse` throws `InvalidOperationException` wrapping `JsonException` with context. `Load` reads the file (throws clean if missing) and passes `Path.GetDirectoryName(path)` as baseDir.
- [ ] Step 4: run tests, expect PASS.
- [ ] Step 5: commit `render3d(7.46.0): AssetManifest parses prop kit JSON + resolves paths`.

### Task 2: PropValidation + PropLoader.Normalize (Render3D)

**Files:**
- Create: `KhaozEngine.Render3D/Models/PropLoader.cs`
- Test: `KhaozEngine.Tests/Render3D/PropLoaderTests.cs` (Normalize cases)

**Interfaces:**
- Produces:
  - `sealed class PropValidation { float MinHeightMeters=0.1f; float MaxHeightMeters=120f; float MinScale=1e-3f; float MaxScale=1e3f; static readonly PropValidation Default; }`
  - `static GltfMesh PropLoader.Normalize(GltfMesh raw, float heightMeters, PropValidation? validation = null)`
  - Normalize: bounding box over `raw.Vertices` Position; `rawH = maxY - minY`; throw if `rawH <= 1e-6f`. Throw `InvalidOperationException` if `heightMeters` outside `[MinHeightMeters, MaxHeightMeters]`; compute `scale = heightMeters / rawH`; throw if `scale` outside `[MinScale, MaxScale]` (the 1.8 m guard: assets authored ~1u=1m, so an absurd implied scale means wrong units). Re-center X/Z on bbox centre, drop base (minY) to 0, multiply all positions by `scale`. Normals/UV/Color unchanged; Tangent xyz left as-is (uniform scale preserves direction). Keep index buffer identical.

- [ ] Step 1: failing tests:
  - `Normalize_ScalesToDeclaredHeight` (build a 2 m-tall box GltfMesh, declare 14 m -> result height ~14, base at y~0, centred at x/z~0).
  - `Normalize_ImplausibleDeclaredHeight_Throws` (declare 5000 m -> throws).
  - `Normalize_ImplausibleScale_Throws` (raw 5000 units tall, declare 1.8 m -> scale 3.6e-4 < MinScale -> throws).
  - `Normalize_DegenerateMesh_Throws` (flat mesh rawH ~0 -> throws).
- [ ] Step 2: run, expect fail.
- [ ] Step 3: implement Normalize + PropValidation. Helper to build a `GltfMesh` box in the test (positions + 16-bit indices).
- [ ] Step 4: run, expect PASS.
- [ ] Step 5: commit `render3d(7.46.0): PropLoader.Normalize scales to heightMeters + 1.8 m guard`.

### Task 3: PropLoader.LoadProp + LoadPropWithMaterial (Render3D)

**Files:**
- Modify: `KhaozEngine.Render3D/Models/PropLoader.cs`
- Test: `KhaozEngine.Tests/Render3D/PropLoaderTests.cs` (file-path cases, build glb in-process via SharpGLTF)

**Interfaces:**
- Produces:
  - `static GltfMesh PropLoader.LoadProp(AssetEntry entry, PropValidation? validation = null)` = `Normalize(GltfLoader.Load(entry.File), entry.HeightMeters, validation)`, wrapping load IO errors with the entry id/file context.
  - `static (GltfMesh Mesh, GltfMaterialMaps Maps) PropLoader.LoadPropWithMaterial(AssetEntry entry, PropValidation? validation = null)` = load via `GltfLoader.LoadWithMaterial`, normalize the mesh, pass maps through.

- [ ] Step 1: failing tests:
  - `LoadProp_NormalizesInProcessGlb` (build a known-size box glb to temp via SharpGLTF SceneBuilder, entry HeightMeters 11 -> loaded mesh height ~11).
  - `LoadProp_MissingFile_ThrowsWithContext` (entry.File = nonexistent -> throws, message mentions the id).
- [ ] Step 2: run, expect fail.
- [ ] Step 3: implement LoadProp/LoadPropWithMaterial.
- [ ] Step 4: run, expect PASS.
- [ ] Step 5: commit `render3d(7.46.0): PropLoader.LoadProp loads + normalizes a manifest entry`.

### Task 4: PropScatter (Terrain, render-free)

**Files:**
- Create: `KhaozEngine.Terrain/PropScatter.cs`
- Test: `KhaozEngine.Tests/Terrain/PropScatterTests.cs`

**Interfaces:**
- Produces:
  - `readonly struct PropPlacement { string Id; float X, Z, Y, Scale, Yaw; int Variant; }`
  - `readonly struct PropKind { string Id; float Weight; }`
  - `sealed class BiomeScatterRule { BiomeId Biome; float Density=0.55f; PropKind[] Kinds; }`
  - `readonly struct RectArea { float MinX, MinZ, MaxX, MaxZ; }`
  - `sealed class ScatterConfig { int Seed=1337; float CellSize=4.5f; float Jitter=1.6f; float ClearingRadius=26f; System.Numerics.Vector2 ClearingCenter=Zero; float? MaxHeight=6f; float ScaleMin=0.8f; float ScaleMax=1.35f; BiomeScatterRule[] Biomes; static ScatterConfig ForestRing(); }`
  - `static IReadOnlyList<PropPlacement> PropScatter.Generate(TerrainField field, ScatterConfig config, RectArea area)`
- Algorithm per integer cell `(gx,gz)` whose centre `(gx*CellSize, gz*CellSize)` lies in `[MinX,MaxX) x [MinZ,MaxZ)` (half-open -> tiling-invariant):
  - `x = gx*CellSize + Hash2(gx,gz,Seed^S_JX)*Jitter`, `z = gz*CellSize + Hash2(gx,gz,Seed^S_JZ)*Jitter`.
  - `biome = field.SampleBiome(x,z)`; rule = first `Biomes` entry matching biome; if none -> skip.
  - density keep: `u01(Hash2(gx,gz,Seed^S_DEN)) < rule.Density` else skip.
  - `y = field.SampleHeight(x,z)`; skip if `y < field.WaterLevel`; skip if `MaxHeight is float m && y > m`.
  - clearing: skip if `dist((x,z),ClearingCenter) < ClearingRadius`.
  - kind: pick from `rule.Kinds` by normalized weight using `u01(Hash2(gx,gz,Seed^S_KIND))`; `Variant` = bucket index; `Id` = kinds[Variant].Id.
  - `Scale = ScaleMin + u01(Hash2(..S_SCALE))*(ScaleMax-ScaleMin)`; `Yaw = u01(Hash2(..S_YAW))*2*PI`.
  - emit. `u01(h) = h*0.5f + 0.5f` (Hash2 in [-1,1)).
- `ForestRing()` = Meadow rule density 0.55 with Kinds {pine_a .6, pine_b .25, oak_a .1, rock_a .05} (ids match the committed manifest), defaults reproduce the greybox.

- [ ] Step 1: failing tests:
  - `Generate_IsDeterministic` (same field/config/area twice -> identical lists, element-by-element).
  - `Generate_IsTilingInvariant` (area A vs 4 half-open sub-tiles -> same placement set after sort by (X,Z)).
  - `Generate_YEqualsFieldHeight` (every placement Y == field.SampleHeight(X,Z)).
  - `Generate_ExcludesBelowWater` (high WaterLevel field -> 0 placements; or assert all Y >= WaterLevel).
  - `Generate_ExcludesClearingRadius` (all placements outside ClearingRadius of centre).
  - `Generate_DensityWithinTolerance` (flat single-biome field, ClearingRadius 0, MaxHeight null, density 0.5 -> placed/candidate ~0.5 +/- 0.06).
  - Helper: a flat `TerrainField` (single Meadow band, GentleAmplitude 0, WaterLevel very negative).
- [ ] Step 2: run, expect fail.
- [ ] Step 3: implement PropScatter. Internal `u01`. Salt constants distinct ints.
- [ ] Step 4: run, expect PASS.
- [ ] Step 5: commit `terrain(7.46.0): PropScatter deterministic coordinate-hash placement`.

### Task 5: PropRenderer instanced helper (Terrain.Render3D)

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/PropRenderer.cs`
- Test: `KhaozEngine.Tests/Terrain/PropRendererTests.cs`

**Interfaces:**
- Produces:
  - `static int PropRenderer.Queue(SceneInstances instances, IReadOnlyList<PropPlacement> placements, IReadOnlyDictionary<string, MeshHandle> meshes, System.Numerics.Vector3 focus, float drawRadius, Color? tint = null)` — for each placement within `drawRadius` HORIZONTAL (XZ) distance of `focus` AND whose `Id` has a mesh in the map, `instances.Add(handle, TRS(placement), tint ?? White)`. Returns the count queued. Out-of-range and unknown-id placements skipped.
  - `static int Scene3D.DrawProps(this Scene3D scene, IReadOnlyList<PropPlacement> placements, IReadOnlyDictionary<string, MeshHandle> meshes, System.Numerics.Vector3 focus, float drawRadius, Color? tint = null)` — same logic via `scene.Draw(handle, world, tint)`. Shared private `Emit(..., Action<MeshHandle, Matrix4x4> sink)`.
  - `TRS = CreateScale(Scale) * CreateRotationY(Yaw) * CreateTranslation(X, Y, Z)`.
- [ ] Step 1: failing tests (use `SceneInstances` directly, GPU-free):
  - `Queue_InRangeQueued_OutOfRangeCulled` (placements at XZ distance 5 and 500 from focus, drawRadius 50 -> only the near one queued; assert count 1 + the queued matrix translation).
  - `Queue_UnknownId_Skipped` (placement id not in map -> not queued).
  - `Queue_BuildsTRS` (scale + yaw + position reflected in the queued world matrix translation == X,Y,Z; M11 reflects scale*cos(yaw)).
- [ ] Step 2: run, expect fail.
- [ ] Step 3: implement PropRenderer + DrawProps extension.
- [ ] Step 4: run, expect PASS.
- [ ] Step 5: commit `terrain-render3d(7.46.0): PropRenderer queues instanced props with distance cull`.

### Task 6: Commit the CC0 Quaternius kit (sample assets)

**Files:**
- Create: `TerrainWalkSample/assets/props/*.glb`, `props.manifest.json`, `CREDITS.md`
- Modify: `TerrainWalkSample/TerrainWalkSample.csproj` (copy assets to output)

Steps (offline ingest, NOT TDD):
- [ ] Clone `github.com/levy-street/world-of-claudecraft` to a temp dir; copy `public/models/foliage/{pine_*,oak_*,rock_*}.glb` (CC0 Quaternius). Target ~2-3 pines, 1-2 oaks, 1-2 rocks.
- [ ] Decompress each (drops EXT_meshopt_compression): `npx --yes @gltf-transform/cli@latest cp <in>.glb <out>.glb`. Verify no meshopt/draco/ktx2 extension remains and the file loads (a throwaway `GltfLoader.Load` check or `gltf-transform inspect`).
- [ ] Place decompressed `.glb` in `TerrainWalkSample/assets/props/` with stable ids (`pine_a.glb`, `pine_b.glb`, `oak_a.glb`, `rock_a.glb`, ...).
- [ ] Write `props.manifest.json` (root `{ "props": [...] }`) with real `heightMeters` (pine ~14, oak ~11, rock ~2), `source` "Quaternius", `license` "CC0". Ids match `ScatterConfig.ForestRing()` Kinds.
- [ ] Write `CREDITS.md` crediting Quaternius / CC0 with the source URL.
- [ ] Add to csproj: `<None Include="assets/props/**" CopyToOutputDirectory="PreserveNewest" />`.
- [ ] Commit `sample(7.46.0): commit CC0 Quaternius nature kit + manifest + credits`.

### Task 7: Wire the forest into TerrainWalkSample

**Files:**
- Modify: `TerrainWalkSample/Program.cs`

- [ ] Step 1: in `OnLoad`, after the terrain grid: `var manifest = AssetManifest.Load(Path.Combine(AppContext.BaseDirectory, "assets/props/props.manifest.json"));` build `Dictionary<string, MeshHandle>` by `LoadPropWithMaterial(entry)` -> `sc.LoadMesh(mesh, maps)`; `var placements = PropScatter.Generate(_field, ScatterConfig.ForestRing(), new RectArea(-58, -58, 58, 16));`.
- [ ] Step 2: in `OnDraw3D`, after chunks: `scene.DrawProps(_placements, _propMeshes, _character.Position, drawRadius: 90f);`.
- [ ] Step 3: build the sample (`dotnet build TerrainWalkSample`), expect success. (Not unit-tested; the windowed run is the user's manual check.)
- [ ] Step 4: commit `sample(7.46.0): scatter the CC0 forest around the player`.

### Task 8: Release (version bump + docs + pack)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`, `docs/USING-KHAOZENGINE.md`

- [ ] Bump `<KhaozEngine5xVersion>` 7.45.0 -> 7.46.0.
- [ ] `CHANGELOG.md`: newest-first detailed entry (the three new surfaces + the offline gltf-transform ingest note).
- [ ] `CHANGENOTES.md`: one-line digest.
- [ ] Update the 3 guard declarations to 7.46.0: `docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md` "Current released version", `README.md` PackageReference example(s).
- [ ] `docs/USING-KHAOZENGINE.md`: a usage section for `AssetManifest`/`PropLoader`, `PropScatter`, `Scene3D.DrawProps`, plus the offline `gltf-transform cp` decompress note (engine has no meshopt decoder).
- [ ] Run `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` (all green).
- [ ] Run `scripts/check-doc-versions.sh` (passes).
- [ ] `dotnet pack -c Release -o ./local-feed`.
- [ ] Commit `docs(7.46.0): prop scatter + asset pipeline release notes`.
- [ ] Merge to main, repack from main root, `git tag v7.46.0`, push main + tag, clean up worktree + branch.

## Self-review

- Spec coverage: AssetManifest (T1), LoadProp + normalize + 1.8 m validation (T2/T3), PropScatter determinism/density/exclusions/Y + streaming-ready tiling (T4), instanced render helper in-range/cull (T5), committed CC0 kit + manifest + CREDITS (T6), sample forest (T7), minor release + USING doc + ingest note (T8). All spec sections mapped.
- Out-of-scope items (meshopt decoder, LOD, PBR splat, collision, streaming, animation) are NOT tasks. Good.
- Type consistency: `AssetEntry`/`PropValidation`/`PropPlacement`/`ScatterConfig`/`RectArea`/`PropKind`/`BiomeScatterRule` names used identically across tasks. `Hash2(gx,gz,seed)` signature matches the existing `TerrainNoise.Hash2`. `SceneInstances.Add(handle, world, tint)` matches the existing signature.
