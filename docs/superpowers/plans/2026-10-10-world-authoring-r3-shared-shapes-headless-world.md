# World Authoring R3: Shared Shapes and Headless World Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build one GPU-free, immutable static world from a native MapDoc document, identical on client and server, whose physical shapes drive collision, occlusion and capture and whose shared interaction envelopes drive picking, reach and walk-up stance, including stacked cave floors and ceilings.

**Architecture:** A new opt-in package, `KhaozEngine.MapDoc.Physics`, reads collider and selection resources from the verified asset closure, builds placement geometry and interaction envelopes, compiles R2 terrain faces into bounded physics meshes with face provenance, and registers everything into any `IPhysicsWorld`. Ground movement, support and navigation column sampling stay with the #438 contact-classification controller, which consumes these statics. R3 owns geometry, envelopes, physical relations, residency, registration and navigation tile identity.

**Tech Stack:** C# on the repository's .NET target, System.Numerics, System.Text.Json, xUnit, the existing `KhaozEngine.Physics` seam with `KhaozEngine.Physics.Bepu` as the test backend. No new third-party dependency.

**Spec:**
- `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md` (C3, T1 to T9, DG9).
- The R3 owner gate with OA22, Grimhollow `feature/world-authoring` `docs/superpowers/programs/world-authoring/R3-OWNER-GATE.md` and `DECISIONS.md`.
- `docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md` (ownership, body model).
- `docs/superpowers/plans/2026-10-05-world-authoring-r2-authored-terrain-paint.md` (Downstream allocation, R3 row).

This plan replaces `2026-10-05-world-authoring-r3-shared-shapes-headless-builders.md` on `feature/world-authoring`, which was written against 49b045f75 and runs no task as written against v20.30.0.

## Approval and execution gate

The owner approved the R3 gate decisions as OA22 on 2026-10-10 and asked for this rewrite. This plan needs the owner's plan approval before any code. Execution is subagent-driven and serial (OA8 method), with direct bounded verification (OA20). Approval of this plan does not authorize releases, game adoption or native G1b acceptance. Only the owner tags.

## Global Constraints

| Rule | Requirement |
| --- | --- |
| OA1 to OA3 | One authoring tool, free placement, full native swap. No TileWorld or Grimhollow reference from any `KhaozEngine.MapDoc*` project |
| OA22 decision 1 | R3 adds no step limit, ledge rule or support model. Ground support, seating, steps and navigation column sampling belong to #438 |
| OA22 decision 2 | R3 owns navigation tile partition, capture, profile and link identity, affected-tile invalidation and deterministic seams. Incremental rebake orchestration, cross-tile planning budgets and on-demand loading are the separate #1301 work item |
| OA22 decision 3 | Terrain faces install as bounded physics meshes with a triangle to `MapFaceKey` map and explicit refusal, never truncation or a silent fallback |
| OA22 decisions 4 to 11 | Aligned grids, 1 m minimum reach height applied before band clipping, collider-derived envelopes only for solid assets, `AuthoredOpen` portals only, the #438 shell for clearance, released `IPhysicsQueryLease`, solidity from the collider resource, R2 canonical faces without retriangulation |
| Formats | No MapDoc document format change and no authored identity token change. `Collider` and `Selection` resources carry `PropCollisionFormat` version 1 bytes with resource `PayloadVersion` 1 |
| Package | `KhaozEngine.MapDoc.Physics` is opt-in, in no umbrella, references exactly `KhaozEngine.MapDoc`, `KhaozEngine.Physics` and `KhaozEngine.Movement`, and never a physics backend, renderer, GPU, TileWorld or Grimhollow project |
| Determinism | Two independent builds of the same document, closure and options give byte-identical digests and statics. `IPhysicsQueryLease.GeometryGeneration` never enters any identity |
| Coordinates | Terrain chunk vertices are float offsets from whole-metre `MapSubmissionAnchor` values. Registration uses `new Pose(position - world.Origin, orientation)` with an asserted whole-metre origin. Installed positions stay within 1,000,000 m on every axis |
| Policies | Interaction envelope policy id `kemap/interaction-envelope/1`, minimum vertical reach height exactly 1 m. Built world domain `kemap/built-world/1` |
| Repository | Warnings are errors. New behavior gets a headless test in the matching test project under `KhaozEngine.Tests.*` namespaces. New files stay under 800 lines with no `.filesize-baseline` growth. No dash glyphs and no prose semicolons in Markdown. Third-party code only behind engine seams |
| Verification | Every local build or test runs through `build-slot`, serially, once per step. No GPU tests, windows, stress or repeated runs. Full suites run once at the candidate gate |

## Review Focus

These inputs are the most likely to hurt a user. Each has a named test in its owning task.

1. A compound doorway placed with non-quarter yaw and non-unit scale must stay open to pick rays, reach, line of sight and stance, while its jambs block (Tasks 2, 5, 6: `NativeDoorway_StaysOpenAfterYawAndScale`).
2. A cave floor directly above another cave's ceiling must keep the two levels distinct for physics sidedness, line of sight and clearance (Tasks 3, 5, 8: `StackedCaveFloorsAndCeilings_PhysicalHalf`).
3. A rebased physics world, a non-whole-metre origin or a fault during registration must not leak handles or apply the origin twice (Task 8: `Registration_RebasedOriginAndFaultsLeakNothing`).
4. A large placement crossing negative storage seams must belong to every tile it touches but be built and registered once (Tasks 7, 8: `LargePlacement_IntersectsEveryTileAndBuildsOnce`).
5. A dense fine patch whose single slot cell exceeds the chunk cap must refuse with `physics chunk capacity`, never truncate (Task 3: `DenseCell_RefusesWithPhysicsChunkCapacity`).

---

## Ownership and deferred work

| Item | Owner | Where it lands |
| --- | --- | --- |
| Native F3 G1b adapter with `MovementQueryLease` and the complete capsule resolver | R3 | A later R3 amendment after #438 phase 4 releases those types |
| Vertical-layer navigation capture on the selected controller | #438 phase 5 | R3 Task 9 provides tile and seam identity only |
| Incremental rebake orchestration, planning budgets, on-demand loading | #1301 separate work item | After #438 phase 5 |
| Multi-cell ghost handoff proof | R8 and G3 | R3 Task 7 provides grid mapping and ownership |
| Placement-local support surfaces (`Surface` resources) | R5 | R3 refuses them explicitly |
| Live portal and door state | R5 | R3 publishes `AuthoredOpen` only |
| Water medium and walker profiles | R4 and #1299 | R3 registration leaves the medium delegate null |
| Clearance body model confirmation | Owner, #1344 | R3 uses the #438 shell through `MoveTuning` |

## File Structure

| Path | Responsibility |
| --- | --- |
| `KhaozEngine.MapDoc.Physics/KhaozEngine.MapDoc.Physics.csproj`, `README.md` | Opt-in package, edges pinned by an architecture test |
| `KhaozEngine.MapDoc.Physics/MapAssetShapes.cs` | Collider and selection resources read from the closure |
| `KhaozEngine.MapDoc.Physics/MapShapeBounds.cs` | World bounds of a shape under a pose |
| `KhaozEngine.MapDoc.Physics/MapInteractionPolicy.cs`, `MapPlacementGeometry.cs` | Envelope policy, transformed placement colliders and envelopes |
| `KhaozEngine.MapDoc.Physics/MapTerrainPhysics.cs` | R2 faces to bounded physics meshes with face owners |
| `KhaozEngine.MapDoc/MapNativeResolution.cs` | One resolver routing rule shared by the builder and `NativeDocumentService` |
| `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs`, `MapBuiltWorld.cs` | Complete immutable build, static descriptors, identity, diagnostics |
| `KhaozEngine.MapDoc.Physics/MapShapeQueries.cs` | Exact point, capsule and ray math against shapes and triangle soups |
| `KhaozEngine.MapDoc.Physics/MapWorldQueries.cs` | Pick, reach and physical distance |
| `KhaozEngine.MapDoc.Physics/MapPhysicalRelations.cs` | Line of sight and clearance with certainty and witness |
| `KhaozEngine.MapDoc.Physics/MapStanceCandidates.cs` | Walk-up candidates with a caller-bound validator |
| `KhaozEngine.MapDoc.Physics/MapWorldGrids.cs`, `MapResidencyOwnership.cs` | Aligned grids, tile membership and edit invalidation |
| `KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs` | Installation into `IPhysicsWorld` with static and face provenance |
| `KhaozEngine.MapDoc.Physics/MapNavTiling.cs` | Navigation tile partition, capture identity, seams and invalidation |
| `KhaozEngine.Terrain.Render3D/NativeMapAssetLoader.cs` | Source-unit mesh loading without height renormalization |
| `KhaozEngine.MapEdit.Tool/NativeCollisionService.cs`, `MapAssetFileWriter.cs` | Collider measurement, height edits and content-addressed asset writes |
| `KhaozEngine.MapDoc.Physics.Tests/` | Headless tests over MapDoc.Physics and Physics.Bepu |
| `KhaozEngine.Tests/ArchitectureTests.MapDocPhysics.cs` | Package edge pins |

## Task dependency and estimate

Estimates are engineer-day judgments, not measurements, in the same form as R2's.

| Task | Depends on | Engineer-days |
| --- | --- | --- |
| 1 Package and asset shapes | v20.30.0 | 2 to 3 |
| 2 Placement geometry and envelopes | 1 | 3 to 5 |
| 3 Terrain physics chunks | 1 | 3 to 5 |
| 4 Resolution routing and world build | 2, 3 | 2 to 4 |
| 5 Pick, reach and physical relations | 4 | 4 to 7 |
| 6 Stance candidates | 5 | 2 to 3 |
| 7 Grids, residency and invalidation | 4 | 2 to 4 |
| 8 Physics registration and provenance | 4 | 2 to 4 |
| 9 Navigation tile identity | 7, 8 | 3 to 5 |
| 10 Native asset scale and collider edits | 4 | 3 to 5 |
| Reviews, release preparation and integration | all | 4 to 6 |
| Total | | 30 to 51 |

## Verification conventions

Run once per execution session from the worktree root:

```bash
test "$(git branch --show-current)" = "feature/wa-r3-shared-shapes"
wa_r3_log_dir="/tmp/grimhollow-orch/logs/wa-r3-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p "$wa_r3_log_dir"
MP=KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj
MAPDOC=KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj
EDITOR=KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj
RUMP=KhaozEngine.Tests/KhaozEngine.Tests.csproj
wa_run()  { n=$1; shift; local rc; if build-slot --label "wa-r3-$n" -- "$@" >"$wa_r3_log_dir/$n.log" 2>&1; then rc=0; else rc=$?; fi; tail -25 "$wa_r3_log_dir/$n.log"; echo "exit $rc for $n"; return "$rc"; }
wa_test() { wa_run "$1" dotnet test "$2" -c Release -m:1 --logger "trx;LogFileName=$1.trx" --results-directory "$wa_r3_log_dir" --filter "$3"; }
```

A red step is valid only when the named tests fail for the stated reason or the build fails only on a type this task introduces. A green step needs exit 0, zero failed tests and a nonzero matching count read from the log. A filter matching nothing is a failure. Each command runs once. Exit 75 from `build-slot` means nothing ran, so retry later. Every task ends with format on its changed files and the five guards:

```bash
wa_run tN-format dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --include <changed .cs files>
wa_run tN-dashes sh scripts/check-dashes.sh --tree
wa_run tN-prose sh scripts/check-prose.sh --tree
wa_run tN-file-size sh scripts/check-file-size.sh --tree
wa_run tN-agent sh scripts/check-agent-instructions.sh --tree
wa_run tN-doc-versions bash scripts/check-doc-versions.sh
```

Test fixtures live in `KhaozEngine.MapDoc.Physics.Tests/NativeWorldFixtures.cs` and grow task by task. They build closures through `MapAssetClosure.Load(roots, source)` over an in-memory `IMapAssetSource`, collider bytes through `PropCollisionFormat.Write`, and resolver-2 documents through public R2 APIs only.

---

### Task 1: Package and asset shapes

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/KhaozEngine.MapDoc.Physics.csproj`, `KhaozEngine.MapDoc.Physics/README.md`, `KhaozEngine.MapDoc.Physics/MapAssetShapes.cs`
- Create: `KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj`, `KhaozEngine.MapDoc.Physics.Tests/NativeWorldFixtures.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapAssetShapesTests.cs`
- Create: `KhaozEngine.Tests/ArchitectureTests.MapDocPhysics.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.cs` (`OptInBackends` gains `"MapDoc.Physics"`), `KhaozEngine.slnx`, `README.md` (package catalog row, "not in any umbrella"), `docs/DEPENDENCY-SEAMS.md` (new package-edges section modelled on TileWorld.Physics)

**Interfaces:**
- Consumes: `MapAssetClosure.GetAsset(string)`, `GetResource(string) -> MapResolvedResource` (`Bytes`, `Kind`, `Reference.PayloadVersion`), `MapResolvedAsset` (`CollisionResourceId`, `SelectionResourceId`, `SupportResourceIds`, `SourceUnitsToMetres`), `PropCollisionFormat.Read(Stream) -> PhysicsShape`.
- Produces: `public sealed class MapAssetShapes` with `string AssetId`, `PhysicsShape? Collider`, `PhysicsShape? Selection`, `string? ColliderSha256`, `string? SelectionSha256`, `bool IsSolid` (true exactly when `Collider` is not null), `float SourceUnitsToMetres`, and `public static MapAssetShapes Read(MapAssetClosure closure, string assetId)`. Shapes stay in asset source units.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapAssetShapesTests
{
    [Fact]
    public void CompoundCollider_ReadsAsSolidWithItsDigest()
    {
        var f = NativeWorldFixtures.Assets();
        var shapes = MapAssetShapes.Read(f.Closure, "doorway");
        Assert.True(shapes.IsSolid);
        Assert.IsType<CompoundShape>(shapes.Collider);
        Assert.Equal(f.Closure.GetResource("doorway.collider").Reference.Sha256, shapes.ColliderSha256);
        Assert.Null(shapes.Selection);
    }

    [Fact]
    public void SelectionOnlyAsset_IsNotSolid()
    {
        var shapes = MapAssetShapes.Read(NativeWorldFixtures.Assets().Closure, "examine-sign");
        Assert.False(shapes.IsSolid);
        Assert.IsType<BoxShape>(shapes.Selection);
    }

    [Theory]
    [InlineData("future-collider", "collision payload")]
    [InlineData("garbage-collider", "collision payload")]
    [InlineData("mesh-in-compound", "triangle mesh inside a compound")]
    [InlineData("deck-with-support", "placement-local support surfaces arrive with R5")]
    public void UnsupportedShapeData_Refuses(string assetId, string message)
    {
        var f = NativeWorldFixtures.Assets();
        var e = Assert.Throws<MapDocumentException>(() => MapAssetShapes.Read(f.Closure, assetId));
        Assert.Contains(message, e.Message);
    }
}
```

In `ArchitectureTests.MapDocPhysics.cs`, pin the package's direct project references to exactly `KhaozEngine.MapDoc`, `KhaozEngine.Physics` and `KhaozEngine.Movement`, and the test project's to exactly `KhaozEngine.MapDoc.Physics`, `KhaozEngine.Physics.Bepu` and `KhaozEngine.Sharding` (the last for Task 7's grid equivalence), modelled on `ArchitectureTests.Movement.cs`.

`NativeWorldFixtures.Assets() -> (MapAssetClosure Closure, IReadOnlyList<MapAssetRef> Roots)` provides: `doorway` (compound of two jamb boxes 0.3 x 2.4 x 0.3 m at local x = -0.75 and 0.75 and a lintel 1.8 x 0.3 x 0.3 m at y = 2.55, source units 1), `examine-sign` (selection box only), `crate` (box 0.6 x 0.2 x 0.6 m), `tree` (cylinder radius 0.3, length 6), `large-building` (box 100 x 10 x 100 m), `future-collider` (resource `PayloadVersion` 2), `garbage-collider` (random bytes), `mesh-in-compound` and `deck-with-support` (one `SupportResourceIds` entry).

- [ ] **Step 2: Run red**

Run: `wa_test t1-red "$MP" "FullyQualifiedName~MapAssetShapesTests"`. Expected: compile failure only on `MapAssetShapes`.

- [ ] **Step 3: Implement `MapAssetShapes.Read(MapAssetClosure closure, string assetId)` and the package wiring**

Read each present resource with `PropCollisionFormat.Read` over its bytes after checking `Kind` and `Reference.PayloadVersion == 1`. Map every read failure to `MapDocumentException` with "collision payload" and the resource id. Walk compounds recursively and refuse a `TriangleMeshShape` child. Refuse any non-empty `SupportResourceIds`. The csproj follows `KhaozEngine.TileWorld.Physics.csproj` (PackageId, version knob, README packed, opt-in description). The test csproj follows `KhaozEngine.TileWorld.Physics.Tests.csproj` with `RootNamespace` `KhaozEngine.Tests`.

- [ ] **Step 4: Run green**

Run: `wa_test t1-green "$MP" "FullyQualifiedName~MapAssetShapesTests"` (expect 7) and `wa_test t1-arch "$RUMP" "FullyQualifiedName~ArchitectureTests"`.

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): read collider and selection shapes from the closure`

---

### Task 2: Placement geometry and interaction envelopes

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapShapeBounds.cs`, `MapInteractionPolicy.cs`, `MapPlacementGeometry.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapPlacementGeometryTests.cs`

**Interfaces:**
- Consumes: Task 1 `MapAssetShapes.Read`, `MapResolvedDocument` (`Placements`, `AssetClosure`), `MapResolvedPlacement.Transform` (`Position`, `YawRadians`, `Scale`), `PhysicsShapeScale.Uniform(PhysicsShape, float)`.
- Produces:
  - `public static class MapShapeBounds { public static MapBox3 Of(PhysicsShape shape, Pose pose) }`.
  - `public static class MapInteractionPolicy` with `public const string PolicyId = "kemap/interaction-envelope/1"`, `public const float MinimumVerticalReachHeightMetres = 1f`, `public static string Hash { get; }` (SHA-256 of the canonical policy text, lowercase hex).
  - `public sealed class MapInteractionEnvelope` with `string PlacementId`, `PhysicsShape Shape`, `Pose WorldPose`, `float MinY`, `float MaxY`, `bool DerivedFromCollider`.
  - `public sealed class MapPlacementGeometry` with `string PlacementId`, `long? NumericId`, `string AssetId`, `PhysicsShape? Collider`, `Pose WorldPose`, `MapBox3? ColliderBounds`, `MapInteractionEnvelope Envelope`, `string Digest`.
  - `public static class MapPlacementShapes { public static IReadOnlyList<MapPlacementGeometry> Resolve(MapResolvedDocument document) }`, ordered by ordinal `PlacementId`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapPlacementGeometryTests
{
    [Fact]
    public void Collider_ScalesBySourceUnitsAndPlacementScaleOnce()
    {
        var f = NativeWorldFixtures.Doorway(yaw: 0.371f, scale: 1.137f);
        var g = MapPlacementShapes.Resolve(f.Resolved).Single(p => p.PlacementId == "doorway");
        var jamb = ((CompoundShape)g.Collider!).Children[0];
        Assert.Equal(new Vector3(0.15f, 1.2f, 0.15f) * 1.137f, ((BoxShape)jamb.Shape).HalfExtents);
        Assert.Equal(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.371f), g.WorldPose.Orientation);
    }

    [Fact]
    public void LowObject_EnvelopeIsOneMetreTallWhilePhysicalBoundsStay()
    {
        var g = MapPlacementShapes.Resolve(NativeWorldFixtures.Crate().Resolved).Single();
        Assert.Equal(0.2, g.ColliderBounds!.Value.MaxY - g.ColliderBounds.Value.MinY, 5);
        Assert.Equal(1.0, (double)g.Envelope.MaxY - g.Envelope.MinY, 5);
        Assert.Equal((float)g.ColliderBounds.Value.MinY, g.Envelope.MinY);
    }

    [Fact]
    public void SolidWithoutSelection_DerivesEnvelopeFromCollider()
        => Assert.True(MapPlacementShapes.Resolve(NativeWorldFixtures.Crate().Resolved).Single().Envelope.DerivedFromCollider);

    [Fact]
    public void NonSolidWithoutSelection_Refuses()
    {
        var e = Assert.Throws<MapDocumentException>(() => MapPlacementShapes.Resolve(NativeWorldFixtures.ShapelessProp().Resolved));
        Assert.Contains("interaction source", e.Message);
    }

    [Fact]
    public void TwoResolutions_HaveIdenticalDigests()
    {
        var a = MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(0.371f, 1.137f).Resolved);
        var b = MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(0.371f, 1.137f).Resolved);
        Assert.Equal(a.Select(p => p.Digest), b.Select(p => p.Digest));
    }
}
```

Fixtures return `(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved)`. `Doorway(yaw, scale)` places `doorway` at (0.23, 0, 0.17) on a flat native floor. `Crate()` places `crate` on the floor. `ShapelessProp()` uses an asset with neither collider nor selection.

- [ ] **Step 2: Run red** `wa_test t2-red "$MP" "FullyQualifiedName~MapPlacementGeometryTests"`

- [ ] **Step 3: Implement the produced types**

Combined scale is `asset.SourceUnitsToMetres * transform.Scale`, applied once through `PhysicsShapeScale.Uniform`, which leaves baked meshes at unit scale. `WorldPose` is `new Pose(transform.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, transform.YawRadians))`. The envelope source is the selection shape when present, otherwise the collider for a solid asset, otherwise refuse. Its vertical range is the source's world bounds, raised at the top to at least `MinimumVerticalReachHeightMetres`. Band clipping happens only at query time (Task 5). `Digest` is SHA-256 over placement id, numeric id, asset id, both resource digests, the float bits of position, yaw and combined scale, the envelope range bits and `MapInteractionPolicy.PolicyId`.

- [ ] **Step 4: Run green** `wa_test t2-green "$MP" "FullyQualifiedName~MapPlacementGeometryTests"` (expect 5)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): resolve placement colliders and interaction envelopes`

---

### Task 3: Terrain physics chunks

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapTerrainPhysics.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapTerrainPhysicsTests.cs`

**Interfaces:**
- Consumes: `MapScopedSurfaces` (`Status`, `Surfaces`, `Patch(MapPatchKey)`, `ReadWitness`), `MapSurfaceCompiler.Compile(MapSurfaceRef, MapSurfacePatch) -> MapCompiledPatch` (`Anchor`, `Offsets`, `Faces`, `Role`, `LegacyFallbackCells`), `MapWallStripCompiler.Compile(...) -> MapCompiledStrip`, `MapCompiledFace(Key, Role, A, B, C, Normal)`.
- Produces:
  - `public sealed record MapTerrainChunkPolicy(int MaxTrianglesPerChunk = 2048)`, valid from 64 to 65,536.
  - `public sealed class MapTerrainChunk` with `string ChunkId`, `MapSubmissionAnchor Anchor`, `TriangleMeshShape Shape` (vertices are offsets from `Anchor`), `IReadOnlyList<MapFaceKey> TriangleOwners` and `IReadOnlyList<MapFaceRole> TriangleRoles` (index i describes triangle i), `string Digest`.
  - `public sealed class MapTerrainChunkSet` with `IReadOnlyList<MapTerrainChunk> Chunks`, `MapReadWitness Witness`, `int LegacyFallbackCellsSkipped`.
  - `public static class MapTerrainPhysics { public static MapTerrainChunkSet Compile(MapScopedSurfaces view, MapTerrainChunkPolicy policy) }`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapTerrainPhysicsTests
{
    [Fact]
    public void StackedCaveFloorsAndCeilings_PhysicalHalf()
    {
        var set = MapTerrainPhysics.Compile(NativeWorldFixtures.StackedCave().View, new MapTerrainChunkPolicy());
        var roles = set.Chunks.SelectMany(c => c.TriangleRoles).Distinct();
        Assert.Contains(MapFaceRole.SupportFloor, roles);
        Assert.Contains(MapFaceRole.Ceiling, roles);
        Assert.Contains(MapFaceRole.Wall, roles);
        foreach (var chunk in set.Chunks)
            for (int t = 0; t < chunk.TriangleOwners.Count; t++)
                Assert.True(NativeWorldFixtures.WindingMatchesCompiledNormal(chunk, t));
    }

    [Fact]
    public void ChunksRespectTheCap_AndEveryTriangleHasItsFace()
    {
        var set = MapTerrainPhysics.Compile(NativeWorldFixtures.FinePatch().View, new MapTerrainChunkPolicy(64));
        Assert.All(set.Chunks, c => Assert.InRange(c.TriangleOwners.Count, 1, 64));
        Assert.Equal(NativeWorldFixtures.FinePatch().CompiledFaceCount, set.Chunks.Sum(c => c.TriangleOwners.Count));
        Assert.Equal(set.Chunks.Sum(c => c.TriangleOwners.Count), set.Chunks.SelectMany(c => c.TriangleOwners).Distinct().Count());
    }

    [Fact]
    public void LegacyFallbackCells_EmitNoTriangles()
    {
        var f = NativeWorldFixtures.LegacyExteriorWithFallback();
        var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy());
        Assert.Equal(f.FallbackCellCount, set.LegacyFallbackCellsSkipped);
        Assert.DoesNotContain(set.Chunks.SelectMany(c => c.TriangleOwners), k => f.IsFallbackFace(k));
    }

    [Fact]
    public void DenseCell_RefusesWithPhysicsChunkCapacity()
    {
        var e = Assert.Throws<MapDocumentException>(() =>
            MapTerrainPhysics.Compile(NativeWorldFixtures.DenseCell().View, new MapTerrainChunkPolicy(64)));
        Assert.Contains("physics chunk capacity", e.Message);
    }

    [Fact]
    public void IncompleteView_Refuses()
        => Assert.Contains("complete", Assert.Throws<MapDocumentException>(() =>
            MapTerrainPhysics.Compile(NativeWorldFixtures.IncompleteView(), new MapTerrainChunkPolicy())).Message);
}
```

`DenseCell()` holds one slot cell with more than 64 compiled faces (a fine subdivision of 8 per edge gives 128). `StackedCave()` is a resolver-2 document with a floor at y 0, a ceiling at y 3 with one opening cell, an upper floor at y 3.5 over the same XZ and wall strips joining them. `WindingMatchesCompiledNormal` checks that `(B - A) x (C - A)` points along the compiled face normal.

- [ ] **Step 2: Run red** `wa_test t3-red "$MP" "FullyQualifiedName~MapTerrainPhysicsTests"`

- [ ] **Step 3: Implement `MapTerrainPhysics.Compile`**

Refuse unless `view.Status` is `Complete`. Compile every `SupportFloor` and `Ceiling` patch and every wall strip. Skip `PaintOverride` surfaces. Faces only come from `MapCompiledPatch.Faces`, so legacy fallback cells contribute none, and their count is reported. Group a patch's faces by slot cell (`MapFaceKey.Primitive`), then split the 64 by 64 slot block recursively into quadrants until each chunk has at most the cap. A single slot cell over the cap refuses with "physics chunk capacity", the patch key and the cell. Strips chunk by contiguous primitive ranges under the same cap. Emit vertices in the winding that makes the triangle's geometric normal agree with `MapCompiledFace.Normal`, so Bepu's one-sided meshes face the role's open side. Faces are R2's compiled faces as they are, never retriangulated. Chunk ids are `<surfaceId>/<patchKey>/<minX>,<minZ>,<size>` or `<stripId>/<firstPrimitive>`. Order chunks and triangles by ordinal id then `MapFaceKey` order. `Digest` covers id, anchor, vertex float bits, indices and owners.

- [ ] **Step 4: Run green** `wa_test t3-green "$MP" "FullyQualifiedName~MapTerrainPhysicsTests"` (expect 5)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): compile R2 faces into bounded physics meshes`

---

### Task 4: Resolution routing and world build

**Files:**
- Create: `KhaozEngine.MapDoc/MapNativeResolution.cs`, `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs`, `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs`
- Modify: `KhaozEngine.MapEdit.Tool/NativeDocumentService.cs` (route through `MapNativeResolution`)
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapWorldBuilderTests.cs`, `KhaozEngine.MapDoc.Tests/MapNativeResolutionTests.cs`

**Interfaces:**
- Consumes: `MapResolver.Resolve(MapDocument, MapAssetClosure, Func<float,float,float>, MapResolveOptions)`, `MapResolverV2.Resolve(MapDocument, MapAssetClosure, IMapSurfaceSource, MapResolveOptions) -> MapSupportedResolution`, `MapDocumentSurfaceSource.Capture(MapDocument)`, `MapScopedSurfaces.CompleteView(MapSurfaceSet)`, Tasks 2 and 3.
- Produces:
  - `public static class MapNativeResolution { public static MapResolvedDocument Resolve(MapDocument document, MapAssetClosure assets, MapResolveOptions options, Func<float,float,float>? legacySupportHeight = null) }`. Resolver identity (1, 1) requires `legacySupportHeight` and options resolver 1. Identity (1, 2) captures the document's surfaces and uses resolver 2. Any other pairing refuses before reading resources.
  - `public sealed record MapWorldBuildOptions(MapResolveOptions Resolve, string ConsumerPolicyIdentity, MapTerrainChunkPolicy Chunks, Func<float,float,float>? LegacySupportHeight = null, int BuilderVersion = 1)`.
  - `public enum MapStaticKind { Placement, TerrainChunk }`.
  - `public sealed record MapStaticDescriptor(string OwnerId, MapStaticKind Kind, PhysicsShape Shape, Vector3 Position, Quaternion Orientation, IReadOnlyList<MapFaceKey> TriangleOwners)`.
  - `public enum MapFeatureQuerySupport { Supported, LeafCapacity, CurvedUntilPhase2b, MeshTriangleCapacity }` and `public sealed record MapStaticDiagnostic(string OwnerId, MapFeatureQuerySupport Support)`.
  - `public sealed class MapBuiltWorld` with `MapResolvedDocument Document`, `MapScopedSurfaces Surfaces`, `IReadOnlyList<MapPlacementGeometry> Placements`, `MapTerrainChunkSet Terrain`, `IReadOnlyList<MapStaticDescriptor> Statics`, `IReadOnlyList<MapStaticDiagnostic> Diagnostics`, `MapBox3 Bounds`, `string AuthoredHash`, `string BuildHash`, `bool IsNative` (resolver 2), `Func<float,float,float>? LegacySupportHeight`.
  - `public static class MapWorldBuilder { public static MapBuiltWorld Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) }`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWorldBuilderTests
{
    [Fact]
    public void TwoIndependentHeads_BuildIdenticalWorlds()
    {
        var a = MapWorldBuilder.Build(NativeWorldFixtures.StackedCave().Document, NativeWorldFixtures.StackedCave().Assets, NativeWorldFixtures.Options());
        var b = MapWorldBuilder.Build(NativeWorldFixtures.StackedCave().Document, NativeWorldFixtures.StackedCave().Assets, NativeWorldFixtures.Options());
        Assert.Equal(a.BuildHash, b.BuildHash);
        Assert.Equal(a.AuthoredHash, b.AuthoredHash);
        Assert.Equal(a.Statics.Select(s => (s.OwnerId, s.Position, s.Orientation)), b.Statics.Select(s => (s.OwnerId, s.Position, s.Orientation)));
    }

    [Fact]
    public void PartialWindowAndMismatchedResolver_Refuse()
    {
        Assert.Contains("partial", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildPartialWindow()).Message);
        Assert.Contains("resolver", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithResolverMismatch()).Message);
        Assert.Contains("legacy support height", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildLegacyWithoutHeight()).Message);
        Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithPlacementScale(float.NaN));
        Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithPlacementScale(0f));
    }

    [Fact]
    public void BuildHash_FollowsPolicyIdentityAndChunkPolicy()
    {
        var f = NativeWorldFixtures.StackedCave();
        string baseline = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()).BuildHash;
        Assert.NotEqual(baseline, MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options() with { ConsumerPolicyIdentity = "other" }).BuildHash);
        Assert.NotEqual(baseline, MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options() with { Chunks = new MapTerrainChunkPolicy(1024) }).BuildHash);
    }

    [Fact]
    public void FeatureQueryLimits_AreReportedNotRefused()
    {
        var world = MapWorldBuilder.Build(NativeWorldFixtures.TreeAndWideCompound().Document, NativeWorldFixtures.TreeAndWideCompound().Assets, NativeWorldFixtures.Options());
        Assert.Contains(new MapStaticDiagnostic("tree", MapFeatureQuerySupport.CurvedUntilPhase2b), world.Diagnostics);
        Assert.Contains(new MapStaticDiagnostic("parapet-70", MapFeatureQuerySupport.LeafCapacity), world.Diagnostics);
    }
}
```

`MapNativeResolutionTests` asserts that identity (1, 1) with resolver-1 options matches `MapResolver.Resolve`, that identity (1, 2) matches `MapResolverV2.Resolve(...).Document`, and that (1, 2) with resolver-1 options refuses with "resolver". The existing `NativeDocumentServiceResolverTests` must stay green unchanged.

- [ ] **Step 2: Run red** `wa_test t4-red "$MP" "FullyQualifiedName~MapWorldBuilderTests"` and `wa_test t4-red-mapdoc "$MAPDOC" "FullyQualifiedName~MapNativeResolutionTests"`

- [ ] **Step 3: Implement routing, the builder and the built world**

Refuse a partial window first (`document.Tiles is { IsPartial: true }`). Resolve through `MapNativeResolution`. For resolver 2, take `MapScopedSurfaces.CompleteView(document.Surfaces)` and compile terrain with Task 3. For resolver 1, terrain stays analytic, the chunk set is empty and `LegacySupportHeight` is kept for Tasks 6 and 9. Statics are placement colliders (non-solid placements have none) then terrain chunks, each in ordinal owner order. A terrain static's `Position` is its anchor as whole metres. Refuse any static position beyond 1,000,000 m on an axis. Diagnostics name compounds over 64 leaves, cylinders and curved shapes, and meshes over 65,536 triangles. `BuildHash` is SHA-256 over the canonical JSON `{ "domain": "kemap/built-world/1", authoredHash, builderVersion, consumerPolicyIdentity, interactionPolicyHash, maxTrianglesPerChunk, placements: [[id, digest]], terrain: [[chunkId, digest]] }`. Change `NativeDocumentService` to call `MapNativeResolution` with no behavior change.

- [ ] **Step 4: Run green** `wa_test t4-green "$MP" "FullyQualifiedName~MapWorldBuilderTests"` (expect 4), `wa_test t4-green-mapdoc "$MAPDOC" "FullyQualifiedName~MapNativeResolutionTests"` (expect 3), `wa_test t4-regress-editor "$EDITOR" "FullyQualifiedName~NativeDocumentService"`

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): build the complete immutable world`

---

### Task 5: Pick, reach and physical relations

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapShapeQueries.cs`, `MapWorldQueries.cs`, `MapPhysicalRelations.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapWorldQueriesTests.cs`, `MapPhysicalRelationsTests.cs`

**Interfaces:**
- Consumes: Task 4 `MapBuiltWorld`, `MovementBody`, `ReachTarget.Box`, `ReachGeometry.Distance/Within`, `MoveTuning` (`CapsuleRadius`, `CapsuleHalfHeight`, `StepHeight`), `MapFramePoint`, `WorldFrame`, `MapPhysicalCertainty`, `MapReadWitness`.
- Produces:
  - `public readonly record struct MapPickRay(Vector3 Origin, Vector3 Direction, float MaxDistance)`, `public readonly record struct MapInteractionBand(float MinY, float MaxY)`, `public sealed record MapPickHit(string PlacementId, long? NumericId, float Distance, Vector3 Point, Vector3 Normal)`.
  - `public static class MapShapeQueries` with `Distance(Vector3 point, PhysicsShape shape, Pose pose) -> float`, `Distance(in MovementBody body, PhysicsShape shape, Pose pose) -> float`, `Raycast(MapPickRay ray, PhysicsShape shape, Pose pose, out float distance, out Vector3 normal) -> bool`, and the same three over a `TriangleMeshShape` at an anchor.
  - `public sealed class MapWorldQueries(MapBuiltWorld world)` with `Pick(MapPickRay ray, MapInteractionBand? band = null) -> MapPickHit?`, `Distance(in MovementBody body, string placementId, MapInteractionBand? band = null) -> float`, `Within(in MovementBody body, string placementId, float range, float tolerance = 0f, MapInteractionBand? band = null) -> bool`, `PhysicalDistance(in MovementBody body, string placementId) -> float`.
  - `public sealed record MapPhysicalResult(MapPhysicalCertainty Certainty, string? BlockingOwner, float? BlockDistance, MapReadWitness Witness, string BuildHash)`.
  - `public sealed class MapPhysicalRelations(MapBuiltWorld world)` with `LineOfSight(MapFramePoint from, MapFramePoint to) -> MapPhysicalResult` and `Clearance(IReadOnlyList<MapFramePoint> feetPath, in MoveTuning tuning) -> MapPhysicalResult`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWorldQueriesTests
{
    [Fact]
    public void NativeDoorway_StaysOpenAfterYawAndScale()
    {
        var f = NativeWorldFixtures.Doorway(0.371f, 1.137f);
        var q = new MapWorldQueries(MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()));
        var t = f.Resolved.Placements.Single(p => p.PlacementId == "doorway").Transform;
        var through = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.371f));
        Assert.Null(q.Pick(new MapPickRay(t.TransformPoint(new Vector3(0, 1, -2)), through, 4f)));
        Assert.NotNull(q.Pick(new MapPickRay(t.TransformPoint(new Vector3(-0.75f, 1, -2)), through, 4f)));
        Assert.True(q.PhysicalDistance(new MovementBody(t.TransformPoint(new Vector3(0, 1, 0)), 0.2f, 0.8f), "doorway") > 0);
    }

    [Fact]
    public void TreeBand_HitsAtBasePlusOneAndRefusesAtBasePlusThree()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuiltTree());
        var band = new MapInteractionBand(0f, 2f);
        Assert.NotNull(q.Pick(new MapPickRay(new Vector3(0, 1, -2), Vector3.UnitZ, 4f), band));
        Assert.Null(q.Pick(new MapPickRay(new Vector3(0, 3, -2), Vector3.UnitZ, 4f), band));
    }

    [Fact]
    public void LowObject_ReachUsesTheOneMetreEnvelope()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuiltCrate());
        var head = new MovementBody(new Vector3(0, 1.6f, -0.7f), 0.2f, 0.1f);
        Assert.True(q.Within(head, "crate", 0.5f));
        Assert.True(q.PhysicalDistance(head, "crate") > 0.5f);
    }

    [Fact]
    public void EqualDistancePicks_BreakTiesByOrdinalPlacementId()
        => Assert.Equal("a-crate", new MapWorldQueries(NativeWorldFixtures.BuiltTwinCrates()).Pick(new MapPickRay(new Vector3(0, 0.1f, -2), Vector3.UnitZ, 4f))!.PlacementId);
}

public class MapPhysicalRelationsTests
{
    [Fact]
    public void StackedCave_LineOfSightAndClearanceKeepLevelsDistinct()
    {
        var f = NativeWorldFixtures.StackedCave();
        var r = new MapPhysicalRelations(MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()));
        Assert.Equal(MapPhysicalCertainty.Blocked, r.LineOfSight(f.LowerRoom, f.UpperRoom).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.LineOfSight(f.LowerUnderOpening, f.UpperOverOpening).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.Clearance(new[] { f.LowerRoomFeet }, MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, r.Clearance(new[] { f.UnderLowCeilingFeet }, MoveTuning.Default).Certainty);
    }

    [Fact]
    public void CaveFloorCeilingSupportAndPick_AgreeOnBothHeads()
    {
        var f = NativeWorldFixtures.StackedCave();
        var client = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        var server = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        foreach (var point in f.ProbePoints)
        {
            Assert.Equal(new MapPhysicalRelations(client).LineOfSight(point, f.UpperRoom), new MapPhysicalRelations(server).LineOfSight(point, f.UpperRoom));
            Assert.Equal(new MapWorldQueries(client).Pick(f.PickRayFrom(point)), new MapWorldQueries(server).Pick(f.PickRayFrom(point)));
            Assert.Equal(NativeWorldFixtures.Support(client, point), NativeWorldFixtures.Support(server, point));
        }
    }

    [Fact]
    public void Wall_BlocksWithItsOwner_AndOutsideCoverageIsUnknown()
    {
        var f = NativeWorldFixtures.Doorway(0.371f, 1.137f);
        var r = new MapPhysicalRelations(MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()));
        var blocked = r.LineOfSight(f.BeforeJamb, f.AfterJamb);
        Assert.Equal(MapPhysicalCertainty.Blocked, blocked.Certainty);
        Assert.Equal("doorway", blocked.BlockingOwner);
        Assert.Equal(MapPhysicalCertainty.Unknown, r.LineOfSight(f.BeforeJamb, f.FarOutsideBounds).Certainty);
    }
}
```

- [ ] **Step 2: Run red** `wa_test t5-red "$MP" "FullyQualifiedName~MapWorldQueriesTests|FullyQualifiedName~MapPhysicalRelationsTests"`

- [ ] **Step 3: Implement queries and relations**

Pick, Distance and Within query envelopes only. Pick ignores hits outside the band's Y range, takes the nearest hit and breaks equal distances by ordinal placement id. Box members whose local rotation is about Y alone reuse `ReachTarget.Box` and `ReachGeometry`. Other members use exact distances: segment to triangle for hulls and meshes, exact cylinder distance and cap math for cylinders, recursive poses for compounds, never an AABB. `PhysicalDistance` uses colliders only. Line of sight intersects the segment with every collider and every terrain chunk triangle (envelopes never block), and returns the nearest blocker with its owner (placement id, or the `MapFaceKey` owner id for terrain). Clearance tests the #438 shell at each feet point: a capsule of `tuning.CapsuleRadius` from knee height `feet.Y + tuning.StepHeight` to the head at `feet.Y + 2 * tuning.CapsuleHalfHeight`, refused when `2 * CapsuleHalfHeight - StepHeight < 2 * CapsuleRadius`. A point or segment outside the world's playable bounds or the terrain witness coverage gives `Unknown`. Results carry `world.Terrain.Witness` and `world.BuildHash`. Portals are openings with no faces, so `AuthoredOpen` needs no extra state. Relations evaluate on the immutable built world, which is identical on both heads, so they take no physics lease. A consumer that queries the registered physics world (Task 8) does so under the released `IPhysicsQueryLease`.

- [ ] **Step 4: Run green** `wa_test t5-green "$MP" "FullyQualifiedName~MapWorldQueriesTests|FullyQualifiedName~MapPhysicalRelationsTests"` (expect 7)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): query envelopes and physical relations`

---

### Task 6: Stance candidates

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapStanceCandidates.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapStanceCandidatesTests.cs`

**Interfaces:**
- Consumes: Task 5 `MapWorldQueries.Within`, `MapSupportQuery(MapScopedSurfaces).Select(MapSupportRequest)` for native worlds, `MapBuiltWorld.LegacySupportHeight` for resolver 1.
- Produces:
  - `public delegate bool MapStanceValidator(Vector3 feet)`.
  - `public sealed record MapStanceOptions(float Spacing, float Range, float Tolerance = 0f, MapInteractionBand? Band = null)`.
  - `public static class MapStanceCandidates { public static IReadOnlyList<Vector3> Find(MapBuiltWorld world, string placementId, Vector3 actorFeet, in MoveTuning tuning, MapStanceOptions options, MapStanceValidator validate) }`, absolute feet positions ordered by distance to `actorFeet`, then X, Z, Y.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapStanceCandidatesTests
{
    [Fact]
    public void RotatedCompound_CandidatesAreDeterministicReachableAndValidated()
    {
        var f = NativeWorldFixtures.Doorway(0.371f, 1.137f);
        var world = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        var q = new MapWorldQueries(world);
        var options = new MapStanceOptions(Spacing: 0.25f, Range: 0.4f);
        var seen = new List<Vector3>();
        var a = MapStanceCandidates.Find(world, "doorway", new Vector3(0, 0, -2), MoveTuning.Default, options, p => { seen.Add(p); return true; });
        var b = MapStanceCandidates.Find(world, "doorway", new Vector3(0, 0, -2), MoveTuning.Default, options, _ => true);
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        Assert.All(a, p => Assert.True(q.Within(new MovementBody(p + Vector3.UnitY * MoveTuning.Default.CapsuleHalfHeight, MoveTuning.Default.CapsuleRadius, MoveTuning.Default.CapsuleHalfHeight), "doorway", 0.4f)));
        Assert.Subset(seen.ToHashSet(), a.ToHashSet());
    }

    [Fact]
    public void ValidatorRejectingEverything_GivesNoCandidates()
        => Assert.Empty(MapStanceCandidates.Find(NativeWorldFixtures.BuiltCrate(), "crate", new Vector3(0, 0, -2), MoveTuning.Default, new MapStanceOptions(0.25f, 0.5f), _ => false));

    [Fact]
    public void LegacyWorld_UsesTheLegacySupportHeight()
        => Assert.All(MapStanceCandidates.Find(NativeWorldFixtures.BuiltLegacySlope(), "crate", new Vector3(0, 0, -2), MoveTuning.Default, new MapStanceOptions(0.25f, 0.5f), _ => true),
            p => Assert.Equal(NativeWorldFixtures.LegacySlopeHeight(p.X, p.Z), p.Y));
}
```

- [ ] **Step 2: Run red** `wa_test t6-red "$MP" "FullyQualifiedName~MapStanceCandidatesTests"`

- [ ] **Step 3: Implement `Find`**

Walk the envelope's XZ footprint outline, outset by the capsule radius, at `Spacing`, including aperture sides of compounds. Take feet height from `MapSupportQuery.Select` with `MaxStepUp` and `MaxDropDown` set to `tuning.StepHeight` around the outline point's envelope base (native) or from `LegacySupportHeight` (resolver 1). Drop points with no `Supported` result, outside playable bounds, or outside envelope reach. Call `validate` last, only for survivors, and keep those it accepts. No step, ledge or slope rule lives here. Sort as specified.

- [ ] **Step 4: Run green** `wa_test t6-green "$MP" "FullyQualifiedName~MapStanceCandidatesTests"` (expect 3)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): derive walk-up candidates with a caller validator`

---

### Task 7: Grids, residency and invalidation

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapWorldGrids.cs`, `MapResidencyOwnership.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapResidencyOwnershipTests.cs`

**Interfaces:**
- Consumes: Task 4 statics and bounds, `MapTileGrid.CoordOf`, `MapTileCoord`, `MapTileRect`, `MapNativeEditEffects` (`OldBounds`, `NewBounds`, `Patches`, `Invalidates`).
- Produces:
  - `public readonly record struct MapNavTileCoord(int X, int Z)`, `public readonly record struct MapServerCellCoord(int X, int Z)`.
  - `public sealed record MapWorldGrids(float StorageTileSize, int NavTileStorageTiles, int ServerCellStorageTiles, Vector2 Origin)` with `Validate()`, `NavTileOf(MapTileCoord) -> MapNavTileCoord`, `ServerCellOf(MapTileCoord) -> MapServerCellCoord`, `NavTileSize`, `ServerCellSize`. Storage tile size is a positive whole number of metres, the multipliers are positive, and each origin component is a whole multiple of the storage tile size.
  - `public sealed record MapResidencyEntry(string OwnerId, MapTileCoord StorageOwner, IReadOnlyList<MapTileCoord> Membership, IReadOnlyList<MapNavTileCoord> NavTiles, IReadOnlyList<MapServerCellCoord> Cells, MapBox3 WorldBounds)`.
  - `public static class MapResidencyOwnership` with `Build(MapBuiltWorld world, MapWorldGrids grids) -> IReadOnlyList<MapResidencyEntry>`, `InWindow(IReadOnlyList<MapResidencyEntry> entries, MapTileRect window) -> IReadOnlyList<string>`, `Affected(MapBuiltWorld world, MapWorldGrids grids, MapNativeEditEffects effects) -> MapAffectedSet`.
  - `public sealed record MapAffectedSet(IReadOnlyList<string> Owners, IReadOnlyList<MapTileCoord> StorageTiles, IReadOnlyList<MapNavTileCoord> NavTiles)`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapResidencyOwnershipTests
{
    static readonly MapWorldGrids Grids = new(64f, 2, 4, Vector2.Zero);

    [Fact]
    public void LargePlacement_IntersectsEveryTileAndBuildsOnce()
    {
        var world = NativeWorldFixtures.BuiltLargeBuilding();
        var entry = Assert.Single(MapResidencyOwnership.Build(world, Grids), e => e.OwnerId == "large-building");
        Assert.Contains(new MapTileCoord(-1, -1), entry.Membership);
        Assert.Contains(new MapTileCoord(0, 0), entry.Membership);
        Assert.Equal(entry.Membership.Count, entry.Membership.Distinct().Count());
        Assert.Single(world.Statics, s => s.OwnerId == "large-building");
    }

    [Fact]
    public void ExactSeams_DoNotOvercount_AndWindowsUnionToTheWhole()
    {
        var world = NativeWorldFixtures.BuiltSeamAligned();
        var entries = MapResidencyOwnership.Build(world, Grids);
        Assert.All(entries.Where(e => e.OwnerId == "seam-box"), e => Assert.Single(e.Membership));
        var union = NativeWorldFixtures.TilingWindows().SelectMany(w => MapResidencyOwnership.InWindow(entries, w)).Distinct().OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(entries.Select(e => e.OwnerId).OrderBy(s => s, StringComparer.Ordinal), union);
    }

    [Fact]
    public void Grids_RefuseMisalignment_AndMatchShardingCells()
    {
        Assert.Contains("grid alignment", Assert.Throws<MapDocumentException>(() => new MapWorldGrids(64f, 2, 4, new Vector2(10, 0)).Validate()).Message);
        var cellGrid = new KhaozEngine.Sharding.CellGrid(Grids.ServerCellSize, Grids.Origin);
        foreach (var t in new[] { new MapTileCoord(-5, 3), new MapTileCoord(7, -9) })
        {
            var centre = MapTileGrid.CenterOf(t, Grids.StorageTileSize);
            var c = cellGrid.CoordFor(centre.X, centre.Y);
            Assert.Equal(new MapServerCellCoord(c.X, c.Y), Grids.ServerCellOf(t));
        }
    }

    [Fact]
    public void EditEffects_MapToAffectedOwnersAndTiles()
    {
        var world = NativeWorldFixtures.BuiltStackedCave();
        var affected = MapResidencyOwnership.Affected(world, Grids, NativeWorldFixtures.CeilingEditEffects());
        Assert.Contains(NativeWorldFixtures.CeilingChunkId, affected.Owners);
        Assert.Equal(new[] { new MapNavTileCoord(0, 0) }, affected.NavTiles);
    }
}
```

- [ ] **Step 2: Run red** `wa_test t7-red "$MP" "FullyQualifiedName~MapResidencyOwnershipTests"`

- [ ] **Step 3: Implement grids, ownership and invalidation**

Membership uses each static's world bounds (`MapShapeBounds.Of` for placements, anchor plus vertex extent for chunks), minimum inclusive and maximum exclusive, with degenerate extents assigned to the tile holding their minimum corner. Nav tiles and server cells follow from storage tiles by floor division of the offset from the origin. `Affected` unions the effects' old and new bounds with the chunks of every listed patch. It returns nothing when `Invalidates` lacks `Physics`, `Nav` and `Residency`. The README states that `MapTileResidency` remains the streaming loader keyed by storage tile, while this index is the ownership R8 consumes.

- [ ] **Step 4: Run green** `wa_test t7-green "$MP" "FullyQualifiedName~MapResidencyOwnershipTests"` (expect 4)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): own residency on aligned grids`

---

### Task 8: Physics registration and provenance

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapPhysicsRegistrationTests.cs`

**Interfaces:**
- Consumes: Task 4 statics, `IPhysicsWorld` (`AddStatic`, `RemoveStatic`, `Origin`, `Rebase`, `Raycast`, `SweepCapsule`), `IPhysicsQueryLeaseSource.AcquireQueryReadLease()`, `IPhysicsCapsuleFeatures.QueryCapsuleFeature(...)` (`Target`, `LeafId`, `FeatureId`), `GroundMoveContext` (six-argument constructor).
- Produces:
  - `public sealed record MapStaticOwner(string OwnerId, MapStaticKind Kind)`.
  - `public sealed class MapPhysicsRegistration : IDisposable` with `public static MapPhysicsRegistration Register(MapBuiltWorld world, IPhysicsWorld physics)`, `IReadOnlyList<StaticHandle> Handles`, `bool TryOwner(StaticHandle handle, out MapStaticOwner owner)`, `bool TryFaceOwner(StaticHandle handle, int triangleIndex, out MapFaceKey face)`, `GroundMoveContext CreateLegacyMoveContext()`, `void Dispose()`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapPhysicsRegistrationTests
{
    [Fact]
    public void Registration_RebasedOriginAndFaultsLeakNothing()
    {
        var world = NativeWorldFixtures.BuiltStackedCave();
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new Vector3(64, 0, -64));
        using (var r = MapPhysicsRegistration.Register(world, physics))
        {
            Assert.True(physics.Raycast(NativeWorldFixtures.AboveLowerFloor - physics.Origin, -Vector3.UnitY, 10f, out var hit));
            Assert.Equal(0f, hit.Point.Y + physics.Origin.Y, 4);
        }
        var fault = new NativeRegistrationFaultWorld(failOnAdd: 3);
        Assert.Throws<InvalidOperationException>(() => MapPhysicsRegistration.Register(world, fault));
        Assert.Equal(0, fault.LiveStaticCount);
        var offOrigin = new NativeRegistrationFaultWorld(origin: new Vector3(0.5f, 0, 0));
        Assert.Contains("whole-metre origin", Assert.Throws<MapDocumentException>(() => MapPhysicsRegistration.Register(world, offOrigin)).Message);
    }

    [Fact]
    public void StackedCave_SidednessHoldsInTheBackend()
    {
        var world = NativeWorldFixtures.BuiltStackedCave();
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(world, physics);
        Assert.True(physics.Raycast(NativeWorldFixtures.InLowerRoom, Vector3.UnitY, 10f, out var ceiling));
        Assert.Equal(3f, ceiling.Point.Y, 4);
        Assert.True(physics.Raycast(NativeWorldFixtures.InLowerRoom, -Vector3.UnitY, 10f, out var floor));
        Assert.Equal(0f, floor.Point.Y, 4);
    }

    [Fact]
    public void FeatureQueryHits_MapToCanonicalOwners()
    {
        var world = NativeWorldFixtures.BuiltStackedCave();
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(world, physics);
        var chunk = r.Handles.First(h => r.TryOwner(h, out var o) && o.Kind == MapStaticKind.TerrainChunk && o.OwnerId == NativeWorldFixtures.LowerFloorChunkId);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Span<CapsuleIncidentFace> faces = stackalloc CapsuleIncidentFace[256];
        var result = ((IPhysicsCapsuleFeatures)physics).QueryCapsuleFeature(lease, chunk, new CapsuleShape(0.2f, 0.4f), Pose.At(NativeWorldFixtures.InLowerRoom), 5f, faces);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var expected = world.Terrain.Chunks.Single(c => c.ChunkId == NativeWorldFixtures.LowerFloorChunkId);
        Assert.True(r.TryFaceOwner(chunk, result.FeatureId, out var face));
        Assert.Equal(expected.TriangleOwners[result.FeatureId], face);
        Assert.Equal(MapFaceRole.SupportFloor, expected.TriangleRoles[result.FeatureId]);
    }

    [Fact]
    public void BridgeDeck_OverhangsSupportAndParapetsBlock()
    {
        var f = NativeWorldFixtures.Bridge();
        var world = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(world, physics);
        foreach (float x in new[] { -8.75f, 8.75f })
        {
            Assert.True(physics.Raycast(new Vector3(x, 10f, 0f), -Vector3.UnitY, 20f, out var deck));
            Assert.Equal(2.825f, deck.Point.Y, 4);
        }
        Assert.Equal(MapPhysicalCertainty.Blocked, new MapPhysicalRelations(world).LineOfSight(f.OnDeckFacingParapet, f.BeyondParapet).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, new MapPhysicalRelations(world).Clearance(new[] { f.DeckCentreFeet }, MoveTuning.Default).Certainty);
    }

    [Fact]
    public void LegacyMoveContext_IsLegacyOnly()
    {
        using var physics = new BepuPhysicsWorld();
        using var native = MapPhysicsRegistration.Register(NativeWorldFixtures.BuiltStackedCave(), physics);
        Assert.Contains("contact controller", Assert.Throws<MapDocumentException>(() => native.CreateLegacyMoveContext()).Message);
        using var legacyPhysics = new BepuPhysicsWorld();
        using var legacy = MapPhysicsRegistration.Register(NativeWorldFixtures.BuiltLegacySlope(), legacyPhysics);
        Assert.Same(legacyPhysics, legacy.CreateLegacyMoveContext().Physics);
    }
}
```

`NativeRegistrationFaultWorld(int failOnAdd = 0, Vector3 origin = default)` is a test `IPhysicsWorld` that counts live statics, reports the given origin and throws `InvalidOperationException` on the nth `AddStatic`. `Bridge()` places a 16 by 5 m deck asset whose walk surface is at 2.825 m, overhanging local x from -9 to 9, with 32 separate one-edge 1 by 1 m parapet wall placements of collision height 3.825 m. These values are a fixture, not a claim about the shipped bridge, which R11 refreezes.

- [ ] **Step 2: Run red** `wa_test t8-red "$MP" "FullyQualifiedName~MapPhysicsRegistrationTests"`

- [ ] **Step 3: Implement registration**

Refuse an origin with a non-integer component. Install statics in descriptor order with `new Pose(position - physics.Origin, orientation)`. Never call inside a held read lease, since the backend refuses mutation then, and say so in the doc comment. On any exception remove every added handle in reverse order and rethrow. `Dispose` removes in reverse and never disposes the caller's world. Keep a handle to owner map and, for terrain chunks, the chunk's triangle owners, so `TryFaceOwner` returns `TriangleOwners[triangleIndex]`. A mesh static's `FeatureId` from the capsule feature query is its triangle index (verify against `BepuPhysicsWorld.CapsuleFeatures` and pin it in the test). `CreateLegacyMoveContext` exists for resolver-1 worlds only: it builds `GroundMoveContext(world.LegacySupportHeight, null, physics, playable-bounds clamp, null, null)`. For native worlds it refuses with "native worlds move on the contact controller (#438 phase 5)". The medium delegate stays null until R4.

- [ ] **Step 4: Run green** `wa_test t8-green "$MP" "FullyQualifiedName~MapPhysicsRegistrationTests"` (expect 5)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): register statics with face provenance`

---

### Task 9: Navigation tile identity

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapNavTiling.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapNavTilingTests.cs`

**Interfaces:**
- Consumes: Task 7 grids and `Affected`, Task 8 `CreateLegacyMoveContext`, `PhysicsNavBake.Capture(GroundMoveContext, PhysicsNavBakeOptions, NavAreaClassifier)` and `BuildProfile(in MoveTuning, NavAreaFilter)` as the capture fixture.
- Produces:
  - `public sealed record MapNavTileOptions(float SeamMarginMetres, string ProfileIdentity, string ControllerIdentity)`.
  - `public sealed record MapNavTile(MapNavTileCoord Coord, MapBox3 Bounds, MapBox3 CaptureBounds, string GeometryDigest, string CaptureIdentity)`.
  - `public sealed record MapNavSeam(MapNavTileCoord A, MapNavTileCoord B, string Digest)`.
  - `public static class MapNavTiling` with `Partition(MapBuiltWorld world, MapWorldGrids grids, MapNavTileOptions options) -> IReadOnlyList<MapNavTile>`, `Seams(IReadOnlyList<MapNavTile> tiles, MapBuiltWorld world, MapNavTileOptions options) -> IReadOnlyList<MapNavSeam>`, `AffectedTiles(MapBuiltWorld world, MapWorldGrids grids, MapNativeEditEffects effects) -> IReadOnlyList<MapNavTileCoord>`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapNavTilingTests
{
    static readonly MapWorldGrids Grids = new(64f, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Options = new(2f, "profile-a", "legacy-stepper");

    [Fact]
    public void AffectedTileRebake_PreservesUnaffectedDigests()
    {
        var before = MapNavTiling.Partition(NativeWorldFixtures.BuiltTwoTileLegacy(), Grids, Options);
        var after = MapNavTiling.Partition(NativeWorldFixtures.BuiltTwoTileLegacyWithMovedCrate(), Grids, Options);
        var affected = MapNavTiling.AffectedTiles(NativeWorldFixtures.BuiltTwoTileLegacyWithMovedCrate(), Grids, NativeWorldFixtures.MovedCrateEffects());
        foreach (var tile in before)
            if (affected.Contains(tile.Coord)) Assert.NotEqual(tile.CaptureIdentity, after.Single(t => t.Coord == tile.Coord).CaptureIdentity);
            else Assert.Equal(tile.CaptureIdentity, after.Single(t => t.Coord == tile.Coord).CaptureIdentity);
    }

    [Fact]
    public void TiledNav_SeamsAndLinksAreDeterministic()
    {
        var world = NativeWorldFixtures.BuiltTwoTileLegacy();
        var tiles = MapNavTiling.Partition(world, Grids, Options);
        Assert.Equal(MapNavTiling.Seams(tiles, world, Options), MapNavTiling.Seams(MapNavTiling.Partition(world, Grids, Options), world, Options));
        Assert.True(NativeWorldFixtures.SeamColumnsAgree(world, tiles[0], tiles[1]));
    }

    [Fact]
    public void Partition_CoversTheWorldExactlyOnce()
    {
        var world = NativeWorldFixtures.BuiltTwoTileLegacy();
        var tiles = MapNavTiling.Partition(world, Grids, Options);
        Assert.Equal(tiles.Count, tiles.Select(t => t.Coord).Distinct().Count());
        Assert.True(NativeWorldFixtures.BoundsUnionEquals(tiles.Select(t => t.Bounds), world.Bounds, Grids));
    }

    [Fact]
    public void CaptureIdentity_FollowsProfileAndController()
    {
        var world = NativeWorldFixtures.BuiltTwoTileLegacy();
        var a = MapNavTiling.Partition(world, Grids, Options)[0].CaptureIdentity;
        Assert.NotEqual(a, MapNavTiling.Partition(world, Grids, Options with { ProfileIdentity = "profile-b" })[0].CaptureIdentity);
        Assert.NotEqual(a, MapNavTiling.Partition(world, Grids, Options with { ControllerIdentity = "contact-controller" })[0].CaptureIdentity);
    }
}
```

`SeamColumnsAgree` captures each tile with `PhysicsNavBake.Capture` over its `CaptureBounds` through `CreateLegacyMoveContext`, and asserts identical heights and traversal for every column within the seam margin.

- [ ] **Step 2: Run red** `wa_test t9-red "$MP" "FullyQualifiedName~MapNavTilingTests"`

- [ ] **Step 3: Implement tiling**

Partition the world bounds into nav tiles. `CaptureBounds` is the tile expanded by `SeamMarginMetres`. `GeometryDigest` covers the digests of every static whose bounds intersect `CaptureBounds`, in ordinal owner order, plus the legacy support function's identity for resolver-1 worlds (taken from `ConsumerPolicyIdentity`). `CaptureIdentity` is SHA-256 over the geometry digest, profile, controller, seam margin and tile coordinate. Seam digests cover both neighbours' geometry digests and the shared edge. `AffectedTiles` widens Task 7's affected set by the seam margin. Vertical layers on native worlds wait for #438 phase 5, which is recorded in the README.

- [ ] **Step 4: Run green** `wa_test t9-green "$MP" "FullyQualifiedName~MapNavTilingTests"` (expect 4)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): identify navigation tiles, seams and invalidation`

---

### Task 10: Native asset scale and collider edits

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/NativeMapAssetLoader.cs`, `KhaozEngine.MapEdit.Tool/NativeCollisionService.cs`, `KhaozEngine.MapEdit.Tool/MapAssetFileWriter.cs`
- Modify: `KhaozEngine.MapEdit.Tool/KhaozEngine.MapEdit.Tool.csproj` (reference `KhaozEngine.MapDoc.Physics`), `KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs` (non-positional `NativeAssets`), the native transaction seam that publishes write sets, `KhaozEngine.MapDoc.Physics/README.md`, `KhaozEngine.MapEdit.Tool/README.md`, `docs/USING-KHAOZENGINE.md`
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativeAssetScaleTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeCollisionServiceTests.cs`

**Interfaces:**
- Consumes: `PropLoader.LoadProp(AssetEntry, PropValidation?) -> GltfMesh` (Render3D) as the reference for mesh reading, `MapResolvedAsset`, Task 4 `MapWorldBuilder`, `MapNativeEditEffects`, `IMapAssetSource`, `PropCollisionFormat.Write(PhysicsShape, Stream)`.
- Produces:
  - `public static class NativeMapAssetLoader { public static GltfMesh Load(MapResolvedAsset asset, MapAssetClosure closure) }`, converting source units once and never renormalizing height.
  - `public sealed record NativeCollisionMeasurement(string PlacementId, string AssetId, float RawMeshMaxY, float EffectiveBottom, float EffectiveTop, string ColliderSha256)`.
  - `public sealed record NativeCollisionEditResult(bool Applied, string BeforeSha256, string AfterSha256, IReadOnlyList<string> AffectedPlacementIds, MapNativeEditEffects Effects)`.
  - `public sealed class NativeCollisionService` with `Measure(string placementId) -> NativeCollisionMeasurement` and `SetHeights(string assetId, float bottom, float top, bool dryRun = true) -> NativeCollisionEditResult`.
  - `public sealed class MapAssetFileWriter(string assetRoot)` with `WriteResource(byte[] bytes, string extension) -> MapAssetRef` (content-addressed, never overwriting) and `WriteManifest(MapAssetManifestDoc manifest, string id) -> MapAssetRef`.
  - `MapNativeWriteSet` gains a non-positional `public bool NativeAssets { get; init; }`, so the released record keeps its constructor and a root swap publishes through the existing transaction seam.
  - Test helpers: `NativeCollisionToolFixture : IDisposable` with `Service`, `ReadAssetDirectoryDigest() -> byte[]` and `Reopen() -> NativeCollisionToolFixture` over a temporary asset directory holding `wall-variant` (compound of boxes, placed as `wall-1`) and `rock-mesh` (triangle mesh). `NativeWorldFixturesForEditor.ScaledMeshAsset(...)` and `MaxVertexY(GltfMesh)`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class NativeCollisionServiceTests
{
    [Fact]
    public void DryRun_NeverWritesOrResizesTheMesh()
    {
        using var f = new NativeCollisionToolFixture();
        byte[] before = f.ReadAssetDirectoryDigest();
        var edit = f.Service.SetHeights("wall-variant", 0.1f, 2.4f, dryRun: true);
        Assert.False(edit.Applied);
        Assert.NotEqual(edit.BeforeSha256, edit.AfterSha256);
        Assert.Equal(before, f.ReadAssetDirectoryDigest());
        Assert.Contains("wall-1", edit.AffectedPlacementIds);
        Assert.True(edit.Effects.Invalidates.HasFlag(MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency));
    }

    [Fact]
    public void Apply_WritesNewContentAndReloadsWithTheNewCollider()
    {
        using var f = new NativeCollisionToolFixture();
        var edit = f.Service.SetHeights("wall-variant", 0.1f, 2.4f, dryRun: false);
        Assert.True(edit.Applied);
        var reopened = f.Reopen();
        Assert.Equal(edit.AfterSha256, reopened.Service.Measure("wall-1").ColliderSha256);
        Assert.Equal(2.3f, reopened.Service.Measure("wall-1").EffectiveTop - reopened.Service.Measure("wall-1").EffectiveBottom, 4);
    }

    [Fact]
    public void BakedMeshCollider_RefusesHeightEdits()
    {
        using var f = new NativeCollisionToolFixture();
        Assert.Contains("compound boxes", Assert.Throws<MapDocumentException>(() => f.Service.SetHeights("rock-mesh", 0f, 1f)).Message);
    }
}

public class NativeAssetScaleTests
{
    [Fact]
    public void NativeLoader_KeepsSourceUnitsWithoutHeightNormalization()
    {
        var f = NativeWorldFixturesForEditor.ScaledMeshAsset(sourceUnitsToMetres: 0.01f, rawMaxY: 250f);
        Assert.Equal(2.5f, NativeWorldFixturesForEditor.MaxVertexY(NativeMapAssetLoader.Load(f.Asset, f.Closure)), 4);
    }
}
```

- [ ] **Step 2: Run red** `wa_test t10-red "$EDITOR" "FullyQualifiedName~NativeCollisionServiceTests|FullyQualifiedName~NativeAssetScaleTests"`

- [ ] **Step 3: Implement the loader, service and writer**

The loader reads the verified mesh resource and applies `SourceUnitsToMetres` once. Height edits apply only to colliders that are boxes or compounds of boxes. They scale vertical extents and positions to the requested asset-local bottom and top, and refuse other shapes with "compound boxes". A dry run computes the new collider bytes and digest and touches no file. Apply writes the new collider resource and a new manifest through `MapAssetFileWriter`, then swaps the document's root reference in one native transaction whose write set has `NativeAssets` set, and returns that transaction's effects with `Physics`, `Nav` and `Residency` invalidation. The mesh resource and placement transforms never change.

- [ ] **Step 4: Run green** `wa_test t10-green "$EDITOR" "FullyQualifiedName~NativeCollisionServiceTests|FullyQualifiedName~NativeAssetScaleTests"` (expect 4)

- [ ] **Step 5: Docs sweep, format, guards and commit** `feat(mapedit): measure and edit native colliders`

The docs sweep covers the MapDoc.Physics README (every produced API, the ownership boundary, the feature-query diagnostics, deferred items), `docs/USING-KHAOZENGINE.md` (a MapDoc.Physics section), the MapEdit.Tool README and `git grep` for every new type name across Markdown.

---

## Review, release preparation, candidate gates and integration

**Per task.** The controller checks the worker's report against the commit, the diff and the red and green logs, then dispatches a fresh reviewer on that range. Findings return to an implementer with a scoped re-review. A load-bearing unresolved finding blocks the next task.

**1. Whole-branch review** over the full range, covering Global Constraints, every cross-task interface, OA22's decisions, the Review Focus tests and the deferred-work table.

**2. Release preparation.** Re-read `main`, `Directory.Build.props`, every tag and any staged version. Ride a staged unreleased version or take the next free minor after 20.30.0. One commit `release(<version>): shared shapes and headless world` changes the version knob, adds the `CHANGELOG.md` entry and updates every declaration `scripts/check-doc-versions.sh` guards. Consumer notes state the new opt-in package, that ground support stays with #438, the feature-query diagnostics, the explicit `Surface` resource refusal until R5 and that no MapDoc format or identity token changed.

**3. Merge current `main`** into the branch and resolve there.

**4. Candidate verification**, once each, in order:

```bash
wa_run final-build dotnet build KhaozEngine.slnx -c Release
wa_run final-suite dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
wa_run final-format dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
wa_run final-dashes sh scripts/check-dashes.sh --tree
wa_run final-prose sh scripts/check-prose.sh --tree
wa_run final-file-size sh scripts/check-file-size.sh --tree
wa_run final-agent sh scripts/check-agent-instructions.sh --tree
wa_run final-doc-versions bash scripts/check-doc-versions.sh
```

Run the build and suite through `build-slot --heavy`. Every command needs exit 0, zero warnings and a nonzero test count. A failure goes through the review path and only the affected commands run again.

**5. Private pack** into a temporary `KHAOZENGINE_FEED`, never the shared feed.

**6. Hosted CI** with `gh workflow run ci.yml --repo APKiwiOrg/KhaozEngine --ref feature/wa-r3-shared-shapes`. A failure is reproduced and attributed before any decision, never retried until green.

**7. Integration.** Fresh-check that `main` and `origin/main` are ancestors of the candidate, then fast-forward `main` and push. If `main` moved, return to step 3.

**8. Shared pack** from `main` after `origin/main` contains the release commit.

**9. Owner release.** Only the owner tags, from a session rooted in the engine (game-template #77).

## Outcome and proof ledger

The controller fills this as execution proceeds. A row is complete only when its commit exists on the branch, its review passed and its logs and exits are recorded.

| Task | Red commit | Green commit | Red logs | Green logs | Review | Proof |
| --- | --- | --- | --- | --- | --- | --- |
| 1 to 10 | | | | | | |
| Whole-branch review | | | | | | |
| Candidate verification | | | | | | |
