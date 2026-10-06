# World Authoring R2: Exact Authored Terrain and Paint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add absolute authored terrain with one canonical triangle surface for floor sampling, paint, statics and later rendering.

**Architecture:** Format 5 separates authored surfaces from the analytic terrain path. A GPU-free compiler resolves integer height lattices, exact paint topology, support roles and masks. Native commands edit these DTOs transactionally and invalidate every dependent cache together.

**Tech Stack:** C# on the repository's existing .NET target, System.Numerics, System.Text.Json, closed JSON Schema, xUnit, existing engine seams. No new third-party dependency.

**Spec:** `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md`, spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. Round-plan approval remains pending. Read C2, T1 to T9 and the evidence register before implementation.

## Approval and execution gate, reconciled 2026-10-05

The exact owner answer supplied by the controller is "Approve". It approves both specs, T1 to T9 with revised T4, the rigid prefab v1 scope and pre-OA9 12 to 18 elapsed-week estimate, and C4 exact-boundary differential policy. That earlier policy answer did not approve round execution or any actual changed query result. OA8 subsequently approves R1 and the controller owns its active execution. R2 onward remain unapproved and need OA9 refinement before review. Named import-time acceptance of changed targets, distances, occlusion, stances and water boundaries remains required. R1 to R4 retain existing full plans, this branch holds the R1 planning copy only.

Future verification uses HANDOFF's shared slot runner, restored only if absent. Set a unique log directory from the implementation worktree before Task 1, and retain different red/green log names. These are instructions, not commands run by this documents lane.

```bash
wa_r2_log_dir="/tmp/grimhollow-orch/logs/wa-r2-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p local-feed "$wa_r2_log_dir"
test -f /tmp/grimhollow-orch/slot-run.sh
```

The 1 m allowance is a minimum vertical target reach-envelope height, preserving `MinimumObjectReachHeight`. It is not a 1 m action distance. Action range remains existing game policy and physical colliders never expand. Approval IDs are OA4 specs, OA5 revised T4, OA6 prefab/estimate and OA7 water boundary in the game DECISIONS record.

## OA9 checkpoint, before R2 approval

[DG9.1, DG9.4 and DG9.6](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#dg91-cave-representation-before-r2-approval) are mandatory refinement gates. OA8 R1 approval persists. This R2 plan remains unapproved and its existing surface DTOs/tasks must be reconciled with the chosen cave contract before execution.

- **Decision owner, R2 refinement:** Compare native cave layers with their own floors/ceilings against authored native cave prefabs with collision/nav. Record weighted trade-offs for support/nav, water containment, authoring, residency and coordinate precision, then secure the choice before R2 approval. Both options remain fully native, with no TileWorld hybrid. If prefabs are chosen, [R5](2026-10-05-world-authoring-r5-free-buildings-prefabs-interiors.md) owns their implementation, while R2 pins required surface/support interfaces.
- **Operative constraint:** Deep multi-level caves far below the surface can be dry or flooded. One surface lattice is not a complete cave. Historical four-plane lifts `[0,450,900,1350]` cm preserve import semantics only, not native depth/spacing/layer limits. Keep exact imported corners/cuts/paint and zero alternate-floor discrepancy.
- **Task ownership:** Refine Tasks 1 to 3 geometry roles, floor/ceiling ownership, layer selection and local support queries before approval. `HeightAt(X,Z)`/a single groundSurfaceId cannot alone select support for stacked cave levels. Bind [R3](2026-10-05-world-authoring-r3-shared-shapes-headless-builders.md) collision/picking/nav, [R4](2026-10-05-world-authoring-r4-bounded-water-medium.md) containment, [R5](2026-10-05-world-authoring-r5-free-buildings-prefabs-interiors.md) volumes and [R8](2026-10-05-world-authoring-r8-native-rendering-residency-captures.md) residency/precision interfaces explicitly.
- **Named future proofs:** `NativeCaveRepresentationContract` pins the selected DTOs/ownership. `StackedCaveFloorsAndCeilings_PreserveGeometryAndSupportSelection` checks vertically overlapping levels, ceiling clearance and no surface-floor substitution. `LegacyTerrainImport_RemainsExactWithCaveModel` retains the frozen legacy oracle. Quantify supported extents/depth/minimum precision and refresh the OA9 estimate before approval, then again at the R2 exit.

## R1 transaction carry-forward, before R2 approval

Coordinator CD1 under owner delegation OA17 approves the revised cave/scale design through
0bd1d69d8 as the basis for rewriting this full plan. It does not approve this stale task structure
or implementation. The rewrite must carry accepted F3 producer boundaries, #458 canonical
membership/connectivity consumers, #1310's deterministic prerequisite, exact fine-patch geometry
and material preservation or declared authored differences, and mandatory downstream allocation.
R2 emits canonical facts. Swimming owns generic movement consumers and pivot owns #458 policy.
No floor-key equality or global Y threshold may substitute for the selected space/portal geometry.

R1 Task 5 introduces an atomic native placement command protocol. Native history refuses other
command types before mutation, while direct MCP mutation callbacks validate a detached document.
R2 must extend the transaction protocol for native terrain commands, preserving bound closure
validation, failed-command retry, history/dirty/events and GUI/MCP equivalence. Do not route terrain
around the protocol merely to avoid the current refusal. Reconcile the actual owner-released R1
APIs and tests before approving the R2 implementation plan. [#1302](https://github.com/APKiwiOrg/KhaozEngine/issues/1302) tracks document-size copying for R9.

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

- A diagonal-half cut without an overlay still forces its authored diagonal (Task 2).
- Negative-Z rows and shared corners at storage boundaries must agree exactly (Tasks 1 and 6).
- NoDraw, void and empty upper surfaces must not manufacture capture geometry or extra floors (Task 3).
- A transformed local paint patch must decorate its target without becoming a second support floor (Task 3).
- A rejected multi-corner brush or invalid material must leave document, history and dirty bounds unchanged (Task 5).

---

## File Structure

| Path | Responsibility |
| --- | --- |
| `KhaozEngine.MapDoc/Surfaces/MapSurfaceDoc.cs`, `KhaozEngine.MapDoc/Surfaces/MapMaterialDoc.cs`, `KhaozEngine.MapDoc/Surfaces/MapSurfaceValidator.cs` | Absolute heights, cells, material bindings and source mode |
| `KhaozEngine.MapDoc/Surfaces/MapSurfaceTopology.cs`, `KhaozEngine.MapDoc/Surfaces/MapSurfaceCompiler.cs`, `KhaozEngine.MapDoc/Surfaces/MapCompiledSurface.cs` | Canonical triangles, paint ownership and geometric normals |
| `KhaozEngine.MapDoc/Surfaces/MapSurfaceSampler.cs`, `KhaozEngine.MapDoc/Surfaces/MapSurfacePaint.cs` | Triangle sampling and non-colliding paint patches |
| `KhaozEngine.MapDoc/Volumes/MapIndoorMaskCompiler.cs`, `KhaozEngine.MapDoc/Volumes/MapVolumeMembership.cs` | Connected indoor mask prisms and shared membership |
| `KhaozEngine.MapDoc/Editing/MapSurfaceEdits.cs`, `KhaozEngine.MapDoc/Editing/MapNativeEditResult.cs` | Pure bounded edits and cache invalidation descriptors |
| `KhaozEngine.MapEditor/NativeSurfaceCommands.cs` | Undoable commands used by both frontends |
| `KhaozEngine.MapEdit.Tool/MutationServiceSurfaces.cs` | Typed native service operations |
| `KhaozEngine.MapDoc.Tests/` | Headless native surface tests |
| `KhaozEngine.MapDoc.Compatibility.Tests/` | Offline differential tests referencing MapDoc and TileWorld, never a core dependency |


## Source-Checked Contract and Judgement Calls

R1 produces `MapTransform`, asset closure, native resolver and authored identity. Reuse those exact names. `TileTriangulation.SplitSwNe(short h00, short h10, short h01, short h11, TileOverlayShape shape, int overlayRotation)` chooses `abs(h00-h11) <= abs(h10-h01)`, except DiagonalHalf forces even-rotation SW-NE and odd NW-SE (`TileTriangulation.cs:49-60`). `TileGroundTriangles.TryDescribe(TileWorldDocument document, int worldX, int worldZ, int plane, out TileGroundCell cell, Span<TileLatticeTriangle> triangles)` and `Build(TileWorldDocument document, RegionCoord region, int plane)` are the offline oracle only. The new compiler implements that rule without a TileWorld dependency.

The exact spike cell at source (4,64) has corner centimetres `[1433,1331,1363,518]` in SW, SE, NW, NE order, material 14 and no overlay/cut/rotation/flags. These values were decoded read-only from historical proposal source `regions/r_0_1.json`. At world (4.37,-64.61), choose the NW-SE triangle. The expected height is computed from the same float corner conversion and barycentric weights, not a rounded hardcoded decimal. A source-short height is `height * 0.01f`, as `TileGroundTriangles.cs:162-165` does.

Native row direction is explicit. World surfaces normally use NegativeZ to preserve source indexing, while other native surfaces may use PositiveZ. Storage TileSize remains independent of CellSize. Integer HeightUnits allow centimetres or finer positive finite units, with no analytic deltas. Indoor span is explicit per mask surface, no guessed global plane height. Legacy void/NoDraw fallback is explicitly tagged non-capture support and may be bilinear only there, bounded/clamped as before. Drawn terrain never calls a bilinear fallback.

### J2.1 shared smoothing ruling, 2026-10-05

This technical consistency ruling supersedes the old R2 four-neighbour/1-to-16 proposal. Released main `b39fb1a3b` has `TileEditOps.Smooth(TileWorldDocument doc, TileRect cornerRect, int plane, int iterations)` in `KhaozEngine.TileWorld.Editing/TileEditOps.Heights.cs:69-109`. It accepts 1 to 64, reads a double-buffered 3x3 neighbourhood including the centre, reads outside-patch halo unchanged, and quantizes AwayFromZero after each pass. `KhaozEngine.TileEdit.Tool/Tools/HeightTools.cs:60` documents the same bound. R2 and R10 use one shared bounded operation and preserve those legacy semantics. The range is algorithm work within one call, never a test-execution loop.

Task 5 adds explicit assertions: inputs 1, 16, 17 and 64 succeed, 0 and 65 throw before mutation, a one-corner patch with value 9 and zero-valued eight-corner halo becomes 1 after one pass, the next pass rounds 1/9 to 0, halo bytes stay zero, and direct GUI command/MCP service documents and dirty bounds match. At R10 refinement add equivalent wire-level assertions for 17/64 and invalid 0/65, rather than inventing a second cap. Shared surface boundaries supply the declared canonical halo, or refuse unavailable/ambiguous data before editing.

### Task 1: Format-5 absolute surfaces, materials and validation

**Files:**
- Create: `KhaozEngine.MapDoc/Surfaces/MapSurfaceDoc.cs`, `KhaozEngine.MapDoc/Surfaces/MapMaterialDoc.cs`, `KhaozEngine.MapDoc/Surfaces/MapSurfaceValidator.cs`, `KhaozEngine.MapDoc/Surfaces/MapAuthoredTerrainMigration.cs`
- Modify: `KhaozEngine.MapDoc/MapNativeDocument.cs` from R1, `KhaozEngine.MapDoc/MapDocumentFile.cs:28-83`, `KhaozEngine.MapDoc/MapDocumentValidator.cs:14-27`, `KhaozEngine.MapDoc/MapCanonical.cs:51-76`, `KhaozEngine.MapDoc/MapTiledFile.cs:91-150`, `KhaozEngine.MapDoc/mapdoc.schema.json`
- Test: `KhaozEngine.MapDoc.Tests/NativeSurfaceDocumentTests.cs`, `KhaozEngine.MapDoc.Tests/NativeSurfaceFixtures.cs`

**Interfaces:**
- Consumes R1: `MapTransform`, `MapAssetClosure`, `MapNativeMigration.Upgrade(JsonObject source)`.
- Produces new: `MapTerrainSource { Analytic, Authored }`, root `TerrainSource`, `List<MapSurfaceDoc> Surfaces`, `List<MapMaterialDoc> Materials`.
- Produces new: `MapSurfaceDoc` with payload 1, stable Id, `Vector3 Origin`, `MapRowDirection RowDirection`, positive `float CellSize`, `int Width`, `int Depth`, `float HeightUnitMetres`, `int[] HeightUnits`, `MapSurfaceCell[] Cells`, `MapSurfaceRole Role`, `MapTransform Transform`, optional TargetSurfaceId and IndoorSpan.
- Produces new: `MapRowDirection { NegativeZ, PositiveZ }`, `MapSurfaceRole { TerrainSupport, RigidSupport, PaintOverride }`, `MapVerticalSpan(float MinY, float MaxY)`.
- Produces new: `MapSurfaceCell(ushort Underlay, ushort Overlay, MapOverlayCut Cut, byte Rotation, MapSurfaceFlags Flags, byte ReservedMetadata)`, cuts Full=0, DiagonalHalf=1, CornerQuarter=2, CornerThreeQuarter=3. Flags None=0, Blocked=1, Indoor=2, NoDraw=8, FeatherOverlay=16. Legacy Bridge=4 is preserved in ReservedMetadata, not promoted to a native support flag.
- Produces new: `MapMaterialDoc(ushort Id, string ResourceId, float RepeatsPerMetre, string PhysicalMaterialKey, string MediumKey)`.
- Produces new: `MapSurfaceValidator.Validate(MapDocument document) -> IReadOnlyList<string>`, `ValidateClosure(MapDocument document,MapAssetClosure assets) -> IReadOnlyList<string>`, pure `MapAuthoredTerrainMigration.Upgrade(JsonObject source) -> JsonObject`.
- Test helper new: `NativeSurfaceFixtures.SpikeCell() -> MapSurfaceDoc`, `Materials() -> IReadOnlyDictionary<ushort,MapMaterialDoc>`, `Document() -> MapDocument` with payload-1 material resources.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeSurface_UnitsRowsAndSourceModeAreExplicit()
{
    var doc = NativeSurfaceFixtures.Document();
    var surface = NativeSurfaceFixtures.SpikeCell();
    doc.Surfaces.Add(surface);
    Assert.Equal(new[] { 1433, 1331, 1363, 518 }, surface.HeightUnits);
    Assert.Equal(0.01f, surface.HeightUnitMetres);
    Assert.Equal(MapRowDirection.NegativeZ, surface.RowDirection);
    Assert.Empty(MapSurfaceValidator.Validate(doc));
    surface.HeightUnits = new int[3];
    Assert.NotEmpty(MapSurfaceValidator.Validate(doc));
    surface.HeightUnits = new[] { 1433, 1331, 1363, 518 };
    doc.TerrainOverrides = new MapTerrainOverrides();
    Assert.NotEmpty(MapSurfaceValidator.Validate(doc));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t1:red" "${wa_r2_log_dir}/wa-r2-t1-red.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceDocumentTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced DTOs and validator signatures in the named files. Dimensions require `(Width+1)*(Depth+1)` corners and `Width*Depth` cells using checked multiplication before allocations. Keep exact integer units and material IDs. Reject unsupported payloads/row values/cuts/rotations/flag bits, nonfinite origins/units/derived heights, missing resources, contradictory shared corners and duplicate IDs. Structural validation checks material IDs, and the resolver invokes ValidateClosure to check material resource bindings. Authored source forbids TerrainOverrides, Features and procedural scatter generation, while analytic DTO data stays stored for reversible explicit source conversion. Do not invoke analytic noise in authored mode. The 4 to 5 migration defaults old maps to Analytic and no surfaces, preserving hashes appropriate to the schema upgrade and old runtime geometry. Native surface/material lists are manifest globals in both storage forms for now, with extent membership derived independently. Add all four plane lifts `[0,450,900,1350]` cm and explicit upper overrides as fixture assertions, empty upper surfaces and 14 distinct material bindings.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t1:green" "${wa_r2_log_dir}/wa-r2-t1-green.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceDocumentTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Surfaces/MapSurfaceDoc.cs KhaozEngine.MapDoc/Surfaces/MapMaterialDoc.cs KhaozEngine.MapDoc/Surfaces/MapSurfaceValidator.cs KhaozEngine.MapDoc/Surfaces/MapAuthoredTerrainMigration.cs KhaozEngine.MapDoc/MapNativeDocument.cs KhaozEngine.MapDoc/MapDocumentFile.cs KhaozEngine.MapDoc/MapDocumentValidator.cs KhaozEngine.MapDoc/MapCanonical.cs KhaozEngine.MapDoc/MapTiledFile.cs KhaozEngine.MapDoc/mapdoc.schema.json KhaozEngine.MapDoc.Tests/NativeSurfaceDocumentTests.cs KhaozEngine.MapDoc.Tests/NativeSurfaceFixtures.cs
git diff --cached --check
git commit -m "feat(mapdoc): add absolute authored surface documents"
```


### Task 2: Exact topology, paint cuts and feather descriptors

**Files:**
- Create: `KhaozEngine.MapDoc/Surfaces/MapSurfaceTopology.cs`, `KhaozEngine.MapDoc/Surfaces/MapSurfaceCompiler.cs`, `KhaozEngine.MapDoc/Surfaces/MapCompiledSurface.cs`
- Test: `KhaozEngine.MapDoc.Tests/NativeSurfaceTopologyTests.cs`

**Interfaces:**
- Consumes R1: `MapTransform.TransformPoint(Vector3 local) -> Vector3`. Consumes Task 1 surface/material DTOs.
- Produces new: `MapSurfaceTopology.SplitSwNe(int sw, int se, int nw, int ne, MapOverlayCut cut, byte rotation) -> bool`.
- Produces new: `MapSurfaceCompiler.Compile(MapSurfaceDoc surface, MapTransform transform, IReadOnlyDictionary<ushort,MapMaterialDoc> materials) -> MapCompiledSurface`.
- Produces new: `MapSurfaceTriangle(string SurfaceId, Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, ushort MaterialId)` and immutable `MapCompiledSurface` with Id, Role, Transform, `IReadOnlyList<MapSurfaceTriangle> Triangles`, `IReadOnlyList<MapSurfaceTriangle> PaintTriangles`, plus bounds and captured input digest.
- Produces new: `MapSurfaceCompiler.CompileAll(MapDocument document) -> IReadOnlyList<MapCompiledSurface>`, using each surface Transform once.

- [ ] **Step 1: Write the failing test**

```csharp
[Theory]
[InlineData(0, true)]
[InlineData(1, false)]
[InlineData(2, true)]
[InlineData(3, false)]
public void NativeDiagonalHalfWithoutOverlayStillForcesSplit(byte rotation, bool expected)
{
    Assert.Equal(expected, MapSurfaceTopology.SplitSwNe(0, 100, 100, 100,
        MapOverlayCut.DiagonalHalf, rotation));
    var s = NativeSurfaceFixtures.SpikeCell();
    s.Cells[0] = new MapSurfaceCell(14, 0, MapOverlayCut.DiagonalHalf, rotation, MapSurfaceFlags.None, 0);
    var mesh = MapSurfaceCompiler.Compile(s, s.Transform, NativeSurfaceFixtures.Materials());
    Assert.Equal(2, mesh.Triangles.Count);
    Assert.All(mesh.Triangles, t => Assert.Equal((ushort)14, t.MaterialId));
    Assert.All(mesh.Triangles, t => Assert.True(t.Normal.Y > 0));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t2:red" "${wa_r2_log_dir}/wa-r2-t2-red.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceTopologyTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the compiler signatures in `MapSurfaceCompiler.cs`, factoring topology into `MapSurfaceTopology.cs`. Use widened integer subtraction for the least-height-difference diagonal, with equality selecting SW-NE. Paint absence selects Full triangles but does not erase a forced authored diagonal. Full/diagonal cuts emit two canonical triangles, corner cuts four. Midpoints are `(a+b)*0.5f`, geometric normals point up, and materials follow each triangle's cut ownership. Preserve all 16 cut/rotation combinations, underlay/overlay uint16 bindings, texture repeat and feather metadata. Feather paint subdivisions stay on the canonical triangle plane and never add a different support surface. Add theories for every cut/rotation, extreme int heights, stable topology order, zero overlay, and feathered-versus-unfeathered coplanarity. No coarsened surface LOD or five-channel splat conversion.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t2:green" "${wa_r2_log_dir}/wa-r2-t2-green.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceTopologyTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Surfaces/MapSurfaceTopology.cs KhaozEngine.MapDoc/Surfaces/MapSurfaceCompiler.cs KhaozEngine.MapDoc/Surfaces/MapCompiledSurface.cs KhaozEngine.MapDoc.Tests/NativeSurfaceTopologyTests.cs
git diff --cached --check
git commit -m "feat(mapdoc): compile canonical terrain and paint triangles"
```


### Task 3: Triangle sampler, local supports and floor paint ownership

**Files:**
- Create: `KhaozEngine.MapDoc/Surfaces/MapSurfaceSampler.cs`, `KhaozEngine.MapDoc/Surfaces/MapSurfacePaint.cs`
- Modify: `KhaozEngine.MapDoc/MapResolver.cs` support-snap call path from R1, `KhaozEngine.MapDoc/MapRuntime.cs:64-81` analytic entry guard
- Test: `KhaozEngine.MapDoc.Tests/NativeSurfaceSamplingTests.cs`, `KhaozEngine.MapDoc.Tests/NativeLocalFloorTests.cs`

**Interfaces:**
- Test helper new: `NativeSurfaceFixtures.LocalPaint() -> (MapCompiledSurface Target,MapSurfaceDoc Patch)` with a transformed PaintOverride bound to Target.Id and the same physical plane.
- Consumes Task 2 compiled surfaces and R1 resolver supportHeight delegate.
- Produces new: `MapSurfaceSample(string SurfaceId, float Height, Vector3 Normal, bool IsCaptureSupport)`.
- Produces new: `MapSurfaceSampler(IReadOnlyList<MapCompiledSurface> surfaces, MapBounds playableBounds, string groundSurfaceId, MapSurfaceFallback fallback)`.
- Produces new: `TrySample(string surfaceId, float worldX, float worldZ, out MapSurfaceSample sample) -> bool`, `HeightAt(float worldX,float worldZ) -> float`, `NormalAt(float worldX,float worldZ) -> Vector3`.
- Produces new: `MapSurfaceFallback(Func<float,float,float> Height, MapBounds Bounds, string Identity)` limited to non-drawn cells/outside support.
- Produces new: `MapSurfacePaint.Apply(MapCompiledSurface target, MapSurfaceDoc patch, IReadOnlyDictionary<ushort,MapMaterialDoc> materials) -> MapCompiledSurface`. Target support triangles remain identical, PaintTriangles alone change.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeSpikePointUsesTheCanonicalTriangle_NotBilinear()
{
    var s = NativeSurfaceFixtures.SpikeCell();
    var mesh = MapSurfaceCompiler.Compile(s, s.Transform, NativeSurfaceFixtures.Materials());
    var bounds = new MapBounds { MinX=4, MaxX=5, MinZ=-65, MaxZ=-64 };
    var sampler = new MapSurfaceSampler(new[] { mesh }, bounds, s.Id,
        new MapSurfaceFallback((_, _) => throw new Exception("unexpected fallback"), bounds, "test-refuse-fallback-v1"));
    Assert.True(sampler.TrySample(s.Id, 4.37f, -64.61f, out var floor));
    var t = mesh.Triangles.Single(t => t.A.Y != 518 * 0.01f && t.B.Y != 518 * 0.01f && t.C.Y != 518 * 0.01f);
    float expected = (1433 * 0.01f) * (1 - 0.37f - 0.61f)
        + (1331 * 0.01f) * 0.37f + (1363 * 0.01f) * 0.61f;
    Assert.InRange(MathF.Abs(expected - floor.Height), 0, 0.00001f);
    Assert.Equal(sampler.HeightAt(4.37f, -64.61f), floor.Height);
    Assert.Equal(t.Normal, floor.Normal);
    Assert.True(floor.IsCaptureSupport);
}
[Fact]
public void NativeNoDrawFallbackIsNonCaptureAndLocalPaintNeverAddsSupport()
{
    var s = NativeSurfaceFixtures.SpikeCell();
    s.Cells[0] = s.Cells[0] with { Flags = MapSurfaceFlags.NoDraw };
    var mesh = MapSurfaceCompiler.Compile(s,s.Transform,NativeSurfaceFixtures.Materials());
    Assert.Empty(mesh.Triangles);
    var bounds = new MapBounds { MinX=4,MaxX=5,MinZ=-65,MaxZ=-64 };
    var sampler = new MapSurfaceSampler(new[] { mesh },bounds,s.Id,
        new MapSurfaceFallback((_,_) => 7,bounds,"test-flat-void-v1"));
    Assert.True(sampler.TrySample(s.Id,4.5f,-64.5f,out var fallback));
    Assert.False(fallback.IsCaptureSupport);
    var f = NativeSurfaceFixtures.LocalPaint();
    var painted = MapSurfacePaint.Apply(f.Target,f.Patch,NativeSurfaceFixtures.Materials());
    Assert.Equal(f.Target.Triangles,painted.Triangles);
    Assert.Equal(f.Target.Role,painted.Role);
    Assert.NotEqual(f.Target.PaintTriangles,painted.PaintTriangles);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t3:red" "${wa_r2_log_dir}/wa-r2-t3-red.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceSamplingTests|FullyQualifiedName~NativeLocalFloorTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the sampler and paint signatures in the new files. Inverse-transform query XZ to the surface's local lattice, choose the canonical triangle by maximum minimum barycentric weight, then sample its exact vertices. Every consumer later receives this same descriptor/sample. Fallback requires a nonempty versioned Identity included in the authored build options hash. It is clamped to its declared bounds, tagged non-capture and never expands playable bounds. Compile no capture triangles for void/NoDraw or empty upper layers. PaintOverride requires a valid target, clips to target triangles and has no support ownership. RigidSupport alone contributes new support geometry. Assert transformed floor yaw 0.371, offset (0.23,0.17), scale 1.137, inverse query and normal agreement, double-owner rejection, moved paint not rewriting world cells, NoDraw fallback tagging, empty upper surfaces and unsupported BuildField calls in authored mode. Keep old analytic BuildField behavior unchanged.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t3:green" "${wa_r2_log_dir}/wa-r2-t3-green.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceSamplingTests|FullyQualifiedName~NativeLocalFloorTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Surfaces/MapSurfaceSampler.cs KhaozEngine.MapDoc/Surfaces/MapSurfacePaint.cs KhaozEngine.MapDoc/MapResolver.cs KhaozEngine.MapDoc/MapRuntime.cs KhaozEngine.MapDoc.Tests/NativeSurfaceSamplingTests.cs KhaozEngine.MapDoc.Tests/NativeLocalFloorTests.cs
git diff --cached --check
git commit -m "feat(mapdoc): sample the authored triangle surface"
```


### Task 4: Blocked masks and shared indoor membership

**Files:**
- Create: `KhaozEngine.MapDoc/Volumes/MapIndoorMaskCompiler.cs`, `KhaozEngine.MapDoc/Volumes/MapVolumeMembership.cs`
- Test: `KhaozEngine.MapDoc.Tests/NativeSurfaceMaskTests.cs`

**Interfaces:**
- Consumes Task 1 cell flags, MapVerticalSpan, row direction and Transform.
- Produces new: `MapPolygonPrism(string Id, IReadOnlyList<Vector2> Ring, float MinY, float MaxY)`, immutable world-space ring.
- Produces new: `MapIndoorMaskCompiler.Compile(MapSurfaceDoc surface) -> IReadOnlyList<MapPolygonPrism>`.
- Produces new: `MapVolumeMembership(IReadOnlyList<MapPolygonPrism> volumes)`, `At(Vector3 worldPoint) -> IReadOnlyList<string>` with [MinY,MaxY) vertical ownership.
- Produces new: `MapBlockedMask(string SurfaceId, IReadOnlyList<Vector2> CellRing, MapTransform Transform)` and `MapSurfaceCompiler.BuildBlockedMasks(MapDocument document) -> IReadOnlyList<MapBlockedMask>`. Medium-aware filtering is a later consumer option, not baked into terrain.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeIndoorMembership_UsesOneExplicitVerticalSpan()
{
    var s = NativeSurfaceFixtures.SpikeCell();
    s.IndoorSpan = new MapVerticalSpan(0, 4.5f);
    s.Cells[0] = s.Cells[0] with { Flags = MapSurfaceFlags.Indoor | MapSurfaceFlags.Blocked };
    var volumes = MapIndoorMaskCompiler.Compile(s);
    var membership = new MapVolumeMembership(volumes);
    Assert.Single(membership.At(new Vector3(4.5f, 1, -64.5f)));
    Assert.Empty(membership.At(new Vector3(4.5f, 4.5f, -64.5f)));
    Assert.Single(MapSurfaceCompiler.BuildBlockedMasks(new MapDocument { Surfaces = new() { s } }));
    Assert.Equal(1, volumes.Count);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t4:red" "${wa_r2_log_dir}/wa-r2-t4-red.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceMaskTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `Compile(...)` and `At(...)` in the named files. Trace connected mask boundaries deterministically with hole-aware decomposition into simple prisms, retaining explicit vertical span and free transform. Missing span on an Indoor cell is invalid. Bind later prefab floor volumes through this same membership service in R5. BuildBlockedMasks retains blocked cells separately from support triangles and reserved Bridge metadata. Add L-shaped and hole fixtures, neighboring masks with shared edges, both row directions, span boundary tests, transformed membership and equality of per-cell coverage with prism union. Indoor classification is one shared service for both heads.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t4:green" "${wa_r2_log_dir}/wa-r2-t4-green.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceMaskTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Volumes/MapIndoorMaskCompiler.cs KhaozEngine.MapDoc/Volumes/MapVolumeMembership.cs KhaozEngine.MapDoc.Tests/NativeSurfaceMaskTests.cs
git diff --cached --check
git commit -m "feat(mapdoc): compile authored masks and indoor volumes"
```


### Task 5: Undoable height and paint operations with unified invalidation

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapSurfaceEdits.cs`, `KhaozEngine.MapDoc/Editing/MapNativeEditResult.cs`
- Create: `KhaozEngine.MapEditor/NativeSurfaceCommands.cs`, `KhaozEngine.MapEdit.Tool/MutationServiceSurfaces.cs`
- Modify: `KhaozEngine.MapEditor/EditorDocument.cs:120-168`, `KhaozEngine.MapEditor/EditorCommands.cs:30-61` for additive native effect metadata
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativeSurfaceCommandTests.cs`

**Interfaces:**
- Consumes existing: IEditorCommand.Apply/Revert, EditorDocument.Execute/Undo/Redo, MutationService.Apply(EditorCommand command,string verb,string detail) and its validation boundary.
- Produces new: `MapSurfaceEdits.SetHeights(MapSurfaceDoc surface,int x,int row,int width,int depth,IReadOnlyList<int> values) -> MapSurfaceDoc`.
- Produces new: `MapSurfaceEdits.Brush(MapSurfaceDoc surface, MapHeightBrush brush) -> MapSurfaceDoc`, `SetCells(MapSurfaceDoc surface,int x,int row,int width,int depth,IReadOnlyList<MapSurfaceCell> cells) -> MapSurfaceDoc`.
- Produces new: `MapHeightBrushKind { Set, Raise, Flatten, Smooth }` and `MapNativeCacheFlags` as a flags enum with None=0, Terrain=1, Physics=2, Nav=4, Material=8, Residency=16.
- Produces new: `MapHeightBrush(MapHeightBrushKind Kind, Vector2 Centre, float Radius, int? ValueUnits, float Hardness, int Iterations)` with Set/Raise/Flatten/Smooth.
- Produces new: `MapSurfaceEdits.ImportHeights(IReadOnlyList<float> samples,float sourceMin,float sourceMax,int targetMinUnits,int targetMaxUnits) -> int[]`, rejecting nonfinite values and sourceMin >= sourceMax, clamping normalized values to [0,1], AwayFromZero quantization.
- Produces new: `MapNativeEditResult(IReadOnlyList<string> SurfaceIds, IReadOnlyList<string> PlacementIds, MapBounds DirtyBounds, MapNativeCacheFlags Invalidates, string Identity, string UndoLabel, string RedoLabel)` and flags Terrain, Physics, Nav, Material, Residency.
- Produces new: `AddNativeSurfaceCommand(MapSurfaceDoc surface)`, `RemoveNativeSurfaceCommand(string surfaceId)`, `SetNativeMaterialCommand(MapMaterialDoc material)` all : EditorCommand. `MutationService.SurfaceLayerAdd(MapSurfaceDoc surface)`, `SurfaceLayerRemove(string surfaceId)`, `MaterialSet(MapMaterialDoc material)` each return MapNativeEditResult and refuse dangling paint/support/material references.
- Produces new: `MapSurfaceEdits.ReadHeights(MapSurfaceDoc surface,int x,int row,int width,int depth) -> int[]`, `ReadCells(MapSurfaceDoc surface,int x,int row,int width,int depth) -> IReadOnlyList<MapSurfaceCell>` with bounded row-major addressing.
- Produces new: `ReplaceNativeSurfaceCommand(string surfaceId, MapSurfaceDoc replacement) : EditorCommand`, `MapNativeCommands.ReplaceSurface(MapDocument document,string surfaceId,MapSurfaceDoc replacement) -> ReplaceNativeSurfaceCommand` used by GUI and service.
- Produces new: `MutationService.SurfaceReplace(string surfaceId,MapSurfaceDoc replacement) -> MapNativeEditResult`. Height/paint endpoints prepare through MapSurfaceEdits, then use SurfaceReplace.
- Test helper new: `NativeSurfaceCommandFixture : IDisposable` with `EditorDocument Editor`, `MutationService Service`, `MapDocument Document` and `string State()` capturing serialized doc/history/dirty bounds.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeSurfaceCommand_RejectedPatchDoesNotPublishAnyCorner()
{
    using var f = new NativeSurfaceCommandFixture();
    string before = f.State();
    var bad = MapSurfaceEdits.SetHeights(f.Document.Surfaces[0], 0, 0, 2, 2,
        new[] { 1, 2, 3, 4 });
    bad.Cells[0] = bad.Cells[0] with { Underlay = ushort.MaxValue };
    Assert.Throws<InvalidOperationException>(() => f.Service.SurfaceReplace(bad.Id, bad));
    Assert.Equal(before, f.State());
    var good = MapSurfaceEdits.SetHeights(f.Document.Surfaces[0], 0, 0, 1, 1, new[] { 123 });
    var result = f.Service.SurfaceReplace(good.Id, good);
    Assert.Equal(MapNativeCacheFlags.Terrain | MapNativeCacheFlags.Physics | MapNativeCacheFlags.Nav
        | MapNativeCacheFlags.Material | MapNativeCacheFlags.Residency, result.Invalidates);
    Assert.Contains(good.Id, result.SurfaceIds);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t5:red" "${wa_r2_log_dir}/wa-r2-t5-red.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceCommandTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced pure edit methods, command and service signatures in their named files. Edits deep-copy and prevalidate the full patch, units never change implicitly. Set/Raise require ValueUnits, Flatten may omit it, Smooth ignores it. Radius must be finite positive and Hardness in [0,1]. Flatten without an explicit value uses the selected-corner arithmetic mean rounded to integer units with MidpointRounding.AwayFromZero. Smooth is deterministic Jacobi, reads the prior iteration, uses the legacy 3x3 prior-pass average, 1 to 64 iterations, unchanged outside-patch halo and AwayFromZero per-pass integer quantization. Pin circular falloff as weight `clamp((radius-distance)/(radius*(1-hardness)),0,1)`, hardness 1 as a hard disc, quantizing the final height once. Boundary corners shared by multiple surfaces update atomically or reject inconsistent ownership. Paint patches change only supplied cell fields. History restores exact arrays and effects. Add layer add/remove, exact read/set, explicit image orientation/range conversion helper `ImportHeights(IReadOnlyList<float> samples,float sourceMin,float sourceMax,int targetMinUnits,int targetMaxUnits) -> int[]`, and material/flag/cut/rotation/feather operations over this same replacement boundary. Add GUI-command versus service document/hash/dirty-bound equality, undo/redo, partial-cell preservation and all brush operation tests. MCP verb registration and full GUI brushes belong to R9/R10, not this round.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t5:green" "${wa_r2_log_dir}/wa-r2-t5-green.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceCommandTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/Editing/MapSurfaceEdits.cs KhaozEngine.MapDoc/Editing/MapNativeEditResult.cs KhaozEngine.MapEditor/NativeSurfaceCommands.cs KhaozEngine.MapEdit.Tool/MutationServiceSurfaces.cs KhaozEngine.MapEditor/EditorDocument.cs KhaozEngine.MapEditor/EditorCommands.cs KhaozEngine.MapEditor.Tests/MapDoc/NativeSurfaceCommandTests.cs
git diff --cached --check
git commit -m "feat(mapedit): add transactional native surface commands"
```


### Task 6: Exhaustive terrain oracle and zero semantic discrepancy proof

**Files:**
- Create: `KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj`, `KhaozEngine.MapDoc.Compatibility.Tests/AuthoredTerrainParityTests.cs`, `KhaozEngine.MapDoc.Compatibility.Tests/FrozenTerrainFixture.cs`
- Create: `KhaozEngine.MapDoc.Compatibility.Tests/Fixtures/HollowmereTerrain.json`, `KhaozEngine.MapDoc.Compatibility.Tests/Fixtures/HollowmereTerrain.provenance.json`
- Modify: `KhaozEngine.slnx:1-147` near MapDoc tests
- Modify: `KhaozEngine.MapDoc/README.md:1-26`, `KhaozEngine.MapEdit.Tool/README.md:3-28`, `docs/USING-KHAOZENGINE.md:56-81`
- Test: `KhaozEngine.MapDoc.Compatibility.Tests/AuthoredTerrainParityTests.cs`

**Interfaces:**
- Consumes existing: `TileWorldFile.Load(string directory, TileWorldLoadOptions? options = null) -> TileWorldDocument`, TileGroundTriangles/TileTriangulation oracle from the source-checked block. Only this offline test project references TileWorld.
- Consumes Tasks 1 to 5: native source, canonical compiler, sampler and commands.
- Produces test fixture: `FrozenTerrainFixture.Load() -> FrozenTerrainFixture`, `SourceCorners`, `NativeCorners`, `SourceGroundCorners` as `IReadOnlyList<int>`, `SourceCells`, `NativeCells` as `IReadOnlyList<MapSurfaceCell>`, `SharedCorners` as `IReadOnlyList<(int Left,int Right,int Source)>`, `RowSamples` as `IReadOnlyList<(float SourceZ,float NativeZ)>`, `CompareAllTriangles() -> IReadOnlyList<string>`, `SampleEveryCutSeamAndPlane() -> IReadOnlyList<(float Floor,float Render,float Capture)>`.
- Produces no importer or game dependency. R11 consumes the proven surface contracts.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void AuthoredTerrain_AllFrozenCornersCellsAndThreeConsumersAgree()
{
    var f = FrozenTerrainFixture.Load();
    Assert.Equal(103041, f.SourceGroundCorners.Count);
    Assert.Equal(f.SourceCorners, f.NativeCorners);
    Assert.Equal(f.SourceCells, f.NativeCells);
    Assert.Empty(f.CompareAllTriangles());
    foreach (var c in f.SharedCorners) { Assert.Equal(c.Source,c.Left); Assert.Equal(c.Left,c.Right); }
    foreach (var row in f.RowSamples) Assert.Equal(row.SourceZ,row.NativeZ);
    foreach (var sample in f.SampleEveryCutSeamAndPlane())
    {
        Assert.Equal(sample.Floor, sample.Render);
        Assert.Equal(sample.Floor, sample.Capture);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t6:red" "${wa_r2_log_dir}/wa-r2-t6-red.log" -- dotnet test KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~AuthoredTerrainParityTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the fixture extraction in `FrozenTerrainFixture.cs` as test-only data preparation, reading a frozen source copy from game commit `74f57ee22652bd18234b4479faba4f1898a17047`, recording a SHA-256 per source file and a sorted aggregate digest. Store numeric/cell/triangle oracle data plus provenance under Fixtures, never reference an external mutable worktree during tests. Pin 103,041 distinct ground corners, 102,400 ground underlays, 2,130 overlays, ten shaped cells, 660 feather flags, 8,872 blocked, 836 indoor, nine reserved Bridge values and all four planes' effective corners. NegativeZ rows preserve source row order and world Z sign, check exact shared-corner equality at every 64-cell seam in both storage forms. Source fixture bytes must match provenance before the test runs. Decode the source short/base64 payloads and explicitly materialize all plane heights in this test-only fixture builder. No production offline importer API is created here. Compare exact integers/bytes/IDs, vertex error <=0.00001 m and normal component error <=0.000001 to source oracle. Floor/render-input/capture-input must use identical native triangle descriptors and hence have zero semantic discrepancy, including the spike point. This round proves render/capture inputs, while actual Scene3D/backend goldens remain R8. Preserve analytic and existing TileWorld regression tests. Update live documentation and complete round verification.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-t6:green" "${wa_r2_log_dir}/wa-r2-t6-green.log" -- dotnet test KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~AuthoredTerrainParityTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj KhaozEngine.MapDoc.Compatibility.Tests/AuthoredTerrainParityTests.cs KhaozEngine.MapDoc.Compatibility.Tests/FrozenTerrainFixture.cs KhaozEngine.MapDoc.Compatibility.Tests/Fixtures/HollowmereTerrain.json KhaozEngine.MapDoc.Compatibility.Tests/Fixtures/HollowmereTerrain.provenance.json KhaozEngine.slnx KhaozEngine.MapDoc/README.md KhaozEngine.MapEdit.Tool/README.md docs/USING-KHAOZENGINE.md
git diff --cached --check
git commit -m "test(mapdoc): prove exact authored terrain fidelity"
```


## Round Verification and Handoff

Run from the implementation worktree root, sequentially. Focused tests above are the task red/green cycle. The full solution suite runs once at round finish after the solution build, not once per task and never in a repeat loop. Re-run only when a subsequent code change or integration conflict requires it. The slot runner returns the target exit code. Exit 75 means no command ran because the slot was busy. Hand that result to the controller, never retry a failed test or loop verification.

```bash
mkdir -p local-feed "${wa_r2_log_dir}"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-focused-KhaozEngine.MapDoc.Tests:finish" "${wa_r2_log_dir}/wa-r2-focused-KhaozEngine.MapDoc.Tests-finish.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurface|FullyQualifiedName~NativeLocalFloor"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-focused-KhaozEngine.MapEditor.Tests:finish" "${wa_r2_log_dir}/wa-r2-focused-KhaozEngine.MapEditor.Tests-finish.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceCommandTests"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-focused-KhaozEngine.MapDoc.Compatibility.Tests:finish" "${wa_r2_log_dir}/wa-r2-focused-KhaozEngine.MapDoc.Compatibility.Tests-finish.log" -- dotnet test KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~AuthoredTerrainParityTests"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-build:finish" "${wa_r2_log_dir}/wa-r2-build-finish.log" -- dotnet build KhaozEngine.slnx -c Release
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-format:finish" "${wa_r2_log_dir}/wa-r2-format-finish.log" -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-suite:finish" "${wa_r2_log_dir}/wa-r2-suite-finish.log" -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-check-dashes:finish" "${wa_r2_log_dir}/wa-r2-check-dashes-finish.log" -- sh scripts/check-dashes.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-check-prose:finish" "${wa_r2_log_dir}/wa-r2-check-prose-finish.log" -- sh scripts/check-prose.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-check-file-size:finish" "${wa_r2_log_dir}/wa-r2-check-file-size-finish.log" -- sh scripts/check-file-size.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-check-agent-instructions:finish" "${wa_r2_log_dir}/wa-r2-check-agent-instructions-finish.log" -- sh scripts/check-agent-instructions.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-check-doc-versions:finish" "${wa_r2_log_dir}/wa-r2-check-doc-versions-finish.log" -- bash scripts/check-doc-versions.sh
```

Require exit 0 from every command, zero warnings, nonempty focused selections, no format diff and no guard failures. These commands are future implementation verification, not authorization to run tests in the documents lane. GPU facts are skipped by ordinary `dotnet test`. Any visual golden additions use the relevant backend CI bake from `docs/CROSS-PLATFORM.md`, serialized and without booting a consumer. No local stress or repeated suite runs.

The last task also updates the package README, `docs/USING-KHAOZENGINE.md` and every stale Markdown reference for its added APIs. An orchestrator re-reads main, tags and `Directory.Build.props`, selects the next available engine minor after pivot releases, rides an existing staged version only when it belongs to this same round, and updates `CHANGELOG.md` plus all declarations checked by `check-doc-versions.sh`. Delegated workers record verified commits in Outcome and return them for integration. Each round is its own minor capability release and does not share a pivot or another round's release number. No engine release number is reserved here and no worker tags. Build, test and guard failures block the round's exit claim.

## Self-Review

Coverage: C2 absolute schema/materials/planes Task 1, canonical cuts/feathers Task 2, floor/support/paint ownership Task 3, masks/indoor membership Task 4, native edit commands Task 5, complete differential terrain data Task 6. Runtime rendering and full frontend UX are intentionally assigned to R8 to R10. Every task has one test cycle and a reviewable deliverable. Existing interfaces were checked at the evidence SHA, new interfaces are explicitly produced before consumption, and the five Review Focus cases each have a named assertion in an owning task. The snippets pin behavior rather than implement algorithms. No later round's complete GUI, MCP, importer or rendering workflow is claimed here.

## Outcome

### OA9 documentation outcome, 2026-10-05

- Recorded [OA9](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#owner-rulings-binding-direction) and this plan's [checkpoint](#oa9-checkpoint-before-r2-approval). Existing source-count fixtures and prior guard results below remain historical.
- R2 remains unapproved. Its checkpoint names pending choices, owning tasks/dependencies and future proofs, to be refined before owner round approval. No capability, art, swimming, world enlargement or fresh benchmark is claimed.
- This revision requires serial doc guards and explicit-path commit. The worker stops at the docs commit for controller verification/push, with no builds/tests/format/pack or integration.

### Historical documentation reconciliation before OA8/OA9, 2026-10-05

- Approval stage: specs approved with revised T4. R2 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: Reconcile the released prerequisite APIs and record their actual SHAs before owner plan review. R3 also needs explicit revised-T4 geometry/query refinement. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: source and planning review only, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: J2.1 supersedes the old 1-to-16/four-neighbour smoothing proposal with released legacy 1-to-64/3x3 semantics. R10 shares the contract. Old terrain counts are historical regression data.
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


### R1 Task 5 review carry-forward, 2026-10-05

Recorded the R1 transaction boundary and [#1302](https://github.com/APKiwiOrg/KhaozEngine/issues/1302) as refinement inputs.
This is not approval of this round, a selected optimization or a measured performance result.


### R2 owner-gate research, no implementation approval, 2026-10-06

Released prerequisite is v20.27.0 at a87038f5a. The tagged publication workflow remains queued,
with SQL jobs green and build-test-pack awaiting a hosted runner. Exact release identity is recorded
in the R1 Outcome and program. No R2 execution worktree or implementation has been started.

Controller-checked decision brief is Grimhollow's
[program R2-OWNER-GATE.md](https://github.com/APKiwiOrg/Grimhollow/blob/feature/world-authoring/docs/superpowers/programs/world-authoring/R2-OWNER-GATE.md).
The recommendation is native floor/ceiling layers for primary continuous cave authoring, with native
prefab groups as the alternative primary workflow. Weighted judgments rank layers 89/120 and prefab
groups 79/120 under the continuous-network assumption. This is a proposal, not an approved design.

Await owner cave-model choice and rough horizontal/vertical targets before the full plan is refined
for approval. Both options permit native local editing. Neither inherently solves precision or
residency. Floors, ceilings, walls, openings, support context and occupied domains need distinct
contracts. Do not adopt a single fixed-height prism as every cave layer's complete membership rule.

R1's XZ-only resolver callback and version-1 identity are verified in released source. Extend through
explicit versioned semantics and preserve analytic consumers. Re-anchor stale task citations and
fixture provenance. Do not infer failure of the differential tolerance from float spacing alone.
The research's calendar ranges are not adopted as a forecast. OA9 work is required, and the revised
estimate must follow actual task allocation and the chosen verification envelope.


### OA13 input for design refinement, 2026-10-06

The owner wants seamless sculpted surface-to-cave terrain, spacious caverns, roughly 500 m depth and
500 m peaks, with giant WoW-scale horizontal scope. Native floor/ceiling layers are the working
approach. R2-D1 provisionally tests a 64 km by 64 km envelope, not an owner-specified hard limit or
content commitment. The exact design and this revised full plan remain unapproved.

The pending refinement must explicitly cover entrance apertures and wall/ceiling boundaries, canonical
render/collision/support/nav seams, sparse bounded surface payloads and chunk/region identity. Do not
freeze an eager whole-world height-array assumption into the format. Reuse existing origin-relative
rendering and physics seams where they satisfy measured precision requirements, and expose any
required compatibility migration instead of silently rewriting R1. No coordinate solution is yet approved.

Keep old NoDraw/fallback and imported four-plane semantics explicit. A visual hole does not silently
become a physical cave entrance. Preserve the accepted source and refreeze provenance when fixtures
are prepared. Bound tests at near/far coordinates and vertical extrema without full-area allocations,
large local benchmarks or stress loops. Padding for objects, cameras and bake probes sits outside the
rough terrain extrema. The current nav storage budget remains an owner gate.

Next is a concrete R2 design refinement and task/estimate impact for review, before implementation
plan steps are reconciled to that selected contract. No R2 source edit or execution worktree exists.

### Reviewed cave and scale design candidate, 2026-10-06

The canonical candidate is docs/design/WORLD-AUTHORING-R2-CAVES-SCALE-2026-10-06.md.
Review 1 findings M1 to M3 were addressed at 5bc3ce754. Targeted review 2 accepted the geometry,
precision, shaft, ownership and effort corrections, then identified D1 to D3 in migration/storage.
Controller checked the cited released code at a87038f5a and corrected those paragraphs directly.

D1 uses a document/manifest-level legacy support recipe, so unloaded tile placements do not need
migration tags. D2 accepts released resolver-v1 options unchanged. Full resolver-2 adoption converts
every missing-Y placement and cannot finish through a partial window. D3 puts surface storage under
the already reserved tiles/surfaces namespace, preserves author-owned surfaces resources, and extends
the sweep keep set to both tile and surface dependencies. Acceptance rows explicitly cover each case.

The reviewer requested only a targeted check of these paragraphs and proof rows before owner review.
Controller completed that source check. No new broad review or code execution was started.
Documentation guards passed in /tmp/grimhollow-orch/logs/wa-r2-review2-docs-20261006.log.
A final changed-text check follows the last bookkeeping edits.

Separate existing stale-window publication defect #1310 was source-verified and filed, with no
runtime reproduction claimed. The full plan must allocate or depend on its repair before claiming
windowed no-loss. It is not silently covered by the existing save lock or marked implemented.

The design proposes a sparse 64 km square verification envelope and roughly +/-500 m terrain,
paired floor/ceiling patches with wall strips and openings, whole-cell aperture limits, bounded
storage/query contracts, and 29 to 49 engineer-days of R2 labor. All remain pending owner design
approval. This full executable plan is still unreconciled and unapproved. R2 has no code worktree.
