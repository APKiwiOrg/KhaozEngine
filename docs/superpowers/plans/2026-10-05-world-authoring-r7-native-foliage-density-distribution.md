# World authoring R7: Native foliage density and deterministic distribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Preserve native density bytes and legacy sample identities while supporting deterministic, owner-bound exclusions for free buildings.

**Architecture:** Format 9 persists density payloads independently of procedural scatter and explicit scenery placements. A CPU distributor uses canonical C2 surfaces, C3 shapes, C5 indoor volumes and versioned predicates. Native commands share raster edits and sample previews across the GUI and MCP, with no collision or gameplay objects created by cosmetic foliage.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), approved direction at commit `49b045f75`. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 are owner rulings. T1 to T9 remain subject to owner review.

Task-level plan. Refine to full step level against the landed R1 to R6 APIs before executing, and record the refinement in Outcome.

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
- Consumes C3 oriented collision/selection shapes, C5 shared indoor membership and local/world transform, and C6 explicit marker references.
- Produces legacy-preserving masks for old footprint/roof/doorway/edge/indoor exclusions, transformed with their owner.
- Produces geometry-derived exclusion for new layers through the same sample predicate interface. Cosmetic masks never become reach/collision geometry.

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
