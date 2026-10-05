# World authoring R7: Native foliage density and deterministic distribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Preserve native density bytes and legacy sample identities while supporting deterministic, owner-bound exclusions for free buildings.

**Architecture:** Format 9 persists density payloads independently of procedural scatter and explicit scenery placements. A CPU distributor uses canonical C2 surfaces, C3 shapes, C5 indoor volumes and versioned predicates. Native commands share raster edits and sample previews across the GUI and MCP, with no collision or gameplay objects created by cosmetic foliage.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 and T1 to T9 with revised T4 are approved. This round plan remains pending owner review.

Task-level plan. Refine to full step level against the landed R1 to R6 APIs before executing, and record the refinement in Outcome.

## Approval stage, reconciled 2026-10-05

The controller supplied the exact owner answer "Approve" for both specs, T1 to T9 with revised T4, rigid prefab v1 and the pre-OA9 12 to 18 elapsed-week estimate, and C4 boundary policy. This R7 document remains a draft for refinement at its round. It requires a full plan against owner-released dependencies and then owner plan approval before execution. Spec approval is not acceptance of actual changed targets, distances, occlusion, stances or exact water-boundary samples. R11 records named import acceptance.

The 1 m allowance is a minimum vertical target reach-envelope height, preserving `MinimumObjectReachHeight`. It is not a 1 m action distance. Action range remains existing game policy and physical colliders never expand. Approval IDs are OA4 specs, OA5 revised T4, OA6 prefab/estimate and OA7 water boundary in the game DECISIONS record.

## OA9 checkpoint, R7 round refinement

[DG9.1 to DG9.4](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#dg91-cave-representation-before-r2-approval) extend this draft. R7 remains unapproved. Consume [R2](2026-10-05-world-authoring-r2-authored-terrain-paint.md)'s chosen cave surfaces, [R3](2026-10-05-world-authoring-r3-shared-shapes-headless-builders.md)'s support and [R5](2026-10-05-world-authoring-r5-free-buildings-prefabs-interiors.md)'s underground membership/exclusion ownership.

- **Tasks 1 to 3:** Bind density/predicates/masks to the approved layer/surface/volume context. Vegetation and exclusions on one level must not accidentally sample another floor/ceiling at the same XZ. Surface foliage must not appear underground. Preserve legacy raster bytes, seeds, predicate versions and every generated sample. Water-related exclusions, when applicable, use [R4](2026-10-05-world-authoring-r4-bounded-water-medium.md)'s contained domain.
- **Named future proofs:** `StackedLayerFoliageAndExclusions_RemainInOwnedDomain`, `SurfaceDensity_DoesNotPopulateDeepCave` and `CaveOwnerTransform_InvalidatesOnlyDependentSamples`. Pin selected-model fixtures and bounded affected-tile invalidation before R7 approval, then expose deterministic sample/cache identity to [R8](2026-10-05-world-authoring-r8-native-rendering-residency-captures.md) and [R9](2026-10-05-world-authoring-r9-unified-mapeditor-workflow.md)/[R10](2026-10-05-world-authoring-r10-complete-ke-mapedit-parity.md).
- Current author trees with `LodDistance` 0 stay unchanged until a separate author decision. That source fact is refrozen by [R11](2026-10-05-world-authoring-r11-offline-importer-no-loss-ledger.md), not a new foliage or art edit here.

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

1. A non-square raster at negative world Z must not transpose rows or reverse the positive-Z convention. Task 1 pins byte addresses.
2. Chunk order, reload or an empty density layer must not reroll or duplicate sample IDs. Tasks 2 and 5 test deterministic enumeration.
3. Moving a rotated prefab must move its legacy doorway/roof mask without treating that mask as a physical collider. Task 3 tests ownership.
4. A material cut or paint-only floor override must affect the visible-material predicate at the actual sample point. Tasks 2 and 3 test cuts and exclusions.
5. Invalid bytes/settings or changing a predicate version must refuse silent repair and identify the exact sample differential. Tasks 1, 2 and 4 test failure paths.

## Dependencies and delivered contracts

| Earlier round | Consumed contract |
| --- | --- |
| R1 / C1, transitively | Closed payload versions, stable identities, asset/material closure and normalized hash |
| R2 / C2 | Canonical triangle support, cut/rotation/feather paint, visible material and surface IDs |
| R3 / C3 | Effective oriented shapes, headless spatial query/bounds and support descriptors |
| R5 / C5 | Transformed indoor prisms, stable owner/local-child identity and free prefab transforms |
| R6 / C6 | Generic point/role lookup for explicit exclusion references, with no game habitat policy |

Produces C7 density payload 1, pure format 8 to 9 migration, deterministic CPU distribution, owner-bound legacy masks, geometry-derived exclusions and raster commands. R8 consumes immutable sample sets and their dependency cache identity. R9/R10 use the same brushes/previews. R11 and Grimhollow adoption compare every source byte, generated sample and exclusion decision. worldtrees/worldflora remain explicit C3 placements.

**Name discipline:** `NativeSurface`, `MapWorldBuild`, `NativeAssetDescriptor` and `NativeCommandResult` are provisional names R1 to R4 must confirm. R5/R6 must confirm owner transform and marker lookup signatures. Existing `GroundCoverDistribution` is reused behind a native adapter. New R7 names below are proposed until refinement.

## File Structure

| Action and engine path | Responsibility |
| --- | --- |
| Create `KhaozEngine.MapDoc/Foliage/MapFoliageLayer.cs`, `MapFoliageExclusion.cs`, `MapFoliageValidator.cs` | Closed raster/settings/reference/predicate/mask DTOs |
| Modify `KhaozEngine.MapDoc/MapDocument.cs`, `mapdoc.schema.json`, `MapDocumentFile.cs`, `MapDocumentValidator.cs`, `MapDocumentHash.cs` | Native layer root, migration and closure identity |
| Create `KhaozEngine.MapDoc.Physics/Foliage/MapFoliageSurface.cs`, `MapFoliageDistribution.cs`, `MapFoliageCacheKey.cs` | CPU adapter over canonical surface/shape/volume inputs |
| Modify `KhaozEngine.Terrain/GroundCoverDistribution.cs` only if its seam needs additive input support | Preserve existing consumers and legacy seeded algorithm |
| Create `KhaozEngine.MapEditor/Commands/FoliageCommands.cs` in the landed shared command home | Complete raster replacement and metre brush |
| Create `KhaozEngine.MapEdit.Tool/MutationServiceFoliage.cs`, `Tools/FoliageTools.cs` | Thin native foliage verbs |
| Create `KhaozEngine.MapEditor.Tests/MapDoc/MapFoliagePayloadTests.cs`, `MapEditor/FoliageCommandTests.cs`, `MapEditTool/FoliageToolTests.cs` | Payload and command proofs |
| Create `KhaozEngine.MapDoc.Physics.Tests/Foliage/MapFoliageDistributionTests.cs`, `MapFoliageExclusionTests.cs`, `NativeFoliageAcceptanceTests.cs` | CPU distribution/exclusion/round proofs in the R3 test home, path confirmed at refinement |
| Modify `KhaozEngine.Render.Tests/Terrain/GroundCoverDistributionTests.cs` only for shared distributor conformance | Existing distribution stays unchanged |
| Modify affected READMEs, API/dependency docs and release declarations | Predicate/version/cosmetic ownership and release contract |

## Judgement and refinement record

- J7.1. The native density payload declares positive-world-Z row direction, world origin, spacing and exact byte encoding. No implicit legacy tile-row conversion is allowed in raster editing.
- J7.2. Imported predicate identity includes its original settings and explicit owner-bound exclusion masks. Geometry-derived exclusion is a separate selectable version for new layers. A version change produces a keyed added/removed/changed sample differential before acceptance.
- J7.3. Seed/sample keys are functions of declared layer/sample coordinates and algorithm version, independent of chunk traversal. Cache keys also include effective owner transforms and every paint/shape/volume/mask input.
- Do not copy TileFoliageSurface into a native runtime dependency. R11's optional source reader evaluates its legacy semantics offline. The native adapter implements generic equivalent predicates and is tested against an immutable reference corpus.

---

### Task 1: Exact raster payload and migration

**Files:** Create MapFoliageLayer.cs/MapFoliageExclusion.cs/MapFoliageValidator.cs. Modify document/schema/file/validator/hash and MapFoliagePayloadTests.cs.

**Interfaces:**
- Consumes C1 version/hash/closure and C2 surface/material references.
- Produces density payload version 1 with ID, origin, row direction, spacing, width/depth, byte raster, seed, distribution settings, asset/material references, exclusion rules and explicit predicate version.
- Produces pure format 8 to 9 migration with absent native layers empty, preserving analytic scatter/companion layers.
- Mask references bind to a placement or prefab-local child owner, without adding physics shapes.

- [ ] Deliver exact raster persistence and complete settings/reference validation.
- **Tests:** `DensityRaster_NonSquareNegativeZRoundTripsPositiveRows` compares every address. `DensityPayload_RejectsLengthRangeNonfiniteAndDanglingReferences` includes width*height overflow, zero spacing, bad byte values and future versions. `Format8To9_DoesNotConvertOrRerollAnalyticScatter` preserves legacy analytic behavior. `DensityBytesAndPredicateVersion_ChangeClosureHash` checks every dependency.
- **Exit proof:** Release filter `FullyQualifiedName~MapFoliagePayloadTests` passes. Byte arrays, seed and settings roundtrip exactly.

### Task 2: Canonical CPU distribution and sample identity

**Files:** Create MapFoliageSurface.cs/MapFoliageDistribution.cs/MapFoliageCacheKey.cs and MapFoliageDistributionTests.cs. Extend GroundCoverDistribution only where the native seam requires it.

**Interfaces:**
- Consumes C2 triangle floor, normal and visible-material query plus C7 raster/settings/version.
- Produces immutable sample records with stable sample key, world position, orientation, scale, selected asset/material and predicate decision.
- Produces a cache identity covering density/surface/paint/collider/volume/mask/transform and distributor version. No GPU or TileWorld reference is introduced.
- Version transition compares old/new records by sample key before a command commits it.

- [ ] Deliver deterministic native samples preserving imported predicate parameters.
- **Tests:** `SampleSet_IsIndependentOfChunkEnumerationAndReload` compares complete ordered records. `EmptyRaster_ReturnsStableEmptySet` covers zero density. `PaintCutRotation_UsesVisibleMaterialAtSample` includes all four cuts and quarter-turn paint. `PredicateVersionChange_ReportsAddedRemovedAndChangedSamples` prevents silent reroll. `CacheIdentity_ChangesForEveryDistributionInput` checks terrain, mask, material and owner transform changes.
- **Exit proof:** Release filter `FullyQualifiedName~MapFoliageDistributionTests` and existing GroundCoverDistributionTests pass synchronously. No sample depends on rendering or storage traversal.

### Task 3: Transformed exclusion semantics

**Files:** Modify MapFoliageSurface.cs and MapFoliageExclusion.cs. Create MapFoliageExclusionTests.cs.

**Interfaces:**
- Consumes C3 physical collision shapes for physical exclusions and the separately versioned canonical interaction envelope only for explicit interaction diagnostics, C5 shared indoor membership and local/world transform, and C6 explicit marker references.
- Produces legacy-preserving masks for old footprint/roof/doorway/edge/indoor exclusions, transformed with their owner.
- Produces geometry-derived exclusion for new layers through the same sample predicate interface. Cosmetic masks never become interaction envelopes or physical collision. Minimum vertical reach height never expands solid foliage exclusion by accident.

- [ ] Deliver material/indoor/solid/door predicates and owner-bound mask resolution.
- **Tests:** `RotatedScaledBuilding_MovesLegacyExclusionAndDoorClearance` checks yaw 0.371 and scales 0.8/1.2. `LegacyFootprintMask_DiffersFromColliderWithoutChangingPhysics` proves authored distribution ownership. `FloorPaintAndIndoorBoundary_ProduceExpectedExclusion` shares canonical material/membership queries. `RemovedMaskOwner_RefusesBuildInsteadOfDroppingMask` covers orphaning. `DoorMarkerReference_UsesGenericPointWithoutHabitatPolicy` proves no game roles in the engine.
- **Exit proof:** Release filter `FullyQualifiedName~MapFoliageExclusionTests` passes. A sample decision trace names the contributing surface/material/shape/volume/mask keys.

### Task 4: Shared density commands and preview

**Files:** Create FoliageCommands.cs, MutationServiceFoliage.cs, FoliageTools.cs, FoliageCommandTests.cs and FoliageToolTests.cs.

**Interfaces:**
- Consumes C9 shared command result/history and Tasks 1 to 3 distribution inputs.
- Produces `foliage_layer_set`, `foliage_get`, `foliage_density_set`, `foliage_paint`, `foliage_remove`.
- Circular brush uses world metres, byte density 0 to 255 and hardness 0 to 1. GUI/MCP preview calls the same distributor. Layer get returns detached content in authoring order.

- [ ] Deliver one undo step per raster/layer/brush mutation with complete invalidation.
- **Tests:** `BrushHardnessZeroAndOne_UsePositiveZRows` checks soft and hard footprints at negative Z. `RasterReplaceOrInvalidBrush_IsAtomic` compares document/hash/history and untouched cells. `LayerGet_IsDetachedAndPreservesAuthoringOrder` prevents mutation leaks. `UndoRedo_RestoresExactRasterSeedAndSampleSet` checks stable redo. `DensityEdit_ChangesNoGameplayPlacementOrStatic` guards cosmetic ownership.
- **Exit proof:** Release filters `FullyQualifiedName~FoliageCommandTests|FullyQualifiedName~FoliageToolTests` pass. A tool edit and direct GUI command produce identical raster/hash/dirty bounds.

### Task 5: Frozen distribution differential and consumer contract

**Files:** Create NativeFoliageAcceptanceTests.cs and immutable raster/sample/decision fixtures. Modify live docs and round release declarations.

**Interfaces:**
- Consumes Tasks 1 to 4 and a pinned reference sample inventory/settings, prepared without a native TileWorld runtime dependency.
- Produces all 25,921 frozen 161x161 density-byte comparisons, every generated sample/decision comparison and documented R8 upload/cache contract.
- Explicit worldtrees/worldflora C3 placements remain separately accounted.

- [ ] Deliver exhaustive foliage preservation proof and next-available minor release candidate.
- **Tests:** `FrozenDensity_All25921BytesPreserved` checks source-key equality. `FrozenGeneratedSamples_AllIdentitiesPosesAndDecisionsMatch` includes doorway and roof cases. `NativeFoliage_SaveReloadNoRerollOrGameplayTargets` checks monolithic/tiled storage and CPU-only build. `ReferenceCorpus_DetectsOneByteOrOneDecisionLoss` proves the gate catches sparse loss.
- **Exit proof:** Release filter `FullyQualifiedName~NativeFoliageAcceptanceTests` passes, then one serialized full round build/suite and guards. Preserve shared distributor regressions. Publish predicate upgrade procedure and CPU cache contract, with owner-only tagging.

## Execution and planning review

Before execution, refine each task to failing-test, implementation, verification and explicit-path commit steps against landed APIs. Record the chosen signatures, file moves, schema numbers, judgement rulings and refinement commit in Outcome. Run targeted Release tests synchronously per task, then the repository Release build and full non-LiveSocket suite once at the round finish. Run the applicable documentation, dependency and whole-tree guards. Rendering uses the hosted backend workflow and committed family goldens. Integration, packing and tagging belong to the owning orchestrator and owner.

Self-review covered spec requirements, consistent contract names, all five Review Focus tests and task proportion. This task-level plan contains no implementation bodies and does not claim later-round completion.

## Outcome

### OA9 documentation outcome, 2026-10-05

- Recorded [OA9](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md#owner-rulings-binding-direction) and this plan's [checkpoint](#oa9-checkpoint-r7-round-refinement). Existing source-count fixtures and prior guard results below remain historical.
- R7 remains unapproved. Its checkpoint names pending choices, owning tasks/dependencies and future proofs, to be refined before owner round approval. No capability, art, swimming, world enlargement or fresh benchmark is claimed.
- This revision requires serial doc guards and explicit-path commit. The worker stops at the docs commit for controller verification/push, with no builds/tests/format/pack or integration.

### Historical documentation reconciliation before OA8/OA9, 2026-10-05

- Approval stage: specs approved with revised T4. R7 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: Refine this draft against released prerequisite APIs at round start. Exact signatures, failing assertions and owner plan approval remain open. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: source and planning review only, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: Physical foliage exclusions and owner-bound masks stay separate from revised-T4 interaction envelopes. The vertical reach allowance cannot silently change foliage exclusion geometry.
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
