# World Authoring R2: Exact Authored Terrain and Paint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add absolute authored terrain with one canonical triangle surface for floor sampling, paint, statics and later rendering.

**Architecture:** Format 5 separates authored surfaces from the analytic terrain path. A GPU-free compiler resolves integer height lattices, exact paint topology, support roles and masks. Native commands edit these DTOs transactionally and invalidate every dependent cache together.

**Tech Stack:** C# on the repository's existing .NET target, System.Numerics, System.Text.Json, closed JSON Schema, xUnit, existing engine seams. No new third-party dependency.

**Spec:** `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md`, approved direction at `49b045f75`, with inline clarifications on allocation undo and water boundary ownership. Read C2, T1 to T9 and the evidence register before implementation.

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

The exact spike cell at source (4,64) has corner centimetres `[1433,1331,1363,518]` in SW, SE, NW, NE order, material 14 and no overlay/cut/rotation/flags. These values were decoded read-only from accepted source `regions/r_0_1.json`. At world (4.37,-64.61), choose the NW-SE triangle. The expected height is computed from the same float corner conversion and barycentric weights, not a rounded hardcoded decimal. A source-short height is `height * 0.01f`, as `TileGroundTriangles.cs:162-165` does.

Native row direction is explicit. World surfaces normally use NegativeZ to preserve source indexing, while other native surfaces may use PositiveZ. Storage TileSize remains independent of CellSize. Integer HeightUnits allow centimetres or finer positive finite units, with no analytic deltas. Indoor span is explicit per mask surface, no guessed global plane height. Legacy void/NoDraw fallback is explicitly tagged non-capture support and may be bilinear only there, bounded/clamped as before. Drawn terrain never calls a bilinear fallback.

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

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t1 /tmp/grand-world/wa-r2-t1.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceDocumentTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced DTOs and validator signatures in the named files. Dimensions require `(Width+1)*(Depth+1)` corners and `Width*Depth` cells using checked multiplication before allocations. Keep exact integer units and material IDs. Reject unsupported payloads/row values/cuts/rotations/flag bits, nonfinite origins/units/derived heights, missing resources, contradictory shared corners and duplicate IDs. Structural validation checks material IDs, and the resolver invokes ValidateClosure to check material resource bindings. Authored source forbids TerrainOverrides, Features and procedural scatter generation, while analytic DTO data stays stored for reversible explicit source conversion. Do not invoke analytic noise in authored mode. The 4 to 5 migration defaults old maps to Analytic and no surfaces, preserving hashes appropriate to the schema upgrade and old runtime geometry. Native surface/material lists are manifest globals in both storage forms for now, with extent membership derived independently. Add all four plane lifts `[0,450,900,1350]` cm and explicit upper overrides as fixture assertions, empty upper surfaces and 14 distinct material bindings.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t1 /tmp/grand-world/wa-r2-t1.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceDocumentTests"`
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

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t2 /tmp/grand-world/wa-r2-t2.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceTopologyTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the compiler signatures in `MapSurfaceCompiler.cs`, factoring topology into `MapSurfaceTopology.cs`. Use widened integer subtraction for the least-height-difference diagonal, with equality selecting SW-NE. Paint absence selects Full triangles but does not erase a forced authored diagonal. Full/diagonal cuts emit two canonical triangles, corner cuts four. Midpoints are `(a+b)*0.5f`, geometric normals point up, and materials follow each triangle's cut ownership. Preserve all 16 cut/rotation combinations, underlay/overlay uint16 bindings, texture repeat and feather metadata. Feather paint subdivisions stay on the canonical triangle plane and never add a different support surface. Add theories for every cut/rotation, extreme int heights, stable topology order, zero overlay, and feathered-versus-unfeathered coplanarity. No coarsened surface LOD or five-channel splat conversion.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t2 /tmp/grand-world/wa-r2-t2.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceTopologyTests"`
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

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t3 /tmp/grand-world/wa-r2-t3.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceSamplingTests|FullyQualifiedName~NativeLocalFloorTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the sampler and paint signatures in the new files. Inverse-transform query XZ to the surface's local lattice, choose the canonical triangle by maximum minimum barycentric weight, then sample its exact vertices. Every consumer later receives this same descriptor/sample. Fallback requires a nonempty versioned Identity included in the authored build options hash. It is clamped to its declared bounds, tagged non-capture and never expands playable bounds. Compile no capture triangles for void/NoDraw or empty upper layers. PaintOverride requires a valid target, clips to target triangles and has no support ownership. RigidSupport alone contributes new support geometry. Assert transformed floor yaw 0.371, offset (0.23,0.17), scale 1.137, inverse query and normal agreement, double-owner rejection, moved paint not rewriting world cells, NoDraw fallback tagging, empty upper surfaces and unsupported BuildField calls in authored mode. Keep old analytic BuildField behavior unchanged.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t3 /tmp/grand-world/wa-r2-t3.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceSamplingTests|FullyQualifiedName~NativeLocalFloorTests"`
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

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t4 /tmp/grand-world/wa-r2-t4.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceMaskTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `Compile(...)` and `At(...)` in the named files. Trace connected mask boundaries deterministically with hole-aware decomposition into simple prisms, retaining explicit vertical span and free transform. Missing span on an Indoor cell is invalid. Bind later prefab floor volumes through this same membership service in R5. BuildBlockedMasks retains blocked cells separately from support triangles and reserved Bridge metadata. Add L-shaped and hole fixtures, neighboring masks with shared edges, both row directions, span boundary tests, transformed membership and equality of per-cell coverage with prism union. Indoor classification is one shared service for both heads.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t4 /tmp/grand-world/wa-r2-t4.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceMaskTests"`
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

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t5 /tmp/grand-world/wa-r2-t5.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceCommandTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced pure edit methods, command and service signatures in their named files. Edits deep-copy and prevalidate the full patch, units never change implicitly. Set/Raise require ValueUnits, Flatten may omit it, Smooth ignores it. Radius must be finite positive and Hardness in [0,1]. Flatten without an explicit value uses the selected-corner arithmetic mean rounded to integer units with MidpointRounding.AwayFromZero. Smooth is deterministic Jacobi, reads the prior iteration, uses a four-neighbor mean, 1 to 16 iterations and the same rounding. Pin circular falloff as weight `clamp((radius-distance)/(radius*(1-hardness)),0,1)`, hardness 1 as a hard disc, quantizing the final height once. Boundary corners shared by multiple surfaces update atomically or reject inconsistent ownership. Paint patches change only supplied cell fields. History restores exact arrays and effects. Add layer add/remove, exact read/set, explicit image orientation/range conversion helper `ImportHeights(IReadOnlyList<float> samples,float sourceMin,float sourceMax,int targetMinUnits,int targetMaxUnits) -> int[]`, and material/flag/cut/rotation/feather operations over this same replacement boundary. Add GUI-command versus service document/hash/dirty-bound equality, undo/redo, partial-cell preservation and all brush operation tests. MCP verb registration and full GUI brushes belong to R9/R10, not this round.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t5 /tmp/grand-world/wa-r2-t5.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceCommandTests"`
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

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t6 /tmp/grand-world/wa-r2-t6.log -- dotnet test KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~AuthoredTerrainParityTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the fixture extraction in `FrozenTerrainFixture.cs` as test-only data preparation, reading a frozen source copy from game commit `74f57ee22652bd18234b4479faba4f1898a17047`, recording a SHA-256 per source file and a sorted aggregate digest. Store numeric/cell/triangle oracle data plus provenance under Fixtures, never reference an external mutable worktree during tests. Pin 103,041 distinct ground corners, 102,400 ground underlays, 2,130 overlays, ten shaped cells, 660 feather flags, 8,872 blocked, 836 indoor, nine reserved Bridge values and all four planes' effective corners. NegativeZ rows preserve source row order and world Z sign, check exact shared-corner equality at every 64-cell seam in both storage forms. Source fixture bytes must match provenance before the test runs. Decode the source short/base64 payloads and explicitly materialize all plane heights in this test-only fixture builder. No production offline importer API is created here. Compare exact integers/bytes/IDs, vertex error <=0.00001 m and normal component error <=0.000001 to source oracle. Floor/render-input/capture-input must use identical native triangle descriptors and hence have zero semantic discrepancy, including the spike point. This round proves render/capture inputs, while actual Scene3D/backend goldens remain R8. Preserve analytic and existing TileWorld regression tests. Update live documentation and complete round verification.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r2-t6 /tmp/grand-world/wa-r2-t6.log -- dotnet test KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~AuthoredTerrainParityTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj KhaozEngine.MapDoc.Compatibility.Tests/AuthoredTerrainParityTests.cs KhaozEngine.MapDoc.Compatibility.Tests/FrozenTerrainFixture.cs KhaozEngine.MapDoc.Compatibility.Tests/Fixtures/HollowmereTerrain.json KhaozEngine.MapDoc.Compatibility.Tests/Fixtures/HollowmereTerrain.provenance.json KhaozEngine.slnx KhaozEngine.MapDoc/README.md KhaozEngine.MapEdit.Tool/README.md docs/USING-KHAOZENGINE.md
git diff --cached --check
git commit -m "test(mapdoc): prove exact authored terrain fidelity"
```


## Round Verification and Handoff

Run from the implementation worktree root, sequentially. Focused tests above are the task red/green cycle. The full solution suite runs once at round finish after the solution build, not once per task and never in a repeat loop. Re-run only when a subsequent code change or integration conflict requires it. The slot wrapper retries only lock contention, not failed tests.

```bash
mkdir -p local-feed
/tmp/grand-world/slot-retry.sh wa-r2-focused-KhaozEngine.MapDoc.Tests /tmp/grand-world/wa-r2-focused-KhaozEngine.MapDoc.Tests.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurface|FullyQualifiedName~NativeLocalFloor"
/tmp/grand-world/slot-retry.sh wa-r2-focused-KhaozEngine.MapEditor.Tests /tmp/grand-world/wa-r2-focused-KhaozEngine.MapEditor.Tests.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeSurfaceCommandTests"
/tmp/grand-world/slot-retry.sh wa-r2-focused-KhaozEngine.MapDoc.Compatibility.Tests /tmp/grand-world/wa-r2-focused-KhaozEngine.MapDoc.Compatibility.Tests.log -- dotnet test KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~AuthoredTerrainParityTests"
/tmp/grand-world/slot-retry.sh wa-r2-build /tmp/grand-world/wa-r2-build.log -- dotnet build KhaozEngine.slnx -c Release
/tmp/grand-world/slot-retry.sh wa-r2-format /tmp/grand-world/wa-r2-format.log -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
/tmp/grand-world/slot-retry.sh wa-r2-suite /tmp/grand-world/wa-r2-suite.log -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
/tmp/grand-world/slot-retry.sh wa-r2-check-dashes /tmp/grand-world/wa-r2-check-dashes.log -- sh scripts/check-dashes.sh --tree
/tmp/grand-world/slot-retry.sh wa-r2-check-prose /tmp/grand-world/wa-r2-check-prose.log -- sh scripts/check-prose.sh --tree
/tmp/grand-world/slot-retry.sh wa-r2-check-file-size /tmp/grand-world/wa-r2-check-file-size.log -- sh scripts/check-file-size.sh --tree
/tmp/grand-world/slot-retry.sh wa-r2-check-agent-instructions /tmp/grand-world/wa-r2-check-agent-instructions.log -- sh scripts/check-agent-instructions.sh --tree
/tmp/grand-world/slot-retry.sh wa-r2-check-doc-versions /tmp/grand-world/wa-r2-check-doc-versions.log -- bash scripts/check-doc-versions.sh
```

Require exit 0 from every command, zero warnings, nonempty focused selections, no format diff and no guard failures. These commands are future implementation verification, not authorization to run tests in the documents lane. GPU facts are skipped by ordinary `dotnet test`. Any visual golden additions use the relevant backend CI bake from `docs/CROSS-PLATFORM.md`, serialized and without booting a consumer. No local stress or repeated suite runs.

The last task also updates the package README, `docs/USING-KHAOZENGINE.md` and every stale Markdown reference for its added APIs. An orchestrator re-reads main, tags and `Directory.Build.props`, selects the next available engine minor after pivot releases, rides an existing staged version only when it belongs to this same round, and updates `CHANGELOG.md` plus all declarations checked by `check-doc-versions.sh`. Delegated workers record verified commits in Outcome and return them for integration. Each round is its own minor capability release and does not share a pivot or another round's release number. No engine release number is reserved here and no worker tags. Build, test and guard failures block the round's exit claim.

## Self-Review

Coverage: C2 absolute schema/materials/planes Task 1, canonical cuts/feathers Task 2, floor/support/paint ownership Task 3, masks/indoor membership Task 4, native edit commands Task 5, complete differential terrain data Task 6. Runtime rendering and full frontend UX are intentionally assigned to R8 to R10. Every task has one test cycle and a reviewable deliverable. Existing interfaces were checked at the evidence SHA, new interfaces are explicitly produced before consumption, and the five Review Focus cases each have a named assertion in an owning task. The snippets pin behavior rather than implement algorithms. No later round's complete GUI, MCP, importer or rendering workflow is claimed here.

## Outcome
