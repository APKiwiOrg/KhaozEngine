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
- [ ] Cache six exact products in one retained preprocessing loop with one unchanged product body,
  preserving the original signed term order. Store three shifted fragments and start-word/sign in one
  six-record local table, written once then read-only. Retain one 18-word low-to-high scan with one
  nested six-contribution loop, literal record components and scalar positive/negative carry counters
  bounded to 0 through 5. Normalize each word before comparison and let the highest differing word
  override. Process word 17 and prove zero final carries from the below-2^557 total bound. Remove all
  18-word banks and copy chains. Preserve the complete narrow classifier/span-77 arithmetic and binary32
  helpers. Require actual pinned SPIR-V/HLSL dataflow and retained bounds
  `2,3,4,6,6,6,18,23,27,32`, not source-only assertions. Stop at an emitted cache copy chain before hosted
  expense. No sixfold unrolling, per-word product recomputation or precision/domain change is allowed.
- [ ] Retain one fixed 23-step interpolation loop with one emitted software add, multiply and divide
  body, including one nested 27-step division loop. Select the original ordered scalar operand bits
  and store each rounded result in a local 16-word register bank. Preserve sign-bit XOR subtraction,
  signed zero, the constant/equal-zero early return and unchanged arithmetic helpers and CPU reference.
  Verify actual production interpolation bitwise against that CPU reference on nonconstant depths,
  cancellation and admitted subnormal intermediates, plus unchanged query, image and fallback proofs.
- [ ] Share the six original narrow predicate products in one retained six-term loop without changing
  the span-77 classifier, product/add helpers, four-word accumulators or descending comparison. Share
  one software multiply body for the four ordered bin-mapping steps, with a local two-word u/v bank,
  the original unit clamps, truncation and last-bin minima. Share one software add body for ordered
  X/Z triangle-origin subtraction, retaining sign-bit XOR and signed zeros. Require actual retained
  SPIR-V and HLSL bodies alongside the streamed-wide and interpolation structures before hosted
  proof. Preserve all 87 predicate probes, 235 original queries and 60 interpolation queries. Add
  independent bitwise rectangular-bin/origin coverage and retain six pixel-identical render controls.
- [ ] Pass the pending candidate-only cold Windows compile proof at dispatch `github.sha`, with caches
  off and optimized production flags. Require pixel FXC <= 5000 ms and the measured six-phase total
  <= 10000 ms. Reuse verified control run 40087661847 without recompiling its frozen inputs. These
  fixed-proof engineering limits do not assert universal hardware performance. Rolled-wide run
  40091542332 and interpolation run 40094097628 missed both limits. The shared predicate and mapping
  candidate remains pending this proof and fresh all-three-backend correctness.
- [ ] Implement complementary separate lazy triangle pipelines in a subsequent scoped task so default
  legacy/bilinear construction cannot compile triangle arithmetic. Compile improvement alone does not
  complete Task 2. Retain the original numerical, resource, image and all-three-backend gates.
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
