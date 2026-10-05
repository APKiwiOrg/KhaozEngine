# World Authoring R4: Bounded Water and Medium Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Author independent bounded water bodies whose exact domains and levels are shared by render input, movement medium and nav capture.

**Architecture:** Format 6 adds bounded water records alongside explicit legacy-global mode for analytic compatibility. The shared headless world builder compiles one domain representation for geometry and feet-aware sampling. Commands replace bodies atomically, with terrain height edits unable to change their saved SurfaceY.

**Tech Stack:** C# on the repository's existing .NET target, System.Numerics, System.Text.Json, closed JSON Schema, xUnit, existing engine seams. No new third-party dependency.

**Spec:** `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md`, spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. Round-plan approval remains pending. Read C4, T1 to T9 and the evidence register before implementation.

## Approval and execution gate, reconciled 2026-10-05

The exact owner answer supplied by the controller is "Approve". It approves both specs, T1 to T9 with revised T4, the rigid prefab v1 scope and 12 to 18 elapsed-week estimate, and C4 exact-boundary differential policy. It does not approve this round plan or any actual changed query result. Named import-time acceptance of changed targets, distances, occlusion, stances and water boundaries remains required. R1 to R4 are full plans. Reconcile released prerequisite signatures before their owner review. No production execution has started.

Future verification uses HANDOFF's shared slot runner, restored only if absent. Set a unique log directory from the implementation worktree before Task 1, and retain different red/green log names. These are instructions, not commands run by this documents lane.

```bash
wa_r4_log_dir="/tmp/grimhollow-orch/logs/wa-r4-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p local-feed "$wa_r4_log_dir"
test -f /tmp/grimhollow-orch/slot-run.sh
```

The 1 m allowance is a minimum vertical target reach-envelope height, preserving `MinimumObjectReachHeight`. It is not a 1 m action distance. Action range remains existing game policy and physical colliders never expand. Approval IDs are OA4 specs, OA5 revised T4, OA6 prefab/estimate and OA7 water boundary in the game DECISIONS record.

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

- Negative-Z rectangle edges and polygon vertices must use the same half-open ownership in every consumer (Task 2).
- Equal-medium/equal-level overlapping bodies must be rejected rather than silently double rendering (Task 1).
- Feet exactly at water SurfaceY, including a bridge deck, must remain dry (Task 3).
- An authored map with no bodies must not inherit the old global ocean, and independent levels must survive terrain edits (Tasks 3 and 4).
- The historical seven-body/113-rectangle regression fixture must survive grouping, save/reload and domain compilation (Task 5).

---

## File Structure

| Path | Responsibility |
| --- | --- |
| `KhaozEngine.MapDoc/Water/MapWaterDoc.cs`, `KhaozEngine.MapDoc/Water/MapWaterValidator.cs`, `KhaozEngine.MapDoc/Water/MapWaterMigration.cs` | Format-6 bodies, medium/material bindings and explicit mode |
| `KhaozEngine.MapDoc/Water/MapWaterDomain.cs` | One normalized half-open domain predicate |
| `KhaozEngine.MapDoc.Physics/MapWaterCompiler.cs`, `KhaozEngine.MapDoc.Physics/MapWaterSampler.cs` | Clipped draw/capture descriptors and feet-aware medium |
| `KhaozEngine.MapDoc/Editing/MapWaterEdits.cs` | Pure body and boundary replacements |
| `KhaozEngine.MapEditor/NativeWaterCommands.cs` | Shared undoable water command surface |
| `KhaozEngine.MapEdit.Tool/MutationServiceWater.cs` | Typed lifecycle and query services |
| `KhaozEngine.MapDoc.Physics.Tests/NativeWater*.cs` | Schema/domain/medium/deck/nav proofs |
| `KhaozEngine.MapEditor.Tests/MapDoc/NativeWaterCommandTests.cs` | Command parity and rejection atomicity |
| `KhaozEngine.MapDoc.Compatibility.Tests/Fixtures/HollowmereWater.json` | Frozen seven-body ledger and native/source comparisons |


## Source-Checked Contract and Judgement Calls

R3 produces `MapBuiltWorld`, `MapWorldBuilder.Build`, `MapPhysicsRegistration.CreateMoveContext` and `MapWorldBuildOptions`. Extend those types, preserving their names and existing constructor parameters. Existing `MovementMedium(float waterSurfaceY, bool inWater, float wadeSpeedScale = 1f)` with properties WaterSurfaceY, InWater and WadeSpeedScale is the movement seam (`KhaozEngine.Locomotion/MovementMedium.cs:15-40`). `GroundMoveContext.Medium` is `Func<float,float,float,MovementMedium>?`, and `PhysicsNavBakeOptions.SampleWater` controls capture's medium read. R3's context medium is null before this round.

The spec now pins rectangle ownership [MinX,MaxX) x [MinZ,MaxZ) in world coordinates. Normalize simple polygon rings to counter-clockwise XZ winding. For a point on an edge, include a negative-Z directed edge or a horizontal positive-X directed edge. A vertex belongs only if every incident edge owns it. This gives the rectangle predicate exactly and makes adjacent domains own a shared seam once. Positive-area overlap is invalid even with equal level/media. OA7 approves this rule. Every actual legacy negative-Z boundary change records source key/sample, old/new outcome and a separately named acceptance reference at R11/game import. PolicyId alone never accepts a differential.

Store bodies independently. An editor grouping does not replace body IDs or merge ledger records. Bounded draw triangles retain body/material identity and level, with no camera-centred fallback. Actual Scene3D bounded drawing and backend goldens are R8. R4 provides preview/capture-ready polygons plus GUI/service commands, and R9 completes boundary brush UX.

### Task 1: Format-6 bounded bodies, mode and validation

**Files:**
- Create: `KhaozEngine.MapDoc/Water/MapWaterDoc.cs`, `KhaozEngine.MapDoc/Water/MapWaterValidator.cs`, `KhaozEngine.MapDoc/Water/MapWaterMigration.cs`
- Modify: `KhaozEngine.MapDoc/MapNativeDocument.cs`, `KhaozEngine.MapDoc/MapDocumentFile.cs:28-83`, `KhaozEngine.MapDoc/MapDocumentValidator.cs:14-27`, `KhaozEngine.MapDoc/MapCanonical.cs:51-76`, `KhaozEngine.MapDoc/MapTiledFile.cs:91-150`, `KhaozEngine.MapDoc/mapdoc.schema.json`
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeWaterDocumentTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/NativeWaterFixtures.cs`

**Interfaces:**
- Consumes R1 closure, R2 material registry, R3 builder options and shapes.
- Produces new: `MapWaterMode { LegacyGlobal, Bounded }`, root WaterMode, `List<MapWaterBodyDoc> WaterBodies`, `List<MapWaterMediumDoc> WaterMedia`.
- Produces new: `MapWaterBodyDoc` with `int PayloadVersion=1`, `string Id`, `float SurfaceY`, `string MediumKey`, `ushort RenderMaterialId`, `List<MapWaterDomainDoc> Domains`.
- Produces new: closed domain union `MapWaterDomainDoc`, `MapWaterRect(float MinX,float MinZ,float MaxX,float MaxZ)`, `MapWaterPolygon(IReadOnlyList<Vector2> Ring)`.
- Produces new: `MapWaterMediumDoc(string Key,float WadeSpeedScale)`, finite positive scale, movement interpretation explicit.
- Produces new: `MapWaterValidator.Validate(MapDocument document) -> IReadOnlyList<string>`, pure `MapWaterMigration.Upgrade(JsonObject source) -> JsonObject`.
- Test helper new: `NativeWaterFixtures.Document() -> MapDocument` with two disjoint bodies named river-a/pond-b and levels -0.37/1.2, river-a rectangle [-1,1) x [-2,0), pond-b rectangle [2,3) x [-2,-1), valid media/material closure and canonical ground. `Bridge() -> (MapDocument Document,MapAssetClosure Assets,MapWorldBuildOptions Options)` adds a deck above river-a.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeWater_RejectsPositiveAreaOverlapEvenAtSameLevel()
{
    var doc = NativeWaterFixtures.Document();
    Assert.Empty(MapWaterValidator.Validate(doc));
    var a = doc.WaterBodies[0];
    var b = doc.WaterBodies[1];
    b.SurfaceY = a.SurfaceY;
    b.MediumKey = a.MediumKey;
    b.Domains = a.Domains.ToList();
    Assert.NotEmpty(MapWaterValidator.Validate(doc));
    b.Domains = new() { new MapWaterPolygon(new[] {
        new Vector2(0,0),new Vector2(1,1),new Vector2(0,1),new Vector2(1,0) }) };
    Assert.NotEmpty(MapWaterValidator.Validate(doc));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t1:red" "${wa_r4_log_dir}/wa-r4-t1-red.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWaterDocumentTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced DTOs, mode migration and validator in the named files. Add pure 5 to 6 migration, analytic maps select LegacyGlobal, authored maps select Bounded, with no implicit water bodies. Schema closes all new payloads and discriminators. Require unique stable body IDs, supported payloads, finite SurfaceY/coordinates, nonempty nondegenerate simple domains, known medium/material resources, finite positive medium parameters and disjoint positive-area interiors within/across bodies. Boundary touching is valid. Reject authored LegacyGlobal and a LegacyGlobal map containing bounded bodies rather than drawing both. Persist water globals and canonical identity in both forms. Add unknown resource/version/field, repeated vertex, empty domain, NaN level and analytic-v5 water compatibility tests.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t1:green" "${wa_r4_log_dir}/wa-r4-t1-green.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWaterDocumentTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Water/MapWaterDoc.cs KhaozEngine.MapDoc/Water/MapWaterValidator.cs KhaozEngine.MapDoc/Water/MapWaterMigration.cs KhaozEngine.MapDoc/MapNativeDocument.cs KhaozEngine.MapDoc/MapDocumentFile.cs KhaozEngine.MapDoc/MapDocumentValidator.cs KhaozEngine.MapDoc/MapCanonical.cs KhaozEngine.MapDoc/MapTiledFile.cs KhaozEngine.MapDoc/mapdoc.schema.json KhaozEngine.MapDoc.Physics.Tests/NativeWaterDocumentTests.cs KhaozEngine.MapDoc.Physics.Tests/NativeWaterFixtures.cs
git diff --cached --check
git commit -m "feat(mapdoc): add bounded water document records"
```


### Task 2: One half-open domain predicate and clipped geometry

**Files:**
- Create: `KhaozEngine.MapDoc/Water/MapWaterDomain.cs`
- Create: `KhaozEngine.MapDoc.Physics/MapWaterCompiler.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeWaterDomainTests.cs`

**Interfaces:**
- Consumes Task 1 water records and domain validation.
- Produces new: `MapWaterDomain.Normalize(MapWaterDomainDoc domain) -> MapWaterPolygon`, `Contains(MapWaterDomainDoc domain,Vector2 xz) -> bool`.
- Produces new: `MapWaterTriangle(string BodyId,ushort MaterialId,Vector3 A,Vector3 B,Vector3 C)`.
- Produces new: immutable `MapCompiledWaterBody` with Id, SurfaceY, MediumKey, RenderMaterialId, Domains, Bounds, `IReadOnlyList<MapWaterTriangle> Triangles`.
- Produces new: `MapWaterCompiler.Compile(MapDocument document) -> IReadOnlyList<MapCompiledWaterBody>`, complete domains only, no camera or region-rim parameter.

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
[InlineData(-1, -2, true)]
[InlineData(0, -2, false)]
[InlineData(-1, -1, false)]
[InlineData(-0.5f, -1.5f, true)]
public void NativeWater_RectAndPolygonShareNegativeZBoundary(float x,float z,bool expected)
{
    var rect = new MapWaterRect(-1,-2,0,-1);
    var polygon = MapWaterDomain.Normalize(rect);
    Assert.Equal(expected,MapWaterDomain.Contains(rect,new Vector2(x,z)));
    Assert.Equal(expected,MapWaterDomain.Contains(polygon,new Vector2(x,z)));
}
[Fact]
public void NativeWater_SharedEdgeHasExactlyOneOwner()
{
    var a = new MapWaterRect(-1,-2,0,-1);
    var b = new MapWaterRect(0,-2,1,-1);
    var point = new Vector2(0,-1.5f);
    Assert.False(MapWaterDomain.Contains(a,point));
    Assert.True(MapWaterDomain.Contains(b,point));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t2:red" "${wa_r4_log_dir}/wa-r4-t2-red.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWaterDomainTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `Normalize(...)` and `Contains(...)` in `MapWaterDomain.cs` using the exact spec edge/vertex rule and finite coordinates. No hidden epsilon widens domains. Deterministic polygon triangulation must preserve concavities, area and level, never fill a polygon's AABB. `MapWaterCompiler.Compile(...)` snapshots body records and emits clipped triangles using normalized boundaries, level and material. For polygon point containment use even/odd interior crossing plus the explicit boundary classification. Triangle shared-edge membership uses the same directed rule to prevent duplicate ownership. Add all rectangle corners, both windings, concave polygons, non-axis seam and distant negative-coordinate tests. Compare union area and dense fixed test points to polygon membership without a stress loop. Draw mesh boundaries are closed geometry, raster ownership is half-open, both use the same domain contract.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t2:green" "${wa_r4_log_dir}/wa-r4-t2-green.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWaterDomainTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Water/MapWaterDomain.cs KhaozEngine.MapDoc.Physics/MapWaterCompiler.cs KhaozEngine.MapDoc.Physics.Tests/NativeWaterDomainTests.cs
git diff --cached --check
git commit -m "feat(mapdocphysics): compile bounded water domains"
```


### Task 3: Feet-aware medium and complete capture integration

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapWaterSampler.cs`
- Modify: `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs`, `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs`, `KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs` from R3
- Test: `KhaozEngine.MapDoc.Physics.Tests/NativeWaterMediumTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/NativeWaterCaptureTests.cs`

**Interfaces:**
- Consumes existing: MovementMedium constructor and GroundMoveContext medium delegate, PhysicsNavBake.Capture and SampleWater.
- Consumes Task 2 compiled bodies and R3 complete static context.
- Produces new: `MapWaterMediumSample(string? BodyId,string? MediumKey,float SurfaceY,bool InWater,float WadeSpeedScale)`.
- Produces new: `MapWaterSampler(IReadOnlyList<MapCompiledWaterBody> bodies,IReadOnlyDictionary<string,MapWaterMediumDoc> media)`, `Sample(float worldX,float worldZ,float feetY) -> MapWaterMediumSample`, `MediumAt(float worldX,float worldZ,float feetY) -> MovementMedium`.
- Produces additive: `MapBuiltWorld.WaterBodies`, `WaterSampler`. CreateMoveContext supplies WaterSampler.MediumAt in Bounded mode and explicit legacy medium only for analytic LegacyGlobal.
- Test helper new: `NativeWaterCaptureFixture.Create() -> (MapBuiltWorld World,PhysicsNavBakeOptions Options,NavAreaClassifier Classify,MoveTuning Tuning)`, SampleWater=true and submerged bed/deck sample assertions.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeWater_FeetAtSurfaceAndBridgeDeckAreDry()
{
    var f = NativeWaterFixtures.Bridge();
    var world = MapWorldBuilder.Build(f.Document,f.Assets,f.Options);
    Assert.True(world.WaterSampler.MediumAt(0,-1,-0.38f).InWater);
    Assert.False(world.WaterSampler.MediumAt(0,-1,-0.37f).InWater);
    Assert.False(world.WaterSampler.MediumAt(0,-1,0.4f).InWater);
    Assert.False(world.WaterSampler.MediumAt(50,50,-10).InWater);
    var river = world.WaterSampler.Sample(0,-1,-0.38f);
    Assert.Equal("river-a",river.BodyId);
    Assert.Equal(-0.37f,river.SurfaceY);
    Assert.Equal(1f,world.WaterSampler.MediumAt(0,-1,0.4f).WadeSpeedScale);
    Assert.Equal(1.2f,world.WaterSampler.Sample(2.5f,-1.5f,1).SurfaceY);
    f.Document.WaterBodies.Clear();
    var dry = MapWorldBuilder.Build(f.Document,f.Assets,f.Options);
    Assert.Empty(dry.WaterBodies);
    Assert.False(dry.WaterSampler.MediumAt(0,-1,-100).InWater);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t3:red" "${wa_r4_log_dir}/wa-r4-t3-red.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWaterMediumTests|FullyQualifiedName~NativeWaterCaptureTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement Sample/MediumAt in `MapWaterSampler.cs` with immutable spatial indexing over the Task 2 exact domains. Feet must be strictly below SurfaceY to be wet. Dry returns MovementMedium.Dry, body key diagnostics remain in MapWaterMediumSample. Reject nonfinite query inputs. The builder snapshots water independently of terrain and includes water/media versions and parameters in native identity. CreateMoveContext uses the same medium for movement and complete nav capture, retaining deck statics and terrain-excluding movement queries. No global water plane is emitted in Bounded mode, even with zero bodies. Add independent levels at -0.37/1.2, submerged bed, narrow river edges, zero-body dry map, exact level equality, world-origin rebase, medium parameter and two-head equality tests. Capture with SampleWater=true must assert river surface entries and dry deck traversal, while false never calls medium. Add an instrumented sampler test for that contract.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t3:green" "${wa_r4_log_dir}/wa-r4-t3-green.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWaterMediumTests|FullyQualifiedName~NativeWaterCaptureTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Physics/MapWaterSampler.cs KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs KhaozEngine.MapDoc.Physics.Tests/NativeWaterMediumTests.cs KhaozEngine.MapDoc.Physics.Tests/NativeWaterCaptureTests.cs
git diff --cached --check
git commit -m "feat(mapdocphysics): share bounded water medium and capture"
```


### Task 4: Body lifecycle, boundary and level commands

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapWaterEdits.cs`, `KhaozEngine.MapEditor/NativeWaterCommands.cs`, `KhaozEngine.MapEdit.Tool/MutationServiceWater.cs`
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativeWaterCommandTests.cs`
- Modify: `KhaozEngine.MapEdit.Tool/README.md:3-28` at native command service section

**Interfaces:**
- Produces additive: `MapNativeEditResult.WaterBodyIds { get; init; }` as `IReadOnlyList<string>` default empty. Water commands fill this property, retain SurfaceIds/PlacementIds for truly affected terrain/placement owners.
- Consumes R2 MapNativeEditResult/cache flags and IEditorCommand/history transaction boundary.
- Produces new: `MapWaterEdits.ReplaceBoundary(MapWaterBodyDoc body,IReadOnlyList<MapWaterDomainDoc> domains) -> MapWaterBodyDoc`.
- Produces new: `AddWaterBodyCommand(MapWaterBodyDoc body)`, `ReplaceWaterBodyCommand(string bodyId,MapWaterBodyDoc body)`, `RemoveWaterBodyCommand(string bodyId)` all : EditorCommand.
- Produces new: `MutationService.WaterBodyAdd(MapWaterBodyDoc body) -> MapNativeEditResult`, `WaterBodySet(string bodyId,MapWaterBodyDoc body) -> MapNativeEditResult`, `WaterBodyRemove(string bodyId) -> MapNativeEditResult`.
- Produces new: `NativeWaterQueryService.Get(MapDocument document,string bodyId) -> MapWaterBodyDoc`, `List(MapDocument document) -> IReadOnlyList<MapWaterBodyDoc>` returning defensive copies.
- Test helper new: `NativeWaterCommandFixture : IDisposable` with Document, Service, Editor, State(), replacing bodies through identical command factories.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeWaterCommands_TerrainEditCannotChangeSavedLevel()
{
    using var f = new NativeWaterCommandFixture();
    float y = f.Document.WaterBodies[0].SurfaceY;
    var s = f.Document.Surfaces[0];
    var raised = MapSurfaceEdits.SetHeights(s,0,0,1,1,new[] { 2500 });
    f.Editor.Execute(new ReplaceNativeSurfaceCommand(s.Id,raised));
    Assert.Equal(y,f.Document.WaterBodies[0].SurfaceY);
    string before = f.State();
    var bad = NativeWaterQueryService.Get(f.Document,f.Document.WaterBodies[0].Id);
    bad.SurfaceY = float.NaN;
    Assert.Throws<InvalidOperationException>(() => f.Service.WaterBodySet(bad.Id,bad));
    Assert.Equal(before,f.State());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t4:red" "${wa_r4_log_dir}/wa-r4-t4-red.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWaterCommandTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced signatures in the named files using prepare/validate/publish. Add/set/remove preserve stable IDs and return affected IDs, union old/new dirty bounds, new identity and cache effects Physics/Nav/Material/Residency, with terrain rendering dependency explicitly refreshed when it previews shoreline. Set SurfaceY is an explicit edit, never a rim recomputation. Boundary replacement normalizes domains and rejects overlap atomically. GUI command and typed service invoke the same commands. Add create/get/list/set/remove, media/material edits, undo/redo, rejection preserving history/dirty state, save/reload and exact GUI/service hash/bounds parity tests. R9 adds pointer-driven boundary brushing and R10 registers all MCP water verbs. Add a preview descriptor API through MapBuiltWorld water triangles and diagnostics, not a second water model.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t4:green" "${wa_r4_log_dir}/wa-r4-t4-green.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWaterCommandTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Editing/MapWaterEdits.cs KhaozEngine.MapEditor/NativeWaterCommands.cs KhaozEngine.MapEdit.Tool/MutationServiceWater.cs KhaozEngine.MapEditor.Tests/MapDoc/NativeWaterCommandTests.cs KhaozEngine.MapEdit.Tool/README.md
git diff --cached --check
git commit -m "feat(mapedit): add transactional bounded water commands"
```


### Task 5: Seven-body exhaustive source and domain fidelity proof

**Files:**
- Modify: `KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj` from R2, adding only MapDoc.Physics for shared compiler/medium assertions
- Create: `KhaozEngine.MapDoc.Compatibility.Tests/FrozenWaterFixture.cs`, `KhaozEngine.MapDoc.Compatibility.Tests/BoundedWaterParityTests.cs`, `Fixtures/HollowmereWater.json` and provenance
- Modify: `KhaozEngine.MapDoc.Physics/README.md`, `KhaozEngine.MapDoc/README.md:1-26`, `docs/USING-KHAOZENGINE.md:56-81`
- Test: `KhaozEngine.MapDoc.Compatibility.Tests/BoundedWaterParityTests.cs`

**Interfaces:**
- Consumes existing: `TileWaterBodies.Collect(TileWorldDocument document, TileWorldCatalogs catalogs, RegionCoord region, int plane)` as source semantics oracle. Exact return type is `IReadOnlyList<TileWaterBody>` and source bodies expose Rects/SurfaceY. These are test-only references.
- Consumes Tasks 1 to 4 compiler/sampler/command contracts.
- Produces test fixture: `FrozenWaterFixture.Load() -> FrozenWaterFixture`, NativeDocument, native assets/options, `SourceBodyIds`, `SourceRectangles`, `CompareSourceInteriorSamples() -> IReadOnlyList<string>`, `BoundaryDifferences() -> IReadOnlyList<MapWaterBoundaryDifference>`, `ExpectedBoundaryDifferences` as checked complete fixture rows. Produce `MapWaterBoundaryDifference(string SourceKey, Vector3 SamplePoint, string OldResult, string NewResult, string PolicyId, string? NamedAcceptanceRef)` in FrozenWaterFixture.cs. PolicyId OA7 approves the boundary rule only. Actual case acceptance remains absent until separately recorded at R11/game import.
- NativeDocument is a format-6 document with exactly seven stable bodies, 113 rectangles, every source level -0.37 and all medium/material references frozen.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void BoundedWater_AllSevenBodiesAnd113RectanglesRoundtripExactly()
{
    var f = FrozenWaterFixture.Load();
    Assert.Equal(7,f.NativeDocument.WaterBodies.Count);
    Assert.Equal(113,f.NativeDocument.WaterBodies.Sum(b => b.Domains.Count));
    Assert.All(f.NativeDocument.WaterBodies,b => Assert.Equal(-0.37f,b.SurfaceY));
    var copy = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.NativeDocument));
    Assert.Equal(f.SourceBodyIds,copy.WaterBodies.Select(b => b.Id).ToArray());
    Assert.Equal(f.SourceRectangles,copy.WaterBodies.SelectMany(b => b.Domains).ToArray());
    Assert.Empty(f.CompareSourceInteriorSamples());
    Assert.Equal(f.ExpectedBoundaryDifferences,f.BoundaryDifferences());
    Assert.All(f.BoundaryDifferences(),d =>
    {
        Assert.Equal("OA7",d.PolicyId);
        Assert.NotEmpty(d.SourceKey);
        Assert.NotEqual(d.OldResult,d.NewResult);
        Assert.Null(d.NamedAcceptanceRef);
    });
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t5:red" "${wa_r4_log_dir}/wa-r4-t5-red.log" -- dotnet test KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~BoundedWaterParityTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the frozen fixture from the game evidence commit `74f57ee22652bd18234b4479faba4f1898a17047` with the per-file and sorted aggregate digests used in R2, preserving source region/body keys and all 113 rectangles rather than union-merging bodies. Materialize source levels once, with no runtime TileWorld or rim dependency. Store a deterministic expected record per source body/rectangle and compare IDs, domain coordinates, heights, media and materials exactly. Validate native draw-triangle domain coverage and medium/capture support at all frozen cell centers, seams, dry bridge and independent-level synthetic cases. Document exact-boundary normalization differences explicitly, not as a waived source loss. Game owner acceptance of import differentials is R11. Assert native assemblies have no TileWorld references. Update living docs and perform round verification. This round makes water independently authorable through commands and shared descriptors, actual backend pixels are R8.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-t5:green" "${wa_r4_log_dir}/wa-r4-t5-green.log" -- dotnet test KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~BoundedWaterParityTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj KhaozEngine.MapDoc.Compatibility.Tests/FrozenWaterFixture.cs KhaozEngine.MapDoc.Compatibility.Tests/BoundedWaterParityTests.cs KhaozEngine.MapDoc.Compatibility.Tests/Fixtures/HollowmereWater.json KhaozEngine.MapDoc.Physics/README.md KhaozEngine.MapDoc/README.md docs/USING-KHAOZENGINE.md
git diff --cached --check
git commit -m "test(mapdoc): prove bounded water fixture fidelity"
```


## Round Verification and Handoff

Run from the implementation worktree root, sequentially. Focused tests above are the task red/green cycle. The full solution suite runs once at round finish after the solution build, not once per task and never in a repeat loop. Re-run only when a subsequent code change or integration conflict requires it. The slot runner returns the target exit code. Exit 75 means no command ran because the slot was busy. Hand that result to the controller, never retry a failed test or loop verification.

```bash
mkdir -p local-feed "${wa_r4_log_dir}"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-focused-KhaozEngine.MapDoc.Physics.Tests:finish" "${wa_r4_log_dir}/wa-r4-focused-KhaozEngine.MapDoc.Physics.Tests-finish.log" -- dotnet test KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWater"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-focused-KhaozEngine.MapEditor.Tests:finish" "${wa_r4_log_dir}/wa-r4-focused-KhaozEngine.MapEditor.Tests-finish.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeWaterCommandTests"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-focused-KhaozEngine.MapDoc.Compatibility.Tests:finish" "${wa_r4_log_dir}/wa-r4-focused-KhaozEngine.MapDoc.Compatibility.Tests-finish.log" -- dotnet test KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~BoundedWaterParityTests"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-build:finish" "${wa_r4_log_dir}/wa-r4-build-finish.log" -- dotnet build KhaozEngine.slnx -c Release
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-format:finish" "${wa_r4_log_dir}/wa-r4-format-finish.log" -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-suite:finish" "${wa_r4_log_dir}/wa-r4-suite-finish.log" -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-check-dashes:finish" "${wa_r4_log_dir}/wa-r4-check-dashes-finish.log" -- sh scripts/check-dashes.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-check-prose:finish" "${wa_r4_log_dir}/wa-r4-check-prose-finish.log" -- sh scripts/check-prose.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-check-file-size:finish" "${wa_r4_log_dir}/wa-r4-check-file-size-finish.log" -- sh scripts/check-file-size.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-check-agent-instructions:finish" "${wa_r4_log_dir}/wa-r4-check-agent-instructions-finish.log" -- sh scripts/check-agent-instructions.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r4-check-doc-versions:finish" "${wa_r4_log_dir}/wa-r4-check-doc-versions-finish.log" -- bash scripts/check-doc-versions.sh
```

Require exit 0 from every command, zero warnings, nonempty focused selections, no format diff and no guard failures. These commands are future implementation verification, not authorization to run tests in the documents lane. GPU facts are skipped by ordinary `dotnet test`. Any visual golden additions use the relevant backend CI bake from `docs/CROSS-PLATFORM.md`, serialized and without booting a consumer. No local stress or repeated suite runs.

The last task also updates the package README, `docs/USING-KHAOZENGINE.md` and every stale Markdown reference for its added APIs. An orchestrator re-reads main, tags and `Directory.Build.props`, selects the next available engine minor after pivot releases, rides an existing staged version only when it belongs to this same round, and updates `CHANGELOG.md` plus all declarations checked by `check-doc-versions.sh`. Delegated workers record verified commits in Outcome and return them for integration. Each round is its own minor capability release and does not share a pivot or another round's release number. No engine release number is reserved here and no worker tags. Build, test and guard failures block the round's exit claim.

## Self-Review

Coverage: C4 schema/mode/validation Task 1, normalized clipped domain Task 2, feet-aware medium/render-input/capture agreement Task 3, level/boundary/lifecycle commands Task 4, exact seven-body fixture and differential reporting Task 5. No camera or old-rim dependency enters the native runtime. Every task has one test cycle and a reviewable deliverable. Existing interfaces were checked at the evidence SHA, new interfaces are explicitly produced before consumption, and the five Review Focus cases each have a named assertion in an owning task. The snippets pin behavior rather than implement algorithms. No later round's complete GUI, MCP, importer or rendering workflow is claimed here.

## Outcome

### Documentation reconciliation, 2026-10-05

- Approval stage: specs approved with revised T4. R4 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: Reconcile the released prerequisite APIs and record their actual SHAs before owner plan review. R3 also needs explicit revised-T4 geometry/query refinement. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: source and planning review only, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: OA7 boundary policy is approved. The historical boundary fixture reports keyed old/new cases and explicitly absent named acceptance, rather than accepting a policy label as a blanket waiver. Actual import acceptance remains open.
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
