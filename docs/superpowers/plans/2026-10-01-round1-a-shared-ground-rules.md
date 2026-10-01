# Round 1 A: Shared Ground and Water Rules Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the full-detail ground triangle rule and the water body rule from `KhaozEngine.TileWorld.Render3D` into GPU-free `KhaozEngine.TileWorld`, with render consuming them and its geometry byte-identical.

**Architecture:** Pin today's mesher and water output with byte-exact hash goldens first. Then add `TileGroundTriangles` and `TileWaterBodies` to `KhaozEngine.TileWorld`, prove them equal to the mesher's output, and switch `TileGroundMesher` and `TileWaterPlanes` to consume them. The goldens must not move. Public render API stays, as forwarders where a type moved.

**Tech Stack:** C#/.NET 10, xUnit, KhaozEngine 20.16.0 opening 20.17.0.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-1-DESIGN-2026-10-01.md`, sections 1 (Ground, Water) and 2, decision D2. Branch 1 of 4 in that note's "Sequencing and release". Plans B, C and D are siblings in this folder.

## Global Constraints

- Work in `/Users/antonio/KhaozEngine/.worktrees/round1-a` on `feature/round1-shared-ground-rules`, created from current `origin/main`. Read `AGENTS.md` and `docs/CONTRIBUTOR-RULES.md` (engine code and test contracts, version rules) first.
- The geometry the mesher emits must stay byte-identical. A moved golden is a defect, never a fixture to refresh.
- Public API of `TileGroundMesher` and `TileWaterPlanes` stays source compatible. Moved members keep forwarders.
- New behaviour gets headless tests in the matching test project. Test namespaces stay under `KhaozEngine.Tests.*`.
- KESIZE: put new behaviour in new types. Never grow `.filesize-baseline`. Check `sh scripts/check-file-size.sh --tree`.
- Warnings are errors. No em or en dashes, no prose semicolons in Markdown or comments.
- Version: this is the first package-bearing branch of the round, so it opens 20.17.0 (re-check `main` and tags first, take the next free version if 20.17.0 is gone) with its `CHANGELOG.md` entry in the same commit, and updates every declaration `scripts/check-doc-versions.sh` guards. Plans B, C and D ride it.
- One agent builds or tests at a time on the dev Mac. Focused tests per task. The full Release suite (`dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`) runs once, in Task 5. Never loop tests.
- Commit subjects use `area(scope): summary`. The orchestrator merges and pushes `main` and packs the feed. Workers never push, pack or tag.

## Review Focus

1. **An overlay id with no render material slot.** The mesher keys "overlay present" on the material slot (`TileGroundMesher.cs:271-277`), and `TileRaycast` keys it on the raw id (`TileRaycast.cs:83`). The shared rule must reproduce the mesher exactly, so a catalog where those differ still meshes the same. Owned by Task 2 `TileGroundTrianglesTests.AnOverlayWithoutAMaterialSlotCutsExactlyAsTheMesherDoes`.
2. **Regions at negative coordinates and region edges.** Corner heights are edge-extended across regions and region-local positions use `cornerX - originX`. Owned by Task 2 `TileGroundTrianglesTests.NegativeAndEdgeRegionsMatchTheMesher` and the Task 1 goldens.
3. **Water bodies touching a region border.** Bodies are clipped per region, and the rim must read across the clip as today. Owned by Task 4 `TileWaterBodiesTests.ABodyCrossingARegionBorderKeepsTodaysPerRegionRim`.
4. **The coarse level of detail.** `AddCoarse4` falls back to `AddTile` for incompatible cells, so moving the full-detail path must not change Coarse4 output. Owned by the Task 1 Coarse4 goldens.
5. **Smooth versus face normals and feathered overlays.** Normals, weights, jitter and feather stay render-side, but their inputs (the lattice points) now come from the shared rule. Owned by the Task 1 goldens, which hash whole vertex bytes, not just positions.

---

### Task 1: Pin today's ground and water output with byte-exact goldens

**Files:**
- Create: `KhaozEngine.Render.Tests/TileWorld/TileGroundGeometryGoldenTests.cs`

**Interfaces:**
- Produces golden hashes for later tasks to keep green. No production code.

- [ ] **Step 1: Write the golden tests.**
  - `MesherOutputIsByteIdentical` is a `[Theory]` over the Patchwork, Greybox and River test worlds (`TileRenderTestData`), `TileGroundLod.Full` and `TileGroundLod.Coarse4`, with smooth normals on and off, and feathering on for the Patchwork world.
  - Each case builds every region and plane through the public `TileGroundMesher.Build` and SHA-256 hashes the concatenated vertex bytes (every field the mesh carries) and index bytes, in region order sorted by `RegionCoord`.
  - The expected hash per case is a string literal captured from current `main`.
  - `WaterPlanesAreByteIdentical`: SHA-256 over `TileWaterPlanes.Collect` output (rect and surface Y as bytes) for every region of River, a literal.
- [ ] **Step 2: Capture the literals on unchanged code.** Run each case once, paste the hashes, and re-run. Command: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~TileGroundGeometryGoldenTests"`. Expected: PASS with a non-zero count.
- [ ] **Step 3: Commit.** Message: `test(tileworld): pin ground and water geometry bytes before the rule move`.

### Task 2: `TileGroundTriangles` in TileWorld

**Files:**
- Create: `KhaozEngine.TileWorld/TileGroundTriangles.cs`
- Create: `KhaozEngine.TileWorld/TileGroundMesh.cs`
- Test: `KhaozEngine.TileWorld.Tests/TileWorld/TileGroundTrianglesTests.cs`
- Test: `KhaozEngine.Render.Tests/TileWorld/TileGroundTrianglesParityTests.cs`

**Interfaces:**
- Produces in namespace `KhaozEngine.TileWorld`:
  - `public static bool TileGroundTriangles.IsDrawable(TileWorldDocument document, int worldX, int worldZ, int plane)`: underlay not 0 and `TileSettings.NoDraw` not set, exactly `TileGroundMesher.IsDrawable` today.
  - `public static bool TileGroundTriangles.TryDescribe(TileWorldDocument document, TileWorldCatalogs catalogs, int worldX, int worldZ, int plane, out TileGroundCell cell, Span<TileLatticeTriangle> triangles)`: false when not drawable. Otherwise the cut shape, rotation, `SplitSwNe` and triangle count, decided exactly as `TileGroundMesher.AddTile` does (`TileGroundMesher.cs:256-279`).
  - `public readonly record struct TileGroundCell(TileOverlayShape Cut, int Rotation, bool SplitSwNe, int TriangleCount)`.
  - `public static Vector3 TileGroundTriangles.LatticePosition(TileWorldDocument document, int worldX, int worldZ, int plane, TileLatticePoint point, int originX, int originZ)`: a corner taken as it stands, a mid-edge point as `(a + b) * 0.5f`, positioned through `TileWorldSpace.ToWorld(cornerX - originX, heightCm * 0.01f, cornerZ - originZ, tileSize)`, matching `TileGroundCornerCache.Point` and `TileGroundMesher.Overlays.cs:67-74` and `:189-198` bit for bit.
  - `public static TileGroundMesh TileGroundTriangles.Build(TileWorldDocument document, TileWorldCatalogs catalogs, RegionCoord region, int plane)`: full detail only, region-local positions, triangles counter-clockwise in tile space as the mesher emits them, in the mesher's tile order (`lz` outer, `lx` inner).
  - `public sealed class TileGroundMesh { public RegionCoord Region; public int Plane; public Vector3[] Positions; public int[] Indices; }` as read-only properties.
  - Note: controller ruling A3 removed the `TileWorldCatalogs catalogs` parameter from `TryDescribe` and `Build` above, so the shipped signatures take none: `TryDescribe(document, worldX, worldZ, plane, out cell, triangles)` and `Build(document, region, plane)`.

- [ ] **Step 1: Write the failing tests.**
  - **`TileGroundTrianglesTests`** (TileWorld.Tests, own small worlds from `TileWorldTestData.FlatWorld`):
    - `AFlatTileIsTwoTrianglesAtItsHeight`
    - `ANoDrawTileAndAnUnderlayZeroTileAreSkipped`
    - `ASlopedTileSplitsOnTheDiagonalTheMesherChooses`
    - `ACutOverlayEmitsFourTrianglesWithMidpointCorners`
    - `AnOverlayWithoutAMaterialSlotCutsExactlyAsTheMesherDoes` (Review Focus 1)
    - `NegativeAndEdgeRegionsMatchTheMesher` (Review Focus 2, comparing against values worked out from the rule)
  - **`TileGroundTrianglesParityTests`** (Render.Tests): `SharedPositionsEqualTheMesherFullDetailPositions`, a `[Theory]` over Patchwork, Greybox and River. For every region and plane, `TileGroundTriangles.Build` positions and indices equal, bit for bit, the positions and indices of `TileGroundMesher.Build(..., TileGroundLod.Full)` with feathering off.
- [ ] **Step 2: Run the tests to check they fail.** Run `dotnet test KhaozEngine.TileWorld.Tests/KhaozEngine.TileWorld.Tests.csproj -c Release --filter "FullyQualifiedName~TileGroundTriangles"` and the Render.Tests parity filter. Expected: build failure.
- [ ] **Step 3: Implement** `TileGroundTriangles` and `TileGroundMesh`, reproducing the mesher's decisions exactly. Read the mesher's slot keying for "overlay present" and express the same condition from catalog data alone. If it depends on render-only state, stop and report.
- [ ] **Step 4: Run the tests to check they pass.** Same commands. Expected: PASS, non-zero.
- [ ] **Step 5: Commit.** Message: `feat(tileworld): the ground triangle rule lives in TileWorld`.

### Task 3: The mesher consumes the shared rule

**Files:**
- Modify: `KhaozEngine.TileWorld.Render3D/TileGroundMesher.cs` (`IsDrawable` around 245, `AddTile` around 256-279)
- Modify: `KhaozEngine.TileWorld.Render3D/TileGroundMesher.Overlays.cs` (`AddCutTile` around 17-63, `At` around 67-74, `Midpoint` around 189-198)
- Modify: `KhaozEngine.TileWorld.Render3D/TileGroundCornerCache.cs` (`Point` around 72-73) if its position rule moves
- Modify: the `IsDrawable` callers `TileWaterPlanes.cs:235`, `TileOverlayBoundary.cs:103`, `TileWorldView.Picking.cs:105`

**Interfaces:**
- Consumes Task 2.
- `TileGroundMesher.IsDrawable` stays `internal` as a one-line forwarder to `TileGroundTriangles.IsDrawable`, or callers switch to the public rule. Pick one and use it everywhere.

- [ ] **Step 1:** Route the mesher's per-tile decision (`swne`, `cut`, `Triangulate`) and its lattice positions through `TileGroundTriangles.TryDescribe` and `LatticePosition`. Normals, weights, slots, jitter, feather and the Coarse4 transition cells stay where they are. The corner memo stays, because it holds the same `CornerHeightCm` values.
- [ ] **Step 2:** Run the Task 1 goldens, the Task 2 parity tests and every existing mesher test: `dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~TileWorld"`. Expected: PASS, every golden unchanged.
- [ ] **Step 3: Commit.** Message: `refactor(tileworld): the ground mesher draws from the shared triangle rule`.

### Task 4: `TileWaterBodies` in TileWorld

**Files:**
- Create: `KhaozEngine.TileWorld/TileWaterBodies.cs`
- Modify: `KhaozEngine.TileWorld.Render3D/TileWaterPlanes.cs` (pure members around 27, 55-68, 102-139, 150-195, 234-248 move out)
- Test: `KhaozEngine.TileWorld.Tests/TileWorld/TileWaterBodiesTests.cs`

**Interfaces:**
- Produces in namespace `KhaozEngine.TileWorld`:
  - `public static class TileWaterBodies` with `public const float SurfaceDropMetres = 0.02f`
  - `public static bool IsWater(TileWorldDocument document, TileWorldCatalogs catalogs, int worldX, int worldZ, int plane)`
  - `public static IReadOnlyList<IReadOnlyList<TileRect>> Components(bool[,] mask)`
  - `public static IReadOnlyList<TileRect> Rectangles(bool[,] mask)`
  - `public static IReadOnlyList<TileWaterBody> Collect(TileWorldDocument document, TileWorldCatalogs catalogs, RegionCoord region, int plane)`
  - `public readonly record struct TileWaterBody(IReadOnlyList<TileRect> Rects, float SurfaceY)`
  - `RimHeight` stays private inside the type.
- `TileWaterPlanes` keeps `SurfaceDropMetres`, `Components` and `Rectangles` as public forwarders, and its `Collect` builds planes from `TileWaterBodies.Collect`.

- [ ] **Step 1: Write the failing tests.** Port the pure assertions from `KhaozEngine.Render.Tests/TileWorld/TileWaterPlanesTests.cs` (components, rectangles, rim) to `TileWaterBodiesTests`, and add `ABodyCrossingARegionBorderKeepsTodaysPerRegionRim` (Review Focus 3) and `ANoDrawWaterTileIsNotWater`.
- [ ] **Step 2: Run the tests to check they fail.** Expected: build failure.
- [ ] **Step 3: Implement** `TileWaterBodies` and switch `TileWaterPlanes.Collect` to it.
- [ ] **Step 4: Run** the TileWorld.Tests filter `FullyQualifiedName~TileWaterBodies`, then Render.Tests `FullyQualifiedName~TileWaterPlanes|FullyQualifiedName~TileGroundGeometryGoldenTests`. Expected: PASS, the water golden unchanged.
- [ ] **Step 5: Commit.** Message: `feat(tileworld): the water body rule lives in TileWorld`.

### Task 5: Docs, version and full verification

**Files:**
- Modify: `KhaozEngine.TileWorld/README.md`, `KhaozEngine.TileWorld.Render3D/README.md` (around 148-181), `docs/USING-KHAOZENGINE.md` (around 9379, plus a short section naming the shared rules)
- Modify: `Directory.Build.props` (`KhaozEngineVersion` to 20.17.0), `CHANGELOG.md` (new 20.17.0 entry: shared ground triangle and water body rules), and every declaration `scripts/check-doc-versions.sh` names

- [ ] **Step 1:** Write the docs and the version bump in one commit. The changelog entry opens the round and says the other three branches ride it. Commit message: `release(20.17.0): open the continuous host round with shared ground and water rules`.
- [ ] **Step 2: Full verification**, from the worktree root:
  ```sh
  mkdir -p local-feed
  dotnet build KhaozEngine.slnx -c Release
  dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
  sh scripts/check-dashes.sh --tree
  sh scripts/check-prose.sh --tree
  sh scripts/check-file-size.sh --tree
  bash scripts/check-doc-versions.sh
  ```
  Expected: zero warnings, zero failures, every guard passes. Report any pre-existing flake by name with its issue, and do not loop.
