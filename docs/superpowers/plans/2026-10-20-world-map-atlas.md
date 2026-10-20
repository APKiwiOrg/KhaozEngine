# World map atlas implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development to implement this plan task by task.

**Goal:** Add opt-in palette, contours and vector footprint symbols to the shared map renderer.

**Architecture:** Extend the existing source adapter and CPU raster. Defaults remain byte-identical.
The generic geometry contract carries authored shapes without a second renderer or GPU path.

**Tech Stack:** .NET 10, WorldMap, TileWorld.WorldMap, xUnit.

**Spec:** `docs/design/WORLD-MAP-ATLAS-DESIGN-2026-10-20.md`

## Global constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/ink-world-map`, branch `feature/ink-world-map`.
- Read AGENTS.md, contributor code/test and documentation rules, and the spec.
- No consumer names, geography, palettes, screenshots or private asset data in this public repo.
- No dependencies, GPU path changes, default raster changes, raw input, version/tag or pack changes by workers.
- Build/test/render only through build-slot. No stress or loops. Workers run focused tests.
- Workers commit explicit files only. No merging, pushing, releasing or subagents.
- Preserve unrelated work. Reports go to this plan's SDD workspace, captures to `/tmp/khaozengine-map-atlas/`.

## Review focus

1. Default styles must preserve old bytes, not merely look similar.
2. Negative chunk coordinates and contour sampling margins must have no seams.
3. Caller mutation after construction cannot alter palette or symbol geometry during worker reads.
4. Rotation, non-square extents and transparent fills must retain constant stroke width and correct coverage.
5. Source Changed invalidation must refresh styled chunks and reject stale work.

### Task 1: Ground palette and contours

**Files**

- Modify `KhaozEngine.TileWorld.WorldMap/TileWorldMapSource.cs` and `TileWorldMapSurface.cs`.
- Modify `KhaozEngine.WorldMap/WorldMapStyle.cs`, style validation and raster reach.
- Add `KhaozEngine.WorldMap/WorldMapContours.cs` and `Raster/ContourPainter.cs`.
- Modify `KhaozEngine.WorldMap/WorldMapRasterizer.cs` for the optional contour pass.
- Tests in `KhaozEngine.WorldMap.Tests/` and `KhaozEngine.TileWorld.WorldMap.Tests/`.
- Update both package READMEs and relevant consumer guide sections.

**Interfaces**

- `TileWorldMapSource(..., int surfacePlane = 0,
  IReadOnlyDictionary<ushort, Color>? groundPalette = null)`.
- `public sealed record WorldMapContours(float IntervalMetres, Color Color, float WidthPixels = 0.75f)`.
- `WorldMapStyle.Contours { get; init; }`, nullable, default null.
- Consume current source grid and style level/raster scale. No consumer-specific defaults.

- [ ] Add failing tests for selected underlay palette, overlay precedence, fallback, catalog immutability,
  and a caller mutating its dictionary after source construction.
- [ ] Add failing tests for contours on a ramp, correct metre intervals, configured pixel width at
  different raster levels, no water/void/flat-plateau contour, invalid numeric inputs, and matching
  independently rendered neighboring chunks at negative coordinates.
- [ ] Run `build-slot --label atlas-terrain-red -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release` and `build-slot --label atlas-palette-red -- dotnet test KhaozEngine.TileWorld.WorldMap.Tests/KhaozEngine.TileWorld.WorldMap.Tests.csproj -c Release`, filtering the new tests.
  Record expected failing assertions before production implementation.
- [ ] Implement the snapshotted adapter palette and optional contour coverage. Keep legacy sampling
  untouched when null. Account for finite-difference/sample reach in invalidation.
- [ ] Add default-byte compatibility assertions against the existing renderer's unchanged fixtures.
  Cover source change invalidation with contours active using the existing cache test helpers.
- [ ] Run all existing map tests in the owning area projects through build-slot. Record counts and exits.
- [ ] Update package READMEs and the relevant consumer guide examples. No version declarations yet.
- [ ] Commit as `feat(world-map): add map palettes and elevation contours`.

### Task 2: Vector footprint geometry

**Files**

- Add `KhaozEngine.WorldMap/WorldMapSymbolGeometry.cs`.
- Modify `KhaozEngine.WorldMap/WorldMapSymbols.cs`, style validation and raster geometry helpers.
- Modify `Raster/FootprintPainter.cs` and `CoverageSampler.cs` or add a cohesive custom-geometry sampler.
- Tests in `KhaozEngine.WorldMap.Tests/`.
- Update the package README and consumer guide.

**Interfaces**

- `public readonly record struct WorldMapSymbolStroke(Vector2 Start, Vector2 End)`.
- `public sealed class WorldMapSymbolGeometry` with the spec's constructor and read-only collections.
- `WorldMapSymbol.Geometry { get; init; }`, nullable, default null.
- Consume current SizeMetres, rotation, OutlinePixels and footprint raster bounds.

- [ ] Write failing tests for a concave silhouette, an internal ridge stroke, rotated non-square
  geometry, constant pixel stroke width, transparent fill, and building silhouette shadow.
- [ ] Write validation and defensive-copy tests for nonfinite/out-of-range points, degenerate and
  self-crossing outlines, and mutation of caller collections after construction.
- [ ] Run `build-slot --label atlas-symbol-red -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release --filter FullyQualifiedName~SymbolGeometry` and record missing-feature failures.
- [ ] Implement normalized immutable geometry and supersampled coverage. Use existing fill/outline
  composition, source collection, sizing and chunk reach. Keep default bytes unchanged.
- [ ] Prove matching chunk-edge geometry, optional geometry with SizeMetres, source invalidation,
  and no changes to feature anchor/hover semantics.
- [ ] Run the owning area's full map test set through build-slot and update living documentation.
- [ ] Commit as `feat(world-map): rasterize vector footprint symbols`.

### Task 3: Engine integration and release-ready proof

Owned by the orchestrator after independent review of both tasks.

- [ ] Run a full Markdown sweep for new API names and old map limitations. Update docs/INDEX.md.
- [ ] Re-read main/version/tags. Ride a staged version or select the next additive minor, update
  CHANGELOG.md and all guarded version declarations in the same commit.
- [ ] Reconcile current main on this branch. Run the Release solution build and non-LiveSocket full
  suite once through build-slot, full suite in the heavy lane.
- [ ] Run whole-tree dashes, prose, file-size, agent-instruction and doc-version guards.
- [ ] Obtain a clean whole-branch review and independently inspect returned commits.
- [ ] Check release lock, merge and push main. Run the guarded cumulative local-feed pack from main.
- [ ] Report exact release candidate and verification evidence. Do not tag automatically.
