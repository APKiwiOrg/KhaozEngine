# Triangle-backed water optics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` for sequential
> implementation and independent scoped review. Steps use checkbox syntax for tracking.

**Goal:** Pass the unchanged TileWorld saddle gate with optical depth reconstructed from the actual
rendered mesh, including authored cuts and coarse terrain.

**Architecture:** An additive immutable triangle field uses the existing scene handle and R32Float
binding. TileWorld pairs actual mesh output with owned source snapshots, then publishes dependent
geometry and optical resources together. Default bilinear fields and unbound water remain unchanged.

**Tech Stack:** C#, .NET 10, engine GPU abstraction, GLSL, xUnit headless and hosted GPU tests.

**Spec:** [Triangle-backed optical beds](../../design/WATER-OPTICS-TRIANGLE-AMENDMENT-2026-10-24.md).

**Authority:** Controller-selected amendment under the owner's existing instruction to resolve failed
optical gates and continue subagent execution. This is a subplan of Task 6 in
[per-body water optics](2026-10-24-per-body-water-optics.md), not a new release or consumer task.

## Global constraints

- Use the existing `feature/water-object-optics` worktree. Preserve unrelated work and commit explicit paths.
- The amendment is authoritative for triangle fields. Preserve original bilinear and unbound contracts.
- R32Float atlas axes are 2 through 1024. Bins have at most 32 candidates. No guessed caps or hidden fallback.
- All source positions come from the mesh actually uploaded, using `Indices32`. No second triangulator.
- Use complete certificate coverage and deterministic shared-edge sampling, with explicit numerical refusal.
- No terrain changes, forced Full LOD, geometry clipping, narrower feather or threshold relaxation to pass.
- Match immutable accepted/candidate source generations, including neighbors and actual LOD.
- Pre-record preparation, rollback, generation validation and deferred retirement apply to both resource kinds.
- Metadata ownership includes active, pending and refused dependencies and must be released without stale ABA acceptance.
- One implementer at a time, no worker helpers or reviewers. Fresh scoped review after each task.
- Use build-slot for every build, test, render or pack. GPU/render runs use the heavy lane. No stress or loops.
- No full solution suite in these subtasks. Parent Task 7 owns final suite, backend matrix and integration.
- No private assets, unpublished R8a dependencies, version changes or release tags in this subplan.

## Review focus

1. A certificate can hide a narrow hole even if its corners and center hit. Task 1 proves union coverage.
2. A long edge can meet several short edges with different floating arithmetic. Tasks 1 and 2 prove shared-edge hits.
3. Identical corner heights can emit different authored or LOD surfaces. Tasks 3 and 5 compare actual mesh intersections.
4. A neighbor can change LOD without a height edit. Task 4 proves atomic dependent-field replacement.
5. Pruning source metadata can make a stale pending witness appear current. Task 4 pins cleanup and ABA rejection.

## Task order

| Subtask | Parent name | Deliverable |
| --- | --- | --- |
| 1 | 6A | Immutable triangle atlas and CPU coverage/sampling |
| 2 | 6B | Scene resource and shader support |
| 3 | 6C | Actual mesh source snapshots and provenance |
| 4 | 6D | Transactional TileWorld triangle publication and bounded metadata |
| 5 | 6E | Unchanged saddle and general topology acceptance |

The parent Task 6 remains incomplete until all five scoped gates and the visual gate pass.

### Task 1: Immutable triangle field and certificate validation

**Files:** Create `KhaozEngine.Render3D/WaterOpticsTriangleField.cs`,
`WaterOpticsSurfaceException.cs`, `Internal/WaterOpticsTriangleIndex.cs`,
`Internal/WaterOpticsTriangleCoverage.cs`, `Internal/WaterOpticsTriangleMath.cs`,
`KhaozEngine.Render.Tests/Render3D/WaterOpticsTriangleFieldTests.cs` and focused test helpers when needed.

**Interfaces:** Implement the public triangle, rectangle and field signatures in the amendment.
Expose immutable internal atlas floats for Task 2 without returning a mutable array.
Invalid ordinary arguments throw `ArgumentException`. Unsupported surface construction throws
`WaterOpticsSurfaceException : ArgumentException`, with a readonly `Reason` property of enum
`WaterOpticsSurfaceFailure { IncompleteCoverage, AmbiguousSurface, CapacityExceeded, UnrepresentableSurface }`.
This typed refusal is consumed by the TileWorld adapter, never inferred from exception text.

- [ ] Write tests first for defensive ownership, signed affine sampling, representative depths, final bins
  and atlas boundaries, invalid values, checked capacity and exact byte accounting.
- [ ] Add `NarrowHoleInsideCertificateIsRefused`, `OverlapCannotCancelAHole`,
  `DistinctOverlappingBedsAreRefused`, `OutsideCertificateReportsUnavailable`, and
  `ThirtyThreeCandidatesRefuseWithoutDroppingTriangles`.
- [ ] Add shared-edge, vertex, three-way and long/short T-junction cases in reversed source order.
  Probe exact edges and adjacent representable floats. Use independent literal geometry or ray intersections.
- [ ] Run RED through `build-slot --label water-triangle-cpu-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~WaterOpticsTriangleFieldTests`.
- [ ] Implement immutable packing, exact integer addresses, canonical lines, coverage certification and
  bounded CPU sampling from packed bytes. Follow the amendment layout exactly. No draw-time validation work.
- [ ] Run the filter GREEN and existing `WaterOpticsFieldTests`. Require zero warnings and skips.
  Record actual atlas dimensions, maximum occupancy and retained bytes for representative inputs.
- [ ] Commit explicit paths as `feat(water): define certified triangle optical fields`.

### Task 2: Resource ownership and GPU triangle sampling

**Files:** Create `KhaozEngine.Render3D/Rendering/WaterOpticsPayload.cs`,
`Internal/ShaderSources.WaterOpticsTriangles.cs`,
`KhaozEngine.Render.Tests/Render3D/WaterOpticsTriangleResourceTests.cs` and
`KhaozEngine.Render.Tests/Gpu/WaterOpticsTriangleGpuTests.cs`.
Modify `Rendering/WaterOpticsResources.cs`, `Rendering/WaterRenderer.Optics.cs`,
`Internal/ShaderSources.WaterOptics.cs`, `Scene3D.WaterOptics.cs`, `WaterOpticsBinding.cs`,
`Internal/WaterOpticsCoverage.cs` and focused UBO/binding tests.

**Interfaces:** Consume Task 1 field and immutable atlas. Produce
`Scene3D.LoadWaterOpticsTriangles(WaterOpticsTriangleField field) -> WaterOpticsHandle`.
Entry retains one immutable payload kind. Stats add non-positional live/retired retained CPU-byte values.
Mode 2 uses existing texture/sampler bindings 10/11 and unchanged 1088-byte UBO and 1280-byte slot.

- [ ] Write RED tests for mixed kinds, owner/generation rejection, failed upload rollback, padded texture
  and CPU-byte retirement, exact binding-size requirement and certificate containment.
- [ ] Add shader readback cases for signed triangle depth, both diagonals, arbitrary interior vertices,
  shared edges/T junctions and outer atlas/bin boundaries. Encode hit status independently of depth.
- [ ] Run focused RED through build-slot, GPU with `--heavy` and `KE_GPU_TESTS=1`.
- [ ] Implement typed resource forwarding, mode packing, certificate revalidation and bounded shader lookup.
  Keep default/bilinear shader arithmetic untouched. No missed hit becomes a fallback depth.
- [ ] Verify failed/changed preparation, target/UBO replacement, released handles, order reversal and
  warmed zero managed allocation/upload/resource-set creation using the repository harness.
- [ ] Run `WaterOpticsTriangle` plus existing `WaterOptics`, `UboLayoutTests` and
  `WaterBathymetryUploadGateTests` with GPU enabled. Require zero selected skips. Inspect readback evidence.
- [ ] Commit explicit paths as `feat(water): render certified triangle optical beds`.

### Task 3: Mesh output snapshots and strict source audit

**Files:** Create `KhaozEngine.TileWorld.Render3D/TileGroundSurfaceSnapshot.cs`,
`TileGroundSurfaceAudit.cs`, `TileGroundCpuBuild.cs` and
`KhaozEngine.Render.Tests/TileWorld/TileGroundOpticsSurfaceParityTests.cs`.
Modify `TileGroundCornerCache.cs`, mesher context/emission bookkeeping, `TileWorldBuildSnapshot.cs`,
`TileWorldView.ProfileBuilds.cs`, `TileWorldView.GroundResidency.cs` and the synchronous CPU build seam.

**Interfaces:** Produce immutable `TileGroundSurfaceId(RegionCoord Region, int Plane, long Generation)`,
`TileGroundPositionPayload`, `TileGroundSurfaceSnapshot` and `TileGroundCpuBuild` as described in the
amendment. Snapshot supplies retained bytes, source witness, LOD, world transform and indexed positions.
Pair mesh and snapshot in one build result. Do not enable triangle adapter publication before Task 4.

- [ ] Write RED parity tests comparing copied positions/indices to the exact mesh output. Include indices
  above 65535 and mutation of original mesh arrays after capture.
- [ ] Cover both height-selected diagonals and ties, no-overlay authored rotation, all corner-cut rotations,
  undefined nonzero overlay material, feather children, coarse interiors and transition fans.
- [ ] Cover strict source provenance on a detached worker snapshot, including known-unloaded earlier owner,
  derived/saturated heights and an unrelated missing corner outside the water domain.
- [ ] Run the focused parity filter RED, then implement source audit at the existing shared height read and
  associate it with emitted tile/block index ranges. Preserve mesh output exactly.
- [ ] Carry paired results through synchronous, worker, prime and LOD paths. Extract from actual `Indices32`,
  without reproducing mesher topology or accepting lifted-zero source as authored bed.
- [ ] Run the new filter and existing `TileGroundTrianglesParityTests`, mesher and profile build tests GREEN.
- [ ] Commit explicit paths as `feat(tile-world): retain audited optical surface snapshots`.

### Task 4: Transactional triangle publication and metadata ownership

**Files:** Create `KhaozEngine.TileWorld.Render3D/ITileWorldWaterTriangleOpticsScene.cs`,
`TileWaterTrianglePreparation.cs`, `TileWaterPublication.cs`, `TileWaterDependencyRegistry.cs` and
`KhaozEngine.Render.Tests/TileWorld/TileWorldViewWaterOpticsTriangleTests.cs`.
Modify `ITileWorldScene.cs`, `TileWaterGeneration.cs`, `TileWaterOpticsFailure.cs`,
`TileWorldView.WaterOptics.cs`, `.Water.cs`, `.Flush.cs`, `.GroundResidency.cs`, `.ProfileBuilds.cs`,
`.Foliage.cs`, `.cs` and recording adapters as needed for one coherent publication lifecycle.

**Interfaces:** Add optional `ITileWorldWaterTriangleOpticsScene : ITileWorldWaterOpticsScene` with
`LoadWaterOpticsTriangles`. AuthoredBed requires it before any upload. `Scene3DTileWorldScene` implements
it. Produce source-generation publication groups and bounded dependency ownership. Append diagnostic
kinds from the amendment without renumbering old ones. Expose retained resident/pending source bytes.

- [ ] Write RED tests for first preparation, default compatibility, shared row runs, different levels,
  color-only reuse and actual triangle source sampling.
- [ ] Add `NeighborLodAndDependentFieldsPublishTogether`, `TopologyOnlyEditInvalidatesOptics`,
  `StaleSurfaceNeverBecomesHaloSource`, `UploadFailureRollsBackWholePublicationGroup`,
  `MissingReachableSurfaceRefusesWithoutGuessing`, `UnrelatedEmptyHaloDoesNotRefuseInteriorWater`,
  `AdjacentFieldsUseIdenticalAcceptedSources`, `PendingWitnessSurvivesMetadataPruning` and
  `LastDependentReleasePrunesEpochsAndRefusals`.
- [ ] Run the new filter RED. Implement fields from actual candidate/accepted snapshots after CPU mesh
  completion, with complete source/coverage validation and deterministic bounded packing attempts.
- [ ] Rebuild affected resident fields against one candidate source table, upload all replacements,
  validate again and swap atomically before draws. Reject late stale results. Retain valid coarse fallback
  only with its matching optical field. Missing bed withdraws invalid water while keeping unrelated terrain.
- [ ] Bound epoch/refusal/reverse-dependency metadata by active, pending and refused ownership. Release
  unused source snapshots and metadata without accepting stale ABA witnesses. Use small deterministic
  lifecycle tests, never long traversal/load tests.
- [ ] Run new tests and existing Task 6 view/water/shared-resource/picking/profile regressions GREEN.
  Require zero skips/warnings. Verify zero unchanged-draw allocations/uploads and no source I/O in submission.
- [ ] Commit explicit paths as `fix(tile-world): publish exact optical surface generations`.

### Task 5: Saddle and general topology acceptance

**Files:** Extend `KhaozEngine.Render.Tests/Gpu/TileWorldWaterOpticsSaddleGpuTests.cs` with additional
cases while preserving its original fact and threshold. Add
`KhaozEngine.Render.Tests/Gpu/TileWorldWaterOpticsTopologyGpuTests.cs` and cohesive public fixtures.
Update amendment status with the resulting evidence and parent plan gate state.

**Interfaces:** Consume the completed actual-mesh triangle adapter. Produce saved public images,
independent depth/hit metrics and a justified synthetic acceptance verdict for parent Task 6.

- [ ] Run the original saddle fact unchanged through `build-slot --heavy`, with `KE_GPU_TESTS=1`.
  Require one live field/upload and `authoredNear <= sceneNear + 0.04f`. Preserve the failed baseline.
- [ ] Add opposite-diagonal, all corner-cut rotations, interior coarse and full/coarse transition captures
  through the real adapter, with opaque controls and independent mesh-ray depth expectations.
- [ ] Verify edges, source order, neighboring fields, origin reduction and representative depths. Keep
  the 0.20 m feather and fixed object/terrain geometry. Record atlas occupancy, bytes and run duration.
- [ ] Run focused GPU and all amendment headless filters GREEN, zero skipped selected GPU cases.
  Inspect every new capture and publish owner evidence through html_preview then html_render.
- [ ] Obtain scoped review. Parent Task 6 acceptance requires this look gate, all prior subtask reviews,
  bounded metadata and matching geometry/field generations. Failure triggers investigation, not rebaking.
- [ ] Commit explicit paths as `test(water): verify exact terrain optical surfaces`.

## Parent handoff

Return to parent Task 7 for the complete hosted Metal/D3D11/Vulkan gate, remaining general optical
fixtures and the deferred varying-bank rebase assertion. Documentation must distinguish bilinear direct
fields from triangle-backed TileWorld, include real refusal limits and the viewport-aware preparation
API, and document retained source bytes. The R8b handoff names the neutral triangle API and source digest.
Final reconciliation, version ritual, full Release checks and whole-branch review remain mandatory.
