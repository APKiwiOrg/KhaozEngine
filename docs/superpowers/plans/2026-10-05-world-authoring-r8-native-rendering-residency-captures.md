# World authoring R8: Native rendering, residency and captures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Render and capture the complete native resolved world through one adapter while preserving authored geometry and duplicate-free streaming.

**Architecture:** Add an opt-in MapDoc.Render3D adapter over the immutable headless build and verified render asset closure. Native view, MapEditor viewport and ke-mapedit snapshots share terrain, placement, local-floor, light, water, roof and foliage composition. Residency uses effective bounds with one owner and all intersected storage memberships, independently of the server's required simulation-domain statics.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 and T1 to T9 with revised T4 are approved. This round plan remains pending owner review.

Task-level plan. Refine to full step level against the landed R1 to R7 APIs before executing, and record the refinement in Outcome.

## Approval stage, reconciled 2026-10-05

The controller supplied the exact owner answer "Approve" for both specs, T1 to T9 with revised T4, rigid prefab v1 and the pre-OA9 12 to 18 elapsed-week estimate, and C4 boundary policy. This R8 document remains a draft for refinement at its round. It requires a full plan against owner-released dependencies and then owner plan approval before execution. Spec approval is not acceptance of actual changed targets, distances, occlusion, stances or exact water-boundary samples. R11 records named import acceptance.

The 1 m allowance is a minimum vertical target reach-envelope height, preserving `MinimumObjectReachHeight`. It is not a 1 m action distance. Action range remains existing game policy and physical colliders never expand. Approval IDs are OA4 specs, OA5 revised T4, OA6 prefab/estimate and OA7 water boundary in the game DECISIONS record.

## OA9 checkpoint, before R8 approval

[DG9.1 to DG9.5](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#dg95-water-dependencies-and-captures) extend this draft. R8 remains unapproved. Consume the cave model from [R2](2026-10-05-world-authoring-r2-authored-terrain-paint.md)/[R5](2026-10-05-world-authoring-r5-free-buildings-prefabs-interiors.md), vertical water identity from [R4](2026-10-05-world-authoring-r4-bounded-water-medium.md) and tiled support/nav/cell contracts from [R3](2026-10-05-world-authoring-r3-shared-shapes-headless-builders.md).

- **Tasks 2/4:** Refine deep multi-level cave lighting, ceiling/camera occlusion, surface-versus-underground visibility and dry/flooded domains. Roof view rules must not silently remove cave physical ceilings or leak surface lighting. `CaveLightingAndCameraOcclusion_RespectSelectedDomain` and `FloodedCaveView_UsesContainedWater` are named future backend proofs.
- **Tasks 2/6, water dependencies:** Record [#1300](https://github.com/APKiwiOrg/KhaozEngine/issues/1300) optical/data algorithm ownership and [#1297](https://github.com/APKiwiOrg/KhaozEngine/issues/1297) bounded geometry cost before approval. Bed-based depth is unchosen. `WaterDepthAcceptance_NoPaleTerraceBandOrSeamStep` uses fixed-camera Native TAA/backend captures for a ledge 0.3 m under water, 2 m river, 30 m shelf to 100 m ocean, enclosed cave lake and region-crossing body. `BentWaterBody_AreaScaledWorkAndAuthoredShapeSurvive` pins bounded work counters and bends, no huge local benchmark.
- **Task 3, scale:** [#1301](https://github.com/APKiwiOrg/KhaozEngine/issues/1301) requires explicit tiled/on-demand integration and bounded residency/planning budgets. Resolve ownership/splitting before approval. HLOD and far-origin precision must hold across many vertical layers and server cells. Pin supported extents/depth/minimum precision under DG9.4. No unlimited-precision claim follows from C1 floats.
- **Named future scale proofs:** `DeepLayerResidency_LoadsOnlyApprovedWorkingSet`, `FarOriginHlodRenderAndPick_MeetPrecisionContract` and `MultiCellGhostHandoff_RenderAndNavStayContinuous`, with R3/downstream game G3. Complete coverage is a checked union of domains/tiles, not a requirement to keep the whole world loaded. Visual windows never own server authority.
- **Tasks 5/6:** Expose scoped/tiled capture completeness and missing-closure findings to [R9](2026-10-05-world-authoring-r9-unified-mapeditor-workflow.md)/[R10](2026-10-05-world-authoring-r10-complete-ke-mapedit-parity.md)/[R11](2026-10-05-world-authoring-r11-offline-importer-no-loss-ledger.md). Existing complete-load fixture tests stay regression proofs, not a scalable-world certification. Backend tests and owner look review remain future execution gates.

## Global Constraints

- "One tool, MapEditor GUI plus ke-mapedit MCP, authors terrain, props and buildings. No two-format hybrid"
- "Props and buildings accept free position, yaw and positive uniform scale as an engine-wide capability"
- "Grimhollow leaves TileWorld completely through the full native MapDoc swap, option A"
- "Core MapDoc and its runtime packages acquire no TileWorld or Grimhollow dependency." The optional offline importer is the only new package allowed to read TileWorld. Existing analytic MapDoc and TileWorld consumers retain current behavior.
- "The ordinal list position is never identity." Numeric IDs are positive int64, encoded as decimal strings in JSON/MCP. Imported leaf IDs survive. Persist allocation high-water marks, tombstones and prefab child bindings. Redo restores allocation rather than allocating again.
- "All newly structured payloads use closed schemas and explicit payload versions." Schema transitions are baseline-relative, not reserved numbers. Rebase them onto the next free format if concurrent work takes one. Pure sequential migrations preserve old analytic execution. Refuse future versions.
- Authored-world identity includes normalized MapDoc, complete prefab/material/asset/collider/light/LOD closure and builder versions/options. Validate missing, duplicate, cyclic, stale or unsupported references before building. Equal TileSize monolithic/tiled forms resolve equally. Keep playable bounds separate from storage bounds.
- "One GPU-free triangle compiler yields vertices, topology, surface IDs and geometric normals." Rendering, floor sampling, physics and capture consume the same descriptors. No bilinear movement fallback or coarser height-field replacement for authored geometry.
- C2 surfaces distinguish canonical support geometry from non-colliding material-override paint. One owner per floor. C3 applies position/yaw/positive uniform scale exactly once. Use the revised T4 shared interaction envelope derived from canonical asset geometry for pick/reach/stance, with minimum 1 m vertical target reach-envelope height (`MinimumObjectReachHeight`) for low objects and explicit selectable bounds. Physical collision and occlusion use unchanged physical shapes. Preserve apertures and lower-2-m tree eligibility. Policy version, geometry closure and transform identity are shared by both heads. R3 refinement pins exact geometry/query rules before execution, with no hidden mesh-AABB versus footprint choice.
- C4 bounded water retains its explicit level, medium and domain. No rim or camera recomputation, double global-water rendering or implicit terrain-driven water-level edits.
- "GUI and MCP invoke one undoable native command layer." Mutations report affected IDs, dirty bounds, identity and undo/redo labels. Validation failure writes nothing. Save validates the closure and writes atomically, retaining unindexed-overwrite protection.
- "Each release takes the next available engine minor after the concurrent pivot program's releases. Only the owner tags." Reserve no engine numbers. Re-read main/version/tags at execution, ride the applicable unreleased round version and reconcile through the owning orchestrator. No pivot repin. Grimhollow adoption waits for both 0.11.0 and accepted grand-world on main.
- This documents-only reconciliation changes engine planning documents only, with no code, tests, builds, version or release history changes. The worker commits explicit paths and stops. The controller reviews and pushes, with no worker merge, tag or pack. Future package-bearing rounds update version, CHANGELOG and guarded doc declarations together under repository release policy.
- Work in an isolated task worktree, preserve unrelated changes and stage explicit paths. Commit subjects use `area(scope): summary`. A delegated implementer stops at the verified commit when integration belongs to its orchestrator.
- "`AppWindow` is the only class that touches raw Silk.NET or GLFW input." Other code uses InputState through InputManager/Pointer and shared bounds helpers.
- "Player-facing text resolves through the localization catalog with `StringId`." Prefer LocalizedText at GUI sinks. Developer output and tokens use the explicit raw path.
- "New behavior gets a headless test in the matching area test project." Keep namespaces under KhaozEngine.Tests.*, reference only used projects and disable parallelization for process-global mutation collections.
- Third-party libraries stay behind dependency-free seams with opt-in backends. KESIZE growth/exemptions require owner approval. New responsibilities get focused types. Warnings are errors, with no blanket suppressions.
- "No em-dash or en-dash glyphs in shipped prose or comments. No prose semicolons in Markdown." Perform the full Markdown/API sweep and repository whole-tree guards.
- Execution verification is synchronous and serialized through the shared build slot. No stress loops or parallel local builds. Build Release once and run the full non-LiveSocket suite once at round finish. GPU proofs use the backend CI/golden route in CROSS-PLATFORM.md, with actual scene/capture tests, not mesher-only evidence. Grimhollow captures use TAA Native.

## Review Focus

1. A building/tree whose anchor is outside the loaded window must remain present while its bounds intersect it. Task 3 tests overlapping memberships.
2. An evicted chunk or delayed build must not remove an object still owned by another loaded membership, or resurrect stale geometry. Task 3 tests generation fences.
3. A missing material/LOD/collider/light reference must fail capture before producing a misleading terrain-only image. Tasks 1 and 5 test closure failures.
4. Hidden roofs must keep shadow pixels while camera blockers and picking follow current visibility and physics stays complete. Tasks 4 and 6 test each channel.
5. Reused snapshot scenes, temporal state and low-resolution goldens can hide missing thin paint/walls/foliage. Tasks 5 and 6 test fresh-versus-aged capture and targeted evidence.

## Dependencies and delivered contracts

| Earlier round | Consumed contract |
| --- | --- |
| R1 / C1, transitively | Immutable resolved identity/tags, render-free asset manifest, closure hashes, playable/storage bounds and tiled validation scope |
| R2 / C2 | Canonical triangle/material/cut/feather descriptors, explicit local support versus paint roles |
| R3 / C3 | Asset shapes, mesh/LOD/light extents, support descriptors, headless bounds and residency ownership |
| R4 / C4 | Bounded clipped draw polygons and medium/domain identity |
| R5 / C5 | Effective prefab children/lights/floors/water, volume membership and roof/shadow/camera intent |
| R6 / C6 | Point/shape markers and ownership in streamed windows |
| R7 / C7 | Immutable sample sets, sample identity and complete distribution cache keys |

Produces C8 native render adapter/view/snapshot, effective residency integration and complete captures with no schema revision. R9 uses the viewport. R10 uses render/query/streaming tool adapters. R11 uses fixed-camera differential evidence and complete-load versus windowed proof. Grimhollow adoption receives the same client/SnapshotTool view while the GPU-free server keeps independent required-domain coverage under DG9.3.

**Name discipline:** `NativeAssetDescriptor`, `ResolvedMapWorld`, `MapWorldBuild`, canonical geometry and residency-owner descriptor names are provisional R1 to R4 contracts. R5 to R7 refinement confirms roof/membership/sample APIs. Baseline MapTileResidency/MapResidencyGate, ViewportWorld, RenderService and Render3DSnapshot exist. MapDoc.Render3D and its types are proposed, not existing packages.

## File Structure

| Action and engine path | Responsibility |
| --- | --- |
| Create `KhaozEngine.MapDoc.Render3D/KhaozEngine.MapDoc.Render3D.csproj`, `README.md`, `MapRenderAssets.cs`, `NativeMapView.cs` | Opt-in rendering adapter over headless descriptors and verified render assets |
| Create `KhaozEngine.MapDoc.Render3D/NativeMapTerrain.cs`, `NativeMapPlacements.cs`, `NativeMapWater.cs`, `NativeMapFoliage.cs`, `NativeMapRoofs.cs`, `NativeMapSnapshot.cs` | Focused composition, visibility and snapshot responsibilities |
| Create `KhaozEngine.MapDoc/MapResolvedResidency.cs`, modify `MapTileResidency.cs`, `MapResidencyGate.cs` and landed R3 ownership files | Composite bounds, canonical owner/membership and stale-build fences |
| Modify `KhaozEngine.MapEditor/ViewportWorld.cs`, `ViewportWorld.InPlaceRefresh.cs`, `KhaozEngine.MapEdit.Tool/RenderService.cs`, `RenderStreamPlan.cs` | Native branch using the shared adapter, analytic branch retained |
| Modify `KhaozEngine.Terrain.Render3D` integration files only where shared draw seams require it | Preserve authored triangle geometry across distance/grouping changes |
| Create `KhaozEngine.Render.Tests/MapDoc/MapRenderAssetTests.cs`, `NativeMapCompositionTests.cs`, `NativeMapRoofRenderTests.cs`, `NativeMapGoldenGpuTests.cs` | Asset/render/headless recording/backend proofs |
| Create `KhaozEngine.MapEditor.Tests/MapDoc/MapResolvedResidencyTests.cs`, `MapEditTool/NativeMapCaptureTests.cs`, `MapEditor/NativeViewportAdapterTests.cs` | Streaming and real frontend composition plumbing |
| Modify area csproj references, `KhaozEngine.slnx`, architecture expectations, README/package/API/dependency docs and release declarations | Optional package registration and published native view |

## Judgement and refinement record

- J8.1. Use a focused optional MapDoc.Render3D package rather than placing GPU dependencies in MapDoc or MapDoc.Physics. Its only headless inputs are resolved descriptors/closure. No umbrella addition is assumed, decide membership against the package catalog at refinement.
- J8.2. Each resolved resource has a deterministic owner key and references in every intersected storage chunk. Any loaded membership retains it once. Unload releases it only when no membership remains. Authoritative simulation-domain statics do not follow camera residency. DG9.3 permits approved on-demand loading, independent of view windows.
- J8.3. A windowed document exposes its validation scope and missing closure explicitly. Validation/capture reports its explicit domain and requires that complete closure. Whole-world coverage may use a checked union of scoped/tiled captures, never a partial result labelled complete or a forced simultaneous whole-world load.
- Golden evidence includes sparse-feature and shadow/visibility checks in addition to coarse grids. Use Native TAA for Grimhollow evidence, and do not move cameras to hide differences.

---

### Task 1: Verified asset adapter and package seam

**Files:** Create MapDoc.Render3D csproj/README/MapRenderAssets.cs, MapRenderAssetTests.cs and update package/project reference registration.

**Interfaces:**
- Consumes C1 render-free asset DTO and C3 explicit preserve-source-scale, local bounds, collider/LOD/light/material descriptors.
- Produces `MapRenderAssets` adapting verified assets to render meshes/materials while retaining leaf ID, numeric ID, Kind, variant and ordered tags.
- Required asset closure is validated before scene construction, with a typed missing/stale asset failure and no greybox or terrain-only success fallback.

- [ ] Deliver native render asset loading without reversing the headless dependency seam.
- **Tests:** `RenderAdapter_PreservesMeshOriginUnitsPartsLodAndLights` checks imported scale. `MissingRequiredAsset_FailsBeforeSceneBuild` covers mesh/material/LOD/light/collider digest cases. `RenderAdapter_RetainsResolvedIdentityAndTags` prevents Kind-as-ID regression. `NativeHeadlessPackages_HaveNoGpuRenderOrTileWorldReference` audits direct/transitive edges.
- **Exit proof:** Release filter `FullyQualifiedName~MapRenderAssetTests` and architecture seam checks pass synchronously. Documentation/project graph includes the optional package only where needed.

### Task 2: Complete native scene composition

**Files:** Create NativeMapView/Terrain/Placements/Water/Foliage.cs, NativeMapCompositionTests.cs. Modify applicable Terrain.Render3D integration seams.

**Interfaces:**
- Consumes MapWorldBuild geometry/shapes, C2 surfaces/paint, C3 render assets/LOD/lights, C4 polygons, C5 children and C7 sample sets.
- Produces one native scene composition whose authored terrain vertices/topology/normals remain canonical at every distance. LOD may change grouping/material work, not the surface.
- Placement, floor, water, light and foliage transforms use effective resolved records once, without separate renderer anchoring.

- [ ] Deliver complete draw population over a recording scene, with immutable dependencies.
- **Tests:** `NativeScene_ComposesEveryResourceFromOneBuild` checks counts and keyed records. `AuthoredTerrain_DistanceChangesNeverReplaceTriangles` compares descriptors at near/far settings. `LocalPaint_DecoratesCanonicalFloorWithoutSecondSupport` checks draw/support roles. `BoundedWater_NoGlobalOceanOrCameraDrivenLevel` includes bridge and independent levels. `FoliageUpload_UsesExactCpuSamplesAndNoDuplicateStatics` checks sample identities.
- **Exit proof:** Release filter `FullyQualifiedName~NativeMapCompositionTests` passes against a recording/fake render seam, proving actual view wiring without a graphics device.

### Task 3: Composite residency and clean lifetime

**Files:** Create MapResolvedResidency.cs/MapResolvedResidencyTests.cs. Modify MapTileResidency/MapResidencyGate, R3 ownership and NativeMapView lifecycle.

**Interfaces:**
- Consumes effective mesh/shape/surface/LOD/light bounds, C5 resource owners and C6/C7 marker/sample bounds.
- Produces owner plus all intersected chunk memberships, loaded reference tracking and generation-safe publication/unload.
- Partial load reports incomplete validation scope. Required-domain server coverage remains independent from visual window changes, with scalable/on-demand integration refined under DG9.3.

- [ ] Deliver cross-chunk load equivalence, single publication and stale-build rejection.
- **Tests:** `AnchorOutsideWindow_SpanningBuildingAndTreeRemainResident` covers negative seams and elevated floors/lights. `TwoLoadedMemberships_DrawAndStaticsExistOnce` checks owner reference counts. `UnloadOneMembership_RetainsResourceUntilLastRelease` proves cleanup. `LateChunkBuild_CannotResurrectOldClosure` checks generation fence. `CompleteLoadEqualsChunkedResolvedInventory` includes markers/foliage/water and storage-independent coordinates. `PartialLoad_CannotClaimWholeWorldValidation` is fail-closed. `VisualWindowChanges_DoNotChangeCompleteServerStatics` proves server independence.
- **Exit proof:** Release filter `FullyQualifiedName~MapResolvedResidencyTests` passes. Repeatedly calling lifecycle operations within one deterministic test is a bounded semantic sequence, not a stress loop.

### Task 4: Roof visibility, shadows, picking and camera channels

**Files:** Create NativeMapRoofs.cs/NativeMapRoofRenderTests.cs. Modify NativeMapView and viewport camera/pick adapter hooks.

**Interfaces:**
- Consumes R5 membership and Auto/AlwaysVisible/AlwaysHidden roof policy.
- Produces shadow-only roof submission, visible selection/camera blockers and shared indoor-lighting input.
- Physical world statics and complete capture descriptors remain intact across view-mode changes. Solid and non-solid selection use the revised T4 C3 selectable interaction envelope. LOS/camera physical blockers use unchanged physical geometry, subject only to the recorded roof-view filter. Shared envelope/policy/closure identity is preserved across rendering and headless queries.

- [ ] Deliver independent roof draw, shadow, selection, camera and physics behavior.
- **Tests:** `HiddenRoof_UsesShadowOnlyPass` checks recorded submissions. `ObserverStorey_HidesOnlyLinkedRoofsAboveVolume` covers adjacent buildings/open shelter. `RoofMode_ChangesCameraPickButNotPhysicalStatics` checks each channel. `IndoorLighting_UsesSameMembershipAsServer` verifies shared results. `FreeRoofTransform_PreservesHeightAndShadowBounds` checks higher roofs.
- **Exit proof:** Release filter `FullyQualifiedName~NativeMapRoofRenderTests` passes. Task 6 adds actual pixel proof of shadow-only behavior, no recording test is presented as pixel evidence.

### Task 5: Shared snapshots and frontend adapters

**Files:** Create NativeMapSnapshot.cs, NativeMapCaptureTests.cs and NativeViewportAdapterTests.cs. Modify ViewportWorld, RenderService and RenderStreamPlan.

**Interfaces:**
- Consumes native view/assets/resolver/options plus observer volume and explicit framing.
- Produces snapshots used by MapEditor, ke-mapedit RenderService and consumer clients with identical composition options.
- Capture returns exact framing/text plus inline PNG, optional additive save path and explicit incomplete/missing-device failures. Top-down overlays use canonical native geometry/bounds/identities.

- [ ] Deliver native scene/capture plumbing while preserving existing analytic frontend routes.
- **Tests:** `ViewportAndSnapshot_UseSameResolvedComposition` compares all resource keys. `CaptureMissingManifest_RefusesTerrainOnlyResult` checks no image is returned as success. `TopdownAndPerspective_UseObserverVolumeAndNativeBounds` checks framing, fixed overlay order and paired observer inputs. `FreshAndAgedSnapshotScenes_Agree` resets temporal/camera/streaming state and compares equivalent scenes. `CaptureNoGpuDevice_FailsPrecisely` checks bounded failure.
- **Exit proof:** Synchronous Release filters `FullyQualifiedName~NativeViewportAdapterTests|FullyQualifiedName~NativeMapCaptureTests` pass for CPU plumbing. GPU capture cases run through the backend workflow and report zero skipped proofs. No consumer client boot is needed.

### Task 6: Backend evidence and native view release contract

**Files:** Create NativeMapGoldenGpuTests.cs and backend family golden artifacts. Modify affected package READMEs, API/dependency/index/release docs.

**Interfaces:**
- Consumes Tasks 1 to 5 and fixed-camera native fixtures.
- Produces goldens/evidence for paint cuts/feathers, river/bridge, roofs/indoor shadows, free props/buildings, forest density and ridge.
- Publishes the shared view/snapshot/closure/residency contract for R9 to R11 and Grimhollow.

- [ ] Deliver backend pixel proof, preserved legacy goldens and next-available minor release candidate.
- **Tests:** `NativeTerrainCutsAndFeathers_Golden`, `NativeRiverBridge_Golden`, `NativeRoofHiddenShadow_Golden`, `NativeFreeBuildingsAndProps_Golden`, `NativeFoliageAndRidge_Golden` keep fixed cameras and Native TAA for Grimhollow fixtures. `ThinFeatureAndShadowCoverage_IsObservable` adds targeted ROI/geometry-backed assertions. Shared multi-image Scene3D fixtures carry fresh-versus-aged parity. Existing TileWorld and analytic-terrain goldens remain unchanged.
- **Exit proof:** Hosted metal-native/direct3d11-native/vulkan-native legs bake then verify their own committed families per CROSS-PLATFORM.md. Record artifacts/results, prove zero skipped required GPU tests, then one serialized full round Release build/suite and guards. Owner reviews native appearance and tags.

## Execution and planning review

Before execution, refine each task to failing-test, implementation, verification and explicit-path commit steps against landed APIs. Record the chosen signatures, file moves, schema numbers, judgement rulings and refinement commit in Outcome. Run targeted Release tests synchronously per task, then the repository Release build and full non-LiveSocket suite once at the round finish. Run the applicable documentation, dependency and whole-tree guards. Rendering uses the hosted backend workflow and committed family goldens. Integration, packing and tagging belong to the owning orchestrator and owner.

Self-review covered spec requirements, consistent contract names, all five Review Focus tests and task proportion. This task-level plan contains no implementation bodies and does not claim later-round completion.

## Outcome

### OA9 documentation outcome, 2026-10-05

- Recorded [OA9](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#owner-rulings-binding-direction) and this plan's [checkpoint](#oa9-checkpoint-before-r8-approval). Existing source-count fixtures and prior guard results below remain historical.
- R8 remains unapproved. Its checkpoint names pending choices, owning tasks/dependencies and future proofs, to be refined before owner round approval. No capability, art, swimming, world enlargement or fresh benchmark is claimed.
- This revision requires serial doc guards and explicit-path commit. The worker stops at the docs commit for controller verification/push, with no builds/tests/format/pack or integration.

### Historical documentation reconciliation before OA8/OA9, 2026-10-05

- Approval stage: specs approved with revised T4. R8 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: Refine this draft against released prerequisite APIs at round start. Exact signatures, failing assertions and owner plan approval remain open. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: source and planning review only, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: Native selection consumes revised-T4 selectable envelopes. Physical occlusion/statics remain separate, with only the recorded roof-view filter changing presentation channels.
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
