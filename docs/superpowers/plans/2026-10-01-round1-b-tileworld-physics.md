# Round 1 B: TileWorld Physics Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new opt-in package `KhaozEngine.TileWorld.Physics` turns an authored tile world into static physics colliders, a floor sampler and a water medium sampler, so continuous character movement runs on it.

**Architecture:** An optional archetype `collisionHeight` goes into the catalog first. Then the package describes colliders as data (`TileWorldColliders.Build`), hashes them, and only then registers them with any `IPhysicsWorld`. The floor and water samplers read the shared ground and water rules from plan A, so feet, rays and drawn ground agree. A `ke-tileedit` verb pair fills the new height from mesh bounds.

**Tech Stack:** C#/.NET 10, xUnit, `KhaozEngine.Physics` seam (tests on `KhaozEngine.Physics.Bepu`), `KhaozEngine.Locomotion`, riding 20.17.0.

**Spec:** `docs/design/CONTINUOUS-HOST-ROUND-1-DESIGN-2026-10-01.md`, section 1 and decisions D1, D3 and D4. Branch 2 of 4. It needs plan A merged on `main` first.

## Global Constraints

- **Worktree:** `/Users/antonio/KhaozEngine/.worktrees/round1-b` on `feature/round1-tileworld-physics`, created from `origin/main` after plan A is merged. Read `AGENTS.md`, `docs/CONTRIBUTOR-RULES.md` and `docs/DEPENDENCY-SEAMS.md` first.
- **Package references:** `KhaozEngine.TileWorld.Physics` references `KhaozEngine.TileWorld`, `KhaozEngine.Physics` and `KhaozEngine.Locomotion` only. No Render3D, no Bepu. It goes on the `OptInBackends` list and in no umbrella.
- **Internals:** `KhaozEngine.TileWorld` grants it internals (`InternalsVisibleTo`) for `TileObjectPlacement.PlanarBasis` and `AnchorPlanar`. Quarter turns never use trigonometry.
- **Defaults:**
  - `TileColliderOptions.WallThickness` = 0.1 m
  - `TileColliderOptions.BlockedHeight` = the document's `PlaneHeight` (3 m)
  - walk-surface box thickness = 0.1 m
  - water surface = the body's rim less `TileWaterBodies.SurfaceDropMetres` (0.02 m)
- **Scope:** plane 0 plus walk surfaces only. Roofs get no collider. No `.coll` support in this round (D4).
- **Missing heights:** building a `Solid`, `Diagonal`, `Wall` or `WallCorner` archetype with no `collisionHeight` throws, naming the archetype id.
- **Movement types:** an in-water `MovementMedium` is always built through its constructor, never `default`.
- **Version:** ride the staged 20.17.0. Extend its `CHANGELOG.md` entry, do not bump.
- **KESIZE:** `ArchitectureTests.cs` has about 24 lines of headroom. Keep additions to the `OptInBackends` entry and one references test. Never grow `.filesize-baseline`.
- **Builds and tests:** one building agent at a time, focused tests per task, and the full suite once in Task 7. Never loop tests. Workers never push, pack or tag.
- **Prose:** no em or en dashes, no prose semicolons.

## Review Focus

1. **Bepu meshes are one-sided.** A ground mesh registered with tile-space winding lets rays and capsules fall through. Owned by Task 5 `TileWorldPhysicsQueryTests.ADownwardRayHitsTheGroundEverywhereTheSamplerSaysItIs`.
2. **A deck or crate top must not become the floor.** The floor sampler reads only the drawn ground, and decks support bodies only as physics statics. Owned by Task 5 `CharacterOnTileWorldTests.ABodyCanWalkUnderADeckAndStandOnIt`.
3. **Floating origin.** Registration subtracts `world.Origin`, so a rebased world still matches. Owned by Task 5 `TileWorldPhysicsQueryTests.ARebasedWorldStillAgreesWithTheSampler`.
4. **Doorways.** A 1 m door gap between wall boxes must pass the default capsule (radius 0.4). Owned by Task 5 `CharacterOnTileWorldTests.ACapsulePassesADoorwayAndIsStoppedByTheWallBesideIt`.
5. **Catalog round trip.** The format-preserving writer must not rewrite a catalog it did not change. Owned by Task 6 `CatalogWriterTests.AnUnchangedCatalogWritesBackByteIdentical`.

---

### Task 1: The optional archetype `collisionHeight`

**Files:**
- Modify: `KhaozEngine.TileWorld/TileWorldCatalogs.cs` (record around 56-83, `AddArchetype` around 223-233, validation around 240-255, `Merge` around 193)
- Modify: `KhaozEngine.TileWorld/tileworld.catalog.schema.json` (archetype properties around 27-53)
- Modify: `KhaozEngine.TileWorld/TileWorldHash.cs` (archetype line around 107-124)
- Test: `KhaozEngine.TileWorld.Tests/TileWorld/TileWorldCatalogsTests.cs`, `TileWorldHashTests.cs`

**Interfaces:**
- Produces `TileObjectArchetype.CollisionHeight` as a `float?` in metres. JSON name `collisionHeight`. Validated finite and greater than 0 when present.
- Hash: when non-null, a distinct marker line (not the `"w "` walk-surface marker) with the value's invariant bytes. When null, nothing is written and `SchemeVersion` stays 2.

- [ ] **Step 1: Write the failing tests.**
  - `ACatalogWithCollisionHeightLoadsIt`
  - `ANonFiniteOrNonPositiveCollisionHeightIsRejected`
  - `AnUnknownFieldIsStillRejected` (the schema keeps `additionalProperties: false`)
  - `GreyboxHashIsUnchanged` (the pinned `541ac513...` literal does not move)
  - `AHeightChangesTheCatalogHash`
  - `AbsentAndNullHeightsHashAlike`
- [ ] **Step 2: Run** `dotnet test KhaozEngine.TileWorld.Tests/KhaozEngine.TileWorld.Tests.csproj -c Release --filter "FullyQualifiedName~TileWorldCatalogs|FullyQualifiedName~TileWorldHash"`. Expected: FAIL.
- [ ] **Step 3: Implement** the field, schema, validation and hash marker.
- [ ] **Step 4: Run** the same command. Expected: PASS, non-zero.
- [ ] **Step 5: Commit.** Message: `feat(tileworld): archetypes can declare a collision height`.

### Task 2: Package and test project skeleton

**Files:**
- Create: `KhaozEngine.TileWorld.Physics/KhaozEngine.TileWorld.Physics.csproj`, `KhaozEngine.TileWorld.Physics/README.md`
- Create: `KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj` (`IsPackable=false`, `RootNamespace=KhaozEngine.Tests`, referencing the package and `KhaozEngine.Physics.Bepu`)
- Modify: `KhaozEngine.TileWorld/KhaozEngine.TileWorld.csproj` (`InternalsVisibleTo` for the new package)
- Modify: `KhaozEngine.slnx`, root `README.md` (catalog row, the "in no umbrella" paragraph, repo layout), `docs/DEPENDENCY-SEAMS.md` (an edge block under "Tile world package edges" and the "Where to look" table)
- Modify: `KhaozEngine.Tests/ArchitectureTests.cs` (`OptInBackends` around 35-55, one test `TileWorldPhysics_ReferencesOnlyTileWorldPhysicsAndLocomotion`)
- Model commit: `6e68ab3c7` (Identity.Exchange).

- [ ] **Step 1:** Add the skeleton with one placeholder-free public type, `TileColliderOptions`:
  - `public sealed record TileColliderOptions`
  - `public float WallThickness { get; init; } = 0.1f`
  - `public float? BlockedHeight { get; init; }`, where null means the document's plane height
  - `public float WalkSurfaceThickness { get; init; } = 0.1f`
- [ ] **Step 2:** Run `dotnet build KhaozEngine.slnx -c Release`, then `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests"` and `bash scripts/check-doc-versions.sh`. Expected: PASS, zero warnings.
- [ ] **Step 3: Commit.** Message: `build(tileworld-physics): an opt-in package for tile world colliders`.

### Task 3: Describe the colliders

**Files:**
- Create: `KhaozEngine.TileWorld.Physics/TileWorldColliders.cs`, `TileCollider.cs`, `TileColliderKind.cs`, `TileColliderBuilder.Ground.cs`, `TileColliderBuilder.Objects.cs` (split by concern, one responsibility each)
- Test: `KhaozEngine.TileWorld.Physics.Tests/TileWorldCollidersTests.cs`, `TileWorldPhysicsTestData.cs`

**Interfaces:**
- Consumes plan A's `TileGroundTriangles.Build` and `IsDrawable`, Task 1's `CollisionHeight`, `TileFootprint.Of`, `TileObjectPlacement` (`AnchorPlanar`, `PlanarBasis`, `YawRadians`) and the baker's `WallFacing` rule.
- Produces:
  - `public sealed class TileWorldColliders` with `static Build(TileWorldDocument document, TileWorldCatalogs catalogs, TileColliderOptions? options = null)`, `IReadOnlyList<TileCollider> Colliders` and `byte[] Hash`. Task 4 adds `TileGroundSampler Ground` and `TileMediumSampler Medium`, and Task 5 adds `TileColliderRegistration AddTo(IPhysicsWorld world)`, so each task compiles on its own.
  - `public readonly record struct TileCollider(TileColliderKind Kind, PhysicsShape Shape, Pose Pose)`, with `Pose` absolute.
  - `public enum TileColliderKind : byte { Ground, Wall, Blocked, Object, WalkSurface }`.
- **Rules:**
  - **Ground:** one `TriangleMeshShape` per region on plane 0, from `TileGroundTriangles.Build`, with each triangle's second and third index swapped for Bepu's one-sided front face (the `TerrainChunkCollision.cs:75-94` swap). Posed at the region origin.
  - **Blocked:** one box per tile where `underlay == 0` or `TileSettings.Blocked`, from the tile's lowest corner to `BlockedHeight` above it.
  - **Walls:** from the placed `Wall` and `WallCorner` objects, never the map's mirrored flags. One box per blocked edge on the anchor tile, `WallThickness` thick, centred on the edge, `CollisionHeight` tall from the ground under the edge.
  - **Objects:** `Solid` gets a box over the rotated footprint. `Diagonal` gets a box over the anchor tile only. Each is `CollisionHeight` tall from the drawn ground at the anchor, yawed with `PlanarBasis`.
  - **Walk surfaces:** a box `WalkSurfaceThickness` thick whose top is the surface height, over the surface rectangle resolved as `TileWalkSurfaces` resolves it.
  - **Roofs:** none.
  - **Order:** canonical, by kind, then plane, region (sorted `RegionCoord`), tile Z, tile X and object id.
  - **Hash:** SHA-256 over each collider's kind byte, its `PropCollisionFormat.Write` bytes and its pose's twelve floats as little-endian bytes.

- [ ] **Step 1: Write the failing tests.**
  - `AFlatWorldIsOneGroundMeshPerRegion`
  - `GroundTrianglesAreWoundForAOneSidedTopFace` (each triangle's geometric normal points +Y after the swap)
  - `AVoidOrBlockedTileGetsABlockedBox`
  - `AWallObjectGetsOneEdgeBoxAndAWallCornerTwo`
  - `ARotatedSolidObjectCoversItsRotatedFootprint`
  - `ADiagonalObjectCoversOnlyItsAnchorTile`
  - `ARoofGetsNoCollider`
  - `AWalkSurfaceIsAThinBoxAtItsHeight`
  - `AMissingCollisionHeightThrowsNamingTheArchetype`
  - `TheHashIsStableAcrossRebuildsAndReloads`
  - `TheHashMovesWhenAHeightChanges`
- [ ] **Step 2: Run** `dotnet test KhaozEngine.TileWorld.Physics.Tests/KhaozEngine.TileWorld.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~TileWorldColliders"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same command. Expected: PASS, non-zero.
- [ ] **Step 5: Commit.** Message: `feat(tileworld-physics): describe a tile world as colliders with a stable hash`.

### Task 4: Floor and water samplers

**Files:**
- Create: `KhaozEngine.TileWorld.Physics/TileGroundSampler.cs`, `TileMediumSampler.cs`
- Test: `KhaozEngine.TileWorld.Physics.Tests/TileSamplerTests.cs`

**Interfaces:**
- Produces `public sealed class TileGroundSampler`:
  - `float HeightAt(float worldX, float worldZ)` and `Vector3 NormalAt(float worldX, float worldZ)`, analytic over plan A's triangles on plane 0.
  - `Func<float, float, float> HeightDelegate` and `Func<float, float, Vector3> NormalDelegate`.
  - Off the world, it returns the nearest drawn tile's edge height.
- Produces `public sealed class TileMediumSampler`:
  - `MovementMedium MediumAt(float worldX, float worldZ, float feetY)` returns `new MovementMedium(surfaceY, true)` over a water body from `TileWaterBodies.Collect`, else `MovementMedium.Dry`.
  - `Func<float, float, float, MovementMedium> Delegate`.
  - Wade scale is left to `MoveTuning`.

- [ ] **Step 1: Write the failing tests.**
  - `HeightMatchesTheSharedTrianglesOnAGrid` (within 1 mm, sloped and cut test world)
  - `HeightIsNotTheBilinearHeightWhereTheyDiffer`
  - `NormalPointsUpOnFlatGround`
  - `AFeetPointInARiverIsInWaterAtTheRimLessTwoCentimetres`
  - `APointBesideTheRiverIsDry`
  - `AnInWaterMediumHasAPositiveWadeScale`
- [ ] **Step 2: Run** the filter `FullyQualifiedName~TileSamplerTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: `feat(tileworld-physics): floor and water samplers from the shared rules`.

### Task 5: Registration, queries and a walking body

**Files:**
- Create: `KhaozEngine.TileWorld.Physics/TileColliderRegistration.cs`
- Test: `KhaozEngine.TileWorld.Physics.Tests/TileWorldPhysicsQueryTests.cs`, `CharacterOnTileWorldTests.cs`

**Interfaces:**
- Produces `TileColliderRegistration AddTo(IPhysicsWorld world)`: `AddStatic(shape, Pose(position - world.Origin, orientation))` for each collider. The result exposes `IReadOnlyList<StaticHandle> Handles`, `Remove()` (removes every handle once) and `Dispose()`, which calls `Remove`.

- [ ] **Step 1: Write the failing tests** (Bepu world).
  - `TileWorldPhysicsQueryTests`:
    - `ADownwardRayHitsTheGroundEverywhereTheSamplerSaysItIs` (within 1 mm, Review Focus 1)
    - `ARebasedWorldStillAgreesWithTheSampler` (Review Focus 3)
    - `RemoveLeavesNoStatics`
  - `CharacterOnTileWorldTests`, driving `CharacterMovement.Step` with `Ground.HeightDelegate`, `Ground.NormalDelegate`, the physics world and `Medium.Delegate`:
    - `ABodyWalksUpASlopeAndStaysOnTheGround`
    - `ACapsulePassesADoorwayAndIsStoppedByTheWallBesideIt` (Review Focus 4)
    - `AWallBlocksFromBothSides`
    - `ABodyCanWalkUnderADeckAndStandOnIt` (Review Focus 2)
    - `WadingSlowsTheBody`
    - `ABlockedTileStopsTheBody`
- [ ] **Step 2: Run** the filter `FullyQualifiedName~TileWorldPhysicsQueryTests|FullyQualifiedName~CharacterOnTileWorldTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: `feat(tileworld-physics): register tile colliders and walk a body across them`.

### Task 6: `ke-tileedit` height verbs and a format-preserving catalog writer

**Files:**
- Create: `KhaozEngine.TileEdit.Tool/Tools/ArchetypeHeightTools.cs`, `KhaozEngine.TileEdit.Tool/CatalogWriter.cs`
- Modify: `KhaozEngine.TileEdit.Tool/Tools/McpBootstrap.cs` (register around 26-38), the tool `README.md` (verb count), root `README.md:111` (the stale verb count)
- Test: `KhaozEngine.TileEdit.Tests/ArchetypeHeightToolsTests.cs`, `CatalogWriterTests.cs`, `McpAdapterTests.cs` (`ExpectedVerbs` around 48)

**Interfaces:**
- `archetype_measure_heights(kitRoot)` is read-only. It returns each archetype id with its measured height (`max.Y` of mesh bounds through `TileObjectBoundsCache` over a `GltfMeshResolver(kitRoot)` with no greybox fallback). A missing mesh is an error entry for that archetype, never a guess.
- `archetype_set_collision_heights(heights, overwrite = false)` writes `collisionHeight` into the archetype entries of the session's catalog files through `CatalogWriter`. It skips archetypes that already have a height unless `overwrite`, and returns what it changed.
- `CatalogWriter` edits archetype entries in place. It keeps property order, indentation, line endings and every untouched byte, and puts a new `collisionHeight` after `collisionKind`.

- [ ] **Step 1: Write the failing tests.**
  - `AnUnchangedCatalogWritesBackByteIdentical` (Review Focus 5)
  - `SettingAHeightAddsOnlyThatProperty`
  - `AnExistingHeightIsKeptWithoutOverwrite`
  - `MeasureReportsAMissingMeshAsAnError`
  - `MeasureReadsTheModelTopAsTheHeight`
  - `ExpectedVerbs` gains both names.
- [ ] **Step 2: Run** `dotnet test KhaozEngine.TileEdit.Tests/KhaozEngine.TileEdit.Tests.csproj -c Release --filter "FullyQualifiedName~ArchetypeHeight|FullyQualifiedName~CatalogWriter|FullyQualifiedName~McpAdapterTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit.** Message: `feat(tileedit): measure and record archetype collision heights`.

### Task 7: Docs, changelog and full verification

**Files:**
- Modify: `KhaozEngine.TileWorld.Physics/README.md` (use, defaults, the floor rule, the one-sided winding note)
- Modify: `KhaozEngine.TileWorld/README.md` (field list around 127)
- Modify: `docs/USING-KHAOZENGINE.md` (a "Physics for a tile world" section near the tile world sections, and the archetype field near 9227)
- Modify: `CHANGELOG.md` (extend 20.17.0), `docs/DEPENDENCY-SEAMS.md` (if not already done)

- [ ] **Step 1:** Write the docs. Commit message: `docs(tileworld-physics): how to give a tile world continuous collision`.
- [ ] **Step 2:** Full verification, as plan A Task 5 Step 2. Expected: zero warnings, zero failures, every guard passing.
