# World Authoring R3: Shared Collision, Picking, Reach and Headless Builders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build one GPU-free resolved static world whose physical shapes drive collision/occlusion/capture and whose shared canonical interaction envelopes drive picking/reach/stance in both heads.

**Architecture:** MapDoc owns versioned immutable asset data, while the new MapDoc.Physics package compiles shared shapes and complete static descriptors. Existing physics backends register those descriptors and expose a movement view excluding only terrain statics. Narrow-phase queries consume the same oriented compound or baked geometry, with AABBs used solely for acceleration.

**Tech Stack:** C# on the repository's existing .NET target, System.Numerics, System.Text.Json, closed JSON Schema, xUnit, existing engine seams. No new third-party dependency.

**Spec:** `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md`, spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. Round-plan approval remains pending. Read C3, T1 to T9 and the evidence register before implementation.

## Approval and execution gate, reconciled 2026-10-05

The exact owner answer supplied by the controller is "Approve". It approves both specs, T1 to T9 with revised T4, the rigid prefab v1 scope and pre-OA9 12 to 18 elapsed-week estimate, and C4 exact-boundary differential policy. That earlier policy answer did not approve round execution or any actual changed query result. OA8 subsequently approves R1 and the controller owns its active execution. R2 onward remain unapproved and need OA9 refinement before review. Named import-time acceptance of changed targets, distances, occlusion, stances and water boundaries remains required. R1 to R4 retain existing full plans, this branch holds the R1 planning copy only.

Future verification uses HANDOFF's shared slot runner, restored only if absent. Set a unique log directory from the implementation worktree before Task 1, and retain different red/green log names. These are instructions, not commands run by this documents lane.

```bash
wa_r3_log_dir="/tmp/grimhollow-orch/logs/wa-r3-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p local-feed "$wa_r3_log_dir"
test -f /tmp/grimhollow-orch/slot-run.sh
```

The 1 m allowance is a minimum vertical target reach-envelope height, preserving `MinimumObjectReachHeight`. It is not a 1 m action distance. Action range remains existing game policy and physical colliders never expand. Approval IDs are OA4 specs, OA5 revised T4, OA6 prefab/estimate and OA7 water boundary in the game DECISIONS record.

## OA9 checkpoint, before R3 approval

[DG9.1 to DG9.4 and DG9.5](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#dg93-navigation-and-server-scale-before-r3r8-approval) extend the existing T4 refinement gate. R3 remains unapproved. Consume [R2](2026-10-05-world-authoring-r2-authored-terrain-paint.md)'s selected cave/support contract, [R4](2026-10-05-world-authoring-r4-bounded-water-medium.md)'s water contract when available and [R8](2026-10-05-world-authoring-r8-native-rendering-residency-captures.md)'s residency requirements without assuming a single topmost floor.

- **Tasks 2/3/6:** Refine floor/ceiling collision, layer-correct support/picking/LOS and nav under the selected cave model. Current XZ ground delegates and one complete capture are fixture interfaces, not proof of many vertical layers. Pin layer/domain selection before implementation and certify quantified extent/depth/precision under DG9.4.
- **Tasks 5/6 dependency decision:** [#1301](https://github.com/APKiwiOrg/KhaozEngine/issues/1301) requires tiled/incremental nav, deterministic cross-tile seams/links, many vertical layers, on-demand loading and bounded planning work. Record dependency ownership/splitting and tile invalidation/capture/profile identity before R3 approval. Resolve separately landed support or approved round scope explicitly, with no silent extra round/version reservation. Current once-only whole-world adoption rebake remains a fixture procedure. Future edits rebake affected tiles and links.
- **Authoritative scale:** Server coverage follows required simulation domains independently of visual streaming. CellOrigin's one-cell layout is a fixture. With [R8](2026-10-05-world-authoring-r8-native-rendering-residency-captures.md) and downstream game G3, define cross-cell geometry/nav ownership and ghosting/handoff continuity. Storage chunks, nav tiles and server cells need explicit mapping, not an assumed identity.
- **Named future proofs:** `CaveFloorCeilingSupportAndPick_AgreeOnBothHeads`, `TiledNav_SeamsLinksAndVerticalLayersAreDeterministic`, `AffectedTileRebake_PreservesUnaffectedDigests`, `OnDemandNav_RespectsApprovedPlanningBudget` and `MultiCellGhostHandoff_PreservesLayerAndWorldCoordinates`. Pin bounded fixtures/budgets at refinement, no large local rerun of #1301 measurements.
- **Water/profile dependency:** [#1299](https://github.com/APKiwiOrg/KhaozEngine/issues/1299) and R4 own matching client/server/nav opt-in behavior. `DeepWaterBlocksWalkers_ShallowWadingAndOptOutRemainLegacy` is a named gate. Swimming stays outside the program. Today's 8 MiB gzip-9/plain-git game budget remains until a new owner storage/distribution decision.

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

### Shared producer boundaries to pin before plan approval

Consume the CD1-approved R2 cave design and mutually accepted swimming F3 at engine 404fa519f.
Migration owns canonical geometry/support/occupied-space identity, native adapters and bounded
space/portal/link connectivity facts. Swimming owns the generic read lease, complete capsule
resolver and movement consumers under its separate gates. R3 must consume those prerequisites
without duplicating #1299. Record exact shared package/signature and released-pin dependencies.

[Grimhollow #458](https://github.com/APKiwiOrg/Grimhollow/issues/458) is a required consumer of
these producer facts. Pivot owns hearing/presentation/dialogue/shop/follow/target eligibility.
Different support keys cannot classify separate floors across slopes, seams or valid portals.
Do not export one universal CanHear/CanSee/CanInteract result from topology, or build a parallel
floor-ID scheme. Supply scoped provenance and explicit unresolved outcomes. Physical LOS, target
envelopes, traversal and game policy retain their distinct meanings.

Pin bounded geometric relations, portal aperture/state and physical occlusion/clearance certainty
under one frame/read witness. Declare authored versus live state and its producer, with no new
acoustic simulation and no game-maintained topology. Exact R3 adapters consume released generic
F3 prerequisites and the approved R2 producer signatures, not similarly named provisional types.

Refinement must pin fixtures for connected distinct supports, occluded stacked spaces sharing XZ,
portal/shaft transitions and missing/stale topology. Producer tests verify geometry/identity and
connectivity facts. Pivot's consumer fixtures verify each policy and server-authoritative actions.
Native integration remains blocked until exact contracts, required releases and adoption gates.

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

Existing `ReachTarget.Box(Vector3 centre, Vector3 halfExtents, float yawRadians = 0f)` already supports oriented boxes, and `ReachGeometry.Distance(in MovementBody body, in ReachTarget target) -> float` / `Within(in MovementBody body, in ReachTarget target, float range, float tolerance = 0f) -> bool` have no hidden epsilon. Reuse them for box members. Do not rewrite working single-box support. General compound/baked physical queries and derived interaction-envelope queries belong to the shared native service. Revised T4 no longer requires physical-only picking/reach.

Existing `IPhysicsWorld.AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) -> StaticHandle`, `RemoveStatic(StaticHandle handle)`, `CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> excludedStatics) -> IPhysicsWorldQueryView` and `Origin` are the backend seams (`IPhysicsWorld.cs:14-104`). `PropCollisionFormat.Read(Stream stream) -> PhysicsShape` and `Version=1` parse baked data render-free. `PhysicsNavBake.Capture(GroundMoveContext context, PhysicsNavBakeOptions options, NavAreaClassifier classify) -> PhysicsNavBake` needs complete physics, never only a slope query.

Use `MapDoc.Physics` as a new optional package, not a render-side ChunkStatics export. Solid intent becomes explicit native asset metadata in this round, retaining Kind as a game-facing key. Collision variants are digest-bearing assets supplied by the offline importer in R11, not runtime footprint recalculation. R3 publishes complete scene descriptors and geometry queries, not the later camera/view stream or backend goldens. Runtime residency ownership is ready here, R8 composes streaming and unload. Interaction bands are caller-supplied absolute world Y ranges. Tree eligibility uses [resolved base Y, resolved base Y + 2 m], regardless of tree scale, without changing its physical collider. MinimumObjectReachHeight applies a minimum vertical target reach-envelope height of 1 m, not action range. Explicit shape-specific construction/selectability is a mandatory R3 refinement gate below.

### Revised T4 refinement gate, OA5, 2026-10-05

R3 remains a full task/step plan but is not execution-approved. Before its owner plan review, bind these proposed new APIs to the released R1/R2 closure and refine the precise envelope algorithm and assertions for each supported geometry. The original physical-only interaction instruction is superseded. This is a technical plan proposal under the approved policy, not an exact owner quotation.

- Canonical input is the digest-verified interaction-source resource in the asset closure, with compound/baked topology and explicit non-solid selection geometry. No head independently substitutes a rendered mesh AABB or game catalog footprint. An asset with physical and interaction resources records both digests, their derivation and the envelope policy version.
- Produce one immutable envelope per placement from that canonical input and the same source-units/XYZ/yaw/positive-scale transform as its physical geometry. Pin `MinimumObjectReachHeight` as a minimum 1 m world-space vertical reach-envelope height for low objects, independently of action range. Define selectable bounds and exact eligible ray/distance geometry consistently. Refinement must specify whether selectable geometry includes the vertical allowance and pin the result with tests, rather than leaving a silent pick-versus-reach divergence.
- Preserve compound apertures and baked openings. Physical shapes/statics/occlusion never use the enlarged envelope. Tree lower-2-m eligibility applies to pick/reach/stance consistently. Physical clearance and LOS remain separate queries on exact physical shapes. Candidate generation uses the envelope, clearance uses complete physical geometry.
- Refinement records exact box/baked-cylinder/hull/mesh/compound construction, source choice, vertical allowance/band clipping order, coordinate convention, closest-point/ray results and policy schema/version/hash. It must remove any remaining ambiguous branch before execution. A blanket AABB, physical-collider inflation or per-head geometry fork fails review.
- Named tests must prove a 0.2 m-high object's target reach envelope has minimum vertical height 1 m while its physical bounds remain 0.2 m, unchanged game action range, consistent selectable hit behavior, an open rotated/scaled doorway, physical wall occlusion, a tree hit at base+1 m and refusal at base+3 m, and identical two-head envelope/policy/physical digests. Import-time acceptance still names actual changed targets/distances/occlusion/stances.

### Late bridge and downstream capture requirements, 2026-10-05

Task 2/6 fixtures preserve the `river_bridge_grand` support over local x [-9,9] for its 16x5 deck footprint, source bed -130 cm, `walkSurface` 2.825, parapet `collisionHeight` 3.825 and 32 separate one-edge 1x1 Wall pieces. These PROGRAM values require source refreeze at R11, not a claim that this old fixture has them. Assert support exists at both overhangs, deck feet are dry after R4, parapet edges block without a full-cell box, and complete capture/movement registration retains support. R11 validates source-driven variants and named differential outcomes.

The game nav artifact budget is 8 MiB deterministic `gzip -9`, committed in plain git. R3 supplies full capture/options/profile/geometry identity hooks for the downstream G3 rebake. Do not turn that game artifact budget into a universal engine limit or claim a bake measured here.

### Task 1: Version-1 asset shapes and optional GPU-free package

**Files:**
- Create: `KhaozEngine.MapDoc/Assets/MapCollisionDoc.cs`, `KhaozEngine.MapDoc/Assets/MapSupportDoc.cs`, `KhaozEngine.MapDoc/Assets/MapInteractionPolicyDoc.cs`
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
- Produces new: `MapShapePayload.Read(MapAssetClosure closure, MapResolvedAsset asset, bool interactionSource) -> MapCollisionDoc`, validating supported payload and baked format/digest.
- Produces proposed new: `MapInteractionPolicyDoc(int PayloadVersion, int PolicyVersion, float MinimumVerticalReachHeightMetres)` with minimum height 1 m for the approved policy. Its closure reference and geometry derivation are explicit, with finite-value/version refusal. Selectability/band construction is finalized by the refinement gate, not silently chosen at implementation.
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

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t1:red" "${wa_r3_log_dir}/wa-r3-t1-red.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeShapeDocumentTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the payload DTOs in MapDoc and `MapShapePayload.Read(...)` in new `KhaozEngine.MapDoc.Physics/MapShapePayload.cs`. Default old descriptors IsSolid=false for compatibility, require collision data and a non-None shape for explicit solids. Canonical interaction-source geometry may use the same versioned schema on solids/non-solids, with a separate digest-bearing MapInteractionPolicyDoc. It is not physical collision. Refine the exact policy payload before owner plan approval. Reject empty compounds, nonfinite or nonpositive dimensions, unsupported kinds/versions, bad indices/hulls and stale .coll payloads. The package references only MapDoc, Physics, Movement and Collision as needed, with no GPU/Render3D/MapEditor/TileWorld edge and no Bepu dependency. Tests reference MapDoc.Physics plus Physics.Bepu, IsPackable=false and `KhaozEngine.Tests.MapDocPhysics` namespaces. Add the package/test projects to the solution and authoritative package/dependency rows. No umbrella inclusion is implicit.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t1:green" "${wa_r3_log_dir}/wa-r3-t1-green.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeShapeDocumentTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Assets/MapCollisionDoc.cs KhaozEngine.MapDoc/Assets/MapSupportDoc.cs KhaozEngine.MapDoc/Assets/MapInteractionPolicyDoc.cs KhaozEngine.MapDoc.Physics/MapShapePayload.cs KhaozEngine.MapDoc.Physics/KhaozEngine.MapDoc.Physics.csproj KhaozEngine.MapDoc.Physics/README.md KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj KhaozEngine.MapDoc.Physics.Tests/NativeShapeDocumentTests.cs KhaozEngine.MapDoc.Physics.Tests/NativeShapeFixtures.cs KhaozEngine.MapDoc/Assets/MapAssetManifest.cs KhaozEngine.MapDoc/Assets/MapAssetClosure.cs KhaozEngine.slnx README.md docs/DEPENDENCY-SEAMS.md
git diff --cached --check
git commit -m "feat(mapdoc): define native asset collision payloads"
```


### Task 2: Complete immutable builder and transformed statics

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapWorldBuildOptions.cs`, `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs`, `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs`, `KhaozEngine.MapDoc.Physics/MapPlacementShapes.cs`, `KhaozEngine.MapDoc.Physics/MapInteractionEnvelopes.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeWorldBuilderTests.cs`

**Interfaces:**
- Consumes R1: MapResolver.Resolve and MapResolveOptions. Consumes R2: CompileAll, MapSurfaceSampler, blocked masks and MapVolumeMembership. Consumes Task 1: shape/support payloads.
- Produces new: `MapWorldBuildOptions(string GroundSurfaceId, MapSurfaceFallback Fallback, string ConsumerPolicyIdentity, Func<MapBlockedMask,bool>? KeepBlockedMask = null, int BuilderVersion = 1)`.
- Produces new: `MapWorldBuilder.Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) -> MapBuiltWorld`.
- Produces new: immutable `MapResolvedShape` with PlacementId, NumericId, Digest, Bounds, SelectionOnly and `CreatePhysicsShape() -> PhysicsShape`, `Pose WorldPose`. Internal geometry owns defensive copies.
- Produces proposed new: immutable `MapResolvedInteractionEnvelope(string PlacementId, long? NumericId, MapResolvedShape Geometry, MapLocalBounds SelectableBounds, string PolicyDigest, string Digest)` and `MapInteractionEnvelopes.Resolve(MapResolvedDocument document, IReadOnlyList<MapResolvedShape> physicalShapes) -> IReadOnlyList<MapResolvedInteractionEnvelope>`. These names identify new outputs, not released APIs. Refine the exact bounds space and geometry derivation before approval.
- Produces new: `MapPlacementShapes.Resolve(MapResolvedDocument document) -> IReadOnlyList<MapResolvedShape>`.
- Produces new: `MapStaticDescriptor(string OwnerId, bool IsTerrain, MapResolvedShape Shape)` and immutable `MapBuiltWorld` properties Document, Surfaces, Shapes, InteractionEnvelopes, SupportSurfaces, BlockedMasks, Volumes, Sampler, StaticDescriptors, AuthoredHash, Bounds. R4 adds water properties without replacing these names.

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

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t2:red" "${wa_r3_log_dir}/wa-r3-t2-red.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWorldBuilderTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced signatures in their named files. Validate complete closure/doc before publishing any result. Resolve null-Y placements via the canonical sampler, retain imported explicit Y, and compose local shape pose, asset SourceUnitsToMetres and placement Scale exactly once. Build interaction envelopes from canonical source geometry using the reviewed revised-T4 policy. Keep envelopes and support/deck triangles separate from solid static ownership while sharing transforms and closure/policy identity. Bounds union shapes, verified mesh bounds, support, lights and LOD extents. Compile terrain static descriptors from R2 canonical triangles, skip non-capture fallback and keep blocked masks explicit. Hash actual builder version/options and a stable consumer policy identity, rejecting empty identity when a callback is supplied. No backend/device allocation occurs. Add scale 0/-1/infinity, baked stale data, slope-seated wall variants, two-edge corner walls, support decks, unchanged Kind and defensive-shape-copy tests.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t2:green" "${wa_r3_log_dir}/wa-r3-t2-green.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWorldBuilderTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Physics/MapWorldBuildOptions.cs KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs KhaozEngine.MapDoc.Physics/MapPlacementShapes.cs KhaozEngine.MapDoc.Physics/MapInteractionEnvelopes.cs KhaozEngine.MapDoc.Physics.Tests/NativeWorldBuilderTests.cs
git diff --cached --check
git commit -m "feat(mapdocphysics): build shared authored world descriptors"
```


### Task 3: Shared-envelope picking/reach and physical LOS

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapShapeGeometry.cs`, `KhaozEngine.MapDoc.Physics/MapWorldQueries.cs`, `KhaozEngine.MapDoc.Physics/MapInteractionGeometry.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeShapeQueryTests.cs`

**Interfaces:**
- Consumes existing: MovementBody(Vector3 centre,float radius,float halfHeight), ReachTarget.Box and ReachGeometry.Distance/Within. Consumes Task 2 physical shapes and interaction envelopes.
- Produces new: `MapPickRay(Vector3 Origin,Vector3 Direction,float MaxDistance)`, `MapInteractionBand(float MinY,float MaxY)`, `MapPickHit(string PlacementId,long? NumericId,float Distance,Vector3 Point,Vector3 Normal)`.
- Produces new: `MapShapeGeometry.Distance(Vector3 point,MapResolvedShape shape) -> float`, `Distance(in MovementBody body,MapResolvedShape shape,MapInteractionBand? band = null) -> float`, `Raycast(MapPickRay ray,MapResolvedShape shape,MapInteractionBand? band,out MapPickHit hit) -> bool`.
- Produces proposed new: `MapInteractionGeometry.Distance(in MovementBody body, MapResolvedInteractionEnvelope envelope, MapInteractionBand? band = null) -> float`, `Raycast(MapPickRay ray, MapResolvedInteractionEnvelope envelope, MapInteractionBand? band, out MapPickHit hit) -> bool`. Exact geometry/band/selectable behavior is fixed at refinement. MapShapeGeometry retains physical-only distance/ray operations for clearance and occlusion.
- Produces new: `MapWorldQueries(MapBuiltWorld world)`, `Pick(MapPickRay ray,MapInteractionBand? band = null) -> MapPickHit?`, `Distance(in MovementBody body,string placementId,MapInteractionBand? band = null) -> float`, `Within(in MovementBody body,string placementId,float range,float tolerance = 0,MapInteractionBand? band = null) -> bool`, `PhysicalDistance(in MovementBody body,string placementId) -> float`, `HasLineOfSight(Vector3 from,Vector3 to,string? ignoredPlacementId = null) -> bool`. Pick/Distance/Within consume envelopes. PhysicalDistance/LOS consume unchanged physical shapes.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeDoorwayInteractionPreservesOpenings_WithoutPhysicalInflation()
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

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t3:red" "${wa_r3_log_dir}/wa-r3-t3-red.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeShapeQueryTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the refined geometry/query signatures in their new files. Pick/Distance/Within query the canonical interaction envelope. PhysicalDistance and LOS query unchanged physical shapes. The 1 m minimum is vertical target-envelope height only, not range. Box members use existing exact yawed-box reach. Compound distance is the minimum to actual members, never their bounding AABB. Baked hull/mesh members use point/segment-to-triangle distances and ray intersections on their respective immutable physical or canonical interaction geometry. Envelope geometry must never be supplied to CreatePhysicsShape for static registration. Baked cylinder members use exact cylinder distance/ray-cap math, not a tessellated proxy. Recursively apply baked compound local poses and reject unsupported geometry before building. Capsule distance measures the capsule's medial segment then subtracts radius, returning zero on overlap. Validate rays, finite nonnegative ranges/tolerances and nonempty bands, clipping eligible geometry to [MinY,MaxY] for band queries while physical collision remains unchanged. LOS ignores selection-only shapes. Pick uses the explicitly refined selectable envelope geometry for solid and non-solid placements, with nearest distance then ordinal PlacementId tie breaking. Add rotated doorway hole, baked concavity, non-solid Examine selection, self-ignored LOS, equal-distance pick ordering and epsilon-free reach boundary tests.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t3:green" "${wa_r3_log_dir}/wa-r3-t3-green.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeShapeQueryTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Physics/MapShapeGeometry.cs KhaozEngine.MapDoc.Physics/MapWorldQueries.cs KhaozEngine.MapDoc.Physics/MapInteractionGeometry.cs KhaozEngine.MapDoc.Physics.Tests/NativeShapeQueryTests.cs
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
    var a = MapStanceCandidates.Find(world,"doorway",feet,options,b => q.PhysicalDistance(b,"doorway") > 0);
    var b = MapStanceCandidates.Find(world,"doorway",feet,options,b => q.PhysicalDistance(b,"doorway") > 0);
    Assert.NotEmpty(a);
    Assert.Equal(a,b);
    Assert.All(a,p => Assert.True(q.Within(new MovementBody(p+Vector3.UnitY*0.8f,0.2f,0.8f),"doorway",0.4f)));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t4:red" "${wa_r3_log_dir}/wa-r3-t4-red.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeStanceTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `Find(...)` in `MapStanceCandidates.cs`. Generate candidates from the refined interaction-envelope box/mesh boundary edges at explicit Spacing, include aperture-side edges, sample canonical terrain or placement supports, and reject points outside playable bounds, beyond MaxStepHeight, without supplied complete clearance, or outside exact envelope reach. Complete physical clearance and LOS stay unchanged, not envelope blockers. Do not introduce a circular proxy, global grid alignment or whole-building box. Stable geometric traversal and sorting make repeat calls/head results identical. Range and tolerance remain caller-owned. Add compound aperture usable stance, sloped ground, bridge support, zero-result case, invalid option, lower-band and narrow-door capsule-clearance assertions. This returns local candidates, not a game route or interaction policy.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t4:green" "${wa_r3_log_dir}/wa-r3-t4-green.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeStanceTests"`
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

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t5:red" "${wa_r3_log_dir}/wa-r3-t5-red.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeResidencyOwnershipTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `Build(...)` and `InWindow(...)` in `MapResidencyOwnership.cs`. A stable owner ID is stored once, membership lists every storage chunk intersecting effective shape/mesh/support/light/LOD union bounds, including negative coordinates and exact edges. Use minimum-inclusive maximum-exclusive chunk membership, with degenerate bounds explicitly assigned by anchor. Broad bounds do not replace narrow-phase query geometry. InWindow returns sorted distinct owners whose membership overlaps the window. For the complete-load regression fixture, server StaticDescriptors remain complete and do not take a client window/camera parameter. DG9.3 refinement defines required authoritative domains and on-demand loading, never camera-owned simulation residency. Add a transformed spanning wall and tree light/LOD extents, exact-seam no-overcount, union-of-windows equals complete ownership, and refusal of partial MapDocument input that lacks declared required-domain closure. DG9.3 must refine domain-scoped validation before scalable loading is claimed. Actual streamed loading, unload and draw deduplication are R8 gates.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t5:green" "${wa_r3_log_dir}/wa-r3-t5-green.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeResidencyOwnershipTests"`
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

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t6:red" "${wa_r3_log_dir}/wa-r3-t6-red.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativePhysicsRegistrationTests|FullyQualifiedName~NativeNavigationCaptureTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `Register(...)`, CreateMoveContext and Dispose in `MapPhysicsRegistration.cs`. Register world-space poses relative to `physics.Origin`, subtracting it exactly once. Complete physics contains all terrain/support/solid descriptors. Exclude only IsTerrain handles from the movement query view, never bridge decks or rails. Supply canonical sampler height/normal and a playable-bounds clamp. Until R4 the native context medium delegate is null, preserving explicit absence. Remove handles in reverse order on failure/disposal, never dispose the caller-owned physics world and refuse unsupported query-view backends. Add backend downward ray comparisons <=0.00001 m to canonical samples, retained deck/rail clearance, rebased-origin equivalence, disposal count and `PhysicsNavBake` profiles proving doorway opening and transformed obstacle blocking. Use existing `PhysicsNavBake.BuildProfile(in MoveTuning tuning,NavAreaFilter areas) -> GroundNavigation` and `GroundNavigation.AllowsSegment(Vector3 fromFeet,Vector3 toFeet) -> bool`. Set tuning MaxSlopeRadians exactly equal to capture Options.MaxSlopeRadians. The fixture pins aperture center x=0, solid jamb x=1, z=-0.5 to 0.5 and a deck segment y=0.4. Assert aperture traversal true, jamb traversal false and deck traversal true.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t6:green" "${wa_r3_log_dir}/wa-r3-t6-green.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativePhysicsRegistrationTests|FullyQualifiedName~NativeNavigationCaptureTests"`
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

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t7:red" "${wa_r3_log_dir}/wa-r3-t7-red.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeCollisionMeasurementTests|FullyQualifiedName~NativeAssetScaleTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced loader and service signatures in their named files. Native mesh/local origin is preserved, source units convert once and HeightMeters never renormalizes native art. Main mesh/parts/LOD/material paths use verified resources. Measure raw mesh maximum Y separately from world transformed physical collision bottom/top. Height writes use asset-local metre bottom/top values and prevalidate a chosen editable compound-box asset, scale vertical extents only, refuse ambiguous baked-shape edits with an explicit diagnostic, and keep mesh digests/transform unchanged. Atomic asset promotion updates references/closure hash and invalidates every affected shape/nav/residency owner. Dry run reports the exact diff and allocated nothing. Add real apply/save/reload, failure preservation, slope variant movement diagnostics and GUI/service shared-build tests. Publish docs and run all round verification before handoff. Backend visual captures remain R8.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-t7:green" "${wa_r3_log_dir}/wa-r3-t7-green.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeCollisionMeasurementTests|FullyQualifiedName~NativeAssetScaleTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.Terrain.Render3D/NativeMapAssetLoader.cs KhaozEngine.MapEdit.Tool/NativeCollisionService.cs KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj KhaozEngine.MapEdit.Tool/KhaozEngine.MapEdit.Tool.csproj KhaozEngine.MapDoc.Physics/README.md docs/USING-KHAOZENGINE.md KhaozEngine.MapEdit.Tool/README.md KhaozEngine.MapEditor.Tests/MapDoc/NativeCollisionMeasurementTests.cs KhaozEngine.MapEditor.Tests/MapDoc/NativeAssetScaleTests.cs
git diff --cached --check
git commit -m "feat(mapedit): expose native collider measurements"
```


## Round Verification and Handoff

Run from the implementation worktree root, sequentially. Focused tests above are the task red/green cycle. The full solution suite runs once at round finish after the solution build, not once per task and never in a repeat loop. Re-run only when a subsequent code change or integration conflict requires it. The slot runner returns the target exit code. Exit 75 means no command ran because the slot was busy. Hand that result to the controller, never retry a failed test or loop verification.

```bash
mkdir -p local-feed "${wa_r3_log_dir}"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-focused-KhaozEngine.MapDoc.Physics.Tests:finish" "${wa_r3_log_dir}/wa-r3-focused-KhaozEngine.MapDoc.Physics.Tests-finish.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~Native"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-focused-KhaozEngine.MapEditor.Tests:finish" "${wa_r3_log_dir}/wa-r3-focused-KhaozEngine.MapEditor.Tests-finish.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeCollision|FullyQualifiedName~NativeAssetScale"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-build:finish" "${wa_r3_log_dir}/wa-r3-build-finish.log" -- dotnet build KhaozEngine.slnx -c Release
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-format:finish" "${wa_r3_log_dir}/wa-r3-format-finish.log" -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-suite:finish" "${wa_r3_log_dir}/wa-r3-suite-finish.log" -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-check-dashes:finish" "${wa_r3_log_dir}/wa-r3-check-dashes-finish.log" -- sh scripts/check-dashes.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-check-prose:finish" "${wa_r3_log_dir}/wa-r3-check-prose-finish.log" -- sh scripts/check-prose.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-check-file-size:finish" "${wa_r3_log_dir}/wa-r3-check-file-size-finish.log" -- sh scripts/check-file-size.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-check-agent-instructions:finish" "${wa_r3_log_dir}/wa-r3-check-agent-instructions-finish.log" -- sh scripts/check-agent-instructions.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r3-check-doc-versions:finish" "${wa_r3_log_dir}/wa-r3-check-doc-versions-finish.log" -- bash scripts/check-doc-versions.sh
```

Require exit 0 from every command, zero warnings, nonempty focused selections, no format diff and no guard failures. These commands are future implementation verification, not authorization to run tests in the documents lane. GPU facts are skipped by ordinary `dotnet test`. Any visual golden additions use the relevant backend CI bake from `docs/CROSS-PLATFORM.md`, serialized and without booting a consumer. No local stress or repeated suite runs.

The last task also updates the package README, `docs/USING-KHAOZENGINE.md` and every stale Markdown reference for its added APIs. An orchestrator re-reads main, tags and `Directory.Build.props`, selects the next available engine minor after pivot releases, rides an existing staged version only when it belongs to this same round, and updates `CHANGELOG.md` plus all declarations checked by `check-doc-versions.sh`. Delegated workers record verified commits in Outcome and return them for integration. Each round is its own minor capability release and does not share a pivot or another round's release number. No engine release number is reserved here and no worker tags. Build, test and guard failures block the round's exit claim.

## Self-Review

Coverage: C3 payload/package Task 1, transforms/physical descriptors/interaction envelopes Task 2, envelope pick/reach and physical LOS/bands Task 3, walk-up candidates Task 4, extent/residency ownership Task 5, physics/navigation split Task 6, preserved source scale and measured collision edits Task 7. R11 supplies legacy collision variants and R8 consumes the residency data. Every task has one test cycle and a reviewable deliverable. Existing interfaces were checked at the evidence SHA, new interfaces are explicitly produced before consumption, and the five Review Focus cases each have a named assertion in an owning task. The snippets pin behavior rather than implement algorithms. No later round's complete GUI, MCP, importer or rendering workflow is claimed here.

## Outcome

### OA9 documentation outcome, 2026-10-05

- Recorded [OA9](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#owner-rulings-binding-direction) and this plan's [checkpoint](#oa9-checkpoint-before-r3-approval). Existing source-count fixtures and prior guard results below remain historical.
- R3 remains unapproved. Its checkpoint names pending choices, owning tasks/dependencies and future proofs, to be refined before owner round approval. No capability, art, swimming, world enlargement or fresh benchmark is claimed.
- This revision requires serial doc guards and explicit-path commit. The worker stops at the docs commit for controller verification/push, with no builds/tests/format/pack or integration.

### Historical documentation reconciliation before OA8/OA9, 2026-10-05

- Approval stage: specs approved with revised T4. R3 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: Reconcile the released prerequisite APIs and record their actual SHAs before owner plan review. R3 also needs explicit revised-T4 geometry/query refinement. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: source and planning review only, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: OA5 replaces physical-only interaction with canonical envelopes while physical collision/occlusion remain unchanged. Explicit geometry/query refinement is an open R3 approval gate. Late bridge support/edge-Wall and downstream game nav-budget requirements are recorded.
- Approval record: OA4 to OA7 in game DECISIONS, controller-reported pushed game docs commit `3e46fac49c1d15adf84c948fdc17f4b6606827aa`. No engine implementation approval is inferred.

Documentation checks from `/Users/antonio/KhaozEngine/.worktrees/world-authoring` on 2026-10-05, no builds/tests. The commands below are rerun serially against final text before committing.

| Command | Exit |
| --- | --- |
| `sh scripts/check-dashes.sh --tree` | 0 |
| `sh scripts/check-prose.sh --tree` | 0 |
| `sh scripts/check-file-size.sh --tree` | 0 |
| `sh scripts/check-agent-instructions.sh --tree` | 0 |
| `bash scripts/check-doc-versions.sh` | 0 |
| `git diff --check` | 0 |

No pre-existing documentation guard blocker was observed. Doc-version validation checks this historical planning branch's 20.24.0 declarations. It does not claim this branch contains released 20.25.0 or its packages. Controller review/push and separate owner round approval remain the next gates.
