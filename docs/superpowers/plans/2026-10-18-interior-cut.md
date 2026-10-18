# Interior Cut Implementation Plan

As built. This plan is historical. The accepted deviations and final API differences are recorded in
[the spec's As built section](../../design/INTERIOR-CUT-DESIGN-2026-10-18.md#as-built), including source-wide
encoding, code-space membership, contiguous retained TileWorld storeys, the two terminal `DrawProps`
cut-flag overloads, `CutArchetypeIds`, `InteriorCutBinding` and core tests in Foundation.Tests. Backend
correctness and timing evidence remain pending. Living use is in USING-KHAOZENGINE and the package READMEs.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut opted-in rigid prop pixels inside authored interiors, including their shadows and interactions, while preserving crowns above roofs and every non-opted-in picture.

**Architecture:** A world-format-agnostic Primitives builder turns polygon volumes and existing clearance samples into one packed height texture with matching CPU queries. A TileWorld adapter and background view driver supply it to Scene3D. One instance flag gates colour, temporal, shadow, outline and silhouette cuts, and the applied field also filters picks.

**Tech Stack:** C# / .NET 10, System.Numerics, xUnit, the engine GPU seam, GLSL cross-compiled for Metal, Direct3D 11 and Vulkan.

**Spec:** `docs/design/INTERIOR-CUT-DESIGN-2026-10-18.md`, IC1 to IC6 and OC1 to OC10, including the approved OC8 (a). Roadmap #1470. Read the spec with this plan.

## Global Constraints

- IC2 cuts only inside the outline and below the sampled roof. Fill only cells wholly inside a simple polygon, either winding, without holes. The height interval is floor-inclusive and top-exclusive.
- Window: 512 m per side at every cell size (H1). Cells per side are `InteriorWindow.CellsFor(512f, cellSize)`, ceil(512 / cellSize) capped at 4096: 2048 at 25 cm, 2134 at 24 cm. A capped window logs interior.window once. Minimum snapped to the world-origin lattice. Re-centre at 64 m or more along either axis of the prop draw focus (OC9), covering the 192 m full-detail tree range.
- OC3 picks the largest cell at or below 25 cm dividing TileSize exactly: 0.25 m for 1 m and 1.5 m tiles, 0.24 m for 1.2 m tiles. H1 keeps the window at 512 m for every cell size.
- OC4 merges to the lowest floor and highest top, including disjoint storeys. MergedCells counts cells with multiple contributing volumes once, not pairwise merges.
- Roof samples use ClearanceSamples.Build(mesh, cellSize / 2f), transformed onto the world grid. Take the lowest sample, never ClearanceSolid's local grid or its distance field. Missing samples use CeilingY. Gable faces and slab undersides intentionally lower the cut.
- OC7 uses R8G8B8A8UNorm, floor high/low in RG, top high/low in BA, rows along +world Z. Decode each byte with round(channel * 255), use texelFetch, never filtered sampling.
- The vertical encoding belongs to the whole source, not the window (H2). BaseY is the lowest floor of every interior the source holds and StepY is 0.01 m through a 655.35 m range, then range / 65534, both carried in InteriorSourceResult.Encoding. They change only when the source's interiors change. Floor and top round down. A top not above its floor is empty, including a zero texel. The adapter reports interior.step when StepY exceeds 0.01 m.
- Field buffers are cells squared times 4 bytes, 16 MiB GPU and 16 MiB CPU each at 2048 cells. Applied plus under-construction CPU field buffers total 32 MiB. Do not retain a third completed field or full-window float grids beside those buffers. Snapshot and mesh-sample storage are separate from field buffers.
- One build in flight, queued requests superseded by newer generations, collection and rasterization off the render thread. Upload the whole texture once per applied field on the next render. Before the first result, picture and picks both have no cut.
- New flags default false. InstanceData remains 128 bytes. SpecParams.xyz keep their meanings, w is 0 or 1. HLOD merged geometry, grass, ground cover, animated foliage, skinned meshes, ordinary props and roofs never opt in.
- Frame UBO grows 1344 to 1376 bytes, with new vec4 at 1344 and 1360. Model set 0 binding 14 follows atmosphere 11 to 13 and reuses sampler binding 6. Existing offsets and six dynamic uniform descriptors stay unchanged.
- Cascade slots stay 256 bytes, new vec4 at 96 and 112. Point slots stay 256 bytes, new vec4 at 144 and 160. Rigid outline payload grows 160 to 192 bytes in a 256-byte slot, new vec4 at 160 and 176, Params.w is the flag. Silhouette payload grows 160 to 192 in a 256-byte slot, the same tail offsets, Params.y is the flag.
- Use the spec's InteriorCutGlsl helper and gated discard. Colour discards follow the three material samples. Temporal colour and motion discard together. Cascade and point shadows both cut. No new cut-only caster pipeline in this implementation.
- No layer opting in means byte-identical rendering. Existing references must hold without rebaking or widening tolerances. Final review runs the complete existing Golden suite, including GoldenImageTests, GoldenSnapshotTests, TargetOutlineGoldenTests, SilhouetteGoldenTests, FarFromOriginGoldenTests and TemporalOffByteIdentityGoldenTests, on every backend family.
- Every local build, test and render uses build-slot --label <what> -- <command>, with --heavy for full suites and GPU renders. Never loop tests. Create local-feed before restore. Use focused tests per task and one full headless suite at the end.
- Plain dotnet test skips GPU facts. GPU tasks below give an exact local Metal correctness command with KE_GPU_TESTS=1 and KE_GRAPHICS_BACKEND=metal-native. Their new pixel facts must have zero skips. D3D11 and Vulkan correctness run in cross-platform-gpu at final branch review, not per task, following docs/CROSS-PLATFORM.md.
- InteriorCutCostPerfGpuTests is source work only in Task 9. Only the orchestrator runs it, exactly once after final review, through gpu-hosted-bench on the T4 legs under OC8 (a). No local Metal timing and no task dispatch. Print mode 4 minus mode 2 against 0.1 ms, never assert wall time.
- No MapDoc adapter (#1474, R8), world_validate issue integration (#1473), physics or editor ray changes, authoring prevention, soft cut edges, consumer adoption or owner captures here. IC6 remains the Grimhollow adoption gate.
- No version number selected or bumped by these tasks. Task 9 adds one minor-feature note under the unreleased section while the orchestrator owns eventual version reconciliation. No task merges, pushes, packs, tags or releases unless separately assigned.
- Preserve unrelated edits, use explicit paths for staging and commits, and commit subjects area(scope): summary. No em or en dashes, prose semicolons or attribution. No KESIZE baseline growth without owner approval.

## Review Focus

1. Negative lattice coordinates and rays exactly on a cell edge must terminate and agree with the shader's half-open span. Add BoundaryAndVerticalRaysTerminate to Task 1.
2. A superseded worker finishing after disable, disposal or a later focus request must not resurrect a stale picture or stale picks. Add StaleInteriorResultsNeverApply to Task 6.
3. First allocation, replacement, resize and clearing must update every live material without a freed binding or repeated upload. Add FieldReplacementKeepsMaterialBindingsLive to Task 4.
4. Skinned shadow pipelines share today's rigid fragments and must retain their old layout and picture. Add SkinnedShadowPairsRemainUncut to Task 5.
5. A cut boundary at a quantization step after a 10 km render-origin change must agree between CPU and GPU. Add RebasedBoundaryMatchesCpu to Task 4.

---

## Verified implementation readings and orchestrator rulings

- **Plan deviation D1:** Core tests live in the existing KhaozEngine.Foundation.Tests, beside RayMathTests and Clearance/ClearanceGeometryTests. KhaozEngine.Primitives.Tests does not exist. The spec permits the matching area project, so do not create a redundant project.
- **Plan deviation D2:** Keep PropLayer a public readonly struct. The API sketch calls it a sealed class, but PropLayer.cs and its value-copy factories establish the shipped representation. Add the flag to that representation and preserve it through every copy.
- **Plan deviation D3:** Scene3D.DrawProps is extension syntax for PropRenderer.DrawProps in KhaozEngine.Terrain.Render3D/PropRenderer.cs. Add the parameter there, in Task 6 with the layer route, rather than inventing a Render3D-to-Terrain reference cycle in Task 4.
- **Plan deviation D4:** Reuse the existing public TileCollisionBaker.WallFacing(int rotation). The spec calls for making it internal, but it is already public. Reducing visibility would break existing consumers for no cut benefit.
- **Plan deviation D5:** Preserve uncut skinned shadow fragment strings under new skinned-only constants before changing the six rigid fragments. ShadowMapRenderer and PointShadowRenderer.Skinned currently reuse those rigid fragments, so changing them directly would introduce undeclared varyings and cut bindings into excluded skinned pipelines.
- **Plan deviation D6, superseded by H2:** the adapter computes the encoding, so it reports interior.step itself at collection. The core stays free of TileWorld issue types.

Orchestrator rulings on the plan's two hold points (2026-10-18). They are binding for Tasks 1 and 6.

1. **H1, the window is defined in metres.** The window stays 512 m per side at every cell size, so OC1's coverage holds through 64 m of drift whatever OC3 picks. `InteriorWindow.CellsFor(float extentMetres, float cellSize)` returns ceil(extentMetres / cellSize) capped at `InteriorWindow.MaxCells = 4096`, the smallest 2D texture dimension every backend guarantees. A tile size whose cell would need more than 4096 cells gets a 4096-cell window and one logged interior.window issue naming the reduced extent. Memory scales with cells squared (17.4 MiB at 2134 cells).
2. **H2, the vertical encoding belongs to the source.** `InteriorEncoding.For(lowestFloorY, highestTopY)` derives BaseY and StepY from every interior the source holds, not those in the window, and `InteriorSourceResult` carries it. The builder packs against that encoding, so a re-centre over unchanged interiors keeps every overlapping texel byte-identical, and the encoding moves only when the interiors change, which already forces a full rebuild. A volume outside the encoding range is clamped into it and counted, which a correct source never triggers. This is quantizing against the whole source on purpose: the TileWorld adapter scans indoor flags world-wide, and a MapDoc source knows its prisms.

## File Structure

All paths below are relative to the engine worktree. Every Create path, new member, new test name and new helper signature in this plan is **new**. Modify paths and consumed members were checked at ea2b32cad. Test namespaces remain KhaozEngine.Tests.*. Create focused companions instead of growing a ratcheted owner file.

| Task | Responsibility and file homes |
|---|---|
| 1 | New KhaozEngine.Primitives/InteriorCut/{InteriorVolume,InteriorWindow,IInteriorSource,InteriorHeightField,InteriorHeightFieldBuilder,InteriorFieldRasterizer,InteriorFieldRays}.cs. CPU tests in KhaozEngine.Foundation.Tests/InteriorCut/. |
| 2 | New KhaozEngine.Primitives/InteriorCut/InteriorRoofRasterizer.cs. Extend the Task 1 builder. Roof tests beside the core tests. |
| 3 | New KhaozEngine.TileWorld/{TileWorldInteriors,TileInteriorRoofSamples,TileInteriorInputs}.cs. Adapter tests in KhaozEngine.TileWorld.Tests/TileWorld/. |
| 4 | New Render3D scene partial, resource owner, uniform value and shared GLSL helper. Modify model frame packing, layout transaction and rigid descriptor/queue packing. Headless and GPU tests in KhaozEngine.Render.Tests. |
| 5 | Modify cascade and point shadow renderers, shader fragments and cache invalidation. New focused shadow contract and GPU tests. |
| 6 | Extend Terrain prop factories/submission, TileWorld layer definitions, snapshots, queue and view lifecycle. New view driver and recording-scene tests. |
| 7 | Extend RayMath and both TileWorld pick seams. Headless ray, doorway, walk surface and camera probe tests. |
| 8 | Extend rigid outline and silhouette queues, renderers, shader pairs and TileWorld callers. New payload, recording-scene and GPU tests. |
| 9 | New timing source and deterministic fixture tests, then the required living-doc, package README, design status and CHANGELOG sweep. |

Task order is 1 through 9. Task 2 owns roof sampling, Task 3 owns collection and its input digest, Task 6 owns asynchronous change detection and publication. Each task finishes its own red/green cycle and commit before review. Rulings H1 and H2 above bind Tasks 1, 3 and 6.

### Task 1: Interior description, packed field and CPU ray query

**Files:**

- Create: the seven Primitives files listed for Task 1 above.
- Create tests: KhaozEngine.Foundation.Tests/InteriorCut/InteriorHeightFieldTests.cs and InteriorFieldRayTests.cs.

**Interfaces:**

- Consumes: existing ClearanceSamples.Points as IReadOnlyList<Vector3>.
- Produces: the following new public API, in namespace KhaozEngine.Primitives. InteriorVolume and InteriorRoof share InteriorVolume.cs, and InteriorSourceResult shares IInteriorSource.cs.

```csharp
public sealed record InteriorVolume
{
    public required IReadOnlyList<Vector2> Outline { get; init; }
    public required float FloorY { get; init; }
    public required float CeilingY { get; init; }
    public IReadOnlyList<InteriorRoof> Roofs { get; init; } = [];
}
public readonly record struct InteriorRoof(ClearanceSamples Samples, Matrix4x4 LocalToWorld);
public readonly record struct InteriorWindow(Vector2 Min, float CellSize, int Cells)
{
    public const int MaxCells = 4096;
    public static int CellsFor(float extentMetres, float cellSize);
    public static InteriorWindow Centred(Vector2 focus, float cellSize, int cells);
    public bool NeedsRecentre(Vector2 focus, float distance);
}
public interface IInteriorSource { InteriorSourceResult Collect(InteriorWindow window); }
public readonly record struct InteriorEncoding(float BaseY, float StepY)
{
    public static InteriorEncoding For(float lowestFloorY, float highestTopY);
}
public sealed record InteriorSourceResult(IReadOnlyList<InteriorVolume> Volumes, InteriorEncoding Encoding, IReadOnlyList<string> Issues);
public sealed partial class InteriorHeightField
{
    public InteriorWindow Window { get; }
    public float BaseY { get; }
    public float StepY { get; }
    public int MergedCells { get; }
    public ReadOnlyMemory<byte> Texels { get; }
    public long Version { get; }
    public bool TryGetSpan(int cellX, int cellZ, out float floorY, out float topY);
    public bool Contains(Vector3 world);
    public bool FirstUncut(Vector3 origin, Vector3 direction, float tStart, float tEnd, out float t);
}
public static class InteriorHeightFieldBuilder
{
    public static InteriorHeightField Build(IReadOnlyList<InteriorVolume> volumes, InteriorWindow window, InteriorEncoding encoding);
}
```

- Internal cell coverage lives in InteriorFieldRasterizer, with `internal static bool Covers(IReadOnlyList<Vector2> outline, Vector2 min, float cellSize)`. InteriorFieldRays.cs is the ray-query partial of InteriorHeightField, not another public abstraction.
- The field owns immutable packed storage, and each build gets a distinct Version. Contains decodes that storage with the shader's comparisons. BaseY and StepY come from the encoding argument (H2).

- [ ] **Step 1: Write the failing core and ray tests.** RectFillsOnlyWholeCells uses a 1 m square on a 0.25 m grid and a shifted edge at 0.125 m. RotatedPolygonNeverCutsOutside uses both windings at 30 degrees, a point-in-polygon oracle and a concave notch crossed by a cell whose four corners alone would pass. StackedAndDisjointStoreysMergeOnce uses spans [0,3) and [6,9). QuantizationRoundsDownAndPacksExactly uses negative floors, the 655.35 m threshold and a larger range. EmptyFieldCutsNothing checks an empty input and wholly off-window volumes. RecentreKeepsOverlapBytes compares the same volumes after a 64 m shift. FirstUncutMatchesDenseOracle uses deterministic seeded rays and additionally checks exact interval endpoints. BoundaryAndVerticalRaysTerminate covers zero XZ direction, zero-length segments, negative cells, grid corners and both signs of each direction. The assertions include:

```csharp
Assert.Equal(16, filledCellCount);
Assert.False(straddlingCellFilled);
Assert.Equal(1, merged.MergedCells);
Assert.Equal(0f, mergedFloor);
Assert.Equal(9f, mergedTop);
Assert.Equal(0.01f, centimetreField.StepY);
Assert.True(wideField.StepY > 0.01f);
Assert.Equal(expectedPackedBytes, field.Texels.ToArray());
Assert.False(empty.Contains(Vector3.Zero));
Assert.Equal(overlapBefore, overlapAfter);
Assert.False(field.FirstUncut(inside, Vector3.UnitX, 0f, whollyCutEnd, out _));
Assert.True(field.FirstUncut(outside, Vector3.UnitY, 0f, 1f, out float first));
Assert.Equal(0f, first);
```

- [ ] **Step 2: Run the new tests red.** Run `build-slot --label interior-core-red -- dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter FullyQualifiedName~InteriorCut`. Expect missing new API compile failures first, then named assertion failures after API shells exist. Do not mistake a restore failure for the required red result.
- [ ] **Step 3: Implement the exact API above.** Use polygon-edge intersection as well as corner containment for inward cells. Reject non-finite geometry, invalid windows and malformed rings with argument exceptions, allow an empty volume list and zero-height volumes. Preserve either winding. Pack only after the encoding is known, merge conservatively, and count each merged cell once. Walk ray cells with 2D DDA and solve each cell's linear Y interval, without sampled stepping. Outside the window is uncut. Keep floor inclusive and top exclusive, including a descending ray's first representable uncut value below a floor. Roofless volumes work now, Task 2 supplies roof samples.
- [ ] **Step 4: Run the same focused command green with label interior-core-green.** Expect every new row passing. Test that 63.999 m does not re-centre and 64 m does along either axis. RecentreKeepsOverlapBytes builds both windows with one InteriorEncoding from the whole input, including a volume that lies outside both windows and sets the lowest floor, and asserts byte-identical overlap. Add CellsForKeeps512Metres (2048 at 0.25, 2134 at 0.24, 4096 capped at 0.1) and EncodingClampsOutOfRangeVolumes.
- [ ] **Step 5: Commit explicit Task 1 paths.** Subject `feat(interior-cut): add packed interior fields and exact ray queries`.

### Task 2: World-grid lowest roof sampling

**Files:**

- Create: KhaozEngine.Primitives/InteriorCut/InteriorRoofRasterizer.cs.
- Modify: KhaozEngine.Primitives/InteriorCut/InteriorHeightFieldBuilder.cs.
- Create test: KhaozEngine.Foundation.Tests/InteriorCut/InteriorRoofRasterizerTests.cs.

**Interfaces:**

- Consumes: `ClearanceSamples.Build(ClearanceMesh mesh, float pitch)`, `ClearanceSamples.Points`, Task 1 InteriorRoof and InteriorWindow.
- Produces: new internal `static void InteriorRoofRasterizer.Lower(InteriorRoof roof, InteriorWindow window, int firstCellX, int firstCellZ, int width, int height, Span<float> lowestSamples)` for a bounded row or tile slice. Infinity means no sample. Build remains the public entry point.

- [ ] **Step 1: Write GableRoofUsesLowestWorldSample, RotatedRoofSamplesTheWorldLattice, MissingSamplesUseCeiling and EavesCannotFillAnOutdoorCell.** Build a two-slope ClearanceMesh with a vertical gable face, sample at 0.125 m and transform it by translation and yaw. For a roof with analytic height h over a 0.25 m cell at a 45 degree slope, assert top <= h and h - top <= 0.25 m plus the downward quantization step. Assert the gable row stops at the eave and an outside eave cannot change a cell. Use a slab case to assert the underside wins and a no-sample cell returns CeilingY.

```csharp
Assert.True(top <= analyticHeight);
Assert.InRange(analyticHeight - top, 0f, 0.25f + field.StepY);
Assert.Equal(eaveHeight, gableRowTop);
Assert.Equal(ceilingHeight, unsampledTop);
Assert.False(field.Contains(outdoorEavePoint));
```

- [ ] **Step 2: Run red.** `build-slot --label interior-roof-red -- dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter FullyQualifiedName~InteriorRoofRasterizerTests`. Expect the gable or transformed-cell assertions to fail against flat ceilings.
- [ ] **Step 3: Implement Lower with the complete slice signature above and call it from Build.** Transform mesh-local points once per roof per rasterized slice, bin in world XZ, and take the minimum before volume merging. Do not clamp samples upward to CeilingY when a readable sample is lower. Keep the roofless fallback. Reuse bounded scratch, not another 2048-square float field. Unreadable meshes belong to Task 3 because the core accepts only samples.
- [ ] **Step 4: Run green.** Use the Task 1 InteriorCut filter with label interior-roof-green. Expect all core and roof rows passing and unchanged overlap bytes.
- [ ] **Step 5: Commit the three explicit paths.** Subject `feat(interior-cut): rasterize lowest roof samples on the world grid`.

### Task 3: TileWorld interior source and input digest

**Files:**

- Create: KhaozEngine.TileWorld/TileWorldInteriors.cs, TileInteriorRoofSamples.cs and TileInteriorInputs.cs.
- Create tests: KhaozEngine.TileWorld.Tests/TileWorld/TileWorldInteriorsTests.cs and TileInteriorInputsTests.cs.
- Read existing: TileWorldClearance.cs, ITileMeshGeometry.cs, TileCollisionBaker.cs, TileWorldDocument.Heights.cs, TileObjectPlacement.cs, TileWorldCatalogs.cs and TileWorldSpace.cs in KhaozEngine.TileWorld.

**Interfaces:**

- Consumes: `ITileMeshGeometry.TryLoad(string meshRef, out ClearanceMesh? mesh, out string? error)`, `TileObjectPlacement.LocalToWorld(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o)`, `TileFootprint.Of(TileObjectArchetype a, int x, int z, int rotation)`, `TileCollisionBaker.WallFacing(int rotation)` and Task 1 Build/Collect types.
- Produces: new public `sealed class TileWorldInteriors : IInteriorSource`, constructor `(TileWorldDocument detached, TileWorldCatalogs catalogs, ITileMeshGeometry meshes)`, `InteriorSourceResult Collect(InteriorWindow window)` and `IReadOnlyList<TileWorldIssue> LastIssues { get; }`.
- Produces: new `public static float TileWorldInteriors.CellSizeFor(float tileSize)` implementing OC3. New internal `static long TileInteriorInputs.Digest(TileWorldDocument document, TileWorldCatalogs catalogs, RegionCoord region, int plane)` for Task 6.
- Produces: new internal sealed TileInteriorRoofSamples with `ClearanceSamples? Get(ITileMeshGeometry meshes, string meshRef, float cellSize, out string? error)`. An internal four-argument TileWorldInteriors constructor adds `TileInteriorRoofSamples samples` so the view can retain the sample cache across detached builds. The public constructor creates its own cache.

- [ ] **Step 1: Write the failing fixtures.** OnePlaneBuildingCollectsBoundsAndRoofs checks the four-point outline, minimum floor corners and ceiling corners one plane above. TwoStoreysChooseOnlyTheRoofAbove checks stacked indoors and rejects a roof on an unrelated plane. OuterWallIsAcceptedAndInnerWallIsReported checks all four rotations, WallCorner ownership, the opposite-edge neighbour and the removed tile. DiagonalBoundaryIsExcluded checks interior.diagonal-wall. UnreadableRoofFallsBackOnce checks missing MeshRef and failed TryLoad, one interior.roof-mesh per archetype. CellSizeDividesTile tests 1, 1.2 and 1.5. EncodingCoversTheWholeWorld puts a lower building outside the collected window and asserts the result's encoding BaseY is its floor, and that a building taller than 655.35 m above it reports interior.step. RelevantInputsChangeDigest checks indoors, wall, roof and corner edits against tree movement and ground material changes. SampleCacheTracksMeshIdentity checks one sample build for an unchanged mesh and resampling after TryLoad returns a replacement mesh.

```csharp
Assert.Equal(0.25f, TileWorldInteriors.CellSizeFor(1f));
Assert.Equal(0.24f, TileWorldInteriors.CellSizeFor(1.2f));
Assert.Equal(0.25f, TileWorldInteriors.CellSizeFor(1.5f));
Assert.Single(source.LastIssues, i => i.Code == "interior.wall-inner-edge");
Assert.False(field.Contains(excludedBoundaryTileCentre));
Assert.Single(source.LastIssues, i => i.Code == "interior.roof-mesh");
Assert.Equal(ceilingY, fallbackTop);
Assert.Equal(beforeTreeMove, afterTreeMove);
Assert.NotEqual(beforeIndoorEdit, afterIndoorEdit);
```

- [ ] **Step 2: Run red.** `build-slot --label interior-adapter-red -- dotnet test KhaozEngine.TileWorld.Tests/KhaozEngine.TileWorld.Tests.csproj -c Release --filter "FullyQualifiedName~TileWorldInteriorsTests|FullyQualifiedName~TileInteriorInputsTests"`. Expect missing adapter API or failed collection assertions.
- [ ] **Step 3: Implement the exact interfaces above.** Scan indoors across planes, exclude unsafe wall tiles on the affected plane, then group equal lowest/top-plane pairs into maximal row-scanned rects. Convert through TileWorldSpace because tile Z is opposite world Z. Use all lattice corners under a rect for the two minimum heights. Assign only footprint-intersecting roofs on the plane above that rect's top indoor plane. Check ownership by neighbouring walls as well as the current object. Cache samples by readable mesh identity and pitch, retaining GltfTileMeshGeometry's file-refresh behavior by calling TryLoad again on a rebuild. Return the H2 encoding from InteriorEncoding.For over every indoor rect in the document, not only the window, cached until an indoors, wall or roof digest changes. Return formatted source issue strings and keep typed LastIssues, including object, tile and plane context, and interior.step when the encoding's StepY exceeds 0.01 m. Do not change WallFacing visibility or world_validate.
- [ ] **Step 4: Run the same adapter filter green with label interior-adapter-green.** Also include existing TileWorldClearanceTests in this one green command to prove the reused mesh path and optional authoring check still work. Expect all pass.
- [ ] **Step 5: Commit the five explicit new files.** Subject `feat(tile-world): collect safe interior volumes and roof samples`.

### Task 4: Scene field ownership, colour and temporal cut

**Files:**

- Create: KhaozEngine.Render3D/Scene3D.InteriorCut.cs, Rendering/InteriorCutResources.cs, Rendering/InteriorCutUniforms.cs, Rendering/ModelRenderer.InteriorCut.cs and Internal/ShaderSources.InteriorCut.cs.
- Modify in KhaozEngine.Render3D: RigidInstanceDraw.cs, SceneInstances.cs, Scene3D.InstanceGrouping.cs, Scene3D.cs and Scene3D.FrameView.cs.
- Modify in KhaozEngine.Render3D/Rendering: ModelRenderer.cs, ModelRenderer.FrameUbo.cs and ModelRenderer.ShadowLayoutReplacement.cs.
- Modify in KhaozEngine.Render3D/Internal: ShaderSources.Model.cs, ShaderSources.MotionFrame.cs, ShaderSources.Foliage.cs, ShaderSources.Terrain.cs and ShaderSources.TileGround.cs. Inspect ShaderSources.Motion.cs because ModelMotionFrag is derived, not separately edited.
- Modify tests in KhaozEngine.Render.Tests: Render3D/UboLayoutTests.cs, Render3D/MotionUboLayoutTests.cs, Render3D/PointShadowUboLayoutTests.cs, Render3D/RigidInstanceGroupingTests.cs, Gpu/VulkanDescriptorLimitTests.cs, Gpu/D3D11RegisterNumberingTests.cs and Gpu/ShaderSourceValidationTests.cs. Keep new assertions in the new contract file because UboLayoutTests.cs is already 798 lines.
- Create tests there: Render3D/InteriorCutContractTests.cs and Gpu/InteriorCutGpuTests.cs.

**Interfaces:**

- Consumes: Task 1 InteriorHeightField properties and the existing `Scene3D.Draw(in RigidInstanceDraw draw)` path.
- Produces: new public `InteriorHeightField? Scene3D.InteriorCut { get; set; }`, new `bool RigidInstanceDraw.CutInsideInteriors { get; init; }` and new `bool SceneInstances.Instance.CutInsideInteriors { get; }` copied from the descriptor. Legacy instance constructors keep false.
- Produces: new internal readonly record struct `InteriorCutUniforms(Vector4 Window, Vector4 Heights)` with `internal static InteriorCutUniforms From(InteriorHeightField? field, Vector3 renderOrigin)`.
- Produces: new internal sealed `InteriorCutResources : IDisposable`, constructor `(IGpuDevice device)`, properties `IGpuTexture Texture { get; }`, `long TextureGeneration { get; }`, `long Version { get; }`, `void Apply(InteriorHeightField? field)` and `void CommitBindings()`. Apply owns the 1x1 zero default, field upload and candidate texture replacement. CommitBindings retires the replaced handle only after all renderer bindings succeed. Version changes on field identity or clearing, not each frame.
- Produces: new `const string ShaderSources.InteriorCutGlsl`. New `internal void ModelRenderer.SetInteriorCut(IGpuTexture texture, in InteriorCutUniforms uniforms)` updates the shared tail and transactionally rebuilds tracked rigid material sets only when the texture handle changes. New `internal InteriorCutResources ModelRenderer.InteriorResources { get; }` exposes the single resource owner to the Scene3D driver. Initialize it before constructing the first material set so binding 14 has its zero default from construction, including standalone ModelRenderer test fixtures. ModelRenderer disposes this owner after its borrowing sets.
- RenderInternal already calls LatchFrameView in Scene3D.FrameView.cs before colour or shadow encoding. Prepare the requested field there after the render origin is latched, keeping Scene3D.cs at or below its 2228-line ratchet. SetInteriorCut's same signature is added to other renderers in Tasks 5 and 8. They borrow the model owner's texture and never dispose it. Build their cut sets in SetInteriorCut before the first relevant draw, including explicit setup in standalone renderer test fixtures.

- [ ] **Step 1: Write the headless and GPU failures.** PacksOnlyTheOptedInW checks mixed runs, legacy constructors, skinned and foliage zero lanes and unchanged 128-byte size. TailAndBindingsKeepCompatibilityOffsets asserts 1344, 1360, 1376, binding 14 and six dynamic uniform descriptors. ColourAndMotionShareTheGatedDiscard checks the exact spec line, material-sample ordering and every changed pair through ShaderValidation.ValidatePair. FieldReplacementKeepsMaterialBindingsLive checks default, textured, reused and newly loaded materials through first allocation, changed contents, a different-sized field, null and reactivation, with one upload per applied version and safe cleanup after a failed set replacement. RebasedBoundaryMatchesCpu tests floor, top and both adjacent represented values after a 10 km origin change. The new GPU facts ColourCutsOnlyInteriorPixels, ColourAndMotionCutTogether and NoOptInIsByteIdentical use a tall box side-on with a direct field and no drawn building.

```csharp
Assert.Equal(128, ModelRenderer.InstanceData.SizeInBytes);
Assert.Equal(1f, optedInData.SpecParams.W);
Assert.Equal(0f, ordinaryData.SpecParams.W);
Assert.Equal(1376u, ModelRenderer.UboBytes);
Assert.Contains("if (vSpecParams.w > 0.5 && interiorCut(vWorldPos)) discard;", ShaderSources.ModelFrag);
Assert.Equal(withoutFieldPixels, fieldButNoOptInPixels);
Assert.Equal(cpuInside, gpuBackgroundAtProbe);
```

- [ ] **Step 2: Run red.** Headless command: `build-slot --label interior-colour-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~InteriorCutContractTests|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~RigidInstanceGroupingTests|FullyQualifiedName~VulkanDescriptorLimitTests|FullyQualifiedName~D3D11RegisterNumberingTests|FullyQualifiedName~MslBindingOrderGuardTests|FullyQualifiedName~ShaderSourceValidationTests"`. GPU command: `build-slot --heavy --label interior-colour-metal-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~InteriorCutGpuTests`. First establish API compilation, then expect the named pixel failures with zero skips, not a device failure.
- [ ] **Step 3: Implement the listed API and binding changes.** Put new lifecycle work in companions, preserve every pre-tail byte and append both vec4 to all shared U block declarations, including compact motion blocks. Compute window/base subtraction in double before converting to render-relative float. Append InteriorCut after AtmosphereBindings.AppendLayout's result, including its corresponding resource-set order. Extend the existing tracked material transaction, preserving cascade, point, transient, atmosphere and point-light resources across either replacement direction. Keep the texture stable between content uploads and rebuild only on allocation or dimensions changing. Null binds the zero default and writes Cells = 0. Put the gated discard after implicit-LOD samples. ModelMotionFrag inherits it. Do not add a cut branch to skinned shaders or a flag to foliage packers.
- [ ] **Step 4: Run the same focused headless and Metal commands green with labels interior-colour-green and interior-colour-metal-green.** Expect all new correctness facts passing with zero skips. D3D11 and Vulkan run in cross-platform-gpu at final branch review, not here. No golden update.
- [ ] **Step 5: Commit only the explicit Task 4 paths.** Subject `feat(render3d): cut opted-in rigid colour and motion pixels`.

### Task 5: Cascade and point shadows, including dirty state

**Files:**

- Modify KhaozEngine.Render3D: Scene3D.InteriorCut.cs, Scene3D.ShadowCasters.cs, Scene3D.ShadowDirtyState.cs and Scene3D.PointShadows.cs.
- Modify Rendering/ShadowMapRenderer.cs, Rendering/PointShadowRenderer.cs and Rendering/PointShadowRenderer.Skinned.cs. New focused companions Rendering/ShadowMapRenderer.InteriorCut.cs and Rendering/PointShadowRenderer.InteriorCut.cs hold cut resources.
- Modify Internal/ShaderSources.Shadow.cs and Internal/ShaderSources.PointShadow.cs.
- Modify tests: KhaozEngine.Render.Tests/Render3D/ShadowDepthSelectionTests.cs, PointShadowUboLayoutTests.cs and KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs.
- Create tests: KhaozEngine.Render.Tests/Render3D/InteriorCutShadowTests.cs and KhaozEngine.Render.Tests/Gpu/InteriorCutShadowGpuTests.cs.

**Interfaces:**

- Consumes: Task 4 Texture, TextureGeneration, Version, InteriorCutUniforms and helper. Existing `internal static ShadowCastKind Scene3D.ClassifyCaster(in SceneInstances.Instance instance)` and `internal static ShadowDepthVariant ShadowDepthSelection.ForRigidSpan(ShadowCastKind kind, bool meshCutsOut)` retain signatures.
- Produces: new `internal void SetInteriorCut(IGpuTexture texture, in InteriorCutUniforms uniforms)` on ShadowMapRenderer and PointShadowRenderer. Their frame/face packing writes the exact tail offsets without changing existing callers' public signatures.
- Produces: new uncut constants `ShaderSources.SkinnedShadowDepthDissolveFrag`, `SkinnedShadowDepthDissolveInvertedFrag`, `PointShadowSkinnedDissolveFrag` and `PointShadowSkinnedDissolveInvertedFrag`, preserving the former fragment strings for GPU-skinned pipelines.
- The existing six rigid fragments keep their names and gain the helper. Cascade dissolve uses new set 1, cascade cutout set 2, point rigid dissolve set 1. Each renderer owns one cut set per required layout, never one per mesh. Their layouts add no uniform descriptor.

- [ ] **Step 1: Write OptedInCastersRouteWithoutChangingOtherPolicies, InteriorVersionInvalidatesBothCaches and SkinnedShadowPairsRemainUncut.** Test no-cast precedence, opted-in solid, MASK, ordinary dissolve, complement, inverted shadow and shadow-only descriptors. Assert a field change, null and re-centre each dirty cascade and static point maps once with frozen geometry. Also flip the flag on an already-dissolving draw so unchanged ShadowCastKind cannot hide a changed cut. Pin the cascade 96/112 and point 144/160 payloads, fragment-stage visibility and every changed pair. Preserve skinned dissolve fragments and their original resource layouts. GPU CascadeCutsTheOutsideHalf and PointCutsTheOutsideHalf use the AlphaCutoutShadowGpuTests arrangement, a vertical sun and a half-cut box, allowing only the existing PCF footprint at the edge. Add opt-out byte-identity controls.

```csharp
Assert.Equal(ShadowCastKind.Dissolving, solidCutKind);
Assert.Equal(ShadowDepthVariant.Cutout, ShadowDepthSelection.ForRigidSpan(solidCutKind, true));
Assert.Equal(ShadowCastKind.None, nonCasterKind);
Assert.DoesNotContain("interiorCut", ShaderSources.SkinnedShadowDepthDissolveFrag);
Assert.DoesNotContain("interiorCut", ShaderSources.PointShadowSkinnedDissolveFrag);
Assert.Equal(1, cascadeRedrawsAfterFieldChange);
Assert.Equal(1, pointRedrawsAfterFieldChange);
Assert.Equal(noFieldShadowPixels, optOutShadowPixels);
```

- [ ] **Step 2: Run red.** `build-slot --label interior-shadows-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~InteriorCutShadowTests|FullyQualifiedName~ShadowDepthSelectionTests|FullyQualifiedName~PointShadowUboLayoutTests|FullyQualifiedName~ShaderSourceValidationTests"`. Metal: `build-slot --heavy --label interior-shadows-metal-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~InteriorCutShadowGpuTests`. Expect routing/cache and half-shadow failures after compilation, with zero skips for new correctness facts.
- [ ] **Step 3: Implement SetInteriorCut and caster/cache changes.** Carry render-relative world position and w through vCut on rigid dissolve and cutout vertices. Add the helper to both cascade dissolve fragments, both cascade cutout fragments and both point rigid dissolve fragments before depth writes. Keep existing alpha, dissolve, inverted and near-plane-pancake behavior. Save the old skinned-only fragments before changing the shared rigid names, then point GPU-skinned pipelines at them. CPU-skinned draws can use rigid layouts but must carry w = 0. Track field Version in both dirty checks, and include the per-instance cut bit in caster content signatures where kind alone would not reveal a change. No opted-in draws means existing opaque routing stays opaque.
- [ ] **Step 4: Run the headless and Metal commands green with labels interior-shadows-green and interior-shadows-metal-green.** Include ShadowShaderContractTests and SkinnedPointShadowShaderTests in the green headless filter. Expect zero new correctness skips. D3D11 and Vulkan legs run at final branch review, not per task.
- [ ] **Step 5: Commit explicit Task 5 paths.** Subject `feat(render3d): cut interior shadow casters and invalidate cached depth`.

### Task 6: Layer propagation and asynchronous TileWorld view driver

**Files:**

- Modify KhaozEngine.Terrain.Render3D/PropLayer.cs, PropLayerTopology.cs, PropRenderer.cs and PropClusterRenderer.cs.
- Modify KhaozEngine.TileWorld.Render3D/TilePropLayerDefinition.cs, TileWorldPropClusters.cs, ITileWorldScene.cs, TileWorldBuildQueue.cs, TileWorldBuildSnapshot.cs, TileWorldView.cs and TileWorldView.Flush.cs.
- Create: KhaozEngine.TileWorld.Render3D/TileWorldView.InteriorCut.cs and TileInteriorBuildInput.cs.
- Create tests: KhaozEngine.Render.Tests/TileWorld/TileWorldInteriorCutTests.cs, RecordingTileWorldScene.InteriorCut.cs and KhaozEngine.Render.Tests/Terrain/PropLayerInteriorCutTests.cs.
- Modify recording declarations in KhaozEngine.Render.Tests/TileWorld/TileRenderTestData.cs and existing TileWorldBuildQueueTests.cs.

**Interfaces:**

- Consumes: Task 3 constructor, CellSizeFor and Digest, Tasks 1 and 4 field and scene API. New public `bool TilePropLayerDefinition.CutInsideInteriors { get; init; }` and `bool PropLayer.CutsInsideInteriors { get; }` default false.
- Produces: append `bool cutInsideInteriors = false` to the existing PropLayer factory signatures, preserve it in WithHlod, WithLodCrossfade, WithIdentity and WithGeneration, and include it in PropLayerTopology comparisons.
- Produces: append `bool cutInsideInteriors = false` after the existing parameters of all six PropRenderer.DrawProps extension overloads. The two terminal overload signatures are the existing single/multipart forms through `PropDrawFilter? drawFilter = null, string? layerIdentity = null`, followed by the new bool. Existing convenience overloads forward the bool. Keep Queue and DrawShadowOnlyProps defaults uncut.
- Produces: new `void ITileWorldScene.SetInteriorCut(InteriorHeightField? field) { }`, forwarded by Scene3DTileWorldScene, and new `ITileMeshGeometry? TileWorldView.InteriorMeshes { get; set; }` and `InteriorHeightField? TileWorldView.AppliedInteriorCut { get; }`.
- Produces: new `TileWorldBuildKind.InteriorCut`, `int TileWorldBuildQueueOptions.MaxInteriorCutAppliesPerPump { get; init; } = 1` and its explicit budget branch. A separate TileWorldBuildQueue<TileInteriorBuildInput, TileInteriorBuildOutput> instance has MaxConcurrentBuilds = 1 and a single stable world-field key, independent of its moving window.
- Produces: new internal records `TileInteriorBuildInput(TileWorldDocument Document, TileWorldCatalogs Catalogs, ITileMeshGeometry Meshes, InteriorWindow Window, TileInteriorRoofSamples Samples)` and `TileInteriorBuildOutput(InteriorHeightField Field, IReadOnlyList<string> Issues)`. New snapshot methods `TileWorldDocument CaptureInteriors(TileWorldDocument document, InteriorWindow window)` and `TileWorldCatalogs CaptureInteriorCatalogs(TileWorldCatalogs catalogs)` on TileWorldBuildSnapshot. Copy mutable archetype fields and relevant objects as well as ground data.

- [ ] **Step 1: Write LayerCopiesKeepTheOptIn, AuthoredLodIsCutButMergedAndFoliageAreNot, RelevantEditsQueueOneBuild, FocusControlsRecentre, AppliedFieldAndPicksSharePublication and StaleInteriorResultsNeverApply.** Use the recording scene and a held IChunkBuildDispatcher. Assert no collection on the view/render thread, one active build, replacement of a queued request and no stale apply when an older generation completes after a newer request, InteriorMeshes = null, disposal or source replacement. Assert Indoors/wall/roof/corner edits queue and tree/material edits do not, including profile-managed and legacy region rebuild paths. Check a focus at 63.999 m then 64 m, independent of Observer. Log each issue once, including interior.step after Build. Null reader or no opted-in layers means no field build and a cleared applied cut. Add H1 and H2 cases: a 1.2 m tile world gets 2134 cells and a 512 m window, a re-centre over unchanged interiors uploads a field whose overlap bytes equal the previous field's, and adding a building lower than every other floor changes the encoding and rebuilds.

```csharp
Assert.True(layer.WithIdentity("trees").CutsInsideInteriors);
Assert.True(individualLodDraw.CutInsideInteriors);
Assert.False(mergedDraw.CutInsideInteriors);
Assert.False(foliageDraw.CutInsideInteriors);
Assert.Equal(1, maximumConcurrentInteriorBuilds);
Assert.Same(recordedSceneField, view.AppliedInteriorCut);
Assert.Null(fieldAfterDisabledWorkerCompletes);
Assert.Equal(1, occurrencesOfEachIssue);
```

- [ ] **Step 2: Run red.** `build-slot --label interior-view-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~TileWorldInteriorCutTests|FullyQualifiedName~PropLayerInteriorCutTests|FullyQualifiedName~TileWorldBuildQueueTests"`. Expect missing route and stale-generation failures, not timing assertions.
- [ ] **Step 3: Implement the exact route and driver above under rulings H1 and H2.** SceneBackend.DrawProps passes the layer flag through Terrain's extensions to RigidInstanceDraw for every individual full/LOD part. SceneBackend.DrawMerged never sets it. Snapshot mutable document/catalog values on the view thread, including intersecting roof footprints whose anchors lie outside the window, then collect and build on the sole interior worker. Retain one sample cache on that serialized worker. GltfTileMeshGeometry is single-threaded, so document that InteriorMeshes is dedicated to this driver and never call it concurrently from capture or another job. Compare relevant per-region-plane digests on dirty handling, including adjacent corner and wall dependencies and load/unload changes. Pump completion at Draw(focus), apply only the latest generation, and call SetInteriorCut and replace AppliedInteriorCut in the same view-thread publication. Preserve the previous field until a valid replacement lands. Disable/source changes cancel old generations and clear the scene and picks. Dispose the queue and release resident fields. Keep capture/prime paths from running collection inline on the render thread.
- [ ] **Step 4: Run green.** Use the same focused filter with label interior-view-green and add PropClusterRendererTests, TileWorldPropLayerTests and TileWorldSceneDescriptorTests. Expect all passing without sleeps or polling. Release held dispatchers explicitly. This task has no new GPU behavior beyond Tasks 4 and 5.
- [ ] **Step 5: Commit explicit Task 6 paths.** Subject `feat(tile-world): drive interior cuts from opted-in prop layers`.

### Task 7: Cut-aware object, walk-surface and camera picks

**Files:**

- Modify KhaozEngine.Primitives/RayMath.cs and KhaozEngine.Foundation.Tests/RayMathTests.cs.
- Modify KhaozEngine.TileWorld/TileWalkSurfaces.cs and KhaozEngine.TileWorld.Render3D/TileObjectRaycast.cs, TileWorldView.Picking.cs and TileWorldCameraProbe.cs.
- Create tests: KhaozEngine.TileWorld.Tests/TileWorld/TileWalkSurfaceCutTests.cs and KhaozEngine.Render.Tests/TileWorld/TileWorldInteriorPickingTests.cs.
- Modify tests: KhaozEngine.Render.Tests/TileWorld/TileObjectRaycastTests.cs and TileWorldCameraProbeTests.cs.

**Interfaces:**

- Consumes: Task 1 `bool InteriorHeightField.FirstUncut(Vector3 origin, Vector3 direction, float tStart, float tEnd, out float t)`, Contains and Task 6 AppliedInteriorCut plus the union of opted-in archetype ids.
- Produces: new overload `public static bool RayMath.IntersectObbY(Vector3 origin, Vector3 direction, Vector3 center, float yaw, Vector3 min, Vector3 max, out float tNear, out float tFar)`. Old overload forwards and retains inside-origin, zero-direction and NaN behavior.
- Produces: new `public readonly record struct TileObjectCut(InteriorHeightField? Field, IReadOnlySet<string> ArchetypeIds)` in TileObjectRaycast.cs. New overload `public static int TileObjectRaycast.Pick(TileWorldDocument document, TileWorldCatalogs catalogs, int plane, Vector3 origin, Vector3 direction, float maxDistance, BoundsSource bounds, List<TileObjectHit> hits, TileObjectCut cut)`, with the existing overload forwarding an inactive cut.
- Produces: extend `public static TileHit? TileWalkSurfaces.Raycast(TileWorldDocument doc, TileWorldCatalogs catalogs, int plane, Vector3 origin, Vector3 direction, float maxDistance, Func<TileObject, bool>? include = null, Func<TileObject, Vector3, bool>? acceptPoint = null)`. Include remains the object filter, acceptPoint runs before each candidate competes for best.
- Existing PickObjects, PickSurface and `float TileWorldCameraProbe.Reach(Vector3 origin, Vector3 direction, float length, float radius)` retain signatures.

- [ ] **Step 1: Write ObbExitPreservesEntryAndRayUnits, CutBoxReturnsFirstVisibleDistance, DoorwayPicksFloorOrBackWallBeforeCutTree, CutWalkSurfaceAllowsLowerCandidate and BoomIgnoresOnlyTheCutBranch.** Compare old/new entry distances for yawed boxes, inside starts, non-unit directions, zero direction and NaN. Clip an opted-in box segment to tFar and maxDistance, reject wholly cut segments, and sort a partially cut hit by its first uncut point. Check equality at maxDistance stays excluded and an uncut point only at tFar cannot revive a cut object. Use an open doorway, a tree reaching indoors, a floor and a back wall. Repeat with the flag off. Include multiple walk surfaces on the same object to prove rejection is per candidate. Test inflated camera bounds, hidden roofs and the existing origin-inside skip.

```csharp
Assert.Equal(oldNear, newNear);
Assert.True(exitLiesOnTheYawedBox);
Assert.Equal(wallId, doorwayHits[0].ObjectId);
Assert.DoesNotContain(whollyCutTreeId, doorwayHits.Select(h => h.ObjectId));
Assert.Equal(floorY, surface.Value.Point.Y);
Assert.Equal(requestedLength, boomInsideCutBranch);
Assert.True(boomAtVisibleBranch < requestedLength);
```

- [ ] **Step 2: Run red.** Foundation: `build-slot --label interior-ray-red -- dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter FullyQualifiedName~RayMathTests`. TileWorld: `build-slot --label interior-walk-red -- dotnet test KhaozEngine.TileWorld.Tests/KhaozEngine.TileWorld.Tests.csproj -c Release --filter FullyQualifiedName~TileWalkSurfaceCutTests`. Render: `build-slot --label interior-picks-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~TileWorldInteriorPickingTests|FullyQualifiedName~TileObjectRaycastTests|FullyQualifiedName~TileWorldCameraProbeTests"`. Expect new exit/filter or doorway assertions failing.
- [ ] **Step 3: Implement the exact overloads and filters above.** Reuse the existing slab math's far bound. For opted-in objects only, query FirstUncut with tEnd = min(tFar,maxDistance), then reject a returned t >= tEnd so the object's segment is half-open despite the core query's inclusive endpoint. Default TileObjectCut must be inactive without dereferencing a null default ArchetypeIds. Cache the view's point-filter delegate rather than allocate it per pick. Camera picks inherit the field through PickObjects and PickSurface. Preserve the camera's origin-inside rule even when a cut changes a hit's entry from zero to positive, using an uncut bounds containment check rather than treating that adjusted distance as a new outside-origin obstruction. Leave external MapDoc, editor and physics queries alone.
- [ ] **Step 4: Run the same three focused commands green with labels interior-ray-green, interior-walk-green and interior-picks-green.** Add existing TileWorldCameraProbeAllocationTests to the render filter so cached query plumbing keeps its allocation contract.
- [ ] **Step 5: Commit explicit Task 7 paths.** Subject `feat(tile-world): filter interior pixels from picks and camera probes`.

### Task 8: Target outline and silhouette parity

**Files:**

- Modify KhaozEngine.Render3D/Scene3D.TargetOutlinePass.cs, Scene3D.SilhouettePass.cs and Scene3D.InteriorCut.cs.
- Modify KhaozEngine.Render3D/Rendering/TargetOutlineRenderer.cs, TargetOutlineRenderer.Skinned.cs and SilhouetteRenderer.cs. New companions TargetOutlineRenderer.InteriorCut.cs and SilhouetteRenderer.InteriorCut.cs hold the new bindings and payload helpers.
- Modify KhaozEngine.Render3D/Internal/ShaderSources.TargetOutline.cs, ShaderSources.Silhouette.cs and inspect ShaderSources.MotionPreserve.cs for the derived fragment.
- Modify KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs, TileWorldPropClusters.Outline.cs and TileWorldView.cs.
- Create tests: KhaozEngine.Render.Tests/Render3D/InteriorCutHighlightTests.cs and Gpu/InteriorCutHighlightGpuTests.cs.
- Modify tests: KhaozEngine.Render.Tests/Gpu/ShaderSourceValidationTests.cs, TileWorld/TileWorldTargetOutlineLodTests.cs and TileWorld/RecordingTileWorldScene.InteriorCut.cs.

**Interfaces:**

- Consumes: Tasks 4 and 6 cut uniforms, texture, layer opt-in and applied field.
- Produces: new overloads `public void Scene3D.DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world, float dissolve, bool dissolveComplement, bool cutInsideInteriors)` and `public void Scene3D.DrawMeshSilhouette(MeshHandle mesh, Matrix4x4 world, Color color, float widthMetres, bool cutInsideInteriors)`. Existing overloads forward false.
- Produces: matching ITileWorldScene overloads, with default implementations forwarding the existing overloads and the Scene3DTileWorldScene implementation forwarding the bool to Scene3D.
- Produces: new `internal void SetInteriorCut(IGpuTexture texture, in InteriorCutUniforms uniforms)` on TargetOutlineRenderer and SilhouetteRenderer. Queued rigid outline parts and silhouette tuples gain a bool. The skinned payload writer stays uncut.
- Produces: new `ShaderSources.TargetOutlineRigidFullMaskFrag` and `TargetOutlineRigidVisibleMaskFrag`. Existing TargetOutlineFullMaskFrag and TargetOutlineVisibleMaskFrag remain unchanged for skinned pipelines.

- [ ] **Step 1: Write HighlightPayloadsKeepOffsets, SkinnedOutlineStringsAndLayoutStayUncut, IndividualOutlineAndSilhouettePassTheLayerFlag and MergedOutlineNeverCuts.** Assert Params.w/y flags and 160/176 tails with a 192-byte payload and 256-byte stride, preserving original offsets. Assert the two rigid mask fragments cut and the two skinned fragment strings do not change. Compile rigid, skinned and silhouette temporal/non-temporal pairs. GPU BothOutlineCoverageTargetsCut tests both occlusion modes, absence of full and visible coverage inside the projected cut and the composited edge on the cut boundary. SilhouetteCutsTheExtrudedHull tests the extruded render-relative position, a 0.05 m width and no hull pixels inside the projection. HighlightOptOutIsByteIdentical compares null field and flag-off field, with temporal both off and on.

```csharp
Assert.Equal(1f, rigidOutlineParams.W);
Assert.Equal(1f, silhouetteParams.Y);
Assert.Equal(0f, skinnedOutlineParams.W);
Assert.Equal(192, payloadBytes);
Assert.Equal(256, slotBytes);
Assert.Equal(0, coveredInteriorPixels);
Assert.Equal(noFieldPixels, flagOffPixels);
```

- [ ] **Step 2: Run red.** `build-slot --label interior-highlights-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter "FullyQualifiedName~InteriorCutHighlightTests|FullyQualifiedName~TileWorldTargetOutlineLodTests|FullyQualifiedName~ShaderSourceValidationTests"`. Metal: `build-slot --heavy --label interior-highlights-metal-red -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~InteriorCutHighlightGpuTests`. Expect payload/coverage failures after compilation and zero new correctness skips.
- [ ] **Step 3: Implement the exact overloads, queue flags and renderer setters.** Rigid masks use set 2 with the cut texture and sampler. Keep skinned set 2 for the palette and keep their original fragment pair. Extend the shared draw UBO layout compatibly, writing zero flag/tail for skinned draws. Cut both rigid coverage targets using interiorCut(vWorldPos - RenderOrigin.xyz). DrawIndividualOutline carries layer.CutsInsideInteriors through OutlineSink, DrawMergedOutline explicitly passes false. Silhouette uses set 1, passes the extruded position from vertex to fragment and cuts at that position. The derived SilhouetteMotionFrag inherits the discard. DrawSilhouettedObject determines the flag from the opted-in archetype union. Bind one set per renderer and rebuild on texture identity changes, not per highlighted mesh.
- [ ] **Step 4: Run headless and Metal green with labels interior-highlights-green and interior-highlights-metal-green.** Add TargetOutlineRendererTests, SkinnedTargetOutlineRendererTests and SilhouetteRendererShapeTests to the headless filter. Expect zero new correctness skips. D3D11 and Vulkan run at final branch review. Do not re-bake goldens.
- [ ] **Step 5: Commit explicit Task 8 paths.** Subject `feat(render3d): keep interior outlines and silhouettes aligned with props`.

### Task 9: Timing source, documentation and final verification

**Files:**

- Create: KhaozEngine.Render.Tests/Gpu/InteriorCutCostPerfGpuTests.cs, Gpu/InteriorCutCostScene.cs and Render3D/InteriorCutCostSceneTests.cs.
- Modify: CHANGELOG.md, docs/USING-KHAOZENGINE.md, docs/INDEX.md and docs/design/INTERIOR-CUT-DESIGN-2026-10-18.md status.
- Modify READMEs: KhaozEngine.Primitives/README.md, KhaozEngine.Render3D/README.md, KhaozEngine.Terrain.Render3D/README.md, KhaozEngine.TileWorld/README.md and KhaozEngine.TileWorld.Render3D/README.md.
- Sweep every tracked Markdown file for the names/layouts below and change only descriptions made stale by this feature. Record any additional exact paths before staging them.

**Interfaces:**

- Consumes: all prior task APIs, existing TemporalResolveCostPerfGpuTests and ShadowCascadeCullPerfGpuTests measurement patterns, GpuTimingSensitive collection and RequiresRealGpu gate.
- Produces: new sealed `InteriorCutCostPerfGpuTests(ITestOutputHelper output)` in KhaozEngine.Tests.Gpu with one `[GpuFact(RequiresRealGpu = true)] public void VillageCutCostIsRecorded()` and `[Collection("GpuTimingSensitive")]` on the class.
- Produces: new nested internal `enum Mode { ShadowsOff, NoOptIn, EmptyField, VillageInteriors }`, `internal static Mode[] OrderFor(int round)` and `internal static bool IsCaptureDevice(string backend, string deviceName)` on that class. IsCaptureDevice permits NVIDIA T4 only on direct3d11-native and vulkan-native, so ordinary workflow software adapters and real Metal never start timing. No new dependencies or timestamp-query API.
- Produces: new internal sealed InteriorCutCostScene in KhaozEngine.Tests.Gpu, `internal static InteriorCutCostScene Create()`, `IReadOnlyList<PropPlacement> Trees { get; }`, `IReadOnlyList<InteriorVolume> Interiors { get; }`, `float SunAngleFor(int frame)`, `bool CutsFor(InteriorCutCostPerfGpuTests.Mode mode)` and `InteriorHeightField? FieldFor(InteriorCutCostPerfGpuTests.Mode mode)`. Instance members listed here are internal. This GPU-free fixture owns the deterministic scene inputs both test classes inspect.

- [ ] **Step 1: Write the failing deterministic source tests.** RotatingModesCoverAllFourBaselines asserts each of four consecutive orders has four unique modes and each mode occupies every position once. CaptureDeviceExcludesMetalAndSoftware asserts the device gate. Add fixture assertions that EmptyField keeps the layer opted in with a non-null all-zero field, VillageInteriors has non-empty cells and the sun angle changes each frame. Keep these assertions GPU-free and never call VillageCutCostIsRecorded from them.

```csharp
Assert.Equal(4, order.Distinct().Count());
Assert.False(InteriorCutCostPerfGpuTests.IsCaptureDevice("metal-native", "Apple M2 Max"));
Assert.False(InteriorCutCostPerfGpuTests.IsCaptureDevice("vulkan-native", "llvmpipe"));
Assert.True(InteriorCutCostPerfGpuTests.IsCaptureDevice("direct3d11-native", "NVIDIA Tesla T4"));
Assert.True(emptyModeOptsIn);
Assert.False(emptyField.Texels.Span.ContainsAnyExcept((byte)0));
Assert.NotEqual(sunAngleBefore, sunAngleAfter);
```

- [ ] **Step 2: Run red without GPU timing.** `build-slot --label interior-cost-source-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~InteriorCutCostSceneTests`. Expect missing source-helper or fixture assertions, no GPU capture.
- [ ] **Step 3: Implement the source helpers and fixture.** Use 3600 deterministic tree placements across a 500 m village extent, with solid trunks, cutout leaf cards and 49 box buildings. Render at 960x600 with four 2048 cascades, matching ShadowCascadeCullPerfGpuTests. SunAngleFor advances by 0.2 / 60 degrees per frame. FieldFor returns null for ShadowsOff and NoOptIn, a non-null zero field for EmptyField, and the village field for VillageInteriors. CutsFor is true only for the last two modes. Measure frame blocks on one device in rotating four-mode order, following TemporalResolveCostPerfGpuTests (15 rounds, 2 warm frames and 12 measured frames per mode). Print every block, adapter/backend, mode medians, paired differences, mode 3 minus mode 2 routing cost and mode 4 minus mode 2 total cost against 0.1 ms. Assert configuration and coverage validity only. IsCaptureDevice returns before any warm-up or measurement on all other adapters. This task never runs the timing fact.
- [ ] **Step 4: Complete the living-doc and Markdown sweep.** Add one minor-feature CHANGELOG entry describing the static cut, opt-in, both shadows, picks, outlines, silhouettes and exclusions under the current unreleased version heading, without selecting or bumping a number. Do not insert a literal `## Unreleased` above the numeric heading because check-doc-versions requires the newest heading to equal Directory.Build.props. If no unreleased version exists at execution, leave version staging to the orchestrator before this step. Update Tile world use (InteriorMeshes, issue logging and picks), DrawProps, target outlines, silhouettes and the camera probe in USING-KHAOZENGINE. Update all five package READMEs, including the field ray queries, OBB exit overload and walk-surface point filter. Change the INDEX and spec status to implemented with backend/timing evidence still pending until collected. Run `git grep -n -E 'SpecParams|1344|1376|binding[ =]+14|InteriorCut|DrawProps|DrawUbo|PayloadBytes|Silhouette|TargetOutline|TileWalkSurfaces|TileObjectRaycast|camera boom' -- '*.md'` and correct stale binding lists and quoted payload layouts. Historical records remain history, add a current-layout note where a quoted contract would mislead. Do not change #1473 or #1474 scope.
- [ ] **Step 5: Run source tests green and the final local gates.** Use the Step 2 command with label interior-cost-source-green, then run once each:

```bash
build-slot --label interior-final-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label interior-final-headless -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
bash scripts/check-doc-versions.sh
sh scripts/check-file-size.sh --tree
```

Expect zero build warnings, all headless tests passing and guard exit codes 0. GPU facts skipped by the headless command are not rendering evidence. The orchestrator reconciles current main and repeats only checks invalidated by integration.

- [ ] **Step 6: Commit explicit Task 9 paths.** Subject `docs(interior-cut): document the feature and add hosted cost capture`. Do not change Directory.Build.props, pack or dispatch timing.

## Orchestrator final review and evidence

- [ ] Review the whole branch, including every Plan deviation, hold-point resolution, shader pair and byte-identity control. Verify the approved spec against the final diff. A task report is not verification.
- [ ] At that reviewed commit, run cross-platform-gpu once with all legs, bake=false, tier=push and an empty renderTestFilter. This runs the complete existing Golden suite as well as all new correctness facts on metal-native, direct3d11-native and vulkan-native. Preserve and compare the checked-in backend references, with zero golden drift. Capability/virtual-adapter skips allowed by CROSS-PLATFORM do not excuse any skipped new interior correctness fact. The cost source's device gate prevents an extra measurement here.
- [ ] After that final review and correctness gate, the orchestrator makes the one approved OC8 dispatch: `gh workflow run gpu-hosted-bench.yml -f ref=feature/interior-cut -f filter=FullyQualifiedName~InteriorCutCostPerfGpuTests -f legs=all`. Ensure the branch names the reviewed commit before dispatch. The three hosted T4 jobs cover Windows D3D11, Windows Vulkan and Linux Vulkan in one dispatch. Record the exact commit, run URL and all per-leg artifacts. No task invokes this command, and this plan grants no second dispatch or local timing run.
- [ ] Record mode 3 minus mode 2 and mode 4 minus mode 2 against 0.1 ms, including spread, without turning the budget into a flaky test assertion. A budget miss returns to design review. Do not silently add the risk section's cut-only opaque pipeline or a toroidal upload scheme.
- [ ] Set the final spec/INDEX status from actual evidence. Keep IC6 owner captures and Grimhollow adoption as consumer work. Merge, push, version reconciliation and release remain the orchestrator's separately authorized finish.

## Plan self-review

- Spec coverage: description, packing, rasterization and rays are Task 1. Roof sampling is Task 2. Volumes, wall checks and adapter issues are Task 3. Texture/layout/colour/temporal are Task 4. Both shadows and caching are Task 5. Opt-in, snapshots, change detection, queueing and publication are Task 6. Object, walk and boom picks are Task 7. Outline and silhouette are Task 8. Timing source, docs and the full final gates are Task 9 plus the orchestrator checklist. MapDoc and world_validate remain their filed follow-ups.
- Step scan: each task has named failing assertions, a red command, exact new interfaces, a green command and an explicit-path commit. Bodies are left to implementers. Existing API quirks are called out rather than replaced from the sketch.
- Type consistency: InteriorHeightField and InteriorCutUniforms are the shared CPU and render contracts. Every renderer setter uses the same texture/uniform signature. TileWorld's public namespace remains KhaozEngine.TileWorld except TileObjectRaycast/TileObjectCut in KhaozEngine.TileWorld.Render3D. PropLayer stays a struct and DrawProps stays a Terrain extension.
- Review Focus: all five uncovered failures have tests in Tasks 1, 6, 4, 5 and 4 respectively. Additional checks cover concave outlines, cut-bit-only shadow invalidation, refreshed roof meshes and camera starts inside inflated bounds.
- Proportion: the plan is shorter than the 850-line spec, and code blocks carry signatures, assertions and commands rather than implementation bodies. The nine tasks retain the suggested review boundaries with D3 moving Terrain submission into its owning task.
- Remaining gaps: none. The orchestrator settled the encoding and window hold points as H1 and H2. No product implementation, GPU run, timing run or version selection is part of writing this plan.
