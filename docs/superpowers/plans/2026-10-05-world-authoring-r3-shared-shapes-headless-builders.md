# World Authoring R3: Shared Collision, Picking, Reach and Headless Builders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build one GPU-free resolved static world whose exact authored shapes drive physics, selection, reach, stance and nav capture in both heads.

**Architecture:** MapDoc owns versioned immutable asset data, while the new MapDoc.Physics package compiles shared shapes and complete static descriptors. Existing physics backends register those descriptors and expose a movement view excluding only terrain statics. Narrow-phase queries consume the same oriented compound or baked geometry, with AABBs used solely for acceleration.

**Tech Stack:** C# on the repository's existing .NET target, System.Numerics, System.Text.Json, closed JSON Schema, xUnit, existing engine seams. No new third-party dependency.

**Spec:** `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md`, approved direction at `49b045f75`, with inline clarifications on allocation undo and water boundary ownership. Read C3, T1 to T9 and the evidence register before implementation.

## Global Constraints

| Ruling | Decision |
| --- | --- |
| OA1 | One tool, MapEditor GUI plus ke-mapedit MCP, authors terrain, props and buildings. No two-format hybrid |
| OA2 | Props and buildings accept free position, yaw and positive uniform scale as an engine-wide capability |
| OA3 | Grimhollow leaves TileWorld completely through the full native MapDoc swap, option A |

Names below identify proposed API responsibilities, not existing public types. Core MapDoc and its runtime packages acquire no TileWorld or Grimhollow dependency. An optional offline importer is the only new package allowed to read TileWorld. Existing analytic MapDoc consumers and TileWorld consumers retain their current behavior [E1, E2, E6, E26, E31].

Formats advance at the rounds below: 5 native terrain, 6 water, 7 prefabs/interiors, 8 markers, 9 foliage. Every N to N+1 migration is pure and tested. Numbers describe this baseline-relative schema sequence, not reserved engine releases. If concurrent MapDoc work takes a number, rebase the sequence onto the next free format, preserving these semantic transitions. Future-format input refuses rather than dropping unknown fields. All newly structured payloads use closed schemas and explicit payload versions [E2, E26, E43].

Each round below is one separately reviewed implementation plan and one independently usable engine minor capability release. Contract skeletons may be additive, but no round may claim a later round's complete authoring workflow. All releases reconcile with the pivot's current main, take the next available minor and wait for the owner's tag. Engine rounds may run before game 0.11.0 without changing pivot pins. No numeric engine releases are reserved [E30, E31, G1, G14, G15].

Engine exit tests run synchronously in the owning plan's area projects. Rendering follows repository backend CI/golden policy. Build/verification is serialized through the shared slot, no stress loops or parallel local builds. This documents-only revision runs document guards, not production builds or full suites [E29, E31, G15].

The following binding lines are copied from `AGENTS.md`.

- In shared worktrees, stage and commit explicit paths. Never use the shared stash for coordination.
- Existing unrelated changes belong to their owner. Do not revert or absorb them.
- New behavior gets a headless test in the matching area test project. Test projects reference only
  the engine projects they use and keep namespaces under `KhaozEngine.Tests.*`.
- Tests that mutate process-global state use a collection definition with
  `DisableParallelization = true`.
- Third-party libraries sit behind dependency-free engine seams with opt-in backends. Read
  `docs/DEPENDENCY-SEAMS.md` before changing an edge.
- KESIZE is a structure ratchet. Put new behavior in a new type. Do not split at an arbitrary line.
  Baseline growth and exemptions require owner approval. Ratchet down freely with
  `scripts/check-file-size.sh --update`.
- Warnings are errors. Fix them at the source. Do not add blanket suppressions or disable the rule.
- CI tests Release. Validate any Debug-only behavior in Release with a configuration-independent
  observable.
- No em-dash or en-dash glyphs in shipped prose or comments. No prose semicolons in Markdown. Run the
  whole-tree guards before completion.

`AGENTS.md` routes implementation to `docs/CONTRIBUTOR-RULES.md`, including dependency seams, documentation sweep, package catalog and release rules. Player-facing GUI text uses `StringId` or `LocalizedText`, raw device input stays in `AppWindow`, and no consumer clients are launched for engine tooling work. Documentation alone does not bump the engine version. Package work rides the next available engine minor after concurrent pivot releases, re-reading main and tags at execution, with no reserved numeric engine versions. Schema numbers 4/5/6 are also baseline-relative and rebase together if occupied. Only the owner tags this program. The owning orchestrator handles integration and packing from pushed main. A delegated implementer stops at its verified commit and never merges, packs the shared feed or tags.

The approved execution method is subagent-driven-development. Execute serially after spec and plan approval. Start by checking the assigned absolute worktree and branch, then preserve unrelated changes. Line ranges below refer to `49b045f758f1110f74516b1447ffccda42d9dd95`. Re-locate symbols after prerequisite rounds, never edit by obsolete line number. Existing signatures are source-checked at that SHA. Signatures marked **new** are planned contracts, not claims that they already exist. No production code or tests run while authoring this plan.

## Review Focus

- A compound doorway must remain open to rays, capsule clearance and stance after non-quarter yaw and scaling (Tasks 2 to 4).
- A lower tree interaction band must constrain eligible hits without replacing its physical collider (Task 3).
- A large placement crossing negative-coordinate storage seams must be resident in every intersected chunk, but built once (Task 5).
- Physics registration failure or a rebased world must not leak handles or apply world origin twice (Task 6).
- A missing baked collider, unsupported payload or nonfinite/free-scale transform must refuse both heads before partial build (Tasks 1 and 2).

---

## File Structure

| Path | Responsibility |
| --- | --- |
| `KhaozEngine.MapDoc/Assets/MapCollisionDoc.cs`, `KhaozEngine.MapDoc/Assets/MapSupportDoc.cs` | Payload-1 collision/selection and support asset contracts |
| `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs`, `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs`, `KhaozEngine.MapDoc.Physics/MapWorldBuildOptions.cs` | Complete GPU-free immutable build and deterministic identity |
| `KhaozEngine.MapDoc.Physics/MapPlacementShapes.cs`, `KhaozEngine.MapDoc.Physics/MapShapeGeometry.cs` | One transformed shape source and narrow-phase geometry |
| `KhaozEngine.MapDoc.Physics/MapWorldQueries.cs`, `KhaozEngine.MapDoc.Physics/MapStanceCandidates.cs` | Pick, LOS, distance and filtered walk-up candidates |
| `KhaozEngine.MapDoc.Physics/MapResidencyOwnership.cs`, `KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs` | Chunk membership and complete/movement static split |
| `KhaozEngine.Terrain.Render3D/NativeMapAssetLoader.cs` | Explicit preserve-source-scale render loading |
| `KhaozEngine.MapEdit.Tool/NativeCollisionService.cs` | Collision diagnostics and measurement dry run |
| `KhaozEngine.MapDoc.Physics.Tests/` | New headless test project with MapDoc.Physics and opt-in Bepu only |
| `KhaozEngine.MapEditor.Tests/MapDoc/NativeCollision*.cs` | Tool descriptor writes and editor/service effects |


## Source-Checked Contract and Judgement Calls

Existing `ReachTarget.Box(Vector3 centre, Vector3 halfExtents, float yawRadians = 0f)` already supports oriented boxes, and `ReachGeometry.Distance(in MovementBody body, in ReachTarget target) -> float` / `Within(in MovementBody body, in ReachTarget target, float range, float tolerance = 0f) -> bool` have no hidden epsilon. Reuse them for box members. Do not rewrite working single-box support. General compound/baked queries belong to the shared native shape service.

Existing `IPhysicsWorld.AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) -> StaticHandle`, `RemoveStatic(StaticHandle handle)`, `CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> excludedStatics) -> IPhysicsWorldQueryView` and `Origin` are the backend seams (`IPhysicsWorld.cs:14-104`). `PropCollisionFormat.Read(Stream stream) -> PhysicsShape` and `Version=1` parse baked data render-free. `PhysicsNavBake.Capture(GroundMoveContext context, PhysicsNavBakeOptions options, NavAreaClassifier classify) -> PhysicsNavBake` needs complete physics, never only a slope query.

Use `MapDoc.Physics` as a new optional package, not a render-side ChunkStatics export. Solid intent becomes explicit native asset metadata in this round, retaining Kind as a game-facing key. Collision variants are digest-bearing assets supplied by the offline importer in R11, not runtime footprint recalculation. R3 publishes complete scene descriptors and geometry queries, not the later camera/view stream or backend goldens. Runtime residency ownership is ready here, R8 composes streaming and unload. Interaction bands are caller-supplied absolute world Y ranges, so a game can request exactly the lower 2 m without changing the asset.

### Task 1: Version-1 asset shapes and optional GPU-free package

**Files:**
- Create: `KhaozEngine.MapDoc/Assets/MapCollisionDoc.cs`, `KhaozEngine.MapDoc/Assets/MapSupportDoc.cs`
- Create: `KhaozEngine.MapDoc.Physics/MapShapePayload.cs`
- Create: `KhaozEngine.MapDoc.Physics/KhaozEngine.MapDoc.Physics.csproj`, `KhaozEngine.MapDoc.Physics/README.md`
- Create: `KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj`, `KhaozEngine.MapDoc.Physics.Tests/NativeShapeDocumentTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/NativeShapeFixtures.cs`
- Modify: `KhaozEngine.MapDoc/Assets/MapAssetManifest.cs`, `KhaozEngine.MapDoc/Assets/MapAssetClosure.cs` from R1
- Modify: `KhaozEngine.slnx:1-147`, `README.md:62-87`, `docs/DEPENDENCY-SEAMS.md:114-139` at the MapDoc/physics package rows
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeShapeDocumentTests.cs`

**Interfaces:**
- Consumes R1: MapAssetDoc/MapResolvedAsset/resource closure, source units and bounds.
- Produces new: asset `bool IsSolid`, payload `MapCollisionDoc` with `int PayloadVersion=1`, `MapCollisionKind Kind`, `IReadOnlyList<MapLocalBox> Boxes` and `MapAssetRef? Baked` (exclusive union members by Kind). `MapCollisionKind { None, CompoundBoxes, Baked }`.
- Produces new: `MapLocalBox(Vector3 Centre, Vector3 HalfExtents, float YawRadians)`, all finite, strictly positive half extents.
- Produces new: `MapSupportDoc(int PayloadVersion, string SurfaceId, IReadOnlyList<MapSurfaceTriangle> Triangles)` from R2.
- Produces new: `MapShapePayload.Read(MapAssetClosure closure, MapResolvedAsset asset, bool selection) -> MapCollisionDoc`, validating supported payload and baked format/digest.
- Test helper new: `NativeShapeFixtures.Valid() -> (MapAssetClosure Assets, string AssetId)`, `LoadMissingBaked() -> void`, `LoadFutureCollision() -> void`, `LoadSolidWithoutShape() -> void`, each attempting invalid closure/shape load inside the assertion. Additional builders `Doorway()`, `CornerWall()`, `Bridge()`, `Tree()`, `CrossChunkBuilding()` return `(MapDocument Document, MapAssetClosure Assets, MapWorldBuildOptions Options)` once Task 2 supplies that type.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeShape_SolidDataCannotBeMissingOrUnsupported()
{
    var valid = NativeShapeFixtures.Valid();
    Assert.Equal(1, MapShapePayload.Read(valid.Assets, valid.Assets.GetAsset(valid.AssetId), false).PayloadVersion);
    foreach (Action invalid in new Action[] { NativeShapeFixtures.LoadMissingBaked,
        NativeShapeFixtures.LoadFutureCollision, NativeShapeFixtures.LoadSolidWithoutShape })
        Assert.Throws<MapDocumentException>(invalid);
    Assert.DoesNotContain(typeof(MapShapePayload).Assembly.GetReferencedAssemblies(),
        a => a.Name!.Contains("Render3D") || a.Name.Contains("Gpu") || a.Name.Contains("TileWorld"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t1 /tmp/grand-world/wa-r3-t1.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeShapeDocumentTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the payload DTOs in MapDoc and `MapShapePayload.Read(...)` in new `KhaozEngine.MapDoc.Physics/MapShapePayload.cs`. Default old descriptors IsSolid=false for compatibility, require collision data and a non-None shape for explicit solids. Selection may independently use the same schema on non-solids. Reject empty compounds, nonfinite or nonpositive dimensions, unsupported kinds/versions, bad indices/hulls and stale .coll payloads. The package references only MapDoc, Physics, Movement and Collision as needed, with no GPU/Render3D/MapEditor/TileWorld edge and no Bepu dependency. Tests reference MapDoc.Physics plus Physics.Bepu, IsPackable=false and `KhaozEngine.Tests.MapDocPhysics` namespaces. Add the package/test projects to the solution and authoritative package/dependency rows. No umbrella inclusion is implicit.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t1 /tmp/grand-world/wa-r3-t1.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeShapeDocumentTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Assets/MapCollisionDoc.cs KhaozEngine.MapDoc/Assets/MapSupportDoc.cs KhaozEngine.MapDoc.Physics/MapShapePayload.cs KhaozEngine.MapDoc.Physics/KhaozEngine.MapDoc.Physics.csproj KhaozEngine.MapDoc.Physics/README.md KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj KhaozEngine.MapDoc.Physics.Tests/NativeShapeDocumentTests.cs KhaozEngine.MapDoc.Physics.Tests/NativeShapeFixtures.cs KhaozEngine.MapDoc/Assets/MapAssetManifest.cs KhaozEngine.MapDoc/Assets/MapAssetClosure.cs KhaozEngine.slnx README.md docs/DEPENDENCY-SEAMS.md
git diff --cached --check
git commit -m "feat(mapdoc): define native asset collision payloads"
```


### Task 2: Complete immutable builder and transformed statics

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapWorldBuildOptions.cs`, `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs`, `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs`, `KhaozEngine.MapDoc.Physics/MapPlacementShapes.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeWorldBuilderTests.cs`

**Interfaces:**
- Consumes R1: MapResolver.Resolve and MapResolveOptions. Consumes R2: CompileAll, MapSurfaceSampler, blocked masks and MapVolumeMembership. Consumes Task 1: shape/support payloads.
- Produces new: `MapWorldBuildOptions(string GroundSurfaceId, MapSurfaceFallback Fallback, string ConsumerPolicyIdentity, Func<MapBlockedMask,bool>? KeepBlockedMask = null, int BuilderVersion = 1)`.
- Produces new: `MapWorldBuilder.Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) -> MapBuiltWorld`.
- Produces new: immutable `MapResolvedShape` with PlacementId, NumericId, Digest, Bounds, SelectionOnly and `CreatePhysicsShape() -> PhysicsShape`, `Pose WorldPose`. Internal geometry owns defensive copies.
- Produces new: `MapPlacementShapes.Resolve(MapResolvedDocument document) -> IReadOnlyList<MapResolvedShape>`.
- Produces new: `MapStaticDescriptor(string OwnerId, bool IsTerrain, MapResolvedShape Shape)` and immutable `MapBuiltWorld` properties Document, Surfaces, Shapes, SupportSurfaces, BlockedMasks, Volumes, Sampler, StaticDescriptors, AuthoredHash, Bounds. R4 adds water properties without replacing these names.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeWorld_TwoIndependentHeadsHaveIdenticalTransformedStatics()
{
    var f = NativeShapeFixtures.Doorway();
    var p = f.Document.Placements[0];
    p.X = 0.23f; p.Z = 0.17f; p.Yaw = 0.371f; p.Scale = 1.137f;
    var client = MapWorldBuilder.Build(f.Document, f.Assets, f.Options);
    var server = MapWorldBuilder.Build(f.Document, f.Assets, f.Options);
    Assert.Equal(client.AuthoredHash, server.AuthoredHash);
    Assert.Equal(client.Shapes.Select(s => s.Digest), server.Shapes.Select(s => s.Digest));
    Assert.Equal(client.Shapes.Select(s => s.WorldPose), server.Shapes.Select(s => s.WorldPose));
    Assert.Equal(client.Surfaces.SelectMany(s => s.Triangles), server.Surfaces.SelectMany(s => s.Triangles));
    p.Scale = float.NaN;
    Assert.Throws<MapDocumentException>(() => MapWorldBuilder.Build(f.Document, f.Assets, f.Options));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t2 /tmp/grand-world/wa-r3-t2.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWorldBuilderTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced signatures in their named files. Validate complete closure/doc before publishing any result. Resolve null-Y placements via the canonical sampler, retain imported explicit Y, and compose local shape pose, asset SourceUnitsToMetres and placement Scale exactly once. Keep support/deck triangles and selection geometry separate from solid static ownership while sharing transforms. Bounds union shapes, verified mesh bounds, support, lights and LOD extents. Compile terrain static descriptors from R2 canonical triangles, skip non-capture fallback and keep blocked masks explicit. Hash actual builder version/options and a stable consumer policy identity, rejecting empty identity when a callback is supplied. No backend/device allocation occurs. Add scale 0/-1/infinity, baked stale data, slope-seated wall variants, two-edge corner walls, support decks, unchanged Kind and defensive-shape-copy tests.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t2 /tmp/grand-world/wa-r3-t2.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWorldBuilderTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Physics/MapWorldBuildOptions.cs KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs KhaozEngine.MapDoc.Physics/MapPlacementShapes.cs KhaozEngine.MapDoc.Physics.Tests/NativeWorldBuilderTests.cs
git diff --cached --check
git commit -m "feat(mapdocphysics): build shared authored world descriptors"
```


### Task 3: Shape-authoritative picking, reach, LOS and interaction bands

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapShapeGeometry.cs`, `KhaozEngine.MapDoc.Physics/MapWorldQueries.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeShapeQueryTests.cs`

**Interfaces:**
- Consumes existing: MovementBody(Vector3 centre,float radius,float halfHeight), ReachTarget.Box and ReachGeometry.Distance/Within. Consumes Task 2 shapes.
- Produces new: `MapPickRay(Vector3 Origin,Vector3 Direction,float MaxDistance)`, `MapInteractionBand(float MinY,float MaxY)`, `MapPickHit(string PlacementId,long? NumericId,float Distance,Vector3 Point,Vector3 Normal)`.
- Produces new: `MapShapeGeometry.Distance(Vector3 point,MapResolvedShape shape) -> float`, `Distance(in MovementBody body,MapResolvedShape shape,MapInteractionBand? band = null) -> float`, `Raycast(MapPickRay ray,MapResolvedShape shape,MapInteractionBand? band,out MapPickHit hit) -> bool`.
- Produces new: `MapWorldQueries(MapBuiltWorld world)`, `Pick(MapPickRay ray,MapInteractionBand? band = null) -> MapPickHit?`, `Distance(in MovementBody body,string placementId,MapInteractionBand? band = null) -> float`, `Within(in MovementBody body,string placementId,float range,float tolerance = 0,MapInteractionBand? band = null) -> bool`, `HasLineOfSight(Vector3 from,Vector3 to,string? ignoredPlacementId = null) -> bool`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeDoorwayRayAndReachUseCompoundMembers_NotTheirAabb()
{
    var f = NativeShapeFixtures.Doorway();
    f.Document.Placements[0].X=0.23f; f.Document.Placements[0].Z=0.17f;
    f.Document.Placements[0].Yaw=0.371f; f.Document.Placements[0].Scale=1.137f;
    var world = MapWorldBuilder.Build(f.Document, f.Assets, f.Options);
    var q = new MapWorldQueries(world);
    var transform = world.Document.Placements[0].Transform;
    Vector3 direction = Vector3.Transform(Vector3.UnitZ,Quaternion.CreateFromAxisAngle(Vector3.UnitY,0.371f));
    Assert.Null(q.Pick(new MapPickRay(transform.TransformPoint(new Vector3(0,1,-2)),direction,4.548f)));
    var body = new MovementBody(transform.TransformPoint(new Vector3(0,1,0)), 0.2f, 0.8f);
    Assert.True(q.Distance(body, "doorway") > 0);
    Assert.False(q.Within(body, "doorway", 0));
    var tree = NativeShapeFixtures.Tree();
    var tq = new MapWorldQueries(MapWorldBuilder.Build(tree.Document,tree.Assets,tree.Options));
    Assert.Null(tq.Pick(new MapPickRay(new Vector3(0,3,-2),Vector3.UnitZ,4),new MapInteractionBand(0,2)));
    Assert.NotNull(tq.Pick(new MapPickRay(new Vector3(0,1,-2),Vector3.UnitZ,4),new MapInteractionBand(0,2)));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t3 /tmp/grand-world/wa-r3-t3.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeShapeQueryTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced geometry/query signatures in their new files. Box members use existing exact yawed-box reach. Compound distance is the minimum to actual members, never their bounding AABB. Baked hull/mesh members use point/segment-to-triangle distances and ray intersections on the same immutable geometry used by CreatePhysicsShape. Baked cylinder members use exact cylinder distance/ray-cap math, not a tessellated proxy. Recursively apply baked compound local poses and reject unsupported geometry before building. Capsule distance measures the capsule's medial segment then subtracts radius, returning zero on overlap. Validate rays, finite nonnegative ranges/tolerances and nonempty bands, clipping eligible geometry to [MinY,MaxY] for band queries while physical collision remains unchanged. LOS ignores selection-only shapes. Pick combines solid and explicit selection shapes, with nearest distance then ordinal PlacementId tie breaking. Add rotated doorway hole, baked concavity, non-solid Examine selection, self-ignored LOS, equal-distance pick ordering and epsilon-free reach boundary tests.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t3 /tmp/grand-world/wa-r3-t3.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeShapeQueryTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Physics/MapShapeGeometry.cs KhaozEngine.MapDoc.Physics/MapWorldQueries.cs KhaozEngine.MapDoc.Physics.Tests/NativeShapeQueryTests.cs
git diff --cached --check
git commit -m "feat(mapdocphysics): query the shared placement geometry"
```


### Task 4: Deterministic shape-based walk-up stance

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapStanceCandidates.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeStanceTests.cs`

**Interfaces:**
- Consumes Task 3 MapWorldQueries.Distance/Within and shape geometry, Task 2 canonical support.
- Produces new: `MapStanceOptions(float Radius,float HalfHeight,float Range,float Tolerance,float Spacing,float MaxStepHeight,MapInteractionBand? Band = null)`.
- Produces new: `MapStanceCandidates.Find(MapBuiltWorld world,string placementId,Vector3 actorFeet,MapStanceOptions options,Func<MovementBody,bool> hasClearance) -> IReadOnlyList<Vector3>` returning absolute feet coordinates, ordered by actor distance then X/Z/Y.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeStances_AreReachableAndClearBesideRotatedCompound()
{
    var f = NativeShapeFixtures.Doorway();
    f.Document.Placements[0].Yaw = 0.371f;
    f.Document.Placements[0].Scale = 1.137f;
    var world = MapWorldBuilder.Build(f.Document,f.Assets,f.Options);
    var q = new MapWorldQueries(world);
    var options = new MapStanceOptions(0.2f,0.8f,0.4f,0,0.25f,0.3f);
    var feet = new Vector3(0,0,-2);
    var a = MapStanceCandidates.Find(world,"doorway",feet,options,b => q.Distance(b,"doorway") > 0);
    var b = MapStanceCandidates.Find(world,"doorway",feet,options,b => q.Distance(b,"doorway") > 0);
    Assert.NotEmpty(a);
    Assert.Equal(a,b);
    Assert.All(a,p => Assert.True(q.Within(new MovementBody(p+Vector3.UnitY*0.8f,0.2f,0.8f),"doorway",0.4f)));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t4 /tmp/grand-world/wa-r3-t4.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeStanceTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `Find(...)` in `MapStanceCandidates.cs`. Generate candidates from actual box/mesh boundary edges at explicit Spacing, include aperture-side edges, sample canonical terrain or placement supports, and reject points outside playable bounds, beyond MaxStepHeight, without supplied complete clearance, or outside exact shape reach. Do not introduce a circular proxy, global grid alignment or whole-building box. Stable geometric traversal and sorting make repeat calls/head results identical. Range and tolerance remain caller-owned. Add compound aperture usable stance, sloped ground, bridge support, zero-result case, invalid option, lower-band and narrow-door capsule-clearance assertions. This returns local candidates, not a game route or interaction policy.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t4 /tmp/grand-world/wa-r3-t4.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeStanceTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Physics/MapStanceCandidates.cs KhaozEngine.MapDoc.Physics.Tests/NativeStanceTests.cs
git diff --cached --check
git commit -m "feat(mapdocphysics): derive walk-up candidates from shapes"
```


### Task 5: Complete statics and cross-chunk residency ownership

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapResidencyOwnership.cs`
- Modify: `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs`, `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs` from Task 2
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeResidencyOwnershipTests.cs`

**Interfaces:**
- Consumes existing: `MapTileGrid.CoordOf(float worldX,float worldZ,float tileSize) -> MapTileCoord`, MapTileCoord and storage TileSize from MapDoc.
- Produces new: `MapResidencyEntry(string OwnerId,MapTileCoord StorageOwner,IReadOnlyList<MapTileCoord> Membership,MapLocalBounds WorldBounds)`.
- Produces new: `MapResidencyOwnership.Build(MapBuiltWorld world,float tileSize) -> IReadOnlyList<MapResidencyEntry>` and `InWindow(IReadOnlyList<MapResidencyEntry> entries,MapTileRect window) -> IReadOnlyList<string>`.
- Produces new: `MapBuiltWorld.Residency` with one entry per resolved owner. R8 consumes this membership without rebucketing by anchor.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeLargePlacement_IntersectsEveryChunkAndBuildsOnce()
{
    var f = NativeShapeFixtures.CrossChunkBuilding();
    f.Document.TileSize = 64;
    var world = MapWorldBuilder.Build(f.Document,f.Assets,f.Options);
    var entry = Assert.Single(world.Residency.Where(e => e.OwnerId == "large-building"));
    Assert.Contains(new MapTileCoord(-1,-1),entry.Membership);
    Assert.Contains(new MapTileCoord(0,0),entry.Membership);
    Assert.Equal(entry.Membership.Count,entry.Membership.Distinct().Count());
    Assert.Single(world.Shapes.Where(s => s.PlacementId == "large-building"));
    Assert.Equal(world.AuthoredHash,MapWorldBuilder.Build(f.Document,f.Assets,f.Options).AuthoredHash);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t5 /tmp/grand-world/wa-r3-t5.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeResidencyOwnershipTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `Build(...)` and `InWindow(...)` in `MapResidencyOwnership.cs`. A stable owner ID is stored once, membership lists every storage chunk intersecting effective shape/mesh/support/light/LOD union bounds, including negative coordinates and exact edges. Use minimum-inclusive maximum-exclusive chunk membership, with degenerate bounds explicitly assigned by anchor. Broad bounds do not replace narrow-phase query geometry. InWindow returns sorted distinct owners whose membership overlaps the window. Server StaticDescriptors always remain complete and do not take a window/camera parameter. Add a transformed spanning wall and tree light/LOD extents, exact-seam no-overcount, union-of-windows equals complete ownership, and partial MapDocument refusal. Actual streamed loading, unload and draw deduplication are R8 gates.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t5 /tmp/grand-world/wa-r3-t5.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeResidencyOwnershipTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Physics/MapResidencyOwnership.cs KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs KhaozEngine.MapDoc.Physics.Tests/NativeResidencyOwnershipTests.cs
git diff --cached --check
git commit -m "feat(mapdocphysics): expose complete residency ownership"
```


### Task 6: Physics registration, ground split and nav capture

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativePhysicsRegistrationTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/NativeNavigationCaptureTests.cs`

**Interfaces:**
- Consumes existing: IPhysicsWorld AddStatic/RemoveStatic/query-view/Origin, `Pose(Vector3 Position,Quaternion Orientation)`, GroundMoveContext and PhysicsNavBake.Capture.
- Produces new: `MapPhysicsRegistration.Register(MapBuiltWorld built,IPhysicsWorld physics) -> MapPhysicsRegistration`, implementing IDisposable with GroundHandles, AllHandles, `IPhysicsWorldQueryView MovementQueries`, `GroundMoveContext CreateMoveContext()`.
- CreateMoveContext uses the existing six-argument `GroundMoveContext(Func<float,float,float> groundHeight,Func<float,float,Vector3>? groundNormal,IPhysicsWorld? physics,Func<float,float,Vector2>? clampXz,Func<float,float,float,MovementMedium>? medium,IPhysicsWorldQueryView? movementQueries)`.
- Test helper new: `NativeNavigationFixture.Create() -> (MapBuiltWorld World, PhysicsNavBakeOptions Options, NavAreaClassifier Classify, MoveTuning Tuning)`, covering a deck, rail, doorway and free transformed blocker. `NativeRegistrationFaultWorld` wraps a physics world and throws on its second AddStatic, exposing live handle count.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeRegistration_CompleteCaptureAndMovementUseOneWorld()
{
    var f = NativeNavigationFixture.Create();
    using var physics = new BepuPhysicsWorld();
    physics.Rebase(new Vector3(64,0,-64));
    using var registration = MapPhysicsRegistration.Register(f.World,physics);
    var context = registration.CreateMoveContext();
    Assert.Same(physics,context.Physics);
    Assert.Same(physics,registration.MovementQueries.SourceWorld);
    Assert.NotEmpty(registration.GroundHandles);
    using var capture = PhysicsNavBake.Capture(context,f.Options,f.Classify);
    var navigation = capture.BuildProfile(f.Tuning,new NavAreaFilter(0,0));
    Assert.True(navigation.AllowsSegment(new Vector3(0,0,-0.5f),new Vector3(0,0,0.5f)));
    Assert.False(navigation.AllowsSegment(new Vector3(1,0,-0.5f),new Vector3(1,0,0.5f)));
    Assert.Equal(f.World.Sampler.HeightAt(0,0),context.GroundHeight(0,0));
    var fault = new NativeRegistrationFaultWorld();
    Assert.Throws<InvalidOperationException>(() => MapPhysicsRegistration.Register(f.World,fault));
    Assert.Equal(0,fault.LiveStaticCount);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t6 /tmp/grand-world/wa-r3-t6.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativePhysicsRegistrationTests|FullyQualifiedName~NativeNavigationCaptureTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `Register(...)`, CreateMoveContext and Dispose in `MapPhysicsRegistration.cs`. Register world-space poses relative to `physics.Origin`, subtracting it exactly once. Complete physics contains all terrain/support/solid descriptors. Exclude only IsTerrain handles from the movement query view, never bridge decks or rails. Supply canonical sampler height/normal and a playable-bounds clamp. Until R4 the native context medium delegate is null, preserving explicit absence. Remove handles in reverse order on failure/disposal, never dispose the caller-owned physics world and refuse unsupported query-view backends. Add backend downward ray comparisons <=0.00001 m to canonical samples, retained deck/rail clearance, rebased-origin equivalence, disposal count and `PhysicsNavBake` profiles proving doorway opening and transformed obstacle blocking. Use existing `PhysicsNavBake.BuildProfile(in MoveTuning tuning,NavAreaFilter areas) -> GroundNavigation` and `GroundNavigation.AllowsSegment(Vector3 fromFeet,Vector3 toFeet) -> bool`. Set tuning MaxSlopeRadians exactly equal to capture Options.MaxSlopeRadians. The fixture pins aperture center x=0, solid jamb x=1, z=-0.5 to 0.5 and a deck segment y=0.4. Assert aperture traversal true, jamb traversal false and deck traversal true.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t6 /tmp/grand-world/wa-r3-t6.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativePhysicsRegistrationTests|FullyQualifiedName~NativeNavigationCaptureTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs KhaozEngine.MapDoc.Physics.Tests/NativePhysicsRegistrationTests.cs KhaozEngine.MapDoc.Physics.Tests/NativeNavigationCaptureTests.cs
git diff --cached --check
git commit -m "feat(mapdocphysics): register shared static and nav worlds"
```


### Task 7: Preserve mesh scale and expose measured shape diagnostics

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/NativeMapAssetLoader.cs`
- Create: `KhaozEngine.MapEdit.Tool/NativeCollisionService.cs`
- Modify: `KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj:1-19`, `KhaozEngine.MapEdit.Tool/KhaozEngine.MapEdit.Tool.csproj:1-21` project references
- Modify: `KhaozEngine.MapDoc.Physics/README.md`, `docs/USING-KHAOZENGINE.md:56-81`, `KhaozEngine.MapEdit.Tool/README.md:3-28`
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativeCollisionMeasurementTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeAssetScaleTests.cs`

**Interfaces:**
- Consumes existing: `PropLoader.LoadProp(AssetEntry entry, PropValidation? validation = null) -> GltfMesh` and auto/LOD loaders normalize HeightMeters. New native loader bypasses that normalization, old loaders remain unchanged.
- Consumes R1 adapter, Task 2 shared shape/bounds and closure, R2 MapNativeEditResult.
- Produces new: `NativeMapAssetLoader.Load(MapResolvedAsset asset,string resourceRoot) -> IReadOnlyList<GltfMeshPart>` with explicit source-unit conversion and corresponding `LoadLod(...)`.
- Produces new: `NativeCollisionMeasurement(string AssetId,float RawMeshMaxY,float EffectiveBottom,float EffectiveTop,string ColliderDigest)`.
- Produces new: `NativeCollisionService.Measure(MapBuiltWorld world,string placementId) -> NativeCollisionMeasurement`, `SetHeights(string assetId,float bottom,float top,bool dryRun = true) -> NativeCollisionEditResult`.
- Produces new: `NativeCollisionEditResult(bool Applied,string BeforeDigest,string AfterDigest,IReadOnlyList<string> AffectedPlacementIds,MapNativeEditResult Effects)`.
- Test helper new: `NativeCollisionToolFixture : IDisposable` with Service, Document, `byte[] ReadAssetBytes()`, `string MeshDigest` and test collision boxes.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeCollisionHeightDryRun_NeverResizesMeshOrWritesAsset()
{
    using var f = new NativeCollisionToolFixture();
    byte[] before = f.ReadAssetBytes();
    string mesh = f.MeshDigest;
    var edit = f.Service.SetHeights("wall-variant",0.1f,2.4f,dryRun:true);
    Assert.False(edit.Applied);
    Assert.NotEqual(edit.BeforeDigest,edit.AfterDigest);
    Assert.Equal(before,f.ReadAssetBytes());
    Assert.Equal(mesh,f.MeshDigest);
    Assert.Contains("wall-1",edit.AffectedPlacementIds);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t7 /tmp/grand-world/wa-r3-t7.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeCollisionMeasurementTests|FullyQualifiedName~NativeAssetScaleTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced loader and service signatures in their named files. Native mesh/local origin is preserved, source units convert once and HeightMeters never renormalizes native art. Main mesh/parts/LOD/material paths use verified resources. Measure raw mesh maximum Y separately from world transformed physical collision bottom/top. Height writes use asset-local metre bottom/top values and prevalidate a chosen editable compound-box asset, scale vertical extents only, refuse ambiguous baked-shape edits with an explicit diagnostic, and keep mesh digests/transform unchanged. Atomic asset promotion updates references/closure hash and invalidates every affected shape/nav/residency owner. Dry run reports the exact diff and allocated nothing. Add real apply/save/reload, failure preservation, slope variant movement diagnostics and GUI/service shared-build tests. Publish docs and run all round verification before handoff. Backend visual captures remain R8.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r3-t7 /tmp/grand-world/wa-r3-t7.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeCollisionMeasurementTests|FullyQualifiedName~NativeAssetScaleTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.Terrain.Render3D/NativeMapAssetLoader.cs KhaozEngine.MapEdit.Tool/NativeCollisionService.cs KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj KhaozEngine.MapEdit.Tool/KhaozEngine.MapEdit.Tool.csproj KhaozEngine.MapDoc.Physics/README.md docs/USING-KHAOZENGINE.md KhaozEngine.MapEdit.Tool/README.md KhaozEngine.MapEditor.Tests/MapDoc/NativeCollisionMeasurementTests.cs KhaozEngine.MapEditor.Tests/MapDoc/NativeAssetScaleTests.cs
git diff --cached --check
git commit -m "feat(mapedit): expose native collider measurements"
```


## Round Verification and Handoff

Run from the implementation worktree root, sequentially. Focused tests above are the task red/green cycle. The full solution suite runs once at round finish after the solution build, not once per task and never in a repeat loop. Re-run only when a subsequent code change or integration conflict requires it. The slot wrapper retries only lock contention, not failed tests.

```bash
mkdir -p local-feed
/tmp/grand-world/slot-retry.sh wa-r3-focused-KhaozEngine.MapDoc.Physics.Tests /tmp/grand-world/wa-r3-focused-KhaozEngine.MapDoc.Physics.Tests.log -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~Native"
/tmp/grand-world/slot-retry.sh wa-r3-focused-KhaozEngine.MapEditor.Tests /tmp/grand-world/wa-r3-focused-KhaozEngine.MapEditor.Tests.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeCollision|FullyQualifiedName~NativeAssetScale"
/tmp/grand-world/slot-retry.sh wa-r3-build /tmp/grand-world/wa-r3-build.log -- dotnet build KhaozEngine.slnx -c Release
/tmp/grand-world/slot-retry.sh wa-r3-format /tmp/grand-world/wa-r3-format.log -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
/tmp/grand-world/slot-retry.sh wa-r3-suite /tmp/grand-world/wa-r3-suite.log -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
/tmp/grand-world/slot-retry.sh wa-r3-check-dashes /tmp/grand-world/wa-r3-check-dashes.log -- sh scripts/check-dashes.sh --tree
/tmp/grand-world/slot-retry.sh wa-r3-check-prose /tmp/grand-world/wa-r3-check-prose.log -- sh scripts/check-prose.sh --tree
/tmp/grand-world/slot-retry.sh wa-r3-check-file-size /tmp/grand-world/wa-r3-check-file-size.log -- sh scripts/check-file-size.sh --tree
/tmp/grand-world/slot-retry.sh wa-r3-check-agent-instructions /tmp/grand-world/wa-r3-check-agent-instructions.log -- sh scripts/check-agent-instructions.sh --tree
/tmp/grand-world/slot-retry.sh wa-r3-check-doc-versions /tmp/grand-world/wa-r3-check-doc-versions.log -- bash scripts/check-doc-versions.sh
```

Require exit 0 from every command, zero warnings, nonempty focused selections, no format diff and no guard failures. These commands are future implementation verification, not authorization to run tests in the documents lane. GPU facts are skipped by ordinary `dotnet test`. Any visual golden additions use the relevant backend CI bake from `docs/CROSS-PLATFORM.md`, serialized and without booting a consumer. No local stress or repeated suite runs.

The last task also updates the package README, `docs/USING-KHAOZENGINE.md` and every stale Markdown reference for its added APIs. An orchestrator re-reads main, tags and `Directory.Build.props`, selects the next available engine minor after pivot releases, rides an existing staged version only when it belongs to this same round, and updates `CHANGELOG.md` plus all declarations checked by `check-doc-versions.sh`. Delegated workers record verified commits in Outcome and return them for integration. Each round is its own minor capability release and does not share a pivot or another round's release number. No engine release number is reserved here and no worker tags. Build, test and guard failures block the round's exit claim.

## Self-Review

Coverage: C3 payload/package Task 1, transforms/complete descriptors Task 2, common pick/reach/LOS/bands Task 3, walk-up candidates Task 4, extent/residency ownership Task 5, physics/navigation split Task 6, preserved source scale and measured collision edits Task 7. R11 supplies legacy collision variants and R8 consumes the residency data. Every task has one test cycle and a reviewable deliverable. Existing interfaces were checked at the evidence SHA, new interfaces are explicitly produced before consumption, and the five Review Focus cases each have a named assertion in an owning task. The snippets pin behavior rather than implement algorithms. No later round's complete GUI, MCP, importer or rendering workflow is claimed here.

## Outcome
