# World authoring R11: Offline importer and exhaustive no-loss ledger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Tasks use checkbox (`- [ ]`) syntax for tracking. This is a planning artifact, not implementation approval.

**Goal:** Convert a frozen TileWorld source to a self-contained native MapDoc closure with exhaustive, deterministic fidelity evidence and publication refusal on any unapproved loss.

**Architecture:** One optional offline migration package provides the CLI and library pipeline. It evaluates source runtime semantics, converts them using the landed native contracts, stages a new destination and emits ledger payload 1 before atomic acceptance. CPU geometry/query proofs and backend capture differentials are independent gates, with approved semantic exceptions explicitly identified rather than labelled lossless.

**Tech Stack:** C#/.NET, System.Numerics, versioned JSON and JSON Schema, xUnit, engine-owned rendering/physics seams, MapEditor and ke-mapedit MCP.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), approved direction at commit `49b045f75`. Read the spec, its evidence register and the earlier rounds' Outcome sections before refinement. OA1 to OA3 are owner rulings. T1 to T9 remain subject to owner review.

Task-level plan. Refine to full step level against the landed R1 to R10 APIs before executing, and record the refinement in Outcome.

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

1. A one-cell, one-byte, missing empty plane or unassigned building member can disappear while totals still match. Tasks 2 and 5 require source-key bijection.
2. Frozen counts can become stale after accepted grand-world changes. Task 1 refreshes source hashes/inventory and refuses inconsistent evidence.
3. A crash, changed input or unrelated existing output must not leave an accepted partial destination or modify the source. Task 6 tests staging/publication.
4. A digest-valid conversion can still close a doorway, wet a bridge, change targetability or miss hidden-roof shadows. Task 7 tests runtime and pixels separately.
5. Prefab grouping, storage seams and slope-dependent collider variants can duplicate leaves or move art while preserving source IDs. Tasks 3 and 4 compare effective semantics.

## Dependencies and delivered contracts

| Earlier rounds | Consumed contracts |
| --- | --- |
| R1 / C1 | Native closure/identity/allocator/tombstones, stable numeric/string IDs, schema migrations and atomic writers |
| R2 / C2 | Exact integer-unit surfaces, four cuts/rotations/flags, all effective plane heights, support-versus-paint and canonical triangles |
| R3 / C3 | Version 1 asset collision descriptors/variants, shared oriented queries, complete/movement statics, capture and bounds |
| R4 / C4 | Native bounded bodies/domains/SurfaceY/medium/material and feet-aware rules |
| R5 / C5 | Definition/variant/instance/override keys, persisted leaf bindings, local floor/volume/roof/water transforms |
| R6 / C6 | All-role general points, exact explicit heights and typed spawn projections |
| R7 / C7 | Exact density/sample records, predicate versions and owner-bound legacy exclusion masks |
| R8 / C8 | Complete native view/capture, shadow-only roofs, residency and backend evidence |
| R9 / C9 | Unified GUI command model and accepted native asset/world validation workflow |
| R10 / C9 | Complete command/query/summary surface and profile-aware route/capture APIs |

Produces C9 offline importer, ledger payload 1, source inventory/membership manifest validation, deterministic native-only output and differential acceptance report. Grimhollow adoption uses this importer with its game-owned declarative membership/role manifest after both 0.11.0 and accepted grand-world land. Engine TileWorld remains available to other consumers. This engine round does not delete game code, mutate journals/catalogs or force a pivot pin.

**Name discipline:** `NativeAssetDescriptor`, `ResolvedMapWorld`, `NativeSurface`, `MapWorldBuild`, `NativeWaterBody`, complete/movement/capture query signatures and atomic writer APIs are provisional R1 to R4 names. R5 to R10 names/versions are confirmed from their landed plans. New importer/ledger names below are proposed until refinement.

## Ledger payload 1

Use closed, versioned machine-readable JSON with a canonical header and canonically sorted row stream, optionally deterministically compressed. Commit the full rows plus plain summary/digests, not a transcript, sample or aggregate-only report. Numeric game IDs are decimal strings. Nonfinite values are invalid. Stable ordering is source category, canonical source key, destination identity. Compression cannot inject timestamps or non-deterministic metadata.

### Header

| Field group | Required contents |
| --- | --- |
| Identity/version | Ledger payloadVersion 1, source repository commit/SHA and source world hash, converter algorithm/package version, source-semantics version, native builder algorithm/version and all options |
| Frozen closure | Source catalog/prefab/mesh/material/collider/light/LOD path-to-digest inventory and accepted-source category/key counts |
| Manifest | Membership/role manifest payload version, digest and explicit original-object/cell/marker/roof membership, reserved numeric IDs and approval evidence |
| Native result | Destination normalized document hash, complete native closure hash, target format/payload versions, per-category leaf/container/storage counts and unchanged playable bounds |
| Comparison policy | Exact authored equality and predeclared 0.00001 m transformed position/vertex tolerance, 0.000001 normal-component tolerance, algorithm/query profiles/windows and fixed camera/TAA Native capture settings |
| Exceptions | Explicit owner-approved exception references with affected source keys, old/new values or queries and rationale. No implicit T4 blanket waiver |
| Result/evidence | Per-category and overall acceptance result, row-stream digest/count, complete/movement static digests, nav/profile hashes, query/capture artifact digests and failure list |

### One row per source key

Each row contains `category`, `sourceKey`, `sourceAddresses`, `sourceValueDigest`, `destinationKeys`, `destinationOwner`, `effectiveTransform`, `role`, `geometryDigest`, `comparison`, `maxPositionError`, `maxNormalComponentError` and optional `approvedExceptionRef`. Destination values/field digests are included for reproducible comparison. Nullable fields are explicit by category. One source row may name an equivalent decomposed destination representation, but every destination original-leaf record has exactly one source owner.

| Category and source key | Mandatory destination accounting and comparison |
| --- | --- |
| Object numeric ID plus region/plane/address/Kind | Exactly one original native leaf with unchanged numeric/string identity, Kind, ordered tags, role, explicit-Y transform and asset variant/physical shape digest. Parent containers count separately |
| Corner on every source plane | Native surface corner/unit and source derivation mode, exact integer value and effective triangle heights. Shared seam aliases retain all source addresses/digests and must agree |
| Cell/layer/plane, including absent/empty upper-plane payloads | Material/height/cut/quarter-turn/flags/feather/reserved metadata and support/paint owner. No empty plane invents walkable geometry |
| Marker/typed spawn | Native point or local marker, exact name/role/archetype/enabled/tags and resolved XYZ/yaw/height policy |
| Reusable prefab definition | Native definition/variant payload/key/digest and complete source resource membership |
| Actual instance membership | Native parent/override/binding keys and each original object/cell/marker/roof represented exactly once |
| Roof link and connected indoor footprint | Native child/storey/volume/roof relationship, original roof height, membership/visibility/shadow cases |
| Water body and every clipped rectangle | Native stable body/domain/SurfaceY/medium/material, exact source identity, half-open edge and dry/submerged sample results |
| Each density byte | Native layer coordinate/value/settings/predicate version, exact byte digest in original positive-Z row convention |
| Every generated foliage sample/decision | Stable sample identity, position/orientation/scale/asset/material, exclusion decision and contributing masks/owners |
| Every asset/catalog closure reference | Native path/digest and Kind binding, mesh units/origin/parts/material/repeat/LOD/light/collider/support data, no hidden legacy runtime reference |
| Added containers and storage metadata | Separate native parent/new IDs, single owner/all memberships, bounds and original-leaf inventory, with no duplicated draws/statics or widened playable area |

Comparison classes are `preserved`, `equivalent-native`, `approved-semantic-change` and `failed`. Grouping/storage changes count as equivalent only if resolved values/geometry match. Each source key appears once. Unmapped, duplicate, dangling, unauthorized or above-tolerance rows fail acceptance. The complete row set, not the class counts, proves coverage.

## Acceptance checks

These are required by C1 to C9/T1 to T9 and the companion game specification, not optional checklist suggestions.

1. Every frozen source key has exactly one ledger classification, including all four planes/cells/corners, markers, seven reusable definitions, actual interiors/roof links, water rectangles and every density byte/sample. Native leaves and new containers have separate inventories.
2. Authored integers, bytes, numeric/string IDs, Kind, roles and ordered tags compare exactly. Effective source placements use explicit Y, preserve mesh origin/units/materials/parts/LOD/lights and retain slope-dependent collision spans in digest-keyed variants.
3. Derived transforms/vertices compare within 0.00001 m, normals within 0.000001 per component. Record tolerances before conversion and report every exceedance. Native floor/render/capture use identical triangle descriptors, with zero alternate-floor disagreement at (4.37,-64.61).
4. Preserve exact canonical legacy movement triangles, complete source statics and explicit game-filtered movement/capture views. No unused upper plane becomes a floor, no paint-only patch becomes support, no source doorway becomes a box.
5. Frozen evidence fixture checks 25 regions of 64x64 cells, four planes at 4.5 m, 14 materials, 121 archetypes/113 used, 5,566 objects (4,305 None, 842 Solid, 381 Wall, 38 WallCorner), 578 interactive objects and ten higher roofs. These are baseline regression counts, not hardcoded accepted-world publication counts.
6. Check the frozen 103,041 distinct ground corners, 102,400 underlays, 2,130 overlays, ten shaped cuts, 660 feather flags, 8,872 blocked, 836 indoor and nine reserved Bridge flags. Record all upper-plane derivations and empties independently.
7. Preserve all 38 frozen markers (32 NPC, one player, five landmarks), their complete values, seven clipped water bodies/113 rectangles at Y -0.37 m and all 25,921 bytes of the 161x161 positive-Z density raster plus every generated sample/decision. Source hash/profile/settings refresh is mandatory for the accepted world.
8. Actual cottage/bank/crafting-hall interiors and original leaf differences survive via checked membership/overrides. Roofs remain at their source transforms, linked hiding/shadow behavior separates storeys, and the open pasture shelter stays visible in Auto.
9. Compare routes/reach/picks for spawn-to-bank/cottage doors, adjacent-wall refusal, compound openings/tree bands, bridge deck/bed, river wading, cliff slopes and generic habitat inputs under the established profile/windows. Game pen/duck policies run in later game adoption, not engine policy.
10. Differential T4 report lists exact old/new hit targets, distances and walk-up outcomes, including the legacy minimum one-metre reach height and mesh-bound selection. Physics preservation and same-head shape agreement are separate gates. Owner-approved exceptions never broaden collision to hide changed reach.
11. Compare complete load versus chunked load, all-spanning bounds/memberships, duplicate-free draws/statics and clean unload. Playable coordinates/bounds and saved world-point compatibility stay unchanged.
12. Capture fixed-camera native paint/feathers, every indoor/roof/hidden-shadow case, river/bridge, free props/buildings, forest and ridge through the same R8 composition with TAA Native. Keep unchanged legacy/analytic/art goldens. No camera shift or gratuitous rebaseline.
13. Reload native monolithic/tiled closure, build both headless heads and full nav capture without TileWorld files/packages. Missing/stale closure, nav/profile input or client identity refuses. Full native boot/source/transitive/output audit proves runtime independence.
14. Identical source/manifest/options produce identical output/row/closure digests. Source remains untouched, unrelated destination refuses overwrite, crash/rejection creates no accepted publication pointer.
15. Grimhollow's later adoption owns native content commits, game-specific differential approval, journal/state continuity, all production/test/tool TileWorld deletion and its build/test/format checks. This round supplies evidence hooks and no-loss converter contracts, not a claim that the game has migrated.

## File Structure

| Action and engine path | Responsibility |
| --- | --- |
| Create `KhaozEngine.MapDoc.TileWorldImport/KhaozEngine.MapDoc.TileWorldImport.csproj`, `README.md`, `Program.cs`, `ImportRequest.cs`, `ImportPipeline.cs` | Optional package, one offline CLI/library pipeline and arguments |
| Create `Source/TileWorldSourceInventory.cs`, `Source/TileWorldSourceSemantics.cs`, `Source/BuildingMembershipManifest.cs` | Frozen source closure, legacy effective geometry/queries and declarative grouping validation |
| Create `Conversion/NativeSurfaceConversion.cs`, `NativeAssetConversion.cs`, `NativeBuildingConversion.cs`, `NativeEnvironmentConversion.cs` | Focused native representation conversion over R1 to R7 contracts |
| Create `Ledger/ImportLedger.cs`, `ImportLedgerWriter.cs`, `ImportLedgerValidator.cs`, `FidelityComparison.cs`, `QueryDifferential.cs` | Payload 1, canonical complete rows, coverage/errors and semantic report |
| Create `Publication/ImportStaging.cs`, `ImportPublication.cs` | New destination staging, validation and atomic accepted result |
| Create `KhaozEngine.MapDoc.TileWorldImport.Tests/KhaozEngine.MapDoc.TileWorldImport.Tests.csproj`, `SourceInventoryTests.cs`, `SourceSemanticsTests.cs`, `NativeConversionTests.cs`, `BuildingEnvironmentConversionTests.cs`, `ImportLedgerTests.cs`, `ImportPublicationTests.cs`, `NativeImportAcceptanceTests.cs` | Narrow optional offline test corpus and complete CPU acceptance |
| Create `KhaozEngine.MapDoc.TileWorldImport.Tests/Fixtures/Hollowmere/` frozen source/manifest/native-reference files or immutable external fixture manifest | Full source inventory, pin/digest provenance and compact deterministic ledger evidence |
| Create `KhaozEngine.Render.Tests/MapDoc/NativeImportDifferentialGpuTests.cs` with optional source comparison fixture access | Backend rendering differential, no native runtime TileWorld dependency |
| Modify solution/test references, package/architecture expectations, root/package/API/dependency/index/release docs | Opt-in importer boundary, CLI/ledger contract and released round capability |

## Judgement and refinement record

- J11.1. Proposed one optional package exports the library request/pipeline and a thin offline `ke-mapimport` entry point. Choose its final packaging against repository dotnet-tool conventions at refinement. Source, destination, membership manifest and ledger paths are required arguments. No importer code is duplicated inside ke-mapedit.
- J11.2. Checked membership may not be inferred from nominal stamps. Ambiguous/unassigned original content refuses, with exact source keys. A parent is new identity, leaves preserve original IDs. Source seam corner aliases retain evidence, rather than silently deduplicating inconsistent values.
- J11.3. Converter execution needs no graphics device. Source-side legacy predicate/geometry evaluation stays in the optional package/test corpus. Render differential uses the separate backend test route. MapDoc/native packages never acquire a TileWorld dependency to help conversion.
- J11.4. Frozen proposal counts test converter regressions. Before real adoption, re-freeze the accepted grand-world and derive its full inventory. Do not weaken baseline assertions to lower bounds or invent owner-approved exceptions.
- Spec gap J5.1, local water in prefab payload, must be reconciled before building conversion. T4 targetability acceptance and T3/v1 scope remain owner review items, with no new option A/B/C decision.

---

### Task 1: Optional pipeline, frozen source and manifest contract

**Files:** Create importer package/Program/ImportRequest/ImportPipeline, SourceInventory/BuildingMembershipManifest, SourceInventoryTests and fixture manifest. Modify narrow project/package registration.

**Interfaces:**
- Consumes C1 closure/schema/identity and C9 required offline source/destination/membership/ledger arguments.
- Produces `ImportRequest` with paths, source commit/hash, expected closure/inventory, converter/builder options and fixed tolerance policy.
- Produces immutable source inventory/manifest with explicit original memberships, roles, reserved IDs, roof/storey/volume/floor ownership and full input digests.
- No read of an unfrozen changing world and no source mutation.

- [ ] Deliver validated inputs and one offline pipeline contract, with no world editor capability.
- **Tests:** `SourceFreeze_RejectsChangedCatalogPrefabMeshOrWorld` checks all input files. `MembershipManifest_RejectsAmbiguousDuplicateAndUnassignedKeys` names failures. `SourceDestinationOrLedgerOverlapAndExistingUnrelatedOutput_Refuse` checks canonical paths/symlinks. `AcceptedWorldInventory_IsDerivedNotFrozenCountDefault` proves refreshed counts. `ImporterPackage_IsOptInAndNativeGraphHasNoTileWorld` checks package edges.
- **Exit proof:** Release filter `FullyQualifiedName~SourceInventoryTests` in TileWorldImport.Tests passes. The complete frozen header/manifest exists before conversion begins.

### Task 2: Source runtime semantics and exact comparison oracle

**Files:** Create TileWorldSourceSemantics.cs/SourceSemanticsTests.cs and pinned source value/geometry fixtures.

**Interfaces:**
- Consumes TileWorld source/catalog/prefab data only in the optional source side, including canonical source transforms and terrain/collider/water/foliage semantics.
- Produces immutable effective corners/cells/materials/triangles, object shapes/support, water bodies/medium, markers, indoor/roof facts and generated foliage records.
- Source oracle records original bilinear art anchoring explicitly, while movement floor uses the legacy drawn triangles. All four plane derivations/empties are inventoried.

- [ ] Deliver the checked source oracle against which native conversion is compared.
- **Tests:** `AllFourPlanes_IncludeDerivedOverridesAndEmptyCells` covers upper floors without inventing support. `SpikePoint_UsesLegacyMovementTrianglesNotBilinearFloor` pins the measured location. `LegacyWallCornerSolidAndDeck_CompileExactEffectiveShapes` includes terrain-dependent spans. `LegacyWaterAndFoliage_RecordEveryDomainByteSampleAndDecision` checks row direction and predicate parameters. `SourceSeamAliases_DisagreementRefuses` prevents silent corner repair.
- **Exit proof:** Release filter `FullyQualifiedName~SourceSemanticsTests` passes. Source input digests and effective semantic inventory are stable without a GPU.

### Task 3: Exact native surfaces, assets and leaf identity

**Files:** Create NativeSurfaceConversion.cs/NativeAssetConversion.cs/NativeConversionTests.cs.

**Interfaces:**
- Consumes Task 2 source oracle and R1/R2/R3 native DTOs/builders.
- Produces absolute 1 m integer-unit surfaces/material/paint/flags, all effective upper surfaces, non-support paint and explicit-Y leaf placements.
- Produces digest-keyed source-scale asset/collider variants for slope-dependent solids/walls/decks, exact mesh/material/LOD/light closure and Kind.
- Numeric allocation starts above all imported/reserved IDs, preserves leaf IDs/tags and never uses ordinal identity.

- [ ] Deliver terrain/assets/leaves with exact authored values and canonical geometry.
- **Tests:** `EverySurfaceField_PreservesExactIntegerPaintFlagAndReservedMetadata` checks all cuts/rotations/feathers. `CanonicalNativeTriangles_MatchSourceAndAllConsumers` enforces 0.00001 m and 0.000001 normal component tolerances with per-key failures. `SlopeDependentColliderVariant_PreservesRigidSpanAndMeshScale` preserves openings/support. `LargeImportedIDs_ReserveHighWaterAndKeepOrderedTags` covers >2^53 and exhaustion. `StoragePadding_DoesNotWidenPlayableBounds` catches spike-style padding.
- **Exit proof:** Release filter `FullyQualifiedName~NativeConversionTests` passes. Two native heads agree exactly on the same descriptors and identity table, independent of source comparison tolerances.

### Task 4: Actual buildings and environment preservation

**Files:** Create NativeBuildingConversion.cs/NativeEnvironmentConversion.cs/BuildingEnvironmentConversionTests.cs.

**Interfaces:**
- Consumes checked source memberships, R4 water, R5 definitions/instances/overrides/volumes, R6 registry and R7 layers/predicates/masks.
- Produces seven frozen reusable definitions and nine actual interior memberships with per-instance differences and persisted leaf bindings.
- Produces exact bounded water/marker/foliage records, legacy explicit masks bound to transformed owners and no game-specific habitat rules.
- New prefab parents/storage metadata have separate accounting from original source leaves.

- [ ] Deliver all non-terrain semantics through native data without stamp reconstruction.
- **Tests:** `ActualInteriors_KeepCraftingHallDifferencesAndEveryLeafOnce` checks bank/cottage/furniture/lights/roof links. `HigherRoofsOpenShelterAndStoreys_PreservePolicy` checks visibility/shadows as intent. `All38Markers_PreserveRolesHeightsAndTags` checks each row. `SevenBodies113Rects_PreserveSurfaceAndMedium` includes Y -0.37 and bridge feet. `All25921DensityBytesAndGeneratedSamples_Preserve` checks decisions and explicit mask ownership.
- **Exit proof:** Release filter `FullyQualifiedName~BuildingEnvironmentConversionTests` passes. Every source building/environment key maps to a native owner without duplication or guessing.

### Task 5: Canonical ledger and fail-closed fidelity gate

**Files:** Create ledger DTO/writer/validator/FidelityComparison/QueryDifferential and ImportLedgerTests.cs.

**Interfaces:**
- Consumes Tasks 1 to 4 inventories/representations, C9 fixed tolerances and explicit owner-approved exception manifest.
- Produces ledger payload 1 as specified above, full sorted rows/digests and reproducible old/new comparisons.
- Produces separate T4 hit-target/distance/stance differences, not a claim of unchanged reach. Physics shape preservation and same-head agreement stay independent.

- [ ] Deliver exhaustive source-to-native coverage validation and semantic difference classification.
- **Tests:** `Ledger_AllSourceKeysAccountedExactlyOnce` compares key sets/inverses. `OneMissingCellByteEmptyPlaneRoofOrSample_RefusesAcceptance` injects sparse loss. `DuplicateDanglingOrUnapprovedDifference_Refuses` checks each failure class. `ToleranceHeaderFixedBeforeConversion_ReportsEveryExceedance` checks exact boundary values. `T4Differential_ListsOldNewTargetDistanceAndApprovedReference` includes the one-metre legacy envelope. `CanonicalRowsAndCompression_AreDeterministic` checks bytes/digests.
- **Exit proof:** Release filter `FullyQualifiedName~ImportLedgerTests` passes. Aggregate counts alone can never accept conversion.

### Task 6: Deterministic native reload and atomic publication

**Files:** Create ImportStaging.cs/ImportPublication.cs/ImportPublicationTests.cs. Complete CLI/library pipeline orchestration.

**Interfaces:**
- Consumes complete ledger acceptance, C1 atomic native writers/closure and headless builder.
- Produces staged new native output, library/CLI result with artifact paths/digests/failures and one accepted publication promotion.
- Runtime asset references resolve wholly inside the portable native closure. Source paths occur only as immutable ledger evidence, never native runtime references.
- Identical source/manifest/options produce identical canonical native content/ledger/digests. Refuse unrelated outputs, preserve source and existing accepted output.

- [ ] Deliver failure-safe conversion/save/reload with native-only packaged output.
- **Tests:** `IdenticalImportInputs_ProduceIdenticalNativeAndLedgerDigests` compares two bounded fresh destinations once. `SaveReloadMonolithicAndTiled_PreserveClosureAndValues` keeps TileSize identity. `CrashOrRejectionBeforePromotion_PublishesNoAcceptedPointer` uses deterministic fault injection. `ChangedInputDuringImport_RefusesFinalPromotion` rechecks freeze. `NativeOutput_LoadsAfterMovingDestinationAndRemovingSource` boots the headless resolver/build in an isolated native-only host. `UnindexedOrUnrelatedDestination_IsUntouched` checks writer guards.
- **Exit proof:** Release filter `FullyQualifiedName~ImportPublicationTests` passes. CLI and library call the same pipeline, with nonzero failure status and no accepted output on rejection.

### Task 7: Exhaustive runtime/render acceptance and adoption handoff

**Files:** Create NativeImportAcceptanceTests.cs/NativeImportDifferentialGpuTests.cs and full checked fixture/ledger/report artifacts. Modify live package/API/dependency/index/release docs.

**Interfaces:**
- Consumes Tasks 1 to 6, R3 complete/movement/capture query descriptors, R8 fixed snapshots and R10 inventory.
- Produces full frozen-fixture report for Acceptance checks 1 to 14 and explicit game-owned hooks for check 15.
- Outputs old/new shape/nav/query/capture evidence and exceptions awaiting owner review, never an unexplained-loss success.

- [ ] Deliver independently usable converter capability, complete fidelity report and next-available minor release candidate.
- **Tests:** `FrozenHollowmere_ExhaustiveNativeAcceptance` checks all frozen categories/key counts and no unexplained row. `SpawnBankCottageBridgeRiverCliffRoutes_Agree` uses fixed profile/window and adjacent-wall/door tests. `TwoHeads_PickReachStanceStaticsAndCaptureAgree` covers free compounds/tree bands and dry decks. `NativeOnlyPackagedBoot_HasNoTileWorldRuntimeReferences` audits projects/transitive packages/output. `NativeImportFixedCamera_DifferentialGpu` covers feathers/indoors/hidden shadows/river/forest/ridge with TAA Native, unchanged legacy/art goldens and targeted sparse-feature checks.
- **Exit proof:** Release filter `FullyQualifiedName~NativeImportAcceptanceTests` passes. Backend differential evidence has zero skipped required GPU cases. Then one serialized full round Release build/suite and guards. Owner reviews T4 exceptions/no-loss report before accepted-world publication and release tagging. Game adoption remains a separately approved fresh branch after both barriers, with full TileWorld deletion and journal/state continuity proofs.

## Execution and planning review

Before execution, refine each task to failing-test, implementation, verification and explicit-path commit steps against landed APIs. Record the chosen signatures, file moves, schema numbers, judgement rulings and refinement commit in Outcome. Run targeted Release tests synchronously per task, then the repository Release build and full non-LiveSocket suite once at the round finish. Run the applicable documentation, dependency and whole-tree guards. Rendering uses the hosted backend workflow and committed family goldens. Integration, packing and tagging belong to the owning orchestrator and owner.

Self-review covered spec requirements, consistent contract names, all five Review Focus tests and task proportion. This task-level plan contains no implementation bodies and does not claim later-round completion.

## Outcome
