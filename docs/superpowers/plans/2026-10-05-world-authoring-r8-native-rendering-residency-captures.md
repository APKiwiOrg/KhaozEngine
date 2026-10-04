# World authoring R8: Native rendering, residency and captures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Render and capture the complete native resolved world through one adapter while preserving authored geometry and duplicate-free streaming.

**Architecture:** Add an opt-in MapDoc.Render3D adapter over the immutable headless build and verified render asset closure. Native view, MapEditor viewport and ke-mapedit snapshots share terrain, placement, local-floor, light, water, roof and foliage composition. Residency uses effective bounds with one owner and all intersected storage memberships, independently of the server's complete statics.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), approved direction at commit `49b045f75`. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 are owner rulings. T1 to T9 remain subject to owner review.

Task-level plan. Refine to full step level against the landed R1 to R7 APIs before executing, and record the refinement in Outcome.

## Global Constraints

- "One tool, MapEditor GUI plus ke-mapedit MCP, authors terrain, props and buildings. No two-format hybrid"
- "Props and buildings accept free position, yaw and positive uniform scale as an engine-wide capability"
- "Grimhollow leaves TileWorld completely through the full native MapDoc swap, option A"
- "Core MapDoc and its runtime packages acquire no TileWorld or Grimhollow dependency." The optional offline importer is the only new package allowed to read TileWorld. Existing analytic MapDoc and TileWorld consumers retain current behavior.
- "The ordinal list position is never identity." Numeric IDs are positive int64, encoded as decimal strings in JSON/MCP. Imported leaf IDs survive. Persist allocation high-water marks, tombstones and prefab child bindings. Redo restores allocation rather than allocating again.
- "All newly structured payloads use closed schemas and explicit payload versions." Schema transitions are baseline-relative, not reserved numbers. Rebase them onto the next free format if concurrent work takes one. Pure sequential migrations preserve old analytic execution. Refuse future versions.
- Authored-world identity includes normalized MapDoc, complete prefab/material/asset/collider/light/LOD closure and builder versions/options. Validate missing, duplicate, cyclic, stale or unsupported references before building. Equal TileSize monolithic/tiled forms resolve equally. Keep playable bounds separate from storage bounds.
- "One GPU-free triangle compiler yields vertices, topology, surface IDs and geometric normals." Rendering, floor sampling, physics and capture consume the same descriptors. No bilinear movement fallback or coarser height-field replacement for authored geometry.
- C2 surfaces distinguish canonical support geometry from non-colliding material-override paint. One owner per floor. C3 applies position/yaw/positive uniform scale exactly once. Use oriented asset collision/selection shapes for pick/reach/stance. A broad-phase AABB is not a narrow-phase target.
- C4 bounded water retains its explicit level, medium and domain. No rim or camera recomputation, double global-water rendering or implicit terrain-driven water-level edits.
- "GUI and MCP invoke one undoable native command layer." Mutations report affected IDs, dirty bounds, identity and undo/redo labels. Validation failure writes nothing. Save validates the closure and writes atomically, retaining unindexed-overwrite protection.
- "Each release takes the next available engine minor after the concurrent pivot program's releases. Only the owner tags." Reserve no engine numbers. Re-read main/version/tags at execution, ride the applicable unreleased round version and reconcile through the owning orchestrator. No pivot repin. Grimhollow adoption waits for both 0.11.0 and accepted grand-world on main.
- This documents-only lane changes no production code, version, release history or spec. It commits and pushes only its task branch, with no merges, tags or issues. Future package-bearing rounds update version, CHANGELOG and guarded doc declarations together under repository release policy.
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

Produces C8 native render adapter/view/snapshot, effective residency integration and complete captures with no schema revision. R9 uses the viewport. R10 uses render/query/streaming tool adapters. R11 uses fixed-camera differential evidence and complete-load versus windowed proof. Grimhollow adoption receives the same client/SnapshotTool view while the GPU-free server keeps independent complete statics.

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
- J8.2. Each resolved resource has a deterministic owner key and references in every intersected storage chunk. Any loaded membership retains it once. Unload releases it only when no membership remains. Complete server statics do not follow camera residency.
- J8.3. A windowed document exposes its validation scope and missing closure explicitly. Whole-world validation/capture requires the complete closure or deliberate closure loading, never a successful partial result labelled complete.
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
- Partial load reports incomplete validation scope. Complete server build remains independent from visual window changes.

- [ ] Deliver cross-chunk load equivalence, single publication and stale-build rejection.
- **Tests:** `AnchorOutsideWindow_SpanningBuildingAndTreeRemainResident` covers negative seams and elevated floors/lights. `TwoLoadedMemberships_DrawAndStaticsExistOnce` checks owner reference counts. `UnloadOneMembership_RetainsResourceUntilLastRelease` proves cleanup. `LateChunkBuild_CannotResurrectOldClosure` checks generation fence. `CompleteLoadEqualsChunkedResolvedInventory` includes markers/foliage/water and storage-independent coordinates. `PartialLoad_CannotClaimWholeWorldValidation` is fail-closed. `VisualWindowChanges_DoNotChangeCompleteServerStatics` proves server independence.
- **Exit proof:** Release filter `FullyQualifiedName~MapResolvedResidencyTests` passes. Repeatedly calling lifecycle operations within one deterministic test is a bounded semantic sequence, not a stress loop.

### Task 4: Roof visibility, shadows, picking and camera channels

**Files:** Create NativeMapRoofs.cs/NativeMapRoofRenderTests.cs. Modify NativeMapView and viewport camera/pick adapter hooks.

**Interfaces:**
- Consumes R5 membership and Auto/AlwaysVisible/AlwaysHidden roof policy.
- Produces shadow-only roof submission, visible selection/camera blockers and shared indoor-lighting input.
- Physical world statics and complete capture descriptors remain intact across view-mode changes. Non-solid selection uses C3 selection shapes.

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
