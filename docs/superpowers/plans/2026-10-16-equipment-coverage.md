# Equipment Coverage Implementation Plan

Historical construction and display APIs in this document are superseded by the GPU path in
[Amendment R](../../design/EQUIPMENT-COVERAGE-AMENDMENT-R-2026-10-18.md). The exact geometry certifier and pinned
reference remain offline evidence. The CPU cost authorization is retired and cannot start a workload.


> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep a closed skinned host behind close-fitting rigid equipment through bounded, authored host conformation, with the same final surface in every render pass.

**Architecture:** Render3D owns immutable host metadata, source provenance, fixed refinement, geometric certification and a canonical CPU-prepared surface. Attachments owns a separate GPU-free companion definition. Attachments.Render3D binds exact hosts and equipment, composes the existing rigid frames and requests the renderer's prepared surface without adding an Attachments dependency to Render3D.

**Tech Stack:** C#, System.Numerics, existing glTF importer, engine GPU abstraction and native Metal, Direct3D11 and Vulkan backends, xUnit, existing snapshot and temporal fixtures.

**Spec:** [Approved equipment coverage design](../../design/EQUIPMENT-COVERAGE-DESIGN-2026-10-16.md), approved with its visible collar implication on 2026-10-16. Source audit base is `0bd787b5c`, written draft was `73e763bea`.

**Status:** Owner approved this plan and subagent-driven execution. Tasks 1 to 3 are implemented and reviewed through `2304c2c0`, with [Task 1 checkpoint evidence](proofs/2026-10-16-equipment-coverage-task1.md). Task 4 safety fixes are separately reviewed through `afd0fbae3`. Task 4 continues under the owner-approved [construction and render-error amendment](../../design/EQUIPMENT-COVERAGE-AMENDMENT-2026-10-16.md) and its [execution supplement](2026-10-16-equipment-coverage-amendment.md), which supersede the affected interfaces and steps below. Tasks 4 to 10 remain open. Work belongs on `feature/equipment-coverage` in the existing engine equipment-coverage worktree. Do not change consumer art or its acceptance gates.

## Global Constraints

- Preserve the authored rig, animations, base host asset and rigid equipment geometry. Only the eligible posed render surface changes.
- Core strength is exactly 1. Collar strength is finite in `[0,1]`. Every point outside the region and on its boundary has strength 0.
- The inward support is the actual displayed triangle sheet, a single-valued height field with a simple convex boundary, no holes and no overhangs.
- Keep attachment JSON v1/v2 unchanged. Host and equipment coverage sidecars each use strict format 1. Requested missing or malformed coverage refuses explicitly.
- Attachments remains BCL-only. Attachments.Render3D references only Attachments and Render3D. Add no package or third-party dependency.
- Initial limits are 32 regions, 4 disjoint active commands, 1,024 inward support triangles and 256 boundary edges per command, refinement level 1 to 4, and 65,536 final vertices and triangles per host.
- Skin original vertices before barycentric refinement. Never interpolate rest weights and then skin a refined vertex.
- Validate every submitted pose, including intermediate blends. Exceeding recession or capacity refuses, never clamps or truncates an uncleared core.
- Protected positions and zero-displacement skin data remain exact. Preserve welded boundaries and original material and shading seams.
- Covered hosts use one canonical CPU-prepared surface even with `UseGpuSkinning` enabled. Uncovered actors retain their selected CPU/GPU route.
- Colour, depth, normals, every cascade and point face, outlines, dissolve and motion use that surface. Motion uses previous final positions.
- Warm valid preparation, validation and submission allocate zero managed bytes. Growth occurs before recording, with explicit capacities and deferred resource retirement.
- Refuse unsupported mirrors, singular/projective/nonfinite or numerically uncertifiable transforms. Never freeze an actor or mutate simulation on refusal.
- Public fixtures are synthetic. Private consumer meshes, scripts, images, numerical datasets and personal filesystem paths never enter this repository.
- Every local build/test/render runs through `build-slot`, with `--heavy` for full suites and rendering. No local stress, throughput test or repetition loop.
- Load measurements require separate permission identifying workload, runner, iterations and duration. Use the hosted stress workflow. Native GPU performance remains unproved if no permitted runner exists.
- Proposed acceptance budgets remain zero warm allocations, at most 2 ms added preparation/validation for 32 typical actors, and at most 15 percent completed-frame GPU overhead on each native reference GPU. These are gates, not measured claims.
- No release tag or consumer pin change follows automatically. Follow the current engine release ritual only after separate authorization.

## Review Focus

1. Same numeric mesh handle in two scenes, recycled slots, and actor respawn must never reuse another actor's geometry or motion history. Tests belong to Tasks 5 and 7.
2. Caller mutation after import, upload or queue must not change accepted coverage, especially indices and inverse binds currently retained by reference. Tests belong to Tasks 2, 5 and 6.
3. One source primitive placed by several rigid nodes must select the actual displayed occurrences without losing transforms or inventing a single proxy sheet. Tests belong to Task 2.
4. Outline-only, camera-culled and shadow-only actors must prepare and upload the same final surface even when there is no ordinary colour draw. Tests belong to Tasks 5 and 8.
5. Equip changes during an origin rebase, resize or near-plane crossing must remove old skin history without resetting every unrelated actor. Tests belong to Task 7.

## Execution and source map

Tasks are sequential review units. Task 1's two schemas may be edited independently, but do not run concurrent implementation against the renderer files. Each task ends in its tested commit. The orchestrator independently verifies the diff, command exits and commit, then pushes the validated task checkpoint before proceeding. Do not merge a partially working capability to main.

| Existing seam | Required change | Task |
| --- | --- | --- |
| `Models/GltfLoader.SkinnedMaterialParts.cs`, `Models/GltfLoader.Skinned.cs`, `Models/GltfLoader.MaterialParts.cs` | Preserve source identity through grouping and actual import transforms. Existing parts retain material identity, not triangle provenance. | 2 |
| `Scene3D.SkinnedMeshLoading.cs`, `Scene3D.Unload.cs` | Snapshot covered source arrays and CPU indices, retain map/refinement lifetime and handle generation. | 2, 5 |
| `Scene3D.SkinnedSubmission.cs`, `Scene3D.cs`, `Scene3D.ShadowCasters.cs`, `Scene3D.SkinnedCasters.cs`, `Scene3D.SkinnedPointShadows.cs` | Replace scene-wide mutually exclusive route gates with independently populated CPU/GPU lists. | 5 |
| `Rendering/ModelRenderer.Skinned.cs`, `Rendering/PointShadowRenderer.Skinned.cs` | Draw prepared vertices with the matching fixed refined index buffer and material ranges. | 5 |
| `Scene3D.SkinnedTargetOutlines.cs`, `Rendering/TargetOutlineRenderer.Skinned.cs` | Consume the prepared slice, bypass separate skinning for covered outlines. | 5 |
| `Scene3D.MotionTarget.cs`, `Internal/MotionHistory.cs`, `Scene3D.TemporalResolve.cs` | Retain previous final positions and reject changed temporal bounds. | 7 |
| `Rendering/TemporalResolveRenderer.cs`, `Rendering/TemporalSplitResolve.cs` and their entry variants | Add an explicit transition rejection-mask input. None exists at the audit base. | 7 |
| `AttachmentSetCache`, `AttachmentSetBinding`, `BoundAttachmentSet`, `IAttachmentScene` | Reuse asset verification, binding transforms and borrowed-draw lifetime through opt-in coverage. | 6 |
| `Gpu/TemporalFixture.cs`, `Gpu/SkinnedAttachmentGpuScene.cs`, `Gpu/SkinnedPointShadowScene.cs` | Reuse native snapshot, material, motion and shadow proof infrastructure. | 8 |

Paths in the first seven rows are relative to `KhaozEngine.Render3D/`. The attachment row is in `KhaozEngine.Attachments.Render3D/`. GPU fixture paths are in `KhaozEngine.Render.Tests/`.

## Interface rules used below

All names introduced below are proposed additions, not existing APIs. Keep existing constructors, overloads and interface members intact. Small data records may share the file for their coherent concern. Do not create one file per scalar record or grow an existing oversized file to avoid defining a module.

`CoverageFailure` is an immutable value with `Code`, host identity, equipment instance ID, command ID and relevant counts. Success uses `Code=None`. Codes include the spec's refusal reasons plus `UnsupportedAdapter`. Load-time malformed data throws `InvalidDataException` with asset identity, before GPU allocation. Frame-time unsupported geometry returns a failure with no usable token or partial draw.

Test commands below run once for red and once after implementation for green, not in a repeat-until-pass loop. Missing API compilation is an expected initial red for a new seam. Later reds must be the named assertion, not unrelated setup failure. Keep full logs and return exit codes. A review fix reruns only affected tests until the final full solution gate.

### Task 1: Immutable sidecars and canonical identity

**Files:**
- Create `KhaozEngine.Render3D/Coverage/HostCoverageMap.cs`, `HostCoverageMapJson.cs` and `CoverageFailure.cs`.
- Create `KhaozEngine.Attachments/EquipmentCoverageDefinition.cs` and `EquipmentCoverageJson.cs`.
- Create `KhaozEngine.Render.Tests/Render3D/HostCoverageMapTests.cs` and `KhaozEngine.Attachments.Tests/EquipmentCoverageJsonTests.cs`.
- Modify `KhaozEngine.Attachments.Tests/JsonTests.cs` only to pin legacy byte compatibility.

**Interfaces:**
- Produce `HostCoverageMapJson.Read(ReadOnlySpan<byte> json, string identity) -> HostCoverageMap`, `Write(HostCoverageMap map) -> byte[]`, and `SemanticSha256(HostCoverageMap map) -> string`.
- Produce equivalent `EquipmentCoverageJson.Read/Write/SemanticSha256` methods returning `EquipmentCoverageDefinition`.
- `HostCoverageMap` exposes immutable `HostSha256`, topology entries `(Mesh, Primitive, Vertex, SurfaceId)`, regions, refinement and authoring. A region has an ordinal key, triangle ranges with `Core` or `Collar` role, and `(SurfaceId, Strength)` entries. Refinement entries pair source triangle ranges with level 1 to 4.
- `EquipmentCoverageDefinition` exposes immutable exact `AttachmentSha256`, semantic `HostMapSha256` and ordered commands. Each command has `uint Id`, `uint PartId`, `string HostRegion`, `Matrix4x4 CoverageLocal`, inward source triangle ranges, `CollarWidth`, `MinZ`, `MaxZ`, `Clearance` and `MaxWorldRecession`. `Clearance` is a required world-space distance. Caps and collar width are in coverage coordinates.
- Source triangle ranges are `(int Mesh, int Primitive, int FirstTriangle, int Count)`. Name the Render3D record `CoverageTriangleRange`. Keep each package's schema records inside its own dependency boundary. Equipment records do not reference Render3D types. Define the shared Render3D `CoverageFailure` and `CoverageFailureCode` values from the interface rules here, before later tasks consume them.

- [x] **Step 1: Write `Canonical_round_trip_has_fixed_bytes`, `Malformed_or_over_limit_sidecar_refuses` and `Legacy_attachment_bytes_are_unchanged`.** Construct minimal synthetic JSON inline. Pin member order, sorted command IDs/region keys, normalized negative zero, exact format 1, domain-separated semantic digests, independent byte hashes, and rejection before over-capacity allocation. Top-level host member order is `formatVersion`, `hostSha256`, `topology`, `regions`, `refinement`, `authoring`. Equipment order is `formatVersion`, `attachmentSha256`, `hostMapSha256`, `commands`. Nested fields use the interface declaration order above, fixed by literal expected JSON.

```csharp
Assert.Equal(canonical, HostCoverageMapJson.Write(HostCoverageMapJson.Read(canonical, "host")));
Assert.Throws<InvalidDataException>(() => HostCoverageMapJson.Read(unknownMember, "host"));
Assert.Throws<InvalidDataException>(() => EquipmentCoverageJson.Read(duplicateCommandId, "equipment"));
Assert.Equal(legacyV1Bytes, AttachmentSetJson.Write(legacyRigid));
Assert.Equal(legacyV2Bytes, AttachmentSetJson.Write(legacyMixed));
```

- [x] **Step 2: Run red.** Use `build-slot --label coverage-map-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~HostCoverageMapTests`, then the Attachments project with filter `FullyQualifiedName~EquipmentCoverageJsonTests`.
- [x] **Step 3: Implement the immutable models and strict codecs.** Snapshot caller collections. Use distinct hash prefixes `KhaozEngine.HostCoverageMap/1\n` and `KhaozEngine.EquipmentCoverage/1\n` before canonical UTF-8. Reject duplicate/unknown fields, invalid hashes, duplicate keys/IDs, zero IDs, nonfinite numbers, invalid ranges, negative budgets, nonpositive clearance/collar and invalid caps. Preserve all existing attachment JSON bytes and identity rules.
- [x] **Step 4: Run green with both commands, then existing Attachments `JsonTests`.** Add boundary rows for 32/33 regions and 4/5 commands, including duplicate logical vertices and differing JSON member order. Counts exceeding the spec refuse, not silently slice.
- [x] **Step 5: Commit explicit paths** with `feat(coverage): add immutable host and equipment sidecars`.

### Task 2: Import provenance and compiled support surfaces

**Files:**
- Create `KhaozEngine.Render3D/Models/GltfLoader.Coverage.cs`.
- Create `KhaozEngine.Render3D/Coverage/HostCoveragePart.cs`, `CoverageSourceTopology.cs` and `CoverageSupportSurface.cs`.
- Modify `KhaozEngine.Render3D/Models/GltfLoader.Skinned.cs`, `GltfLoader.SkinnedMaterialParts.cs`, `GltfLoader.MaterialParts.cs`, `GltfLoader.cs` and `SkinnedGltfMesh.cs` at their decode/grouping seams.
- Create `KhaozEngine.Render.Tests/Render3D/CoverageImportTests.cs` and `CoverageSupportTests.cs`.
- Modify `test-shared/SkinnedAttachmentGlbFixture.cs` only if existing fixture mutation cannot express the required synthetic cases.

**Interfaces:**
- Consume Task 1 maps and ranges.
- Produce `GltfLoader.LoadSkinnedPartsWithMaterials(ReadOnlyMemory<byte> glb, string identity, HostCoverageMap hostCoverageMap) -> IReadOnlyList<GltfSkinnedMeshPart>`.
- Add optional `HostCoveragePart? Coverage { get; init; }` to `SkinnedGltfMesh`, without changing old constructor signatures. Each part exposes its immutable `Map`, `SourceTriangles`, shared source topology and material slice.
- Produce `GltfLoader.LoadCoverageSource(ReadOnlyMemory<byte> glb, string identity) -> CoverageSourceTopology` for rigid support. This uses the same decode, winding, material grouping and node placement as ordinary rigid import, not a second glTF decoder.
- Produce `CoverageSupportSurface.Compile(CoverageSourceTopology source, ReadOnlySpan<CoverageTriangleRange> inwardFaces, in Matrix4x4 coverageLocal) -> CoverageSupportSurface`. `CoverageTriangleRange` is the Render3D source-range record. The result retains the actual complete equipment solid, selected inward sheet, convex boundary, projected query BVH, edge BVH and immutable identity.

- [ ] **Step 1: Write `Material_grouping_retains_exact_source_identity`, `Support_matches_all_displayed_node_occurrences` and `Changed_source_or_metadata_refuses`.** Cover two materials sharing a seam, split normals, UV absence, 32-bit indices, source winding, reordered influence slots, caller-array mutation and exact GLB hash mismatch. Assert legacy import output remains byte-identical when the optional map is absent.

```csharp
Assert.Equal(expectedSourceTriangles, imported.Coverage.SourceTriangles);
Assert.Equal(expectedBakedRigidPositions, rigidSource.Positions.ToArray());
Assert.Equal(beforeCallerMutation, HostCoverageMapJson.Write(imported.Coverage.Map));
Assert.Throws<InvalidDataException>(() => GltfLoader.LoadSkinnedPartsWithMaterials(changedGlb, "host", mapForOriginalGlb));
```

- [ ] **Step 2: Run red.** `build-slot --label coverage-import-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageImportTests|FullyQualifiedName~CoverageSupportTests'`.
- [ ] **Step 3: Preserve provenance while importing.** Carry mesh/primitive/triangle/corner identity before grouping. Retain rigid node-occurrence identity and apply exactly the transforms applied by the ordinary importer. A range selects all displayed occurrences of its source triangles, never an arbitrary first node. Validate the resulting support as one sheet. Unsupported overlapping occurrences refuse. The host path follows actual skinned import semantics and verifies one compatible skin, rather than trusting comments about node transforms. Do not change uncovered import behavior or expand this task into unrelated importer fixes.
- [ ] **Step 4: Compile and validate immutable topology and support.** Match welded vertices by exact rest positions and canonical normalized influence sets, allowing slot permutation but not independent surfaces. Verify range ownership, region disjointness, core strength, zero boundary, seam agreement and affected manifold topology. Retain a fingerprint of each decoded material slice's vertices, indices, skin and inverse binds so upload can reject metadata attached to a changed mesh. Check the projected selected triangles form one complete single-valued convex-boundary sheet, with no overlaps, holes or inconsistent shared heights. Reject over 1,024 support triangles or 256 boundary edges. Build finite query structures once. Keep all selected triangles traceable to the displayed equipment source.
- [ ] **Step 5: Run green and existing importer regressions.** Use the red filter plus `GltfSkinnedMaterialPartsTests`, `GltfMaterialPartsMemoryTests`, `GltfLoaderNodeTransformTests` and `GltfIndexValidationTests`. Add explicit multiple-node, nonuniform node-transform, mirrored selected sheet, empty selection, hole and two-depth-at-one-XY cases.
- [ ] **Step 6: Commit** with `feat(coverage): retain imported topology and compile inward support`.

### Task 3: Fixed refinement and independent synthetic oracle

**Files:**
- Create `KhaozEngine.Render3D/Coverage/CoverageRefinement.cs`, `CoverageSurfaceNormals.cs` and `CoverageScratch.cs`.
- Create `KhaozEngine.Render.Tests/Render3D/CoverageFixture.cs`, `CoverageGeometryOracle.cs`, `CoverageRefinementTests.cs` and `CoverageNormalTests.cs`.

**Interfaces:**
- Produce `CoverageRefinement.Build(CoverageSourceTopology source, HostCoverageMap map) -> CoverageRefinement` with immutable indices, material ranges, shading groups, rational edge splits and source triangle/barycentric records.
- Produce `CoverageRefinement.Interpolate(ReadOnlySpan<ModelVertex> posedSource, Span<ModelVertex> destination) -> void`.
- Produce `CoverageSurfaceNormals.Update(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, Span<ModelVertex> after, CoverageScratch scratch, out CoverageFailure failure) -> bool`.
- `CoverageScratch` is a capacity-owned reusable workspace for geometry operations, created for exact compiled topology and bounded commands. It contains no GPU device or attachment types.
- Test-only `CoverageFixture.CurvedPanel()` returns explicit source topology, authored map, posed vertices, rigid/support triangles and raw command data. Task 4 converts that data to its production command type. Variants cover a closed segmented ellipsoid, static gap, lower-edge bulge, independent crossing segment, second actor and second disjoint region.
- Test-only `CoverageGeometryOracle.Validate(before, after, fixture)` returns independent double-precision counts and distances for clearance, edge incidence, protected changes, folds and nonadjacent intersections. It must not call production geometry predicates.

- [ ] **Step 1: Write `Refinement_interpolates_posed_triangles`, `Material_seams_share_rational_splits` and `Normals_follow_deformation_without_crossing_shading_seams`.** Use a nonuniform two-bone pose. Require post-skin barycentric positions, identical rational edge splits across materials, no T-junctions and exact original piecewise-linear protected surfaces. Include a negative control that skins interpolated rest weights and demonstrably disagrees.

```csharp
Assert.Equal(Vector3.Lerp(posedA, posedB, 0.5f), refinedMidpoint.Position);
Assert.NotEqual(refinedMidpoint.Position, skinInterpolatedRestWeights);
Assert.Equal(0, oracle.UnmatchedInteriorEdges);
Assert.Equal(0, oracle.ProtectedPositionChanges);
```

- [ ] **Step 2: Run red.** `build-slot --label coverage-refinement-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageRefinementTests|FullyQualifiedName~CoverageNormalTests'`.
- [ ] **Step 3: Implement bounded conforming refinement.** Level means edge subdivision count, so level 4 gives 16 children for a fully selected triangle. Propagate shared-edge splits into protected neighbors without refining the entire body. Reuse welded position calculations while keeping shading/material duplicates. Check final vertex and triangle totals before allocation. Preserve `uint` indices and reject either total above 65,536.
- [ ] **Step 4: Implement normal/tangent deformation.** Form each nondegenerate triangle's before/after tangent bases from its two edges and unit normal. Use the resulting forward gradient for tangents and inverse transpose for normals, accumulating only within original shading groups. Preserve tangent handedness and zero/no-TBN semantics. Copy unchanged skin attributes exactly. Refuse degenerate gradients.
- [ ] **Step 5: Run green, including 65,536/65,537 capacity rows and zero-deformation identity.** Demonstrate that the independent oracle fails a cracked seam, reversed triangle and deliberately displaced protected vertex. Do not accept an oracle that merely echoes production results.
- [ ] **Step 6: Commit** with `feat(coverage): add fixed refinement and surface validation oracle`.

### Task 4: Bounded conformation and every-pose certification

**Files:**
- Create `KhaozEngine.Render3D/Coverage/CoverageCommandPose.cs`, `CoverageEvaluator.cs`, `CoverageCertification.cs` and `CoverageBvh.cs`.
- Extend Task 3's `CoverageScratch` in its own coherent coverage file.
- Create `KhaozEngine.Render.Tests/Render3D/CoverageEvaluatorTests.cs` and `CoverageCertificationTests.cs`.

**Interfaces:**
- `CoverageCommandPose` contains `uint Id`, `ulong EquipmentInstanceId`, resolved `int RegionIndex`, immutable `CoverageSupportSurface Support`, `Matrix4x4 CoverageWorld`, caps, collar width, world clearance, world recession budget and `float TransitionWeight` in `[0,1]`.
- Produce `CoverageEvaluator.TryEvaluate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource, in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, Span<ModelVertex> finalVertices, out CoverageFailure failure) -> bool`.
- Produce `CoverageCertification.Validate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after, in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, out CoverageFailure failure) -> bool` over the actual final float positions. Certification never reads a GPU or animation endpoint whitelist.

- [ ] **Step 1: Write `Actual_sheet_clears_the_core_and_preserves_the_host` and theory `Invalid_pose_returns_its_specific_refusal`.** The same synthetic curved-panel fixture must show positive baseline breakthrough, positive accepted core movement and zero protected changes. Include finite-gap removal and global-minimum-plane negative controls.

```csharp
Assert.True(baseline.BreakthroughArea > 0);
Assert.True(accepted);
Assert.True(result.CoreMovedArea > 0);
Assert.Equal(0, result.ProtectedPositionChanges);
Assert.Equal(0, result.OpenBoundaryEdges);
Assert.True(result.MinimumCoreClearance >= requiredClearance - certifiedErrorBound);
```

- [ ] **Step 2: Run red.** `build-slot --label coverage-geometry-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageEvaluatorTests|FullyQualifiedName~CoverageCertificationTests'`.
- [ ] **Step 3: Implement the exact support/collar construction from spec section 4.** Evaluate actual triangle heights and nearest actual boundary continuation. Use the full row-vector transform and world-length recession budget. Copy zero-work points without arithmetic changes. Convert world clearance conservatively using a lower singular-value bound and the support height field's maximum slope, then independently verify the final triangles' actual world clearance. Numerical error bounds must fit inside the authored safety margin, otherwise refuse `UnsupportedTransform`. No budget clamp or global target plane.
- [ ] **Step 4: Implement every-pose certification.** Check core domain and finite caps, projected patch injectivity, affected manifold/weld/area invariants, complete triangle/support overlays, triangle-solid clearance, new host intersections and disjoint positive-strength command ownership. Refit a preallocated host BVH. Include collar triangles in equipment/intersection checks. Ambiguous predicates refuse. A transition weight below 1 may return a valid closed transition surface but never reports certified full core clearance.
- [ ] **Step 5: Run green against the independent oracle.** Pin each refusal code with a reachable fixture: escaped core, cap contact, insufficient budget, vertical/folded patch, support gap, uncleared triangle interior despite clear vertices, rear-host penetration, overlapping commands, stale identity and capacity failure. Test positive scale/shear and explicit mirror/projective/nonfinite refusal. Test valid endpoints with an invalid intermediate pose. Require positive work so refuse-all and no-op implementations fail.
- [ ] **Step 6: Commit** with `feat(coverage): certify bounded posed host conformation`.

### Task 5: One prepared frame surface in mixed rendering routes

**Files:**
- Create `KhaozEngine.Render3D/Coverage/SkinnedHostConformer.cs`, `SkinnedCoverageFrame.cs` and `CoverageFrameArena.cs`.
- Create `KhaozEngine.Render3D/Scene3D.Coverage.cs` and `Scene3D.CoverageOutlines.cs`.
- Modify the source-map submission/loading/unload, colour, caster and outline files, `SkinnedInstanceDraw.cs`, `Scene3D.DrawDescriptors.cs` and `Scene3D.FramePrepare.cs`.
- Create `KhaozEngine.Render.Tests/Render3D/CoverageFrameTests.cs`, `CoverageMixedRouteTests.cs` and `CoverageOutlineTests.cs`.

**Interfaces:**
- Produce `Scene3D.CreateHostConformer(Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> meshes) -> SkinnedHostConformer`. Require a complete compatible set of material slices of one exact host map.
- `SkinnedHostConformer.TryPrepare(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, ReadOnlySpan<CoverageCommandPose> commands, MotionKey actorKey, out SkinnedCoverageFrame frame, out CoverageFailure failure) -> bool` consumes Tasks 2 to 4. It is actor-owned, disposable, render-thread-only and non-reentrant.
- `SkinnedCoverageFrame` is an opaque readonly value. Default means no coverage. It binds scene identity, frame generation, actor incarnation/key, host handle generations, map/refinement revisions, palette/model and all command instances/frames.
- Add `SkinnedCoverageFrame Coverage { get; init; }` to `SkinnedInstanceDraw`, initialized to default by its existing constructor.
- Add `Scene3D.DrawSkinnedOutline(MeshOutlineGroup group, in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette)`. Reuse descriptor model/dissolve fields and the same frame token. Existing overloads retain uncovered behavior.
- Expose per-frame `Scene3D.CoveredHostPreparationCount`. Internal test accessors `CoverageDiagnosticsForTests` and `CoverageVerticesForTests(SkinnedCoverageFrame frame)` read the actual route records and immutable arena, not a second evaluator. Diagnostics expose `CoveredCpuPreparations` and `UncoveredGpuDraws`.

- [ ] **Step 1: Write `Covered_cpu_and_uncovered_gpu_draws_coexist`, `Prepared_frame_is_owned_and_immutable` and `Outline_only_uses_the_final_surface` using existing fake-GPU fixtures.** Record one covered CPU draw beside an uncovered GPU draw, both with shadows and outlines. Reverse submission order. Assert correct counts, independent offsets and identical prepared vertex/index slices for all passes. Test outline-only and shadow-only submissions.

```csharp
Assert.Equal(1, diagnostics.CoveredCpuPreparations);
Assert.Equal(1, diagnostics.UncoveredGpuDraws);
Assert.Equal(expectedFinalVertices, scene.CoverageVerticesForTests(frame).ToArray());
Assert.Throws<InvalidOperationException>(() => otherScene.DrawSkinned(coveredDraw, palette));
Assert.Equal(beforeFailedRequest, scene.QueuedSkinnedInstancesForTests.Count);
```

- [ ] **Step 2: Run red.** `build-slot --label coverage-frame-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageFrameTests|FullyQualifiedName~CoverageMixedRouteTests|FullyQualifiedName~CoverageOutlineTests'`.
- [ ] **Step 3: Implement conformer ownership and frame snapshots.** Covered upload verifies the compiled slice fingerprint, then copies vertices, indices, inverse binds and metadata. Reject mutation before upload and isolate mutation after upload. Validate handle generation in the owning scene and encode scene identity in conformers/tokens. Raw legacy handles retain their existing scene-local semantics. Successful preparation copies final vertices, material ranges, command identities and exact pose into the scene arena before returning. Refined index buffers are immutable per compiled topology. Reserve/grow/upload before recording, then borrow immutable offsets. Dispose/reset/unload invalidates tokens through existing retirement paths.
- [ ] **Step 4: Make route selection per draw.** Populate/upload CPU and GPU lists independently. Remove global mutual exclusion from colour, directional caster counting/drawing, point-caster packing/drawing and motion upload scheduling. Covered draws never re-skin in a later pass. Use the refined index buffer and matching base vertex everywhere. Update conservative bounds from the final surface, including shadows outside the main camera.
- [ ] **Step 5: Add covered outline consumption.** TargetOutlineRenderer borrows the final geometry and fixed index ranges without calling its original-source skinning loop. Both full and visible masks use the same geometry before their existing material/dissolve rules. Maintain legacy outline-only preparation for uncovered controls.
- [ ] **Step 6: Run green plus `Scene3DSkinnedQueueTests`, `SkinnedDrawFlagTests`, `SkinnedMeshMaterialLifetimeTests`, `SkinnedTargetOutlineApiTests` and `SkinnedTargetOutlineRendererTests`.** Mutate input arrays after upload/prepare/queue, recycle a handle, change scene, actor key, frame or palette, dispose mid-lifetime and inject a partial allocation failure. Require exact no-op compatibility when coverage is absent.
- [ ] **Step 7: Commit** with `feat(render3d): share covered surfaces across mixed skinning passes`.

### Task 6: Attachment binding, atomic equipment changes and adapter compatibility

**Files:**
- Create `KhaozEngine.Attachments.Render3D/HostCoverageBinding.cs`, `LoadedEquipmentCoverage.cs` and `IAttachmentCoverageScene.cs`.
- Create `KhaozEngine.Attachments.Render3D/AttachmentSetCache.Coverage.cs`, making the existing cache partial if needed.
- Modify `AttachmentPartResources.cs`, `AttachmentSetBinding.cs`, `BoundAttachmentSet.cs` and `Scene3DAttachmentScene.cs` only at their coverage/pose access seams.
- Modify `KhaozEngine.TileWorld.Render3D/ITileWorldScene.cs` for opt-in engine adapter forwarding, without adding required members to the existing interfaces.
- Create `KhaozEngine.Render.Tests/Render3D/CoverageAttachmentBindingTests.cs` and `CoverageAttachmentLifetimeTests.cs`.

**Interfaces:**
- Produce `AttachmentSetCache.GetCoverage(LoadedAttachmentSet set, string key, string expectedSha256) -> LoadedEquipmentCoverage`. It verifies exact companion/set/mesh bytes through the existing `IAttachmentAssetSource`, then compiles Task 2 support surfaces. The cache owns those immutable resources.
- `CoveredAttachmentInstance` pairs a positive stable `ulong InstanceId`, `BoundAttachmentSet Set`, `LoadedEquipmentCoverage Coverage` and runtime transition weight.
- Produce `HostCoverageBinding(IAttachmentScene scene, Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> hosts, MotionKey actorKey)` and `TrySetEquipment(ReadOnlySpan<CoveredAttachmentInstance> equipment, out CoverageFailure failure) -> bool`.
- Produce `HostCoverageBinding.TryPrepare(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, in RigidInstanceDraw equipmentTemplate, out SkinnedCoverageFrame frame, out CoverageFailure failure) -> bool`, plus borrowed `ReadOnlyMemory<AttachmentDraw> PreparedEquipmentDraws`, valid until next preparation. No draw is queued by preparation.
- `IAttachmentCoverageScene : IAttachmentScene` adds `CreateHostConformer(Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> meshes) -> SkinnedHostConformer` and `DrawSkinnedOutline(MeshOutlineGroup group, in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette) -> void`. Existing `IAttachmentScene` remains unchanged. Existing uncovered custom adapters compile and run unchanged, while requested coverage against them refuses `UnsupportedAdapter`.

- [ ] **Step 1: Write `Binding_requires_exact_host_and_equipment`, `Failed_replacement_preserves_active_equipment` and `Legacy_adapter_rejects_only_requested_coverage` using fake scene and asset source.** Pin exact skeleton instance, host map, attachment byte hash, displayed mesh hashes, part IDs and instance IDs. Missing companion/region, skinned target part, stale map, duplicate instance and unsupported adapter must refuse before partial submission.

```csharp
Assert.False(binding.TrySetEquipment(badReplacement, out var failure));
Assert.Equal(previousInstances, binding.ActiveInstanceIdsForTests);
Assert.Equal(0, fakeScene.QueuedDrawCount);
Assert.Equal(CoverageFailureCode.UnsupportedAdapter, unsupported.Code);
```

- [ ] **Step 2: Run red.** `build-slot --label coverage-binding-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageAttachmentBindingTests|FullyQualifiedName~CoverageAttachmentLifetimeTests'`.
- [ ] **Step 3: Bind and stage complete actor state.** Reuse `AttachmentSetBinding.ComposeWorlds` and the exact contract alignment/joint/model composition. Resolve string region keys and source ranges at binding time. Prepare all equipment descriptors and all host commands from one immutable pose snapshot before requesting the host frame. Snapshot borrowed palettes/descriptors where the next preparation could overwrite them. Never use an unvalidated second pose to queue equipment after validating the first.
- [ ] **Step 4: Implement transactional equip/replace/unequip and disposal.** Validate and acquire all replacement resources before switching the active list. Failed replacements retain the old list and release provisional resources. Unequip prepares the current uncovered host next frame. Cache disposal invalidates borrowed bindings. Refusal returns no valid token and requires the caller's explicit uncovered current-pose presentation. It never freezes the actor, hides the body or changes ownership/gameplay.
- [ ] **Step 5: Run green plus existing `SkinnedAttachmentAssetTests`, `RigidAttachmentAssetTests`, `SkinnedAttachmentSubmissionTests` and `SkinnedAttachmentOutlineTests`.** Cover two actors sharing one cache, reversed submission, an independent second region, partial load, repeated disposal and unsupported custom adapters. Verify TileWorld's engine adapter forwards active tokens.
- [ ] **Step 6: Commit** with `feat(attachments): bind covered hosts with transactional equipment state`.

### Task 7: Final-position motion and transition history rejection

**Files:**
- Create `KhaozEngine.Render3D/Coverage/CoverageMotionHistory.cs` and `Rendering/CoverageTransitionMask.cs`.
- Modify `Scene3D.MotionTarget.cs`, `Scene3D.TemporalResolve.cs`, `Scene3D.TemporalFinish.cs` and the coverage frame/binding lifecycle seams.
- Modify `Rendering/TemporalResolveRenderer.cs`, `Rendering/TemporalSplitResolve.cs`, `Rendering/TemporalSplitResolve.AtDisplaySize.cs` and matching `Internal/ShaderSources.Temporal*.cs` entries that consume resolve inputs.
- Create `KhaozEngine.Render.Tests/Render3D/CoverageMotionTests.cs` and `CoverageTransitionTests.cs`.
- Create `KhaozEngine.Render.Tests/Gpu/CoverageTemporalGpuTests.cs`.

**Interfaces:**
- Produce internal `CoverageMotionHistory.Record(MotionKey actorKey, ulong revision, ReadOnlySpan<Vector3> finalPositions, in Matrix4x4 absoluteModel) -> void` and `TryGetPrevious(MotionKey actorKey, ulong revision, out CoveragePreviousSurface previous) -> bool`. `CoveragePreviousSurface` is a readonly ref struct with a borrowed previous-frame position span and absolute model, never a caller array. Keep this history alongside each existing active `MotionHistory`, with the same per-view/frame lifecycle.
- Produce internal `CoverageTransitionMask.AddChangedBounds(in CoverageChangedBounds bounds)` and `Prepare(IGpuCommandList commands, in FrameView current)`. Bounds contain current/previous absolute surface bounds and a near-plane uncertainty flag. The mask is an internal-resolution R8 texture, zero for no rejection and one for rejection.
- Add the mask to `TemporalResolveInputs` and bind a zero texture when unused. All temporal entry variants apply the same history rejection decision, including display-size resolve and precision variants.

- [ ] **Step 1: Write `Motion_uses_previous_final_vertices_after_rebase`, `Transition_rejects_both_bounds_only` and `Near_plane_transition_rejects_the_viewport`.** Pin previous final vertices at the refined count, including simultaneous rigid-model motion and render-origin rebase. A changed map, equipment set, topology, incarnation or refusal seeds current final positions and invalidates history only over changed projected bounds.

```csharp
Assert.Equal(previousFinalInCurrentRenderSpace, uploadedPreviousPositions);
Assert.Equal(currentFinalCount, uploadedPreviousPositions.Length);
Assert.True(mask.Contains(previousProjectedBounds.Union(currentProjectedBounds)));
Assert.False(mask.Rejects(unrelatedActorPixel));
```

- [ ] **Step 2: Run red.** `build-slot --label coverage-motion-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageMotionTests|FullyQualifiedName~CoverageTransitionTests'`.
- [ ] **Step 3: Replace only the covered CPU previous-position branch.** Reuse the existing parallel previous-position vertex stream. Append retained final vertices with the prior absolute model rebased into the current origin. Never reconstruct original source vertices from previous bones for a refined covered draw. Keep ordinary uncovered `CpuSkinnedMotion` behavior unchanged. Record history only for successfully presented frames, with frame/actor/revision identity and bounded double-buffered storage.
- [ ] **Step 4: Implement conservative transition rejection.** Clear/fill the mask before temporal resolve. Project current and previous changed bounds into each active frame view, conservatively round outward and reject the viewport on an uncertain near-plane crossing. Include unequip, missing next-frame coverage, refusal, reset, host replacement, disposal and failed replacement rollback semantics. Resize/recreate binds a correctly sized mask. Steady motion does not reset global temporal history.
- [ ] **Step 5: Cover dissolve transitions.** Conformation precedes existing ordinary/complementary/inverted-shadow dissolve. Whole-actor fade keeps full conformation. Independent equipment fade must explicitly change runtime transition weight or disable coverage, with history keyed by topology/equipment identity and genuine surface motion retained. Weights below 1 cannot claim full core clearance.
- [ ] **Step 6: Run green and existing `CpuSkinnedMotionTests` and `MotionTargetSkinnedWiringTests`.** Include equip/unequip with origin rebase, resize, behind-camera transition, zero motion key refusal for a covered actor, actor respawn, multiple views and missing-frame reappearance. Task 8 runs the new GPU sequence assertions on all native backends.
- [ ] **Step 7: Commit** with `feat(render3d): track conformed motion and reject changed history`.

### Task 8: Native render-pass and appearance proof

**Files:**
- Create `KhaozEngine.Render.Tests/Gpu/CoverageGpuScene.cs`, `CoverageSurfaceGpuTests.cs`, `CoverageShadowGpuTests.cs`, `CoverageOutlineGpuTests.cs` and `CoverageGoldenTests.cs`.
- Extend Task 7's `CoverageTemporalGpuTests.cs`.
- Create focused internal readback helpers in `KhaozEngine.Render3D/Scene3D.CoverageDiagnostics.cs` for depth, normals, masks, cascade and point-face targets. Keep them test-only, not a public capture API.
- Modify `.github/workflows/cross-platform-gpu.yml` only if the existing filter/artifact routing cannot carry the coverage proof. Preserve ordinary backend selection and golden ownership.

**Interfaces:**
- `CoverageGpuScene` builds Task 3's synthetic fixtures and supplies `Capture(CoverageCase testCase, bool coverage, bool gpuSkinning) -> CoverageCapture` and bounded transition sequences through existing `TemporalFixture` / `Render3DSnapshot.CaptureSequence`.
- `CoverageCapture` contains colour, depth, normal, motion, full/visible outline, cascade and six-face point-shadow readbacks, actual backend/device and frame diagnostics. Artifact naming identifies case, pass, view, route and coverage state.

- [ ] **Step 1: Write `Accepted_surface_has_no_breakthrough_or_new_hole`, `Every_shadow_face_uses_the_final_surface`, `Both_outline_masks_use_the_final_surface` and `Transitions_leave_no_original_skin_history` before adding goldens.** Use normal opaque off/on images at identical pose/camera/material/light. Require baseline breakthrough, retained protected witnesses, cleared accepted core and no new background holes. Require a removal-only negative control that visibly fails a grazing view.

```csharp
Assert.True(baseline.BreakthroughPixels > 0);
Assert.Equal(0, conformed.BreakthroughPixels);
Assert.Equal(0, conformed.NewBackgroundHoles);
Assert.True(conformed.ProtectedWitnessesRetained);
Assert.Equal(oracle.FinalSurfaceSignature, conformed.SubmittedSurfaceSignature);
```

- [ ] **Step 2: Run one local native red/green cycle when implementing the fixtures.** Use `build-slot --heavy --label coverage-gpu -- env KE_GPU_TESTS=1 KE_GRAPHICS_BACKEND=metal-native dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~CoverageSurfaceGpuTests|FullyQualifiedName~CoverageShadowGpuTests|FullyQualifiedName~CoverageOutlineGpuTests|FullyQualifiedName~CoverageTemporalGpuTests'`. A missing device is failure, not a pass-by-skip. Use the matching backend token on another native development OS.
- [ ] **Step 3: Complete the proof matrix.** Cover front/rear/both grazing/high/low views, backlighting, contrasting background, UV-free and normal-mapped hosts, cel/PBR materials, two actors in different poses, four cascades, all six point faces, static-light transient and dynamic base rows, camera-culled/shadow-only actors, both outline masks and both global skinning settings. Compare final geometry to the independent oracle and prove uncovered CPU/GPU controls still use their original routes.
- [ ] **Step 4: Prove temporal sequences.** Capture steady/blended motion, equip, unequip, replacement, failed replacement, refusal, reset, independent equipment fade and whole-actor dissolve. Verify no ghost skin, old shadow or stale outline survives. Test every temporal entry/precision path with the new mask input, temporal accumulation on/off and all dissolve variants.
- [ ] **Step 5: Run the three hosted correctness legs on the pushed task commit.** Use `.github/workflows/cross-platform-gpu.yml`, `legs=all`, `renderTestFilter=FullyQualifiedName~Coverage`, `bake=false` initially. Read its current dispatch schema before submitting. Record exact commit, actual `MetalNative`, `Direct3D11Native` and `VulkanNative` adapters, cache state and test counts. WARP/lavapipe may prove correctness, never native performance.
- [ ] **Step 6: Inspect the full-resolution opaque artifacts and create backend-owned goldens only after correctness passes.** Show the synthetic appearance packet in chat. Do not rebake to excuse holes, folds, stale history or excessive collar changes. Commit only reviewed synthetic artifacts and passing evidence. Return to geometry design if the accepted fixture cannot pass without weakening requirements.
- [ ] **Step 7: Commit** with `test(coverage): prove native render and transition parity`.

### Task 9: Bounded allocation and cost evidence

**Files:**
- Create `KhaozEngine.Render.Tests/Render3D/CoverageAllocationTests.cs` and `Gpu/CoverageCostHarness.cs`.
- Create `tools/EquipmentCoverage.Cost.Tests/EquipmentCoverage.Cost.Tests.csproj` and `CoverageCostTests.cs`. Keep this opt-in project outside the default solution. It references the existing Render.Tests project and reuses its fixtures and existing test dependencies.
- Create `KhaozEngine.Render.Tests/AssemblyInfo.CoverageCost.cs` granting internals access only to the cost-test assembly. This is a test-to-test edge, not a production dependency.
- Create `docs/superpowers/plans/proofs/2026-10-16-equipment-coverage-cost.md` when measurements actually exist.
- Use the existing `.github/workflows/stress-test.yml` caller. Do not add a self-hosted runner or a local load script.

**Interfaces:**
- A bounded allocation test performs one warm-up and two distinct accepted poses for two actors, then checks allocated bytes around the second preparation/submission. It does not loop to manufacture load.
- `CoverageCostHarness.Measure(CoverageCostManifest manifest) -> CoverageCostReport` consumes an explicit authorized manifest with actor count, route, pass set, warm-up frames, measured frames and backend. Fixture modes are `Typical` (2,000 source triangles, 200 eligible, level 2, two disjoint commands/materials) and `Limit` (four commands, level 4, below the final caps). Emit actual triangle/vertex counts, skin/preparation/validation CPU time separately, allocated bytes, upload bytes, peak arenas, draws and completed-frame GPU time. The separately selected cost project invokes this method, ordinary solution tests do not.

- [ ] **Step 1: Write `Warm_changed_poses_and_actor_switching_allocate_zero` at the public binding/preparation/submission boundary.** Include changed poses, actor switching and reused temporary inputs. `Assert.Equal(0, warmAllocatedBytes)` must cover the real validation path.
- [ ] **Step 2: Run red/green once.** `build-slot --label coverage-allocations -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~CoverageAllocationTests`. Compile the separate cost project through build-slot without running it. Test manifest validation and report serialization through a fake measurement callback in the ordinary Render.Tests project. No measurement loop runs before permission.
- [ ] **Step 3: Prepare a concrete measurement request.** Specify 1/32/128 actors, off/on, both global skinning settings, colour-only and complete shadow/outline/temporal passes, warm-up/measured frame counts, runner hardware and estimated duration. Request separate owner permission before running any throughput measurement. The numbers and runner in that request become the authorized run manifest.
- [ ] **Step 4: After permission, run the selected CPU workload through the hosted stress workflow with `load=0`.** Select `tools/EquipmentCoverage.Cost.Tests/EquipmentCoverage.Cost.Tests.csproj`, the exact manifest-specific test filter and the iteration count approved in Step 3. For each native GPU budget, first establish a permitted hosted hardware route through that workflow. If it is unavailable, report the gate unproved and stop performance acceptance. Do not substitute virtual adapters or run sustained load locally.
- [ ] **Step 5: Compare the recorded typical workload against 2 ms added CPU and 15 percent completed-frame GPU overhead, with zero warm allocations.** Report limit behavior without applying typical budgets to it. A miss returns to design review. Never delete validation or increase recession to hit a timing gate.
- [ ] **Step 6: Commit the fixture and, separately when available, measured evidence** with `test(coverage): add bounded allocation and cost evidence`. Keep unrun and failed gates visibly open.

### Task 10: Consumer contract, integration and release handoff

**Files:**
- Update `KhaozEngine.Render3D/README.md`, `KhaozEngine.Attachments/README.md`, `KhaozEngine.Attachments.Render3D/README.md`, `docs/USING-KHAOZENGINE.md`, `docs/DEPENDENCY-SEAMS.md`, `docs/INDEX.md`, the approved design status and `CHANGELOG.md`.
- Update version declarations only if the current staged version rules require it, using `Directory.Build.props` and all files identified by `scripts/check-doc-versions.sh`.

**Interfaces:**
- Publish one complete consumer example showing strict sidecar load, exact host upload, cache/binding lifetime, atomic equip, `TryPrepare`, host draw token, prepared equipment draws, covered outline, explicit refusal fallback and disposal. It must use the tested signatures above and explain the CPU route and performance limits.
- The engine output is a verified main commit and separately authorized release handoff. Consumer adoption is tracked by [Grimhollow #547](https://github.com/APKiwiOrg/Grimhollow/issues/547), outside this implementation plan.

- [ ] **Step 1: Perform the full documentation/API sweep.** Search all Markdown for added APIs and changed behavior. Keep the design as rationale and move shipped use into living docs. Document schema authoring, re-export after GLB changes, protected-region semantics, supported transforms, capacity/refusal codes and adapter opt-in compatibility. Compile the consumer example in an existing test, including a legacy custom adapter that has no coverage methods.
- [ ] **Step 2: Reconcile current main in the task branch.** Fetch, compare local main and origin/main, merge the required current base into the worktree and resolve there. Re-read version/tags, ride the staged version if one exists, and never select a future release tag in this plan. Run relevant focused tests after conflicts or newly changed code.
- [ ] **Step 3: Run the full final verification once through the admission wrapper.**

```bash
build-slot --heavy --label coverage-final-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label coverage-final-tests -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

- [ ] **Step 4: Complete one whole-branch independent review.** Verify the integrated commit, synthetic native artifacts and all spec gates, including performance. Any waived requirement needs an explicit owner decision recorded in the design, never a quiet green checkbox. If a gate is unproved, preserve the branch and evidence without claiming the capability is complete.
- [ ] **Step 5: Integrate and push verified work under the current engine repository ritual.** Use a private `KHAOZENGINE_FEED` for any pre-merge pack proof. After origin/main contains the integrated commit, run the required main-checkout pack through `build-slot --heavy`. Follow hook failures. Close engine #1418 only when its accepted capability and required proofs have actually landed.
- [ ] **Step 6: Hand off the exact implemented commit for a separately authorized engine release.** A game-rooted thread uses the release-khaozengine skill and verifies the returned annotated tag, exact-commit CI, publication, packages and tools. No automatic tag or branch package becomes the consumer pin.
- [ ] **Step 7: Resume consumer planning after the verified released package exists.** Reconcile the already reviewed mixed-attachment adoption, author production host maps/companions, prove the full supported motion domain, finish waist/arm/equipment clearance and ordered catalog upgrade, then collect engine-rendered evidence and owner playtest. The approved one-pose Blender probe is not those acceptance proofs.

## Spec coverage and handoff checklist

| Spec requirements | Owning tasks |
| --- | --- |
| 1 to 4, chosen construction and closed/protected surface | 2 to 4, falsification in 8 |
| 5, authoring/provenance and exact/canonical identities | 1, 2, 6, consumer contract in 10 |
| 6, fixed post-skin refinement and normals | 3, native normal proof in 8 |
| 7, offline and every-pose geometry, refusal and limits | 2, 4, 6 |
| 8 and 9, compatible APIs, frames and scale conventions | 1, 2, 4 to 6 |
| 10, all passes, memory, lifecycle, dissolve and temporal | 5 to 8, allocation in 9 |
| 11, cost and capacity | 1 to 4, 9 |
| 12 and 13, independent oracle and native evidence | 3, 4, 8 |
| 14, approval, release and consumer adoption boundary | This review gate, 9 and 10 |

- [x] Owner reviewed this implementation plan and confirmed execution before Task 1.
- [x] Selected method is subagent-driven execution, one implementer and one independent task reviewer at a time, followed by a whole-branch review. The geometry and renderer lifetime boundaries warrant independent checks at each task.
- [ ] Native execution is available if the owner prefers one implementer for all tasks followed by one independent whole-branch review.
- [ ] Separate permission remains required for Task 9 load measurements and Task 10 release work.
