# World authoring R6: General markers and spawn projections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Provide one native point registry for landmarks, player/NPC spawn projections and prefab-local markers with exact saved heights.

**Architecture:** Format 8 integrates the R5 local-marker payload into a world registry with stable IDs, free XYZ/yaw, names, roles, enabled state and ordered tags. The resolver provides generic points and tagged shapes, while consumer role validation and habitat/spawn policy remain outside MapDoc. Legacy typed lists project losslessly through this registry.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), approved direction at commit `49b045f75`. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 are owner rulings. T1 to T9 remain subject to owner review.

Task-level plan. Refine to full step level against the landed R1 to R5 APIs before executing, and record the refinement in Outcome.

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

1. Duplicate marker names across two prefab instances must refuse rather than overwrite a landmark. Task 2 tests global name conflicts.
2. An explicit elevated Y must survive terrain changes and old-spawn migration without re-snapping. Tasks 1 and 2 test height mode.
3. Disabled spawn records must survive every reader, summary and edit. Task 2 tests filtered versus exhaustive views.
4. A stale owner/local key after prefab override or unpack must not silently orphan a marker. Tasks 2 and 3 test ownership.
5. A role string unknown to a game must yield a consumer finding, without coupling the engine to cow/duck policy. Task 2 tests the role registry.

## Dependencies and delivered contracts

| Earlier round | Consumed contract |
| --- | --- |
| R1 / C1 | Stable identity, closed schema/payload versions, resolver, closure/hash and storage |
| R2 / C2 | Canonical support sampling and migration access to the old computed spawn height |
| R5 / C5 | Local marker records, instance transforms, stable child keys/bindings and unpack/override ownership |

Produces C6 world marker registry, pure format 7 to 8 migration, generic role/name lookup, typed player/NPC projections and shared marker/spawn commands. R7 can use explicit marker-associated doorway exclusions without game policy. R8 includes marker residency. R9/R10 expose point/role controls. R11 and Grimhollow adoption obtain all 38 frozen-fixture points and the accepted world's refreshed inventory.

**Name discipline:** `ResolvedMapWorld`, `NativeSurface`, `NativeCommandResult` and canonical support-query names are provisional names R1/R2 must confirm. R5 must confirm MapPrefabAsset/Instance marker ownership and effective transform access. Baseline MapSpawn/MapPlayerSpawn and MapRuntime exist. New contracts below are proposed until refinement.

## File Structure

| Action and engine path | Responsibility |
| --- | --- |
| Create `KhaozEngine.MapDoc/Markers/MapMarker.cs`, `MapMarkerRegistry.cs`, `MapMarkerProjection.cs`, `MapMarkerValidator.cs` | General DTO, identity/name lookup, typed projections and consumer role findings |
| Modify `KhaozEngine.MapDoc/MapDocument.cs`, `mapdoc.schema.json`, `MapDocumentFile.cs`, `MapDocumentValidator.cs`, `MapDocumentHash.cs`, `MapRuntime.cs` | Root registry, migration, hash and compatibility readers |
| Modify R5 prefab resolver/local marker files | Registry integration, owner transform and reference propagation |
| Create `KhaozEngine.MapEditor/Commands/MarkerCommands.cs` in the landed shared command home | Undoable registry/spawn edits |
| Create `KhaozEngine.MapEdit.Tool/MutationServiceMarkers.cs`, `Tools/MarkerTools.cs` | marker_set/remove/list and spawn adapters |
| Modify existing spawn adapters in `KhaozEngine.MapEdit.Tool/Tools/MutationTools.cs` | Preserve old verb surfaces using the registry |
| Create `KhaozEngine.MapEditor.Tests/MapDoc/MapMarkerMigrationTests.cs`, `MapMarkerRegistryTests.cs`, `NativeMarkerAcceptanceTests.cs`, `MapEditor/MarkerCommandTests.cs`, `MapEditTool/MarkerToolTests.cs` | Migration/runtime/command/fixture proofs |
| Modify MapDoc, MapEditor and MapEdit.Tool READMEs and live API/release docs | Point/height/role ownership and round release |

## Judgement and refinement record

- J6.1. Marker ID is immutable identity, name is editable lookup metadata. Imported names must be globally unique. Prefab instances require explicit resolved-name overrides when duplicate local names would collide, no silent suffixing.
- J6.2. Persist explicit height or an explicit support-snap policy. Migration computes legacy Y once with the pre-migration source semantics and stores explicit Y. A saved explicit height never inherits a new canonical sampler.
- J6.3. Consumer role registry is injected validation, not a list of Grimhollow constants. Engine returns generic role/tagged-point data. NPC archetype payload must be retained in typed projection metadata.
- R5 local markers are promoted rather than copied into a second independently editable registry.

---

### Task 1: Registry schema and lossless spawn migration

**Files:** Create MapMarker.cs/MapMarkerProjection.cs/MapMarkerValidator.cs. Modify schema/document/file/hash and MapMarkerMigrationTests.cs.

**Interfaces:**
- Consumes C1 migration/identity/closure and legacy MapSpawn/MapPlayerSpawn data, old height computation from R2.
- Produces `MapMarker` with stable ID/name, world or local XYZ/yaw, role, enabled, ordered tags, optional prefab owner/key and typed spawn archetype metadata.
- Produces pure format 7 to 8 migration and lossless player/NPC projection readers. Numeric game placement IDs are not required.
- Explicit support mode versus explicit Y is serializable and validated.

- [ ] Deliver closed marker schema and old-map migration retaining every typed value.
- **Tests:** `LegacySpawnMigration_PreservesComputedYRoleArchetypeAndTags` uses a slope where old and canonical sampling differ. `ExplicitElevatedMarker_RoundTripsWithoutSnap` checks sub-metre XYZ/yaw and height mode. `MarkerPayload_RejectsFutureVersionAndInvalidFiniteValues` fails before build. `AnalyticOldSpawnReaders_KeepExistingBehavior` covers unchanged analytic execution.
- **Exit proof:** Release filter `FullyQualifiedName~MapMarkerMigrationTests` passes. Every legacy field has a native or typed-projection destination.

### Task 2: Deterministic lookup, projection and prefab resolution

**Files:** Create MapMarkerRegistry.cs. Modify MapRuntime and R5 resolver. Create MapMarkerRegistryTests.cs.

**Interfaces:**
- Consumes immutable R1 resolution, R2 support query and R5 local-to-world transform.
- Produces exhaustive ID/name/role lookup and explicitly filtered enabled spawn views over the same registry.
- Produces generic tagged shapes/points for consumer habitat interpretation, with injected role validation. Marker points produce no render/collision geometry.
- Local marker world point is computed once from its owner. Override/removal/unpack propagates references.

- [ ] Deliver lossless registry readers and shared local/world resolution.
- **Tests:** `DuplicateNamesAcrossPrefabInstances_RefuseBeforeBuild` includes a world landmark conflict. `DisabledSpawn_RemainsInExhaustiveRegistry` distinguishes enabled selection. `UnknownConsumerRole_ReturnsFindingWithoutGameDependency` proves validation injection. `MovedScaledPrefab_TransformsMarkerExactlyOnce` checks explicit Y and yaw. `DanglingOwnerOrChildKey_RefusesRegistry` rejects removed owners. `Landmark_IsPointNotRegionDisc` proves no fake geometry or gameplay blocking.
- **Exit proof:** Release filter `FullyQualifiedName~MapMarkerRegistryTests` passes. Both heads return identical complete and projected marker tables.

### Task 3: Shared marker and compatibility spawn commands

**Files:** Create MarkerCommands.cs, MutationServiceMarkers.cs, MarkerTools.cs, MarkerCommandTests.cs/MarkerToolTests.cs. Modify existing spawn adapters.

**Interfaces:**
- Consumes C9 NativeCommandResult, name/identity validation and R5 ownership edits, names earlier rounds must confirm.
- Produces `marker_set(name, XYZ, yaw, role, enabled, tags, height policy, ownership)`, `marker_remove`, `marker_list` semantics over stable identity.
- Existing spawn_add/move/set_enabled/rename/remove and player_spawn controls project to these commands, with preserved legacy signatures for old analytic maps.
- Rename checks name uniqueness without changing marker ID. Remove checks references. Undo restores tags, ownership and exact point.

- [ ] Deliver one undoable marker/spawn mutation path and thin MCP adapters.
- **Tests:** `MarkerSetExistingName_PreservesIDAndOrderedTags` checks upsert semantics. `RenameConflict_WritesNothing` compares hash/history/dirty bounds. `PrefabUnpack_PreservesMarkerWorldPointAndReferences` removes local ownership deliberately. `MarkerUndoRedo_RestoresSameIDAndEnabledState` checks exhaustive summary. `SpawnVerbAndMarkerCommand_ProduceSameDocument` verifies compatibility projections.
- **Exit proof:** Release filters `FullyQualifiedName~MarkerCommandTests|FullyQualifiedName~MarkerToolTests` pass. Rejected edits change no document, identity or history.

### Task 4: Complete fixture inventory and published consumer contract

**Files:** Create NativeMarkerAcceptanceTests.cs and marker fixture rows in the native fixture corpus. Modify affected README/API/release docs.

**Interfaces:**
- Consumes Tasks 1 to 3 and the checked frozen source marker inventory.
- Produces complete fixture table with 32 NPC, one player and five landmarks, all 38 exact roles/tags/enabled/world points. Actual accepted-world counts are refreshed in R11.
- Publishes C6 for R7 to R11 without cow pen, duck habitat or spawn selection logic.

- [ ] Deliver the round marker proof and next-available minor release candidate.
- **Tests:** `Frozen38Markers_PreserveEveryPointRoleAndTag` compares each key rather than totals alone. `MarkerRegistry_SaveReloadBothStorageFormsIsEqual` includes local/elevated/disabled points. `NativeMarkerConsumers_LoadWithoutTileWorldOrGameTypes` checks package closure. Run existing MapDocumentWindowingSpawnTests/MapWindowSpawnSearchTests unchanged.
- **Exit proof:** Release filter `FullyQualifiedName~NativeMarkerAcceptanceTests` passes, then one serialized full round build/suite and guards. Live docs explain height policy, projection metadata and name collisions. Only owner tags.

## Execution and planning review

Before execution, refine each task to failing-test, implementation, verification and explicit-path commit steps against landed APIs. Record the chosen signatures, file moves, schema numbers, judgement rulings and refinement commit in Outcome. Run targeted Release tests synchronously per task, then the repository Release build and full non-LiveSocket suite once at the round finish. Run the applicable documentation, dependency and whole-tree guards. Rendering uses the hosted backend workflow and committed family goldens. Integration, packing and tagging belong to the owning orchestrator and owner.

Self-review covered spec requirements, consistent contract names, all five Review Focus tests and task proportion. This task-level plan contains no implementation bodies and does not claim later-round completion.

## Outcome
