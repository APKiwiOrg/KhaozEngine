# World authoring R6: General markers and spawn projections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Provide one native point registry for landmarks, player/NPC spawn projections and prefab-local markers with exact saved heights.

**Architecture:** Format 8 integrates the R5 local-marker payload into a world registry with stable IDs, free XYZ/yaw, names, roles, enabled state and ordered tags. The resolver provides generic points and tagged shapes, while consumer role validation and habitat/spawn policy remain outside MapDoc. Legacy typed lists project losslessly through this registry.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 and T1 to T9 with revised T4 are approved. This round plan remains pending owner review.

Task-level plan. Refine to full step level against the landed R1 to R5 APIs before executing, and record the refinement in Outcome.

## Approval stage, reconciled 2026-10-05

The controller supplied the exact owner answer "Approve" for both specs, T1 to T9 with revised T4, rigid prefab v1 and the 12 to 18 elapsed-week estimate, and C4 boundary policy. This R6 document remains a draft for refinement at its round. It requires a full plan against owner-released dependencies and then owner plan approval before execution. Spec approval is not acceptance of actual changed targets, distances, occlusion, stances or exact water-boundary samples. R11 records named import acceptance.

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

- Reconciliation 2026-10-05. PROGRAM adds negative x regions `r_-1_0` through `r_-1_4`, tile x -64 through -1. Accept signed region/tile coordinates and world X without zero clamping, unsigned keys, truncation-to-zero bucketing or an implicit shift. PROGRAM's 6x5/384x320-m and cow-pen x -33 through 7 values are requirements pending actual shipped-source refreeze, not this historical fixture's accepted counts. Task 4 refreezes current shipped source and complete marker inventory/digests after the 0.11.0/accepted-grand-world main barriers. If those barriers have not landed, use the old fixture for regression only and leave actual-source acceptance open. R11 refreezes again for adoption.

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
- **Tests:** `LegacySpawnMigration_PreservesComputedYRoleArchetypeAndTags` uses a slope where old and canonical sampling differ. `ExplicitElevatedMarker_RoundTripsWithoutSnap` checks sub-metre XYZ/yaw and height mode. `NegativeXRegionsAndMarkers_RoundTripWithoutShift` includes source regions r_-1_0/r_-1_4, world X -33, tile x -64/-1 and a marker just across X 0 in both storage forms. Signed coordinates preserve source addressing and resolver identity. `MarkerPayload_RejectsFutureVersionAndInvalidFiniteValues` fails before build. `AnalyticOldSpawnReaders_KeepExistingBehavior` covers unchanged analytic execution.
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
- Produces complete fixture table with 32 NPC, one player and five landmarks, all 38 exact roles/tags/enabled/world points. These 38 are historical regression counts. Task 4 derives an actual shipped-source marker inventory after both game-main barriers, recording commit, per-file/aggregate digests and exact rows. R11 refreezes that actual source again before import.
- Publishes C6 for R7 to R11 without cow pen, duck habitat or spawn selection logic.

- [ ] Deliver the round marker proof and next-available minor release candidate.
- **Tests:** `Frozen38Markers_PreserveEveryPointRoleAndTag` compares historical fixture keys rather than totals alone. `ActualSourceRefreeze_DerivesSignedRegionAndMarkerInventory` compares complete source/native key sets, including r_-1_* and changed marker rows, without defaulting to 38 or old proposal counts. `MarkerRegistry_SaveReloadBothStorageFormsIsEqual` includes local/elevated/disabled points. `NativeMarkerConsumers_LoadWithoutTileWorldOrGameTypes` checks package closure. Run existing MapDocumentWindowingSpawnTests/MapWindowSpawnSearchTests unchanged.
- **Exit proof:** Release filter `FullyQualifiedName~NativeMarkerAcceptanceTests` passes, then one serialized full round build/suite and guards. Live docs explain height policy, projection metadata and name collisions. Only owner tags.

## Execution and planning review

Before execution, refine each task to failing-test, implementation, verification and explicit-path commit steps against landed APIs. Record the chosen signatures, file moves, schema numbers, judgement rulings and refinement commit in Outcome. Run targeted Release tests synchronously per task, then the repository Release build and full non-LiveSocket suite once at the round finish. Run the applicable documentation, dependency and whole-tree guards. Rendering uses the hosted backend workflow and committed family goldens. Integration, packing and tagging belong to the owning orchestrator and owner.

Self-review covered spec requirements, consistent contract names, all five Review Focus tests and task proportion. This task-level plan contains no implementation bodies and does not claim later-round completion.

## Outcome

### Documentation reconciliation, 2026-10-05

- Approval stage: specs approved with revised T4. R6 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: Refine this draft against released prerequisite APIs at round start. Exact signatures, failing assertions and owner plan approval remain open. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: source and planning review only, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: Signed negative x source regions r_-1_* are required. Task 4 refreezes actual shipped source/marker inventory after both game-main barriers. R11 repeats the actual-source freeze before adoption. Old 38-marker counts are regression data only.
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
