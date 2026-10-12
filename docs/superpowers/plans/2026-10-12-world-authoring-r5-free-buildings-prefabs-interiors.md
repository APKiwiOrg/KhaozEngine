# World Authoring R5: Free Buildings, Prefabs and Interiors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Author rigid reusable buildings at free XYZ, yaw and positive uniform scale, preserving every child identity and publishing identical support, occupied-space, local-water and roof/light/marker facts to both heads.

**Architecture:** Format 7 adds digest-pinned prefab payload 1 and persisted instance bindings. Resolver 2 expands them into one immutable effective resource view. Polygon-prism rooms extend R2's occupied-space authority and nest inside authored parents. R3 installs the same transformed canonical faces and shapes, R4 compiles water against the resulting occupied partitions, and roof intent stays separate from physical statics. Existing native transactions own allocation and atomic publication.

**Tech Stack:** C# on the repository's .NET target, System.Numerics, System.Text.Json, closed JSON schemas, xUnit, MapDoc's exact geometry and R4 rational clipping, the existing Physics seam and Bepu test backend. No new package or third-party dependency.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), C1, C2, C5, DG9 and the prefab/interior rows of the verb map. Read also [R2 caves and scale](../../design/WORLD-AUTHORING-R2-CAVES-SCALE-2026-10-06.md), [R3 executable plan](2026-10-10-world-authoring-r3-shared-shapes-headless-world.md), and [R4 executable plan](2026-10-11-world-authoring-r4-bounded-water.md).

Binding private controller inputs are Grimhollow `feature/world-authoring` `docs/superpowers/programs/world-authoring/R5-OWNER-GATE.md`, `DECISIONS.md` OA33, `R3-LEDGER.md` and `R4-LEDGER.md`. The controller supplies those texts in worker briefs. Research inputs are the engine controller's `.superpowers/sdd/2026-10-12-world-authoring-r5-refresh/research-engine.md` and `research-program.md`. Their proposals have no authority where OA33 chose otherwise.

## Approval and execution gate

OA33 approved the 19 gate decisions on 2026-10-12, including Decision 4 option C. This document replaces the six-task 2026-10-05 draft. It is written against `06b734c5722bc2cc95cea459255d6ec93e9236c7`, staged 20.33.0, after released R1 20.27.1, R2 20.30.0, R3 20.31.0 and R4 20.32.0.

OA33 authorizes this rewrite only. Independent plan reviews and the owner's approval of the reviewed plan precede product code. R5-P21 to R5-P23 are binding controller review-2 rulings. R5-P24 is the binding review-3 materialization ruling. Other R5-P choices remain proposals except where the supplied adjudications fix the stated correction. Workers stop at verified task commits. Integration and an engine-rooted release belong to the controller under OA25 and Decision 19. Game adoption and the R11 freeze remain separate.

## Global Constraints

- OA2. Free XYZ, yaw radians and finite positive uniform scale, including non-grid position, non-quarter yaw and scales 0.8 and 1.2.
- OA5. Exactly 1 m minimum vertical target reach-envelope height in world metres. This is not action range and is not multiplied by instance scale. Physical collision stays exact to its source geometry.
- OA6. Rigid prefab payload 1, optional local water, stable keyed overrides and optional authoring snap. No nesting, generated stairs, foundations, CAD or new cave model.
- Decisions 1 and 2. Rooms are spaces in R2, with authored parents, one horizontal boundary owner under R4-P28 and vertical spans [lower, upper). An ancestor is not a competing owner. True peer overlap is Ambiguous.
- Decision 3. Partial-face material regions stay inside unchanged canonical triangles. Their subdivision is render-only. No duplicate floor or persistent world repaint.
- Decision 4. Portals remain AuthoredOpen. Live door state belongs to https://github.com/APKiwiOrg/KhaozEngine/issues/1363, superseding OA22's former R5 door promise.
- Decision 5. Headless geometry, owners, membership, water inputs, roof intent and physical doorway clearance are R5 evidence. Native routes wait for G1b, #1355 and #438 phase 5.
- Decisions 7 to 9. Format 7, prefab payload 1, manifest Prefab resources, complete digest-verified closure, no cycles, persisted bindings for every child type. Loading and resolution never allocate. Removed keys retain reservations, restore reuses and unpack transfers.
- OA32 and R4-P35. Keep the format version in native root identity. Format 7 moves every native identity once, even without prefabs. Analytic execution is unchanged.
- Decision 10. One composition and rounding policy applies to floors, shapes, spaces, water, lights and markers. Local vertical distances scale once. Shared edges agree.
- OA28 and Decision 12. Water covers authored spaces only. No implicit exterior or new water algorithm. Publication, compile and build validate complete effective containment under R4-P1 to R4-P4.
- Decisions 6 and 13. Explicit roof links and world concealment links. Hidden roofs cast shadows, do not block the camera boom, still block sky picking and never change statics.
- Decision 14. Lights and prefab-local markers have their own versioned fields. Markers are not XZ-only MapSpawn records.
- Decisions 15 and 16. An immutable asset version updates every referring instance in the open complete map in one accepted edit. Other maps retain their digest. MapDoc prepares edits, focused MapEditor commands publish them, and ke-mapedit uses those commands.
- Decisions 17 and 18, CD2. Public synthetic village and private exhaustive comparison. Payload, identity and membership tests live in MapDoc.Tests, physics in MapDoc.Physics.Tests, commands/tools in MapEditor.Tests. The private harness stays outside the solution and public CI.
- R3 precision. 1,024 triangles maximum per physics chunk, exact offsets at most 64 m per axis from a whole-metre anchor, WorldFrame local planar radius 512 m and installed coordinates at most 1,000,000 m per axis. Float placement input retains its documented quantization. No cross-platform libm determinism claim.
- Bounded work. Preserve MapQueryLimits defaults 256 patches, 4,096 faces, 64 support intersections, 32 page reads, 64 record reads and depth 8. Preserve R4 limits, including MaxBoundCells. Overflow refuses before work, never truncates an owner.
- Package edges. MapDoc remains physics/render/GPU/editor/TileWorld/game free. MapEditor gains no MapDoc.Physics reference. Only Compatibility.Tests and OracleHarness.Tests may read TileWorld for comparison.
- Repository. Additive public signatures, explicit-path commits, zero warnings, tests under KhaozEngine.Tests.*, new C# files at most 800 lines and no growth of baselined files without owner approval. No dash glyphs or prose semicolons in Markdown.
- Verification. Every build, test, format, render or pack uses build-slot. Focused Release checks per task, full Release build and non-LiveSocket suite once at the candidate gate. No local stress, loops, GPU runs or consumer windows.
- Input and text. AppWindow alone reads raw devices. GUI consumers use InputState, InputManager and Pointer. Player text uses StringId/LocalizedText. Tool diagnostics use the explicit raw path.
- Decision 19. Ride untagged staged 20.33.0 at finish. If already tagged, use the next free minor after reconciled main. CHANGELOG and guarded declarations travel together. An engine-rooted controller thread handles tagging under OA25.

## Review Focus

1. Equivalent geometry uses a direct vertex or an offset child/support origin at a far site. It must freeze identically and survive real sloped R2 queries without intermediate overflow (Task 3 `DecomposedOrigin_EqualsDirectGeometryAtNearAndFarSites`, `PlacedSlope_SupportMembershipAndBoundsSucceedWithWideIntermediates`).
2. A transformed room crosses a seam with its prism, parent bound or remote anchor unavailable. Exact witnesses keep membership/water/roofs conservative. Missing prefab bytes before capture refuse expansion, captured bytes are never reread (Tasks 3, 4, 7 and 8 per-fact theories, with complete controls and nonempty roof IDs).
3. A parent space recurs below and above a dry room while water level 4 lies above the room. Distinct fragments must give lower Closed, room Dry and upper FreeSurface, with stable provenance and reload digests (Tasks 4 and 7 `Column_PartitionsParentAroundNestedStoreys`, `ParentWater_ExcludesAnUncoveredNestedRoom`). Shared free-yaw/reflex edges and storeys keep one owner.
4. Unpack materializes a support resource or leaf emission, then an existing placement command moves, scales, remaps or deletes its owner at a far site. Scope, storey payload, exact untouched pose components, reservations and one attached producer must survive reload/undo without prefab updates recreating children (Tasks 2, 8 to 11 `SameTextLocalAndWorldReferences_RemapOnlyTheLocalOccurrence`, `LeafLights_TwoOwnersHaveDistinctBoundEmissions`, `UnpackAttachedSupport_HasExactlyOneProducer`, `UnpackedPreciseLeaf_ExistingCommandsPublishEveryRoot`, `MaterializedUnpackExtractReloadReplace_PreservesCapturedSupportAndLight`, `LocalExport_MaterializedRowsPreserveEditedAttachments`, `MergedAttachedPlacement_RedoEqualsFinalAcceptedSnapshot`).
5. A fresh prefab command needs bytes, a generic callback changes scatter/exclusions, a floor reseats a long leaf or mixed history crosses collider roots. Preparation receives the matching closure, every edited root and old/new tile publishes, and outer rejection advances nothing (Task 10 `BoundPreparation_FreshCommandReceivesClosureAndCanRetry`, `FloorReseat_InvalidatesEveryChangedNeighborTile`, Task 11 `GenericSnapshot_ScatterAndBakeSurviveHistoryAndReload`, `AssetPublicationFailure_LeavesSessionAndOtherMapsUntouched`, `MixedNativeHistory_RestoresRootsClosureEffectsAndDirty`).

---

## Ownership and later gates

| Concern | Owner and required proof |
| --- | --- |
| Native controller walking, body certificates and layered routes | G1b, #1355 and #438 phase 5. R5 supplies statics, ownership and tile identities only |
| Compound contact provenance beyond canonical support surfaces | #1327. R5 closes the prefab-floor part, not every primitive edge/vertex mapping |
| Live doors and generation-fenced collision/nav/water swaps | #1363 |
| Roof colour/shadow pixels, camera boom integration, sky rays and local-light rendering | R8 consumes Task 8 intent. Hiding cannot remove collision |
| Partial-face paint drawing and foliage exclusions | R8 drawing, R7 distribution. Both consume effective canonical owners |
| Runtime streaming, eviction, HLOD and on-demand nav | R8 and #1301. R5 proves a bounded effective producer and scoped completeness |
| GUI closure/bounds binding, prefab mode and real input | R9 and #1349. R5 adds no GUI host |
| Whole verb-map wire parity | R10. R5 registers its specified prefab/interior wire verbs and supplies their shared services |
| General marker registry and spawn projection | R6 consumes payload-1 local marker fields |
| Private lossless importer and actual source freeze | R11/G2. Task 13 is a comparison procedure, not accepted game adoption |
| Water indexing, huge exterior coverage and medium-aware seam capture | #1358 to #1362. Keep explicit refusals and no per-frame performance claim |

## Controller rulings and remaining proposals

Each judgment not fixed by OA33 is named here. R5-P21 to R5-P23 come from the binding review-2 adjudication and R5-P24 from review 3. Selecting the gate's approved option is not a new ruling.

| ID | Proposed decision and reason | Cost if wrong |
| --- | --- | --- |
| R5-P1 | Keep resolver identity (1, 2) and AuthoredBindingsV2. Version prefab expansion as 1 and transformed query policy as 2. Include them only for effective resources, preserving resolver-1 routing | A later resolver 3 requires an explicit migration and API adapter |
| R5-P2 | Store instances, bindings, overrides, transfers and world building intent in native root globals, once. Local geometry stays in immutable prefab resources. No tile instance copies | Large instance registries need an indexed root extension in R8 |
| R5-P3 | REVISED. Preserve released public Compose. Effective origins, yaw and scale compose in binary64 until one point freeze, including implicit support resources. Freeze XZ to dyadic quantum, Y to the released binary32 seam, and declare a separate backend pose approximation | Composition and adapter error bounds are explicit. Wider predicates and output narrowing cost additional query work |
| R5-P4 | REVISED. Every prefab-only reference occurrence carries a closed typed scope slot. Child keys map to declared payload IDs and anchors. Explicit Surface children forbid embedded records. Support-resource embedded records receive declared implicit Record bindings | Slot grammar and retained reservations are versioned. Extraction must emit complete slots |
| R5-P5 | REVISED. Deepest room occupancy carves ancestors into stable identified fragments with lower/upper boundary provenance. Effective intervals intersect canonical parent bounds, and repeated parent intervals retain different fragment IDs | Fragment propagation affects support, water contours, bounds and classification |
| R5-P6 | A prism domain is a new topology record referencing an existing MapSpaceDoc, not a second classifier. It carries parent, storey, lower/upper heights and instance ownership, with no mandatory physical wall at every semantic edge | A later sealed-room rule adds coverage checks, without changing membership authority |
| R5-P7 | Positive-volume peer overlap refuses publication and returns Ambiguous in readable diagnostic views. Roof groups are explicit room sets, not transitive portal flood fill | Authors must maintain concealment sets when redesigning connections |
| R5-P8 | REVISED. Uneven-site seating stays diagnostic. Pure support-resource installation belongs to MapDoc before support sampling. Physics consumes the installed descriptors. Coincident independent floors refuse | A future foundation tool remains separate. Stage order is now a cross-package contract |
| R5-P9 | Overlapping material regions require an explicit integer priority, then ordinal paint ID. Equal-priority positive-area conflicting paint refuses. Render subdivisions retain parent face keys | A future blend system needs a new paint policy |
| R5-P10 | REVISED. R4 compiles effective occupied fragments with their identities and boundary provenance. Add a fragment classifier. Legacy space/XZ classification returns Unresolved for differing fragment answers and preserves single-interval behavior | Consumers requiring vertical distinction use the additive API. No lattice resampling |
| R5-P11 | REVISED. Roofs have an explicit inventory. Each link names its attached groups. Only those groups expand its room set, and Auto hides above links. Unresolved membership keeps the full inventory visible | Authors maintain explicit inventory and attachments. Group overlap does not create transitive closure |
| R5-P12 | Publish digest-addressed immutable prefab and manifest bytes first, then accept one map/closure switch. Failed switches may leave unreachable immutable bytes, never changed roots. Undo restores the old closure | Unreferenced immutable files need later explicit cleanup, not automatic deletion |
| R5-P13 | REVISED. Preflight caps instances, children, references, expanded patches/records/geometry and all retained binding/override/transfer state before copying or remapping. Shared immutable payloads do not waive per-instance expansion charges | Larger valid maps need explicit limits or later R8 indexing |
| R5-P14 | REVISED. Use 13 serial tasks with independent acceptance/review gates, splitting pure edits, commands/effects and asset/history/tools. Keep one private comparison at the candidate gate | More review transitions, with bounded ownership and clearer rejection gates |
| R5-P15 | REVISED for review 2. Unpack keeps explicit standalone homes and precise poses. Existing placement commands own component edits, exact undo/redo and retired transfer reservations. Materialized attached support/light ownership persists under R5-P22/P23 | Lifecycle changes widen placement write sets and require exact before/after state |
| R5-P16 | REVISED. ParentSpace is nullable. Require it only for $parent, rooms, water or other resources requiring authored context. Roomless rigid prefabs work in spaceless maps under OA28 | Parent-dependent local editing needs explicit context. No inferred exterior |
| R5-P17 | REVISED for review 2. Closure-aware preparation receives the verified matching closure through Stage. Native history accepts only after outer publication. Generic callbacks use R5-P21 complete snapshots, focused commands keep named write sets and collider swaps keep matching closures | Two explicit publication modes and local-only compatibility need focused regressions |
| R5-P18 | REVISED for review 2. Light payload 1 position/radius are metres, with stable implicit per-placement emissions under R5-P23 as well as explicit children. Marker fields remain XYZ/yaw/name/role/enabled/tags | Future profiles, source-unit payloads or gameplay metadata require versioned extensions |
| R5-P19 | A remove override retains the previous typed override. Restore reinstates it and its reservation, so removal does not discard customization | Retained override data and its restore dependencies enlarge the root/closure |
| R5-P20 | A missing-Y prefab leaf transforms its typed support request, samples the effective world floor once through R2 and installs that world Y directly. No rescaling of the sampled height | Another internal resolver entry point and a focused support-binding regression |
| R5-P21 | Controller ruling. Accepted generic native callbacks use fully captured before/after documents and complete-document publication, full validation and retained reservations. Named write-set isolation stays on focused commands | Whole snapshots cost bounded copies and need every semantic root in clone/publication |
| R5-P22 | Controller ruling. At unpack, the transfer registry records placement-attached support as materialized under transferred Surface/Record IDs. Standalone expansion reuses those descriptors and suppresses new materialization. Shared asset bytes never change | Persisted associations and retire/restore/remap must remain consistent with standalone ownership |
| R5-P23 | Controller ruling. LightResourceIds produce implicit Light children, scoped, bound, charged and transferred like support children, using a per-placement key grammar. Explicit children coexist without duplicate ownership | More retained child bindings and attachment lifecycle state. No rendering scope is added |
| R5-P24 | Controller ruling. Prefab payloads carry asset-local materialization rows with local placement/child references and captured precise frames. Extract and local Export preserve edited descriptors, and planner/resolver suppression precedes implicit support/light enumeration | Codec, visitor, identity, preflight and local editing must transport the same one-producer disposition |

## File structure

All paths are repository-relative. New files below are created by the owning task, not claimed as released APIs.

| Area | Files and responsibility |
| --- | --- |
| Prefab payload | MapDoc `MapDocument.Prefabs.cs`, `Prefabs/MapPrefabRecords.cs`, `MapPrefabChildren.cs`, `MapPrefabOverrides.cs`, `MapPrefabJson.cs`, `MapPrefabFile.cs`, `MapPrefabMigration.cs`, `MapPrefabValidation.cs` |
| Stable bindings and attachment ownership | MapDoc `Prefabs/MapPrefabBindings.cs`, `Identity/MapPrefabIdentity.cs`, `Prefabs/MapPrefabReferences.cs`, `MapPrefabTransfers.cs`, `MapPrefabLocalMaterializations.cs`, `MapPrefabMaterializationOverrides.cs`, `MapEffectiveAttachments.cs`. Extend root persistence, bound validation and both identity paths |
| One effective view | MapDoc `Prefabs/MapPrefabResolver.cs`, `MapEffectiveResources.cs`, `MapResourceTransform.cs`, `MapEffectiveTransform.cs`, `MapPrefabPreflight.cs`, `Storage/MapEffectiveSurfaceSource.cs`, `MapEffectiveAcquisition.cs`, `Surfaces/MapEffectiveGeometry.cs` |
| Rooms in R2 | MapDoc `Spaces/MapPrismDomain.cs`, `MapPrismGeometry.cs`, `MapOccupiedPartitions.cs`, `MapPrismCoverage.cs`. Extend membership, intervals, support and topology codecs |
| Local support and statics | MapDoc `Surfaces/MapSupportResource.cs`, MapDoc `Prefabs/MapEffectiveSupport.cs`, MapDoc.Physics `MapPrefabSupportBounds.cs` and `MapBackendPoseAdapter.cs`. Extend terrain/placement builder, residency and nav paths |
| Paint | MapDoc `Surfaces/MapPaintRegions.cs`, `MapPartialPaintCompiler.cs`. Preserve physical triangles and publish draw-only regions |
| Local water | MapDoc `Water/MapEffectiveWater.cs`, `MapWaterOccupiedPieces.cs`. Adapt R4 compile, query, containment, bound reads and digests |
| View intent | MapDoc `Interiors/MapRoofIntent.cs`, `MapRoofPolicy.cs`, `Prefabs/MapLightFile.cs`, `MapEffectiveAnnotations.cs`, `MapPrefabAttachedLights.cs` |
| Authoring | MapDoc `Editing/MapPrefabEdits.cs`, `MapPrefabEditPreparation.cs`, `MapInteriorEdits.cs`, `MapPrefabExtraction.cs`. Standalone `Interiors/MapStoreyRecords.cs`. MapEditor `NativeCompleteSnapshotCommands.cs`, `NativePlacementResourceState.cs`, closure-aware commands and lifecycle in Task 10 |
| ke-mapedit | MapEdit.Tool `PrefabService.cs`, `PrefabAssetPublisher.cs`, `MutationService.Prefabs.cs`, `Tools/PrefabTools.cs`, `Tools/InteriorTools.cs` |
| Public proofs | MapDoc.Tests `Prefabs/`, `Spaces/`, MapDoc.Physics.Tests `MapPrefab*.cs`, MapEditor.Tests `MapDoc/NativePrefab*.cs`, `MapEditTool/Prefab*.cs` |
| Private comparison | OracleHarness.Tests `ShippedBuildingInputs.cs`, `ShippedBuildingComparison.cs`, `ShippedBuildingComparisonTests.cs`, synthetic guards and README |

Prefixes mean the existing `KhaozEngine.<prefix>/` projects. Each task expands its actual paths. Do not grow baselined EditorCommands.cs, EditorTool.cs or scene test files. Focused new files stay below 800 lines. Overload hooks go into existing small types or cohesive partial files.

## Dependencies and estimate

| Task | Deliverable | Depends on | Engineer-days |
| --- | --- | --- | --- |
| 1 | Format 7, closed scoped payload, storeys and closure | released base | 5 to 7 |
| 2 | Bindings, references, identity and native pin migration | 1 | 5 to 8 |
| 3 | Precise effective view, wide queries and bounded preflight | 2 | 8 to 12 |
| 4 | Prism rooms and identified occupied fragments | 3 | 8 to 12 |
| 5 | Pure support installation, shapes, bounds and tiles | 4 | 5 to 8 |
| 6 | Partial-face paint and publication refusal | 3, 5 | 3 to 5 |
| 7 | Fragment-aware transformed local water | 4, 5 | 6 to 9 |
| 8 | Explicit roof inventory, groups, lights and markers | 4, 7 | 4 to 6 |
| 9 | Pure edits, extraction and materialized attachment unpack | 2 to 8 | 6 to 9 |
| 10 | Bound preparation, complete snapshots and placement lifecycle | 9 | 7 to 10 |
| 11 | Asset session, generic callback history, publication and tools | 10 | 8 to 12 |
| 12 | Public synthetic village and two-head acceptance | 11 | 2 to 3 |
| 13 | Private closure comparison and guard tests | 12 | 1 to 2 |
| | Task subtotal | | 68 to 103 |
| | Reviews, release preparation and integration | all | 4 to 6 |
| | Total | | 72 to 109 |

Revision 4 assigns the asset-local disposition to Tasks 2/3, complete materialized re-extraction to Task 9, accepted precise-state coalescing to Task 10 and local export transport to Task 11. Their increases are respectively 1 to 2, 1, 1 to 2, 1 and 1 engineer-days, raising the revision-3 total of 67 to 102 to 72 to 109. Other ranges and the thirteen serial gates are unchanged. These are indicative scope estimates, not measured person-hours or firm bounds.

R3 took about 14 h and R4 about 9 h from approval to tag. Keep an unverified R5 forecast of 18 to 34 h elapsed agent time after plan approval, plus plan review time. Owner response, subscription resets, private inputs, concurrent-main reconciliation and release CI extend calendar time. Engineer-days and Git spans are not wall-clock telemetry. At R5 exit refresh R6 to R11, currently 3 to 6 calendar weeks, and G1 to G5, another 1 to 2 weeks plus owner playtest.

## Dispatch and verification conventions

The controller copies Global Constraints, proposed rulings as accepted or corrected, this block, the shared fixture contract and consumed Interfaces blocks into every bounded worker brief. Supply the approved gate and relevant ledger rulings. Execute serially with a fresh reviewer per task, following the session's Gauge placement rules. The plan-writing brief authorizes no helper. Execution tasks have serial independent review gates: Task 9 pure preparation, Task 10 publication/effects and Task 11 session/history/publication each must pass before the next is dispatched.

Execution workers start at the assigned accepted commit in this worktree and branch. The controller pins that commit in the brief. The initial writer guard was HEAD `06b734c57`. Revision 5 starts at `9b3349ee9` on the same branch and adds a new docs-only commit. Released signatures still refer to `06b734c57`.

```bash
cd /Users/antonio/KhaozEngine/.worktrees/wa-r5-free-buildings
test "$(git branch --show-current)" = "feature/wa-r5-free-buildings"
wa_r5_log_dir="/tmp/grimhollow-orch/logs/wa-r5-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p "$wa_r5_log_dir"
MAPDOC=KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj
MP=KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj
EDITOR=KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj
ORACLE=KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj
RUMP=KhaozEngine.Tests/KhaozEngine.Tests.csproj
wa_run() {
    local n=$1 rc
    shift
    if build-slot --label "wa-r5-$n" -- "$@" >"$wa_r5_log_dir/$n.log" 2>&1
    then rc=0
    else rc=$?
    fi
    tail -25 "$wa_r5_log_dir/$n.log"
    echo "exit $rc for $n"
    return "$rc"
}
wa_test() {
    wa_run "$1" dotnet test "$2" -c Release -m:1 --logger "trx;LogFileName=$1.trx" --results-directory "$wa_r5_log_dir" --filter "$3"
}
wa_finish() {
    local n=$1
    shift
    local path
    local cs_paths=()
    for path in "$@"
    do
        case "$path" in *.cs) cs_paths+=("$path") ;; esac
    done
    if [ "$n" = t13 ]
    then
        wa_run "$n-oracle-format" dotnet format "$ORACLE" --verify-no-changes --no-restore || return $?
    fi
    wa_run "$n-format" dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --include "${cs_paths[@]}" &&
    wa_run "$n-dashes" sh scripts/check-dashes.sh --tree &&
    wa_run "$n-prose" sh scripts/check-prose.sh --tree &&
    wa_run "$n-size" sh scripts/check-file-size.sh --tree &&
    wa_run "$n-agent" sh scripts/check-agent-instructions.sh --tree &&
    wa_run "$n-docs" bash scripts/check-doc-versions.sh &&
    git diff --check
}
```

Create the worktree's empty local-feed before restore if nuget.config requires it. Red needs named assertions failing for the stated reason, or compile errors exclusively for new task-owned APIs. A malformed fixture, missing released signature or unrelated compilation failure is not red evidence. Green needs exit 0, zero failures and a nonzero matching count from the TRX/log. Zero matches fails. Run each command once, never poll tests. Exit 75 means build-slot ran nothing, so retry later. Product changes or a genuine new failure justify another focused check, not a full suite loop.

Every task ends with its explicit path array and wa_finish below, which selects C# paths for format. Add any fix-round paths explicitly before staging. Repair formatting through wa_run and dotnet format on that same explicit C# list, then verify. Task 13 also formats its outside-solution project. Guard exit 0 and git diff --check precede each explicit-path commit. Plan-writing verification is limited to the four doc guards in its dispatch contract and never runs this execution block.

## Shared fixture contract

Create `KhaozEngine.MapDoc.Tests/Prefabs/PrefabFixtures.cs` as `internal static partial class PrefabFixtures`. Extend by concern in files ending Fixtures.cs, which MapEditor.Tests already links. Editor-only helpers stay in MapEditor.Tests. Physics helpers stay in MapDoc.Physics.Tests without a project reference to MapDoc.Tests. Task 5 links public fixture source explicitly for Physics.Tests. Task 12 consumes those links.

- Task 1 creates `internal sealed record PrefabFixture(MapDocument Document, MapAssetClosure Assets)`, `PrefabFixture Create(string variant = "basic")`, `MapResolveOptions Options()` returning builder public-prefab-fixture, version 1, options hash fixture-options/1, resolver 2, and `string TempDirectory()`.
- Task 1 creates `string FormatSixJson(bool analytic)`, valid without R5 fields, `byte[] AssetBytes(string variant)` for payload failures and `MapAssetClosure LoadAssets(byte[] prefabBytes, bool refreshDigest)`. Changed bytes without refresh fail closure verification.
- Task 2 creates `MapStoredSurfaceSource Stored(MapDocument document)`, repeating the save/open pattern locally, and `IReadOnlyList<MapPrefabBinding> LiveBindings(MapDocument document)`.
- Task 3 creates `MapEffectiveResources Effective(PrefabFixture fixture)`, `MapFramePoint Point(MapEffectiveResources resources, float x, float y, float z)` in the acquired frame, and `PrefabFixture Transformed(float yaw, float scale, Vector3 position)`. Transformed uses the all-resources variant, including the pool and a room/floor/water shared corner.
- Task 4 adds `PrefabFixture Cave(string variant)`: stacked, transformed, dry-flooded, missing-parent, peer-overlap, adjacent, uneven-parent and reflex-storeys. Query actual membership and intervals, never a fixture classifier.
- Task 10 creates editor-only `PrefabEditorFixtures.History(MapDocument document, MapAssetClosure assets) -> EditorHistory` and SupportReseat with actual commands/injected bounds. Task 11 extends it with `Session(PrefabFixture fixture) -> MapEditSession`, `Document(MapEditSession session) -> MapDocument` and fault publication/state snapshots using actual session/history entry points.

The basic synthetic prefab is hut, instance a, on an explicitly authored 16 by 16 m exterior at y -2 with negative-Z rows. It has an elevated 4 by 4 m support floor at y 0, ceiling at y 3, a doorway compound with 1.2 m clear width, a selectable item 0.2 m high, room room, storey lower, light lamp, marker entry, roof roof and a roof link. Variant pool is a separate room with floor -1, ceiling 3 and local water level 0.5. Covers, portal records and material closure are explicit. Non-placement bindings have no numeric ID. Placement IDs are supplied by fixtures, reserved above high-water mark 100.

All-resources includes exactly one pool body and marker entry at local (0,1,0), role entrance, enabled true and ordered tags first/second. Floor/room/water share XZ corner (4,-4). Their different vertical coordinates remain explicit. For transformed variants the fixture authors a parent exterior at the translated site's floor level and footprint, so deep/far placements are valid rather than outside an unchanged y -2 parent.

The far transform is (30000.25, -450, -29999.75), yaw 0.371 and scale 1.2. Near transforms use (10.25, 5, -3.125), yaw 0.371, scales 0.8 and 1.2. All variants are invented public geometry. None copies private buildings, leaf IDs or coordinates.

### Task 1: Format 7, payload 1 and verified prefab closure

**Files:**
- Create: `KhaozEngine.MapDoc/Prefabs/MapEffectiveTransform.cs`, `KhaozEngine.MapDoc/Interiors/MapStoreyRecords.cs`, `KhaozEngine.MapDoc/MapDocument.Prefabs.cs`, and `Prefabs/MapPrefabRecords.cs`, `MapPrefabChildren.cs`, `MapPrefabOverrides.cs`, `MapPrefabJson.cs`, `MapPrefabFile.cs`, `MapPrefabMigration.cs`, `MapPrefabValidation.cs`, `MapPrefabTransfers.cs` under `KhaozEngine.MapDoc/`.
- Modify: `KhaozEngine.MapDoc/MapDocumentFile.cs:34-46` and `:85-95`, `MapCanonical.cs:51-108`, `MapTiledFile.cs:54-89` and `:205-226`, `MapDocumentMembers.cs:19-65`, `MapDocumentSchema.cs:1-35`, `mapdoc.schema.json`, `Assets/MapAssetManifestReader.cs:78-85`, `Assets/MapAssetClosure.cs:75-98`, `MapDocumentValidator.cs`, `README.md`.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabFormatTests.cs`, `MapPrefabClosureTests.cs`, `PrefabFixtures.cs` in that directory. Update `NativeAssetClosureTests.cs:149` to test unsupported prefab payload rather than the reserved role. Current-format assertions in `FormatAdvanceTests.cs` and `NativeDocumentTests.cs` use CurrentFormatVersion, preserving historical input bytes.

**Interfaces:**
- Consumes released `MapDocumentFile.LoadText(string json, MapDocumentLoadOptions? options = null, string? sourcePath = null)`, `SaveText(MapDocument doc, MapDocRegistry? registry = null)`, `SaveTiled(MapDocument doc, string directory, MapDocRegistry? registry = null, MapDocumentSaveOptions? save = null)`, `LoadTiled(string directory, MapDocumentLoadOptions? options = null)`, and `MapDocumentLoadOptions.RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> step)`.
- Consumes `MapAssetClosure.Load(IReadOnlyList<MapAssetRef> roots, IMapAssetSource source)` at Assets/MapAssetClosure.cs:46 and `MapAssetRef(string Id, string Path, string Sha256, int PayloadVersion)` at MapNativeDocument.cs:39. MapResourceKind.Prefab already exists.
- Produces in MapDoc.Prefabs `MapPrefabSet(int PayloadVersion, int ExpansionVersion, IReadOnlyList<MapPrefabInstance> Instances, IReadOnlyList<MapPrefabBindingTransfer> Transfers)`, with deep `Clone() -> MapPrefabSet` and canonical `Digest() -> string`. Adds init-only `Attachments: IReadOnlyList<MapPrefabAttachmentTransfer>`, default empty, part of that same persisted transfer registry.
- Produces `MapPrefabInstance(string Id, string ResourceId, string ResourceSha256, MapTransform Transform, MapRecordRef? ParentSpace, MapPrefabBindings Bindings, IReadOnlyList<MapPrefabOverride> Overrides)`, with init-only bool Removed default false, and `MapPrefabAsset(int PayloadVersion, string Id, IReadOnlyList<MapPrefabChild> Children)`. Asset adds init-only `Roofs: IReadOnlyList<MapPrefabReference>`, default empty, explicitly inventorying local or world roof placements. No extra child kind or ID reservation is implied by an inventory entry. Task 2 adds its asset-local MaterializedAttachments disposition under R5-P24.
- Produces `MapPrefabChildKey(MapPrefabChildKind Kind, string Key)`, enum kinds `Placement, Surface, Record, Storey, WaterMedium, WaterBody, Light, Marker, RoofLink, RoofGroup`, and abstract `MapPrefabChild(MapPrefabChildKey Key)`.
- Closed derived child constructors take Key followed by these arguments: `MapPrefabPlacementChild(MapPlacement Value)`, `MapPrefabSurfaceChild(MapSurfaceRef Surface, IReadOnlyList<MapSurfacePatch> Patches)`, `MapPrefabRecordChild(MapTopologyRecord Value, MapPatchKey Anchor)`, `MapPrefabStoreyChild(string Name)`, `MapPrefabMediumChild(MapWaterMediumDoc Value)`, `MapPrefabWaterChild(MapWaterBodyDoc Value)`, `MapPrefabLightChild(MapLocalLightDoc Value)`, `MapPrefabMarkerChild(MapLocalMarkerDoc Value)`, `MapPrefabRoofChild(MapRoofLinkDoc Value)`, `MapPrefabRoofGroupChild(MapRoofGroupDoc Value)`. The constructor includes Key as its first parameter in every case. The base child adds init-only `MapTransform LocalTransform`, default identity, and `IReadOnlyList<MapPrefabReferenceSlot> References`, default empty. Placement children must leave it identity and use MapPlacement's own transform, avoiding double application.
- Produces `MapPrefabBinding(MapPrefabChildKey Child, string WorldId, long? NumericId, bool Removed)`, `MapPrefabBindings(IReadOnlyList<MapPrefabBinding> Entries)`, `MapPrefabBindingTransfer(string InstanceId, MapPrefabBinding Binding)`, adding init-only bool Retired default false. A retired transfer reserves its ID but has no live standalone owner.
- Produces in new Prefabs/MapPrefabTransfers.cs `MapPrefabAttachmentKind { Support, Light }`, `MapPrefabAttachedChild(MapPrefabChildKey LocalChild, string WorldId, MapPatchKey? Anchor, MapEffectiveTransform OwnerLocalPose, string SourceDigest)` and `MapPrefabAttachmentTransfer(string PlacementId, string ResourceId, string ResourceSha256, MapPrefabAttachmentKind Kind, IReadOnlyList<MapPrefabAttachedChild> Children, bool Retired)`. Task 9 creates materialized rows, Task 10 owns their lifecycle. Clone and schema omit empty attachment lists and false Retired defaults, preserving absent-R5 projections. Rows are keyed by (PlacementId, Kind, ResourceId), cloned/serialized/canonicalized once under Prefabs.Attachments and checked on loaded/stored identity paths.
- Produces abstract `MapPrefabOverride(MapPrefabChildKey Child)` and variants `MapPrefabRemoveOverride(Child, MapPrefabOverride? Previous = null)`, `MapPrefabReplaceOverride(Child, MapPrefabChild Value)`, `MapPrefabPlacementOverride(Child, MapTransform? Transform, string? AssetId, IReadOnlyList<string>? Tags)`, `MapPrefabTransformOverride(Child, MapTransform Transform)`, `MapPrefabAddOverride(Child, MapPrefabChild Value)`. Child means MapPrefabChildKey in every signature. A transform override updates LocalTransform, or MapPlacement's transform for a placement child. For materialized children, Task 3 first transfers that typed intent to an effective PlacementFrame or OwnerLocalPose in binary64. Presentation LocalTransform becomes identity only after transfer, on the detached effective copy. Previous cannot itself be a remove override. Restore reinstates Previous if present and retains the binding. There is one effective override per child key.
- Produces typed `MapPrefabReference(MapPrefabReferenceScope Scope, MapPrefabChildKind Kind, string Id)`, scopes Local and World. Produces `MapPrefabReferenceSlot(string Path, MapPrefabReference Target)`. This prefab-only metadata scopes every R2 reference position. Released standalone MapRecordRef, MapPatchKey and support DTOs keep their semantics. Task 2 pins the complete closed occurrence grammar and remapping.
- Produces `MapLocalLightDoc(int PayloadVersion, string Id, MapPrefabReference? Owner, Vector3 Position, Vector3 Colour, float Intensity, float Radius, bool CastShadows)`, `MapLocalMarkerDoc(int PayloadVersion, string Id, string Name, Vector3 Position, float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags)`, `MapRoofLinkDoc(string Id, MapPrefabReference Roof, IReadOnlyList<MapPrefabReference> Rooms, IReadOnlyList<MapPrefabReference> Groups, float AboveY)`, `MapRoofGroupDoc(string Id, IReadOnlyList<MapPrefabReference> Rooms)`.
- Produces `MapBuildingIntent(int PayloadVersion, IReadOnlyList<MapRoofLinkDoc> RoofLinks, IReadOnlyList<MapRoofGroupDoc> RoofGroups, IReadOnlyList<MapLocalLightDoc> Lights, IReadOnlyList<MapLocalMarkerDoc> Markers)`, deep `Clone() -> MapBuildingIntent` and canonical `Digest() -> string`. It adds init-only `Roofs: IReadOnlyList<MapPrefabReference>` and `Storeys: IReadOnlyList<MapStoreyDoc>`, default empty. Roof inventory is independent of concealment links.
- Produces `MapPrefabExpansionLimits(int MaxInstances = 65536, int MaxChildren = 1048576, int MaxReferenceEdges = 262144, int MaxPatches = 1048576, int MaxRecords = 4194304, int MaxGeometryElements = 16777216, int MaxBindings = 2097152, int MaxOverrides = 1048576, int MaxTransfers = 1048576, long MaxPayloadBytes = 268435456)`. Task 1 codecs use these finite collection ceilings before materialization, Task 3 charges aggregate effective expansion.
- Produces the persistence DTO `MapEffectiveTransform(double X, double Y, double Z, double YawRadians, double Scale)` in Prefabs/MapEffectiveTransform.cs. Task 3 adds composition methods and geometric policy.
- Produces `MapStandaloneResourceTransform(MapPrefabChildKind Kind, string OwnerId, MapEffectiveTransform Transform, string SourceDigest)`. MapDocument gains nullable `MapPrefabSet? Prefabs`, `MapBuildingIntent? BuildingIntent` and `IReadOnlyList<MapStandaloneResourceTransform>? NativeResourceTransforms`, omitted when absent. The transform registry supports unpacked surfaces, topology, water and annotations without a prefab instance.
- Produces in new `Interiors/MapStoreyRecords.cs` `MapStoreyDoc(int PayloadVersion, string Id, string Name, string? InstanceId)`, payload 1. Standalone BuildingIntent.Storeys owns unpacked storey IDs/names. Room StoreyId must resolve to one storey with matching instance ownership, or to a standalone storey after unpack. Persist, clone and hash the collection on both loaded/stored paths.
- Produces `MapPrefabFile.Read(ReadOnlyMemory<byte> bytes, string resourceId) -> MapPrefabAsset`, `Write(MapPrefabAsset asset) -> byte[]`, `MapPrefabMigration.Upgrade(JsonObject source) -> JsonObject`, `MapPrefabValidation.Validate(MapDocument document, MapAssetClosure? assets) -> IReadOnlyList<string>`.

- [ ] **Step 1: Write failing tests with these assertions.**

```csharp
[Fact] public void SixToSeven_IsPureAndAnalyticExecutionUnchanged()
{
    var old = JsonNode.Parse(PrefabFixtures.FormatSixJson(true))!.AsObject();
    var upgraded = MapPrefabMigration.Upgrade(old);
    Assert.Equal(7, upgraded["formatVersion"]!.GetValue<int>());
    Assert.Null(upgraded["prefabs"]);
    Assert.Null(upgraded["buildingIntent"]);
    old["formatVersion"] = 7;
    Assert.True(JsonNode.DeepEquals(old, upgraded));
    Assert.Null(MapDocumentFile.LoadText(upgraded.ToJsonString()).ResolverIdentity);
}
[Fact] public void PayloadAndRoot_RoundTripMonolithicAndTiled()
{
    var f = PrefabFixtures.Create();
    var text = MapDocumentFile.SaveText(f.Document);
    Assert.Equal(text, MapDocumentFile.SaveText(MapDocumentFile.LoadText(text)));
    var dir = PrefabFixtures.TempDirectory();
    MapDocumentFile.SaveTiled(f.Document, dir);
    Assert.Equal(f.Document.Prefabs!.Digest(), MapDocumentFile.LoadTiled(dir).Prefabs!.Digest());
    Assert.Equal(1, MapPrefabFile.Read(PrefabFixtures.AssetBytes("valid"), "hut").PayloadVersion);
}
[Theory]
[InlineData("future-payload")] [InlineData("unknown-member")]
[InlineData("duplicate-member")] [InlineData("null-children")]
[InlineData("nested-instance")] [InlineData("duplicate-child")]
public void ClosedPrefabPayload_Refuses(string variant) =>
    Assert.Throws<MapDocumentException>(() => MapPrefabFile.Read(PrefabFixtures.AssetBytes(variant), "hut"));
[Fact] public void ChangedBytesRequireNewDigest_AndCyclesRefuse()
{
    Assert.Throws<MapDocumentException>(() => PrefabFixtures.LoadAssets(PrefabFixtures.AssetBytes("changed"), false));
    Assert.NotEqual(PrefabFixtures.Create().Assets.Hash,
        PrefabFixtures.LoadAssets(PrefabFixtures.AssetBytes("changed"), true).Hash);
    Assert.Throws<MapDocumentException>(() => PrefabFixtures.Create("cyclic-closure"));
}
```

Add `FormatEight_Refuses`, `AnalyticResolution_MatchesRecordedFormatFourBits` against the committed FormatFour recorder expectations, and `UnknownPrefabMaterialColliderLightOrSurface_RefusesBeforeBuild` per wrong-kind reference. Exact int64 strings beyond 2^53 and ordered tags round-trip. The first fixture uses existing lattice spaces. Task 4 replaces its room domain with the new prism when that type exists.

- [ ] **Step 2: Run red** `wa_test t1-red "$MAPDOC" "FullyQualifiedName~MapPrefabFormatTests|FullyQualifiedName~MapPrefabClosureTests"`. Expect missing new APIs or the released Prefab-role refusal.
- [ ] **Step 3: Implement these codecs, schema, closure and migration.**

Register 6 to 7 after MapWaterMigration. Upgrade clones input, changes formatVersion only and handles already-upgraded input as existing migration helpers do. Closed prefab/annotation objects refuse missing, duplicate, case-variant and unknown fields, undefined enum strings, nonfinite transforms and nonpositive scale. Nested instances refuse. Manifest Prefab resources require payload 1. They are resources, not mesh-bearing MapAssetDoc variants. NativeAssets stays manifest roots.

Load verifies bytes once, validates the graph, decodes bounded verified immutable Prefab snapshots and validates all leaf references through the closure. Payload resource edges must appear in declared dependencies. Missing/wrong-kind references refuse before build. A prefab has no mesh of its own. Material/collider/LOD/light dependencies stay on leaves.

The Surface child Patches must have empty Records. Every authored topology record is a separate Record child with a declared local anchor. If a Record has its own LocalTransform, all referenced local geometry must use that same effective transform or an explicit World reference. Refuse inconsistent local anchor/geometry poses rather than silently selecting one. Support-resource payload records are the only embedded exception and get implicit bindings in Task 2. Reject unscoped or duplicate reference slots, unsupported paths and payload ID/key inconsistencies. Scope never follows text matching.

Present Prefabs, BuildingIntent or NativeResourceTransforms requires resolver identity (1,2) and AuthoredBindingsV2. Add `PrefabSections_RefuseResolverOneBeforeAnyCallback`. Absent sections keep analytic and resolver-1 behavior. Binding and light IDs use the released decimal-string converters wherever a numeric ID is present.

MapCanonical.WriteGlobals writes nonnull Prefabs, BuildingIntent and NativeResourceTransforms once through canonical codecs, so both OfWorld and native root identity cover them. WriteNativeGlobals must not write them again. Tiled root reading and GlobalsOnly preserve all three. Instances, bindings, transforms and overrides sort ordinal by typed owner/key, preserving tag order. Never persist expanded duplicates in placements or tile copies. ParentSpace is nullable and may be omitted in JSON for a parent-independent instance. Add `RoomlessParentNull_RoundTripsAndRoomParentNullRefuses`: valid roomless payload round-trips monolithic/tiled with no manufactured space, while a room/$parent-bearing instance fails context validation. Structural validation accepts complete supplied bindings and never allocates. Task 2 strengthens identity/reservations. Update format documentation, not the engine version.

- [ ] **Step 4: Run green** with Step 2's filter as `t1-green`, then `wa_test t1-regress "$MAPDOC" "FullyQualifiedName~FormatAdvance|FullyQualifiedName~FormatFour|FullyQualifiedName~NativeDocument|FullyQualifiedName~NativeAssetClosure|FullyQualifiedName~Tiled"`. Expect all cases passing and the same recorded analytic transforms/callbacks.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t1_paths=(
    KhaozEngine.MapDoc/Prefabs/MapPrefabTransfers.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveTransform.cs
    KhaozEngine.MapDoc/Interiors/MapStoreyRecords.cs
    KhaozEngine.MapDoc/MapDocument.Prefabs.cs
    KhaozEngine.MapDoc/MapDocumentFile.cs
    KhaozEngine.MapDoc/MapCanonical.cs
    KhaozEngine.MapDoc/MapTiledFile.cs
    KhaozEngine.MapDoc/MapDocumentMembers.cs
    KhaozEngine.MapDoc/MapDocumentSchema.cs
    KhaozEngine.MapDoc/mapdoc.schema.json
    KhaozEngine.MapDoc/MapDocumentValidator.cs
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapDoc/Prefabs/MapPrefabRecords.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabChildren.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabOverrides.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabFile.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabMigration.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabValidation.cs
    KhaozEngine.MapDoc/Assets/MapAssetManifestReader.cs
    KhaozEngine.MapDoc/Assets/MapAssetClosure.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabFormatTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabClosureTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabFixtures.cs
    KhaozEngine.MapDoc.Tests/NativeAssetClosureTests.cs
    KhaozEngine.MapDoc.Tests/FormatAdvanceTests.cs
    KhaozEngine.MapDoc.Tests/NativeDocumentTests.cs
)
wa_finish t1 "${wa_r5_t1_paths[@]}" &&
git add -- "${wa_r5_t1_paths[@]}" &&
git commit --only -m "feat(mapdoc): add format 7 prefab payloads and verified closure" -- "${wa_r5_t1_paths[@]}"
```

### Task 2: Bindings, typed references and both identity paths

**Files:**
- Create: `KhaozEngine.MapDoc/Prefabs/MapPrefabLocalMaterializations.cs`, `MapLightFile.cs`, `MapPrefabAttachedLights.cs`, `KhaozEngine.MapDoc/Surfaces/MapSupportResource.cs`, `Prefabs/MapEffectiveSupport.cs`, `KhaozEngine.MapDoc/Prefabs/MapPrefabBindings.cs`, `MapPrefabReferences.cs`, `KhaozEngine.MapDoc/Identity/MapPrefabIdentity.cs`.
- Modify: `KhaozEngine.MapDoc/MapNativeValidation.cs:117-144`, `MapBoundDocumentValidation.cs:14-74`, `Identity/MapAuthoredIdentityV2.cs:16-48`, `Storage/MapStoredSurfaceSource.Identity.cs:9-14`, `Storage/MapStoredSurfaceSource.Content.cs:17-70`, Task 1's MapPrefabRecords.cs, MapPrefabFile.cs, MapPrefabValidation.cs, MapPrefabJson.cs and mapdoc.schema.json for the asset-local disposition.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabBindingTests.cs`, `MapPrefabIdentityTests.cs`, `PrefabIdentityFixtures.cs`. No editor reference. Modify `KhaozEngine.MapDoc.Physics.Tests/MapPersistedIdentityTests.cs:59-66` for the deliberate native format transition.

**Interfaces:**
- Consumes `MapNumericIds.Reserve(MapDocument document, IEnumerable<long> ids)` and `Allocate(MapDocument document) -> long` at MapNumericIds.cs:12 and :31. They mutate only a detached preparation candidate.
- Consumes `MapAuthoredIdentityV2.Compute(MapDocument document, MapAssetClosure assets, MapResolveOptions options) -> string` and `Compute(MapStoredSurfaceSource source, MapAssetClosure assets, MapResolveOptions options) -> string` at :16 and :31. RootDigest includes FormatVersion.
- Produces in Prefabs/MapPrefabLocalMaterializations.cs `MapPrefabLocalMaterializedChild(MapPrefabReference Child, MapEffectiveTransform OwnerLocalPose, string SourceDigest)` and `MapPrefabLocalMaterialization(MapPrefabReference Placement, string ResourceId, string ResourceSha256, MapPrefabAttachmentKind Kind, MapEffectiveTransform PlacementFrame, IReadOnlyList<MapPrefabLocalMaterializedChild> Children)`. Adds init-only `MapPrefabAsset.MaterializedAttachments: IReadOnlyList<MapPrefabLocalMaterialization>`, default empty, omitted when empty. PlacementFrame is the immutable captured asset-local base before freezing, OwnerLocalPose the immutable captured child-relative base. Task 3 reconciles instance overrides into detached effective frames before resolution. Children references identify captured explicit definitions in Asset.Children. No new child kind or reservation is introduced by a row.
- Produces `MapPrefabLocalMaterializations.Validate(MapPrefabAsset asset, MapAssetClosure assets) -> void`. Placement must be typed Local Placement and each child typed Local Surface/Record for Support or Local Light for Light. Keys, anchors and canonical captured descriptor digests must match the original explicit definitions before overrides. Task 3 refreshes detached descriptor/effective digests after reconciliation, without treating an override as a broken original pin. Rows sort by (Placement.Id,Kind,ResourceId), with child refs in typed ordinal order, and enter payload codec/digest, typed References/Remap and loaded/stored identity. Resource ID/SHA must match the leaf's advertised verified edge. All rows for a placement share identical PlacementFrame. Duplicate edge ownership, world-scoped row refs, wrong kinds and dangling children refuse.
- Produces `MapPrefabBindingPlan(MapPrefabBindings Bindings, long HighWaterMark)`, `MapPrefabBindingPlanner.Plan(MapDocument document, MapPrefabAsset asset, MapAssetClosure assets, string instanceId, MapPrefabBindings existing, bool allocateNumericPlacements = true) -> MapPrefabBindingPlan`, pure.
- Produces `MapPrefabReferences.Remap(MapPrefabAsset asset, IReadOnlyDictionary<MapPrefabChildKey, string> bindings, MapRecordRef? parentSpace) -> MapPrefabAsset`, `RemapWorld(MapDocument document, MapPrefabChildKind kind, string oldId, string newId) -> MapDocument`, pure, and `References(MapPrefabAsset asset) -> IReadOnlyList<MapPrefabReference>`.
- Produces `MapSupportResource(int PayloadVersion, MapSurfaceSet Surfaces, MapRecordRef? Space, IReadOnlyList<MapPrefabReferenceSlot> References)`, `MapSupportResourceFile.Read(ReadOnlyMemory<byte> bytes, string resourceId) -> MapSupportResource`, `Write(MapSupportResource resource) -> byte[]`, in Surfaces/MapSupportResource.cs. Explicit scopes cover every embedded reference. Produces internal `MapEffectiveSupport.Enumerate(MapAssetClosure assets, IReadOnlyList<MapPrefabChild> children, IReadOnlyList<MapPrefabLocalMaterialization>? materialized = null) -> IReadOnlyList<MapPrefabChild>` in Prefabs/MapEffectiveSupport.cs. This pure codec/enumeration is ready before the binding planner runs. Task 3 installs its remapped descriptors and Task 5 adds statics.
- Produces `MapLightFile.Read(ReadOnlyMemory<byte> bytes, string resourceId) -> MapLocalLightDoc`, `Write(MapLocalLightDoc light) -> byte[]` and internal `MapPrefabAttachedLights.Enumerate(MapAssetClosure assets, IReadOnlyList<MapPrefabChild> children, IReadOnlyList<MapPrefabLocalMaterialization>? materialized = null) -> IReadOnlyList<MapPrefabChild>`, pure. Payload 1 encodes Position/Radius in metres. Read validates owner null or typed Local Placement $owner, which enumeration binds to the consuming placement key. Task 8 publishes these bound children as annotations.
- Produces internal `MapPrefabIdentity.Require(MapDocument document, MapAssetClosure assets) -> void`, `RequireStored(MapStoredSurfaceSource source, MapAssetClosure assets) -> void`. Both run identical schema, closure, pin, binding, override and reservation checks without geometric containment.
- Produces internal `MapPrefabIdentity.ChildDigest(MapPrefabChild child) -> string`, the canonical semantic digest of one effective child. Dependency consumers use child/patch digests, not the containing prefab SHA, while whole authored identity includes the verified complete closure.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Theory]
[InlineData(MapPrefabChildKind.Placement)] [InlineData(MapPrefabChildKind.Surface)]
[InlineData(MapPrefabChildKind.Record)] [InlineData(MapPrefabChildKind.Storey)]
[InlineData(MapPrefabChildKind.WaterMedium)] [InlineData(MapPrefabChildKind.WaterBody)]
[InlineData(MapPrefabChildKind.Light)] [InlineData(MapPrefabChildKind.Marker)]
[InlineData(MapPrefabChildKind.RoofLink)] [InlineData(MapPrefabChildKind.RoofGroup)]
public void EveryChildKind_RetainsReservationsAndTransfersOnce(MapPrefabChildKind kind)
{
    var f = PrefabFixtures.Create("all-kinds");
    var asset = MapPrefabFile.Read(f.Assets.GetResource("hut").Bytes, "hut");
    var before = f.Document.Prefabs!.Instances.Single().Bindings;
    var removed = PrefabFixtures.WithChildRemoved(asset, kind);
    var dead = MapPrefabBindingPlanner.Plan(f.Document, removed, f.Assets, "a", before);
    var restored = MapPrefabBindingPlanner.Plan(f.Document, asset, f.Assets, "a", dead.Bindings);
    Assert.Equal(before.Entries.Select(b => (b.Child, b.WorldId, b.NumericId)),
        restored.Bindings.Entries.Select(b => (b.Child, b.WorldId, b.NumericId)));
    Assert.Equal(f.Document.NumericIdHighWaterMark, restored.HighWaterMark);
    Assert.Contains(dead.Bindings.Entries, b => b.Child.Kind == kind && b.Removed);
}
[Theory]
[InlineData("transform")] [InlineData("override")] [InlineData("binding")]
[InlineData("reservation")] [InlineData("roof-group")] [InlineData("asset-version")]
public void EveryAuthoredChange_MovesLoadedAndStoredIdentityTogether(string variant)
{
    var a = PrefabFixtures.Create();
    var b = PrefabFixtures.Create(variant);
    string A(PrefabFixture f) => MapAuthoredIdentityV2.Compute(f.Document, f.Assets, PrefabFixtures.Options());
    Assert.NotEqual(A(a), A(b));
    Assert.Equal(A(b), MapAuthoredIdentityV2.Compute(PrefabFixtures.Stored(b.Document), b.Assets, PrefabFixtures.Options()));
}
[Fact] public void SixToSeven_MovesNativeIdentityEvenWithoutPrefabs()
{
    var f = PrefabFixtures.Create("native-without-prefabs");
    Assert.NotEqual(PrefabFixtures.FormatSixRootDigest(f.Document), MapSurfaceSemantics.RootDigest(f.Document));
    Assert.Null(f.Document.Prefabs);
}
```

This task adds `WithChildRemoved(MapPrefabAsset asset, MapPrefabChildKind kind) -> MapPrefabAsset` and `FormatSixRootDigest(MapDocument document) -> string` in PrefabIdentityFixtures.cs. The latter computes the released root projection with format 6 from a detached copy, not an updated golden copied from product output. Add tests `MissingOrDuplicateBindings_RefuseOnBothPaths`, `ReorderingAndRepacking_PreserveIdentity`, `NumericExhaustion_PublishesNothing` and `TypedRemap_DoesNotTouchTagsAssetIdsOrNames`. Transfers are validated here and exercised through unpack in Task 9. Add `SameTextLocalAndWorldReferences_RemapOnlyTheLocalOccurrence`: local Surface ground and world Surface ground coexist, with paint and bound references using explicit different slots. Assert local IDs/anchors remap and world ones remain ground. Add `ImplicitLeafLightKeys_RetainBindingsAcrossRemoveRestore`, `AssetLocalMaterializations_RoundTripScopeIdentityAndSuppression`, `AttachmentRows_ValidateLoadedAndStoredIdentity` and `EmbeddedSupportRecord_RemoveRestoreHasOneBinding` and `ExplicitSurfaceEmbeddedRecords_Refuse`.

Task 2 owns `StackedCaveBuildHash_IsPinned` in MapPersistedIdentityTests.cs. Reproduce format-6 authored `436a2731a92851e05bc667b3d02bb89e2980922d894348a704f950df55b78235` and build `655026f34e7bc1dd9a93157d406c28547b8b3d832fec1dc041a7e87ad1b0efc3` using a detached format-6 root projection. Independently derive the new format-7 root/authored/build composite from canonical bytes, declared policies and recorded independent chunk inputs. Never paste hashes printed by the changed product. Explain the single format-field change beside the new pins, as R4 did. Existing analytic build pins remain byte-identical.

- [ ] **Step 2: Run red** `wa_test t2-red "$MAPDOC" "FullyQualifiedName~MapPrefabBindingTests|FullyQualifiedName~MapPrefabIdentityTests"`. Expect missing planner/visitor, incomplete reservation checks or identity omissions.
- [ ] **Step 3: Implement the planner, visitor and shared validation.**

Plan on a detached reservation candidate in sorted (kind,key) order. Existing keys reuse bindings. Missing asset keys become Removed and remain reserved. Restore reactivates that reservation. New placement leaves allocate above imported numeric IDs and retained high-water mark. Other child types reserve string IDs only. New world IDs are `prefab/<escaped-instance>/<kind>/<escaped-key>`, with percent escaping of every UTF-8 byte outside ASCII letters/digits/hyphen/underscore and uppercase hex. Imported IDs are accepted exactly. Refuse collisions before publishing any allocation.

The closed slot grammar is the canonical JSON pointer emitted by a typed field visitor, never an arbitrary pointer evaluator. Its permitted positions are PaintTargetSurfaceId and IndoorSpan.ParentSpace, patch keys and vertex owners, seam/chain/strip owners and anchors, portal/opening From/To/boundary references, space Parent/AliasOf/Walls/Portals/Links, bounds and indoor spans, placement support owner/surface/space, water covers/medium and roof/group/light references. Repeated positions use decimal array indices without leading zeros. The codec derives the complete occurrence set from the concrete DTO type and requires exactly one slot per reference occurrence, including anchor SurfaceId. Unknown or unused slots refuse. Definition IDs and display fields are not occurrences. Existing annotation MapPrefabReferences carry scope directly and do not get duplicate slots.

Only these DTO paths and their recursively typed leaves are permitted. JSON path segments use codec camelCase names, child roots value/surface/patches/anchor and indexed lists. Every MapRecordRef consumes slots for id and anchor/surfaceId, with matching scope and the declared anchor. Every MapPatchKey and MapLatticeVertex consumes its surfaceId slot. Definition IDs and lattice numeric coordinates are not reference slots.

| DTO position | Scoped reference occurrences |
| --- | --- |
| MapSurfaceRef/MapSurfacePatch | PaintTargetSurfaceId, IndoorSpan.ParentSpace, patch Key and CornerDependencies.Owner.Patch |
| Seam/chain/strip | First/Second edge Patch/From/To, Pairs vertex owners, SourcePatch, Vertices.Vertex, LowerChain/UpperChain |
| CavePortal/opening/link | FromSpace/ToSpace, Interval vertex owners, BandBottom/BandTop, opening Patch, UpperSpace/LowerSpace, Openings/Portals/GeometryOwners |
| Space/footprint | Parent/AliasOf, Walls/Portals.Record, Links, Space, Lattice, Lower/Upper.SurfaceId or Opening |
| Placement support | SupportBinding.SurfaceId/SpaceId, with reference kind selected by MapSupportBinding.Kind |
| Water | Medium string is kind WaterMedium, Covers are Record references |
| Prism | Space/Parent Record references and StoreyId kind Storey. InstanceId is derived instance ownership, never author-guessed |
| New annotations | Owner, Roof, Rooms, Groups and roof inventory already carry MapPrefabReference scope directly |

For every explicit kind, Key.Key equals its payload definition ID (Surface.Id, Record.Id, WaterMedium.Key, WaterBody.Id, light/marker/link/group Id). Placement uses its MapPlacement.Id. Storey has no embedded ID and uses Key.Key directly. A Record's Anchor names a declared Surface child ID and existing local patch coordinates. Local record references must name that key and exactly its declared anchor. Local surface/owner references name the matching typed child, with local patch coordinates preserved. World references preserve their supplied ID and world anchor exactly. Local $parent is valid only for a Record parent/space reference and substitutes the supplied ParentSpace. A missing required parent refuses before allocation. Same text never chooses a scope.

R5-P24 rows suppress their (local placement,kind,resource) edge before either implicit enumerator or the binding planner discovers children. Captured explicit children participate once in ordinary scope/remap/binding rules. An empty row suppresses the whole edge. Shared leaf advertisements/bytes remain intact, and captured keys may match the implicit grammar only because that edge is suppressed. The visitor remaps row refs with the child refs, never guesses ownership from text.

Implicit Light keys are `<placement-key>/<light-resource-id>/<payload-light-id>` in kind Light. Both planner and resolver enumerate every leaf LightResourceIds edge using the verified payload, replace only $owner with the consuming placement, and persist one binding per consuming prefab leaf. Ordinary standalone leaves without transfer rows use deterministic typed IDs from placement/resource/light ID, like standalone support, with no load-time or numeric allocation. Materialized unpacked rows preserve their original bound IDs. IDs never use the shared payload ID alone. Explicit Light children must have different typed keys. A colliding explicit/implicit key refuses before allocation, while distinct lights at the same coordinates remain distinct authored emissions.

Implicit support Surface keys are `<placement-key>/<resource-id>/<surface-id>`. Embedded support Record keys are `<placement-key>/<resource-id>/<record-id>` in kind Record, with their declared resource-local anchors remapped to the corresponding Surface key. These unescaped local compound keys are escaped once by the world-ID grammar. They reserve identities, survive remove/restore and transfer on unpack exactly like explicit children. Their resource payload scopes are validated by the same typed visitor in the owning placement context. Plain tags, names and asset IDs are never replaced.

Binding validation spans standalone records, live children, removed reservations and transfers. An active unpack transfer points to exactly one standalone record of the matching kind and identity, and relinquishes instance ownership. A Retired transfer points to zero live owners and retains its reservation. Attachment child mappings must match active/retired transfers, descriptor SourceDigest and declared anchors, without redecoding an attachment payload. Validate no duplicate producer or dangling live association. Count one logical leaf once. Numeric transfers match the standalone numeric ID rather than adding a second reservation. Missing bindings refuse on load/resolve without raising the mark.

Root globals already hash all prefab state, once, on both identity paths. Do not add duplicate instance entries to MapAuthoredContentDigest. Stored validation checks root bindings against every verified content slice using the same shared identity registry. No geometry is loaded merely to hash identity. ExpansionVersion 1 and transformed policy identity become root/effective identity inputs. No-prefab analytic routing remains unchanged.

- [ ] **Step 4: Run green** with Step 2's filter as `t2-green`, then `wa_test t2-regress "$MAPDOC" "FullyQualifiedName~NativeNumericId|FullyQualifiedName~Identity|FullyQualifiedName~StoredSurface|FullyQualifiedName~FormatFour"`. Expect complete equality for monolithic/tiled/repacked forms and changed identities for each semantic mutation. Run `wa_test t2-pins "$MP" "FullyQualifiedName~MapPersistedIdentityTests"`, including unchanged analytic pins and independently derived format-7 native pins.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t2_paths=(
    KhaozEngine.MapDoc/Prefabs/MapPrefabLocalMaterializations.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabRecords.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabFile.cs
    KhaozEngine.MapDoc/mapdoc.schema.json
    KhaozEngine.MapDoc/Prefabs/MapLightFile.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabAttachedLights.cs
    KhaozEngine.MapDoc/Surfaces/MapSupportResource.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveSupport.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabBindings.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabReferences.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabValidation.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapDoc/Identity/MapPrefabIdentity.cs
    KhaozEngine.MapDoc/Identity/MapAuthoredIdentityV2.cs
    KhaozEngine.MapDoc/MapNativeValidation.cs
    KhaozEngine.MapDoc/MapBoundDocumentValidation.cs
    KhaozEngine.MapDoc/Storage/MapStoredSurfaceSource.Identity.cs
    KhaozEngine.MapDoc/Storage/MapStoredSurfaceSource.Content.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabBindingTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabIdentityTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabIdentityFixtures.cs
    KhaozEngine.MapDoc.Physics.Tests/MapPersistedIdentityTests.cs
)
wa_finish t2 "${wa_r5_t2_paths[@]}" &&
git add -- "${wa_r5_t2_paths[@]}" &&
git commit --only -m "feat(mapdoc): persist prefab child reservations and typed identity" -- "${wa_r5_t2_paths[@]}"
```

### Task 3: One transformed effective resource view

**Files:**
- Create: `KhaozEngine.MapDoc/Prefabs/MapPrefabMaterializationOverrides.cs`, `MapPrefabResolver.cs`, `MapEffectiveResources.cs`, `MapResourceTransform.cs`, `MapPrefabPreflight.cs` in Prefabs, `KhaozEngine.MapDoc/Storage/MapEffectiveSurfaceSource.cs`, `MapEffectiveAcquisition.cs`, `KhaozEngine.MapDoc/Prefabs/MapEffectiveAttachments.cs`, `KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs`. Extend Task 1's MapEffectiveTransform.cs and Task 2's MapEffectiveSupport.cs.
- Modify: `KhaozEngine.MapDoc/Support/MapResolverV2.cs:16-46`, `MapNativeResolution.cs:21-60`, `MapResolvedDocument.cs:54-73`, `Storage/MapScopedSurfaces.cs:17-100`, `Storage/MapAcquiredFacts.cs:79-97`, `Surfaces/MapFrameLocal.cs:39-78`, `Spaces/MapBoundFaceContext.cs:133-157`, `Spaces/MapBoundPreparation.cs:47-97`, `Spaces/MapCommonRefinement.cs`, `Spaces/MapSpaceGeometry.cs:13-100`, `Surfaces/MapBoundaryGeometry.cs`, `Surfaces/MapSubdividedTriangle.cs:83-101`, `Water/MapWaterExact.cs:11-125`, `Assets/MapAssetClosure.cs:75-98` for bounded borrowed-byte access.
- Modify also: `KhaozEngine.MapDoc/Support/MapSupportQuery.cs:24-145`, `Spaces/MapSpaceMembership.cs:46-250`, `MapSpaceInterval.cs:26-100` for the shared exact-query entry points.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabExpansionTests.cs`, `MapPrefabTransformTests.cs`, `PrefabTransformFixtures.cs`.

**Interfaces:**
- Preserves released `MapTransform.Compose(MapTransform parent, MapTransform local) -> MapTransform` at MapResolvedDocument.cs:18 for existing callers. Its float absolute-origin composition must never feed effective geometry. New effective transforms compose independently until the single freeze.
- Consumes `MapResolverV2.Resolve(MapDocument document, MapAssetClosure assets, IMapSurfaceSource surfaces, MapResolveOptions options) -> MapSupportedResolution` at Support/MapResolverV2.cs:16, `MapScopedSurfaces.Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256, int queryPolicyVersion = 1, int buildPolicyVersion = 1)`, and `CompleteView(MapSurfaceSet set)` at Storage/MapScopedSurfaces.cs:48 and :73.
- Consumes Task 1's MapPrefabExpansionLimits. Produces `MapPrefabResolver.Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits? limits = null) -> MapEffectiveResources`.
- Produces internal `MapResolverV2.ResolveEffectivePlacements(MapDocument authored, MapAssetClosure assets, IReadOnlyList<MapPlacement> effective, IReadOnlyDictionary<string, MapEffectiveTransform> transforms, IMapSurfaceSource surfaces, out IReadOnlyList<MapPlacementSupport> supports) -> IReadOnlyList<MapResolvedPlacement>`, a non-expanding entry into the existing R2 support core. MapEffectiveResources adds `PlacementSupports: IReadOnlyList<MapPlacementSupport>`, the released support record type.
- Produces immutable `MapEffectiveResources`: `Placements: IReadOnlyList<MapResolvedPlacement>`, `Surfaces: MapSurfaceSet` returned as a defensive clone, `Transforms: IReadOnlyDictionary<string, MapEffectiveTransform>`, `Water: MapWaterSet?` defensive clone, `BuildingIntent: MapBuildingIntent` defensive clone, `Digest: string`, `SurfaceSource: IMapSurfaceSource`, `CompleteView() -> MapScopedSurfaces`, `Acquire(MapSurfaceScope scope) -> MapScopedSurfaces`.
- Produces `MapResourceTransform(MapEffectiveTransform Transform)` with `Point(MapExactPoint local) -> MapExactPoint`, `Height(MapExactValue local) -> MapExactValue`, `ToFrame(MapExactPoint world, WorldFrame frame) -> Vector3`, and `Normal(Vector3 local) -> Vector3`.
- Extends Task 1's MapEffectiveTransform DTO with `From(MapTransform authored) -> MapEffectiveTransform`, `Compose(MapEffectiveTransform parent, MapTransform local) -> MapEffectiveTransform` and `Compose(MapEffectiveTransform parent, MapEffectiveTransform local) -> MapEffectiveTransform`, preserving stored attachment-relative components without float conversion. Standalone registry values persist these finite binary64 components losslessly. They are mathematical poses before freezing, not rounded MapResolvedPlacement presentation poses.
- Produces internal `MapSupportQuery.SampleSurfaceExact(MapExactPoint world, string surfaceId) -> MapSupportResult` and `SelectExact(MapSupportRequest request, MapExactPoint world) -> MapSupportResult`, sharing the released support core. Released float frame points are presentation only for these entries. Adds shared `MapSpaceMembership.QueryExact(MapExactPoint world) -> MapMembershipResult`, `MapSpaceIntervals.AtExact(MapScopedSurfaces view, MapExactPoint world) -> MapSpaceInterval` and get-only membership ExactPoint. Task 4 extends these exact entries with prism occupancy.
- Produces internal `MapPrefabPreflight.Count(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits limits) -> MapPrefabExpansionWork`, with long counters Instances, Children, ReferenceEdges, Patches, Records, GeometryElements, Bindings, Overrides, Transfers and PayloadBytes. Produce internal `MapPrefabExpansionObserver(Action<string> EnterStage)` injectable through an internal Expand overload. It observes Copies, Remaps, Sorts and ExpandedGeometry only after their preflight, without changing public Expand. Invalid limits throw ArgumentOutOfRangeException. Count overflow or limit exhaustion refuses before the guarded operation.
- Produces in Prefabs/MapPrefabMaterializationOverrides.cs `MapPrefabReconciledAsset(MapPrefabAsset EffectiveAsset, IReadOnlyDictionary<MapPrefabChildKey, string> EffectiveDigests, IReadOnlySet<MapPrefabChildKey> Removed)` and pure `MapPrefabMaterializationOverrides.Reconcile(MapPrefabAsset original, IReadOnlyList<MapPrefabOverride> overrides) -> MapPrefabReconciledAsset`. After preflight, check original captured descriptor digests, clone and apply keyed overrides, then transfer materialized transform intent before remap/ResolveLocal. EffectiveDigests cover each changed normalized descriptor and reconciled precise frame. Effective row SourceDigest refreshes the canonical descriptor digest, while effective geometry also consumes the frame digest. Original rows, asset bytes and shared leaf resources remain unchanged.
- Consumes Task 2's MaterializedAttachments and exact optional materialized parameter on MapEffectiveSupport.Enumerate/MapPrefabAttachedLights.Enumerate. Produces internal `MapEffectiveAttachments.ResolveLocal(MapPrefabReconciledAsset reconciled, IReadOnlyDictionary<MapPrefabChildKey, string> bindings, MapEffectiveTransform instance) -> IReadOnlyList<MapPrefabAttachmentTransfer>`. Remap placement/child IDs and anchors, compose instance with PlacementFrame and then OwnerLocalPose in binary64, register each existing explicit child once and suppress the declared implicit edge before discovery. ResolveLocal consumes the reconciled EffectiveAsset, EffectiveDigests and Removed keys (including owned attachments of a removed placement), with the original digest check already complete. It recomputes remapped world descriptor/frame digests without comparing an overridden child to the original descriptor pin. Instance removal overrides filter associated children while the asset rows/definitions and reservations stay intact, so restore reuses the captured child and binding. These are effective associations, not new serialized world rows until unpack.
- Produces internal `MapEffectiveAttachments.Reuse(MapDocument document, MapPrefabAttachmentTransfer attachment) -> IReadOnlyList<MapPrefabChild>` in Prefabs/MapEffectiveAttachments.cs. It reads transferred standalone descriptors and returns references to those world children, never a second materialization or binding allocation. Call before support/light resource enumeration for standalone placements.
- Extends Task 2's pure MapEffectiveSupport with `Install(MapAssetClosure assets, IReadOnlyList<MapPrefabChild> children, IReadOnlyDictionary<string, MapEffectiveTransform> transforms) -> MapSurfaceSet`. Install occurs before effective source capture and support sampling.
- Produces internal `MapEffectiveGeometry.Patch(MapScopedSurfaces view, MapPatchKey key) -> MapCompiledPatch` and `Strip(MapScopedSurfaces view, MapWallStrip strip) -> MapCompiledStrip`, preserving remapped face keys and canonical topology.
- Produces internal `CellCandidates(MapScopedSurfaces view, string surfaceId, MapExactXz world) -> IReadOnlyList<(long X, long Z)>` and `CellPolygon(MapScopedSurfaces view, MapPatchKey key, int slotCell) -> IReadOnlyList<MapExactXz>` on MapEffectiveGeometry. Existing bound/refinement APIs keep their signatures and use these effective world polygons internally.
- Adds nullable get-only `MapResolvedDocument.EffectiveResources: MapEffectiveResources?` through an internal constructor overload. Existing signatures remain. Scoped view owns an immutable effective context and its transform/resource witness.
- The reconciled effective PlacementFrame is authoritative after placement transform intent has been transferred from the keyed override. Child frame uses its reconciled effective OwnerLocalPose once. Normalize the captured child's detached presentation LocalTransform to identity only after transferring its override intent. Unchanged captured frames keep their binary64 components, rather than being rebuilt from presentation floats. Local light Position/Radius keep payload units and one owner-offset composition. Standalone NativeResourceTransforms enter that same context. Placement entries are authoritative precise poses with authored float presentation fields. Task 10's component-aware placement path updates both together. Materialized attachment rows suppress automatic support/light enumeration for those (placement, kind, resource) edges. Validate/reuse their captured standalone IDs, descriptors and relative poses, without reading those payload bytes again. Unassociated standalone placements keep ordinary deterministic attachment expansion. Their SourceDigest and owner bindings enter loaded/stored root identity, scoped witnesses and effective geometry digests.

- [ ] **Step 1: Write failing tests.**

```csharp
[Theory] [InlineData(0.8f, 7.3999996f)] [InlineData(1.2f, 8.6f)]
public void AllChildResources_UseOneTransformAndSharedEdge(float scale, float upperY)
{
    var f = PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f));
    var effective = PrefabFixtures.Effective(f);
    var composed = MapEffectiveTransform.Compose(MapEffectiveTransform.From(f.Document.Prefabs!.Instances.Single().Transform),
        new MapTransform(Vector3.Zero, 0, 1));
    var transform = new MapResourceTransform(composed);
    var point = new MapExactPoint(new(4, 1), new(3, 1), new(-4, 1));
    var expected = transform.Point(point);
    foreach (var corner in new[] { PrefabFixtures.SharedFloorCorner(effective),
        PrefabFixtures.SharedRoomCorner(effective), PrefabFixtures.SharedWaterCorner(effective) })
        Assert.Equal((expected.X, expected.Z), (corner.X, corner.Z));
    Assert.Equal(transform.Height(new(3, 1)), PrefabFixtures.StoreyUpper(effective));
    Assert.Equal(upperY, PrefabFixtures.StoreyUpper(effective).ToSingle());
}
[Theory] [InlineData(false)] [InlineData(true)]
public void DecomposedOrigin_EqualsDirectGeometryAtNearAndFarSites(bool far)
{
    var f = PrefabFixtures.Decomposed(far);
    var (direct, offset) = PrefabFixtures.DecomposedCorners(PrefabFixtures.Effective(f));
    Assert.Equal(direct, offset);
    if (far)
    {
        Assert.Equal(new MapExactValue(31460408117, 1048576), direct.X);
        Assert.Equal(new MapExactValue(-31463533353, 1048576), direct.Z);
    }
}
[Fact] public void ResolveAndReload_NeverAllocateOrMutate()
{
    var f = PrefabFixtures.Create();
    var before = MapDocumentFile.SaveText(f.Document);
    var a = MapPrefabResolver.Expand(f.Document, f.Assets);
    var b = MapPrefabResolver.Expand(MapDocumentFile.LoadText(before), f.Assets);
    Assert.Equal(a.Digest, b.Digest);
    Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    Assert.Throws<MapDocumentException>(() => PrefabFixtures.Effective(PrefabFixtures.Create("missing-binding")));
}
[Theory]
[InlineData("room-domain")] [InlineData("parent-floor")] [InlineData("remote-anchor")]
public void ScopedChildren_RequireTheirFullWitness(string fact)
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Create("seam-instance"));
    Assert.Equal(MapAcquireStatus.Complete, PrefabFixtures.AcquireAll(r).Status);
    var missing = PrefabFixtures.AcquireWithout(r, fact);
    Assert.Equal(MapAcquireStatus.Incomplete, missing.Status);
    Assert.False(missing.ReadWitness.Complete);
    Assert.Contains(fact, PrefabFixtures.UnavailableIds(missing));
    Assert.True(PrefabFixtures.UnrelatedFactsRemainReadable(missing));
}
```

Task 3's fixture file declares `SharedFloorCorner/SharedRoomCorner/SharedWaterCorner(MapEffectiveResources resources) -> MapExactPoint`, `StoreyUpper(MapEffectiveResources resources) -> MapExactValue`, and `AcquireWithout(MapEffectiveResources resources, string dependencyId) -> MapScopedSurfaces`. These expose transformed data from the actual context, not independent arithmetic. Add `AcquireAll(MapEffectiveResources resources) -> MapScopedSurfaces`, `UnavailableIds(MapScopedSurfaces view) -> IReadOnlyList<string>` mapping the released witness to exact fixture IDs, and `UnrelatedFactsRemainReadable(MapScopedSurfaces view) -> bool`. Use released `MapUnavailable(string What, MapPatchKey Key, MapPatchStatus Status)` from Identity/MapWitnesses.cs:11. Assert What, Key and Status against the fixture table, including record versus missing anchor, rather than only an aggregate incomplete bit. AcquireWithout wraps an IMapSurfaceAcquisitionSource that refuses precisely the selected fact after discovery, retaining unrelated records. Before Task 4 the room-domain theory targets the existing lattice room/footprint record. Task 4 replaces that case with the prism record and exercises membership. Remote-anchor faults keep the record discovered but refuse its anchor read. Add `FarFrame_SharedEdgesAgreeWithoutAbsoluteFloatRoundTrip`, `ExpansionPreflight_StopsEveryGuardedOperation`, `ScopedDigest_FollowsTransformAssetAndBindings`, and `LaterCallerMutation_CannotChangeEffectiveView`.

Add `Decomposed(bool far) -> PrefabFixture` and `DecomposedCorners(MapEffectiveResources resources) -> (MapExactPoint Direct, MapExactPoint OffsetSupport)` to Task 3 fixtures. Represent local (4,0,-4) both as a direct floor/room/water vertex and as a placement support-resource origin with a zero vertex. Near and far outputs must equal after one freeze, without consulting Compose for the oracle. At the far transform the independently computed frozen X is 30002.983204841614 and Z is -30005.963662147522. The early float Compose results 30002.982421875 and -30005.962890625 must differ. Independently check frozen ticks and maximum composition/quantum/Y/backend errors, not rounded decimal display alone.

Add `PlacedSlope_SupportMembershipAndBoundsSucceedWithWideIntermediates`: an invented floor rising from local Y 0 to 1 over X 0 to 4, at yaw 0.371/scales 0.8 and 1.2 at both sites. A sloped transformed floor at both sites resolves nonempty support, room bounds and membership with independent exact barycentric heights. Exercise MapSubdividedTriangle.Height/Cross, bound refinement and normal derivation. A separate rational query whose independently reduced denominator exceeds long.MaxValue must return MapSupportStatus.NotRepresentable and MapMembershipStatus.NotRepresentable, not throw from an intermediate Multiply or float fallback.

Add finite preflight boundary theories with small custom limits, one over-limit and one exact-limit positive case per counter. No million-element fixture or local load test is needed. Place one huge Surface child under many instances, retain removed bindings, Previous overrides, tombstones and transfers, and include embedded support records. A test-only stage observer records Copies, Remaps, Sorts and ExpandedGeometry. Each exceeded counter refuses with all four zero. Check negative limits and checked multiplication/addition overflow. Verify within-budget controls do nonzero guarded work.

Add `MaterializedTransformOverrides_MoveCapturedFramesExactly` to MapPrefabTransformTests. `PrefabFixtures.MaterializedOverrideFixture() -> PrefabFixture` authors the verified captured-row asset shape used by the later Task 9 re-extraction proof, with identity instance transform, owner PlacementFrame (4,0,-4), yaw 0, scale 1, identity child frames, floor corner (0,0.5,0) and light Position (0,2,0), Radius 3. Import IDs deck-owner, captured-floor and captured-light. `MaterializedTransformOverride(string kind) -> MapPrefabOverride` targets the exact captured typed keys. Owner placement/transform variants replace the asset-local frame with (7.25,0,-2.5), yaw 0, scale 1. Floor variant replaces its owner-local frame with (-0.5,0.25,0.75), light variant with (0.75,0.5,-1.25), both yaw 0/scale 1. No expected value calls the product transform kernel.

```csharp
[Theory]
[InlineData("owner-placement", 7.25f, 0.5f, -2.5f, 7.25f, 2f, -2.5f)]
[InlineData("owner-transform", 7.25f, 0.5f, -2.5f, 7.25f, 2f, -2.5f)]
[InlineData("floor", 3.5f, 0.75f, -3.25f, 4f, 2f, -4f)]
[InlineData("light", 4f, 0.5f, -4f, 4.75f, 2.5f, -5.25f)]
public void MaterializedTransformOverrides_MoveCapturedFramesExactly(string kind,
    float fx, float fy, float fz, float lx, float ly, float lz)
{
    var f = PrefabFixtures.MaterializedOverrideFixture();
    var changed = PrefabFixtures.WithMaterializedOverride(f, PrefabFixtures.MaterializedTransformOverride(kind));
    var r = PrefabFixtures.Effective(changed);
    var (floor, light) = PrefabFixtures.CapturedCoordinates(r);
    Assert.Equal(new MapExactPoint(MapExactValue.FromSingle(fx), MapExactValue.FromSingle(fy), MapExactValue.FromSingle(fz)), floor);
    Assert.Equal(new MapExactPoint(MapExactValue.FromSingle(lx), MapExactValue.FromSingle(ly), MapExactValue.FromSingle(lz)), light);
    Assert.Equal(new[] { "captured-floor" }, PrefabFixtures.CapturedSupportOwners(r));
    Assert.Equal(new[] { "captured-light" }, MapEffectiveAnnotations.Lights(r).Select(x => x.Id));
}
```

Declare `WithMaterializedOverride(PrefabFixture fixture, MapPrefabOverride value) -> PrefabFixture`, cloning authored state and replacing only its override set, `CapturedCoordinates(MapEffectiveResources resources) -> (MapExactPoint Floor, MapExactPoint Light)` reading actual canonical vertices/annotations, and `CapturedSupportOwners(MapEffectiveResources resources) -> IReadOnlyList<string>` reading the canonical captured floor owners, excluding the fixture's separately named ground. Require unchanged bindings/high-water, original row/asset codec bytes and shared leaf hashes, identity-only normalized child presentation after transfer, and changed effective frame digests. Corrupting the original SourceDigest must refuse even if an override replaces that descriptor. Save/reload keeps the same exact coordinates and one producer.

Add `MissingYChild_IsSeatedOnceOnTheEffectiveFloor`. Variant support-resource-bound-child has parent transform (10.25,5,-3.125), yaw 0.371, scale 1.2, low-item with null local Y and a surface binding specifically to deck/deck-support/floor at local 0, supplied only by a placement SupportResourceIds entry, with no explicit floor child. Assert resolved world Y exactly 5, equal to MapSupportQuery.SampleSurface on the effective floor, and unchanged allocator/document bytes. A second parent transform of Y -450 proves no parent-height addition or scale after sampling. Task 3 proves direct MapDoc resolution. Task 5 proves MapWorldBuilder returns the same Y after Physics installation.

- [ ] **Step 2: Run red** `wa_test t3-red "$MAPDOC" "FullyQualifiedName~MapPrefabExpansionTests|FullyQualifiedName~MapPrefabTransformTests"`. Expect absent expansion/context or current independent transform paths disagreeing.
- [ ] **Step 3: Implement expansion, exact transformation and acquisition.**

Preflight counts before any effective snapshot materialization, retained-state copy, sort or remap. Then validate original payload/descriptor pins and snapshot closure bytes/records. Reconcile keyed overrides through MapPrefabMaterializationOverrides before effective row resolution, then remap persisted IDs and compose each child transform once. Native standalone placements remain in the same snapshot. A root container is not a replacement placement. Expand all kinds, keeping local geometry and the unfrozen MapEffectiveTransform beside it. Every geometric child origin, including placement-attached support children, uses the same double composition. Released resolved-placement transforms are presentation/backend values only. Missing bindings refuse. No allocations, tags-based inference or repeated expansion in the builder.

For a materialized owner, MapPrefabPlacementOverride with a nonnull Transform or MapPrefabTransformOverride supplies a complete replacement asset-local PlacementFrame, promoted exactly from authored floats to binary64. Update all rows of that owner together. For a captured attached child, MapPrefabTransformOverride supplies a complete replacement owner-local OwnerLocalPose, not an additional world transform. Transfer this intent first, then normalize its copied LocalTransform. Value-only changes preserve precise base frames. Replacement children transfer an explicitly changed presentation pose in the same way before digest refresh. Suppression keys, references and bindings never change because a transform changes.

Reconciliation always starts from the immutable captured asset and the current effective override set, never from a prior reconciled result. Removing a transform override through undo restores the captured base frame. A remove override retains Previous as already specified, suppresses the live owner/child without rediscovering its advertised resource edge, and restore reinstates Previous's effective frames and the same reserved IDs. Redo recomputes that same effective result. No extra allocation or shared-leaf rewrite occurs.

Build the effective geometry/source before resolving missing-Y leaves. Transform a child's support ReferenceY and local search extents into world units, remap its surface/space owner, and sample through the non-expanding R2 entry point. Install the returned world Y directly. Explicit-Y children compose normally. ResolveEffectivePlacements also handles standalone placements against that source. Its precise transform dictionary supplies exact support XZ before any MapPlacement/MapResolvedPlacement float presentation. MapSupportQuery.SelectExact/SampleSurfaceExact use effective CellCandidates and exact membership/barycentric geometry. Existing Compute must not infer rotated cells from the raw local lattice frame or round the support point through a Vector3. Public resolver 2 computes authored identity from the original document, then constructs its resolved snapshot and support list from the one expansion. It never hashes a derived expanded document or recursively expands while sampling.

Compose binary64 origins without an absolute Vector3: parent origin plus double rotation of (local origin times parent scale), sum yaw in double and multiply scales in double. Convert authored floats exactly to double. Apply all parent/child/support-resource transforms before any coordinate freeze. Point evaluation then uses Math.SinCos, rational coordinates converted to double and the composed double pose. Freeze once with checked ties-even ticks at quantum 1/1048576 m. Freeze XZ as MapExactValue(ticks,1048576). Y takes that value through ToSingle/FromSingle once for the released height seam. Height uses the identical Y rule. Raw unplaced lattice vertices remain exact.

Keep four error sources separate. Binary64 arithmetic has the standard gamma(n) sum-of-magnitudes bound for the fixed operation count, evaluated per pose, plus the separately reported Math.SinCos deviation from the independent golden. This is not a cross-platform libm guarantee. At the near/far fixtures require pre-freeze composition error below 1e-9 m against the independent high-precision golden. Quantum adds at most 0.000000476837158203125 m per axis. Y output adds at most half the binary32 ULP at quantized world Y (up to 0.03125 m within the installed 1,000,000 m limit). Backend pose rounding is a fourth approximation, never an input to semantic geometry. Task 5 reports its origin half-ULP plus local-coordinate rounding and radius times measured scale/rotation error in metres.

Normalize negative zero and refuse collapsed/nonfinite heights or unrepresentable final outputs. Frame output subtracts exact XZ anchors before ToSingle. Never round world points back into a lattice. Extend the released R4 MapWideRational/MapWidePoint kernel from Water/MapWaterExact.cs into effective triangle determinant, barycentric height, normal cross products and common-refinement operations. Specifically MapSubdividedTriangle.Cross/Height at :83-101 must avoid int64 narrowing between multiply/divide stages. Preserve public signatures with narrow-at-result wrappers, and use wide internal paths for transformed queries. Narrow only externally emitted values, map refusal to existing statuses, and keep old unplaced answers pinned. A dyadic input quantum alone does not guarantee later rational arithmetic fits.

R5-P3 chooses double composition with one dyadic freeze and wider predicates over early float composition. The independent decomposition golden proves why: early float origins lose about 0.8 mm at the far fixture before the precise kernel can run. Full binary64 rational storage would greatly enlarge every denominator. This policy adds explicit sub-micrometre coordinate rounding and final narrowing refusals while retaining released output types.

MapFrameLocal.CompileInFrame delegates to this point kernel. Placed R4 water converges in Task 7, using the same frozen vertices, not quaternion float math. Unit transforms on existing unplaced lattice geometry retain their exact vertices. Policy `kemap/effective-resources/1` names expansion and transform rules, query policy 2 names transformed spatial reads. These enter effective/scoped/build identities. Same-process independent heads agree. Residual Math.SinCos platform dependence remains documented.

MapPrefabPreflight traverses the closure's once-decoded immutable payload metadata without constructing new child/patch arrays. Task 1's prefab decoder caps an individual payload at 268435456 bytes and streams element counts under the Task 1 limits before materializing its collection. Task 2 support payloads use the same bounded scan. Task 3 then charges the aggregate per-instance work before any effective copy/remap/sort. Add internal `MapAssetClosure.ResourceByteLength(string resourceId) -> int` and `BorrowVerifiedResourceBytes(string resourceId) -> ReadOnlyMemory<byte>` backed by the captured immutable resource, so preflight checks size without the released Bytes copy. The borrowed memory never escapes the internal reader or becomes mutable. Copy only after acceptance of the relevant count. Charge unique payload bytes once, then instances and every per-instance effective child, patch, topology record and geometry element (vertices, faces, cell slots, polygon/chain/strip/opening points). Charge live and removed binding entries, every override including Previous and replacement geometry, tombstoned instances and transfer entries. MaxReferenceEdges counts each typed reference occurrence and each consumed resource dependency edge, including restore state, leaf LightResourceIds and attachment associations. MaxChildren/Bindings include implicit lights, MaxTransfers charges each transfer, world or asset-local association and association-child row, including per-instance copies and retained replacement state, and geometry/payload counters charge attached support/light records before any copy. Captured explicit child geometry is charged once, with row/reference/frame metadata charged before copying or remapping. Suppressed resource edges remain verified closure/payload dependencies but incur no implicit geometry expansion. Add `AssetLocalMaterialization_PreflightSuppressesBeforeCopy` with small exact-limit and over-limit controls and existing zero guarded-stage assertions. Shared decoded bytes do not exempt per-instance copied/expanded geometry. Checked long sums/products refuse overflow. Decoder scans and context-copy operations consume this same accepted budget. No R8 streaming/indexing work enters R5.

MapEffectiveSurfaceSource implements IMapSurfaceAcquisitionSource, including OpenAcquisition and incident-record reads. Index remapped local patch world bounds, including every transformed corner, and request local patches by conservative inverse bounds. Local slot coordinates remain local, never masquerade as world slots. World scopes and whole-column completeness use world bounds. Charge discovery, records and dependency expansion before work under existing budgets. Snapshots include parent/domain/transform/binding/resource facts even when their anchors lie outside the requested XZ. Add `MissingPrefabBytes_RefusesBeforeCaptureAndCapturedBytesAreNeverReread`. A valid byte source first completes closure loading/expansion. A fresh source missing only the named prefab refuses closure capture with its exact ResourceId and expected digest, before an effective view is returned. The already captured closure and effective view still expand/query successfully after that source is armed to throw on further prefab reads. After capture, expansion/acquisition reads only the immutable captured payload and never the byte source again. A source that later throws on prefab reads must not affect captured queries. An unavailable geometric dependency prevents a complete witness. KnownEmpty remains distinct from unloaded/missing.

MapScopedSurfaces snapshots this effective context with its acquired facts. Both CompleteView and scoped paths use the same geometry evaluator. Resolver 2 attaches one effective snapshot and resolves support bindings against its source, including selected expanded placement IDs in ResolvePlacements. Resolver 1 rejects prefab sections before callbacks and retains its released path. R8 still owns runtime eviction and large-world scheduler proofs.

MapBoundFaceContext transforms its compiled canonical bound faces and opening planes through MapEffectiveGeometry. MapBoundPreparation/CommonRefinement use the transformed cell polygon rather than an axis-aligned world rectangle inferred from local lattice addresses. Boundary chains/strips use the same point kernel. Inverse double coordinates discover a conservative local cell halo only, charged under the existing budgets. Final selection uses exact world polygons and canonical triangle predicates, so inverse rounding never decides ownership. Surface normals are derived from effective canonical triangle vertices after Y rounding. This is one query pipeline, not a local sampler beside R2.

- [ ] **Step 4: Run green** with Step 2's filter as `t3-green`, then `wa_test t3-regress "$MAPDOC" "FullyQualifiedName~FrameLocal|FullyQualifiedName~NativeResolution|FullyQualifiedName~Acquisition|FullyQualifiedName~Support"`. Expect shared exact vertices, intact witnesses and no change to unplaced lattice/analytic results.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t3_paths=(
    KhaozEngine.MapDoc/Prefabs/MapPrefabMaterializationOverrides.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveAttachments.cs
    KhaozEngine.MapDoc/Assets/MapAssetClosure.cs
    KhaozEngine.MapDoc/Support/MapSupportQuery.cs
    KhaozEngine.MapDoc/Spaces/MapSpaceMembership.cs
    KhaozEngine.MapDoc/Spaces/MapSpaceInterval.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveSupport.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveResources.cs
    KhaozEngine.MapDoc/Prefabs/MapResourceTransform.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveTransform.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabPreflight.cs
    KhaozEngine.MapDoc/Surfaces/MapSubdividedTriangle.cs
    KhaozEngine.MapDoc/Water/MapWaterExact.cs
    KhaozEngine.MapDoc/Storage/MapEffectiveSurfaceSource.cs
    KhaozEngine.MapDoc/Storage/MapEffectiveAcquisition.cs
    KhaozEngine.MapDoc/Storage/MapScopedSurfaces.cs
    KhaozEngine.MapDoc/Storage/MapAcquiredFacts.cs
    KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs
    KhaozEngine.MapDoc/Surfaces/MapFrameLocal.cs
    KhaozEngine.MapDoc/Surfaces/MapBoundaryGeometry.cs
    KhaozEngine.MapDoc/Spaces/MapBoundFaceContext.cs
    KhaozEngine.MapDoc/Spaces/MapBoundPreparation.cs
    KhaozEngine.MapDoc/Spaces/MapCommonRefinement.cs
    KhaozEngine.MapDoc/Spaces/MapSpaceGeometry.cs
    KhaozEngine.MapDoc/Support/MapResolverV2.cs
    KhaozEngine.MapDoc/MapNativeResolution.cs
    KhaozEngine.MapDoc/MapResolvedDocument.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabExpansionTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabTransformTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabTransformFixtures.cs
)
wa_finish t3 "${wa_r5_t3_paths[@]}" &&
git add -- "${wa_r5_t3_paths[@]}" &&
git commit --only -m "feat(mapdoc): expand prefabs through one transformed resource view" -- "${wa_r5_t3_paths[@]}"
```

### Task 4: Polygon-prism rooms inside R2's one space authority

**Files:**
- Create: `KhaozEngine.MapDoc/Spaces/MapPrismDomain.cs`, `MapPrismGeometry.cs`, `MapOccupiedPartitions.cs`, `MapPrismCoverage.cs`.
- Modify: `KhaozEngine.MapDoc/Spaces/MapSpaceRecords.cs:6-15`, `MapSpaceMembership.cs:46-250`, `MapSpaceInterval.cs:26-100`, `MapSpaceGeometry.cs:13-100`, `MapSpaceCoverageValidator.cs:18-96`, `MapSpaceOverlapValidator.cs` in Spaces, `Surfaces/MapTopologyReferenceValidator.cs`, `Surfaces/MapSurfacePatchCodec.Records.cs:31-163`, `Storage/MapAcquiredFacts.cs:84-97`, `Support/MapSupportQuery.cs:74-145`, Task 1's prefab schema/codec, `docs/design/WORLD-AUTHORING-R2-CAVES-SCALE-2026-10-06.md:199-255`.
- Test: `KhaozEngine.MapDoc.Tests/Spaces/MapPrismMembershipTests.cs`, `MapPrismCoverageTests.cs`, `KhaozEngine.MapDoc.Tests/Prefabs/PrefabCaveFixtures.cs`.
- Modify also: `KhaozEngine.MapDoc/Surfaces/MapSurfacePatch.Records.cs:11-36`, adding deep cloning of prism footprint lists. Acquired record copies otherwise throw on the new record before a query can run.

**Interfaces:**
- Consumes `MapSpaceDoc(string Id, MapSpaceKind Kind, MapRecordRef? Parent, MapRecordRef? AliasOf, IReadOnlyList<string> DomainTags, IReadOnlyList<MapBoundaryRef> Walls, IReadOnlyList<MapBoundaryRef> Portals, IReadOnlyList<MapRecordRef> Links)` at Spaces/MapSpaceRecords.cs:9.
- Consumes `new MapSpaceMembership(MapScopedSurfaces scoped)`, `Query(MapFramePoint point) -> MapMembershipResult` at :40 and :46, `MapSpaceIntervals.At(MapScopedSurfaces view, MapFramePoint point) -> MapSpaceInterval` and `Column(...) -> IReadOnlyList<MapSpaceInterval>` at MapSpaceInterval.cs:30 and :45.
- Consumes `MapSpaceCoverageValidator.Validate(MapScopedSurfaces view) -> IReadOnlyList<string>`, canonical parent bound refinement and Task 3 context. MapSpaceKind adds Room after Cave and Exterior, preserving existing numeric values.
- Produces `MapPrismDomain(string Id, int PayloadVersion, MapRecordRef Space, MapRecordRef Parent, string InstanceId, string StoreyId, MapWaterDomainDoc Footprint, float LowerY, float UpperY) : MapTopologyRecord(Id)`, payload version exactly 1. It is a Record child, anchored in a declared local surface patch and remapped with every other record. Space names an existing Room MapSpaceDoc whose Parent must equal this Parent. After unpack InstanceId is empty with a validated transfer.
- Produces `MapPrismGeometry.Compile(MapScopedSurfaces view, MapPrismDomain domain) -> MapWaterDomain` and `Bounds(MapScopedSurfaces view, MapPrismDomain domain) -> MapBox3`, sharing R4 polygon ownership and Task 3 transform.
- Produces internal `MapOccupiedPiece(string SpaceId, string FragmentId, MapOccupiedBoundary LowerBoundary, MapOccupiedBoundary UpperBoundary, IReadOnlyList<MapExactXz> Polygon, IReadOnlyList<MapBoundFace> LowerFaces, IReadOnlyList<MapBoundFace>? UpperFaces, MapExactValue? LowerPlane, MapExactValue? UpperPlane)`, `MapOccupiedPartitions.Pieces(MapScopedSurfaces view, string spaceId, MapExactXz min, MapExactXz max, MapRefinementLimits limits) -> IReadOnlyList<MapOccupiedPiece>`.
- Produces `MapOccupiedBoundary(string OwnerId, string Role, string SemanticDigest)` and `MapOccupiedFragment(string Id, string SpaceId, MapOccupiedBoundary Lower, MapOccupiedBoundary Upper)`. Role is the closed set canonical-floor, canonical-ceiling, prism-lower, prism-upper and open-top. `MapSpaceInterval.Fragment: MapOccupiedFragment?` and `MapMembershipResult.Fragment: MapOccupiedFragment?` are additive get-only properties. A resolved R5 interval always carries one.
- Produces `MapPrismCoverage.Validate(MapScopedSurfaces view, MapRefinementLimits limits) -> IReadOnlyList<string>`, called by the existing coverage validator. Adds immutable `MapMembershipResult.ParentSpaceIds: IReadOnlyList<string>` without removing its constructor/signatures.
- Consumes and extends Task 3's QueryExact/AtExact and membership ExactPoint. They share QueryCore with released frame-point APIs, passing exact coordinates without a float round trip. Point remains its rounded presentation.

- [ ] **Step 1: Write failing membership/coverage tests.**

```csharp
[Fact] public void RoomIsOccupied_ExteriorIsOnlyItsParent()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Create());
    var p = PrefabFixtures.Point(r, 2, 1, -2);
    var m = new MapSpaceMembership(r.CompleteView()).Query(p);
    Assert.Equal(MapMembershipStatus.Resolved, m.Status);
    Assert.Equal("prefab/a/Record/room", m.SpaceId);
    Assert.Equal(new[] { "outside" }, m.ParentSpaceIds);
    Assert.Contains("outside", m.DomainKeys);
    Assert.Equal(m.SpaceId, MapSpaceIntervals.At(r.CompleteView(), p).SpaceId);
}
[Fact] public void SharedEdgesReflexCornersAndStoreys_HaveOneOwner()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Cave("reflex-storeys"));
    foreach (var p in PrefabFixtures.RoomBoundaryPoints(r))
    {
        var m = new MapSpaceMembership(r.CompleteView()).QueryExact(p);
        Assert.Equal(MapMembershipStatus.Resolved, m.Status);
        Assert.Equal(PrefabFixtures.ExpectedBoundaryOwner(r, p), m.SpaceId);
    }
    var atThree = MapSpaceIntervals.At(r.CompleteView(), PrefabFixtures.Point(r, 2, 3, -2));
    Assert.Equal("prefab/a/Record/upper-room", atThree.SpaceId);
}
[Fact] public void TruePeerOverlap_IsAmbiguousAndCannotPublish()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Cave("peer-overlap"));
    Assert.Equal(MapMembershipStatus.Ambiguous,
        new MapSpaceMembership(r.CompleteView()).Query(PrefabFixtures.Point(r, 2, 1, -2)).Status);
    Assert.Contains(MapSpaceCoverageValidator.Validate(r.CompleteView()),
        e => e.Contains("peer occupied overlap", StringComparison.Ordinal));
}
[Theory]
[InlineData("prism")] [InlineData("parent-floor")] [InlineData("remote-anchor")]
public void MissingParentOrPrism_IsMissingGeometry(string fact)
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Cave("transformed"));
    Assert.Equal(MapAcquireStatus.Complete, PrefabFixtures.AcquireAll(r).Status);
    var view = PrefabFixtures.AcquireWithout(r, fact);
    Assert.Contains(fact, PrefabFixtures.UnavailableIds(view));
    Assert.True(PrefabFixtures.UnrelatedFactsRemainReadable(view));
    Assert.Equal(MapMembershipStatus.MissingGeometry,
        new MapSpaceMembership(view).Query(PrefabFixtures.Point(r, 2, 1, -2)).Status);
}
```

This task declares `RoomBoundaryPoints(MapEffectiveResources resources) -> IReadOnlyList<MapExactPoint>` and `ExpectedBoundaryOwner(MapEffectiveResources resources, MapExactPoint point) -> string`. Expected owners are an explicit fixture table, not computed using product Contains. Include lower edges, upper edges, convex corners, the L reflex corner tiled by a square, transformed shared walls between two instances and both sides of storey y 3. Two adjacent instances use a common authored transform and complementary local polygons, so the shared wall is genuinely identical. Independently rounded placements that merely nearly touch are not silently snapped or aliased. Use yaw 0.371, scales 0.8/1.2, negative Z and the far transform. Exact boundary proofs use QueryExact, while representable frame points also exercise the released Query path.

Add named `SelectedCaveModel_ComposesFloorsCeilingsAndVolumes`: two variable-bound R2 cave spaces at -60 to -50 and -40 to -30 with a room inside each. Assert both cave bounds remain canonical, each room is the occupied child with its cave parent, and no fixed 4.5 m layer rule appears. `PrismOverUnevenParent_UsesCanonicalIntersection` pins floor heights -0.5 to 0.5 and room [0,3), resolving the room only above max(parent floor, 0). `PrismWithNoAuthoredParent_Refuses`, `OutsideParentFootprint_Refuses`, `ZeroHeightOrSelfIntersectingPrism_Refuses`, and `Column_PartitionsParentAroundNestedStoreys` cover full geometry and publication. Existing R2 cave/portal answers remain pinned.

- [ ] **Step 2: Run red** `wa_test t4-red "$MAPDOC" "FullyQualifiedName~MapPrismMembershipTests|FullyQualifiedName~MapPrismCoverageTests"`. Expect unsupported topology record/domain, duplicate occupied owners or wrong boundary selection.
- [ ] **Step 3: Implement domains, nested occupancy, coverage and query integration.**

A room uses a MapSpaceDoc and one MapPrismDomain. Persist local polygons/heights and the instance owner. Compile its footprint through the same exact transformed domain path as water. Normalize world XZ winding. R4-P28 ownership uses the world direction (1, infinitesimal +Z), including the convex/reflex/collinear vertex distinction. Do not apply the edge rule in local coordinates after rotation.

At a point, acquire the room, parent chain, footprint and canonical bounds before choosing. Intersect the prism [lower,upper) with its parent's actual interval. Choose the deepest containing descendant. Remove containing ancestors from the competing-owner set and return them separately in ParentSpaceIds/DomainKeys. Two incomparable descendants remain Ambiguous. A parent's remaining occupied volume excludes all descendants. Columns split into disjoint intervals, including repeated parent IDs in separate intervals, and use identical tie rules. Each fragment ID is the canonical digest of SpaceId, the lower/upper authored boundary owner IDs and roles, and the ordered ancestor-carving boundary path. It does not depend on clipping order, tile packing, query rectangle or float endpoints. Boundary SemanticDigest affects geometry/digests without reallocating the logical fragment ID. Common-refinement triangles in the same logical vertical slab share the fragment. Point membership and columns return that same ID and provenance. Reload/repacking preserves it, while different lower and upper parent slabs have different IDs. Task 7 carries it into regions, contours, bounds and classifiers.

Lattice-only portals retain released R2 ToSpace exact-plane ownership. A portal incident to a new prism room uses the prism's R4-P28 world-edge owner for occupied membership, consistently in point, column and water partition paths. Its authored From/To normal remains a relation fact, not a competing owner rule. Add `RoomPortalPlane_UsesPrismOwnershipAfterYaw` and `LatticePortalPlane_KeepsReleasedToSpaceOwnership`, including a portal whose ToSpace points against world +X. This implements Decision 2 without changing old cave outcomes.

Coverage validates nonempty simple polygons, positive effective separation over common refinements, complete horizontal coverage by the declared parent footprint, ancestry without cycles and room/storey ownership, including required storey existence and matching instance ownership. Standalone rooms reference standalone Storeys, not a removed instance. Add `StandaloneStorey_RoundTripsNameAndRoomOwnership` with exact ID/name and invalid ownership cases. Use shared polygon clipping and R2 bound faces to check every refinement vertex, not corner samples or a bounding box. Rooms are semantic spaces, so their boundary need not fabricate a physical wall. Existing Cave enclosure rules still require walls/ceilings/apertures. Preserve aliases and distinguish semantic nesting from peer overlap. No implicit exterior.

MapOccupiedPartitions exposes this same partition to water and support. It uses transformed polygons and canonical variable bound faces, with explicit constant prism planes, not new support triangles. Query candidate counting shares existing patch/record/face limits. An unavailable prism/parent dependency returns MissingGeometry, overflow CapacityExceeded and exact narrowing NotRepresentable. Whole columns still refuse incomplete or height-bounded acquisitions under R4-P30. Reference codec and defensive record clone support the new record type. Update the R2 topic's historical separate-domain wording to the approved one-space model, preserving variable cave semantics.

- [ ] **Step 4: Run green** with Step 2's filter as `t4-green`, then `wa_test t4-regress "$MAPDOC" "FullyQualifiedName~SpaceMembership|FullyQualifiedName~SpaceInterval|FullyQualifiedName~Coverage|FullyQualifiedName~CommonRefinement|FullyQualifiedName~Support|FullyQualifiedName~Relation"`. Expect unchanged existing cave/portal cases and exact point/column agreement.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t4_paths=(
    KhaozEngine.MapDoc/Spaces/MapPrismDomain.cs
    KhaozEngine.MapDoc/Spaces/MapPrismGeometry.cs
    KhaozEngine.MapDoc/Spaces/MapOccupiedPartitions.cs
    KhaozEngine.MapDoc/Spaces/MapPrismCoverage.cs
    KhaozEngine.MapDoc/Spaces/MapSpaceRecords.cs
    KhaozEngine.MapDoc/Spaces/MapSpaceMembership.cs
    KhaozEngine.MapDoc/Spaces/MapSpaceInterval.cs
    KhaozEngine.MapDoc/Spaces/MapSpaceGeometry.cs
    KhaozEngine.MapDoc/Spaces/MapSpaceCoverageValidator.cs
    KhaozEngine.MapDoc/Spaces/MapSpaceOverlapValidator.cs
    KhaozEngine.MapDoc/Surfaces/MapTopologyReferenceValidator.cs
    KhaozEngine.MapDoc/Surfaces/MapSurfacePatchCodec.Records.cs
    KhaozEngine.MapDoc/Surfaces/MapSurfacePatch.Records.cs
    KhaozEngine.MapDoc/Storage/MapAcquiredFacts.cs
    KhaozEngine.MapDoc/Support/MapSupportQuery.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabValidation.cs
    KhaozEngine.MapDoc/mapdoc.schema.json
    KhaozEngine.MapDoc.Tests/Spaces/MapPrismMembershipTests.cs
    KhaozEngine.MapDoc.Tests/Spaces/MapPrismCoverageTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabCaveFixtures.cs
    docs/design/WORLD-AUTHORING-R2-CAVES-SCALE-2026-10-06.md
)
wa_finish t4 "${wa_r5_t4_paths[@]}" &&
git add -- "${wa_r5_t4_paths[@]}" &&
git commit --only -m "feat(mapdoc): nest transformed prism rooms in canonical spaces" -- "${wa_r5_t4_paths[@]}"
```

### Task 5: Local support, doorway shapes, bounds and tile ownership

**Files:**
- Create: `KhaozEngine.MapDoc.Physics/MapPrefabSupportBounds.cs`, `MapBackendPoseAdapter.cs`. Consume and harden Tasks 2/3's MapSupportResource codec and MapEffectiveSupport installation with direct missing-Y proofs.
- Modify: `KhaozEngine.MapDoc.Physics/MapAssetShapes.cs:46-64`, `MapTerrainPhysics.cs:107-146` and chunk inputs, `MapPlacementGeometry.cs:64-126`, `MapWorldBuilder.cs:50-93`, `MapBuiltWorld.cs:80-158`, `MapResidencyOwnership.cs:45-96` and `:152-205`, `MapNavTiling.cs:75-128` and `:225-270`, `MapPhysicsRegistration.cs:101-112`, `README.md`.
- Modify: Task 2's binding planner and Task 3's expansion/geometry for SupportResourceIds. `KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj` links self-contained public Prefabs/*Fixtures.cs source files, without a test-project reference or TileWorld edge. `KhaozEngine.MapDoc/KhaozEngine.MapDoc.csproj:15-18` adds InternalsVisibleTo for KhaozEngine.MapDoc.Physics.Tests, so linked identity/geometry fixture helpers compile.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapSupportResourceTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPrefabSupportTests.cs`, `MapPrefabDoorwayTests.cs`, `MapPrefabResidencyTests.cs`, `PrefabPhysicsFixtures.cs`.

**Interfaces:**
- Consumes released `MapAssetShapes.Read(MapAssetClosure closure, string assetId)` at :51, `MapSurfaceCompiler.Compile(MapSurfaceRef surface, MapSurfacePatch patch) -> MapCompiledPatch` at :14, `MapTerrainPhysics.Compile(MapScopedSurfaces view, MapTerrainChunkPolicy policy) -> MapTerrainChunkSet` at :107.
- Consumes `MapWorldBuilder.Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) -> MapBuiltWorld` at :40, `MapPlacementShapes.Resolve(MapResolvedDocument document)` at MapPlacementGeometry.cs:64, `MapPhysicsRegistration.TryFaceOwner(StaticHandle handle, int faceId, out MapFaceKey face) -> bool` at :101.
- Consumes `MapPhysicalRelations.Clearance(IPhysicsQueryLease lease, IReadOnlyList<MapFramePoint> feetPath, in MoveTuning tuning) -> MapPhysicalResult` at :88, using ContactShell and the controller's released sweep filter.
- Consumes `MapResidencyOwnership.Build(MapBuiltWorld world, MapWorldGrids grids)`, `Affected(MapBuiltWorld world, MapWorldGrids grids, MapNativeEditEffects effects)`, and `MapNavTiling.AffectedTiles(MapBuiltWorld world, MapWorldGrids grids, MapNavTileOptions options, MapNativeEditEffects effects)`.
- Consumes Task 2's MapSupportResource/File and Enumerate, and Task 3's MapEffectiveSupport.Install. No new pure installer lives in Physics.
- Consumes the shared implicit descriptors as the planner/visitor/expansion source. Physics produces `MapPrefabSupportBounds.Bounds(MapEffectiveResources resources) -> MapBox3` and `MapBackendPoseAdapter.Pose(MapEffectiveTransform transform, WorldFrame frame) -> Pose`, with `ErrorMetres(MapEffectiveTransform transform, WorldFrame frame, double sourceRadius) -> double`. Adds get-only `MapAssetShapes.SupportResources: IReadOnlyList<MapSupportResource>` without removing released constructors.
- Fixture produces `PrefabPhysicsFixtures.Build(PrefabFixture fixture) -> MapBuiltWorld`, `Options() -> MapWorldBuildOptions`, and `DoorPath(MapBuiltWorld world, bool throughJamb) -> IReadOnlyList<MapFramePoint>`.

- [ ] **Step 1: Write failing physics and codec tests.**

```csharp
[Fact] public void LocalFloor_HasCanonicalFaceAndOccupiedRoomOwner()
{
    var f = PrefabFixtures.Create("support-resource");
    var world = PrefabPhysicsFixtures.Build(f);
    using var physics = new BepuPhysicsWorld();
    using var registration = MapPhysicsRegistration.Register(world, physics);
    Assert.True(physics.Raycast(new(2, 2, -2), -Vector3.UnitY, 4, out var hit));
    Assert.NotNull(hit.Body);
    using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
    Span<CapsuleIncidentFace> faces = stackalloc CapsuleIncidentFace[256];
    var contact = ((IPhysicsCapsuleFeatures)physics).QueryCapsuleFeature(lease,
        hit.Body.Value, new CapsuleShape(0.2f, 0.4f), Pose.At(new(2, 0.405f, -2)), 0.01f, faces);
    Assert.Equal(CapsuleFeatureStatus.Complete, contact.Status);
    Assert.True(contact.Written > 0);
    Assert.True(registration.TryFaceOwner(hit.Body.Value, faces[0].FaceId, out var face));
    Assert.Equal("prefab/a/Surface/deck%2Fdeck-support%2Ffloor", face.OwnerId);
    var p = PrefabFixtures.Point(world.Document.EffectiveResources!, 2, 0.01f, -2);
    Assert.Equal("prefab/a/Record/room", new MapSpaceMembership(world.Surfaces).Query(p).SpaceId);
}
[Theory] [InlineData(0.8f)] [InlineData(1.2f)]
public void FreeDoorway_ProvesPhysicalClearanceOnly(float scale)
{
    var world = PrefabPhysicsFixtures.Build(PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f)));
    using var physics = new BepuPhysicsWorld();
    using var registration = MapPhysicsRegistration.Register(world, physics);
    using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
    var relation = new MapPhysicalRelations(registration);
    Assert.Equal(MapPhysicalCertainty.Clear, relation.Clearance(lease, PrefabPhysicsFixtures.DoorPath(world, false), MoveTuning.Default).Certainty);
    Assert.Equal(MapPhysicalCertainty.Blocked, relation.Clearance(lease, PrefabPhysicsFixtures.DoorPath(world, true), MoveTuning.Default).Certainty);
    Assert.Equal(1f, MapInteractionPolicy.MinimumVerticalReachHeightMetres);
}
```

Add `TransformedCavePrefabOrLayer_PreservesCollisionNavAndMembership`: a cave-local doorway/floor/ceiling assembly at the far transform retains canonical floor/ceiling hit roles, room ancestry and every crossed nav tile's geometry identity after reload. It does not call a planner or claim a movement route. Rebase physics within 2 km of the proof before origin-relative ray/clearance queries.

Support-resource variant replaces the explicit floor child with placement deck's resource deck-support containing local surface floor. Its binding key is deck/deck-support/floor. The asset has a selection volume and no duplicate collider floor. Room records explicitly anchor/reference that implicit surface key.

Add `CoincidentSupportOwners_Refuse`, `PaintAddsNoPhysicsFaces`, `ScaledLowItem_StillHasOneMetreReachMinimum`, `LocalChunks_Keep1024TrianglesAnd64MetreOffsets`, `RotatedBounds_NameEveryCrossedTileOnce`, `RemovedInstance_InvalidatesOldTiles` and `TerrainSupportReseat_HasAReachableNeighborCounterexample`. The latter constructs the #1348 lead, with a 150 m leaf support-bound to an edited floor at a 64 m tile seam, showing that geometry outside the edited patch really changes. Task 10 proves the transaction emits the required widening.

- [ ] **Step 2: Run red** `wa_test t5-red-doc "$MAPDOC" "FullyQualifiedName~MapSupportResourceTests"`, then `wa_test t5-red "$MP" "FullyQualifiedName~MapPrefabSupportTests|FullyQualifiedName~MapPrefabDoorwayTests|FullyQualifiedName~MapPrefabResidencyTests"`. Expect the released placement-local support refusal, missing provenance or wrong transformed bounds.
- [ ] **Step 3: Implement support resource installation and existing builder consumers.**

Surface resource payload 1 embeds the existing surface ref/patch codec and its exact units, topology and space reference. It declares no analytic terrain. Validate role, bound owner and reference closure. The exact order is closure verification and preflight, MapDoc support decoding/enumeration, bindings/override/remap, precise effective surface/topology installation, effective source capture, R2 missing-Y support resolution, then Physics meshes/statics/bounds. MapDoc resolver calls the Task 3 installer directly after Task 2 enumeration, without a Physics back-edge. Physics consumes those same installed descriptors and never reinserts a surface or independently decodes geometry. SupportResourceIds decode through MapDoc's codec. Remove MapAssetShapes' unconditional refusal only when those resources validate. The pure surface descriptor is installed in MapDoc before sampling. MapTerrainPhysics later makes its canonical faces into statics. Add `ImplicitSupportMissingY_ResolvesInMapDocBeforePhysics` and `ImplicitSupportMissingY_BuilderMatchesResolver` for Task 3's bound-child fixture at near/far sites. Assert installed surface ID, nonempty support result, exact world Y 5 or -450, identical builder pose Y and no allocation.

Support frames declare metres through MapLatticeFrame's units. Source-unit conversion is already encoded there and is not applied again. Collider/render bounds still use the asset's SourceUnitsToMetres once. Add `SourceUnitsAreEncodedInSupportFrames_AndNotAppliedAgain` with source units 0.01, 100 source units per metre, instance scale 1.2 and expected 1.2 m support/collider edge agreement.

Every prefab-attached support resource enumerates implicit child keys under its placement key, resource ID and local surface/record ID. The binding planner and expansion enumerate the same closure children and persist their bindings. Standalone support-bearing placements without materialized attachment rows use deterministic derived string IDs from stable placement ID/resource/local key, with no numeric allocation. Unpacked placements use R5-P22 transferred descriptors and IDs instead. MapEffectiveAttachments checks the disposition before MapEffectiveSupport.Enumerate, so Physics receives only one canonical support set. Their support owner never equals a collider's unrelated face. Refuse independently authored coincident support faces, including a baked collider floor duplicated by a support resource. Do not claim arbitrary primitive edges/vertices map to floor faces.

MapWorldBuilder consumes resolved.EffectiveResources once. Terrain/strip compilation uses effective exact geometry, preserves face keys/roles and selects whole-metre chunk anchors after transform. Keep existing chunk ID grammar, sidedness, triangle and local-extent limits. BackendPoseAdapter alone approximates the precise pose as float frame-local origin/quaternion/scale. ErrorMetres reports origin half-ULP plus source radius times scale error and 2*sin(angular error/2), plus local-coordinate half-ULP. Test near/far adapters against independently transformed vertices and keep semantic support untouched. Placement compounds retain jambs, lintel and offsets with source-unit/instance scale applied once. Interaction raising happens after scale, in world metres. No whole-building box.

Before constructing a complete prefab/unpacked world, run MapSpaceCoverageValidator over its effective complete view and refuse every finding, even without water. Expand remains a structural/diagnostic snapshot operation, so tests can inspect Ambiguous membership, but a complete build never certifies that ambiguity. This does not retroactively alter source geometry for old documents without R5 sections.

World bounds include support, collider/envelope, render/LOD/light bounds and water. Residency has one storage owner per leaf/chunk and membership in every intersected tile, following R3-P14. Nav remains a world-aligned grid consuming transformed geometry. Effects use both old/new effective bounds, with removed owner IDs computed by a pre/post diff. Unknown geometry retains Unbounded semantics and cannot block repair/deletion. Preserve static diagnostics for rotated certified-sweep limitations.

- [ ] **Step 4: Run green** with both Step 2 filters as `t5-green-doc` and `t5-green`, then `wa_test t5-regress "$MP" "FullyQualifiedName~Terrain|FullyQualifiedName~PhysicalRelations|FullyQualifiedName~PlacementGeometry|FullyQualifiedName~NavTiling|FullyQualifiedName~Residency"`. Expect physical openings clear, jambs blocked, canonical face owners and complete changed-tile coverage.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t5_paths=(
    KhaozEngine.MapDoc/Surfaces/MapSupportResource.cs
    KhaozEngine.MapDoc/KhaozEngine.MapDoc.csproj
    KhaozEngine.MapDoc/Prefabs/MapPrefabBindings.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs
    KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveSupport.cs
    KhaozEngine.MapDoc.Physics/MapPrefabSupportBounds.cs
    KhaozEngine.MapDoc.Physics/MapBackendPoseAdapter.cs
    KhaozEngine.MapDoc.Physics/MapAssetShapes.cs
    KhaozEngine.MapDoc.Physics/MapTerrainPhysics.cs
    KhaozEngine.MapDoc.Physics/MapPlacementGeometry.cs
    KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs
    KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs
    KhaozEngine.MapDoc.Physics/MapResidencyOwnership.cs
    KhaozEngine.MapDoc.Physics/MapNavTiling.cs
    KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs
    KhaozEngine.MapDoc.Physics/README.md
    KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj
    KhaozEngine.MapDoc.Physics.Tests/MapPrefabSupportTests.cs
    KhaozEngine.MapDoc.Physics.Tests/MapPrefabDoorwayTests.cs
    KhaozEngine.MapDoc.Physics.Tests/MapPrefabResidencyTests.cs
    KhaozEngine.MapDoc.Physics.Tests/PrefabPhysicsFixtures.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapSupportResourceTests.cs
)
wa_finish t5 "${wa_r5_t5_paths[@]}" &&
git add -- "${wa_r5_t5_paths[@]}" &&
git commit --only -m "feat(mapdocphysics): install transformed prefab support and doorway geometry" -- "${wa_r5_t5_paths[@]}"
```

### Task 6: Partial-face paint over unchanged support triangles

**Files:**
- Create: `KhaozEngine.MapDoc/Surfaces/MapPaintRegions.cs`, `MapPartialPaintCompiler.cs`.
- Modify: `KhaozEngine.MapDoc/Surfaces/MapSurfacePaintOverride.cs:9-54`, `MapSurfaceCompiler.cs` ApplyPaintOverride adapter, Task 3's effective geometry, Task 1's child codec/schema for paint priority.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabPaintTests.cs`, `PrefabPaintFixtures.cs`.
- Modify also: `KhaozEngine.MapDoc/Surfaces/MapSurfaceRef.cs:10-11`, `MapSurfaceSemantics.cs:83-99`, `MapSurfaceJson.cs` and `KhaozEngine.MapDoc/MapNativeValidation.cs:30-90` for the priority field, its default omission and validation.

**Interfaces:**
- Consumes canonical MapCompiledPatch faces/exact vertices and the released internal `MapSurfacePaintOverride.Apply(MapCompiledPatch target, MapSurfaceRef surface, MapSurfacePatch patch)` at :9. Task 3 supplies both target and paint effective geometry.
- Produces `MapPaintRegion(MapFaceKey Face, string PaintId, int Priority, MapPaintCoverage Material, IReadOnlyList<MapExactPoint> Polygon)`, `MapPaintRegionSet(IReadOnlyList<MapPaintRegion> Regions, string Digest)`.
- Produces `MapPartialPaintCompiler.Compile(MapScopedSurfaces view, string targetSurfaceId, MapRefinementLimits? limits = null) -> MapPaintRegionSet`.
- Adds init-only `MapSurfaceRef.PaintPriority: int`, default 0, preserved by codecs, metadata digest and clone. A non-paint surface with nonzero priority refuses.
- Omit priority 0 in both JSON and semantic metadata, preserving old surface digests. Nonzero priority enters the canonical metadata digest and is valid only for PaintOverride.
- Adds `MapEffectiveResources.PaintRegions: IReadOnlyDictionary<string, MapPaintRegionSet>` through its internal constructor, compiled once per affected target surface after the effective view exists.
- Fixture produces `PrefabFixtures.CanonicalPatch(MapEffectiveResources resources, string surfaceId) -> MapCompiledPatch`, `PaintArea(MapPaintRegionSet regions) -> MapExactValue`, `PaintDomainArea(MapEffectiveResources resources) -> MapExactValue`. PaintDomainArea uses the four transformed footprint corners and an independent wide shoelace sum, never compiler region output.

- [ ] **Step 1: Write failing paint tests.**

```csharp
[Fact] public void PartialPaint_LeavesFacesAndSupportByteIdentical()
{
    var plain = PrefabFixtures.Effective(PrefabFixtures.Create("uneven-unpainted"));
    var painted = PrefabFixtures.Effective(PrefabFixtures.Create("uneven-painted"));
    var a = PrefabFixtures.CanonicalPatch(plain, "ground");
    var b = PrefabFixtures.CanonicalPatch(painted, "ground");
    var regions = MapPartialPaintCompiler.Compile(painted.CompleteView(), "ground");
    Assert.Equal(a.ExactVertices, b.ExactVertices);
    Assert.Equal(a.Faces, b.Faces);
    Assert.Equal(a.LegacyFallbackCells, b.LegacyFallbackCells);
    Assert.NotEmpty(regions.Regions);
    Assert.All(regions.Regions, r => Assert.Contains(b.Faces, face => face.Key == r.Face));
    Assert.Equal(PrefabFixtures.PaintDomainArea(painted), PrefabFixtures.PaintArea(regions));
    Assert.InRange(PrefabFixtures.PaintArea(regions).ToDouble(), 3.999996d, 4.000004d);
}
[Fact] public void PaintConflict_RequiresExplicitPriority()
{
    Assert.Throws<MapDocumentException>(() =>
        PrefabFixtures.Effective(PrefabFixtures.Create("equal-priority-paint")));
    var ordered = PrefabFixtures.Effective(PrefabFixtures.Create("prioritized-paint"));
    var regions = ordered.PaintRegions["ground"].Regions;
    Assert.NotEmpty(regions);
    Assert.Equal(new[] { "priority-high", "priority-low" }, regions.Select(x => x.PaintId).Distinct().Order());
    Assert.True(PrefabFixtures.HighPriorityOwnsOverlap(ordered));
}
```

Prioritized-paint authors world paint IDs priority-high and priority-low with different materials, integer priorities 2 and 1, and a positive-area overlap. Both retain nonempty uncovered regions. Equal-priority-paint changes only priority 2 to 1 and fails at expansion.

Uneven-painted is a 2 by 2 m local paint rectangle at non-grid position, yaw 0.371 and scale 1 over a sloping 1 m world lattice. Its ideal area is 4 square metres, with the explicit 0.000004 square-metre rounding bound above. Clipping must conserve the quantized footprint's exact area. Assertions compare physical inputs independent of paint digest. Add `MoveUndoReload_DoesNotWriteWorldMaterialArrays`, `PaintAbsentNotch_LeavesUnderlyingMaterialVisible`, `RenderSubdivisions_LieOnTheirParentTriangle` and `PaintBudgetAndUnrepresentableIntersection_Refuse`.

- [ ] **Step 2: Run red** `wa_test t6-red "$MAPDOC" "FullyQualifiedName~MapPrefabPaintTests"`. Expect the current physical-face boundary refusal or missing material region API.
- [ ] **Step 3: Implement exact clipped material regions and compatibility adapter.**

Clip transformed paint polygons in XZ against each unchanged canonical target triangle using the shared wide rational kernel, then lift each intersection back onto that triangle's plane. Emit only material coverage and parent face keys. Do not copy paint height into target geometry. Subtract higher-priority regions and retain uncovered base material. Equal-priority positive-area conflicting materials refuse, identical coverage coalesces in ordinal ID order. Half-open semantic ownership follows R4-P28, while draw polygons may share closed edges. All emitted polygon points narrow exactly or fail.

Expansion eagerly compiles PaintRegions after effective view construction and refuses invalid conflict before returning resources. Public build and command publication use that same strict boundary. The failure assertion therefore surrounds expansion itself. `HighPriorityOwnsOverlap(MapEffectiveResources resources) -> bool` probes the exact overlap against the independent fixture region expectation, not the compiler priority algorithm.

Retain the whole-face ApplyPaintOverride signature and its released behavior where one material covers a whole face. Its old refusal becomes a diagnostic directing partial consumers to MapPaintRegionSet rather than inventing one material for a split face. Effective view and R8 receive the region set. Physics/support/nav geometry consumes only original faces. Invalidation sets Material/Residency, never Physics solely for paint. Moving/removing paint changes instance state and derived regions, not world cell arrays. R8 owns tessellation and pixels.

- [ ] **Step 4: Run green** with Step 2's filter as `t6-green`, then `wa_test t6-regress "$MAPDOC" "FullyQualifiedName~Paint|FullyQualifiedName~SurfaceCompiler|FullyQualifiedName~Support"`. Expect unchanged support/physical triangles and exact material partition area.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t6_paths=(
    KhaozEngine.MapDoc/Surfaces/MapPaintRegions.cs
    KhaozEngine.MapDoc/Surfaces/MapPartialPaintCompiler.cs
    KhaozEngine.MapDoc/Surfaces/MapSurfacePaintOverride.cs
    KhaozEngine.MapDoc/Surfaces/MapSurfaceCompiler.cs
    KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs
    KhaozEngine.MapDoc/Surfaces/MapSurfaceRef.cs
    KhaozEngine.MapDoc/Surfaces/MapSurfaceSemantics.cs
    KhaozEngine.MapDoc/Surfaces/MapSurfaceJson.cs
    KhaozEngine.MapDoc/MapNativeValidation.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveResources.cs
    KhaozEngine.MapDoc/mapdoc.schema.json
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabPaintTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabPaintFixtures.cs
)
wa_finish t6 "${wa_r5_t6_paths[@]}" &&
git add -- "${wa_r5_t6_paths[@]}" &&
git commit --only -m "feat(mapdoc): clip prefab paint within canonical physical faces" -- "${wa_r5_t6_paths[@]}"
```

### Task 7: Full transformed local water through R4

**Files:**
- Create: `KhaozEngine.MapDoc/Water/MapEffectiveWater.cs`, `MapWaterOccupiedPieces.cs`.
- Modify: `KhaozEngine.MapDoc/Water/MapWaterDomain.cs:12-16` and `:59-130`, `MapWaterCompiler.cs:25-120` and digest composition, `MapWaterContainment.cs:23-110`, `MapWaterFootprints.cs:32-95`, `MapWaterBoundReads.cs`, `MapWaterQuery.cs:66-140`, `MapCompiledWaterBody.cs`, `MapWaterSample.cs:14-16`, `MapWaterClosedContour.cs:7-20`, `MapWaterRegionBuilder.cs` for fragment propagation, `KhaozEngine.MapDoc/MapBoundDocumentValidation.cs:14-74`, Task 3's effective context.
- Modify: `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs:58-93`, `MapResidencyOwnership.cs:77-123`, `MapNavTiling.cs` medium digest inputs.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabWaterTests.cs`, `PrefabWaterFixtures.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPrefabWaterWorldTests.cs`.

**Interfaces:**
- Consumes `MapWaterDomain.Compile(MapWaterDomainDoc doc, MapWaterPlacement? placement = null) -> MapWaterDomain` at :59, `MapWaterCompiler.Compile(MapScopedSurfaces view, MapWaterSet water, MapWaterCompileLimits? limits = null)` at :25, `MapWaterContainment.Validate(MapScopedSurfaces view, MapWaterSet water)` at :23.
- Consumes `MapWaterQuery(MapScopedSurfaces scoped, MapWaterSet water)`, `Sample(MapFramePoint point) -> MapWaterSample`, `Column(MapFramePoint point) -> IReadOnlyList<MapWaterSample>`, Task 4 occupied partitions and Task 3 immutable transform context.
- Produces internal `MapEffectiveWaterBody(MapWaterBodyDoc Source, MapWaterDomain Domain, MapExactValue SurfaceY, string BodyId, IReadOnlyList<MapRecordRef> Covers, string TransformDigest)`, `MapEffectiveWater.Resolve(MapScopedSurfaces view, MapWaterBodyDoc body) -> MapEffectiveWaterBody`.
- Produces internal `MapWaterOccupiedPieces.Of(MapScopedSurfaces view, string spaceId, MapExactXz min, MapExactXz max, MapRefinementLimits limits) -> IReadOnlyList<MapOccupiedPiece>`, delegating to Task 4.
- Existing public query/compiler signatures remain. Add init-only `MapWaterRegion.Fragment: MapOccupiedFragment?`, `MapWaterSample.Fragment: MapOccupiedFragment?`, internal contour FragmentId and footprint/bound data keyed by FragmentId. Effective R5 regions/samples carry nonnull fragments. Produce `MapWaterFragmentBounds(MapOccupiedFragment Fragment, MapBox3 Bounds)` and `MapCompiledWaterBody.FragmentBounds: IReadOnlyList<MapWaterFragmentBounds>`. They discover transforms from the view and snapshot them alongside water. Compiled/sample nominal heights are world heights and every BodyId/Covers is already binding-remapped.
- Produces additive `MapCompiledWaterBody.ClassifyFragment(string fragmentId, MapExactXz point) -> MapWaterRegionKind?`. Released Classify(spaceId,point) keeps single-interval behavior. With several owned fragments it returns their unanimous kind, or Unresolved when kinds differ. Never use Closed precedence across fragments. Queries choose the membership/column fragment before classification.
- Produces `MapWaterQuery.SampleExact(MapExactPoint world) -> MapWaterSample`, sharing the same sample core and exact membership path. Add init-only nullable ExactPoint on MapWaterSample, preserving its positional constructor. Shared transformed boundary tests use exact points, not rounded coordinates claimed to lie on a boundary.

- [ ] **Step 1: Write failing full-path tests.**

```csharp
[Theory] [InlineData(0.8f, 5.3999996f)] [InlineData(1.2f, 5.6000004f)]
public void LocalLevelCoversContourAndBounds_TransformOnce(float scale, float surfaceY)
{
    var f = PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f));
    var r = PrefabFixtures.Effective(f);
    var body = MapWaterCompiler.Compile(r.CompleteView(), r.Water!).Single();
    Assert.Equal(surfaceY, body.SurfaceY);
    Assert.Equal(MapWaterCompileStatus.Complete, body.Status);
    Assert.Equal("prefab/a/WaterBody/pool", body.BodyId);
    Assert.All(body.Regions, region => Assert.Equal("prefab/a/Record/pool-room", region.SpaceId));
    var sample = new MapWaterQuery(r.CompleteView(), r.Water!).Sample(PrefabFixtures.PoolProbe(r));
    Assert.Equal((MapWaterStatus.Wet, body.BodyId, body.SurfaceY),
        (sample.Status, sample.BodyId, sample.NominalSurfaceY));
}
[Fact] public void ParentWater_ExcludesAnUncoveredNestedRoom()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Create("exterior-water-dry-room"));
    var point = PrefabFixtures.Point(r, 2, 1, -2);
    var water = new MapWaterQuery(r.CompleteView(), r.Water!);
    Assert.Equal(MapWaterStatus.Dry, water.Sample(point).Status);
    Assert.Equal("prefab/a/Record/room", water.Sample(point).SpaceId);
    var body = MapWaterCompiler.Compile(r.CompleteView(), r.Water!).Single();
    Assert.Equal(MapWaterRegionKind.Unresolved, body.Classify("outside", point.ExactWorldXz()));
    var parents = MapSpaceIntervals.Column(r.CompleteView(), point).Where(x => x.SpaceId == "outside").ToArray();
    Assert.Equal(2, parents.Length);
    Assert.NotEqual(parents[0].Fragment!.Id, parents[1].Fragment!.Id);
    Assert.Equal(MapWaterRegionKind.Closed, body.ClassifyFragment(parents[0].Fragment!.Id, point.ExactWorldXz()));
    Assert.Equal(MapWaterRegionKind.FreeSurface, body.ClassifyFragment(parents[1].Fragment!.Id, point.ExactWorldXz()));
    Assert.Equal(MapWaterStatus.Wet, water.Sample(PrefabFixtures.Point(r, 2, -1, -2)).Status);
    Assert.Equal(MapWaterStatus.Wet, water.Sample(PrefabFixtures.Point(r, 2, 3.5f, -2)).Status);
}
[Fact] public void DryAndFloodedCaveVolumes_DoNotLeakWaterOrIndoorState()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Cave("dry-flooded"));
    var query = new MapWaterQuery(r.CompleteView(), r.Water!);
    Assert.Equal(MapWaterStatus.Dry, query.Sample(PrefabFixtures.DryCavePoint(r)).Status);
    Assert.Equal(MapWaterStatus.Wet, query.Sample(PrefabFixtures.FloodedCavePoint(r)).Status);
    Assert.NotEqual(new MapSpaceMembership(r.CompleteView()).Query(PrefabFixtures.DryCavePoint(r)).SpaceId,
        new MapSpaceMembership(r.CompleteView()).Query(PrefabFixtures.FloodedCavePoint(r)).SpaceId);
}
```

Declare `PoolProbe/DryCavePoint/FloodedCavePoint(MapEffectiveResources resources) -> MapFramePoint` here. Cave floors at -60 and -40, ceilings -50 and -30, exterior ocean 0 and separate local lake -35 are explicit. The dry room is not a cover. Exterior-water-dry-room uses parent floor -2, dry room [0,3), and exterior water level 4. Assert lower parent wet [-2,0) Closed, room dry [0,3), upper parent wet [3,4) FreeSurface, two different parent fragment IDs in Column and the room as owner at y 1. Exact boundaries 0 and 3 follow [lower,upper). Assert nonempty exact region/fragment sets, contour and FragmentBounds provenance, stable IDs and digest across reload/repacking. Legacy Classify returns Unresolved at that XZ. Separate single-interval R4 controls retain released Closed/free/tangent answers. Add `SharedFloorWaterAndRoomEdge_AgreeExactly`, `CeilingTie_RemainsClosedAfterTransform`, `MissingLocalCoverOrParent_IsUnverifiedNeverDry`, `LocalWaterNeverBecomesAStatic`, `MoveChangesOnlyIntersectedMediumTiles`, `UnrelatedAssetChildEdit_KeepsBodyDigest`, `UnavailableAperture_PreventsCompleteContainment`, and `ExistingCompileLimits_ChargeRotatedAndAbsentBoundWork`. These invoke the full compiler/query/builder, never only a transformed clip helper. Missing-fact theories separately remove the local cover record, prism domain, parent bound and remote anchor from an otherwise valid acquired source. Each valid control first gives Complete compilation and Wet at the selected covered probe. The finite theory uses fault strings local-cover, prism, parent-floor and remote-anchor, asserts expected What/Key/MapPatchStatus from the fixture witness table and the specific compiler Detail/query Detail. Faults must yield the precise MapWaterStatus.MissingGeometry/MapWaterCompileStatus.Incomplete result and exact unavailable witness while unrelated covers resolve. Missing bytes before capture tests expansion refusal, not an acquired-water status.

`UnavailableAperture_PreventsCompleteContainment` acquires both covers, footprints and the portal/link records successfully. Inject failure only when reading the aperture geometry at its declared anchor. Assert that specific read occurred, the compiler Detail and containment findings name that aperture, MapWaterCompileStatus is Incomplete and the acquired view witness names its exact anchor/status, and unrelated cover/footprint/link reads succeed. A missing parent or generic early discovery refusal cannot satisfy the test.

- [ ] **Step 2: Run red** `wa_test t7-red-doc "$MAPDOC" "FullyQualifiedName~MapPrefabWaterTests"`, then `wa_test t7-red "$MP" "FullyQualifiedName~MapPrefabWaterWorldTests"`. Expect untransformed full-body data, parent water leaking through a room partition or absent effective containment.
- [ ] **Step 3: Implement R4 adapters and the shared transform convergence.**

MapWaterPlacement's public fields/signature remain, but its placed path delegates to MapResourceTransform. Preserve exact unplaced lattice geometry. Resolve a local body to its remapped covers, transformed polygon, transformed nominal level and transform digest. Apply the same path in validation, query, full compilation, containment and bounds. Never round rotated vertices back into a saved lattice frame.

Water covers occupied partitions, not raw parent footprints. Subtract uncovered descendants from parent regions, and clip a room's body against its canonical parent/prism intersection. Virtual parent fragments with no positive wet interval at the body's level emit no free surface. Fragments ending below the level are Closed. Preserve FragmentId and both bound owners through clipping, contour emission, wet bounds and digest composition. Geometric refinements never merge two vertical fragments solely because their SpaceId matches. ClassifyFragment filters all footprint/region/contour data by fragment, while legacy classification aggregates explicitly as specified above. This adapter uses the existing interval and height-split kernel. Preserve unplaced R4 terrain-above-water drawing behavior. Explicit multi-space coverage still needs R4 portal/link connectivity below the level, with ties closed. Parentage alone is not a hydraulic connection and covering a parent does not cover a child. Closed/free contour splitting, exact bound reads, bed depths and work limits remain R4 algorithms.

Compile lower and upper canonical effective faces using Task 3 geometry and Task 4 bounds. Preserve R4's MissingGeometry, Incomplete, CapacityExceeded, NotRepresentable and Invalid distinctions and every before-work charge. Digest includes consumed definition, medium, remapped covers, effective transform/policy, consumed geometry metadata and compiled outcome. A local edit outside the body's dependencies leaves its digest unchanged. World water unrelated to the prefab remains unchanged.

TransformDigest and NativeResourceTransforms.SourceDigest use per-child semantic inputs. Never feed the containing prefab SHA, closure hash or whole scoped digest into a compiled body digest. A marker-only asset version change moves whole authored identity but leaves an unrelated local body's digest and wet-tile medium identities unchanged.

Built-world water uses effective data and remains absent from statics. Bounds/residency and nav medium digests use actual effective regions. Owner IDs stay `water:<bound-body-id>`, with the released static collision check. Every editor publication of a water document checks all effective bodies, even when the accepted change is only a prefab, placement or roof edit. Tasks 10 and 11 wire that publication. R5 adds no movement adapter, capsule classifier or optical algorithm.

- [ ] **Step 4: Run green** with both Step 2 filters as `t7-green-doc` and `t7-green`, then `wa_test t7-regress "$MAPDOC" "FullyQualifiedName~Water|FullyQualifiedName~SpaceInterval"` and `wa_test t7-world-regress "$MP" "FullyQualifiedName~WaterWorld|FullyQualifiedName~WaterMovementInputs"`. Expect exact region/query agreement, unaffected R4 fixtures and explicit scoped refusals.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t7_paths=(
    KhaozEngine.MapDoc/Water/MapWaterSample.cs
    KhaozEngine.MapDoc/Water/MapWaterClosedContour.cs
    KhaozEngine.MapDoc/Water/MapWaterRegionBuilder.cs
    KhaozEngine.MapDoc/Water/MapEffectiveWater.cs
    KhaozEngine.MapDoc/Water/MapWaterOccupiedPieces.cs
    KhaozEngine.MapDoc/Water/MapWaterDomain.cs
    KhaozEngine.MapDoc/Water/MapWaterCompiler.cs
    KhaozEngine.MapDoc/Water/MapWaterContainment.cs
    KhaozEngine.MapDoc/Water/MapWaterFootprints.cs
    KhaozEngine.MapDoc/Water/MapWaterBoundReads.cs
    KhaozEngine.MapDoc/Water/MapWaterQuery.cs
    KhaozEngine.MapDoc/Water/MapCompiledWaterBody.cs
    KhaozEngine.MapDoc/MapBoundDocumentValidation.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveResources.cs
    KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs
    KhaozEngine.MapDoc.Physics/MapResidencyOwnership.cs
    KhaozEngine.MapDoc.Physics/MapNavTiling.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabWaterTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabWaterFixtures.cs
    KhaozEngine.MapDoc.Physics.Tests/MapPrefabWaterWorldTests.cs
)
wa_finish t7 "${wa_r5_t7_paths[@]}" &&
git add -- "${wa_r5_t7_paths[@]}" &&
git commit --only -m "feat(mapdoc): compile prefab water against effective occupied spaces" -- "${wa_r5_t7_paths[@]}"
```

### Task 8: Roof concealment intent, local lights and markers

**Files:**
- Create: `KhaozEngine.MapDoc/Interiors/MapRoofIntent.cs`, `MapRoofPolicy.cs`, `KhaozEngine.MapDoc/Prefabs/MapEffectiveAnnotations.cs`. Consume Task 2's MapLightFile/MapPrefabAttachedLights and Task 3 attachment reuse.
- Modify: Task 3's effective view, Task 1's annotation validation/codecs, `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs:80-158`, `MapWorldBuilder.cs:85-93`, `MapResidencyOwnership.cs:45-96`, MapDoc and MapDoc.Physics README.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabAnnotationTests.cs`, `PrefabAnnotationFixtures.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPrefabRoofStaticTests.cs`.

**Interfaces:**
- Consumes MapMembershipResult, its ParentSpaceIds/DomainKeys, remapped roof/group/light/marker records, Task 3 transforms and Task 5 bounds. No Render3D or Scene3D type crosses this boundary.
- Produces in MapDoc.Interiors `MapRoofMode { Auto, AlwaysVisible, AlwaysHidden }`, `MapRoofIntent(string RoofPlacementId, bool ColourVisible, bool CastShadows, bool CameraBoomBlocks, bool SkyPickingBlocks)`, `MapRoofIntentResult(MapMembershipStatus Status, string? SpaceId, IReadOnlyList<MapRoofIntent> Roofs, MapReadWitness Witness)`.
- Produces `MapRoofPolicy.Evaluate(MapEffectiveResources resources, MapScopedSurfaces view, MapFramePoint observer, MapRoofMode mode) -> MapRoofIntentResult`.
- Produces `MapEffectiveLight(int PayloadVersion, string Id, string? OwnerPlacementId, MapExactPoint Position, Vector3 Colour, float Intensity, float Radius, bool CastShadows)`, `MapEffectiveMarker(int PayloadVersion, string Id, string Name, MapExactPoint Position, float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags, string? InstanceId)`.
- Consumes Task 2's exact MapLightFile.Read/Write and MapPrefabAttachedLights.Enumerate interfaces. Produces `MapEffectiveAnnotations.Lights(MapEffectiveResources resources) -> IReadOnlyList<MapEffectiveLight>`, `Markers(...) -> IReadOnlyList<MapEffectiveMarker>`. MapBuiltWorld adds immutable Lights/Markers/RoofLinks/RoofGroups collections and AnnotationDigest.

- [ ] **Step 1: Write failing policy and data tests.**

```csharp
[Fact] public void ExplicitCrossBuildingLink_HidesBothRoofsWithIndependentFlags()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Create("linked-buildings"));
    var result = MapRoofPolicy.Evaluate(r, r.CompleteView(), PrefabFixtures.LinkedRoomPoint(r), MapRoofMode.Auto);
    Assert.Equal(MapMembershipStatus.Resolved, result.Status);
    var linked = result.Roofs.Where(x => x.RoofPlacementId != "shelter-roof").ToArray();
    Assert.Equal(2, linked.Length);
    Assert.All(linked, roof =>
    {
        Assert.False(roof.ColourVisible);
        Assert.True(roof.CastShadows);
        Assert.False(roof.CameraBoomBlocks);
        Assert.True(roof.SkyPickingBlocks);
    });
    Assert.True(result.Roofs.Single(x => x.RoofPlacementId == "shelter-roof").ColourVisible);
}
[Fact] public void LeafLights_TwoOwnersHaveDistinctBoundEmissions()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Create("two-leaf-lamps"));
    var lights = MapEffectiveAnnotations.Lights(r).OrderBy(x => x.Id).ToArray();
    Assert.Equal(new[] { "light-a", "light-b" }, lights.Select(x => x.Id));
    Assert.Equal(new[] { "owner-a", "owner-b" }, lights.Select(x => x.OwnerPlacementId));
    Assert.All(lights, x => Assert.Equal(2.4f, x.Radius));
    Assert.Equal(new MapExactPoint(new(41, 4), MapExactValue.FromSingle(6.2f), new(-25, 8)), lights[0].Position);
    Assert.Equal(new MapExactPoint(new(81, 4), MapExactValue.FromSingle(6.2f), new(-25, 8)), lights[1].Position);
}
[Fact] public void LocalLightAndMarker_PreserveVersionFieldsAndTransform()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f)));
    var lights = MapEffectiveAnnotations.Lights(r);
    Assert.Single(lights);
    Assert.Equal(1, lights.Single().PayloadVersion);
    var marker = MapEffectiveAnnotations.Markers(r).Single();
    Assert.Equal((1, "entry", "entrance", true, "a"),
        (marker.PayloadVersion, marker.Name, marker.Role, marker.Enabled, marker.InstanceId));
    Assert.Equal(new[] { "first", "second" }, marker.Tags);
    Assert.Equal(new MapResourceTransform(r.Transforms[marker.Id]).Point(new(new(0, 1), new(1, 1), new(0, 1))), marker.Position);
}
[Theory]
[InlineData("prism")] [InlineData("parent-floor")] [InlineData("remote-anchor")]
public void UnresolvedObserver_KeepsRoofsVisibleWithStatus(string fact)
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Create("linked-buildings"));
    var p = PrefabFixtures.LinkedRoomPoint(r);
    var complete = MapRoofPolicy.Evaluate(r, r.CompleteView(), p, MapRoofMode.Auto);
    Assert.Equal(new[] { "roof-a", "roof-b", "shelter-roof" }, complete.Roofs.Select(x => x.RoofPlacementId).Order());
    Assert.False(complete.Roofs.Single(x => x.RoofPlacementId == "roof-a").ColourVisible);
    var view = PrefabFixtures.AcquireWithout(r, fact);
    var result = MapRoofPolicy.Evaluate(r, view, p, MapRoofMode.Auto);
    Assert.Equal(MapMembershipStatus.MissingGeometry, result.Status);
    Assert.Equal(new[] { "roof-a", "roof-b", "shelter-roof" }, result.Roofs.Select(x => x.RoofPlacementId).Order());
    Assert.All(result.Roofs, roof => Assert.True(roof.ColourVisible && roof.CastShadows && roof.CameraBoomBlocks && roof.SkyPickingBlocks));
    Assert.Contains(fact, PrefabFixtures.UnavailableIds(view));
    Assert.False(result.Witness.Complete);
}
```

Add `LeafLights_TwoOwnersHaveDistinctBoundEmissions` using two bound placement children lamp-a/lamp-b of one leaf asset with LightResourceIds [lamp-light], verified payload light Id emission, Position (0,1,0), Radius 2, Owner Local Placement $owner and SourceUnitsToMetres 0.01. No explicit Light child is present. Import placement IDs owner-a/owner-b and Light bindings light-a/light-b. At owner origins (10.25,5,-3.125) and (20.25,5,-3.125), yaw 0.371, scale 1.2, assert exactly light-a/light-b, their respective owner IDs, exact positions (10.25,6.199999809265137,-3.125)/(20.25,6.199999809265137,-3.125) and float radius 2.4. This proves metres are not multiplied by 0.01. Assert nonempty bounds and version fields. `ExplicitAndImplicitLights_CoexistWithoutDuplicateKeys` adds explicit light-extra with a distinct key and requires exactly three IDs, while a conflicting implicit key refuses. Task 9 tests unpack/update and Task 10 tests remove/restore through commands. Save/reload keeps IDs and values.

Declare `LinkedRoomPoint(MapEffectiveResources resources) -> MapFramePoint` here. The fixture payload explicitly sets the world/asset inventories that expand to [roof-a, roof-b, shelter-roof], group shared with rooms [room-a, room-b], unrelated group other with [room-c], and links a/b with Roof roof-a/roof-b, empty direct Rooms, Groups [shared], AboveY 3. The shelter has no link. The fixture supplies imported stable world IDs rather than default generated bindings for these records. Show these values in the fixture, not tags. Test observer room-a hides exactly roof-a/roof-b, room-c hides none, overlapping attached groups deduplicate, and every mode emits exactly all three IDs. AlwaysHidden hides the shelter too. Auto keeps it visible. Nonempty statics and registration counts remain identical in every mode. Add `UnresolvedObserver_KeepsRoofsVisibleWithStatus`, `ObserverAtUpperStorey_HidesOnlyAboveLinks`, `AlwaysModes_PreserveShadowAndSkyIntent`, `HidingNeverChangesStaticsOrInteractionGeometry`, `LightBoundsIncludeScaledRadiusAndOwnerOffsetOnce`, and `UnknownRoofRoomOrGroupOrLightOwner_Refuses`. Roof-static proof compares full descriptor digests and registration handle counts before/after every mode. No pixel assertion is implied.

- [ ] **Step 2: Run red** `wa_test t8-red-doc "$MAPDOC" "FullyQualifiedName~MapPrefabAnnotationTests"`, then `wa_test t8-red "$MP" "FullyQualifiedName~MapPrefabRoofStaticTests"`. Expect missing intent/data or linked-room policy.
- [ ] **Step 3: Implement intent and effective annotations.**

Expansion remaps each active asset's explicit Roofs inventory and merges it with standalone BuildingIntent.Roofs, deduplicating the same typed placement ID. Removed children leave no live inventory entry. BuildingIntent.Roofs in the effective view explicitly lists every roof placement, including unlinked roofs. Validate unique entries of kind Placement and require every RoofLink.Roof to occur there. No roof tags or asset-kind guessing. Always modes enumerate that inventory even when there are no links.

RoofLink.Groups contains typed RoofGroup references. Expand its effective room set as the union of its direct Rooms and the Rooms of exactly those attached groups. Groups contain Room references only, never another group. Multiple groups may overlap and deduplicate IDs. Unrelated groups never widen a link. If several links target one inventoried roof, Auto hides when any eligible attached link matches the observer and height. Otherwise it stays visible. Resolve groups into explicit room sets, including rooms in different instances. A link hides its roof in Auto when the resolved observer room belongs to the link/group and observer world Y is below the transformed AboveY. A parent key alone does not hide every descendant roof. Do not infer groups from portals or flood fill. An unlinked open shelter stays visible. AlwaysHidden hides roofs deliberately, AlwaysVisible shows them. Missing/ambiguous observer membership carries that status and conservatively visible Auto intent.

For hidden roofs set colour false, caster true, boom false and sky true. Visible roofs set all four true. Geometry, collision and interaction arrays remain unchanged for every mode. These flags are intent for R8's separate consumers, not a universal hide predicate.

Light resource payload 1 has strict fields and validation: finite nonnegative colour/intensity, positive finite radius and valid optional owner. Explicit and leaf-derived light offsets/radii are metres. Never apply asset SourceUnitsToMetres to them. Compose owner-local offsets through its precise pose exactly once, scale radius once in double then round the output float once, leave colour/intensity unchanged, and retain stable light IDs/shadow intent. Marker payload 1 preserves XYZ, yaw, name, role, enabled and ordered tags, with optional instance ownership. It never becomes MapSpawn. The implicit producer uses Task 2 scoped/bound children, not another annotation-only scan of assets. Both enter effective digest and bounds/residency, sorted by stable ID. Explicit children keep their own keys. A materialized unpacked light uses its transferred descriptor/registry frame once, with OwnerPlacementId retained as identity, without composing the owner again. R6 chooses role registries and spawn projections, R8 renders lights.

- [ ] **Step 4: Run green** with both Step 2 filters as `t8-green-doc` and `t8-green`. Expect both linked roofs hidden with independent flags, unchanged statics and stable effective light/marker records after reload.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t8_paths=(
    KhaozEngine.MapDoc/Interiors/MapRoofIntent.cs
    KhaozEngine.MapDoc/Interiors/MapRoofPolicy.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveAnnotations.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveResources.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabValidation.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs
    KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs
    KhaozEngine.MapDoc.Physics/MapResidencyOwnership.cs
    KhaozEngine.MapDoc.Physics/README.md
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabAnnotationTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabAnnotationFixtures.cs
    KhaozEngine.MapDoc.Physics.Tests/MapPrefabRoofStaticTests.cs
)
wa_finish t8 "${wa_r5_t8_paths[@]}" &&
git add -- "${wa_r5_t8_paths[@]}" &&
git commit --only -m "feat(mapdoc): publish linked roof light and marker intent" -- "${wa_r5_t8_paths[@]}"
```

### Task 9: Pure prefab edits, extraction and lossless unpack

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapPrefabEdits.cs`, `MapPrefabEditPreparation.cs`, `MapInteriorEdits.cs`, `MapPrefabExtraction.cs`, `MapPrefabAssetEdits.cs`.
- Modify: Task 3 MapPrefabMaterializationOverrides.cs for the pure edit integration, Task 1 root validation/codec/schema, MapPrefabTransfers.cs and standalone storey validation, Task 2 MapPrefabLocalMaterializations.cs and Task 3 MapEffectiveAttachments.cs, `KhaozEngine.MapDoc/README.md`.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabEditTests.cs`, `MapPrefabExtractionTests.cs`, `PrefabEditFixtures.cs`. Modify `KhaozEngine.MapDoc.Physics.Tests/MapPrefabSupportTests.cs` for the single installed-support proof. MapDoc keeps no editor/tool/physics dependency.

**Interfaces:**
- Consumes Tasks 1 to 8's verified closure, typed visitor, binding planner and effective geometry/occupied fragments. Before validating an override edit, call `MapPrefabMaterializationOverrides.Reconcile(MapPrefabAsset original, IReadOnlyList<MapPrefabOverride> overrides) -> MapPrefabReconciledAsset` from Task 3, and resolve its result through the same effective-row path.
- Produces abstract `MapPrefabEdit(string Label)` and sealed edits `MapPlacePrefab(string InstanceId, string ResourceId, MapTransform Transform, MapRecordRef? ParentSpace = null, bool NumericLeaves = true)`, `MapMovePrefab(string InstanceId, MapTransform Transform)`, `MapSetPrefabOverride(string InstanceId, MapPrefabOverride Override)`, `MapRestorePrefabChild(string InstanceId, MapPrefabChildKey Child)`, `MapRemovePrefab(string InstanceId)`, `MapUnpackPrefab(string InstanceId)`, `MapUpdatePrefabAsset(string ResourceId, string OldSha256, string NewResourceId, string NewSha256)`.
- Produces `MapPrefabEditResult(MapDocument After, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects)`, `MapPrefabEdits.Prepare(MapDocument document, MapAssetClosure assets, MapPrefabEdit edit) -> MapPrefabEditResult`, pure. It creates candidate reservations, published only if the later command accepts.
- Produces `MapPrefabEdits.PrepareAssetUpdate(MapDocument document, MapAssetClosure beforeAssets, MapAssetClosure afterAssets, MapUpdatePrefabAsset edit) -> MapPrefabEditResult`, pure. Read old children from beforeAssets and new children from afterAssets, check old pins and prepare one after candidate. Set After.NativeAssets to afterAssets.Roots and WriteSet.NativeAssets true along with the instance/binding changes, so staged publication validates against the selected closure. Task 11 supplies both captured closures without ambient lookup.
- Produces abstract `MapInteriorEdit(string Label)` and sealed `MapSetInterior(MapPrismDomain Domain)`, `MapRemoveInterior(string SpaceId)`, `MapSetRoofLink(MapRoofLinkDoc Link)`, `MapSetRoofGroup(MapRoofGroupDoc Group)`. `MapInteriorEdits.Prepare(MapDocument document, MapAssetClosure assets, MapInteriorEdit edit) -> MapPrefabEditResult` is pure.
- Produces `MapPrefabExtraction.Extract(MapDocument document, MapAssetClosure assets, IReadOnlyList<MapPrefabReference> selected, MapTransform origin, string assetId) -> MapPrefabAsset`. Required partial room/floor/roof selections refuse. External references get explicit World slots, internal selected references Local slots. For a selected materialized owner, require every live associated child selected and emit Task 2 MaterializedAttachments with captured explicit child definitions. Rebase the precise owner into the asset origin as PlacementFrame, retain OwnerLocalPose, remap child IDs/anchors/owner refs to local keys and recompute captured local SourceDigest. Do not decode the original attached payload to replace edited values. Empty suppressed edges remain rows.
- Produces `MapPrefabAssetEdits.Replace(MapPrefabAsset asset, MapPrefabChild child, bool remove = false) -> MapPrefabAsset`, pure. Add/remove/replace keys are strict. Restore keeps Previous and its dependency closure. Preserve original rows as input on replacement. After checking original digests, route changed placement/captured-child transform intent through Task 3 reconciliation before refreshing detached descriptor/frame digests. Value-only replacements retain precise frames. A newly written asset version may capture the reconciled frames, while the original asset/leaf bytes remain immutable. Asset-level child removal prunes only that row child, retaining an empty suppressed edge. Required dependent records still refuse a partial removal. Local history restore reinstates the captured child/mapping, while instance remove/restore overrides retain the definition and its stable binding.
- Produces init-only `MapNativeWriteSet.Prefabs`, `BuildingIntent`, `NativeResourceTransforms`, `NumericReservations` booleans, and effects `InstanceIds, ChildIds, RoofIds, LightIds, MarkerIds: IReadOnlyList<string>`, omitted from Describe when empty. Preserve positional constructors. Task 10 completes physical bounds consequences.

- [ ] **Step 1: Write failing pure-edit tests.**

```csharp
[Fact] public void Unpack_PreservesAllTypedReferencesAndIds()
{
    var f = PrefabFixtures.Create("all-kinds");
    var before = PrefabFixtures.Effective(f);
    var result = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a"));
    var after = MapPrefabResolver.Expand(result.After, f.Assets);
    Assert.Equal(before.Placements.Select(x => (x.PlacementId, x.NumericId)),
        after.Placements.Select(x => (x.PlacementId, x.NumericId)));
    Assert.Equal(before.Water!.Digest(), after.Water!.Digest());
    Assert.Equal(PrefabFixtures.SemanticIntent(before), PrefabFixtures.SemanticIntent(after));
    Assert.Equal(before.Surfaces.AllRecords().Select(x => x.Id).Order(),
        after.Surfaces.AllRecords().Select(x => x.Id).Order());
    Assert.Equal(before.BuildingIntent.Storeys.Select(x => (x.Id, x.Name)),
        after.BuildingIntent.Storeys.Select(x => (x.Id, x.Name)));
    Assert.Equal(before.Placements.Count, after.Placements.Count);
}
[Fact] public void RoomlessPrefab_PlacesInSpacelessMap()
{
    var f = PrefabFixtures.Create("spaceless-roomless");
    var result = MapPrefabEdits.Prepare(f.Document, f.Assets,
        new MapPlacePrefab("canopy", "roomless-canopy", new MapTransform(Vector3.Zero, 0, 1)));
    Assert.Null(result.After.Prefabs!.Instances.Single().ParentSpace);
    Assert.NotEmpty(MapPrefabResolver.Expand(result.After, f.Assets).Placements);
    Assert.Throws<MapDocumentException>(() => MapPrefabEdits.Prepare(f.Document, f.Assets,
        new MapPlacePrefab("room", "room-bearing", new MapTransform(Vector3.Zero, 0, 1))));
}
```

Add `MaterializedOverridePreparation_SaveReloadPreservesFramesAndIds` in MapPrefabEditTests first unpacking and completely extracting the changed attachment fixture through the actual pure APIs, then using Task 3's four override variants through actual MapSetPrefabOverride preparation, with its independent coordinate table, one support owner/emission and identical bindings before/after reload. The effective frame changes while immutable asset/row/leaf bytes remain equal. Use remove overrides retaining Previous and MapRestorePrefabChild to test dormant transform intent, suppressed edges and reuse of the same IDs. Task 10 supplies the actual history undo/redo proof for these pure preparations.

Add `MaterializedUnpackExtractReloadReplace_PreservesCapturedSupportAndLight`. The fixture has one deck with advertised support and light resources, no explicit light, a captured floor edited to 0.5 m (50 height units with 1/100 m units), and a captured emission edited to Position (0,2,0), Radius 3. Original resource values are floor 0, Position (0,1,0), Radius 2. Unpack, select the owner and all materialized Surface/Record/Light children, extract, Write/Read the asset, publish verified fixture bytes and place at a separate site. Require exactly one bound floor owner and one emission, captured values and precise frames, Local row refs/anchors, unchanged shared resource digests and no regenerated implicit child. Two rows (Support and Light) survive the asset round-trip. Missing one live selected child is a separate partial-selection refusal. Remove/restore the captured light through instance overrides and require no emission while removed, then the same light ID and captured radius/offset on restore after save/reload.

Declare `PrefabFixtures.MaterializedExtraction() -> PrefabMaterializedExtractionFixture`, a fixture record with Document, Assets, Selection and Origin of the exact Extract argument types, plus `PlaceExtracted(MapPrefabAsset asset) -> MapEffectiveResources` and `AssertCapturedMaterializedSet(MapEffectiveResources resources) -> void`. Helpers build verified invented bytes and probe actual support/annotations, never compute the expected values from resource originals.

```csharp
[Fact] public void MaterializedUnpackExtractReloadReplace_PreservesCapturedSupportAndLight()
{
    var f = PrefabFixtures.MaterializedExtraction();
    var asset = MapPrefabExtraction.Extract(f.Document, f.Assets, f.Selection, f.Origin, "captured");
    var reload = MapPrefabFile.Read(MapPrefabFile.Write(asset), "captured");
    Assert.Equal(2, reload.MaterializedAttachments.Count);
    Assert.All(reload.MaterializedAttachments, x => Assert.Equal(MapPrefabReferenceScope.Local, x.Placement.Scope));
    PrefabFixtures.AssertCapturedMaterializedSet(PrefabFixtures.PlaceExtracted(reload));
}
```

Add the actual support-resource variant with deck/deck-support/floor and an embedded support record. `UnpackAttachedSupport_HasExactlyOneProducer` compares the exact sorted surface IDs, patch keys, record IDs/counts and MapSupportQuery candidates before and after unpack and reload, including canonical face owners. Require a nonempty candidate set and exactly one surface `prefab/a/Surface/deck%2Fdeck-support%2Ffloor`. The resource authors embedded boundary-chain ID deck-edge and its declared floor anchor. Compare its exact bound Record ID and require one occurrence, rather than equality of potentially empty sets. Arm the attachment payload source to fail after capture. The standalone expansion still reuses the persisted descriptors without a read. Task 5's Physics consumer is checked by `UnpackedSupport_StaticsAndFaceOwnersStaySingle`: nonempty exact static descriptor IDs/digests and face owners match before/after. Updating the former prefab's support/light resource version changes live instances only, never the transferred descriptors or IDs.

Use a floor-only map with no MapSpaceDoc and a roomless shelter/furniture asset without parent-dependent references. The room-bearing refusal names the missing parent before reservations change. Add `SameTextReferences_ExtractAndUnpackRetainScope`, `StoreyUnpack_ReloadKeepsIdNameAndRoomOwner`, `EmbeddedSupportRecord_UnpacksOnce`, `UnpackAttachedSupport_HasExactlyOneProducer`, `ImplicitLeafLights_UnpackAndAssetUpdateKeepOwners`, `RemovedPreviousOverride_RestoreKeepsCustomization`, `Extract_RefusesPartialRoomOrDanglingRoof`, and `PrepareReject_DoesNotMutateInputOrAllocator`. A later update to the former prefab must leave unpacked storey/children unchanged. `SemanticIntent(MapEffectiveResources resources) -> string` is a test-only canonical value projection of exact effective roofs/links/groups/lights/markers/storey ID and Name. It excludes instance provenance which deliberately transfers. It never reuses the product intent digest. Compare storey ownership separately against the null standalone owner.

- [ ] **Step 2: Run red** `wa_test t9-red "$MAPDOC" "FullyQualifiedName~MapPrefabEditTests|FullyQualifiedName~MapPrefabExtractionTests"`, then `wa_test t9-red-support "$MP" "FullyQualifiedName~MapPrefabSupportTests"`. Expect missing preparation/unpack homes or lost IDs, not malformed geometry.
- [ ] **Step 3: Implement detached preparation, extraction and each standalone home.**

ParentSpace is optional for assets whose resources need no authored context. Require a valid authored parent only for $parent, prism rooms, water covers or other explicit parent-dependent resources. No tags, implicit exterior or caller-dependent scope guessing. An unused roomless local editing context needs no parent. The planner enumerates implicit support children identically to expansion. Retain Removed instance tombstones and all reservations. Extraction and unpack preserve the explicit roof inventories, remapping local entries to world placement IDs. Undo of first placement in Task 10 retains the high-water mark and accepted bindings, redo reuses them.

| Child kind | Exact unpack home and identity |
| --- | --- |
| Placement | Placements, original PlacementId/NumericId and exact effective pose registry binding |
| Surface | Surfaces.Refs/Patches with original remapped ID and NativeResourceTransforms entry |
| Record | Owning standalone surface patch Records at remapped anchor, original ID and transform registry entry |
| Storey | BuildingIntent.Storeys, MapStoreyDoc payload 1, original reserved ID/Name, InstanceId null |
| WaterMedium | Water.Media, original ID |
| WaterBody | Water.Bodies, original ID/remapped covers and transform registry entry |
| Light | BuildingIntent.Lights, original ID/remapped owner and transform registry entry |
| Marker | BuildingIntent.Markers, original ID/fields and transform registry entry |
| RoofLink | BuildingIntent.RoofLinks, original ID/remapped Roof/Rooms/Groups and transform registry entry |
| RoofGroup | BuildingIntent.RoofGroups, original ID/remapped Rooms |

Materialize each effective child exactly once, including implicit support Surface/Record and Light children. For every attached edge add MapPrefabAttachmentTransfer with the placement's transferred world ID, verified resource ID/SHA, Kind and the complete live materialized resource-local-to-world child map. Removed implicit keys remain reserved on the instance tombstone. An empty materialized row still suppresses that whole asset edge, so absent children cannot be recreated by enumeration. OwnerLocalPose is the precise child frame relative to the placement before point freezing. Anchor names the owning patch for a Record and is null for other kinds. LocalChild is the original typed implicit key, WorldId the transferred bound ID. Surface children keep their patch keys inside the captured descriptor. SourceDigest covers the captured standalone semantic descriptor. The existing immutable leaf AssetId remains, and shared bytes never change.

R5-P24 extraction preserves this world disposition as asset-local rows, including precise frames and the edited explicit definitions. Re-place suppresses the advertised edges before discovery, and unpack of that new instance regenerates world transfer rows from its bound explicit children, never from the original support/light bytes.

R5-P22 reuse is authoritative for that (placement,kind,resource) edge even if its asset still advertises SupportResourceIds/LightResourceIds. MapEffectiveAttachments.Reuse validates the existing standalone IDs/anchors/digests and registers their association, without inserting them again into the effective child or surface collections. It never rereads the attached payload or creates a support-free variant. Unassociated ordinary placements still expand their declared resources normally. On later move/rotate/scale or reseat, Task 10 composes the owner's updated precise pose with persisted OwnerLocalPose, updates child registry frames and digests and retains each descriptor. Explicit lights not belonging to that edge remain separate.

Remove retires the placement transfer and attachment rows and retires/removes their materialized child descriptors/transforms, retaining every ID reservation. The existing remove command's Revert/history undo reinserts the exact captured descriptors, IDs and association. Compare separately loaded saved documents while retaining that in-memory command state. Session Open resets history as already specified. External required references to a removed support owner may make removal refuse under normal validation, with no partial retirement. The removal positive control uses a roomless resource with no dependent room. Stable-ID remap updates PlacementId, owner references and association digests without changing the children's IDs. Unpack undo removes the materialized rows/standalone owners and restores the original instance/bindings. A former-prefab update never enumerates or replaces an unpacked edge. Rewrite prism InstanceId to empty and room StoreyId to its transferred standalone ID. Transfer live binding ownership, keep removed reservations on the tombstone and refuse any duplicate. No rotated lattice resampling. Registry binary64 poses preserve the pre-freeze composition and per-child SourceDigest, so unpack's semantic geometry/water/intent matches before. The authored source-provenance identity may change as ownership transfers. Only required references are remapped. Never duplicate expanded data in a live instance and standalone home simultaneously.

- [ ] **Step 4: Run green** with Step 2's filter as `t9-green`, then `wa_test t9-regress "$MAPDOC" "FullyQualifiedName~Identity|FullyQualifiedName~Binding|FullyQualifiedName~Water|FullyQualifiedName~Prism"`. Expect exact typed IDs/fields, single ownership, valid storeys and input immutability. Run `wa_test t9-support "$MP" "FullyQualifiedName~MapPrefabSupportTests"` for nonempty unpacked statics and face-owner equality.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t9_paths=(
    KhaozEngine.MapDoc/Prefabs/MapPrefabMaterializationOverrides.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabLocalMaterializations.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabTransfers.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveAttachments.cs
    KhaozEngine.MapDoc.Physics.Tests/MapPrefabSupportTests.cs
    KhaozEngine.MapDoc/Editing/MapPrefabEdits.cs
    KhaozEngine.MapDoc/Editing/MapPrefabEditPreparation.cs
    KhaozEngine.MapDoc/Editing/MapInteriorEdits.cs
    KhaozEngine.MapDoc/Editing/MapPrefabExtraction.cs
    KhaozEngine.MapDoc/Editing/MapPrefabAssetEdits.cs
    KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs
    KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs
    KhaozEngine.MapDoc/Interiors/MapStoreyRecords.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabValidation.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapDoc/mapdoc.schema.json
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabEditTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabExtractionTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabEditFixtures.cs
)
wa_finish t9 "${wa_r5_t9_paths[@]}" &&
git add -- "${wa_r5_t9_paths[@]}" &&
git commit --only -m "feat(mapdoc): prepare prefab edits extraction and unpack" -- "${wa_r5_t9_paths[@]}"
```

### Task 10: Native commands, root snapshots and effects

**Files:**
- Create: `KhaozEngine.MapEditor/NativePrefabCommands.cs`, `NativeInteriorCommands.cs`, `NativeCompleteSnapshotCommands.cs`, `NativePlacementResourceState.cs`.
- Modify: `EditorCommands.Placements.cs:174-181,231-236,286-291`, `EditorCommands.NativePlacements.cs:22-58`, `NativePlacementTransaction.cs:39-84`, `NativePlacementCommands.cs:22-103`, `EditorHistory.cs:27-112` for matching closure selection, `NativeDocumentSnapshot.cs:13-78,104-130`, `NativeDocumentTransaction.cs:31-85`, `NativePlacementReferences.cs:9-16`, `NativePlacementEffects.cs`, `NativeTerrainCommands.cs`, MapEditor README.
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativePrefabCommandTests.cs`, `NativeSnapshotPublicationTests.cs`, `NativeUnpackedPlacementTests.cs`, `PrefabEditorFixtures.cs`. Modify `KhaozEngine.MapEditor.Tests/MapDoc/NativeDocumentTransactionTests.cs` for deferred acceptance without adding a tool publication path yet.

**Interfaces:**
- Consumes released `INativeDocumentCommand.Prepare(MapDocument candidate, bool undo) -> NativeDocumentPreparation`, whose Accept action mutates command state, and `NativeDocumentTransaction.Run(MapDocument document, IEditorCommand command, bool undo, MapAssetClosure? assets, MapDocRegistry? registry = null, bool localOnly = false, INativePlacementBounds? placementBounds = null)` at :15-33.
- Consumes `EditorHistory.Execute(MapDocument doc, IEditorCommand command)`, `Undo(MapDocument doc) -> bool`, `Redo(MapDocument doc) -> bool`, `BindNativeAssets(MapDocument document, MapAssetClosure assets, INativePlacementBounds? placementBounds)` at :79,89,102,32. Preserve all public signatures.
- Consumes `INativePlacementBounds.Bounds(MapDocument document, MapAssetClosure assets, MapDocRegistry registry, IReadOnlyList<string> placementIds) -> NativePlacementExtent` at NativePlacementEffects.cs:29.
- Produces internal `INativeAssetClosureCommand.Closure(bool undo) -> MapAssetClosure` for captured before/after closures. EditorHistory selects it before Stage and rebinds only after successful publication/Accept, including direct Execute/Undo/Redo. No closure accessor mutates command state.
- Produces internal `INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) -> NativeDocumentPreparation`. Stage supplies its selected verified closure as a call argument, not an accepted-state binding mutation. Selector prefers this interface, then released INativeDocumentCommand, then the placement adapter. Preserve released legacy Prepare(candidate,undo) and its adapter for resource-independent commands.
- Produces `PrefabEditCommand(MapPrefabEdit edit)` and `InteriorEditCommand(MapInteriorEdit edit)`, EditorCommand/INativeBoundDocumentCommand, using Task 9 pure preparation and accepted before/after snapshots. A direct null-closure Apply/Revert refuses clearly before mutation. Bound EditorHistory/session paths work with fresh commands, without constructor capture or ambient state.
- Produces internal `MapNativePublicationMode { NamedWriteSet, CompleteSnapshot }`, init-only NativeDocumentPreparation.Mode default NamedWriteSet and `NativeStagedPublication(MapDocument Candidate, MapNativeWriteSet WriteSet, MapNativePublicationMode Mode, MapAssetClosure? Assets, MapNativeEditEffects Effects, Action Accept)`. Produces `NativeDocumentTransaction.Stage(MapDocument document, IEditorCommand command, bool undo, MapAssetClosure? assets, MapDocRegistry? registry = null, bool localOnly = false, INativePlacementBounds? placementBounds = null) -> NativeStagedPublication`.
- Stage with localOnly false requires a verified closure. With localOnly true, null is valid for released water/placement commands and uses ValidateLocal, preserving local direct Run calls and Unbounded effects where sizing needs bytes. An INativeBoundDocumentCommand with null refuses `Prefab editing requires a verified asset closure.` before preparation, even in local-only mode. Stage validates without publication/Accept. Run wraps its exact mode, validation, publication and Accept.
- Produces internal `NativeCompleteSnapshotCommand(string label, MapDocument before, MapDocument after, MapAssetClosure beforeAssets, MapAssetClosure afterAssets, MapNativeEditEffects effects)` in NativeCompleteSnapshotCommands.cs. It implements INativeBoundDocumentCommand/INativeAssetClosureCommand, sets Mode CompleteSnapshot and an empty named write set, and stages the whole captured before or after document. NumericIdHighWaterMark never decreases and retained reservation/tombstone/retired-transfer state is merged without recreating live owners. Task 11 uses this command for generic native callbacks.
- Produces internal NativePlacementResourceState in its own file with `Capture(MapDocument document, string placementId) -> NativePlacementResourceState`, `Restore(MapDocument document, NativePlacementResourceState state) -> void`, `ApplyPose(MapDocument document, string placementId, MapPlacementPoseFields fields) -> MapNativeWriteSet`, `Reseat(MapDocument document, MapAssetClosure assets) -> MapNativeWriteSet`. Flags MapPlacementPoseFields are None, X, Y, Z, Yaw and Scale. Capture includes precise registry, binding transfers, materialized associations and attached descriptors, not only MapPlacement fields. NativePlacementEdit.Change adds an internal optional MapPlacementPoseFields argument default None, preserving existing internal callers. Adapter returns the helper's widened write set, and captured before/after state survives coalescing.
- Produces internal `INativePlacementHistoryMerge.PrepareMerge(IEditorCommand newer, NativeStagedPublication publication) -> Action?` in NativePlacementTransaction.cs, implemented by MovePlacementCommand, RotatePlacementCommand and ScalePlacementCommand in EditorCommands.Placements.cs. A compatible same-kind/same-ID merge returns a prepared adoption action, otherwise null. It captures the validated newer after resource state, float targets and the union of affected write sets before publication, with no surviving-command mutation. Adoption preserves the surviving NativePlacementEdit's original before state and replaces only its accepted after state (registry, descriptors, associations, placement and write sets). Task 11 uses the same seam for staged history.
- Extends NativeDocumentSnapshot Clone/Publish/PublishWriteSet to carry Prefabs, BuildingIntent including Storeys/Roofs, NativeResourceTransforms and numeric reservations. NamedWriteSet mode keeps the existing projection/isolation contract. CompleteSnapshot uses whole Clone/Publish, never PublishWriteSet, and validates every root, seam, coverage and water dependency regardless of the empty named write set. The empty set must not skip full coverage or seam checks. Include all released semantic globals (Terrain/features/biomes, ScatterLayers, CompanionLayers, Exclusions, ScatterOverrides, Placements, Spawns, PlayerSpawns, Regions, TerrainOverrides, native metadata/surfaces/water) plus R5 sections. Preserve complete storage context, refuse partial snapshots, retain numeric reservations, and never grow MapNativeWriteSet with global-family flags. Missing bounds preserve Unbounded.

- [ ] **Step 1: Write failing command and effect tests.**

```csharp
[Fact] public void PrefabPlace_UndoRedoRestoresAllocation()
{
    var f = PrefabFixtures.Create("empty-map");
    var history = PrefabEditorFixtures.History(f.Document, f.Assets);
    history.Execute(f.Document, new PrefabEditCommand(new MapPlacePrefab("new", "hut",
        new MapTransform(new(10.25f, 5, -3.125f), 0.371f, 1.2f), PrefabFixtures.ParentRef(f))));
    var accepted = PrefabFixtures.LiveBindings(f.Document).ToArray();
    long mark = f.Document.NumericIdHighWaterMark;
    Assert.True(history.Undo(f.Document));
    Assert.Equal(mark, f.Document.NumericIdHighWaterMark);
    Assert.True(history.Redo(f.Document));
    Assert.Equal(accepted, PrefabFixtures.LiveBindings(f.Document));
}
[Fact] public void FloorReseat_InvalidatesEveryChangedNeighborTile()
{
    var (f, edit) = PrefabEditorFixtures.SupportReseat();
    var options = new MapWorldBuildOptions(PrefabFixtures.Options(), "fixture", new());
    var before = MapWorldBuilder.Build(f.Document, f.Assets, options);
    var history = PrefabEditorFixtures.History(f.Document, f.Assets);
    history.Execute(f.Document, new TerrainEditCommand(edit));
    var after = MapWorldBuilder.Build(f.Document, f.Assets, options);
    var grids = new MapWorldGrids(64f, 2, 4, Vector2.Zero);
    var nav = new MapNavTileOptions(0.5f, "test-profile", "test-controller");
    var a = MapNavTiling.Partition(before, grids, nav).ToDictionary(t => t.Coord);
    var b = MapNavTiling.Partition(after, grids, nav).ToDictionary(t => t.Coord);
    var changed = a.Keys.Union(b.Keys).Where(c => !a.ContainsKey(c) || !b.ContainsKey(c) ||
        a[c].CaptureIdentity != b[c].CaptureIdentity).ToArray();
    Assert.Contains(changed, c => c != new MapNavTileCoord(0, 0));
    var affected = MapNavTiling.AffectedTiles(after, grids, nav, history.LastNativeEffects!).ToHashSet();
    Assert.All(changed, c => Assert.Contains(c, affected));
    Assert.False(history.LastNativeEffects!.Invalidates.HasFlag(MapNativeInvalidation.Unbounded));
}
```

Add `MaterializedOverrideHistory_UndoRedoRemoveRestorePreservesFrames` in NativePrefabCommandTests using the re-extracted fixture and all four Task 3 variants. Execute PrefabEditCommand(MapSetPrefabOverride), save/reload a separate document and assert Task 3's independent moved coordinates, one captured support owner/emission and stable binding IDs. Undo returns to the captured base frames, redo to the moved frames. A remove override preserves Previous, removes the live child or owner/attachments without implicit regeneration, and restore returns the previous moved frames/IDs. Undo/redo those remove/restore steps as well. Seal gestures between operations so this proof tests override history rather than coalescing.

Add `BoundPreparation_FreshCommandReceivesClosureAndCanRetry`: execute a fresh PrefabEditCommand through bound EditorHistory, require nonempty allocated bindings and the same effective result as Task 9. A one-shot bounds/validation fault occurs after preparation, asserts source identity/mark/history and command retry state unchanged, then the same object retries successfully with identical planned IDs. `ResourceCommandWithoutClosure_RefusesBeforePreparation` surrounds local-only Stage with null and checks the exact error. `LegacyLocalOnlyWaterAndPlacement_StillApplyRevert` invokes released WaterEditCommand and AllocateNativePlacementCommand Apply/Revert with no bound closure on valid resolver-2 fixtures without prefab/attachment sections, and compares original values plus retained numeric mark. Do not use a malformed missing-byte fixture as this control. Task 11 exercises the same command through its session entry.

`CompleteSnapshot_PublishesEveryRootAndKeepsReservations` constructs valid whole before/after native documents differing in ScatterOverrides density, Exclusions, Terrain, Regions and R5 associations. Through bound EditorHistory assert actual values, authored identity, undo/redo and retained reservation IDs. A rejected complete after-state publishes nothing. `SnapshotWriteSet_OmitsUnlistedRootChanges` stays a separate NamedWriteSet test: an unlisted ScatterOverrides mutation cannot leak into its validated publication.

`UnpackedPreciseLeaf_ExistingCommandsPublishEveryRoot` is a finite theory for MovePlacementCommand, RotatePlacementCommand, ScalePlacementCommand, SetPlacementLabelCommand/RenamePlacementCommand, RemapPlacementIdCommand, RemovePlacementCommand/undo restore and support reseat. Unpack an offset leaf at the far fixture with local origin (4,0,-4). Baseline frozen X/Z are 31460408117/1048576 and -31463533353/1048576. Rotate to 0.5f changes only Yaw, scale to 0.8f only Scale, and move to (30003.25f,-450f,-30006.125f) replaces XYZ. Compare exact frozen coordinates against an independent component-update golden, and untouched binary64 component bits exactly. Each accepted command is saved/reloaded and undone/redone. Assert nonempty old bounds and nonempty new bounds for a live pose change, null new leaf bounds for removal, every changed nav/residency tile, identical retained numeric IDs and one active or retired transfer as appropriate. A second fixture with materialized support/light attachments proves their precise frames follow the owner, descriptor IDs stay fixed, and retired children return once. Genuine dependent-room removal refusal is a separate atomic-rejection case. Test a same-value float rotate/move intent, not only unequal float fields, and merged moves so intent masks and exact captured state cannot be inferred from presentation equality.

Add `MergedAttachedPlacement_RedoEqualsFinalAcceptedSnapshot` in NativeUnpackedPlacementTests for move, rotate and scale on the far unpacked attached leaf. Each pair is one gesture and uses different targets: move (30003.25,-450,-30006.125) then (30003.5,-450,-30006), rotate 0.4 then 0.5 radians, scale 1.1 then 0.8. Compare full document/registry/association/descriptor codec state before the first edit and after the second. One undo returns to the first before state, one redo equals the second accepted state byte-for-byte. Require the merged write-set union and correct bounds/tile effects. A third same-kind follow-up with a nonfinite component or nonpositive scale refuses and cannot change that surviving before/after snapshot, targets, cursor or write set.

Declare `PrefabEditorFixtures.CoalescedAttachedLeaf(string operation) -> (MapDocument Document, MapAssetClosure Assets, IEditorCommand First, IEditorCommand Second, IEditorCommand Rejected)`, constructing those exact targets and no expected results from command internals.

```csharp
[Theory] [InlineData("move")] [InlineData("rotate")] [InlineData("scale")]
public void MergedAttachedPlacement_RedoEqualsFinalAcceptedSnapshot(string operation)
{
    var f = PrefabEditorFixtures.CoalescedAttachedLeaf(operation);
    var h = PrefabEditorFixtures.History(f.Document, f.Assets);
    string before = MapDocumentFile.SaveText(f.Document);
    h.Execute(f.Document, f.First);
    h.Execute(f.Document, f.Second);
    string final = MapDocumentFile.SaveText(f.Document);
    Assert.Throws<MapDocumentException>(() => h.Execute(f.Document, f.Rejected));
    Assert.Equal(final, MapDocumentFile.SaveText(f.Document));
    Assert.True(h.Undo(f.Document));
    Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
    Assert.True(h.Redo(f.Document));
    Assert.Equal(final, MapDocumentFile.SaveText(f.Document));
}
```

PrefabEditorFixtures creates `History(MapDocument document, MapAssetClosure assets) -> EditorHistory`, binds actual NativePlacementBoundsProvider, and `SupportReseat() -> (PrefabFixture Fixture, MapTerrainEdit Edit)`. SupportReseat raises a world floor by 0.5 m at storage tile (0,0), reseating a 150 m alias-backed leaf across 64 m tile seams. It constructs inputs, never computes expected invalidation. Add `StageDoesNotAcceptBeforeOuterPublication`, `EveryChildKind_RemoveRestoreUndoReloadKeepsId`, `StoreyUnpackUndoRedo_ReloadRetainsIdName`, `UnpackUndo_ReclaimsTransferredBindings`, `OtherNativeEdits_PreservePrefabAndIntentRoots`, and `SnapshotWriteSet_OmitsUnlistedRootChanges`. Test payloads reload after each undo/redo, with exactly one live owner per transferred ID.

- [ ] **Step 2: Run red** `wa_test t10-red "$EDITOR" "FullyQualifiedName~NativePrefabCommandTests|FullyQualifiedName~NativeDocumentTransaction|FullyQualifiedName~NativeSnapshotPublicationTests|FullyQualifiedName~NativeUnpackedPlacementTests"`. Expect omitted root snapshots, premature acceptance or missing neighbor effects.
- [ ] **Step 3: Implement focused commands, snapshot closure and precise effects.**

Stage selects the matching closure first, then calls the bound Prepare overload or the released legacy adapter. Preparation captures tentative before/after only in its returned Accept, so a rejected candidate may retry. CompleteSnapshot clones every semantic root, constructs the complete publication candidate and validates all of it. NamedWriteSet first projects exactly its listed writes and validates that projection. Both produce one staged publication with Mode/Assets and defer Accept. Publish uses the selected mode, not a second inference from candidate fields. Retain max numeric high-water plus all accepted dead reservations/tombstones/retired bindings on snapshot undo, never resurrect their live owners.

NativePlacementEdit captures NativePlacementResourceState before authored changes and its exact accepted after-state only on acceptance. EditorCommands.NativePlacements passes explicit intent masks: move replaces X/Z and supplied Y, rotate Yaw, scale Scale, label None. Convert edited floats exactly to binary64 and keep every untouched registry component bit. Null authored Y selects existing R2 support seating, rather than freezing stale Y. After a supported move/reseat, write returned world Y directly and preserve precise XZ/yaw/scale. Remap updates placement registry OwnerId, typed owner references, transfer Binding.WorldId and attachment PlacementId, preserving numeric ID and attached child IDs. Removal/restore follows Task 9's retired-transfer/attachment rules and exact captured descriptor state. Direct local placement paths can update captured descriptors without closure bytes, with Unbounded bounds when necessary.

EditorHistory's native Execute stages the newer command and prepares any compatible INativePlacementHistoryMerge adoption before publication. Only after publication and newer Accept does it invoke adoption and keep one history entry. Task 11 follows that same order after outer publication. EditorCommands.Placements.cs routes native coalescing through this seam, bypassing its float-only TryMerge branch. Preserve analytic TryMerge behavior. An unsupported or unaccepted native follow-up cannot scalar-merge into an accepted native snapshot. Prepared state/write-set unions make adoption nonthrowing. Redo restores the final accepted snapshot, never recomputes it from merged floats or resamples support.

The placement adapter combines PlacementsOnly with the returned additional named patches/surfaces/records and Prefabs/NativeResourceTransforms/BuildingIntent/NumericReservations flags. It cannot discard registry or retirement writes. Source/child/transform digests and effects come from the validated before/after, including attached descriptors. NativeTerrainCommands carries Reseat's registry/attachment write-set widening and snapshots when a floor changes supported Y. Neither pose edits nor support reseat samples from float presentation XZ. Required external support references may refuse removal, with retry/history untouched.

Accept allocation only with validated publication. Failed Stage leaves retry state and source bytes untouched. Snapshots retain tombstones/high-water marks, storeys, transforms and typed slots. NativePlacementReferences.Remap delegates to the shared typed visitor. Coverage and full effective water containment run on every accepted publication affecting R5 roots. R4 full water checks remain on every water-document publication.

Compute old/new resolved leaves including support-bound placements reseated by terrain edits. Add typed instance/child/space/water/roof/light/marker effects and every changed bound. Fix #1348's reachable pre/post resolved-placement diff, including aliases, instead of forcing full rebuilds. Unknown geometry alone yields Unbounded and cannot block repair/deletion. MapEditor keeps no Physics reference. Each command is independently rejectable and usable directly with EditorHistory before Task 11 adds session lifecycle.

- [ ] **Step 4: Run green** with Step 2's filter as `t10-green`, then `wa_test t10-regress "$EDITOR" "FullyQualifiedName~NativeWater|FullyQualifiedName~NativeTerrain|FullyQualifiedName~NativePlacement"`. Expect unchanged direct command APIs, explicit roots and complete tile effects.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t10_paths=(
    KhaozEngine.MapEditor/EditorCommands.Placements.cs
    KhaozEngine.MapEditor/EditorHistory.cs
    KhaozEngine.MapEditor/NativeCompleteSnapshotCommands.cs
    KhaozEngine.MapEditor/NativePlacementResourceState.cs
    KhaozEngine.MapEditor/NativePlacementTransaction.cs
    KhaozEngine.MapEditor/NativePlacementCommands.cs
    KhaozEngine.MapEditor/EditorCommands.NativePlacements.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeSnapshotPublicationTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeUnpackedPlacementTests.cs
    KhaozEngine.MapEditor/NativePrefabCommands.cs
    KhaozEngine.MapEditor/NativeInteriorCommands.cs
    KhaozEngine.MapEditor/NativeDocumentSnapshot.cs
    KhaozEngine.MapEditor/NativeDocumentTransaction.cs
    KhaozEngine.MapEditor/NativePlacementReferences.cs
    KhaozEngine.MapEditor/NativePlacementEffects.cs
    KhaozEngine.MapEditor/NativeTerrainCommands.cs
    KhaozEngine.MapEditor/README.md
    KhaozEngine.MapEditor.Tests/MapDoc/NativePrefabCommandTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/PrefabEditorFixtures.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeDocumentTransactionTests.cs
)
wa_finish t10 "${wa_r5_t10_paths[@]}" &&
git add -- "${wa_r5_t10_paths[@]}" &&
git commit --only -m "feat(mapeditor): publish prefab snapshots and native effects" -- "${wa_r5_t10_paths[@]}"
```

### Task 11: Local asset sessions, publication, native history and tools

**Files:**
- Create: `KhaozEngine.MapEditor/NativePrefabAssetCommands.cs`, `NativeSessionSnapshotCommands.cs`, `MapPrefabLocalSession.cs`, `EditorHistory.NativeStaging.cs`.
- Create: `KhaozEngine.MapEdit.Tool/PrefabService.cs`, `PrefabAssetPublisher.cs`, `MapEditSession.NativeHistory.cs`, `MutationService.Prefabs.cs`, `Tools/PrefabTools.cs`, `Tools/InteriorTools.cs`.
- Modify: `EditorHistory.cs:27-112` Task 10 closure selection plus staging, `MapEditSession.cs:23-93,123-256,295-412,526-546`, `NativeCollisionService.cs:92-99,251-273`, `MutationService.cs` shared/native callback seams, `MutationService.Terrain.cs`, `MutationService.Water.cs`, `NativePlacementBoundsProvider.cs:29-60`, `Tools/McpBootstrap.cs:15-35`, Task 1's strict JSON readers and MapEditor/MapEdit.Tool README. MapEditSession and EditorHistory become partial without changing public signatures. Use partial files for new concerns and preserve baselined size caps.
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativePrefabAssetCommandTests.cs`, `MapEditTool/PrefabServiceTests.cs`, `PrefabWireTests.cs`, `NativeSessionHistoryTests.cs`, `NativeGenericHistoryTests.cs`. Modify existing `MapDoc/NativeCollisionServiceTests.cs:12-75` and extend PrefabEditorFixtures.

**Interfaces:**
- Consumes released `MapEditSession.Mutate<T>(Func<MapDocument, MapDocRegistry, T> fn, bool worldChanged)` at :526, `ApplyNativeAssets(IEditorCommand command, MapAssetClosure assets)` at :78, and WaterApply at MutationService.Water.cs:13. Preserve existing callers.
- Consumes Task 10's `INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) -> NativeDocumentPreparation`, `INativeAssetClosureCommand.Closure(bool undo) -> MapAssetClosure` and `NativeDocumentTransaction.Stage(MapDocument document, IEditorCommand command, bool undo, MapAssetClosure? assets, MapDocRegistry? registry = null, bool localOnly = false, INativePlacementBounds? placementBounds = null) -> NativeStagedPublication`. Produces `PrefabAssetEditCommand(MapPrefabEdit edit, MapAssetClosure before, MapAssetClosure after)`, implementing both interfaces. First forward asset-update preparation calls Task 9 PrepareAssetUpdate with both captured closures. Accepted snapshots supply undo/redo without rediscovery. Collider root commands use the same bound/matching-closure seam.
- Produces internal `NativeSessionSnapshotCommand(string label, MapDocument before, MapDocument after, MapAssetClosure beforeAssets, MapAssetClosure afterAssets, MapNativeEditEffects effects)` for accepted native callbacks lacking a typed command. It delegates to Task 10's NativeCompleteSnapshotCommand and Mode CompleteSnapshot. Capture the whole candidate, never a family-by-family MapNativeWriteSet diff. Keep focused command mode NamedWriteSet.
- Produces internal `EditorHistory.StageExecute(MapDocument document, IEditorCommand command) -> NativeStagedPublication`, `StageUndo(MapDocument document) -> NativeStagedPublication?`, `StageRedo(MapDocument document) -> NativeStagedPublication?`. The returned NativeStagedPublication carries Mode and matching Assets, which the session uses for outer validation and the same mode of publication. Their Accept closes over command acceptance and stack movement but does neither until the session has published. Partial EditorHistory.NativeStaging owns this addition and consumes Task 10's `INativePlacementHistoryMerge.PrepareMerge(IEditorCommand newer, NativeStagedPublication publication) -> Action?`. Prepare its union/adoption before outer publication, invoke it only after successful publication and newer command Accept, and preserve the original before state on one surviving history entry. Select command Closure(undo) when present, otherwise the currently bound closure. Forward the selected closure through Stage before Prepare, then rebind the returned Assets only on successful acceptance.
- Produces `MapPrefabLocalSession(MapPrefabAsset asset, MapAssetClosure assets, MapDocument authoringContext, MapRecordRef? parentSpace = null)`, `Apply(IEditorCommand command) -> MapNativeEditEffects`, `Undo() -> bool`, `Redo() -> bool`, `Export() -> MapPrefabAsset`, `Cancel() -> void`, using a detached native local document and EditorHistory. Consumes Task 2 MaterializedAttachments and Task 9 Extract. Open maps local rows to detached document attachment associations and precise registry frames. Export maps the current captured definitions/frames back to the same local keys and rows, including empty suppressed edges, instead of regenerating children from leaf bytes. Export omits a retired owner row when that owner is no longer an asset child. Captured rows return with history restore.
- Produces `MapPrefabMutationResult(MutationResult Mutation, MapNativeEditEffects Effects)`, `MutationService.PrefabApply(MapPrefabEdit edit)` and `InteriorApply(MapInteriorEdit edit)`, returning that result under the session lock.
- Produces `MapPrefabAssetInfo(string ResourceId, string Sha256, int PayloadVersion, int Children)`, `MapPrefabPublishResult(MapAssetRef Resource, MapPrefabMutationResult? Mutation)` and `PrefabService(MapEditSession session)` with `List() -> IReadOnlyList<MapPrefabAssetInfo>`, `Open(string resourceId, MapRecordRef? parentSpace = null) -> MapPrefabAsset`, `Edit(MapPrefabChild child, bool remove = false) -> MapPrefabAsset`, `Cancel() -> void`, `Save(string resourceId, string relativeDirectory, bool updateOpenMap = true) -> MapPrefabPublishResult`, `Extract(IReadOnlyList<MapPrefabReference> selected, MapTransform origin, string assetId) -> MapPrefabAsset`. Require context only for parent-dependent assets. A referring instance may supply its explicit parent in ordinal order, never an implicit exterior.
- Produces `IMapPrefabPublicationFiles.PublishNew(string path, ReadOnlyMemory<byte> bytes) -> void`, `Checkpoint(string name) -> void`, `PrefabAssetPublisher(MapEditSession session, IMapPrefabPublicationFiles? files = null)` and `Publish(MapPrefabAsset asset, string resourceId, string relativeDirectory, bool updateOpenMap) -> MapPrefabPublishResult`. Files are absent digest-addressed files or verified identical existing bytes under the guarded resource root.
- Produces internal `MapEditSession.ExecuteNativeCommand(EditorCommand command) -> MapNativeEditEffects`, using the same bound Stage/outer-publication entry as services, for fresh/retried command controls.
- Produces `MapEditSession.UndoNative() -> MapNativeEditEffects?`, `RedoNative() -> MapNativeEditEffects?`, one whole native history stack. Expose internal history/closure/event-count snapshots for tests, without a second public mutation path.

- [ ] **Step 1: Write failing session, asset, mixed-history and wire tests.**

```csharp
[Theory]
[InlineData("before-bytes")] [InlineData("after-prefab")] [InlineData("after-manifest")]
[InlineData("before-root-switch")] [InlineData("outer-validation")]
public void AssetPublicationFailure_LeavesSessionAndOtherMapsUntouched(string checkpoint)
{
    var session = PrefabEditorFixtures.Session(PrefabFixtures.Create("two-instances"));
    var before = PrefabEditorFixtures.State(session);
    Assert.Throws<MapDocumentException>(() => PrefabEditorFixtures.PublishMarkerChange(session, checkpoint));
    Assert.Equal(before, PrefabEditorFixtures.State(session));
    Assert.Equal(PrefabEditorFixtures.OtherMapBefore(session), PrefabEditorFixtures.OtherMapNow(session));
}
[Fact] public void MixedNativeHistory_RestoresRootsClosureEffectsAndDirty()
{
    var session = PrefabEditorFixtures.Session(PrefabFixtures.Create("mixed-history"));
    var states = PrefabEditorFixtures.ApplyMixedHistory(session);
    Assert.Equal(4, states.Count);
    for (int i = 2; i >= 0; i--)
    {
        Assert.NotNull(session.UndoNative());
        PrefabEditorFixtures.AssertPublishedState(session, states[i]);
    }
    for (int i = 1; i < 4; i++)
    {
        Assert.NotNull(session.RedoNative());
        PrefabEditorFixtures.AssertPublishedState(session, states[i]);
    }
}
```

PrefabSessionState is an immutable value record with named Identity, canonical document bytes, root pins, bindings, closure hash, history cursor/depth, event count and dirty state. ApplyMixedHistory captures initial plus three accepted states: prefab marker edit, native terrain edit with local-water consequences, collider-height asset change via actual NativeCollisionService.SetHeights. AssertPublishedState compares roots/closure/document and rebuilds effective water, and compares the operation's effects with the expected reverse/forward owner set. Every accepted undo/redo marks dirty true, including return to the initial saved geometry. It is not expected to restore a historical clean flag. Explicitly verify emitted event count advances once and history depth/cursor moves once. A separate variant uses WaterApply as the middle command. Do not let a helper swallow missing collider undo.

Declare `State(MapEditSession session) -> PrefabSessionState`, `ApplyMixedHistory(MapEditSession session) -> IReadOnlyList<PrefabSessionState>`, `AssertPublishedState(MapEditSession session, PrefabSessionState expected) -> void`, `PublishMarkerChange(MapEditSession session, string checkpoint) -> void`, `OtherMapBefore/OtherMapNow(MapEditSession session) -> string`. State comparisons for rejection include counters exactly. Accepted-history comparisons explicitly account for new events/dirty rather than equating historic snapshots wholesale. Add `OuterValidationReject_DoesNotAcceptCommandOrMoveHistory`, `AssetUndoRedo_RebindsMatchingClosure`, `RejectedOrCancelledEdit_LeavesHistoryIdentityAndDirtyUntouched`, `LocalWaterEdit_UsesTheNativeWaterCommand`, `StorageBoundary_ResetsHistoryOnlyAfterSuccess`, and `GuiServiceAndWire_HaveIdenticalDocumentsEffectsAndRejection` for every verb below.

Add `LocalExport_MaterializedRowsPreserveEditedAttachments` in PrefabServiceTests. Open Task 9's extracted asset, edit the captured floor from 0.5 to 0.75 m and captured light Position (0,2,0) to (0,2.5,0), Radius 3 to 4 through the existing local command/edit services. Export and immutable Save/reopen must preserve both local rows, the exact frames/scopes and those values. Re-place has one support owner and one emission. Local remove/undo/redo controls cannot restore resource-original values or change binding keys. Use the shared resource SHA controls from Task 9 and compare exported codec bytes after undo/redo. The wire Save/Open round-trip exercises this same session/export path.

Add `SessionBoundPreparation_UsesTheSameCommandAndRetriesAtomically` through ExecuteNativeCommand and PrefabApply: the command rejected by direct preparation can succeed in the session from the unchanged document, and fresh direct/session edits match exact documents/effects/bindings. Null local-only water/placement controls remain Task 10 regressions.

```csharp
[Theory] [InlineData("scatter")] [InlineData("bake")]
public void GenericSnapshot_ScatterAndBakeSurviveHistoryAndReload(string variant)
{
    var session = PrefabEditorFixtures.Session(PrefabFixtures.Create("native-generic"));
    var before = PrefabEditorFixtures.State(session);
    PrefabEditorFixtures.ApplyGeneric(session, variant);
    PrefabEditorFixtures.AssertGenericRoots(session, variant, true);
    Assert.NotEqual(before.Identity, PrefabEditorFixtures.State(session).Identity);
    session.Save();
    PrefabEditorFixtures.AssertSavedGenericRoots(session, variant, true);
    Assert.NotNull(session.UndoNative());
    PrefabEditorFixtures.AssertGenericRoots(session, variant, false);
    Assert.NotNull(session.RedoNative());
    PrefabEditorFixtures.AssertGenericRoots(session, variant, true);
}
```

Declare `ApplyGeneric(MapEditSession session, string variant) -> void`, `AssertGenericRoots(MapEditSession session, string variant, bool applied) -> void`, `AssertSavedGenericRoots(MapEditSession session, string variant, bool applied) -> void`. Scatter calls actual MutationService.ScatterOverrideEdit with density 1 to 2 on a valid existing shape. Bake calls actual BakeRegion on an invented density-zero layer and adds exactly one Exclusion with unchanged placements. All rules have density 0 and valid registered kit IDs, so the released bake yields no placement lacking a native AssetId. This isolates a valid Exclusions publication from unrelated native placement admission. Assert density 2, unchanged placement IDs/count for this bake, exactly one added rectangular exclusion at (0,0)-(1,1) with Layers [native-bake], untouched unrelated Terrain/Regions, root pins and whole loaded/stored identity. Save/reload a separate document after each accepted forward/undo/redo state, without pretending disk persists history. Live-session Save keeps history and clears dirty, each edit/undo/redo advances one cursor/event and marks dirty. A fresh Open resets history as specified. For both variants, inject outer rejection after callback but before publication and assert exact before document/identity, roots, reservations, closure, stacks, events and dirty. Invalid density is an additional early-refusal control, not the outer-publication proof.

- [ ] **Step 2: Run red** `wa_test t11-red "$EDITOR" "FullyQualifiedName~NativePrefabAssetCommandTests|FullyQualifiedName~PrefabServiceTests|FullyQualifiedName~PrefabWireTests|FullyQualifiedName~NativeSessionHistoryTests|FullyQualifiedName~NativeGenericHistoryTests|FullyQualifiedName~NativeCollisionServiceTests"`. Expect forward-only collider undo, premature acceptance or absent wire adapters.
- [ ] **Step 3: Implement every native publication path's history behavior and atomic acceptance.**

| Released entry point at 06b734c57 | Accepted history behavior |
| --- | --- |
| MutationService.Apply command and factory overloads, session.ApplyNative | Stage the typed command. Existing native placement allocation/label/remap and supported placement edits share one entry. Preserve rejection of commands without a released native path, rather than silently changing their admission |
| TerrainApply and WaterApply | Stage their existing terrain/water commands, with matching effects |
| TerrainEdit | CompleteSnapshot. Terrain scalars Seed/WaterLevel/BiomeBlend/GentleFrequency/GentleAmplitude/DetailFrequency/DetailOctaves |
| ScatterOverrideEdit | CompleteSnapshot. ScatterOverrides[index] Shape/DensityMultiplier/Kinds/Layers, including two-command callbacks as one entry |
| BakeRegion | CompleteSnapshot. Added Placements and covering Exclusions, with retained numeric reservations |
| SculptApply, SculptFlattenRegion, SculptClear | CompleteSnapshot. TerrainOverrides creation/cell/tile writes or deletion |
| FreezeZone | CompleteSnapshot. Added Placements and cleared ScatterLayers, CompanionLayers, Exclusions and ScatterOverrides |
| ApplyNativeAssets, currently NativeCollisionService.SetHeights | Reversible accepted before/after roots, document snapshots, effects and old/new verified closures. Never retain the forward-only command |
| BindNativeAssets | Same roots/hash rebind preserves history. Different accepted closure binding is an explicit history barrier after validation |
| Open, Create and SetWindow | Reset history only after complete replacement/verification succeeds |
| Save | Same semantic document/closure preserves history, clears dirty on success. Failed save leaves everything intact |
| ConvertToTiled, ConvertToSingle and Retile | Successful storage publication is an explicit barrier clearing both stacks. Failed storage publication leaves stacks/closure/dirty unchanged |
| Direct MapEditor EditorHistory Execute/Undo/Redo and NativeDocumentTransaction.Run | Preserve released direct semantics, using Stage then publication then Accept. GUI host binding remains R9 |

Native MapEditSession.Mutate must not call EditorHistory.Execute inside its callback. Under the lock capture before, produce candidate and staged preparation, run outer MapBoundDocumentValidation including effective coverage/water against the candidate's matching closure, then publish document/root and bind closure. Only after that successful publication invoke nonthrowing command Accept and history stack movement, mark dirty/cache and emit one effect event. A thrown callback, Stage or outer validation discards the stage, leaving command retry state and both history stacks untouched. Undo/redo uses the same order and its captured matching closure. Preallocate stack entry/state before publication so final acceptance cannot fail. Generic accepted native callbacks use Mode CompleteSnapshot through the delegating snapshot command. After a callback returns, wrap its full candidate and before document, fully validate and stage that whole after state. Undo/redo restores the entire captured before/after semantic document plus retained reservations and the matching closure. Do not publish the callback first or replay it on undo. A genuine no-op creates no entry/event/dirty transition. Focused named commands continue to publish their projected write set, with isolation unchanged. Session replacement/barriers occur only after successful publication.

Convert NativeCollisionService.NativeAssetRootsCommand into accepted before/after roots and matching closure snapshots. Remove its undo NotSupportedException. Publish immutable collider resources first as already released, then stage the reversible command through the same session history. NativePlacementBoundsProvider resolves effective placement IDs and computes old/new effects using their respective closures. This is required before registering whole-native-history undo verbs.

- [ ] **Step 4: Implement detached local asset editing, immutable publication and thin wire adapters.**

Preserve R5-P24 materialized ownership through local edits, undo/redo, Export, immutable Save and reopen. Refresh local descriptor digests and keep local scopes while owner/child frames change. Local remove/undo uses captured descriptors and suppresses the empty edge while removed.

Existing placement, TerrainEditCommand and WaterEditCommand edit the detached local context. Export preserves keys, scoped slots and local geometry. Local undo/redo replays accepted values. Cancel performs no world edit/file write. File-only Save writes a verified immutable version with no world history entry.

Publish canonical prefab bytes to absent `prefab/<escaped-logical-asset-id>/<sha256>` resource/path, then a digest-addressed replacement manifest. Retain shared leaf declarations without duplicate root IDs, including the pinned assets/resources still used by standalone placements or materialized attachment rows. A new prefab/support/light version uses new digest-addressed declarations. It cannot replace an unpacked descriptor or its source pin. Verify the new complete closure before updating every referring open-map instance, including tombstoned bindings. MapUpdatePrefabAsset switches ResourceId and ResourceSha256 in one PrefabAssetEditCommand. Other maps remain pinned to old bytes. Existing different bytes refuse. Failed switches may leave unreachable immutable files, never overwritten old resources. No cleanup is automatic. Map save remains the existing atomic save.

| Wire verb | Exact adapter signature, metres/radians | Service/command |
| --- | --- | --- |
| prefab_open | `PrefabOpen(string resourceId, string? parentRefJson = null) -> MapPrefabAsset` | Open |
| prefab_edit | `PrefabEdit(string childJson, bool remove = false) -> MapPrefabAsset` | Edit |
| prefab_save | `PrefabSave(string resourceId, string relativeDirectory, bool updateOpenMap = true) -> MapPrefabPublishResult` | Save |
| prefab_extract | `PrefabExtract(string selectedJson, float x, float y, float z, float yaw, float scale, string assetId) -> MapPrefabAsset` | Extract |
| prefab_place | `PrefabPlace(string instanceId, string resourceId, float x, float y, float z, float yaw, float scale, string? parentRefJson = null, bool numericLeaves = true) -> MapPrefabMutationResult` | MapPlacePrefab |
| prefab_list | `PrefabList() -> IReadOnlyList<MapPrefabAssetInfo>` | List |
| prefab_override | `PrefabOverride(string instanceId, string overrideJson) -> MapPrefabMutationResult` | MapSetPrefabOverride |
| prefab_unpack | `PrefabUnpack(string instanceId) -> MapPrefabMutationResult` | MapUnpackPrefab |
| interior_volume_add/set | `InteriorVolumeAdd(string domainJson)`, `InteriorVolumeSet(string domainJson) -> MapPrefabMutationResult` | MapSetInterior, add requires absent ID, set requires present ID |
| interior_volume_remove | `InteriorVolumeRemove(string spaceId) -> MapPrefabMutationResult` | MapRemoveInterior |
| roof_link_set | `RoofLinkSet(string linkJson) -> MapPrefabMutationResult` | MapSetRoofLink |
| prefab_undo/redo | `PrefabUndo()`, `PrefabRedo() -> MapNativeEditEffects?` | Whole native session history |

Tools use ToolGuard.Guard, explicit raw diagnostics and McpBootstrap registration. MapPrefabJson adds closed `ReadChild(string json) -> MapPrefabChild`, `ReadOverride(string json) -> MapPrefabOverride`, `ReadReferences(string json) -> IReadOnlyList<MapPrefabReference>`, `ReadRecordRef(string json) -> MapRecordRef`, `ReadDomain(string json) -> MapPrismDomain`, `ReadRoofLink(string json) -> MapRoofLinkDoc`. int64 IDs remain strings. GUI/service/wire use the same commands. Optional snap changes the authored transform only. R10 owns remaining parity, R9 the GUI host.

- [ ] **Step 5: Run green** with Step 2's filter as `t11-green`, then `wa_test t11-regress "$EDITOR" "FullyQualifiedName~NativeDocumentTransaction|FullyQualifiedName~NativeDocumentService|FullyQualifiedName~NativeWater|FullyQualifiedName~NativePlacement|FullyQualifiedName~NativeTerrain|FullyQualifiedName~NativeCollision"`. Expect all mixed undo/redo closures valid, no rejection acceptance, identical wire outcomes and immutable old-map pins.
- [ ] **Step 6: Format, guards and commit these explicit paths.**

```bash
wa_r5_t11_paths=(
    KhaozEngine.MapEditor.Tests/MapEditTool/NativeGenericHistoryTests.cs
    KhaozEngine.MapEditor/NativePrefabAssetCommands.cs
    KhaozEngine.MapEditor/NativeSessionSnapshotCommands.cs
    KhaozEngine.MapEditor/MapPrefabLocalSession.cs
    KhaozEngine.MapEditor/EditorHistory.NativeStaging.cs
    KhaozEngine.MapEditor/EditorHistory.cs
    KhaozEngine.MapEditor/README.md
    KhaozEngine.MapEdit.Tool/PrefabService.cs
    KhaozEngine.MapEdit.Tool/PrefabAssetPublisher.cs
    KhaozEngine.MapEdit.Tool/MapEditSession.NativeHistory.cs
    KhaozEngine.MapEdit.Tool/MutationService.Prefabs.cs
    KhaozEngine.MapEdit.Tool/MapEditSession.cs
    KhaozEngine.MapEdit.Tool/MutationService.cs
    KhaozEngine.MapEdit.Tool/MutationService.Terrain.cs
    KhaozEngine.MapEdit.Tool/MutationService.Water.cs
    KhaozEngine.MapEdit.Tool/NativeCollisionService.cs
    KhaozEngine.MapEdit.Tool/NativePlacementBoundsProvider.cs
    KhaozEngine.MapEdit.Tool/README.md
    KhaozEngine.MapEdit.Tool/Tools/PrefabTools.cs
    KhaozEngine.MapEdit.Tool/Tools/InteriorTools.cs
    KhaozEngine.MapEdit.Tool/Tools/McpBootstrap.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativePrefabAssetCommandTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/PrefabEditorFixtures.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/PrefabServiceTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/PrefabWireTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/NativeSessionHistoryTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeCollisionServiceTests.cs
)
wa_finish t11 "${wa_r5_t11_paths[@]}" &&
git add -- "${wa_r5_t11_paths[@]}" &&
git commit --only -m "feat(mapedit): publish prefab assets with complete native history" -- "${wa_r5_t11_paths[@]}"
```

### Task 12: Public synthetic village and independent heads

**Files:**
- Create: `KhaozEngine.MapDoc.Tests/Prefabs/PrefabVillageFixtures.cs`, `MapPrefabVillageTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPrefabVillageAcceptanceTests.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/PrefabVillageWorkflowTests.cs`.
- Modify: `KhaozEngine.MapDoc/README.md`, `KhaozEngine.MapDoc.Physics/README.md`, `KhaozEngine.MapEditor/README.md`, `KhaozEngine.MapEdit.Tool/README.md`, `docs/DEPENDENCY-SEAMS.md:863-889`.
- Create test: `KhaozEngine.Tests/ArchitectureTests.Prefabs.cs`, a partial ArchitectureTests class containing the prefab runtime dependency non-edge assertion. Run the existing `KhaozEngine.MapEditor.Tests/MapDoc/NativeManifestAdapterTests.cs` as a regression without modifying it.

**Interfaces:**
- Consumes Tasks 1 to 11, including actual tool commands, effective view and builder. No TileWorld oracle or game input is used.
- Produces `PrefabFixtures.Village() -> PrefabFixture`: four invented definitions (hut, tower, bath, canopy), five instances (two huts, tower, bath, roomless canopy) with an inventoried unlinked canopy roof. Two storeys, cross-instance roof group, optional local water, one explicit extra-furniture override, an unpainted notch, support-resource floor, light and marker fields all participate.
- Produces `PrefabFixtures.VillageProbes(MapEffectiveResources resources) -> IReadOnlyList<MapFramePoint>`, a fixed list of room edges, storey planes, cave/parent points, paint boundaries and wet/dry probes. No generated exhaustive whole-world scan.

- [ ] **Step 1: Write failing acceptance tests.**

```csharp
[Fact] public void IndependentHeads_ResolveIdenticalEffectiveVillage()
{
    var f = PrefabFixtures.Village();
    var text = MapDocumentFile.SaveText(f.Document);
    var client = MapWorldBuilder.Build(MapDocumentFile.LoadText(text), f.Assets, PrefabPhysicsFixtures.Options());
    var server = MapWorldBuilder.Build(MapDocumentFile.LoadText(text), f.Assets, PrefabPhysicsFixtures.Options());
    Assert.Equal(client.BuildHash, server.BuildHash);
    Assert.Equal(client.Statics.Select(x => (x.OwnerId, x.Digest)), server.Statics.Select(x => (x.OwnerId, x.Digest)));
    Assert.Equal(client.Water.Select(x => x.Digest), server.Water.Select(x => x.Digest));
    foreach (var p in PrefabFixtures.VillageProbes(client.Document.EffectiveResources!))
    {
        var a = new MapSpaceMembership(client.Surfaces).Query(p);
        var b = new MapSpaceMembership(server.Surfaces).Query(p);
        Assert.Equal((a.Status, a.SpaceId), (b.Status, b.SpaceId));
        Assert.Equal(a.DomainKeys.ToArray(), b.DomainKeys.ToArray());
        Assert.Equal(a.ParentSpaceIds.ToArray(), b.ParentSpaceIds.ToArray());
        Assert.Equal(MapRoofPolicy.Evaluate(client.Document.EffectiveResources!, client.Surfaces, p, MapRoofMode.Auto).Roofs,
            MapRoofPolicy.Evaluate(server.Document.EffectiveResources!, server.Surfaces, p, MapRoofMode.Auto).Roofs);
    }
}
[Fact] public void Village_ReloadKeepsEveryLeafOnceAndItsClosure()
{
    var f = PrefabFixtures.Village();
    var dir = PrefabFixtures.TempDirectory();
    MapDocumentFile.SaveTiled(f.Document, dir);
    var a = MapPrefabResolver.Expand(f.Document, f.Assets);
    var b = MapPrefabResolver.Expand(MapDocumentFile.LoadTiled(dir), f.Assets);
    Assert.Equal(a.Digest, b.Digest);
    Assert.Equal(a.Placements.Count, a.Placements.Select(x => x.PlacementId).Distinct().Count());
    Assert.Equal(a.Placements.Select(x => x.NumericId), b.Placements.Select(x => x.NumericId));
}
```

Add `WholeBuildingMoveOverrideUndoRedoSaveReload_UsesSharedCommands`, `CanopyHasNoRoomAndAutoKeepsItVisible`, `TwoStoreysAndCrossBuildingConcealment_StayDistinct`, `ScopedVillageMatchesComplete_WhenWitnessIsComplete`, `MissingClosureFailsBothHeadsBeforeStaticInstall`, and `NativeBuildingRuntime_HasNoTileWorldOrGpuEdge`. Workflow includes 0.371 yaw, non-grid XYZ and scales 0.8/1.2, local water/paint/marker edits, unpack undo and immutable asset update. Compare value sequences explicitly rather than record equality over list references.

- [ ] **Step 2: Run red** `wa_test t12-red-doc "$MAPDOC" "FullyQualifiedName~MapPrefabVillageTests"`, then `wa_test t12-red "$MP" "FullyQualifiedName~MapPrefabVillageAcceptanceTests"` and `wa_test t12-red-editor "$EDITOR" "FullyQualifiedName~PrefabVillageWorkflowTests"`. A new test that already passes records that fact, then the named missing interaction scenario must fail before any fix. Never alter an expected result to manufacture red.
- [ ] **Step 3: Build the synthetic fixture, close only cross-task contract gaps and document consumption.**

Exercise public resources from a fresh byte source and separately loaded documents, not shared mutable snapshots. Fix reachable R5 interface failures in their owning modules with a focused regression. Preserve private-data exclusion and no new runtime dependency edges. Document format-7 identity movement, strict closure, local-support ownership, partial paint, room parenting, explicit AuthoredOpen doors, bound query completeness, roof flags, marker/light payloads and typed command responses. R6 to R11 handoffs name every remaining proof.

- [ ] **Step 4: Run green** with all Step 2 filters as `t12-green-doc`, `t12-green`, `t12-green-editor`, then `wa_test t12-architecture "$RUMP" "FullyQualifiedName~ArchitectureTests"`. Expect identical independent heads and native dependency non-edges. No complete navigation route or pixels are claimed.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t12_paths=(
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabVillageFixtures.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabVillageTests.cs
    KhaozEngine.MapDoc.Physics.Tests/MapPrefabVillageAcceptanceTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/PrefabVillageWorkflowTests.cs
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapDoc.Physics/README.md
    KhaozEngine.MapEditor/README.md
    KhaozEngine.MapEdit.Tool/README.md
    docs/DEPENDENCY-SEAMS.md
    KhaozEngine.Tests/ArchitectureTests.Prefabs.cs
)
wa_finish t12 "${wa_r5_t12_paths[@]}" &&
git add -- "${wa_r5_t12_paths[@]}" &&
git commit --only -m "test(mapdoc): prove free prefab village on both headless heads" -- "${wa_r5_t12_paths[@]}"
```

### Task 13: Private shipped-building comparison with complete input closure

**Files:**
- Create: `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedBuildingInputs.cs`, `ShippedBuildingComparison.cs`, `ShippedBuildingComparisonTests.cs`, `ShippedBuildingInputGuardTests.cs`.
- Modify: `KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj`, `README.md`. Add test-only references to MapDoc.Physics, TileWorld.Physics and Physics.Bepu for source/native physical opening checks. Keep the harness outside KhaozEngine.slnx and every public CI selection.
- Modify: `KhaozEngine.Tests/ArchitectureTests.MapDoc.cs:16-47`, adding a direct-reference assertion for the harness's five test-only projects while preserving outside-solution, unreferenced, nonpackable and TileWorld-oracle boundaries.

**Interfaces:**
- Consumes `PrivateOracleEntry.Run(Action<PrivateOracleInputs, PrivateOracleReport> body)` at PrivateOracleEntry.cs:11, `PrivateOracleInputs.Require(IReadOnlyDictionary<string, string?> environment)` at :42, `PrivateOracleReport.Record(string category, string detailJson)` and `Check(string category, bool ok, string detailJson)` at :60 and :68.
- Consumes `ShippedSourceInventory.ReadWorld(PrivateOracleInputs inputs) -> (TileWorldDocument Document, TileWorldCatalogs Catalogs)` at :30, `TilePrefabFile.Load(string path)` at :65, `TileObjectPlacement.AnchorPosition(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o) -> Vector3` at :38 and `LocalToWorld(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o) -> Matrix4x4` at :52.
- Consumes `TileWorldColliders.Build(TileWorldDocument document, TileWorldCatalogs catalogs, TileColliderOptions? options = null) -> TileWorldColliders` at TileWorld.Physics/TileWorldColliders.cs:91. Pin source collision options in private provenance rather than infer a game server default.
- Produces internal `ShippedBuildingInputs.Read(PrivateOracleInputs inputs) -> ShippedBuildingInputs`, with verified World, Catalogs, Prefabs, MeshBytes and a private Comparison JSON object. It reads only attested paths.
- Produces internal `ShippedBuildingReport(IReadOnlyList<string> Failures, string DetailJson)`, `ShippedBuildingComparison.Compare(ShippedBuildingInputs inputs) -> ShippedBuildingReport`, assertion-free, and test `ShippedBuildings_PreserveEveryBoundLeafAndInterior` inside PrivateOracleEntry.Run.
- Both new test classes carry `[UnsupportedOSPlatform("windows")]`, matching PrivateOracleEntry's released platform contract. No process-global environment mutation is needed for synthetic environment-dictionary guards.

**Input closure and private expectations, before any comparison run:**

The controller freezes current game main and records its full commit in private provenance. The extraction includes exactly the attested world manifest and all listed regions, every catalog path named by that manifest, all nominated prefab definition files, every mesh/collider/material/light dependency the comparison reads and a checked actual-instance membership manifest. That manifest names each original leaf, definition key, actual-instance override, floor footprint, room/storey, roof link, light and doorway correspondence. Catalog paths resolve relative to world.json, prefab/mesh paths relative to the declared extraction root. Reject unverified paths, escape/symlink traversal and missing dependencies before conversion. Do not rediscover an unattested prefab or use a developer checkout as fallback.

The gate's private regression expectations are 7 definitions, 9 buildings, 355 original leaves exactly once, 8 connected interiors, 10 roofs including the open shelter, 10 distinct building openings from 11 doorway/frame records and 27 dedicated indoor lights. The comparison separately names the cottage bookshelf override, crafting hall's outdoor notch and shared hall/smithy concealment group. These gate numbers are a planning acceptance checklist, not constants in public fixtures or public harness assertions. The private provenance comparison object supplies them and the source-specific membership/semantic records. The emitting forge is preserved separately from the dedicated-light count.

Connected-interior count measures authored concealment components, not merged occupied owners. The hall and smithy keep distinct instance-owned rooms and share an explicit group. Light emission records keep their original placement leaves as owners, so publishing light data never removes those leaves from the original inventory.

- [ ] **Step 1: Write failing synthetic input-boundary guards and the sanitized private test.**

```csharp
[Theory]
[InlineData("missing-prefab")] [InlineData("missing-catalog")]
[InlineData("missing-mesh")] [InlineData("stale-mesh-digest")]
[InlineData("unassigned-leaf")] [InlineData("duplicate-leaf")]
[InlineData("partial-room")] [InlineData("dangling-roof")]
public void PrivateBuildingClosure_RefusesMissingOrUnverifiedInput(string variant)
{
    var input = ShippedBuildingGuardFixtures.Create(variant);
    var failure = Assert.ThrowsAny<Exception>(() =>
        PrivateOracleEntry.Run(input.Environment, (i, report) =>
        {
            var result = ShippedBuildingComparison.Compare(ShippedBuildingInputs.Read(i));
            report.Check("building-closure", result.Failures.Count == 0, result.DetailJson);
        }));
    Assert.True(failure is PrivateOracleInputException or PrivateOracleFailure);
    Assert.Null(failure.InnerException);
    Assert.DoesNotContain(ShippedBuildingGuardFixtures.PublicSecretMarker, failure.Message);
}
[Fact] public void ShippedBuildings_PreserveEveryBoundLeafAndInterior() =>
    PrivateOracleEntry.Run((inputs, report) =>
    {
        var result = ShippedBuildingComparison.Compare(ShippedBuildingInputs.Read(inputs));
        report.Record("buildings", result.DetailJson);
        report.Check("building-parity", result.Failures.Count == 0, result.DetailJson);
    });
```

Create `ShippedBuildingGuardFixtures` in ShippedBuildingInputGuardTests.cs with public invented source/mesh/prefab bytes, `Create(string variant)` returning an object with `Environment: IReadOnlyDictionary<string,string?>` and a synthetic PublicSecretMarker. Its valid case also checks report file modes and complete input closure. No private reads occur at discovery, construction or static initialization.

- [ ] **Step 2: Run synthetic red only** `wa_test t13-red "$ORACLE" "FullyQualifiedName~ShippedBuildingInputGuardTests"`. Expect missing comparison/closure checks. The implementer never runs the shipped test or fetches private content.
- [ ] **Step 3: Implement the complete comparison inside the private boundary.**

Use the checked membership manifest, never a nominal restamp or guessed envelope grouping. Convert only the comparison's selected building data in test code, preserving each original numeric leaf once and recording new parents separately. Reuse R2's linked LegacyOracleConverter for floor data and the R5 native asset/instance APIs for free building representation. Refuse ambiguous/unassigned membership and incomplete reference closure. This is not a production importer.

Compare every leaf's effective transform, ordered tags, kind/asset, original mesh bytes and metadata, physical collider, any authored selection geometry and original numeric ID. Revised-T4 derived envelopes compare between native heads. They need not equal legacy visual-AABB picking. Source query deltas are recorded privately for R11 named acceptance, never called lossless. Compare furniture differences, unset notch paint versus world material, room/storey cell-centre membership, each roof transform/link and all lights. Exact boundary changes implied by OA33's one-owner policy are reported separately and still await R11 import acceptance. Unexplained centre/membership differences fail.

Imported derived vertices use the spec's fixed 0.00001 m tolerance and normals 0.000001 per component. Integers, tags and preserved identities compare exactly. Emit every exceeded value privately, never widen tolerances. Use the released source collider builder for source physical geometry, not another TileWorld collision implementation.

For every checked doorway, compare the source opening and native compound shell clearance, including jamb blocking. This proves physical apertures, not native walking. For every checked room/edge/storey probe compare selected occupied owner and connected concealment intent. Verify hidden colour false/caster true/boom false/sky true, unchanged statics and visible unlinked shelter. Reload and two independent heads must preserve the same effective leaf records.

All body failures use report.Check/Record, never Assert, console or ITestOutputHelper on private values. Detailed source IDs, geometry, paths, inventory, provenance and reports stay in the private extraction/report area. The public repository/CI/logs receive only sanitized pass/fail and the private record reference. Missing private input fails acceptance and never skips. Preserve the existing four environment variables and the 0700 directory/0600 exclusive report rules.

- [ ] **Step 4: Run synthetic green** `wa_test t13-green "$ORACLE" "FullyQualifiedName~ShippedBuildingInputGuardTests|FullyQualifiedName~PrivateOracleGuardTests"`. Expect all synthetic guards passing, missing input still refused and no secret marker in public failure text.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t13_paths=(
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedBuildingInputs.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedBuildingComparison.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedBuildingComparisonTests.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedBuildingInputGuardTests.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj
    KhaozEngine.MapDoc.OracleHarness.Tests/README.md
    KhaozEngine.Tests/ArchitectureTests.MapDoc.cs
)
wa_finish t13 "${wa_r5_t13_paths[@]}" &&
git add -- "${wa_r5_t13_paths[@]}" &&
git commit --only -m "test(mapdoc): compare shipped prefab buildings through the private oracle" -- "${wa_r5_t13_paths[@]}"
```

## Self-review and execution handoff

Revision 5 applies only R4-I1, the binding materialized-frame override reconciliation correction. Earlier review corrections remain binding. It maps all 19 gate decisions and the three OA9 proof names to 13 serial tasks, preserves released signatures at 06b734c57 and pins revised cross-task interfaces. Corrections include precise hierarchy composition/wide queries, occupied fragments, complete reference scopes/storeys, closure-aware preparation, full snapshot publication, precise standalone lifecycle, materialized support ownership and bound leaf emissions. No gate item was omitted. Compilation, test execution and runtime claims remain execution evidence, not evidence from this document-writing turn.

- [ ] Check OA33 Decisions 1 to 19 against Tasks 1 to 13. Format/closure is Task 1, bindings/identity Task 2, transforms/completeness Task 3, rooms Task 4, local support/doors/bounds Task 5, paint Task 6, full water Task 7, roof/light/marker intent Task 8, pure edits Task 9, commands/snapshots/effects Task 10, asset/complete generic history/tools Task 11, synthetic acceptance Task 12, private acceptance Task 13 and release below.
- [ ] Check every consumed signature against 06b734c57 and every new type/property against its producing task. Carry interface adjustments into later briefs before dispatch.
- [ ] Check every test fixture is valid for coverage and containment before interpreting red, each failure is falsifiable, each focused filter matches tests and all five Review Focus concerns have named assertions.
- [ ] Check all source paths, old line ranges, constructor overload preservation, 800-line caps, linked fixture boundaries and explicit commit path lists. No baselined-file growth is approved.
- [ ] Check bound Prepare receives its matching closure, null local-only legacy controls still work, full snapshot callbacks restore actual globals, and precise placement/attachment lifecycle tests cover each existing command.
- [ ] Check native identity includes all root fields on loaded/stored paths exactly once and the deliberate format-7 migration is documented separately from unchanged analytic execution.
- [ ] Check all three OA9 proof names exist and their claims stop at R5's geometry/ownership/nav identity boundary.
- [ ] Check private provenance covers every read input before freeze. Gate inventory belongs in private expectations and private detailed reports, never public fixtures or shipped-source goldens.
- [ ] Record plan review verdicts and owner approval in the controller's private ledger before code. No owner-gate requirement is omitted. Rendering, live routes, GUI binding and final import acceptance remain their explicitly approved later gates.

Execution is serial subagent-driven under the program's selected method after owner plan approval. Each task gets a bounded brief and independent review. A load-bearing unresolved finding blocks dependent work. The controller verifies commits, red/green logs, actual diffs and source references rather than accepting a worker report as proof. R5-P1 onward are accepted or corrected explicitly in that ledger before their dependent tasks.

## Review, release preparation, candidate gates and integration

1. Whole-branch review against the reviewed plan, OA33 and carried R3/R4 rulings. One scoped fix/re-review per finding. Triage only task-related findings and keep external prerequisites separate.
2. Fetch and merge current main into the task branch before final version selection. Resolve there, recheck every interface affected by #438 or concurrent MapDoc changes, and reconcile format/version declarations. If another change took format 7, stop for controller plan amendment rather than silently changing the approved transition.
3. Re-read main/origin/main, KhaozEngineVersion, tags and newest CHANGELOG heading. Ride staged 20.33.0 if still untagged. If tagged, use the next free minor, riding an already-staged untagged minor when present. A staged patch becomes that minor with its items retained, following R4-P39. Append staged batch items and keep one related bump. Commit version, CHANGELOG and every scripts/check-doc-versions.sh declaration together. State format-7 cache/capture rebuilds, effective resource policy, AuthoredOpen doors and later route/render gates in consumer notes.
4. Perform the full Markdown/public API sweep from CONTRIBUTOR-RULES. Update root README/package examples, USING-KHAOZENGINE, the C5/verb/exit rows of the migration design, R2's downstream allocation, package READMEs and the doc index. Keep finished plans historical and living API contracts in their documented homes.
5. Candidate gates, once each in order, after reconciliation and release preparation:

```bash
build-slot --heavy --label wa-r5-gate-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label wa-r5-gate-suite -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
wa_run gate-format dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
wa_run gate-dashes sh scripts/check-dashes.sh --tree
wa_run gate-prose sh scripts/check-prose.sh --tree
wa_run gate-size sh scripts/check-file-size.sh --tree
wa_run gate-agent sh scripts/check-agent-instructions.sh --tree
wa_run gate-docs bash scripts/check-doc-versions.sh
git diff --check
```

Expect exit 0, zero warnings and zero failed tests, with nonzero suite counts. Keep logs and exact candidate SHA. Push the candidate branch through the controller and require hosted CI. Do not run stress, GPU or consumer clients. A later main/code change invalidates this candidate and justifies the appropriate renewed focused/full verification once.

6. Controller-only private comparison. Freeze current game main into a new attested extraction with the complete Task 13 closure. Set the four variables to that extraction, its private provenance/digest and an absent report path under an existing 0700 directory outside git. Run once:

```bash
wa_run gate-oracle-build dotnet build "$ORACLE" -c Release -m:1
wa_run gate-private env KHAOZ_R2_SHIPPED_SOURCE="$wa_r5_verified_source" KHAOZ_R2_ORACLE_PROVENANCE="$wa_r5_private_provenance" KHAOZ_R2_ORACLE_PROVENANCE_SHA256="$wa_r5_provenance_sha256" KHAOZ_R2_ORACLE_REPORT="$wa_r5_private_report" dotnet test "$ORACLE" -c Release --no-build --filter "FullyQualifiedName~ShippedBuildings_PreserveEveryBoundLeafAndInterior"
```

The controller supplies those task-specific values in its private acceptance brief. Missing or mismatched inputs block acceptance. Record provenance/report SHA-256s, source commit, complete input inventory and every comparison result in Grimhollow's private program folder as R5-BUILDING-ORACLE.md. The public ledger records only pass/fail and that private record reference. A semantic source change needs named acceptance, not a forced pass. R11/G2 refreezes source again for actual import.

7. Integration belongs to the engine controller. Confirm current main is contained by the verified branch, fast-forward main, push main and pack the shared feed from clean main only after origin/main contains the integrated commit:

```bash
build-slot --heavy --label wa-r5-release-pack -- sh scripts/pack-local-feed.sh
```

Tagging goes through a KhaozEngine-rooted thread under OA25/Decision 19, with fresh version/tag checks and scripts/tag-release.sh, never a hand tag or game-rooted release. Require tag CI publication. Grimhollow PROGRAM/DECISIONS and project memory record the accepted rulings, task/integration commits, private oracle reference, release and refreshed program estimate. Game pins/adoption are separate. Delegated workers do not integrate, push, pack, tag or clean worktrees.

## Outcome and proof ledger

Filled during execution.

| Item | Evidence |
| --- | --- |
| Independent plan reviews and owner approval | |
| Accepted or corrected R5-P rulings | |
| Task commits and red/green logs | |
| Three OA9 cave proofs | |
| Whole-branch review and reconciliation | |
| Candidate guards, build, suite and hosted CI | |
| Private building oracle and closure provenance | |
| Integration and engine release | |
| R6 to R11 and G1 to G5 estimate refresh | |
