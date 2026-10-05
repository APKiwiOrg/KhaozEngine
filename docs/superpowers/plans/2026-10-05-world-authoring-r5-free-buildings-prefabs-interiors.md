# World authoring R5: Free buildings, prefabs and interior volumes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Author reusable rigid buildings with free transforms, stable child identities, local floors and explicit interior/roof relationships.

**Architecture:** Extend the native resolver with prefab payload 1 and persisted child bindings. Compose one local-to-world transform for every child resource, then feed one headless volume service to indoor state and roof policy. R5 publishes roof visibility/shadow intent, while R8 supplies its native rendering integration.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 and T1 to T9 with revised T4 are approved. This round plan remains pending owner review.

Task-level plan. Refine to full step level against the landed R1 to R4 APIs before executing, and record the refinement in Outcome.

## Approval stage, reconciled 2026-10-05

The controller supplied the exact owner answer "Approve" for both specs, T1 to T9 with revised T4, rigid prefab v1 and the 12 to 18 elapsed-week estimate, and C4 boundary policy. This R5 document remains a draft for refinement at its round. It requires a full plan against owner-released dependencies and then owner plan approval before execution. Spec approval is not acceptance of actual changed targets, distances, occlusion, stances or exact water-boundary samples. R11 records named import acceptance.

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

1. Removing/re-adding a prefab child or undoing an override must not recycle its numeric identity. Task 2 tests tombstones and redo.
2. A rotated/scaled paint patch on uneven terrain must not become a second support floor. Task 3 tests ownership and canonical triangles.
3. An observer on a prism edge or shared storey boundary must produce the same membership on both heads. Task 4 pins boundary behavior.
4. Repeated prefab asset edits must invalidate every instance without re-expanding children twice. Tasks 2 and 5 test closure and invalidation.
5. Unpack, partial extraction or a cancelled local edit must preserve child references, local water and world coordinates. Task 5 tests atomic commands.

## Dependencies and delivered contracts

| Earlier round | Consumed contract |
| --- | --- |
| R1 / C1 | Render-free native asset DTO, immutable resolver, closure validation/hash, positive int64 allocator/tombstones and stable placement identity |
| R2 / C2 | Named canonical support surfaces, paint-only material patches, triangle compiler, connected indoor masks and surface commands |
| R3 / C3 | MapDoc.Physics descriptor build, oriented compound/baked shapes, pick/reach/stance service, support surfaces and headless bounds |
| R4 / C4 | Stable bounded-water records, half-open domains, explicit SurfaceY/medium and commands |

Produces C5 prefab payload 1, format 6 to 7 semantic migration, persisted instance-child bindings, effective child resource expansion, volume membership, roof/shadow policy and prefab commands. R6 consumes local marker ownership. R7 consumes owner-bound exclusions and indoor volumes. R8 consumes effective bounds, render/shadow intent and lights. R9/R10 expose the same commands. R11 and Grimhollow adoption consume reusable definitions, actual-instance overrides and stable original leaf IDs.

**Name discipline:** `NativeAssetDescriptor`, `ResolvedMapWorld`, `NativeSurface`, `MapWorldBuild`, `NativeCommandResult` and `NativeWaterBody` are provisional responsibility names that R1 to R4 must confirm. Baseline `MapDocument`, `MapDocumentFile`, `EditorDocument` and `MapEditSession` already exist. New R5 names below are proposed contracts, finalized during refinement without changing their semantics.

## File Structure

| Action and engine path | Responsibility |
| --- | --- |
| Modify `KhaozEngine.MapDoc/MapDocument.cs`, `mapdoc.schema.json`, `MapDocumentFile.cs`, `MapDocumentValidator.cs`, `MapDocumentHash.cs` | Root references, sequential schema transition, closure and hash integration |
| Create `KhaozEngine.MapDoc/Prefabs/MapPrefabAsset.cs`, `MapPrefabInstance.cs`, `MapPrefabOverride.cs`, `MapPrefabBindings.cs`, `MapPrefabFile.cs`, `MapPrefabValidator.cs` | Version 1 payload, persistence and validation |
| Create `KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs`, `MapPrefabTransform.cs` | Stable expansion and one transform composition |
| Create `KhaozEngine.MapDoc/Interiors/MapInteriorVolume.cs`, `MapInteriorMembership.cs`, `MapRoofPolicy.cs` | Prism/storey records, membership and visibility intent |
| Modify R2 surface resolver and R3 `KhaozEngine.MapDoc.Physics` builder files, paths confirmed at refinement | Local support/paint, shapes/lights/water and volume descriptors |
| Create `KhaozEngine.MapEditor/Commands/PrefabCommands.cs`, `InteriorCommands.cs` | Use the landed shared command home if R1 to R4 extracted it from MapEditor |
| Create `KhaozEngine.MapEdit.Tool/MutationServicePrefabs.cs`, `Tools/PrefabTools.cs`, `Tools/InteriorTools.cs` | Thin author-time API over those commands |
| Create `KhaozEngine.MapEditor.Tests/MapDoc/MapPrefabSchemaTests.cs`, `MapPrefabResolverTests.cs`, `MapPrefabSurfaceTests.cs`, `MapInteriorMembershipTests.cs` | CPU payload/resolution proofs |
| Create `KhaozEngine.MapEditor.Tests/MapEditor/PrefabCommandTests.cs`, `MapEditTool/PrefabToolTests.cs`, `MapDoc/NativeBuildingAcceptanceTests.cs` | Command and round assembly proofs |
| Create `KhaozEngine.MapEditor.Tests/Fixtures/NativeBuildings/` fixture files and membership manifest | Native reference counterparts to seven definitions/nine actual interiors, not a second importer |
| Modify affected package READMEs, `docs/USING-KHAOZENGINE.md`, `docs/INDEX.md`, `docs/DEPENDENCY-SEAMS.md` and release declarations | Live contracts and round release evidence |

## Judgement and refinement record

- Reconciliation 2026-10-05, OA6. Rigid free transforms, stable children, overrides, optional snapping, local floors/volumes and optional local C4 water are approved. No nested prefabs, generated stairs/foundations or CAD. The approved estimate is 12 to 18 elapsed weeks, with re-estimation after R2/R5, not a delivery promise. Optional local water uses the same domain/medium/version identity and one transform, with no second algorithm. Plan refinement and owner round approval remain pending.

- Historical J5.1, superseded by OA6 on 2026-10-05. C5 lists placements, floors/paint, lights, markers and volumes in prefab payload 1, but its editor contract also requires local water editing. Plan optional C4 water-body records keyed locally, with the same one-transform expansion and no new water algorithm. That was the pre-approval proposal. OA6 now explicitly approves optional prefab-local water using C4 and resolves the omission. Retain this origin as historical evidence, not an open scope question.
- J5.2, boundary policy. Propose polygon-edge inclusion and half-open vertical spans [lowerY, upperY), returning all occupied volume keys in stable order. A shared-storey boundary belongs to the upper prism. Auto uses the linked roof set above those occupied volumes. Confirm against R2 indoor-mask compilation and owner-reviewed roof behavior at refinement.
- Local marker payloads carry the eventual C6 fields in R5, but R6 owns global registry/projection behavior. This is an additive skeleton, not a claim of complete marker tooling.
- Native acceptance fixtures preserve checked source membership. R5 does not implement the R11 TileWorld importer or touch game files.

---

### Task 1: Versioned prefab and interior payloads

**Files:** Create the Prefabs DTO/file/validator files and MapInteriorVolume.cs. Modify root schema, file migrations, document validator/hash and MapPrefabSchemaTests.cs.

**Interfaces:**
- Consumes C1 asset references/closure and C2 surface DTOs, names R1/R2 must confirm.
- Produces `MapPrefabAsset` payload version 1 with stable local child keys, placements, floor/support roles, paint, lights, marker records, interior volumes and J5.1 local water.
- Produces `MapPrefabInstance` with free XYZ/yaw/positive uniform scale, digest-bearing asset reference, keyed overrides and persisted child bindings. Overrides cover transform/tags/asset variants/additions/removals/paint/volume/roof membership.
- Produces pure format 6 to 7 migration. No nested prefab references in v1.

- [ ] Deliver closed, immutable-at-resolution prefab data with complete reference validation and persistence.
- **Tests:** `PrefabV1_RoundTripsEveryResourceAndOverride` preserves ordered tags and keys. `Format6To7_PreservesAnalyticExecution` adds no fabricated instances. `PrefabValidation_RejectsNestingCyclesDanglingLinksAndDuplicateBindings` refuses every malformed case before build. `PrefabClosure_EditChangesIdentity` includes asset children and versions in identity.
- **Exit proof:** Synchronous Release filter `FullyQualifiedName~MapPrefabSchemaTests` in MapEditor.Tests passes. Save/reload and future-payload refusal are proved without a GPU.

### Task 2: Stable expansion and free transform composition

**Files:** Create MapPrefabResolver.cs/MapPrefabTransform.cs. Modify R1 resolver/allocator/hash integration and MapPrefabResolverTests.cs.

**Interfaces:**
- Consumes C1 allocator, tombstones and immutable resolved placement records, names R1 must confirm.
- Produces resolver expansion with parent identity distinct from leaf placement ID, numeric game ID and Kind. Persist `(instance ID, child key) -> (placement ID, optional numeric ID)`.
- Produces one composed effective transform for shapes, meshes, support/paint, lights, volumes, markers, water and exclusion ownership. Removed original leaves retain tombstones.
- Asset invalidation expands referring instances once, preserving live bindings and deterministic ordering.

- [ ] Deliver transform-correct prefab resolution and override/add/remove identity behavior.
- **Tests:** `FreeBuilding_ComposesAllChildrenOnce` checks yaw 0.371, XZ offset (0.23,0.17), scales 0.8 and 1.2 and explicit Y. `ChildBinding_UndoRedoAndDeleteNeverReallocate` proves stable IDs, tombstones and exhaustion refusal. `TwoInstancesOfEditedAsset_InvalidateWithoutDuplicateChildren` preserves bindings after closure changes. `OverrideOrder_DoesNotChangeResolvedIdentity` checks canonical keyed resolution. `PrefabLocalWater_MoveScalePreservesLevelDomainAndMedium` pins J5.1.
- **Exit proof:** Release filter `FullyQualifiedName~MapPrefabResolverTests` passes. Independently resolved headless instances have identical leaf identities, transforms and closure hashes.

### Task 3: Local support floors, paint and doorway geometry

**Files:** Modify landed R2 surface compiler/resolver and R3 MapDoc.Physics builder. Create MapPrefabSurfaceTests.cs.

**Interfaces:**
- Consumes C2 canonical triangles and explicit support-versus-paint roles, names R2 must confirm.
- Consumes C3 compound/baked shape and support descriptors, names R3 must confirm.
- Produces transformed rigid elevated floors or canonical-terrain material patches with exactly one support owner. Prefab floor belongs to its volume.
- Produces doorway/corner geometry preserving openings under arbitrary free transforms. Uneven seating reports a diagnostic.

- [ ] Deliver local surface and collision composition with no world-cell repaint or generated foundation.
- **Tests:** `PaintOnlyPatch_OnSlopeDoesNotAddSupport` compares movement/render/capture triangle descriptors. `ElevatedLocalFloor_HasOneOwnerAndVolume` rejects duplicate-owned support. `RotatedScaledCottageDoor_RemainsTraversable` checks the same shape query/nav capture at scales 0.8 and 1.2. `PrefabMove_DoesNotRepaintWorldCells` preserves world material arrays. `UnevenSeating_ReportsWithoutChangingAuthoredGeometry` prevents automatic leveling.
- **Exit proof:** Release filter `FullyQualifiedName~MapPrefabSurfaceTests` plus the landed R3 doorway conformance tests passes. Floor agreement comes from shared descriptors rather than numeric sampler approximations.

### Task 4: Shared indoor membership and roof policy

**Files:** Create MapInteriorMembership.cs/MapRoofPolicy.cs. Modify indoor-mask compilation, headless builder descriptors and MapInteriorMembershipTests.cs.

**Interfaces:**
- Consumes C2 connected Indoor masks with explicit vertical span and C5 transformed prism/storey/roof keys.
- Produces membership lookup at world XYZ, stable occupied-volume keys, linked roof visibility and shadow-caster intent for Auto/AlwaysVisible/AlwaysHidden.
- Produces the same membership result for indoor lighting and roof-aware camera blockers. Physical statics are unaffected by roof view mode.

- [ ] Deliver shared volume semantics and roof intent, with rendering adapter hookup reserved for R8.
- **Tests:** `PrismBoundary_AndStoreyEdgeAgreeOnBothHeads` pins J5.2. `AutoRoof_HidesOnlyLinkedRoofsAboveOccupiedVolume` covers adjacent buildings and multi-storey observers. `RoofViewModes_AlwaysVisibleAndAlwaysHiddenKeepShadowPolicy` pins both explicit modes. `HiddenRoof_RemainsShadowCaster` verifies intent. `OpenShelter_HasNoIndoorVolumeAndStaysVisible` covers pasture shelter. `ImportedIndoorMask_MatchesVolumeFootprint` checks exact coverage and rejects invalid/self-intersecting prisms and dangling links.
- **Exit proof:** Release filter `FullyQualifiedName~MapInteriorMembershipTests` passes. Both headless heads return identical membership, roof, camera-blocker and lighting inputs.

### Task 5: Atomic prefab commands and local editing session

**Files:** Create PrefabCommands.cs/InteriorCommands.cs in the landed command home, MutationServicePrefabs.cs, PrefabTools.cs/InteriorTools.cs, PrefabCommandTests.cs and PrefabToolTests.cs.

**Interfaces:**
- Consumes C9 command transaction/result and C1 identity allocation, names R1 to R4 must confirm.
- Produces `prefab_open/edit/override/unpack`, `prefab_save/place/list`, `interior_volume_add/set/remove` and `roof_link_set`.
- Local session commands reuse native placement/surface/water/marker DTOs. Extract preserves local keys and complete selected reference closure. Unpack preserves effective leaf IDs/transforms. Optional local snap is an authoring preference only.
- Asset-save invalidation reports every affected instance and old/new dirty bounds. File-only prefab_save does not invent a world undo entry.

- [ ] Deliver headless prefab editing and thin tool adapters, without claiming R9 GUI workflow completion.
- **Tests:** `PrefabExtract_RefusesPartialVolumeOrDanglingRoofSelection` requires explicit valid selection closure. `Unpack_PreservesWorldTransformsReferencesAndLeafIDs` checks local resources too. `LocalEditCancel_WritesNothing` checks history/hash/files. `PrefabAssetEdit_InvalidatesEveryReferringInstance` reports dirty bounds. `PrefabPlace_UndoRedoRestoresAllocation` fixes legacy stamp redo churn. `LocalWaterCommand_UsesNativeWaterTransaction` covers J5.1.
- **Exit proof:** Release filters `FullyQualifiedName~PrefabCommandTests|FullyQualifiedName~PrefabToolTests` pass. Invalid edits leave document, file closure, identity and history unchanged.

### Task 6: Native building assembly proof and released contract

**Files:** Create NativeBuildings fixtures/manifest and NativeBuildingAcceptanceTests.cs. Modify live package/API/dependency docs and round version/changelog declarations.

**Interfaces:**
- Consumes Tasks 1 to 5 plus R1 to R4 contracts.
- Produces a native fixture inventory containing all seven reusable definitions and nine checked actual interiors, explicit actual-instance differences, higher roofs and original leaf bindings.
- Produces documented C5 consumption for R6 to R11 and Grimhollow, independently usable without TileWorld.

- [ ] Deliver the round assembly proof, consumer examples and next-available minor release candidate.
- **Tests:** `SevenDefinitionsNineInteriors_MatchCheckedMembership` preserves original leaves once, crafting-hall differences, furniture/lights/paint and higher roof transforms. `CottageAndBank_DoorRoutesSurviveFreePlacementReload` proves open apertures and stable IDs. `NativeBuildingClosure_BootsWithoutTileWorld` audits runtime references and packaged closure. Run existing analytic/prefab command regressions unchanged.
- **Exit proof:** Release filter `FullyQualifiedName~NativeBuildingAcceptanceTests` passes, then one serialized full round build/suite and guards. R8 still owes actual shadow pixels. OA6 has approved the v1 scope. Actual workflow acceptance and owner-authorized tagging remain future gates, with no automatic tag.

## Execution and planning review

Before execution, refine each task to failing-test, implementation, verification and explicit-path commit steps against landed APIs. Record the chosen signatures, file moves, schema numbers, judgement rulings and refinement commit in Outcome. Run targeted Release tests synchronously per task, then the repository Release build and full non-LiveSocket suite once at the round finish. Run the applicable documentation, dependency and whole-tree guards. Rendering uses the hosted backend workflow and committed family goldens. Integration, packing and tagging belong to the owning orchestrator and owner.

Self-review covered spec requirements, consistent contract names, all five Review Focus tests and task proportion. This task-level plan contains no implementation bodies and does not claim later-round completion.

## Outcome

### Documentation reconciliation, 2026-10-05

- Approval stage: specs approved with revised T4. R5 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: Refine this draft against released prerequisite APIs at round start. Exact signatures, failing assertions and owner plan approval remain open. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: source and planning review only, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: OA6 resolves J5.1 with optional prefab-local C4 water. Rigid free transforms, stable children, overrides, optional snapping, local floors/volumes and the estimate are approved. The prior proposal is retained as historical evidence.
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
