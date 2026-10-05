# World authoring R10: Complete ke-mapedit parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Expose every ke-tileedit world-editing capability and every required native addition through ke-mapedit with verified GUI/MCP equivalence.

**Architecture:** Complete the existing ke-mapedit services and focused MCP verb classes over the shared native command/query layer. Keep session/storage/file operations explicit, authored geometry queries profile-aware, and assets separate from world mutations. Inventory/schema tests and wire-level tests prove capability parity, identity-safe undo/redo, atomic refusal and legacy analytic compatibility.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 and T1 to T9 with revised T4 are approved. This round plan remains pending owner review.

Task-level plan. Refine to full step level against the landed R1 to R9 APIs before executing, and record the refinement in Outcome.

## Approval stage, reconciled 2026-10-05

The controller supplied the exact owner answer "Approve" for both specs, T1 to T9 with revised T4, rigid prefab v1 and the 12 to 18 elapsed-week estimate, and C4 boundary policy. This R10 document remains a draft for refinement at its round. It requires a full plan against owner-released dependencies and then owner plan approval before execution. Spec approval is not acceptance of actual changed targets, distances, occlusion, stances or exact water-boundary samples. R11 records named import acceptance.

The 1 m allowance is a minimum vertical target reach-envelope height, preserving `MinimumObjectReachHeight`. It is not a 1 m action distance. Action range remains existing game policy and physical colliders never expand. Approval IDs are OA4 specs, OA5 revised T4, OA6 prefab/estimate and OA7 water boundary in the game DECISIONS record.

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

1. A wire client must not round a large int64 ID or send a numeric enum that silently lands as a wrong cut/flag. Tasks 2 and 7 test schemas.
2. Pipelined calls, cancellation or map switching must not reorder mutation/save or return a summary from a half-edit. Tasks 1 and 7 test transport/session ordering.
3. A storage deletion or batch/map-wide operation must not drop overhanging prefab children or allocate new IDs on redo. Tasks 1 and 3 test closure and allocation.
4. A route query with no valid profile/capture must refuse authored-world clearance, not reuse slope/global-water success. Task 5 tests missing/stale inputs.
5. Asset-height updates can leave stale session geometry or be mistaken for a world undo operation. Task 6 tests dry-run, atomic closure and refresh reporting.

## Dependencies and delivered contracts

| Earlier rounds | Consumed contracts |
| --- | --- |
| R1 / C1 | Identity/allocator/schema/closure/storage/session command result |
| R2 / C2 | Exact surface paint/flags/corners and height/layer/material commands |
| R3 / C3 | Effective asset shapes/selection/pick/reach/stance, complete capture and measured-height/shape commands |
| R4 / C4 | Bounded-water body commands |
| R5 / C5 | Prefab local edit/extract/list/place/override/unpack and interior/roof commands |
| R6 / C6 | Registry/role/name/height/spawn commands and lossless projections |
| R7 / C7 | Density/raster/brush commands, deterministic sample preview and exclusions |
| R8 / C8 | Complete native captures, residency and observer-aware snapshot |
| R9 / C9 | Reachable GUI operation inventory invoking the same native command layer |

Produces the complete T7/C9 MCP surface, exact parity fixture table, request/result schemas and GUI/wire equivalence proof. R11 uses one offline authoring/validation surface and exhaustive inventories. Grimhollow adoption retires ke-tileedit authoring after its accepted native import. Old mapedit analytic/procedural/scatter/spawn/region/storage verbs continue to work. No schema revision is planned.

**Name discipline:** `NativeCommandResult`, `MapWorldBuild`, `NativeAssetDescriptor`, nav-capture/profile query methods and storage-owner descriptors are provisional R1 to R4 contracts. Resolve later-round command signatures against their landed Outcomes. Existing tool names in the parity table are verified against baseline Tools/*.cs, not inferred from target names.

## Complete ke-tileedit README parity map

Source is `KhaozEngine.TileEdit.Tool/README.md` at the pinned spec baseline. All 50 documented verbs appear once. **Existing** means the native name exists now and needs extended semantics. **New** means absent now, including commands supplied by R2 to R7 before R10. Capabilities are native, no verb accepts a TileWorld world or silently converts plane/tile addresses.

| ke-tileedit verb | ke-mapedit equivalent | Baseline status | Required contract and owning task |
| --- | --- | --- | --- |
| world_open | map_open | Existing | Validated native closure/session. Task 1 |
| world_create | map_create | Existing | Explicit analytic/authored mode. Task 1 |
| world_save | map_save | Existing | Atomic validated closure, same storage form. Task 1 |
| world_summary | map_summary | Existing | Layer/instance/original-leaf/ID counts and history/dirty state. Task 1 |
| world_validate | map_validate | Existing | Schema/closure/runtime findings and scope. Task 1 |
| catalog_list | asset_list and material_list | New | All legal native bindings, kind/variant distinct. Task 1 |
| region_create | storage_chunk_create | New | Storage extent only, no gameplay tile. Task 1 |
| region_delete | storage_chunk_delete | New | Refuse content loss unless explicitly selected, one atomic undo. Task 1 |
| region_list | storage_chunk_list | New | Storage inventory separate from tagged regions. Task 1 |
| undo | undo | New | Affected IDs/bounds/hash/labels and depth, bounded steps. Task 1 |
| redo | redo | New | Restore IDs, never reallocate. Task 1 |
| tile_get | surface_cell_get | New | Exact paint/flags/corners and derived physics. Task 2 |
| tile_set | surface_cell_set | New | Omitted fields untouched, explicit clear. Task 2 |
| tiles_fill | surface_cells_fill | New | Named surface rect, one transaction. Task 2 |
| tiles_get_rect | surface_cells_get_rect | New | Exact lossless values plus declared ASCII view/legend. Task 2 |
| height_set | surface_height_set | New | Exact corner patch, declared unit and row direction. Task 2 |
| height_raise | surface_height_raise | New | Signed delta and bounded edge falloff. Task 2 |
| height_flatten | surface_height_flatten | New | Explicit value or rounded rect mean. Task 2 |
| height_smooth | surface_height_smooth | New | Shared R2 J2.1 contract, 1 to 64 passes, double-buffered 3x3 average, unchanged halo and AwayFromZero quantization. Task 2 |
| height_get_rect | surface_height_get_rect | New | Same shape/unit/orientation as set. Task 2 |
| height_import | surface_height_import | New | P5 PGM/noninterlaced PNG, 8/16-bit, explicit range/orientation. Task 2 |
| object_place | placement_add | Existing | Asset/XYZ/yaw/scale/tags, stable/numeric IDs. Task 3 |
| object_move | placement_move | Existing | Full XYZ or explicit support snap, old/new bounds. Task 3 |
| object_rotate | placement_rotate | Existing | Yaw radians, shape-aware dirty bounds. Task 3 |
| object_remove | placement_remove | Existing | Reference checks, tombstone and exact undo. Task 3 |
| object_set_tags | placement_set_tags | New | Replace ordered tags, null/empty clears. Task 3 |
| object_get | placement_get | New | Identity, asset/variant/Kind, transform, shape, roles. Task 3 |
| objects_in_rect | placements_in_rect | Existing | Explicit anchor/overlap mode, deterministic order. Task 3 |
| object_find | placement_find | New | Asset/Kind/tag/ID filters, both filters combine. Task 3 |
| objects_line | placements_line | New | World endpoints/metre spacing/free transforms, all allocated IDs, one undo. Task 3 |
| objects_scatter | placements_scatter | New | Seed/spacing/jitter, shared shape/exclusion tests, valid empty result. Task 3 |
| marker_set | marker_set | New | General XYZ/yaw/role/height/enabled, typed spawn projection. Task 4 |
| marker_remove | marker_remove | New | Stable identity/reference checks and exact undo. Task 4 |
| marker_list | marker_list | New | All roles/landmarks/disabled entries, deterministic name order. Task 4 |
| prefab_save | prefab_save | New | Extract complete selected child/surface/volume closure with local keys. Task 4 |
| prefab_place | prefab_place | New | Free world transform and serialized child ID bindings. Task 4 |
| prefab_list | prefab_list | New | Version/digest/closure and directory findings. Task 4 |
| foliage_layer_set | foliage_layer_set | New | Complete validated versioned layer/raster. Task 4 |
| foliage_get | foliage_get | New | Detached layer or authoring-order list/raster metadata. Task 4 |
| foliage_density_set | foliage_density_set | New | Exact 0 to 255 byte rows, declared positive-Z convention. Task 4 |
| foliage_paint | foliage_paint | New | World metre radius, density and hardness. Task 4 |
| foliage_remove | foliage_remove | New | Layer removal, no gameplay/collision effect. Task 4 |
| collision_at | collision_at | New | World XYZ/height, oriented compound shapes and masks. Task 5 |
| is_walkable | is_walkable | Existing | Actual shape clearance, explicit profile/capture, not slope alone. Task 5 |
| path | path | New | Continuous route/profile/window, reached and nearest reachable result. Task 5 |
| walkable_rect | walkable_rect | New | Resolution/profile-declared clearance grid and legend. Task 5 |
| render_topdown | render_topdown | Existing | Native surface/observer framing, fixed overlays and inline PNG. Task 5 |
| render_view | render_view | Existing | World camera/target and roof observer, complete inline PNG. Task 5 |
| archetype_measure_heights | asset_measure_heights | New | Raw mesh max Y versus transformed collision bottom/top. Task 6 |
| archetype_set_collision_heights | asset_set_collision_heights | New | Versioned descriptor, dry-run, atomic closure and refresh report. Task 6 |

### Additional required native operations

| Surface | Required additions and task |
| --- | --- |
| Free/group edits | placement_scale (existing), placement_batch_transform, placements_remove, map_translate. Task 3 |
| Native surfaces/materials | surface_layer_add/remove, material_set. Task 2 |
| Bounded water | water_body_add/set/get/list/remove. Task 4 |
| Prefab lifecycle | prefab_open/edit/override/unpack. Task 4 |
| Volumes and roofs | interior_volume_add/set/remove, roof_link_set. Task 4 |
| Collision authoring | collision_shape_set. Task 6 |

Keep existing set_window/window_status/convert_to_tiled/convert_to_single/retile, procedural/scatter/companion/freeze/sculpt/spawn/region verbs on old maps. Authored mode refuses implicit analytic regeneration. Native row orientation is explicit, negative world Z is not treated as legacy tile north. Height edits report covered versus landed corners at extent edges. Batch line/scatter report every successful allocation.

## File Structure

| Action and engine path | Responsibility |
| --- | --- |
| Modify `KhaozEngine.MapEdit.Tool/MapEditSession.cs`, `Results.cs`, `MapEditSchemaValidation.cs`, `Tools/DocumentTools.cs`, `Tools/McpBootstrap.cs` | Session/native closure and complete registry/result schemas |
| Create `Tools/StorageTools.cs`, `Tools/SurfaceTools.cs`, `Tools/HeightTools.cs`, `Tools/PlacementTools.cs`, `Tools/CollisionTools.cs`, `Tools/AssetTools.cs` | Focused missing adapters, avoid expanding MutationTools monolith |
| Modify landed `Tools/PrefabTools.cs`, `InteriorTools.cs`, `MarkerTools.cs`, `FoliageTools.cs` and R4 water adapter | Complete additional native operations |
| Create `MutationServiceSurfaces.cs`, `MutationServiceBatch.cs`, `AssetEditService.cs` only where not supplied earlier | Shared command delegation and separate asset-file operations |
| Modify `QueryService.cs`, `RenderService.cs`, `Tools/QueryTools.cs`, `Tools/RenderTools.cs` | Native shape/profile/capture queries, analytic compatibility and inline framing |
| Modify shared command files and GUI operation inventory only for missing parity | No second mutation algorithm |
| Create `KhaozEngine.MapEditor.Tests/MapEditTool/NativeDocumentToolTests.cs`, `NativeSurfaceToolParityTests.cs`, `NativePlacementToolParityTests.cs`, `NativeCompositeToolParityTests.cs`, `NativeSpatialToolTests.cs`, `NativeAssetToolTests.cs`, `NativeMcpParityTests.cs` | Task and full wire/GUI parity proofs |
| Modify MapEdit.Tool README, MapEditor README, USING-KHAOZENGINE, package/API/index/release declarations | Exhaustive live verb contracts and next-available release |

## Judgement and refinement record

- J10.1, 2026-10-05. Adopt the shared R2 J2.1 smoothing contract of 1 to 64 passes. Independently checked released-main legacy source and MCP description preserve this supported range. Full wire assertions are added during this draft's round refinement.

- J10.1. Compatibility is explicit by document mode. Old analytic query behavior remains available and labelled analytic. Authored is_walkable/path require a valid shape-aware profile/capture. Do not call an analytic result proof of native clearance.
- J10.2. Native named cuts/flags retain name-based validation and reject unknown/numeric enum values. Surface rows declare units/orientation, exact arrays accompany ASCII maps so legend wrapping cannot lose material IDs.
- J10.3. File-only prefab/asset saves report asset changes outside world undo history. Accepted closure updates invalidate referring resources. A failed refresh reports written changes and sessionRefreshError honestly, never reports an unrefreshed session as current.
- At refinement compare the live baseline README and tool registry again. If new ke-tileedit verbs landed, extend this capability inventory and the test fixture before executing, without editing the approved spec silently.

---

### Task 1: Lifecycle, storage, catalogs and history

**Files:** Modify session/results/schema/DocumentTools/McpBootstrap and create StorageTools.cs/NativeDocumentToolTests.cs.

**Interfaces:**
- Consumes C1 resolver/identity/closure/tiled writer and C9 command history/results.
- Produces all eleven lifecycle/history/storage mappings, plus asset_list/material_list and complete summary/validation scope.
- Storage deletion is content-aware across owner/membership boundaries. Atomic save retains storage form and refuses unindexed overwrites.

- [ ] Deliver complete native lifecycle surface while preserving old storage/session verbs.
- **Tests:** `NativeLifecycle_ValidatesClosureAndPreservesStorageForm` covers monolithic/tiled. `StorageDelete_RefusesUnselectedSpanningContents` restores full closure on undo. `History_ReportsStepsDepthLabelsBoundsAndStableIDs` checks stack exhaustion. `SessionCallOrdering_SaveObservesPriorMutations` uses pipelined requests. `PartialWindowValidation_IsExplicitAndCannotClaimComplete` checks scope.
- **Exit proof:** Release filter `FullyQualifiedName~NativeDocumentToolTests` passes. Lifecycle read calls remain GPU-free and invalid save creates no destination change.

### Task 2: Exact surfaces, heightmaps, materials and layers

**Files:** Create SurfaceTools.cs/HeightTools.cs and missing service adapters. Create NativeSurfaceToolParityTests.cs.

**Interfaces:**
- Consumes R2 commands/triangle preview and C1 schema result envelopes.
- Produces all ten cell/height mappings plus layer/material operations, exact data and ASCII representations.
- Imports declare range, height unit and row convention. PGM/PNG channel/sample rules match the README capability, with coverage/landing counts and finite/bounded validation.

- [ ] Deliver lossless surface/height MCP capability through the same GUI commands.
- **Tests:** `CellEdit_RejectsNumericUnknownEnumsAndLeavesOmittedLayers` covers void clear. `HeightRows_ExactReadWriteAndEdgeCoverage` checks declared north/positive-Z conversion. `HeightImport_PgmPngEightSixteenBitAndRedChannel` covers alpha ignore/noninterlaced validation and documented PGM delimiter handling. `Smooth_RejectsOutsideOneTo64Iterations` accepts 1/16/17/64 and refuses 0/65 atomically. `Smooth_GuiWireAndLegacyOracleAgree` pins the shared R2 J2.1 3x3 prior-pass average, unchanged outside-patch halo and AwayFromZero per-pass quantization. No MCP-only cap or alternate smoothing algorithm. `GuiAndMcp_SurfaceOperationsMatchHashAndDirtyBounds` exercises every operation.
- **Exit proof:** Release filter `FullyQualifiedName~NativeSurfaceToolParityTests` passes for exact values, findings and undo/redo, without GPU work.

### Task 3: Placement lookup, batches and map translation

**Files:** Create PlacementTools.cs/MutationServiceBatch.cs as needed and NativePlacementToolParityTests.cs. Modify shared batch/transform commands if missing.

**Interfaces:**
- Consumes R1 identity and R3 free shape-aware transforms/query/exclusions.
- Produces ten placement mappings, placement_scale/batch_transform, placements_remove and atomic map_translate.
- All spatial parameters use metres/radians with explicit height policy. Queries expose anchor versus overlap mode. Results return each allocated stable/numeric ID, redo restores them.

- [ ] Deliver deterministic single/batch placement editing and whole-map transforms.
- **Tests:** `FreePlacementWire_RoundTripsLargeNumericIDAndOrderedTags` uses an ID above 2^53. `RectQuery_AnchorAndOverlapModesAreDistinct` checks overhanging shapes. `LineAndScatter_OneUndoAllIDsAndStableRedo` includes crowded empty scatter. `MapTranslate_MovesEveryNativeResourceOnceAtomically` covers terrain/local floors/prefabs/markers/water/foliage. `GuiAndMcp_PlacementOperationsMatch` checks rejected edits/hash/bounds.
- **Exit proof:** Release filter `FullyQualifiedName~NativePlacementToolParityTests` passes. No quarter-turn/grid rule or implicit analytic regeneration remains in authored mutation paths.

### Task 4: Composite prefab, marker, water and foliage adapters

**Files:** Modify earlier-round Prefab/Interior/Marker/Foliage/water tool/service adapters. Create NativeCompositeToolParityTests.cs.

**Interfaces:**
- Consumes landed R4 to R7 commands and R9 GUI operation mapping.
- Produces all eleven marker/prefab/foliage mappings and water/prefab/volume/roof additions.
- Prefab extraction/list/file save and local edits distinguish file-only operations from world history. Point roles/heights and positive-Z raster conventions are preserved.

- [ ] Deliver complete composite native surface with consistent errors/results.
- **Tests:** `CompositeWireInventory_CoversEveryRequiredVerb` checks schemas/signatures. `GuiAndMcp_PrefabWaterMarkerFoliageOperationsMatch` compares native document/hash/bounds and rejected edits. `PrefabPlaceRedo_PreservesEveryLeafBinding` fixes old stamp identity drift. `PrefabSave_DoesNotMutateWorldHistory` preserves file-only semantics. `MarkerAndFoliageWire_PreserveExplicitYAndEveryByte` checks elevated/disabled/local markers and raster direction.
- **Exit proof:** Release filter `FullyQualifiedName~NativeCompositeToolParityTests` passes. Every additional operation has a reachable GUI command and a registered MCP adapter.

### Task 5: Shape-aware spatial diagnostics and complete rendering

**Files:** Create CollisionTools.cs/NativeSpatialToolTests.cs. Modify QueryService/QueryTools and RenderService/RenderTools.

**Interfaces:**
- Consumes C3 effective oriented query service, complete nav capture/profile and R8 native snapshot.
- Produces collision_at/is_walkable/path/walkable_rect with declared profile/window/resolution and reached/nearest semantics.
- Produces top-down/perspective text-first inline PNG, optional save path, fixed overlay order, named native surface/observer and honest device/closure failures.

- [ ] Deliver native clearance/routes and complete capture capability without slope-only shortcuts.
- **Tests:** `RouteMissingOrStaleProfileCapture_Refuses` checks source/options hash. `CompoundDoorAndAdjacentWall_UsePhysicalShapeClearance` preserves apertures. `PathWindowLimit_ReportsNearestAndReachedFalse` distinguishes bounded search from disconnected world. `RenderWire_TextThenInlinePngAndOptionalSavePath` checks framing/overlays. `RenderInvalidObserverOrCoincidentEye_FailsBeforeGpu` rejects malformed inputs. `AnalyticLegacyQueries_RemainExplicitlyLabelled` proves J10.1.
- **Exit proof:** Release filter `FullyQualifiedName~NativeSpatialToolTests` passes for CPU/wire cases. Render cases use R8 backend proof with zero required GPU skips and full asset closure.

### Task 6: Measured heights and explicit asset collision updates

**Files:** Create AssetTools.cs/AssetEditService.cs/NativeAssetToolTests.cs. Modify shared shape/asset command and GUI adapters if missing.

**Interfaces:**
- Consumes C3 render-free descriptor plus raw mesh measurement and effective collision bounds.
- Produces asset_measure_heights, asset_set_collision_heights and collision_shape_set, with dry-run diff, valid versioned descriptor writes, changed/skipped/per-entry error rows and session refresh result.
- Asset writes change collision descriptors/closure only, not mesh normalization. Validate all intended files before atomic publication. No world undo entry.

- [ ] Deliver honest, controlled asset-height/shape tooling with refreshed effective geometry.
- **Tests:** `MeasureReportsRawMeshAndTransformedCollisionSeparately` checks scale/local offset. `MissingUnreadableOrNonpositiveMeshTop_IsErrorNotGreybox` preserves source behavior. `HeightDryRun_ChangesNoFileWorldOrHistory` checks all outputs. `CollisionUpdate_AtomicClosureAndAllAffectedStaticsRefresh` covers multiple references. `RefreshFailure_ReportsWrittenChangesAndReopenRequirement` and `NoChanges_DoesNotRefresh` pin session honesty. `DescriptorHeightEdit_DoesNotResizeRenderMesh` guards art.
- **Exit proof:** Release filter `FullyQualifiedName~NativeAssetToolTests` passes. Dry-run and actual publication use the same validated change set and descriptor version.

### Task 7: Exhaustive registry and GUI/wire equivalence gate

**Files:** Create NativeMcpParityTests.cs and machine-readable parity fixture table. Modify complete tool/frontend/API/release docs.

**Interfaces:**
- Consumes Tasks 1 to 6 and R9 GUI command inventory.
- Produces registry inventory mapping all 50 source verbs plus required additions to native registered schemas and GUI actions.
- Each mapping has happy/rejected/undo/redo proof over identical initial documents, comparing result IDs, document/hash, dirty bounds and history labels. File-only operations compare explicit file results instead.

- [ ] Deliver complete parity acceptance and next-available minor release candidate.
- **Tests:** `README50Verbs_AllHaveNativeRegistryAndGuiMapping` refuses missing/duplicate rows. `NativeWireSchemas_PreserveDecimalInt64AndClosedPayloads` includes unknown fields/versions. `GuiMcpParity_AllOperationsHappyRejectUndoRedo` runs one bounded table traversal, not a stress loop. `TransportPipelineCancellationAndErrors_PreserveArrivalOrderAndStdout` uses the production McpBootstrap and transport, stderr logging and clean EOF. `OldAnalyticToolInventory_RemainsUsable` covers all retained verb families.
- **Exit proof:** Release filter `FullyQualifiedName~NativeMcpParityTests` passes with all mapping rows exercised, then one serialized full round build/suite and guards. README/API sweep matches actual registry. Owner tags only after full parity review.

## Execution and planning review

Before execution, refine each task to failing-test, implementation, verification and explicit-path commit steps against landed APIs. Record the chosen signatures, file moves, schema numbers, judgement rulings and refinement commit in Outcome. Run targeted Release tests synchronously per task, then the repository Release build and full non-LiveSocket suite once at the round finish. Run the applicable documentation, dependency and whole-tree guards. Rendering uses the hosted backend workflow and committed family goldens. Integration, packing and tagging belong to the owning orchestrator and owner.

Self-review covered spec requirements, consistent contract names, all five Review Focus tests and task proportion. This task-level plan contains no implementation bodies and does not claim later-round completion.

## Outcome

### Documentation reconciliation, 2026-10-05

- Approval stage: specs approved with revised T4. R10 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: Refine this draft against released prerequisite APIs at round start. Exact signatures, failing assertions and owner plan approval remain open. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: source and planning review only, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: J10.1 adopts R2 J2.1 shared 1-to-64/3x3 smoothing semantics and wire parity assertions. No MCP-only cap or alternate algorithm is permitted.
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
