# World Authoring R3: Shared Shapes and Headless World Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build one GPU-free, immutable static world from a native MapDoc document, identical on client and server, whose physical shapes drive collision, occlusion and capture and whose shared interaction envelopes drive picking, reach and walk-up stance, including stacked cave floors and ceilings.

**Architecture:** A new opt-in package, `KhaozEngine.MapDoc.Physics`, reads collider and selection resources from the verified asset closure, builds placement geometry and interaction envelopes, compiles R2 terrain faces into bounded physics meshes with face provenance, registers everything into any `IPhysicsWorld`, and evaluates physical relations against that world under the released read lease. Ground movement, support and navigation column sampling stay with the #438 contact-classification controller, which consumes these statics. R3 owns geometry, envelopes, physical relations, residency, registration and navigation tile identity.

**Tech Stack:** C# on the repository's .NET target, System.Numerics, System.Text.Json, xUnit, the existing `KhaozEngine.Physics` seam with `KhaozEngine.Physics.Bepu` as the test backend. No new third-party dependency.

**Spec:**
- `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md` (C3, T1 to T9, DG9).
- The R3 owner gate with OA22, Grimhollow `feature/world-authoring` `docs/superpowers/programs/world-authoring/R3-OWNER-GATE.md` and `DECISIONS.md`.
- `docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md` (ownership, body model).
- `docs/superpowers/plans/2026-10-05-world-authoring-r2-authored-terrain-paint.md` (Downstream allocation, R3 row).

This plan replaces `2026-10-05-world-authoring-r3-shared-shapes-headless-builders.md` on `feature/world-authoring`, which was written against 49b045f75 and runs no task as written against v20.30.0. C3's rule that the consumer's movement view excludes ground handles and uses an analytic floor sampler is superseded by OA22 decision 1 for native worlds: native terrain installs as movement statics that the #438 controller stands on. Resolver-1 worlds keep analytic ground and install no terrain statics, so nothing is excluded anywhere.

## Approval and execution gate

The owner approved the R3 gate decisions as OA22 on 2026-10-10 and asked for this rewrite. This plan needs the owner's plan approval before any code. Execution is subagent-driven and serial (OA8 method), with direct bounded verification (OA20). Approval of this plan does not authorize releases, game adoption or native G1b acceptance. Only the owner tags.

## Global Constraints

| Rule | Requirement |
| --- | --- |
| OA1 to OA3 | One authoring tool, free placement, full native swap. No TileWorld or Grimhollow reference from any `KhaozEngine.MapDoc*` project |
| OA22 decision 1 | R3 adds no step limit, ledge rule or support model. Ground support, seating, steps and navigation column sampling belong to #438. Stance candidates are seated by a caller-bound validator |
| OA22 decision 2 | R3 owns navigation tile partition, capture, profile and link identity, affected-tile invalidation and deterministic seams. Incremental rebake orchestration, cross-tile planning budgets and on-demand loading are the separate #1301 work item |
| OA22 decision 3 | Terrain faces install as physics meshes of at most 1,024 triangles, the future complete-contact limit, with a triangle to `MapFaceKey` map, seam contact proofs and explicit refusal, never truncation or a silent fallback |
| OA22 decisions 4 to 11 | Aligned grids, 1 m minimum reach height applied by vertical sweep before band clipping, collider-derived envelopes only for solid assets, `AuthoredOpen` portals only, the #438 shell for clearance through a public pass-through, physical relations bound to the released `IPhysicsQueryLease`, solidity from the collider resource, R2 canonical faces without retriangulation |
| Formats | No MapDoc document format change and no authored identity token change. `Collider` and `Selection` resources carry `PropCollisionFormat` version 1 bytes. R1 closure loading already refuses any resource `PayloadVersion` other than 1 |
| Package | `KhaozEngine.MapDoc.Physics` is opt-in, in no umbrella, references exactly `KhaozEngine.MapDoc`, `KhaozEngine.Physics` and `KhaozEngine.Movement`, and never a physics backend, renderer, GPU, TileWorld or Grimhollow project |
| Determinism | Two independent builds of the same document, closure and options give byte-identical digests and statics. Envelope math is scalar double, so pick and reach agree across platforms. `IPhysicsQueryLease.GeometryGeneration` never enters any identity |
| Precision | Terrain chunks are exact offsets from whole-metre `MapSubmissionAnchor` values. Registration uses `new Pose(position - world.Origin, orientation)` with an asserted whole-metre origin. Physical relations run origin-relative in a rebased physics world. Placement transforms are R1 document floats, so pick and reach carry at most 0.004 m of position quantization within 32 km of the origin. Installed positions stay within 1,000,000 m on every axis. Tests pin pick, relations and registration near 30 km |
| Policies | Interaction envelope policy `kemap/interaction-envelope/1`, minimum vertical reach height exactly 1 m, bands in absolute world Y. Built world domain `kemap/built-world/1`. Contact skin 0.001 m, the #438 value |
| Repository | Warnings are errors. New behavior gets a headless test in the matching test project under `KhaozEngine.Tests.*` namespaces. New files stay under 800 lines with no `.filesize-baseline` growth. No dash glyphs and no prose semicolons in Markdown. Third-party code only behind engine seams. The released 20.30.0 public API changes only additively |
| Verification | Every local build or test runs through `build-slot`, serially, once per step. No GPU tests, windows, stress or repeated runs. Full suites run once at the candidate gate |

## Review Focus

These inputs are the most likely to hurt a user. Each has a named test in its owning task.

1. A compound doorway placed with yaw 0.371, offset (0.23, 0.17) and scale 1.137 must stay open to pick rays, reach, line of sight and stance, while its jambs block (Tasks 2, 5, 6, 9: `NativeDoorway_StaysOpenAfterYawAndScale`).
2. A cave floor directly above another cave's ceiling must keep the two levels distinct for physics sidedness, line of sight and clearance, while a shaft through both stays open (Tasks 3, 8, 9: `StackedCaveFloorsAndCeilings_PhysicalHalf`).
3. A rebased physics world, a non-whole-metre origin or a fault during registration must not leak handles or apply the origin twice (Task 8: `Registration_RebasedOriginAndFaultsLeakNothing`).
4. A large placement crossing negative storage seams must belong to every tile it touches but be built and registered once (Tasks 7, 8: `LargePlacement_IntersectsEveryTileAndBuildsOnce`).
5. A dense fine patch whose single slot cell exceeds the chunk cap must refuse with `physics chunk capacity`, never truncate (Task 3: `DenseCell_RefusesWithPhysicsChunkCapacity`).

---

## Ownership and deferred work

| Item | Owner | Where it lands |
| --- | --- | --- |
| Native F3 G1b adapter with `MovementQueryLease` and the complete capsule resolver | R3 | A later R3 amendment after #438 phase 4 releases those types |
| Vertical-layer navigation capture on the selected controller | #438 phase 5 | R3 Task 10 provides tile, seam and link identity only |
| Incremental rebake orchestration, planning budgets, on-demand loading | #1301 separate work item | After #438 phase 5 |
| Multi-cell ghost handoff proof | R8 and G3 | R3 Task 7 provides grid mapping and ownership |
| Placement-local support surfaces (`Surface` resources) | R5 | R3 refuses them explicitly |
| Live portal and door state | R5 | R3 publishes `AuthoredOpen` only |
| Water medium and walker profiles | R4 and #1299 | R3 registration leaves the medium delegate null |
| Clearance body model confirmation | Owner, #1344 | R3 uses the #438 shell through `ContactShell` |
| Import-time acceptance of changed targets, distances, occlusion and stances | R11 and G2 | Named differentials at import |

## File Structure

| Path | Responsibility |
| --- | --- |
| `KhaozEngine.MapDoc.Physics/KhaozEngine.MapDoc.Physics.csproj`, `README.md` | Opt-in package, edges pinned by an architecture test |
| `KhaozEngine.MapDoc.Physics/MapAssetShapes.cs` | Collider and selection resources read from the closure |
| `KhaozEngine.MapDoc.Physics/MapShapeBounds.cs` | World bounds of a shape under a pose |
| `KhaozEngine.MapDoc.Physics/MapInteractionPolicy.cs`, `MapInteractionEnvelope.cs`, `MapPlacementGeometry.cs` | Envelope policy and construction, transformed placement colliders |
| `KhaozEngine.MapDoc.Physics/MapTerrainPhysics.cs` | R2 faces to bounded physics meshes with face owners |
| `KhaozEngine.MapDoc/MapNativeResolution.cs` | One resolver routing rule shared by the builder and `NativeDocumentService` |
| `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs`, `MapBuiltWorld.cs` | Complete immutable build, static descriptors, identity, diagnostics |
| `KhaozEngine.MapDoc.Physics/MapShapeQueries.cs`, `MapConvexQueries.cs` | Exact scalar double point, capsule and ray math, GJK for hulls and swept members |
| `KhaozEngine.MapDoc.Physics/MapEnvelopeIndex.cs`, `MapWorldQueries.cs` | Deterministic envelope grid index, pick, reach and physical distance |
| `KhaozEngine.MapDoc.Physics/MapStanceCandidates.cs` | Walk-up candidates seated by a caller-bound validator |
| `KhaozEngine.MapDoc.Physics/MapWorldGrids.cs`, `MapResidencyOwnership.cs` | Aligned grids, tile membership and edit invalidation |
| `KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs` | Installation into `IPhysicsWorld` with static and face provenance |
| `KhaozEngine.Locomotion/Contacts/ContactShell.cs` | Public pass-through to the #438 shell geometry |
| `KhaozEngine.MapDoc.Physics/MapPhysicalRelations.cs` | Line of sight and clearance under an `IPhysicsQueryLease` |
| `KhaozEngine.MapDoc.Physics/MapNavTiling.cs` | Navigation tile partition, capture and link identity, seams, invalidation |
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
| 5 Pick, reach and the envelope index | 4 | 3 to 5 |
| 6 Stance candidates | 5 | 1 to 2 |
| 7 Grids, residency and invalidation | 4 | 2 to 4 |
| 8 Physics registration and provenance | 4 | 2 to 4 |
| 9 Physical relations under a lease | 8 | 3 to 5 |
| 10 Navigation tile identity | 7, 8 | 3 to 5 |
| 11 Native asset scale and collider edits | 4 | 3 to 5 |
| Reviews, release preparation and integration | all | 4 to 6 |
| Total | | 31 to 53 |

## Verification conventions

Run once per execution session from the worktree root:

```bash
test "$(git branch --show-current)" = "feature/wa-r3-shared-shapes"
wa_r3_log_dir="/tmp/grimhollow-orch/logs/wa-r3-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p "$wa_r3_log_dir"
MP=KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj
MAPDOC=KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj
EDITOR=KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj
LOCO=KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj
RUMP=KhaozEngine.Tests/KhaozEngine.Tests.csproj
wa_run()  { n=$1; shift; local rc; if build-slot --label "wa-r3-$n" -- "$@" >"$wa_r3_log_dir/$n.log" 2>&1; then rc=0; else rc=$?; fi; tail -25 "$wa_r3_log_dir/$n.log"; echo "exit $rc for $n"; return "$rc"; }
wa_test() { wa_run "$1" dotnet test "$2" -c Release -m:1 --logger "trx;LogFileName=$1.trx" --results-directory "$wa_r3_log_dir" --filter "$3"; }
```

The existing `ShellGeometry` tests live in `KhaozEngine.Game.Tests/Locomotion/Contacts`, so `ContactShell` tests go there too. A red step is valid only when the named tests fail for the stated reason or the build fails only on a type this task introduces. A green step needs exit 0, zero failed tests and a nonzero matching count read from the log. A filter matching nothing is a failure. Each command runs once. Exit 75 from `build-slot` means nothing ran, so retry later. Every task ends with format on its changed files and the five guards:

```bash
wa_run tN-format dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --include <changed .cs files>
wa_run tN-dashes sh scripts/check-dashes.sh --tree
wa_run tN-prose sh scripts/check-prose.sh --tree
wa_run tN-file-size sh scripts/check-file-size.sh --tree
wa_run tN-agent sh scripts/check-agent-instructions.sh --tree
wa_run tN-doc-versions bash scripts/check-doc-versions.sh
```

Test fixtures live in `KhaozEngine.MapDoc.Physics.Tests/NativeWorldFixtures.cs` and grow task by task. They build closures through `MapAssetClosure.Load(roots, source)` over an in-memory `IMapAssetSource`, collider bytes through `PropCollisionFormat.Write`, and resolver-2 documents, including the spaces `MapSupportQuery` needs, through public R2 APIs only. Every fixture collider sits with its bottom at the placement origin, using a compound child pose where a primitive is centred.

---

### Task 1: Package and asset shapes

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/KhaozEngine.MapDoc.Physics.csproj`, `KhaozEngine.MapDoc.Physics/README.md`, `KhaozEngine.MapDoc.Physics/MapAssetShapes.cs`
- Create: `KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj`, `KhaozEngine.MapDoc.Physics.Tests/NativeWorldFixtures.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapAssetShapesTests.cs`
- Create: `KhaozEngine.Tests/ArchitectureTests.MapDocPhysics.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.cs` (`OptInBackends` gains `"MapDoc.Physics"`), `KhaozEngine.slnx`, `README.md` (package catalog row, "not in any umbrella"), `docs/DEPENDENCY-SEAMS.md` (new package-edges section modelled on TileWorld.Physics)

**Interfaces:**
- Consumes: `MapAssetClosure.GetAsset(string)`, `GetResource(string) -> MapResolvedResource` (`Bytes`, `Kind`, `Reference.Sha256`), `MapResolvedAsset` (`CollisionResourceId`, `SelectionResourceId`, `SupportResourceIds`, `SourceUnitsToMetres`), `PropCollisionFormat.Read(Stream) -> PhysicsShape`.
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

`NativeWorldFixtures.Assets() -> (MapAssetClosure Closure, IReadOnlyList<MapAssetRef> Roots)` provides:
- `doorway`: a compound of two jamb boxes 0.3 by 2.4 by 0.3 m centred at local (-0.75, 1.2, 0) and (0.75, 1.2, 0) and a lintel 1.8 by 0.3 by 0.3 m centred at (0, 2.55, 0), source units 1.
- `corner-wall`: a compound of two 2 by 2 by 0.2 m boxes meeting at a right angle.
- `examine-sign`: a selection box only.
- `crate`: one box 0.6 by 0.2 by 0.6 m as a compound child at local y 0.1.
- `tree`: one cylinder of radius 0.3 and length 6 as a compound child at local y 3.
- `large-building`: one box 100 by 10 by 100 m as a compound child at local y 5.
- `garbage-collider`, `mesh-in-compound` and `deck-with-support` (one `SupportResourceIds` entry).

- [ ] **Step 2: Run red**

Run: `wa_test t1-red "$MP" "FullyQualifiedName~MapAssetShapesTests"`. Expected: compile failure only on `MapAssetShapes`.

- [ ] **Step 3: Implement `MapAssetShapes.Read(MapAssetClosure closure, string assetId)` and the package wiring**

Read each present resource with `PropCollisionFormat.Read` over its bytes after checking its `Kind`. Map every read failure to `MapDocumentException` with "collision payload" and the resource id. Walk compounds recursively and refuse a `TriangleMeshShape` child, which the Bepu backend cannot install. Refuse any non-empty `SupportResourceIds`. The csproj follows `KhaozEngine.TileWorld.Physics.csproj` (PackageId, version knob, README packed, opt-in description). The test csproj follows `KhaozEngine.TileWorld.Physics.Tests.csproj` with `RootNamespace` `KhaozEngine.Tests`.

- [ ] **Step 4: Run green**

Run: `wa_test t1-green "$MP" "FullyQualifiedName~MapAssetShapesTests"` (expect 5) and `wa_test t1-arch "$RUMP" "FullyQualifiedName~ArchitectureTests"`.

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): read collider and selection shapes from the closure`

---

### Task 2: Placement geometry and interaction envelopes

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapShapeBounds.cs`, `MapInteractionPolicy.cs`, `MapInteractionEnvelope.cs`, `MapPlacementGeometry.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapPlacementGeometryTests.cs`

**Interfaces:**
- Consumes: Task 1 `MapAssetShapes.Read`, `MapResolvedDocument` (`Placements`, `AssetClosure`), `MapResolvedPlacement.Transform` (`Position`, `YawRadians`, `Scale`), `PhysicsShapeScale.Uniform(PhysicsShape, float)`.
- Produces:
  - `public static class MapShapeBounds { public static MapBox3 Of(PhysicsShape shape, Pose pose) }`.
  - `public static class MapInteractionPolicy` with `public const string PolicyId = "kemap/interaction-envelope/1"`, `public const float MinimumVerticalReachHeightMetres = 1f`, `public static string CanonicalText { get; }` and `public static string Hash { get; }` (lowercase hex SHA-256 of `CanonicalText`).
  - `public sealed class MapInteractionEnvelope` with `string PlacementId`, `PhysicsShape Shape`, `Pose WorldPose`, `MapBox3 Bounds`, `float RaiseMetres`, `bool DerivedFromCollider`.
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
        Assert.Equal(new Vector3(0.23f, 0f, 0.17f), g.WorldPose.Position);
    }

    [Fact]
    public void LowObject_EnvelopeIsSweptToOneMetreWhilePhysicalBoundsStay()
    {
        var g = MapPlacementShapes.Resolve(NativeWorldFixtures.Crate().Resolved).Single();
        Assert.Equal(0.2, g.ColliderBounds!.Value.MaxY - g.ColliderBounds.Value.MinY, 5);
        Assert.Equal(0.8f, g.Envelope.RaiseMetres, 5);
        Assert.Equal(1.0, g.Envelope.Bounds.MaxY - g.Envelope.Bounds.MinY, 5);
        Assert.Equal(g.ColliderBounds.Value.MinX, g.Envelope.Bounds.MinX, 5);
        Assert.Equal(g.ColliderBounds.Value.MaxZ, g.Envelope.Bounds.MaxZ, 5);
    }

    [Fact]
    public void SolidWithoutSelection_DerivesEnvelopeFromCollider()
        => Assert.True(MapPlacementShapes.Resolve(NativeWorldFixtures.Crate().Resolved).Single().Envelope.DerivedFromCollider);

    [Fact]
    public void NonSolidWithoutSelection_Refuses()
        => Assert.Contains("interaction source", Assert.Throws<MapDocumentException>(() =>
            MapPlacementShapes.Resolve(NativeWorldFixtures.ShapelessProp().Resolved)).Message);

    [Fact]
    public void SlopeSeatedAndCornerWalls_KeepTheirShapes()
    {
        var f = NativeWorldFixtures.SlopeAndCornerWalls();
        var shapes = MapPlacementShapes.Resolve(f.Resolved);
        var seated = shapes.Single(p => p.PlacementId == "slope-wall");
        Assert.Equal(f.SlopeHeightAtWall, seated.ColliderBounds!.Value.MinY, 4);
        var corner = shapes.Single(p => p.PlacementId == "corner-wall");
        Assert.Equal(2, ((CompoundShape)corner.Collider!).Children.Length);
    }

    [Fact]
    public void TwoResolutions_HaveIdenticalDigests()
        => Assert.Equal(
            MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(0.371f, 1.137f).Resolved).Select(p => p.Digest),
            MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(0.371f, 1.137f).Resolved).Select(p => p.Digest));
}
```

Fixtures return `(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved)` and resolve through `MapResolverV2.Resolve` directly until Task 4. `Doorway(yaw, scale)` places `doorway` at (0.23, 0, 0.17) on a flat native floor. `Crate()` places `crate` on the floor. `ShapelessProp()` uses an asset with neither collider nor selection. `SlopeAndCornerWalls()` places `slope-wall` with null Y on a 10 degree native slope (exposing `SlopeHeightAtWall`) and `corner-wall` on the floor.

- [ ] **Step 2: Run red** `wa_test t2-red "$MP" "FullyQualifiedName~MapPlacementGeometryTests"`

- [ ] **Step 3: Implement the produced types**

Combined scale is `asset.SourceUnitsToMetres * transform.Scale`, applied once through `PhysicsShapeScale.Uniform`, which leaves baked meshes at unit scale. `WorldPose` is `new Pose(transform.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, transform.YawRadians))`.

Envelope construction:
- The source is the selection shape when present, otherwise the collider for a solid asset, otherwise refuse with "interaction source".
- `RaiseMetres` is `max(0, MinimumVerticalReachHeightMetres - sourceWorldHeight)`, where height comes from the source's world bounds. A raise applies to the whole object.
- With a raise, every member is swept straight up by `RaiseMetres` in world space. A box or convex hull becomes the `ConvexHullShape` of its corners or points plus the same points raised. A cylinder whose axis is world-vertical is lengthened and its pose raised by half the raise. A tilted cylinder refuses with "interaction envelope". A triangle mesh member becomes the `ConvexHullShape` of its vertices plus those vertices raised, which is acceptable only because the raise applies to objects under 1 m tall. A compound sweeps each child.
- Without a raise the envelope shape is the scaled source unchanged, so apertures survive.
- Bands are absolute world Y and clip at query time, after the raise.

`CanonicalText` is exactly `kemap/interaction-envelope/1\nminimumVerticalReachHeightMetres=1\nsource=selection-else-solid-collider\nraise=world-vertical-sweep-of-members\nband=absolute-world-y-after-raise\n`. `Digest` is SHA-256 over placement id, numeric id, asset id, both resource digests, the float bits of position, yaw and combined scale, `RaiseMetres` bits and `MapInteractionPolicy.Hash`.

- [ ] **Step 4: Run green** `wa_test t2-green "$MP" "FullyQualifiedName~MapPlacementGeometryTests"` (expect 6)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): resolve placement colliders and interaction envelopes`

---

### Task 3: Terrain physics chunks

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapTerrainPhysics.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapTerrainPhysicsTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapTerrainSeamTests.cs`

**Interfaces:**
- Consumes:
  - `MapScopedSurfaces` (`Status`, `Surfaces`, `Witness.Present` for patch keys, `Witness.Records`, `Patch(MapPatchKey)`, `RecordsIn(MapPatchKey)`, `TryRecord(...)`, `ReadWitness`).
  - `MapSurfaceCompiler.Compile(MapSurfaceRef, MapSurfacePatch) -> MapCompiledPatch` (`Anchor`, `Offsets` relative to `Anchor`, `Faces`, `Role`).
  - `MapBoundaryGeometry.ResolveChain(MapBoundaryChain, MapScopedSurfaces) -> MapChainResolution` and `MapWallStripCompiler.Compile(MapWallStrip, MapChainResolution, MapChainResolution) -> MapCompiledStrip`.
  - `MapCompiledFace(Key, Role, A, B, C, Normal)`, where `Normal` is `cross(B - A, C - A)` normalized.
- Produces:
  - `public sealed record MapTerrainChunkPolicy(int MaxTrianglesPerChunk = 1024)`, valid from 64 to 1,024.
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
        var roles = set.Chunks.SelectMany(c => c.TriangleRoles).Distinct().ToList();
        Assert.Contains(MapFaceRole.SupportFloor, roles);
        Assert.Contains(MapFaceRole.Ceiling, roles);
        Assert.Contains(MapFaceRole.Wall, roles);
        foreach (var chunk in set.Chunks)
            for (int t = 0; t < chunk.TriangleOwners.Count; t++)
                Assert.True(NativeWorldFixtures.BackendFrontAlongCompiledNormal(chunk, t));
    }

    [Fact]
    public void ChunksRespectTheCap_AndEveryTriangleHasItsFace()
    {
        var f = NativeWorldFixtures.FinePatch();
        var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy(64));
        Assert.All(set.Chunks, c => Assert.InRange(c.TriangleOwners.Count, 1, 64));
        Assert.Equal(f.CompiledFaceCount, set.Chunks.Sum(c => c.TriangleOwners.Count));
        Assert.Equal(f.CompiledFaceCount, set.Chunks.SelectMany(c => c.TriangleOwners).Distinct().Count());
    }

    [Fact]
    public void LegacyFallbackCells_EmitNoTriangles()
    {
        var f = NativeWorldFixtures.LegacyExteriorWithFallback();
        var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy());
        Assert.Equal(f.FallbackCellCount, set.LegacyFallbackCellsSkipped);
        Assert.DoesNotContain(set.Chunks.SelectMany(c => c.TriangleOwners), f.IsFallbackFace);
    }

    [Fact]
    public void DenseCell_RefusesWithPhysicsChunkCapacity()
        => Assert.Contains("physics chunk capacity", Assert.Throws<MapDocumentException>(() =>
            MapTerrainPhysics.Compile(NativeWorldFixtures.DenseCell().View, new MapTerrainChunkPolicy(64))).Message);

    [Fact]
    public void IncompleteViewAndUnresolvedChain_Refuse()
    {
        Assert.Contains("complete", Assert.Throws<MapDocumentException>(() =>
            MapTerrainPhysics.Compile(NativeWorldFixtures.IncompleteView(), new MapTerrainChunkPolicy())).Message);
        Assert.Contains("unresolved chain", Assert.Throws<MapDocumentException>(() =>
            MapTerrainPhysics.Compile(NativeWorldFixtures.UnresolvedStripView(), new MapTerrainChunkPolicy())).Message);
    }
}

public class MapTerrainSeamTests
{
    [Fact]
    public void ChunkSeams_HaveCompleteContactWithoutGapsOrDuplicates()
    {
        var f = NativeWorldFixtures.FinePatch();
        var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy(64));
        using var physics = new BepuPhysicsWorld();
        foreach (var c in set.Chunks)
            physics.AddStatic(c.Shape, new Pose(new Vector3(c.Anchor.X, c.Anchor.Y, c.Anchor.Z), Quaternion.Identity));
        foreach (var p in f.SeamProbePoints)
        {
            Assert.True(physics.Raycast(p + Vector3.UnitY * 5f, -Vector3.UnitY, 10f, out var hit));
            Assert.Equal(f.HeightAt(p.X, p.Z), hit.Point.Y, 4);
            Assert.True(physics.SweepCapsule(new CapsuleShape(0.3f, 0.6f), Pose.At(p + Vector3.UnitY * 3f), -Vector3.UnitY, 5f, out var sweep));
            Assert.Equal(f.HeightAt(p.X, p.Z), sweep.Point.Y, 3);
        }
    }
}
```

Fixture facts:
- `DenseCell()` holds one slot cell subdivided 32 segments per edge, which compiles to 2 x (31 + 31 + 3) = 130 faces, over the cap of 64.
- `FinePatch()` subdivides several cells 4 per edge so a cap of 64 forces chunk boundaries inside one patch, and lists `SeamProbePoints` exactly on those boundaries, including shared vertices.
- `StackedCave()` is a resolver-2 document with spaces: a lower floor at y 0, a lower ceiling at y 3 except a low-ceiling region at y 1.2, a shaft opening through the lower ceiling and a matching opening through an upper floor at y 3.5, wall strips joining the ceiling and upper floor around the shaft, and an upper ceiling at y 6.5.
- `BackendFrontAlongCompiledNormal(chunk, t)` checks that `cross(C - A, B - A)` of the emitted triangle points along the compiled face normal.

- [ ] **Step 2: Run red** `wa_test t3-red "$MP" "FullyQualifiedName~MapTerrainPhysicsTests|FullyQualifiedName~MapTerrainSeamTests"`

- [ ] **Step 3: Implement `MapTerrainPhysics.Compile`**

Refuse unless `view.Status` is `Complete`. Enumerate patch keys from `view.Witness.Present` in key order, read each with `view.Patch(key)`, and compile every `SupportFloor` and `Ceiling` surface's patch. Skip `PaintOverride` surfaces. Enumerate wall strips from the view's records (`RecordsIn` per patch, or `Witness.Records` with `TryRecord`), resolve both chains with `MapBoundaryGeometry.ResolveChain`, refuse any status other than resolved with "unresolved chain" and the strip id, and compile with `MapWallStripCompiler.Compile`.

Faces only come from compiled `Faces`, so legacy fallback cells contribute none, and their count is reported. Faces are used as R2 compiled them, never retriangulated. Group a patch's faces by slot cell (`MapFaceKey.Primitive`), then split the 64 by 64 slot block recursively into quadrants until each chunk has at most the cap. A single slot cell over the cap refuses with "physics chunk capacity", the patch key and the cell. Strips chunk by contiguous primitive ranges under the same cap.

Emit every triangle as (A, C, B). The Bepu backend's one-sided front is `cross(C - A, B - A)` (see `TileColliderBuilder.Ground.cs`), and R2's normal is `cross(B - A, C - A)`, so the swap makes each mesh face the role's open side.

Chunk ids are `<surfaceId>/<SlotX>,<SlotZ>/<minCellX>,<minCellZ>,<sizeCells>` for patches and `<stripId>/<firstPrimitive>` for strips. Order chunks by ordinal id and triangles by `MapFaceKey` order. `Digest` covers id, anchor, vertex float bits, indices, owners and roles.

- [ ] **Step 4: Run green** `wa_test t3-green "$MP" "FullyQualifiedName~MapTerrainPhysicsTests|FullyQualifiedName~MapTerrainSeamTests"` (expect 6)

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
  - `public static class MapNativeResolution { public static MapResolvedDocument Resolve(MapDocument document, MapAssetClosure assets, MapResolveOptions options, Func<float,float,float>? legacySupportHeight = null) }`. Resolver identity (1, 1) requires `legacySupportHeight` and options resolver 1. Identity (1, 2) captures the document's surfaces and uses resolver 2. Any other pairing refuses with "resolver". It receives a loaded closure, so `NativeDocumentService` keeps its identity check and `ValidateLocal` ahead of loading the closure.
  - `public sealed record MapWorldBuildOptions(MapResolveOptions Resolve, string ConsumerPolicyIdentity, MapTerrainChunkPolicy Chunks, Func<float,float,float>? LegacySupportHeight = null, int BuilderVersion = 1)`.
  - `public enum MapStaticKind { Placement, TerrainChunk }`.
  - `public sealed record MapStaticDescriptor(string OwnerId, MapStaticKind Kind, PhysicsShape Shape, Vector3 Position, Quaternion Orientation, MapBox3 Bounds, string Digest, IReadOnlyList<MapFaceKey> TriangleOwners)`.
  - `public enum MapFeatureQuerySupport { Supported, LeafCapacity, CurvedUntilPhase2b, MeshTriangleCapacity }` and `public sealed record MapStaticDiagnostic(string OwnerId, MapFeatureQuerySupport Support)`.
  - `public sealed class MapBuiltWorld` with `MapResolvedDocument Document`, `MapScopedSurfaces Surfaces`, `IReadOnlyList<MapPlacementGeometry> Placements`, `MapTerrainChunkSet Terrain`, `IReadOnlyList<MapStaticDescriptor> Statics`, `IReadOnlyList<MapStaticDiagnostic> Diagnostics`, `MapBox3 Bounds`, `string AuthoredHash`, `string BuildHash`, `bool IsNative` (resolver 2), `Func<float,float,float>? LegacySupportHeight`, `string LegacyTerrainIdentity`.
  - `public static class MapWorldBuilder { public static MapBuiltWorld Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) }`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWorldBuilderTests
{
    [Fact]
    public void TwoIndependentHeads_BuildIdenticalWorlds()
    {
        var a = NativeWorldFixtures.BuildStackedCave();
        var b = NativeWorldFixtures.BuildStackedCave();
        Assert.Equal(a.BuildHash, b.BuildHash);
        Assert.Equal(a.AuthoredHash, b.AuthoredHash);
        Assert.Equal(a.Statics.Select(s => (s.OwnerId, s.Digest, s.Position, s.Orientation)), b.Statics.Select(s => (s.OwnerId, s.Digest, s.Position, s.Orientation)));
    }

    [Fact]
    public void InvalidInputs_Refuse()
    {
        Assert.Contains("partial", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildPartialWindow()).Message);
        Assert.Contains("resolver", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithResolverMismatch()).Message);
        Assert.Contains("legacy support height", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildLegacyWithoutHeight()).Message);
        Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithPlacementScale(float.NaN));
        Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithPlacementScale(0f));
        Assert.Contains("1,000,000", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithPlacementAt(1_000_001f)).Message);
    }

    [Fact]
    public void BuildHash_FollowsPolicyIdentityAndChunkPolicy()
    {
        var f = NativeWorldFixtures.StackedCave();
        string baseline = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()).BuildHash;
        Assert.NotEqual(baseline, MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options() with { ConsumerPolicyIdentity = "other" }).BuildHash);
        Assert.NotEqual(baseline, MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options() with { Chunks = new MapTerrainChunkPolicy(512) }).BuildHash);
    }

    [Fact]
    public void FeatureQueryLimits_AreReportedNotRefused()
    {
        var world = NativeWorldFixtures.BuildTreeAndWideCompound();
        Assert.Contains(new MapStaticDiagnostic("tree", MapFeatureQuerySupport.CurvedUntilPhase2b), world.Diagnostics);
        Assert.Contains(new MapStaticDiagnostic("parapet-70", MapFeatureQuerySupport.LeafCapacity), world.Diagnostics);
    }
}
```

`MapNativeResolutionTests` asserts that identity (1, 1) with resolver-1 options matches `MapResolver.Resolve`, that identity (1, 2) matches `MapResolverV2.Resolve(...).Document`, and that (1, 2) with resolver-1 options refuses with "resolver". The existing `NativeDocumentServiceResolverTests` stay green unchanged.

- [ ] **Step 2: Run red** `wa_test t4-red "$MP" "FullyQualifiedName~MapWorldBuilderTests"` and `wa_test t4-red-mapdoc "$MAPDOC" "FullyQualifiedName~MapNativeResolutionTests"`

- [ ] **Step 3: Implement routing, the builder and the built world**

Refuse a partial window first (`document.Tiles is { IsPartial: true }`). Resolve through `MapNativeResolution`. For resolver 2, take `MapScopedSurfaces.CompleteView(document.Surfaces)` and compile terrain with Task 3. For resolver 1, terrain stays analytic: `Surfaces` is `CompleteView` over an empty `MapSurfaceSet`, the chunk set is empty with that view's witness, `LegacySupportHeight` is kept, and `LegacyTerrainIdentity` is SHA-256 over the document's `Terrain` block and sculpt tiles as R2's content digest serializes them. Statics are placement colliders (non-solid placements have none) then terrain chunks, each in ordinal owner order. A terrain static's `Position` is its anchor in whole metres. Refuse any static position beyond 1,000,000 m on an axis. Diagnostics name compounds over 64 leaves, cylinders and curved shapes, and meshes over 65,536 triangles. `BuildHash` is SHA-256 over the canonical JSON `{ "domain": "kemap/built-world/1", authoredHash, builderVersion, consumerPolicyIdentity, interactionPolicyHash, maxTrianglesPerChunk, legacyTerrainIdentity, placements: [[id, digest]], terrain: [[chunkId, digest]] }`. Change `NativeDocumentService` to call `MapNativeResolution` with no behavior change.

- [ ] **Step 4: Run green** `wa_test t4-green "$MP" "FullyQualifiedName~MapWorldBuilderTests"` (expect 4), `wa_test t4-green-mapdoc "$MAPDOC" "FullyQualifiedName~MapNativeResolutionTests"` (expect 3), `wa_test t4-regress-editor "$EDITOR" "FullyQualifiedName~NativeDocumentService"`

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): build the complete immutable world`

---

### Task 5: Pick, reach and the envelope index

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapShapeQueries.cs`, `MapConvexQueries.cs`, `MapEnvelopeIndex.cs`, `MapWorldQueries.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapWorldQueriesTests.cs`

**Interfaces:**
- Consumes: Task 4 `MapBuiltWorld`, `MovementBody(Vector3 centre, float radius, float halfHeight)` (half height at least the radius, segment half length is half height minus radius), `ReachTarget.Box`, `ReachGeometry.Distance/Within`.
- Produces:
  - `public readonly record struct MapPickRay(Vector3 Origin, Vector3 Direction, float MaxDistance)`, `public readonly record struct MapInteractionBand(float MinY, float MaxY)`, `public sealed record MapPickHit(string PlacementId, long? NumericId, float Distance, Vector3 Point, Vector3 Normal)`.
  - `public static class MapShapeQueries` with `Distance(Vector3 point, PhysicsShape shape, Pose pose) -> double`, `Distance(in MovementBody body, PhysicsShape shape, Pose pose) -> double`, `Raycast(MapPickRay ray, PhysicsShape shape, Pose pose, out double distance, out Vector3 normal) -> bool`.
  - `internal static class MapConvexQueries` with deterministic scalar double GJK distance and GJK ray cast over a point-set support function, used for hulls and swept envelope members.
  - `internal sealed class MapEnvelopeIndex`, a fixed 16 m grid over envelope bounds traversed by a deterministic 3D DDA.
  - `public sealed class MapWorldQueries(MapBuiltWorld world)` with `Pick(MapPickRay ray, MapInteractionBand? band = null) -> MapPickHit?`, `Distance(in MovementBody body, string placementId, MapInteractionBand? band = null) -> float`, `Within(in MovementBody body, string placementId, float range, float tolerance = 0f, MapInteractionBand? band = null) -> bool`, `PhysicalDistance(in MovementBody body, string placementId) -> float`, and `internal int LastPickInspectedEnvelopes`.

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
        Assert.Equal("doorway", q.Pick(new MapPickRay(t.TransformPoint(new Vector3(-0.75f, 1, -2)), through, 4f))!.PlacementId);
        Assert.True(q.PhysicalDistance(new MovementBody(t.TransformPoint(new Vector3(0, 1, 0)), 0.2f, 0.8f), "doorway") > 0);
    }

    [Fact]
    public void TreeBand_HitsAtBasePlusOneAndRefusesAtBasePlusThree()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildTree());
        var band = new MapInteractionBand(0f, 2f);
        Assert.NotNull(q.Pick(new MapPickRay(new Vector3(0, 1, -2), Vector3.UnitZ, 4f), band));
        Assert.Null(q.Pick(new MapPickRay(new Vector3(0, 3, -2), Vector3.UnitZ, 4f), band));
    }

    [Fact]
    public void LowObject_ReachUsesTheOneMetreEnvelope()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildCrate());
        var head = new MovementBody(new Vector3(0, 1.6f, -0.7f), 0.2f, 0.3f);
        Assert.True(q.Within(head, "crate", 0.5f));
        Assert.Equal(0.44f, q.Distance(head, "crate"), 3);
        Assert.True(q.PhysicalDistance(head, "crate") > 1f);
    }

    [Fact]
    public void EqualDistancePicks_BreakTiesByOrdinalPlacementId()
        => Assert.Equal("a-crate", new MapWorldQueries(NativeWorldFixtures.BuildTwinCrates()).Pick(new MapPickRay(new Vector3(0, 0.1f, -2), Vector3.UnitZ, 4f))!.PlacementId);

    [Fact]
    public void Pick_InspectsABoundedNumberOfEnvelopes()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildCrateField(100, 100, spacingMetres: 4f));
        Assert.NotNull(q.Pick(new MapPickRay(new Vector3(0.5f, 0.1f, -2f), Vector3.UnitZ, 30f)));
        Assert.InRange(q.LastPickInspectedEnvelopes, 1, 64);
    }

    [Fact]
    public void FarRegion_PickAndReachHoldTheirTolerance()
    {
        var near = new MapWorldQueries(NativeWorldFixtures.BuildCrate());
        var far = new MapWorldQueries(NativeWorldFixtures.BuildCrateAt(30_000f, 0f, -30_000f));
        var offset = new Vector3(30_000f, 0f, -30_000f);
        var head = new MovementBody(new Vector3(0, 1.6f, -0.7f), 0.2f, 0.3f);
        Assert.Equal(near.Distance(head, "crate"), far.Distance(new MovementBody(head.Centre + offset, 0.2f, 0.3f), "crate"), 0.004f);
    }
}
```

`BuildCrate()` places the crate's bottom at the placement origin, y 0. The reach distance in `LowObject_ReachUsesTheOneMetreEnvelope` is from the capsule segment point (y 1.5, z -0.7) to the swept envelope's top edge (y 1.0, z -0.3), which is sqrt(0.25 + 0.16) - 0.2 = 0.44 m.

- [ ] **Step 2: Run red** `wa_test t5-red "$MP" "FullyQualifiedName~MapWorldQueriesTests"`

- [ ] **Step 3: Implement queries and the index**

Pick, Distance and Within query envelopes only. Pick walks the envelope index, tests each candidate exactly, ignores hits outside the band's Y range, takes the nearest hit and breaks equal distances by ordinal placement id. Box members rotated about Y alone reuse `ReachTarget.Box` and `ReachGeometry`. Hulls and swept members use `MapConvexQueries`. Cylinders use exact distance and cap math. Meshes use segment to triangle distance and ray to triangle tests. Compounds recurse through child poses, never an AABB. `PhysicalDistance` uses colliders only. All math is scalar double. No physics backend is involved.

- [ ] **Step 4: Run green** `wa_test t5-green "$MP" "FullyQualifiedName~MapWorldQueriesTests"` (expect 6)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): pick and reach on shared envelopes`

---

### Task 6: Stance candidates

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapStanceCandidates.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapStanceCandidatesTests.cs`

**Interfaces:**
- Consumes: Task 5 `MapWorldQueries.Within`, `MapBuiltWorld.LegacySupportHeight` for resolver 1.
- Produces:
  - `public delegate bool MapStanceValidator(Vector3 candidateFeet, out Vector3 seatedFeet)`. A game binds it to the #438 controller's placement proof, which seats the candidate or refuses it.
  - `public sealed record MapStanceOptions(float Spacing, float Range, float Tolerance = 0f, MapInteractionBand? Band = null)`.
  - `public static class MapStanceCandidates { public static IReadOnlyList<Vector3> Find(MapBuiltWorld world, string placementId, Vector3 actorFeet, in MoveTuning tuning, MapStanceOptions options, MapStanceValidator validate) }`, seated feet positions ordered by distance to `actorFeet`, then X, Z, Y.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapStanceCandidatesTests
{
    [Fact]
    public void RotatedCompound_CandidatesAreDeterministicReachableAndSeatedByTheValidator()
    {
        var f = NativeWorldFixtures.Doorway(0.371f, 1.137f);
        var world = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        var q = new MapWorldQueries(world);
        var options = new MapStanceOptions(Spacing: 0.25f, Range: 0.4f);
        MapStanceValidator seatAtFloor = (Vector3 c, out Vector3 seated) => { seated = new Vector3(c.X, 0f, c.Z); return true; };
        var a = MapStanceCandidates.Find(world, "doorway", new Vector3(0, 0, -2), MoveTuning.Default, options, seatAtFloor);
        var b = MapStanceCandidates.Find(world, "doorway", new Vector3(0, 0, -2), MoveTuning.Default, options, seatAtFloor);
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        var t = MoveTuning.Default;
        Assert.All(a, p => Assert.Equal(0f, p.Y));
        Assert.All(a, p => Assert.True(q.Within(new MovementBody(p + Vector3.UnitY * t.CapsuleHalfHeight, t.CapsuleRadius, t.CapsuleHalfHeight), "doorway", 0.4f)));
    }

    [Fact]
    public void ValidatorRejectingEverything_GivesNoCandidates()
    {
        MapStanceValidator refuse = (Vector3 c, out Vector3 seated) => { seated = c; return false; };
        Assert.Empty(MapStanceCandidates.Find(NativeWorldFixtures.BuildCrate(), "crate", new Vector3(0, 0, -2), MoveTuning.Default, new MapStanceOptions(0.25f, 0.5f), refuse));
    }

    [Fact]
    public void LegacyWorld_ProposesAtTheLegacySupportHeight()
    {
        var proposed = new List<Vector3>();
        MapStanceValidator record = (Vector3 c, out Vector3 seated) => { proposed.Add(c); seated = c; return true; };
        MapStanceCandidates.Find(NativeWorldFixtures.BuildLegacySlope(), "crate", new Vector3(0, 0, -2), MoveTuning.Default, new MapStanceOptions(0.25f, 0.5f), record);
        Assert.NotEmpty(proposed);
        Assert.All(proposed, p => Assert.Equal(NativeWorldFixtures.LegacySlopeHeight(p.X, p.Z), p.Y));
    }
}
```

- [ ] **Step 2: Run red** `wa_test t6-red "$MP" "FullyQualifiedName~MapStanceCandidatesTests"`

- [ ] **Step 3: Implement `Find`**

Walk the envelope's XZ footprint outline, outset by the capsule radius, at `Spacing`, including aperture sides of compounds. Propose each point at the envelope's base Y for native worlds, or at `LegacySupportHeight` for resolver-1 worlds. Drop points outside playable bounds. Call `validate`, keep only seated positions it accepts, then drop seated positions outside envelope reach. No step, ledge, slope or support rule lives here, and R2's column query is not consulted, so the #438 controller owns the seated height. Sort as specified.

- [ ] **Step 4: Run green** `wa_test t6-green "$MP" "FullyQualifiedName~MapStanceCandidatesTests"` (expect 3)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): propose walk-up candidates for the controller to seat`

---

### Task 7: Grids, residency and invalidation

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapWorldGrids.cs`, `MapResidencyOwnership.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapResidencyOwnershipTests.cs`

**Interfaces:**
- Consumes: Task 4 statics (`Bounds`, `OwnerId`), `MapTileGrid.CoordOf`, `MapTileGrid.CenterOf`, `MapTileCoord`, `MapTileRect`, `MapNativeEditEffects` (`OldBounds`, `NewBounds`, `Patches`, `Invalidates`).
- Produces:
  - `public readonly record struct MapNavTileCoord(int X, int Z)`, `public readonly record struct MapServerCellCoord(int X, int Z)`.
  - `public sealed record MapWorldGrids(float StorageTileSize, int NavTileStorageTiles, int ServerCellStorageTiles, Vector2 Origin)` with `Validate(MapBuiltWorld world)`, `NavTileOf(MapTileCoord) -> MapNavTileCoord`, `ServerCellOf(MapTileCoord) -> MapServerCellCoord`, `NavTileSize`, `ServerCellSize`. The storage tile size equals the document's `TileSize` and is a positive whole number of metres, the multipliers are positive, and each origin component is a whole multiple of the storage tile size. Otherwise refuse with "grid alignment".
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
        var world = NativeWorldFixtures.BuildLargeBuilding();
        var entry = Assert.Single(MapResidencyOwnership.Build(world, Grids), e => e.OwnerId == "large-building");
        Assert.Contains(new MapTileCoord(-1, -1), entry.Membership);
        Assert.Contains(new MapTileCoord(0, 0), entry.Membership);
        Assert.Equal(entry.Membership.Count, entry.Membership.Distinct().Count());
        Assert.Single(world.Statics, s => s.OwnerId == "large-building");
    }

    [Fact]
    public void ExactSeams_DoNotOvercount_AndWindowsUnionToTheWhole()
    {
        var world = NativeWorldFixtures.BuildSeamAligned();
        var entries = MapResidencyOwnership.Build(world, Grids);
        Assert.Single(Assert.Single(entries, e => e.OwnerId == "seam-box").Membership);
        var union = NativeWorldFixtures.TilingWindows().SelectMany(w => MapResidencyOwnership.InWindow(entries, w)).Distinct().OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(entries.Select(e => e.OwnerId).OrderBy(s => s, StringComparer.Ordinal), union);
    }

    [Fact]
    public void Grids_RefuseMisalignment_AndMatchShardingCells()
    {
        var world = NativeWorldFixtures.BuildSeamAligned();
        Assert.Contains("grid alignment", Assert.Throws<MapDocumentException>(() => new MapWorldGrids(64f, 2, 4, new Vector2(10, 0)).Validate(world)).Message);
        Assert.Contains("grid alignment", Assert.Throws<MapDocumentException>(() => new MapWorldGrids(32f, 2, 4, Vector2.Zero).Validate(world)).Message);
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
        var world = NativeWorldFixtures.BuildStackedCave();
        var affected = MapResidencyOwnership.Affected(world, Grids, NativeWorldFixtures.CeilingEditEffects());
        Assert.Contains(NativeWorldFixtures.CeilingChunkId, affected.Owners);
        Assert.Equal(new[] { new MapNavTileCoord(0, 0) }, affected.NavTiles);
    }
}
```

`BuildSeamAligned()` uses document `TileSize` 64 and places `seam-box` with its minimum corner exactly on a tile seam. `BuildStackedCave()` keeps the whole cave inside storage tile (0, 0).

- [ ] **Step 2: Run red** `wa_test t7-red "$MP" "FullyQualifiedName~MapResidencyOwnershipTests"`

- [ ] **Step 3: Implement grids, ownership and invalidation**

Membership uses each static's `Bounds`, minimum inclusive and maximum exclusive, with degenerate extents assigned to the tile holding their minimum corner. Nav tiles and server cells follow from storage tiles by floor division of the offset from the origin. `Affected` unions the effects' old and new bounds with the chunks of every listed patch. It returns nothing when `Invalidates` has none of `Physics`, `Nav` and `Residency`. The README states that `MapTileResidency` remains the streaming loader keyed by storage tile, while this index is the ownership R8 consumes.

- [ ] **Step 4: Run green** `wa_test t7-green "$MP" "FullyQualifiedName~MapResidencyOwnershipTests"` (expect 4)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): own residency on aligned grids`

---

### Task 8: Physics registration and provenance

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapPhysicsRegistrationTests.cs`

**Interfaces:**
- Consumes: Task 4 statics, `IPhysicsWorld` (`AddStatic`, `RemoveStatic`, `Origin`, `Rebase`, `Raycast`), `IPhysicsQueryLeaseSource.AcquireQueryReadLease()`, `IPhysicsCapsuleFeatures.QueryCapsuleFeature(lease, target, capsule, pose, maximumSeparationMetres, faces)` with `maximumSeparationMetres` at most 0.01 and incident faces whose `FaceId` is the mesh triangle index, `GroundMoveContext` (six-argument constructor, `Physics`).
- Produces:
  - `public sealed record MapStaticOwner(string OwnerId, MapStaticKind Kind)`.
  - `public sealed class MapPhysicsRegistration : IDisposable` with `public static MapPhysicsRegistration Register(MapBuiltWorld world, IPhysicsWorld physics)`, `MapBuiltWorld World`, `IPhysicsWorld Physics`, `IReadOnlyList<StaticHandle> Handles`, `bool TryOwner(StaticHandle handle, out MapStaticOwner owner)`, `bool TryFaceOwner(StaticHandle handle, int faceId, out MapFaceKey face)`, `GroundMoveContext CreateLegacyMoveContext()`, `void Dispose()`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapPhysicsRegistrationTests
{
    [Fact]
    public void Registration_RebasedOriginAndFaultsLeakNothing()
    {
        var world = NativeWorldFixtures.BuildStackedCave();
        using (var physics = new BepuPhysicsWorld())
        {
            physics.Rebase(new Vector3(64, 0, -64));
            using var r = MapPhysicsRegistration.Register(world, physics);
            Assert.True(physics.Raycast(NativeWorldFixtures.InLowerRoom - physics.Origin, -Vector3.UnitY, 10f, out var hit));
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
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), physics);
        Assert.True(physics.Raycast(NativeWorldFixtures.InLowerRoom, Vector3.UnitY, 10f, out var ceiling));
        Assert.Equal(3f, ceiling.Point.Y, 4);
        Assert.True(physics.Raycast(NativeWorldFixtures.InLowerRoom, -Vector3.UnitY, 10f, out var floor));
        Assert.Equal(0f, floor.Point.Y, 4);
        Assert.True(physics.Raycast(NativeWorldFixtures.InShaftBelowUpperFloor, Vector3.UnitY, 10f, out var shaftTop));
        Assert.Equal(6.5f, shaftTop.Point.Y, 4);
    }

    [Fact]
    public void FeatureQueryFaces_MapToCanonicalOwners()
    {
        var world = NativeWorldFixtures.BuildStackedCave();
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(world, physics);
        var chunk = r.Handles.First(h => r.TryOwner(h, out var o) && o.Kind == MapStaticKind.TerrainChunk && o.OwnerId == NativeWorldFixtures.LowerFloorChunkId);
        var expected = world.Terrain.Chunks.Single(c => c.ChunkId == NativeWorldFixtures.LowerFloorChunkId);
        foreach (var at in new[] { NativeWorldFixtures.InsideOneLowerFloorTriangle, NativeWorldFixtures.OnLowerFloorTriangleEdge })
        {
            using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
            Span<CapsuleIncidentFace> faces = stackalloc CapsuleIncidentFace[256];
            var result = ((IPhysicsCapsuleFeatures)physics).QueryCapsuleFeature(lease, chunk, new CapsuleShape(0.2f, 0.4f), Pose.At(at + new Vector3(0, 0.405f, 0)), 0.01f, faces);
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            for (int i = 0; i < result.Written; i++)
            {
                Assert.True(r.TryFaceOwner(chunk, faces[i].FaceId, out var face));
                Assert.Equal(expected.TriangleOwners[faces[i].FaceId], face);
                Assert.Equal(MapFaceRole.SupportFloor, expected.TriangleRoles[faces[i].FaceId]);
            }
        }
    }

    [Fact]
    public void BridgeDeck_OverhangsSupportAndParapetsBlock()
    {
        using var physics = new BepuPhysicsWorld();
        using var r = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildBridge(), physics);
        foreach (float x in new[] { -8.75f, 8.75f })
        {
            Assert.True(physics.Raycast(new Vector3(x, 10f, 0f), -Vector3.UnitY, 20f, out var deck));
            Assert.Equal(2.825f, deck.Point.Y, 4);
        }
        Assert.True(physics.Raycast(NativeWorldFixtures.OnDeckFacingParapet, Vector3.UnitZ, 3f, out var parapet));
        Assert.True(r.TryOwner(parapet.Body!.Value, out var owner) && owner.OwnerId.StartsWith("parapet-", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyMoveContext_IsLegacyOnly()
    {
        using var physics = new BepuPhysicsWorld();
        using var native = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), physics);
        Assert.Contains("contact controller", Assert.Throws<MapDocumentException>(() => native.CreateLegacyMoveContext()).Message);
        using var legacyPhysics = new BepuPhysicsWorld();
        using var legacy = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildLegacySlope(), legacyPhysics);
        Assert.Same(legacyPhysics, legacy.CreateLegacyMoveContext().Physics);
    }
}
```

Fixture facts:
- `NativeRegistrationFaultWorld(int failOnAdd = 0, Vector3 origin = default)` is a test `IPhysicsWorld` that counts live statics, reports the given origin and throws `InvalidOperationException` on the nth `AddStatic`.
- The capsule poses in `FeatureQueryFaces_MapToCanonicalOwners` put its bottom 0.005 m above the floor, one inside a single triangle and one over a shared triangle edge.
- `BuildBridge()` places a 16 by 5 m deck asset whose walk surface is at 2.825 m, overhanging local x from -9 to 9, with 32 separate one-edge 1 by 1 m parapet wall placements (`parapet-00` to `parapet-31`) of collision height 3.825 m. These values are a fixture, not a claim about the shipped bridge, which R11 refreezes.

- [ ] **Step 2: Run red** `wa_test t8-red "$MP" "FullyQualifiedName~MapPhysicsRegistrationTests"`

- [ ] **Step 3: Implement registration**

Refuse an origin with a non-integer component, with "whole-metre origin". Install statics in descriptor order with `new Pose(position - physics.Origin, orientation)`. Never call inside a held read lease, since the backend refuses mutation then, and say so in the doc comment. On any exception remove every added handle in reverse order and rethrow. `Dispose` removes in reverse and never disposes the caller's world. Keep a handle to owner map and, for terrain chunks, the chunk's triangle owners, so `TryFaceOwner(handle, faceId)` returns `TriangleOwners[faceId]` for an incident face. A feature id alone is not a triangle index, since edges and vertices number after faces. `CreateLegacyMoveContext` exists for resolver-1 worlds only: it builds `GroundMoveContext(world.LegacySupportHeight, null, physics, playable-bounds clamp, null, null)`. For native worlds it refuses with "native worlds move on the contact controller (#438 phase 5)". The medium delegate stays null until R4.

- [ ] **Step 4: Run green** `wa_test t8-green "$MP" "FullyQualifiedName~MapPhysicsRegistrationTests"` (expect 5)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): register statics with face provenance`

---

### Task 9: Physical relations under a lease

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/ContactShell.cs`, `KhaozEngine.MapDoc.Physics/MapPhysicalRelations.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactShellTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPhysicalRelationsTests.cs`

**Interfaces:**
- Consumes: Task 8 `MapPhysicsRegistration` (`World`, `Physics`, `TryOwner`), `IPhysicsQueryLease` (`SourceWorld`, `Origin`, `AssertCurrent()`), `IPhysicsWorld.Raycast` and `ComputePenetration`, internal `ShellGeometry` (`Validate`, `Shape`, `Centre`), `MapFramePoint(WorldFrame Frame, Vector3 Local)`, `MapPhysicalCertainty`.
- Produces:
  - `public static class ContactShell` in `KhaozEngine.Locomotion` with `Validate(in MoveTuning tuning)`, `Shape(in MoveTuning tuning) -> CapsuleShape` and `Centre(Vector3 feet, in MoveTuning tuning) -> Vector3`, each delegating to `ShellGeometry` so the #438 body model has one implementation.
  - `public sealed record MapPhysicalResult(MapPhysicalCertainty Certainty, string? BlockingOwner, float? BlockDistance, string BuildHash, string TerrainWitnessDigest)`.
  - `public sealed class MapPhysicalRelations(MapPhysicsRegistration registration)` with `LineOfSight(IPhysicsQueryLease lease, MapFramePoint from, MapFramePoint to) -> MapPhysicalResult` and `Clearance(IPhysicsQueryLease lease, IReadOnlyList<MapFramePoint> feetPath, in MoveTuning tuning) -> MapPhysicalResult`.

- [ ] **Step 1: Write the failing tests**

`ContactShellTests` asserts that `ContactShell.Shape` and `Centre` equal `ShellGeometry`'s results for `MoveTuning.Default` and two other tunings, and that `Validate` refuses both invalid tunings `ShellGeometry.Validate` refuses (a shell shorter than its diameter, and a shell that cannot clear its steepest walkable plane with the 0.001 m skin).

```csharp
public class MapPhysicalRelationsTests
{
    [Fact]
    public void StackedCave_LineOfSightAndClearanceKeepLevelsDistinct()
    {
        var f = NativeWorldFixtures.StackedCave();
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Assert.Equal(MapPhysicalCertainty.Blocked, r.LineOfSight(lease, f.LowerRoom, f.UpperRoom).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.LineOfSight(lease, f.LowerUnderShaft, f.UpperOverShaft).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.Clearance(lease, new[] { f.LowerRoomFeet }, MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, r.Clearance(lease, new[] { f.UnderLowCeilingFeet }, MoveTuning.Default).Certainty);
    }

    [Fact]
    public void Doorway_JambBlocksWithItsOwner_OpeningIsClear_OutsideIsUnknown()
    {
        var f = NativeWorldFixtures.Doorway(0.371f, 1.137f);
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var blocked = r.LineOfSight(lease, f.BeforeJamb, f.AfterJamb);
        Assert.Equal(MapPhysicalCertainty.Blocked, blocked.Certainty);
        Assert.Equal("doorway", blocked.BlockingOwner);
        Assert.Equal(MapPhysicalCertainty.Clear, r.LineOfSight(lease, f.BeforeOpening, f.AfterOpening).Certainty);
        Assert.Equal(MapPhysicalCertainty.Unknown, r.LineOfSight(lease, f.BeforeJamb, f.FarOutsideBounds).Certainty);
    }

    [Fact]
    public void CaveFloorCeilingSupportAndPick_AgreeOnBothHeads()
    {
        var f = NativeWorldFixtures.StackedCave();
        using var clientPhysics = new BepuPhysicsWorld();
        using var serverPhysics = new BepuPhysicsWorld();
        using var client = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), clientPhysics);
        using var server = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), serverPhysics);
        using var cl = ((IPhysicsQueryLeaseSource)clientPhysics).AcquireQueryReadLease();
        using var sl = ((IPhysicsQueryLeaseSource)serverPhysics).AcquireQueryReadLease();
        foreach (var point in f.ProbePoints)
        {
            var a = new MapPhysicalRelations(client).LineOfSight(cl, point, f.UpperRoom);
            var b = new MapPhysicalRelations(server).LineOfSight(sl, point, f.UpperRoom);
            Assert.Equal((a.Certainty, a.BlockingOwner, a.BuildHash, a.TerrainWitnessDigest), (b.Certainty, b.BlockingOwner, b.BuildHash, b.TerrainWitnessDigest));
            Assert.Equal(new MapWorldQueries(client.World).Pick(f.PickRayFrom(point))?.PlacementId, new MapWorldQueries(server.World).Pick(f.PickRayFrom(point))?.PlacementId);
        }
    }

    [Fact]
    public void FarRegion_RelationsHoldAfterAWholeMetreRebase()
    {
        var f = NativeWorldFixtures.StackedCaveAt(30_000f, -30_000f);
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new Vector3(30_000f, 0f, -30_000f));
        using var reg = MapPhysicsRegistration.Register(f.Build(), physics);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var r = new MapPhysicalRelations(reg);
        Assert.Equal(MapPhysicalCertainty.Blocked, r.LineOfSight(lease, f.LowerRoom, f.UpperRoom).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.LineOfSight(lease, f.LowerUnderShaft, f.UpperOverShaft).Certainty);
    }
}
```

- [ ] **Step 2: Run red** `wa_test t9-red-loco "$LOCO" "FullyQualifiedName~ContactShellTests"` and `wa_test t9-red "$MP" "FullyQualifiedName~MapPhysicalRelationsTests"`

- [ ] **Step 3: Implement the shell pass-through and relations**

`ContactShell` delegates to `ShellGeometry` without copying any rule. Record this public addition on #438, because that lane owns the body model. Relations require `lease.SourceWorld` to be the registration's world and call `lease.AssertCurrent()`. Points convert from their frame to world double, then subtract `lease.Origin`. Line of sight casts a statics-only ray. A hit nearer than the segment length minus 0.001 m is `Blocked`, a clean miss is `Clear`, and a hit within 0.001 m of the end is `Unknown`. The blocking owner comes from `TryOwner` (a placement id or a terrain chunk id). Clearance tests the `ContactShell` capsule at each feet point with `ComputePenetration`. Penetration deeper than 0.001 m is `Blocked`. A point or segment outside the world's playable bounds is `Unknown`. Results carry `World.BuildHash` and `World.Terrain.Witness.ScopedDigest`. Portals are openings with no faces, so `AuthoredOpen` needs no extra state. Envelopes are never installed, so they never block.

- [ ] **Step 4: Run green** `wa_test t9-green-loco "$LOCO" "FullyQualifiedName~ContactShellTests"` and `wa_test t9-green "$MP" "FullyQualifiedName~MapPhysicalRelationsTests"` (expect 4)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): evaluate physical relations under the read lease`

---

### Task 10: Navigation tile identity

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapNavTiling.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapNavTilingTests.cs`

**Interfaces:**
- Consumes: Task 7 `MapResidencyOwnership.Build` entries (joined to Task 4 statics by `OwnerId` for `Digest`), `Affected`, Task 8 `CreateLegacyMoveContext`, R2 portal and vertical link records from `MapBuiltWorld.Surfaces`, `PhysicsNavBake.Capture(GroundMoveContext, PhysicsNavBakeOptions, NavAreaClassifier)` as the capture fixture.
- Produces:
  - `public sealed record MapNavTileOptions(float SeamMarginMetres, string ProfileIdentity, string ControllerIdentity)`.
  - `public sealed record MapNavTile(MapNavTileCoord Coord, MapBox3 Bounds, MapBox3 CaptureBounds, string GeometryDigest, string CaptureIdentity)`.
  - `public sealed record MapNavSeam(MapNavTileCoord A, MapNavTileCoord B, string Digest)`.
  - `public sealed record MapNavLink(string RecordId, MapNavTileCoord From, MapNavTileCoord To, string Digest)`.
  - `public static class MapNavTiling` with `Partition(MapBuiltWorld world, MapWorldGrids grids, MapNavTileOptions options) -> IReadOnlyList<MapNavTile>`, `Seams(IReadOnlyList<MapNavTile> tiles, MapBuiltWorld world, MapNavTileOptions options) -> IReadOnlyList<MapNavSeam>`, `Links(MapBuiltWorld world, MapWorldGrids grids) -> IReadOnlyList<MapNavLink>`, `AffectedTiles(MapBuiltWorld world, MapWorldGrids grids, MapNativeEditEffects effects) -> IReadOnlyList<MapNavTileCoord>`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapNavTilingTests
{
    static readonly MapWorldGrids Grids = new(64f, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Options = new(2f, "profile-a", "legacy-stepper");

    [Fact]
    public void AffectedTileRebake_PreservesUnaffectedDigests()
    {
        var before = MapNavTiling.Partition(NativeWorldFixtures.BuildTwoTileLegacy(), Grids, Options);
        var movedWorld = NativeWorldFixtures.BuildTwoTileLegacyWithMovedCrate();
        var after = MapNavTiling.Partition(movedWorld, Grids, Options);
        var affected = MapNavTiling.AffectedTiles(movedWorld, Grids, NativeWorldFixtures.MovedCrateEffects());
        Assert.NotEmpty(affected);
        foreach (var tile in before)
            if (affected.Contains(tile.Coord)) Assert.NotEqual(tile.CaptureIdentity, after.Single(t => t.Coord == tile.Coord).CaptureIdentity);
            else Assert.Equal(tile.CaptureIdentity, after.Single(t => t.Coord == tile.Coord).CaptureIdentity);
    }

    [Fact]
    public void LegacySculptEdit_ChangesOnlyItsTiles()
    {
        var before = MapNavTiling.Partition(NativeWorldFixtures.BuildTwoTileLegacy(), Grids, Options);
        var after = MapNavTiling.Partition(NativeWorldFixtures.BuildTwoTileLegacyWithSculptInTileZero(), Grids, Options);
        Assert.NotEqual(before.Single(t => t.Coord == new MapNavTileCoord(0, 0)).CaptureIdentity, after.Single(t => t.Coord == new MapNavTileCoord(0, 0)).CaptureIdentity);
        Assert.Equal(before.Single(t => t.Coord == new MapNavTileCoord(2, 0)).CaptureIdentity, after.Single(t => t.Coord == new MapNavTileCoord(2, 0)).CaptureIdentity);
    }

    [Fact]
    public void TiledNav_SeamsAndLinksAreDeterministic()
    {
        var world = NativeWorldFixtures.BuildTwoTileLegacy();
        var tiles = MapNavTiling.Partition(world, Grids, Options);
        Assert.Equal(MapNavTiling.Seams(tiles, world, Options), MapNavTiling.Seams(MapNavTiling.Partition(world, Grids, Options), world, Options));
        Assert.True(NativeWorldFixtures.SeamColumnsAgree(world, tiles[0], tiles[1]));
        var links = MapNavTiling.Links(NativeWorldFixtures.BuildStackedCaveAcrossTiles(), Grids);
        Assert.Contains(links, l => l.RecordId == NativeWorldFixtures.ShaftLinkRecordId && l.From != l.To);
        Assert.Equal(links, MapNavTiling.Links(NativeWorldFixtures.BuildStackedCaveAcrossTiles(), Grids));
    }

    [Fact]
    public void Partition_CoversTheWorldExactlyOnce_AndIdentityFollowsProfileAndController()
    {
        var world = NativeWorldFixtures.BuildTwoTileLegacy();
        var tiles = MapNavTiling.Partition(world, Grids, Options);
        Assert.Equal(tiles.Count, tiles.Select(t => t.Coord).Distinct().Count());
        Assert.True(NativeWorldFixtures.BoundsUnionEquals(tiles.Select(t => t.Bounds), world.Bounds, Grids));
        Assert.NotEqual(tiles[0].CaptureIdentity, MapNavTiling.Partition(world, Grids, Options with { ProfileIdentity = "profile-b" })[0].CaptureIdentity);
        Assert.NotEqual(tiles[0].CaptureIdentity, MapNavTiling.Partition(world, Grids, Options with { ControllerIdentity = "contact-controller" })[0].CaptureIdentity);
    }
}
```

`BuildTwoTileLegacy()` spans nav tiles (0, 0) to (2, 0) with a crate in tile (0, 0). `SeamColumnsAgree` captures each tile with `PhysicsNavBake.Capture` over its `CaptureBounds` through `CreateLegacyMoveContext`, and asserts identical heights and traversal for every column within the seam margin. `BuildStackedCaveAcrossTiles()` places the shaft's vertical link so its lower and upper apertures fall in different nav tiles.

- [ ] **Step 2: Run red** `wa_test t10-red "$MP" "FullyQualifiedName~MapNavTilingTests"`

- [ ] **Step 3: Implement tiling**

Partition the world bounds into nav tiles. `CaptureBounds` is the tile expanded by `SeamMarginMetres`. `GeometryDigest` covers, in ordinal owner order, the digests of every static whose residency bounds intersect `CaptureBounds`, plus for resolver-1 worlds the terrain block identity and the digests of the sculpt tiles intersecting `CaptureBounds`. `CaptureIdentity` is SHA-256 over the geometry digest, profile, controller, seam margin and tile coordinate. Seam digests cover both neighbours' geometry digests and the shared edge. `Links` yields one link per R2 portal or vertical link record whose aperture geometry touches two nav tiles, with a digest over the record id, its semantic digest and both tiles, in ordinal record order. `AffectedTiles` widens Task 7's affected set by the seam margin. Vertical layers on native worlds wait for #438 phase 5, which the README records.

- [ ] **Step 4: Run green** `wa_test t10-green "$MP" "FullyQualifiedName~MapNavTilingTests"` (expect 4)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): identify navigation tiles, seams, links and invalidation`

---

### Task 11: Native asset scale and collider edits

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/NativeMapAssetLoader.cs`, `KhaozEngine.MapEdit.Tool/NativeCollisionService.cs`, `KhaozEngine.MapEdit.Tool/MapAssetFileWriter.cs`
- Modify: `KhaozEngine.MapEdit.Tool/KhaozEngine.MapEdit.Tool.csproj` (reference `KhaozEngine.MapDoc.Physics`), `KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs` (non-positional `NativeAssets`), `KhaozEngine.MapEditor/NativeDocumentSnapshot.cs` (`PublishWriteSet` publishes the native asset roots when `NativeAssets` is set), `KhaozEngine.MapEditor/NativeDocumentTransaction.cs` if its preparation needs the flag, `KhaozEngine.MapDoc.Physics/README.md`, `KhaozEngine.MapEdit.Tool/README.md`, `docs/USING-KHAOZENGINE.md`
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativeAssetScaleTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeCollisionServiceTests.cs`

**Interfaces:**
- Consumes: `PropLoader.LoadProp(AssetEntry, PropValidation?) -> GltfMesh` (Render3D) as the reference for mesh reading, `MapResolvedAsset`, Task 4 `MapWorldBuilder`, `MapNativeEditEffects`, `IMapAssetSource`, `PropCollisionFormat.Write(PhysicsShape, Stream)`.
- Produces:
  - `public static class NativeMapAssetLoader { public static GltfMesh Load(MapResolvedAsset asset, MapAssetClosure closure) }`, converting source units once and never renormalizing height.
  - `public sealed record NativeCollisionMeasurement(string PlacementId, string AssetId, float RawMeshMaxY, float EffectiveBottom, float EffectiveTop, string ColliderSha256)`.
  - `public sealed record NativeCollisionEditResult(bool Applied, string BeforeSha256, string AfterSha256, IReadOnlyList<string> AffectedPlacementIds, MapNativeEditEffects Effects)`.
  - `public sealed class NativeCollisionService` with `Measure(string placementId) -> NativeCollisionMeasurement` and `SetHeights(string assetId, float bottom, float top, bool dryRun = true) -> NativeCollisionEditResult`.
  - `public sealed class MapAssetFileWriter(string assetRoot)` with `WriteResource(byte[] bytes, string extension) -> MapAssetRef` (content-addressed, never overwriting) and `WriteManifest(MapAssetManifestDoc manifest, string id) -> MapAssetRef`.
  - `MapNativeWriteSet` gains a non-positional `public bool NativeAssets { get; init; }`, so the released record keeps its constructor.
  - Test helpers: `NativeCollisionToolFixture : IDisposable` with `Service`, `ReadAssetDirectoryDigest() -> byte[]` and `Reopen() -> NativeCollisionToolFixture` over a temporary asset directory holding `wall-variant` (a compound of boxes, placed as `wall-1`) and `rock-mesh` (a triangle mesh). `NativeEditorAssetFixtures.ScaledMeshAsset(...)` and `MaxVertexY(GltfMesh)`.

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
        using var reopened = f.Reopen();
        var m = reopened.Service.Measure("wall-1");
        Assert.Equal(edit.AfterSha256, m.ColliderSha256);
        Assert.Equal(2.3f, m.EffectiveTop - m.EffectiveBottom, 4);
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
        var f = NativeEditorAssetFixtures.ScaledMeshAsset(sourceUnitsToMetres: 0.01f, rawMaxY: 250f);
        Assert.Equal(2.5f, NativeEditorAssetFixtures.MaxVertexY(NativeMapAssetLoader.Load(f.Asset, f.Closure)), 4);
    }
}
```

- [ ] **Step 2: Run red** `wa_test t11-red "$EDITOR" "FullyQualifiedName~NativeCollisionServiceTests|FullyQualifiedName~NativeAssetScaleTests"`

- [ ] **Step 3: Implement the loader, service and writer**

The loader reads the verified mesh resource and applies `SourceUnitsToMetres` once. Height edits apply only to colliders that are boxes or compounds of boxes. They scale vertical extents and positions to the requested asset-local bottom and top, and refuse other shapes with "compound boxes". A dry run computes the new collider bytes and digest and touches no file. Apply writes the new collider resource and a new manifest through `MapAssetFileWriter`, then swaps the document's root reference in one native transaction whose write set has `NativeAssets` set, published by `NativeDocumentSnapshot.PublishWriteSet`, and returns that transaction's effects with `Physics`, `Nav` and `Residency` invalidation. The mesh resource and placement transforms never change.

- [ ] **Step 4: Run green** `wa_test t11-green "$EDITOR" "FullyQualifiedName~NativeCollisionServiceTests|FullyQualifiedName~NativeAssetScaleTests"` (expect 4) and `wa_test t11-regress-editor "$EDITOR" "FullyQualifiedName~NativeDocument|FullyQualifiedName~NativePlacement"`

- [ ] **Step 5: Docs sweep, format, guards and commit** `feat(mapedit): measure and edit native colliders`

The docs sweep covers the MapDoc.Physics README (every produced API, the ownership boundary, the feature-query diagnostics, the deferred-work table), `docs/USING-KHAOZENGINE.md` (a MapDoc.Physics section), the MapEdit.Tool README and `git grep` for every new type name across Markdown.

---

## Review, release preparation, candidate gates and integration

**Per task.** The controller checks the worker's report against the commit, the diff and the red and green logs, then dispatches a fresh reviewer on that range. Findings return to an implementer with a scoped re-review. A load-bearing unresolved finding blocks the next task.

**1. Whole-branch review** over the full range, covering Global Constraints, every cross-task interface, OA22's decisions, the Review Focus tests and the deferred-work table.

**2. Release preparation.** Re-read `main`, `Directory.Build.props`, every tag and any staged version. Ride a staged unreleased version or take the next free minor after 20.30.0. One commit `release(<version>): shared shapes and headless world` changes the version knob, adds the `CHANGELOG.md` entry and updates every declaration `scripts/check-doc-versions.sh` guards. Consumer notes state the new opt-in package, that ground support stays with #438, the feature-query diagnostics, the explicit `Surface` resource refusal until R5, the public `ContactShell` and that no MapDoc format or identity token changed.

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
| 1 to 11 | | | | | | |
| Whole-branch review | | | | | | |
| Candidate verification | | | | | | |
