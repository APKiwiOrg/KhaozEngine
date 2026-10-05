# World authoring R9: Unified MapEditor workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Make MapEditor the complete native world authoring UI for terrain, props, buildings, water, markers and foliage through shared commands.

**Architecture:** Build focused tools/inspectors on the landed command layer and R8 native view. World and prefab-local modes share operations and explicit coordinate/selection context, with optional local snapping and one transaction per gesture. UI previews, diagnostics, undo/redo and save/reload use the same resolver and geometry the server consumes.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 and T1 to T9 with revised T4 are approved. This round plan remains pending owner review.

Task-level plan. Refine to full step level against the landed R1 to R8 APIs before executing, and record the refinement in Outcome.

## Approval stage, reconciled 2026-10-05

The controller supplied the exact owner answer "Approve" for both specs, T1 to T9 with revised T4, rigid prefab v1 and the pre-OA9 12 to 18 elapsed-week estimate, and C4 boundary policy. This R9 document remains a draft for refinement at its round. It requires a full plan against owner-released dependencies and then owner plan approval before execution. Spec approval is not acceptance of actual changed targets, distances, occlusion, stances or exact water-boundary samples. R11 records named import acceptance.

The 1 m allowance is a minimum vertical target reach-envelope height, preserving `MinimumObjectReachHeight`. It is not a 1 m action distance. Action range remains existing game policy and physical colliders never expand. Approval IDs are OA4 specs, OA5 revised T4, OA6 prefab/estimate and OA7 water boundary in the game DECISIONS record.

## OA9 checkpoint, R9 round refinement

[DG9.1 to DG9.4](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#oa9-design-gates-and-future-exit-proofs) extend this draft. R9 remains unapproved. Consume the approved cave/domain model from [R2](2026-10-05-world-authoring-r2-authored-terrain-paint.md)/[R4](2026-10-05-world-authoring-r4-bounded-water-medium.md)/[R5](2026-10-05-world-authoring-r5-free-buildings-prefabs-interiors.md) and scoped residency/nav diagnostics from [R3](2026-10-05-world-authoring-r3-shared-shapes-headless-builders.md)/[R8](2026-10-05-world-authoring-r8-native-rendering-residency-captures.md).

- **Tasks 1/2/4/5:** Define underground layer/volume selection, floor and ceiling editing or cave-prefab workflow as selected, explicit marker support, layer-aware vegetation and bounded water inspection. Show active domain/depth and on-demand completeness. Brushes, selection, support snap and route previews must not silently edit/query the surface or another stacked level.
- **Named future input proofs:** `InputWorkflow_AuthorsSelectedCaveModelAndContainedLake`, `UndergroundUndoRedoSaveReload_PreservesLayerAndClosure` and `LargeWorldScopedEdit_ReportsAffectedNavTilesAndMissingData`. Use real Pointer/InputState/key paths, not direct-handler-only proof. Pin supported coordinate/precision bounds and localized refused states before R9 approval.
- Share these commands/domain selectors with [R10](2026-10-05-world-authoring-r10-complete-ke-mapedit-parity.md). Future edits expose affected-tile rebakes, with the once-only adoption rebake retained only as its fixture procedure. No cave art, swimming or world enlargement is implemented here.

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

1. A cancelled drag or mode switch while editing must not commit a partial transform or route undo to the wrong document. Task 1 pins transaction context.
2. A group containing support-snapped and explicit-Y children must transform once without changing numeric IDs or silently snapping art. Task 3 tests mixed groups.
3. A terrain/material/water brush near storage seams or a NoDraw cell must preview the actual floor and invalidate all dependent views. Task 2 tests seam edits.
4. A prefab asset save with stale overrides or incomplete selected closure must refuse without corrupting referring instances. Task 4 tests validation and cancellation.
5. Palette/inspector controls reachable only through direct command tests can pass while the UI is unusable. Task 6 requires pointer/key traversal and localized feedback.

## Dependencies and delivered contracts

| Earlier round | Consumed contract |
| --- | --- |
| R1 / C1, transitively | Stable identity/display-label distinction, native asset palette/closure, validation/save/summary and undoable commands |
| R2 / C2 | Native height/material/cut/flag/feather/layer commands and canonical preview |
| R3 / C3 | Free gizmo transforms, effective shape/pick/reach/stance, collision-height measurement and headless build diagnostics |
| R4 / C4 | Bounded-water level/domain/medium commands and preview |
| R5 / C5 | Prefab-local editing, extract/place/edit/override/unpack, volume/roof links and optional snap |
| R6 / C6 | General point/role/height controls and typed spawn projections |
| R7 / C7 | Density layer/raster/brush commands and exact sample preview |
| R8 / C8 | Native viewport/snapshot, residency, roof/camera/picking and complete capture |

Produces the C9 GUI frontend covering the native shared command surface, with world/prefab mode, diagnostic overlays and real input acceptance proof. R10 consumes UI command mappings as its MCP equivalence oracle. R11 and Grimhollow adoption receive one complete authoring tool, with no TileWorld editor or grid-placement dependency. No schema revision is planned.

**Name discipline:** `NativeCommandResult`, `NativeAssetDescriptor`, `MapWorldBuild` and exact surface/shape query methods are provisional R1 to R4 names. Confirm R5 to R8 command/view signatures at refinement. Baseline EditorDocument, EditorHistory, EditorSelection, MapEditorScene, InputState/InputManager/Pointer and MapEditorStrings exist.

## File Structure

| Action and engine path | Responsibility |
| --- | --- |
| Create `KhaozEngine.MapEditor/Native/NativeAuthoringContext.cs`, `NativeSelectionController.cs`, `NativeHistoryController.cs`, `NativeDocumentInspector.cs` | World/local mode, reference-aware selection, gesture transactions, lifecycle/storage/closure controls |
| Create `KhaozEngine.MapEditor/Native/NativeSurfaceTool.cs`, `NativePaintInspector.cs`, `NativeWaterTool.cs` | Canonical floor/paint/layer/water controls |
| Create `KhaozEngine.MapEditor/Native/NativePlacementTool.cs`, `NativePlacementInspector.cs`, `NativeBatchTransformTool.cs` | Free transforms, identities/tags/variants and batch placement |
| Create `KhaozEngine.MapEditor/Native/NativePrefabMode.cs`, `NativePrefabInspector.cs`, `NativeInteriorTool.cs` | Local editing, optional snap, overrides/floors/volumes/roofs |
| Create `KhaozEngine.MapEditor/Native/NativeMarkerTool.cs`, `NativeFoliageTool.cs`, `NativeCollisionInspector.cs` | Points/density and shape/nav/measurement diagnostics |
| Modify `MapEditorScene.cs`, its Input/Chrome/Document/Overlays/Shortcuts partials, `EditorTool.cs`, `EditorSelection.cs`, `MapEditorStrings.cs` | Thin routing and localized affordances, no growth of monolithic command implementations |
| Modify native command files from earlier rounds only for missing shared behavior | Required GUI/MCP operations remain in one home |
| Create `KhaozEngine.MapEditor.Tests/MapEditor/NativeAuthoringContextTests.cs`, `NativeSurfaceToolTests.cs`, `NativePlacementToolTests.cs`, `NativePrefabWorkflowTests.cs`, `NativeMarkerFoliageToolTests.cs`, `NativeAuthoringInputTests.cs` | Headless input/controller/transaction tests |
| Create `KhaozEngine.MapEditor.Tests/MapEditor/NativeAuthoringWorkflowGpuTests.cs` | Actual UI draw and pointer/key capture evidence |
| Modify MapEditor/MapEdit.Tool READMEs, live API docs, index and release declarations | Complete workflow and consumer handoff |

## Judgement and refinement record

- J9.1. World and prefab-local contexts have separate document histories and explicit coordinate/selection scope. Refuse a context switch with a live gesture until it is committed or cancelled. Never apply local snapping to runtime resolution.
- J9.2. Asset identity is immutable placement ID/numeric ID, with display-label rename distinct from explicit reference-remapping. Inspect Kind, variant and asset descriptor separately.
- J9.3. Batch/map-wide transformations call the shared command layer, including map_translate of terrain, placements, markers, water and foliage atomically. The GUI does not implement parallel mutation algorithms.
- Input evidence must start at Pointer/InputState and reach the visible control. Calling its command handler directly is only a unit proof. Plan one final owner workflow review after automated evidence, no exploratory consumer client boot.

---

### Task 1: Context, selection and transactional history

**Files:** Create NativeAuthoringContext/SelectionController/HistoryController/DocumentInspector.cs and NativeAuthoringContextTests.cs. Modify EditorSelection and scene routing/history affordances.

**Interfaces:**
- Consumes C9 shared command results/history and C1 stable IDs/closure validation.
- Produces explicit world/local context, selected typed resource IDs, pending gesture state and commit/cancel semantics.
- Commands report affected IDs/old-new bounds/identity/history labels to the viewport and diagnostics. Undo/redo chooses the active document intentionally.
- Produces native open/create/save/summary/validate controls, asset/material inventories and storage_chunk_create/delete/list. Keep storage distinct from tagged gameplay regions and expose partial-validation scope.

- [ ] Deliver complete authoring context and reference-aware selection without leaking edits between modes.
- **Tests:** `ContextSwitchDuringGesture_RequiresCommitOrCancel` writes no partial state. `CancelledDrag_RestoresDocumentHashIDsAndHistory` covers all resources. `UndoInPrefabMode_DoesNotMutateWorldHistory` and inverse prove scope. `InvalidSelectionOrReferenceEdit_PreservesActiveClosure` prevents dangling selection. `DocumentInspector_ExposesCompleteInventoryAndValidationScope` pins summary/catalog findings. `StorageDeleteWithUnselectedSpanningContent_Refuses` guards extent edits. `NativeAndAnalyticModeRouting_PreservesExistingWorkflow` checks opt-in behavior.
- **Exit proof:** Release filter `FullyQualifiedName~NativeAuthoringContextTests` passes with InputState-based gesture entry and atomic history/invalidation results.

### Task 2: Native terrain, paint and water workflow

**Files:** Create NativeSurfaceTool/PaintInspector/WaterTool.cs and NativeSurfaceToolTests.cs. Modify scene overlay/tool routing and localized strings.

**Interfaces:**
- Consumes R2 height set/read/raise/flatten/smooth/import, layer/material/cut/rotation/flags/feather commands and R4 water body commands.
- Produces named-surface brushes and actual triangle-floor height/normal preview, explicit units/row orientation and visible-material readout.
- Produces surface_layer_add/remove and material_set controls with reference-aware validation. Surface-cell/rect inspectors show exact values as well as preview colors.
- Produces water boundary/level/medium inspector with the native C4 half-open domain and bridge feet preview. Surface edits invalidate terrain/physics/nav/material/residency as one transaction.

- [ ] Deliver terrain and water authoring using native DTOs and canonical previews.
- **Tests:** `SurfaceBrushAtStorageSeam_InvalidatesBothSidesAndDependentCaches` checks touched IDs/bounds. `CutFlagFeatherPaint_OmittedLayersRemainUntouched` covers every named cut/rotation/flag and void clear. `LayerAndMaterialControls_RejectDanglingOwnersAndBindings` covers create/edit/remove. `NoDrawPreview_DoesNotInventCaptureSupport` checks absence. `WaterLevelEdit_IsIndependentOfTerrainBrush` includes bridge preview. `HeightImportPreview_DeclaresUnitsAndRowOrientation` checks exact read/write array shapes. Invalid finite/overlap input uses localized findings and writes nothing.
- **Exit proof:** Release filter `FullyQualifiedName~NativeSurfaceToolTests` passes. Visible preview values match canonical headless queries, not a document-only height estimate.

### Task 3: Free placement, identity and batch transforms

**Files:** Create NativePlacementTool/Inspector/BatchTransformTool.cs and NativePlacementToolTests.cs. Modify gizmo/picking/selection routing and localized strings.

**Interfaces:**
- Consumes C1 identity/asset/variant/tags and C3 effective transforms/shape queries.
- Produces place/move/yaw/scale/delete/duplicate/label/tag controls, explicit Y or deliberate support snap and measured effective bounds.
- Produces placement_get/find and anchor-versus-overlap rect selection controls, with explicit asset/Kind/tag/ID filters and stable ordering.
- Produces line/scatter, multi-remove, group transform and map_translate controls. Batch outputs expose every allocated ID, one undo step and stable redo.

- [ ] Deliver free placement and batch tools with shared collision-aware previews.
- **Tests:** `PointerGizmo_FreeYawScaleAndXYZPreserveIDs` uses yaw 0.371 and scale 1.137. `MixedHeightGroup_TransformsOnceWithoutImplicitSnap` includes a prefab child/explicit-Y prop. `LabelRename_DoesNotRemapStableIdentity` covers explicit remap rejection/confirmation semantics. `BatchTransformAndMapTranslate_MoveEveryResourceAtomically` includes water/markers/foliage/terrain. `BatchRedo_ReturnsSameAllocation` fixes legacy redo churn. `ShapePickThroughDoor_DoesNotUseBuildingAabb` verifies the shared query.
- **Exit proof:** Release filter `FullyQualifiedName~NativePlacementToolTests` passes. GUI command output equals the shared command document/hash/dirty bounds and IDs.

### Task 4: Prefab-local, override and volume workflow

**Files:** Create NativePrefabMode/Inspector/InteriorTool.cs and NativePrefabWorkflowTests.cs. Modify scene context and palette/localized controls.

**Interfaces:**
- Consumes R5 prefab_open/edit/override/unpack/save/place/list and interior_volume/roof_link commands plus local surfaces/water/markers.
- Produces extract/save/load local editing, optional snap, child collider/opening preview, instance override inspection and unpack.
- Produces prism lower/upper Y/storey editing, roof memberships and observer volume preview using shared R5 membership.

- [ ] Deliver a complete rigid-building workflow without nested prefabs, CAD or generated foundations/stairs.
- **Tests:** `PrefabLocalWorkflow_UsesSamePlacementFloorWaterMarkerCommands` covers optional C4 local water approved by OA6, resolving J5.1. `OptionalSnap_DisabledPermitsSubmetreNonQuarterPlacement` ensures runtime independence. `StaleOverrideOrPartialRoofSelection_RefusesAssetSave` preserves every referring instance. `PrefabEditCancel_RestoresLocalAndWorldClosure` checks histories. `VolumeAndRoofLinkPreview_AgreesAcrossStoreys` checks open shelter and explicit floor ownership.
- **Exit proof:** Release filter `FullyQualifiedName~NativePrefabWorkflowTests` passes. Asset edit invalidation reaches every instance and save/reload preserves bindings/effective transforms.

### Task 5: Marker, foliage and shape diagnostics

**Files:** Create NativeMarkerTool/FoliageTool/CollisionInspector.cs and NativeMarkerFoliageToolTests.cs. Modify overlay/inspector/localization routing.

**Interfaces:**
- Consumes R6 role/name/height/spawn projections, R7 raster/layer/sample previews and C3 collision/reach/stance/nav query service.
- Produces marker enabled/role/tag/local ownership controls and foliage set/import/paint/remove controls.
- Produces collider/opening/seating overlays, raw mesh versus effective collision height display, explicit dry-run asset collision edit, and profile/capture-aware clearance/path preview.
- Produces collision_shape_set and collision-height asset controls through shared descriptor commands, with version/closure/refresh findings separate from world undo.
- Missing nav profile/capture is a finding, never slope-only clearance success.

- [ ] Deliver native point/density authoring and honest collision/route/height diagnostics.
- **Tests:** `MarkerAndFoliageControls_UseRegistryAndExactRaster` preserves explicit marker Y and positive-Z rows. `CollisionInspector_UsesPhysicalShapesAndSharedInteractionEnvelope` separately visualizes unchanged physical collision/occlusion and revised-T4 pick/reach/stance envelopes, including minimum 1 m vertical height, consistent selectable bounds, lower-2-m tree band and door aperture. No GUI-only AABB/footprint target. `MissingNavProfileOrCapture_ShowsLocalizedRefusal` prevents misleading clearance. `MeasuredHeightDryRun_DoesNotResizeMeshOrWriteWorld` separates descriptor files and refresh results. `MovedBuilding_InvalidatesFoliagePreviewAndSeatingDiagnostic` uses shared version/cache inputs.
- **Exit proof:** Release filter `FullyQualifiedName~NativeMarkerFoliageToolTests` passes. Diagnostics identify exact geometry/profile/asset dependencies and every error has a reachable UI state.

### Task 6: Input-path acceptance, save/reload and published workflow

**Files:** Create NativeAuthoringInputTests.cs/NativeAuthoringWorkflowGpuTests.cs. Modify live frontend/API/index/release docs and fixed workflow fixtures.

**Interfaces:**
- Consumes Tasks 1 to 5 and R8 native viewport/capture.
- Produces a replayable authoring scenario driven through Pointer/InputState and keyboard shortcuts, recorded selection/history/hash/dirty bounds and fixed-camera UI captures.
- Produces GUI command inventory covering every native operation mapped in R10, including asset/material/layer/storage/roof/shape operations.

- [ ] Deliver complete input-path authoring acceptance and next-available minor release candidate.
- **Tests:** `InputWorkflow_AuthorsTerrainPropBuildingWaterMarkerFoliage` traverses real visible tools/inspectors rather than directly invoking commands. `InputUndoRedoSaveReload_PreservesExactIdentityAndClosure` checks monolithic/tiled save and unindexed-overwrite refusal. `InvalidInputAndSave_ShowsFindingAndWritesNothing` includes missing closure. `GuiCommandInventory_CoversNativeSurface` names every operation and points to its UI action. `NativeAuthoringWorkflow_GpuCapture` proves visible controls, previews and roof/shape overlays through the same scene.
- **Exit proof:** Synchronous Release filter `FullyQualifiedName~NativeAuthoringInputTests` passes. Backend UI/capture proof reports zero skipped required GPU cases, then one serialized full round build/suite and guards. Owner workflow review uses one copy-ready MapEditor command from the actual integrated checkout, required environment inline. Only owner tags.

## Execution and planning review

Before execution, refine each task to failing-test, implementation, verification and explicit-path commit steps against landed APIs. Record the chosen signatures, file moves, schema numbers, judgement rulings and refinement commit in Outcome. Run targeted Release tests synchronously per task, then the repository Release build and full non-LiveSocket suite once at the round finish. Run the applicable documentation, dependency and whole-tree guards. Rendering uses the hosted backend workflow and committed family goldens. Integration, packing and tagging belong to the owning orchestrator and owner.

Self-review covered spec requirements, consistent contract names, all five Review Focus tests and task proportion. This task-level plan contains no implementation bodies and does not claim later-round completion.

## Outcome

### OA9 documentation outcome, 2026-10-05

- Recorded [OA9](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#owner-rulings-binding-direction) and this plan's [checkpoint](#oa9-checkpoint-r9-round-refinement). Existing source-count fixtures and prior guard results below remain historical.
- R9 remains unapproved. Its checkpoint names pending choices, owning tasks/dependencies and future proofs, to be refined before owner round approval. No capability, art, swimming, world enlargement or fresh benchmark is claimed.
- This revision requires serial doc guards and explicit-path commit. The worker stops at the docs commit for controller verification/push, with no builds/tests/format/pack or integration.

### Historical documentation reconciliation before OA8/OA9, 2026-10-05

- Approval stage: specs approved with revised T4. R9 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: Refine this draft against released prerequisite APIs at round start. Exact signatures, failing assertions and owner plan approval remain open. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: source and planning review only, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: GUI diagnostics distinguish physical geometry from canonical interaction envelopes. Prefab-local C4 water is explicitly approved by OA6. Input/workflow acceptance remains future work.
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
