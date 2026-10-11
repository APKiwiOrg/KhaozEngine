# World Authoring R5: Free Buildings, Prefabs and Interiors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Author rigid reusable buildings at free XYZ, yaw and positive uniform scale, preserving every child identity and publishing identical support, occupied-space, local-water and roof/light/marker facts to both heads.

**Architecture:** Format 7 adds digest-pinned prefab payload 1 and persisted instance bindings. Resolver 2 expands them into one immutable effective resource view. Polygon-prism rooms extend R2's occupied-space authority and nest inside authored parents. R3 installs the same transformed canonical faces and shapes, R4 compiles water against the resulting occupied partitions, and roof intent stays separate from physical statics. Existing native transactions own allocation and atomic publication.

**Tech Stack:** C# on the repository's .NET target, System.Numerics, System.Text.Json, closed JSON schemas, xUnit, MapDoc's exact geometry and R4 rational clipping, the existing Physics seam and Bepu test backend. No new package or third-party dependency.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), C1, C2, C5, DG9 and the prefab/interior rows of the verb map. Read also [R2 caves and scale](../../design/WORLD-AUTHORING-R2-CAVES-SCALE-2026-10-06.md), [R3 executable plan](2026-10-10-world-authoring-r3-shared-shapes-headless-world.md), and [R4 executable plan](2026-10-11-world-authoring-r4-bounded-water.md).

Binding private controller inputs are Grimhollow `feature/world-authoring` `docs/superpowers/programs/world-authoring/R5-OWNER-GATE.md`, `DECISIONS.md` OA33, `R3-LEDGER.md` and `R4-LEDGER.md`. The controller supplies those texts in worker briefs. Research inputs are the engine controller's `.superpowers/sdd/2026-10-12-world-authoring-r5-refresh/research-engine.md` and `research-program.md`. Their proposals have no authority where OA33 chose otherwise.

## Approval and execution gate

OA33 approved the 19 gate decisions on 2026-10-12, including Decision 4 option C. This document replaces the six-task 2026-10-05 draft. It is written against `06b734c5722bc2cc95cea459255d6ec93e9236c7`, staged 20.33.0, after released R1 20.27.1, R2 20.30.0, R3 20.31.0 and R4 20.32.0.

OA33 authorizes this rewrite only. Independent plan reviews and the owner's approval of the reviewed plan precede product code. Proposed R5-P rulings below are submitted to the controller, not described as accepted. Workers stop at verified task commits. Integration and an engine-rooted release belong to the controller under OA25 and Decision 19. Game adoption and the R11 freeze remain separate.

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

1. An asset removes and later restores a non-placement child. Its reserved identity survives redo and reload, and unpack cannot leave a live duplicate (Task 2 `EveryChildKind_RetainsReservationsAndTransfersOnce`, Task 9 `Unpack_PreservesAllTypedReferencesAndIds`).
2. A transformed room crosses a storage seam while its anchor, parent or prefab bytes are unavailable. Membership, water and roof policy must report incomplete data, never exterior, dry or hidden (Tasks 3, 4 and 8 `ScopedChildren_RequireTheirFullWitness`, `MissingParentOrPrism_IsMissingGeometry`, `UnresolvedObserver_KeepsRoofsVisibleWithStatus`).
3. Two free-yaw rooms share an edge or reflex corner, or two storeys share a height. Exactly one owns each boundary while a genuine overlap refuses (Task 4 `SharedEdgesReflexCornersAndStoreys_HaveOneOwner`).
4. Local paint on a slope and a dry nested room below exterior water must not create another floor or disagree with the effective water compiler (Tasks 6 and 7 `PartialPaint_LeavesFacesAndSupportByteIdentical`, `ParentWater_ExcludesAnUncoveredNestedRoom`).
5. A floor edit reseats a long supported leaf into neighboring tiles, or an asset publication fails after immutable bytes are written. Effects cover old/new geometry and the accepted map/history/closure remain atomic (Task 9 `FloorReseat_InvalidatesEveryChangedNeighborTile`, `AssetPublicationFailure_LeavesSessionAndOtherMapsUntouched`).

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
| Private lossless importer and actual source freeze | R11/G2. Task 11 is a comparison procedure, not accepted game adoption |
| Water indexing, huge exterior coverage and medium-aware seam capture | #1358 to #1362. Keep explicit refusals and no per-frame performance claim |

## Proposed controller rulings

Each judgment not fixed by OA33 is named here. Selecting the gate's approved option is not a new ruling.

| ID | Proposed decision and reason | Cost if wrong |
| --- | --- | --- |
| R5-P1 | Keep resolver identity (1, 2) and AuthoredBindingsV2. Version prefab expansion as 1 and transformed query policy as 2. Include them only for effective resources, preserving resolver-1 routing | A later resolver 3 requires an explicit migration and API adapter |
| R5-P2 | Store instances, bindings, overrides, transfers and world building intent in native root globals, once. Local geometry stays in immutable prefab resources. No tile instance copies | Large instance registries need an indexed root extension in R8 |
| R5-P3 | Use MapTransform.Compose once, including its released float composition. Evaluate in double, quantize placed coordinates to 1/1,048,576 m with ties to even, then round world Y once to binary32 for the released height seam. Freeze XZ/Y as exact rationals and subtract XZ anchors before adapter rounding. This avoids binary64 denominators breaking R2 arithmetic | At most half a quantum of added coordinate rounding, plus documented far-Y float quantization. Policy/cache identities change once |
| R5-P4 | Local child identity is (kind, ordinal key). Persist world IDs, using percent-escaped UTF-8 path segments for new bindings, with optional positive int64 only on placement leaves. Import may supply original world IDs | Changing the grammar later needs explicit remap. It adds retained nonnumeric reservations |
| R5-P5 | Rooms select the deepest containing descendant and subtract their owned interval from parent occupancy. The effective room interval is its prism intersected with canonical parent bounds. Parent geometry stays unchanged | If whole-prism containment is later preferred, uneven sites need an explicit conversion |
| R5-P6 | A prism domain is a new topology record referencing an existing MapSpaceDoc, not a second classifier. It carries parent, storey, lower/upper heights and instance ownership, with no mandatory physical wall at every semantic edge | A later sealed-room rule adds coverage checks, without changing membership authority |
| R5-P7 | Positive-volume peer overlap refuses publication and returns Ambiguous in readable diagnostic views. Roof groups are explicit room sets, not transitive portal flood fill | Authors must maintain concealment sets when redesigning connections |
| R5-P8 | Uneven-site seating is a diagnostic, not automatic ground deformation. Coincident independent physical floors refuse. A local floor can sit above an exterior floor | A future foundation tool is separate and needs owner design |
| R5-P9 | Overlapping material regions require an explicit integer priority, then ordinal paint ID. Equal-priority positive-area conflicting paint refuses. Render subdivisions retain parent face keys | A future blend system needs a new paint policy |
| R5-P10 | Full local-water compilation uses effective domain/bound adapters into R4, retaining local records and transforms instead of serializing rotated vertices back onto a lattice | Additional adapter code, but no lossy resampling or new water semantics |
| R5-P11 | Roof groups expand to explicit linked room IDs. Auto hides only links above the observer. Unresolved membership returns its status and conservatively visible roof intent | Incomplete previews show roofs until acquisition completes |
| R5-P12 | Publish digest-addressed immutable prefab and manifest bytes first, then accept one map/closure switch. Failed switches may leave unreachable immutable bytes, never changed roots. Undo restores the old closure | Unreferenced immutable files need later explicit cleanup, not automatic deletion |
| R5-P13 | Expansion defaults to 65,536 instances, 1,048,576 effective children and 262,144 reference edges, charged before allocation. Reuse query/water budgets downstream | Very large valid documents may need explicit larger limits or R8 indexing |
| R5-P14 | Use 11 serial tasks, synthetic guard red/green for the private harness, and one private current-source comparison at the candidate gate. Private provenance carries expected inventory, not public test constants | A newly frozen source needs a new private expectation manifest and owner acceptance |
| R5-P15 | Unpack persists a standalone resource-transform registry and transfers identities, keeping authored local lattices instead of resampling world geometry | Another root field and future editor coordinate adapters |
| R5-P16 | Instances bind an explicit ParentSpace. A typed local $parent slot resolves to that authored record, and unused prefab local editing requires a supplied parent context | Reusable assets require explicit parent selection at authoring time |
| R5-P17 | R5 registers its prefab/interior verbs now and shares EditorHistory with native tool undo/redo. R10 retains the remaining parity map | Session history hooks require focused regressions for existing native edits |
| R5-P18 | Light payload 1 uses position, linear colour, intensity, radius and caster intent. Marker payload 1 uses XYZ/yaw/name/role/enabled/tags, with no gameplay profile or spawn projection | Future light profiles or marker metadata need a versioned extension |
| R5-P19 | A remove override retains the previous typed override. Restore reinstates it and its reservation, so removal does not discard customization | Retained override data and its restore dependencies enlarge the root/closure |
| R5-P20 | A missing-Y prefab leaf transforms its typed support request, samples the effective world floor once through R2 and installs that world Y directly. No rescaling of the sampled height | Another internal resolver entry point and a focused support-binding regression |

## File structure

All paths are repository-relative. New files below are created by the owning task, not claimed as released APIs.

| Area | Files and responsibility |
| --- | --- |
| Prefab payload | MapDoc `MapDocument.Prefabs.cs`, `Prefabs/MapPrefabRecords.cs`, `MapPrefabChildren.cs`, `MapPrefabOverrides.cs`, `MapPrefabJson.cs`, `MapPrefabFile.cs`, `MapPrefabMigration.cs`, `MapPrefabValidation.cs` |
| Stable bindings and identity | MapDoc `Prefabs/MapPrefabBindings.cs`, `Identity/MapPrefabIdentity.cs`, `Prefabs/MapPrefabReferences.cs`. Extend root persistence, bound validation and both identity paths |
| One effective view | MapDoc `Prefabs/MapPrefabResolver.cs`, `MapEffectiveResources.cs`, `MapResourceTransform.cs`, `Storage/MapEffectiveSurfaceSource.cs`, `MapEffectiveAcquisition.cs`, `Surfaces/MapEffectiveGeometry.cs` |
| Rooms in R2 | MapDoc `Spaces/MapPrismDomain.cs`, `MapPrismGeometry.cs`, `MapOccupiedPartitions.cs`, `MapPrismCoverage.cs`. Extend membership, intervals, support and topology codecs |
| Local support and statics | MapDoc `Surfaces/MapSupportResource.cs`, MapDoc.Physics `MapPrefabSupport.cs`. Extend terrain/placement builder, residency and nav paths |
| Paint | MapDoc `Surfaces/MapPaintRegions.cs`, `MapPartialPaintCompiler.cs`. Preserve physical triangles and publish draw-only regions |
| Local water | MapDoc `Water/MapEffectiveWater.cs`, `MapWaterOccupiedPieces.cs`. Adapt R4 compile, query, containment, bound reads and digests |
| View intent | MapDoc `Interiors/MapRoofIntent.cs`, `MapRoofPolicy.cs`, `Prefabs/MapLightFile.cs`, `MapEffectiveAnnotations.cs` |
| Authoring | MapDoc `Editing/MapPrefabEdits.cs`, `MapPrefabEditPreparation.cs`, `MapInteriorEdits.cs`, `MapPrefabExtraction.cs`. Focused MapEditor `NativePrefabCommands.cs` and `NativeInteriorCommands.cs` |
| ke-mapedit | MapEdit.Tool `PrefabService.cs`, `PrefabAssetPublisher.cs`, `MutationService.Prefabs.cs`, `Tools/PrefabTools.cs`, `Tools/InteriorTools.cs` |
| Public proofs | MapDoc.Tests `Prefabs/`, `Spaces/`, MapDoc.Physics.Tests `MapPrefab*.cs`, MapEditor.Tests `MapDoc/NativePrefab*.cs`, `MapEditTool/Prefab*.cs` |
| Private comparison | OracleHarness.Tests `ShippedBuildingInputs.cs`, `ShippedBuildingComparison.cs`, `ShippedBuildingComparisonTests.cs`, synthetic guards and README |

Prefixes mean the existing `KhaozEngine.<prefix>/` projects. Each task expands its actual paths. Do not grow baselined EditorCommands.cs, EditorTool.cs or scene test files. Focused new files stay below 800 lines. Overload hooks go into existing small types or cohesive partial files.

## Dependencies and estimate

| Task | Deliverable | Depends on | Engineer-days |
| --- | --- | --- | --- |
| 1 | Format 7, prefab payload and closure | released base | 4 to 6 |
| 2 | Persisted bindings, references and identity | 1 | 3 to 5 |
| 3 | One transformed effective resource view | 2 | 5 to 8 |
| 4 | Prism rooms in R2 membership and coverage | 3 | 7 to 10 |
| 5 | Local support, shapes, bounds and tile consequences | 4 | 5 to 8 |
| 6 | Partial-face paint | 3, 5 | 3 to 5 |
| 7 | Full prefab-local water | 4, 5 | 4 to 6 |
| 8 | Roof intent, lights and local markers | 4, 7 | 3 to 5 |
| 9 | Atomic commands, asset publication and tools | 2 to 8 | 5 to 7 |
| 10 | Public synthetic village and two-head acceptance | 9 | 2 to 3 |
| 11 | Private closure comparison and its guard tests | 10 | 1 to 2 |
| | Task subtotal | | 42 to 65 |
| | Reviews, release preparation and integration | all | 3 to 5 |
| | Total | | 45 to 70 |

These are scope judgments, not measured person-hours. R3 took about 14 h and R4 about 9 h from approval to tag. Forecast R5 at 12 to 24 h elapsed agent time after plan approval, plus roughly half a day for plan writing/review. Owner response, subscription resets, private input preparation, concurrent-main reconciliation and release CI can extend calendar time. Neither engineer-days nor Git artifact spans are wall-clock telemetry. At R5 exit refresh the remaining R6 to R11 forecast, currently 3 to 6 calendar weeks, and G1 to G5, currently another 1 to 2 weeks plus owner playtest.

## Dispatch and verification conventions

The controller copies Global Constraints, proposed rulings as accepted or corrected, this block, the shared fixture contract and consumed Interfaces blocks into every bounded worker brief. Supply the approved gate and relevant ledger rulings. Execute serially with a fresh reviewer per task, following the session's Gauge placement rules. No helper is authorized by this plan-writing brief.

Execution workers start at the assigned accepted commit in this worktree and branch. The controller pins that commit in the brief. The plan writer's starting guard is HEAD exactly `06b734c57`.

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
    if [ "$n" = t11 ]
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

Every task ends with its explicit path array and wa_finish below, which selects C# paths for format. Add any fix-round paths explicitly before staging. Repair formatting through wa_run and dotnet format on that same explicit C# list, then verify. Task 11 also formats its outside-solution project. Guard exit 0 and git diff --check precede each explicit-path commit. Plan-writing verification is limited to the four doc guards in its dispatch contract and never runs this execution block.

## Shared fixture contract

Create `KhaozEngine.MapDoc.Tests/Prefabs/PrefabFixtures.cs` as `internal static partial class PrefabFixtures`. Extend by concern in files ending Fixtures.cs, which MapEditor.Tests already links. Editor-only helpers stay in MapEditor.Tests. Physics helpers stay in MapDoc.Physics.Tests without a project reference to MapDoc.Tests. Task 10 links public fixture source explicitly.

- Task 1 creates `internal sealed record PrefabFixture(MapDocument Document, MapAssetClosure Assets)`, `PrefabFixture Create(string variant = "basic")`, `MapResolveOptions Options()` returning builder public-prefab-fixture, version 1, options hash fixture-options/1, resolver 2, and `string TempDirectory()`.
- Task 1 creates `string FormatSixJson(bool analytic)`, valid without R5 fields, `byte[] AssetBytes(string variant)` for payload failures and `MapAssetClosure LoadAssets(byte[] prefabBytes, bool refreshDigest)`. Changed bytes without refresh fail closure verification.
- Task 2 creates `MapStoredSurfaceSource Stored(MapDocument document)`, repeating the save/open pattern locally, and `IReadOnlyList<MapPrefabBinding> LiveBindings(MapDocument document)`.
- Task 3 creates `MapEffectiveResources Effective(PrefabFixture fixture)`, `MapFramePoint Point(MapEffectiveResources resources, float x, float y, float z)` in the acquired frame, and `PrefabFixture Transformed(float yaw, float scale, Vector3 position)`. Transformed uses the all-resources variant, including the pool and a room/floor/water shared corner.
- Task 4 adds `PrefabFixture Cave(string variant)`: stacked, transformed, dry-flooded, missing-parent, peer-overlap, adjacent, uneven-parent and reflex-storeys. Query actual membership and intervals, never a fixture classifier.
- Task 9 creates editor-only `PrefabEditorFixtures.Session(PrefabFixture fixture) -> MapEditSession`, `Document(MapEditSession session) -> MapDocument`, `History(MapDocument document, MapAssetClosure assets) -> EditorHistory` and `FaultPublisher(MapEditSession session, string checkpoint) -> PrefabAssetPublisher`, exercising actual session/history entry points and injected bounds.

The basic synthetic prefab is hut, instance a, on an explicitly authored 16 by 16 m exterior at y -2 with negative-Z rows. It has an elevated 4 by 4 m support floor at y 0, ceiling at y 3, a doorway compound with 1.2 m clear width, a selectable item 0.2 m high, room room, storey lower, light lamp, marker entry, roof roof and a roof link. Variant pool is a separate room with floor -1, ceiling 3 and local water level 0.5. Covers, portal records and material closure are explicit. Non-placement bindings have no numeric ID. Placement IDs are supplied by fixtures, reserved above high-water mark 100.

All-resources includes exactly one pool body and marker entry at local (0,1,0), role entrance, enabled true and ordered tags first/second. Floor/room/water share XZ corner (4,-4). Their different vertical coordinates remain explicit. For transformed variants the fixture authors a parent exterior at the translated site's floor level and footprint, so deep/far placements are valid rather than outside an unchanged y -2 parent.

The far transform is (30000.25, -450, -29999.75), yaw 0.371 and scale 1.2. Near transforms use (10.25, 5, -3.125), yaw 0.371, scales 0.8 and 1.2. All variants are invented public geometry. None copies private buildings, leaf IDs or coordinates.

### Task 1: Format 7, payload 1 and verified prefab closure

**Files:**
- Create: `KhaozEngine.MapDoc/MapDocument.Prefabs.cs`, and `Prefabs/MapPrefabRecords.cs`, `MapPrefabChildren.cs`, `MapPrefabOverrides.cs`, `MapPrefabJson.cs`, `MapPrefabFile.cs`, `MapPrefabMigration.cs`, `MapPrefabValidation.cs` under `KhaozEngine.MapDoc/`.
- Modify: `KhaozEngine.MapDoc/MapDocumentFile.cs:34-46` and `:85-95`, `MapCanonical.cs:51-108`, `MapTiledFile.cs:54-89` and `:205-226`, `MapDocumentMembers.cs:19-65`, `MapDocumentSchema.cs:1-35`, `mapdoc.schema.json`, `Assets/MapAssetManifestReader.cs:78-85`, `Assets/MapAssetClosure.cs:75-98`, `MapDocumentValidator.cs`, `README.md`.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabFormatTests.cs`, `MapPrefabClosureTests.cs`, `PrefabFixtures.cs` in that directory. Update `NativeAssetClosureTests.cs:149` to test unsupported prefab payload rather than the reserved role. Current-format assertions in `FormatAdvanceTests.cs` and `NativeDocumentTests.cs` use CurrentFormatVersion, preserving historical input bytes.

**Interfaces:**
- Consumes released `MapDocumentFile.LoadText(string json, MapDocumentLoadOptions? options = null, string? sourcePath = null)`, `SaveText(MapDocument doc, MapDocRegistry? registry = null)`, `SaveTiled(MapDocument doc, string directory, MapDocRegistry? registry = null, MapDocumentSaveOptions? save = null)`, `LoadTiled(string directory, MapDocumentLoadOptions? options = null)`, and `MapDocumentLoadOptions.RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> step)`.
- Consumes `MapAssetClosure.Load(IReadOnlyList<MapAssetRef> roots, IMapAssetSource source)` at Assets/MapAssetClosure.cs:46 and `MapAssetRef(string Id, string Path, string Sha256, int PayloadVersion)` at MapNativeDocument.cs:39. MapResourceKind.Prefab already exists.
- Produces in MapDoc.Prefabs `MapPrefabSet(int PayloadVersion, int ExpansionVersion, IReadOnlyList<MapPrefabInstance> Instances, IReadOnlyList<MapPrefabBindingTransfer> Transfers)`, with deep `Clone() -> MapPrefabSet` and canonical `Digest() -> string`.
- Produces `MapPrefabInstance(string Id, string ResourceId, string ResourceSha256, MapTransform Transform, MapRecordRef ParentSpace, MapPrefabBindings Bindings, IReadOnlyList<MapPrefabOverride> Overrides)`, with init-only bool Removed default false, and `MapPrefabAsset(int PayloadVersion, string Id, IReadOnlyList<MapPrefabChild> Children)`.
- Produces `MapPrefabChildKey(MapPrefabChildKind Kind, string Key)`, enum kinds `Placement, Surface, Record, Storey, WaterMedium, WaterBody, Light, Marker, RoofLink, RoofGroup`, and abstract `MapPrefabChild(MapPrefabChildKey Key)`.
- Closed derived child constructors take Key followed by these arguments: `MapPrefabPlacementChild(MapPlacement Value)`, `MapPrefabSurfaceChild(MapSurfaceRef Surface, IReadOnlyList<MapSurfacePatch> Patches)`, `MapPrefabRecordChild(MapTopologyRecord Value, MapPatchKey Anchor)`, `MapPrefabStoreyChild(string Name)`, `MapPrefabMediumChild(MapWaterMediumDoc Value)`, `MapPrefabWaterChild(MapWaterBodyDoc Value)`, `MapPrefabLightChild(MapLocalLightDoc Value)`, `MapPrefabMarkerChild(MapLocalMarkerDoc Value)`, `MapPrefabRoofChild(MapRoofLinkDoc Value)`, `MapPrefabRoofGroupChild(MapRoofGroupDoc Value)`. The constructor includes Key as its first parameter in every case. The base child adds init-only `MapTransform LocalTransform`, default identity. Placement children must leave it identity and use MapPlacement's own transform, avoiding double application.
- Produces `MapPrefabBinding(MapPrefabChildKey Child, string WorldId, long? NumericId, bool Removed)`, `MapPrefabBindings(IReadOnlyList<MapPrefabBinding> Entries)`, `MapPrefabBindingTransfer(string InstanceId, MapPrefabBinding Binding)`.
- Produces abstract `MapPrefabOverride(MapPrefabChildKey Child)` and variants `MapPrefabRemoveOverride(Child, MapPrefabOverride? Previous = null)`, `MapPrefabReplaceOverride(Child, MapPrefabChild Value)`, `MapPrefabPlacementOverride(Child, MapTransform? Transform, string? AssetId, IReadOnlyList<string>? Tags)`, `MapPrefabTransformOverride(Child, MapTransform Transform)`, `MapPrefabAddOverride(Child, MapPrefabChild Value)`. Child means MapPrefabChildKey in every signature. A transform override updates LocalTransform, or MapPlacement's transform for a placement child. Previous cannot itself be a remove override. Restore reinstates Previous if present and retains the binding. There is one effective override per child key.
- Produces typed `MapPrefabReference(MapPrefabReferenceScope Scope, MapPrefabChildKind Kind, string Id)`, scopes Local and World. R2 MapRecordRef, MapPatchKey and support bindings keep their types and join the Task 2 visitor.
- Produces `MapLocalLightDoc(int PayloadVersion, string Id, MapPrefabReference? Owner, Vector3 Position, Vector3 Colour, float Intensity, float Radius, bool CastShadows)`, `MapLocalMarkerDoc(int PayloadVersion, string Id, string Name, Vector3 Position, float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags)`, `MapRoofLinkDoc(string Id, MapPrefabReference Roof, IReadOnlyList<MapPrefabReference> Rooms, float AboveY)`, `MapRoofGroupDoc(string Id, IReadOnlyList<MapPrefabReference> Rooms)`.
- Produces `MapBuildingIntent(int PayloadVersion, IReadOnlyList<MapRoofLinkDoc> RoofLinks, IReadOnlyList<MapRoofGroupDoc> RoofGroups, IReadOnlyList<MapLocalLightDoc> Lights, IReadOnlyList<MapLocalMarkerDoc> Markers)`, deep `Clone() -> MapBuildingIntent` and canonical `Digest() -> string`.
- Produces `MapStandaloneResourceTransform(MapPrefabChildKind Kind, string OwnerId, MapTransform Transform, string SourceDigest)`. MapDocument gains nullable `MapPrefabSet? Prefabs`, `MapBuildingIntent? BuildingIntent` and `IReadOnlyList<MapStandaloneResourceTransform>? NativeResourceTransforms`, omitted when absent. The transform registry supports unpacked surfaces, topology, water and annotations without a prefab instance.
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

Load verifies bytes once, validates the graph, decodes verified Prefab snapshots and validates all leaf references through the closure. Payload resource edges must appear in declared dependencies. Missing/wrong-kind references refuse before build. A prefab has no mesh of its own. Material/collider/LOD/light dependencies stay on leaves.

Present Prefabs, BuildingIntent or NativeResourceTransforms requires resolver identity (1,2) and AuthoredBindingsV2. Add `PrefabSections_RefuseResolverOneBeforeAnyCallback`. Absent sections keep analytic and resolver-1 behavior. Binding and light IDs use the released decimal-string converters wherever a numeric ID is present.

MapCanonical.WriteGlobals writes nonnull Prefabs, BuildingIntent and NativeResourceTransforms once through canonical codecs, so both OfWorld and native root identity cover them. WriteNativeGlobals must not write them again. Tiled root reading and GlobalsOnly preserve all three. Instances, bindings, transforms and overrides sort ordinal by typed owner/key, preserving tag order. Never persist expanded duplicates in placements or tile copies. Structural validation accepts complete supplied bindings and never allocates. Task 2 strengthens identity/reservations. Update format documentation, not the engine version.

- [ ] **Step 4: Run green** with Step 2's filter as `t1-green`, then `wa_test t1-regress "$MAPDOC" "FullyQualifiedName~FormatAdvance|FullyQualifiedName~FormatFour|FullyQualifiedName~NativeDocument|FullyQualifiedName~NativeAssetClosure|FullyQualifiedName~Tiled"`. Expect all cases passing and the same recorded analytic transforms/callbacks.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t1_paths=(
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
- Create: `KhaozEngine.MapDoc/Prefabs/MapPrefabBindings.cs`, `MapPrefabReferences.cs`, `KhaozEngine.MapDoc/Identity/MapPrefabIdentity.cs`.
- Modify: `KhaozEngine.MapDoc/MapNativeValidation.cs:117-144`, `MapBoundDocumentValidation.cs:14-74`, `Identity/MapAuthoredIdentityV2.cs:16-48`, `Storage/MapStoredSurfaceSource.Identity.cs:9-14`, `Storage/MapStoredSurfaceSource.Content.cs:17-70`, Task 1's MapPrefabValidation.cs and MapPrefabJson.cs.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabBindingTests.cs`, `MapPrefabIdentityTests.cs`, `PrefabIdentityFixtures.cs`. No editor reference.

**Interfaces:**
- Consumes `MapNumericIds.Reserve(MapDocument document, IEnumerable<long> ids)` and `Allocate(MapDocument document) -> long` at MapNumericIds.cs:12 and :31. They mutate only a detached preparation candidate.
- Consumes `MapAuthoredIdentityV2.Compute(MapDocument document, MapAssetClosure assets, MapResolveOptions options) -> string` and `Compute(MapStoredSurfaceSource source, MapAssetClosure assets, MapResolveOptions options) -> string` at :16 and :31. RootDigest includes FormatVersion.
- Produces `MapPrefabBindingPlan(MapPrefabBindings Bindings, long HighWaterMark)`, `MapPrefabBindingPlanner.Plan(MapDocument document, MapPrefabAsset asset, MapAssetClosure assets, string instanceId, MapPrefabBindings existing, bool allocateNumericPlacements = true) -> MapPrefabBindingPlan`, pure.
- Produces `MapPrefabReferences.Remap(MapPrefabAsset asset, IReadOnlyDictionary<MapPrefabChildKey, string> bindings, MapRecordRef parentSpace) -> MapPrefabAsset`, `RemapWorld(MapDocument document, MapPrefabChildKind kind, string oldId, string newId) -> MapDocument`, pure, and `References(MapPrefabAsset asset) -> IReadOnlyList<MapPrefabReference>`.
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

This task adds `WithChildRemoved(MapPrefabAsset asset, MapPrefabChildKind kind) -> MapPrefabAsset` and `FormatSixRootDigest(MapDocument document) -> string` in PrefabIdentityFixtures.cs. The latter computes the released root projection with format 6 from a detached copy, not an updated golden copied from product output. Add tests `MissingOrDuplicateBindings_RefuseOnBothPaths`, `ReorderingAndRepacking_PreserveIdentity`, `NumericExhaustion_PublishesNothing` and `TypedRemap_DoesNotTouchTagsAssetIdsOrNames`. Transfers are validated here and exercised through unpack in Task 9.

- [ ] **Step 2: Run red** `wa_test t2-red "$MAPDOC" "FullyQualifiedName~MapPrefabBindingTests|FullyQualifiedName~MapPrefabIdentityTests"`. Expect missing planner/visitor, incomplete reservation checks or identity omissions.
- [ ] **Step 3: Implement the planner, visitor and shared validation.**

Plan on a detached reservation candidate in sorted (kind,key) order. Existing keys reuse bindings. Missing asset keys become Removed and remain reserved. Restore reactivates that reservation. New placement leaves allocate above imported numeric IDs and retained high-water mark. Other child types reserve string IDs only. New world IDs are `prefab/<escaped-instance>/<kind>/<escaped-key>`, with percent escaping of every UTF-8 byte outside ASCII letters/digits/hyphen/underscore and uppercase hex. Imported IDs are accepted exactly. Refuse collisions before publishing any allocation.

Every local surface ID, patch key, vertex owner, seam, chain, strip, portal, opening, link, space parent/alias, bound, indoor span, support binding, water cover/medium, roof/group, light owner and marker reference passes through one typed visitor. World references remain world references and are closure-validated. Plain tags, display names, resource IDs, asset IDs and Kind are never string-replaced.

Binding validation spans standalone records, live children, removed reservations and transfers. An unpack transfer points to exactly one standalone record of the matching kind and identity, and relinquishes instance ownership. Count one logical leaf once. Numeric transfers match the standalone numeric ID rather than adding a second reservation. Missing bindings refuse on load/resolve without raising the mark.

Root globals already hash all prefab state, once, on both identity paths. Do not add duplicate instance entries to MapAuthoredContentDigest. Stored validation checks root bindings against every verified content slice using the same shared identity registry. No geometry is loaded merely to hash identity. ExpansionVersion 1 and transformed policy identity become root/effective identity inputs. No-prefab analytic routing remains unchanged.

- [ ] **Step 4: Run green** with Step 2's filter as `t2-green`, then `wa_test t2-regress "$MAPDOC" "FullyQualifiedName~NativeNumericId|FullyQualifiedName~Identity|FullyQualifiedName~StoredSurface|FullyQualifiedName~FormatFour"`. Expect complete equality for monolithic/tiled/repacked forms and changed identities for each semantic mutation.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t2_paths=(
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
)
wa_finish t2 "${wa_r5_t2_paths[@]}" &&
git add -- "${wa_r5_t2_paths[@]}" &&
git commit --only -m "feat(mapdoc): persist prefab child reservations and typed identity" -- "${wa_r5_t2_paths[@]}"
```

### Task 3: One transformed effective resource view

**Files:**
- Create: `KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs`, `MapEffectiveResources.cs`, `MapResourceTransform.cs` in Prefabs, `KhaozEngine.MapDoc/Storage/MapEffectiveSurfaceSource.cs`, `MapEffectiveAcquisition.cs`, `KhaozEngine.MapDoc/Surfaces/MapEffectiveGeometry.cs`.
- Modify: `KhaozEngine.MapDoc/Support/MapResolverV2.cs:16-46`, `MapNativeResolution.cs:21-60`, `MapResolvedDocument.cs:54-73`, `Storage/MapScopedSurfaces.cs:17-100`, `Storage/MapAcquiredFacts.cs:79-97`, `Surfaces/MapFrameLocal.cs:39-78`, `Spaces/MapBoundFaceContext.cs:133-157`, `Spaces/MapBoundPreparation.cs:47-97`, `Spaces/MapCommonRefinement.cs`, `Spaces/MapSpaceGeometry.cs:13-100`, `Surfaces/MapBoundaryGeometry.cs`.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabExpansionTests.cs`, `MapPrefabTransformTests.cs`, `PrefabTransformFixtures.cs`.

**Interfaces:**
- Consumes `MapTransform.Compose(MapTransform parent, MapTransform local) -> MapTransform` at MapResolvedDocument.cs:18. Keep its signature and formula as the sole parent/local composition.
- Consumes `MapResolverV2.Resolve(MapDocument document, MapAssetClosure assets, IMapSurfaceSource surfaces, MapResolveOptions options) -> MapSupportedResolution` at Support/MapResolverV2.cs:16, `MapScopedSurfaces.Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256, int queryPolicyVersion = 1, int buildPolicyVersion = 1)`, and `CompleteView(MapSurfaceSet set)` at Storage/MapScopedSurfaces.cs:48 and :73.
- Produces `MapPrefabExpansionLimits(int MaxInstances = 65536, int MaxChildren = 1048576, int MaxReferenceEdges = 262144)`, `MapPrefabResolver.Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits? limits = null) -> MapEffectiveResources`.
- Produces internal `MapResolverV2.ResolveEffectivePlacements(MapDocument authored, MapAssetClosure assets, IReadOnlyList<MapPlacement> effective, IMapSurfaceSource surfaces, out IReadOnlyList<MapPlacementSupport> supports) -> IReadOnlyList<MapResolvedPlacement>`, a non-expanding entry into the existing R2 support core. MapEffectiveResources adds `PlacementSupports: IReadOnlyList<MapPlacementSupport>`, the released support record type.
- Produces immutable `MapEffectiveResources`: `Placements: IReadOnlyList<MapResolvedPlacement>`, `Surfaces: MapSurfaceSet` returned as a defensive clone, `Transforms: IReadOnlyDictionary<string, MapTransform>`, `Water: MapWaterSet?` defensive clone, `BuildingIntent: MapBuildingIntent` defensive clone, `Digest: string`, `SurfaceSource: IMapSurfaceSource`, `CompleteView() -> MapScopedSurfaces`, `Acquire(MapSurfaceScope scope) -> MapScopedSurfaces`.
- Produces `MapResourceTransform(MapTransform Transform)` with `Point(MapExactPoint local) -> MapExactPoint`, `Height(MapExactValue local) -> MapExactValue`, `ToFrame(MapExactPoint world, WorldFrame frame) -> Vector3`, and `Normal(Vector3 local) -> Vector3`.
- Produces internal `MapEffectiveGeometry.Patch(MapScopedSurfaces view, MapPatchKey key) -> MapCompiledPatch` and `Strip(MapScopedSurfaces view, MapWallStrip strip) -> MapCompiledStrip`, preserving remapped face keys and canonical topology.
- Produces internal `CellCandidates(MapScopedSurfaces view, string surfaceId, MapExactXz world) -> IReadOnlyList<(long X, long Z)>` and `CellPolygon(MapScopedSurfaces view, MapPatchKey key, int slotCell) -> IReadOnlyList<MapExactXz>` on MapEffectiveGeometry. Existing bound/refinement APIs keep their signatures and use these effective world polygons internally.
- Adds nullable get-only `MapResolvedDocument.EffectiveResources: MapEffectiveResources?` through an internal constructor overload. Existing signatures remain. Scoped view owns an immutable effective context and its transform/resource witness.
- Standalone NativeResourceTransforms enter that same context. Their SourceDigest and owner bindings enter loaded/stored root identity, scoped witnesses and effective geometry digests.

- [ ] **Step 1: Write failing tests.**

```csharp
[Theory] [InlineData(0.8f, 7.3999996f)] [InlineData(1.2f, 8.6f)]
public void AllChildResources_UseOneTransformAndSharedEdge(float scale, float upperY)
{
    var f = PrefabFixtures.Transformed(0.371f, scale, new(10.25f, 5, -3.125f));
    var effective = PrefabFixtures.Effective(f);
    var composed = MapTransform.Compose(f.Document.Prefabs!.Instances.Single().Transform,
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
[Fact] public void ScopedChildren_RequireTheirFullWitness()
{
    var f = PrefabFixtures.Create("seam-instance");
    var resources = PrefabFixtures.Effective(f);
    var missing = PrefabFixtures.AcquireWithout(resources, "parent-floor");
    Assert.Equal(MapAcquireStatus.Incomplete, missing.Status);
    Assert.False(missing.ReadWitness.Complete);
    Assert.NotEmpty(missing.Witness.Unavailable);
}
```

Task 3's fixture file declares `SharedFloorCorner/SharedRoomCorner/SharedWaterCorner(MapEffectiveResources resources) -> MapExactPoint`, `StoreyUpper(MapEffectiveResources resources) -> MapExactValue`, and `AcquireWithout(MapEffectiveResources resources, string dependencyId) -> MapScopedSurfaces`. These expose transformed data from the actual context, not independent arithmetic. Before Task 4 they inspect the pending room record's geometry, not membership. Add `FarFrame_SharedEdgesAgreeWithoutAbsoluteFloatRoundTrip`, `ExpansionBudget_RefusesBeforePublishingChildren`, `ScopedDigest_FollowsTransformAssetAndBindings`, and `LaterCallerMutation_CannotChangeEffectiveView`.

Add `MissingYChild_IsSeatedOnceOnTheEffectiveFloor`. Variant support-bound-child has parent transform (10.25,5,-3.125), yaw 0.371, scale 1.2, low-item with null local Y and a surface binding to local floor at 0. Assert resolved world Y exactly 5, equal to MapSupportQuery.SampleSurface on the effective floor, and unchanged allocator/document bytes. A second parent transform of Y -450 proves no parent-height addition or scale after sampling.

- [ ] **Step 2: Run red** `wa_test t3-red "$MAPDOC" "FullyQualifiedName~MapPrefabExpansionTests|FullyQualifiedName~MapPrefabTransformTests"`. Expect absent expansion/context or current independent transform paths disagreeing.
- [ ] **Step 3: Implement expansion, exact transformation and acquisition.**

Validate, snapshot closure bytes/records, apply overrides by typed key, remap persisted IDs and compose each child transform once. Native standalone placements remain in the same snapshot. A root container is not a replacement placement. Expand all kinds, keeping local geometry and the composed MapTransform beside it. Missing bindings refuse. No allocations, tags-based inference or repeated expansion in the builder.

Build the effective geometry/source before resolving missing-Y leaves. Transform a child's support ReferenceY and local search extents into world units, remap its surface/space owner, and sample through the non-expanding R2 entry point. Install the returned world Y directly. Explicit-Y children compose normally. ResolveEffectivePlacements also handles standalone placements against that source. Public resolver 2 computes authored identity from the original document, then constructs its resolved snapshot and support list from the one expansion. It never hashes a derived expanded document or recursively expands while sampling.

Point evaluation uses double c/s from Math.SinCos, authored rational coordinates converted to double, and the composed float transform promoted exactly to double. Quantize each placed world coordinate with `ticks = checked((long)Math.Round(value * 1048576d, MidpointRounding.ToEven))`, giving exact `new MapExactValue(ticks, 1048576)`. World Y then rounds once through ToSingle and freezes through FromSingle. Apply this to every placed floor vertex, room bound, water level, light and marker. Height delegates to Point's Y rule. This preserves level ties, limits geometric rounding to 0.000000476837158203125 m and avoids the long-denominator refusal that freezing binary64 trigonometric coordinates would cause in MapExactValue.Multiply/Divide (MapExactValue.cs:49-58).

This is a significant proposed choice under R5-P3. Scores are engineering judgments from 1 to 10, weighted to 100. Absolute floats are cheap but lose far-origin geometry. Full binary64 freezing requires a broader exact-arithmetic representation and query rewrite. The fixed dyadic quantum retains sub-micrometre XZ precision with bounded released rational values.

| Criterion | Weight | Absolute binary32 | Binary64 freeze and wider R2 representation | Dyadic quantum and float height seam |
| --- | ---: | ---: | ---: | ---: |
| Shared-edge agreement | 25 | 10 | 10 | 10 |
| Released R2 arithmetic fit | 30 | 8 | 5 | 9 |
| Far-origin precision | 20 | 3 | 10 | 9 |
| Consumer compatibility | 15 | 6 | 5 | 8 |
| Scoped effort | 10 | 9 | 3 | 8 |
| Weighted total | 100 | 73 | 70.5 | 90 |

Recommend the dyadic policy. Its cost is explicit coordinate rounding and rejection when geometry collapses or still cannot narrow. It does not promise exact mathematical trigonometry or unrestricted sub-quantum content.

Normalize negative zero. Refuse collapsed/nonfinite heights and exact outputs that cannot narrow. Frame output subtracts XZ integer anchors exactly before ToSingle. Never first form an absolute XZ Vector3 or round rotated points into MapLatticeAddress. Preserve raw unplaced lattice geometry. Shapes retain source-unit conversion once and the documented backend float-pose adapter. Exact predicates/refinement on effective geometry use the R4 wide kernel until emitted values narrow, so intermediate arithmetic never becomes a silent float fallback. The far-height test reports the binary32 height error bound instead of claiming sub-quantization precision. Pin quantum/tie rule in effective policy identity and in an independent transform golden.

MapFrameLocal.CompileInFrame delegates to this point kernel. Placed R4 water converges in Task 7, using the same frozen vertices, not quaternion float math. Unit transforms on existing unplaced lattice geometry retain their exact vertices. Policy `kemap/effective-resources/1` names expansion and transform rules, query policy 2 names transformed spatial reads. These enter effective/scoped/build identities. Same-process independent heads agree. Residual Math.SinCos platform dependence remains documented.

MapEffectiveSurfaceSource implements IMapSurfaceAcquisitionSource, including OpenAcquisition and incident-record reads. Index remapped local patch world bounds, including every transformed corner, and request local patches by conservative inverse bounds. Local slot coordinates remain local, never masquerade as world slots. World scopes and whole-column completeness use world bounds. Charge discovery, records and dependency expansion before work under existing budgets. Snapshots include parent/domain/transform/binding/resource facts even when their anchors lie outside the requested XZ. An unavailable dependency prevents a complete witness. KnownEmpty remains distinct from unloaded/missing.

MapScopedSurfaces snapshots this effective context with its acquired facts. Both CompleteView and scoped paths use the same geometry evaluator. Resolver 2 attaches one effective snapshot and resolves support bindings against its source, including selected expanded placement IDs in ResolvePlacements. Resolver 1 rejects prefab sections before callbacks and retains its released path. R8 still owns runtime eviction and large-world scheduler proofs.

MapBoundFaceContext transforms its compiled canonical bound faces and opening planes through MapEffectiveGeometry. MapBoundPreparation/CommonRefinement use the transformed cell polygon rather than an axis-aligned world rectangle inferred from local lattice addresses. Boundary chains/strips use the same point kernel. Inverse double coordinates discover a conservative local cell halo only, charged under the existing budgets. Final selection uses exact world polygons and canonical triangle predicates, so inverse rounding never decides ownership. Surface normals are derived from effective canonical triangle vertices after Y rounding. This is one query pipeline, not a local sampler beside R2.

- [ ] **Step 4: Run green** with Step 2's filter as `t3-green`, then `wa_test t3-regress "$MAPDOC" "FullyQualifiedName~FrameLocal|FullyQualifiedName~NativeResolution|FullyQualifiedName~Acquisition|FullyQualifiedName~Support"`. Expect shared exact vertices, intact witnesses and no change to unplaced lattice/analytic results.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t3_paths=(
    KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveResources.cs
    KhaozEngine.MapDoc/Prefabs/MapResourceTransform.cs
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
- Produces internal `MapOccupiedPiece(string SpaceId, IReadOnlyList<MapExactXz> Polygon, IReadOnlyList<MapBoundFace> LowerFaces, IReadOnlyList<MapBoundFace>? UpperFaces, MapExactValue? LowerPlane, MapExactValue? UpperPlane)`, `MapOccupiedPartitions.Pieces(MapScopedSurfaces view, string spaceId, MapExactXz min, MapExactXz max, MapRefinementLimits limits) -> IReadOnlyList<MapOccupiedPiece>`.
- Produces `MapPrismCoverage.Validate(MapScopedSurfaces view, MapRefinementLimits limits) -> IReadOnlyList<string>`, called by the existing coverage validator. Adds immutable `MapMembershipResult.ParentSpaceIds: IReadOnlyList<string>` without removing its constructor/signatures.
- Produces `MapSpaceMembership.QueryExact(MapExactPoint world) -> MapMembershipResult` and `MapSpaceIntervals.AtExact(MapScopedSurfaces view, MapExactPoint world) -> MapSpaceInterval`. They share QueryCore with the released frame-point APIs, passing exact coordinates without a float round trip. MapMembershipResult gains get-only ExactPoint. Point remains its rounded frame-point presentation.

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
[Fact] public void MissingParentOrPrism_IsMissingGeometry()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Cave("missing-parent"));
    var view = PrefabFixtures.AcquireWithout(r, "parent-floor");
    Assert.Equal(MapMembershipStatus.MissingGeometry,
        new MapSpaceMembership(view).Query(PrefabFixtures.Point(r, 2, 1, -2)).Status);
}
```

This task declares `RoomBoundaryPoints(MapEffectiveResources resources) -> IReadOnlyList<MapExactPoint>` and `ExpectedBoundaryOwner(MapEffectiveResources resources, MapExactPoint point) -> string`. Expected owners are an explicit fixture table, not computed using product Contains. Include lower edges, upper edges, convex corners, the L reflex corner tiled by a square, transformed shared walls between two instances and both sides of storey y 3. Two adjacent instances use a common authored transform and complementary local polygons, so the shared wall is genuinely identical. Independently rounded placements that merely nearly touch are not silently snapped or aliased. Use yaw 0.371, scales 0.8/1.2, negative Z and the far transform. Exact boundary proofs use QueryExact, while representable frame points also exercise the released Query path.

Add named `SelectedCaveModel_ComposesFloorsCeilingsAndVolumes`: two variable-bound R2 cave spaces at -60 to -50 and -40 to -30 with a room inside each. Assert both cave bounds remain canonical, each room is the occupied child with its cave parent, and no fixed 4.5 m layer rule appears. `PrismOverUnevenParent_UsesCanonicalIntersection` pins floor heights -0.5 to 0.5 and room [0,3), resolving the room only above max(parent floor, 0). `PrismWithNoAuthoredParent_Refuses`, `OutsideParentFootprint_Refuses`, `ZeroHeightOrSelfIntersectingPrism_Refuses`, and `Column_PartitionsParentAroundNestedStoreys` cover full geometry and publication. Existing R2 cave/portal answers remain pinned.

- [ ] **Step 2: Run red** `wa_test t4-red "$MAPDOC" "FullyQualifiedName~MapPrismMembershipTests|FullyQualifiedName~MapPrismCoverageTests"`. Expect unsupported topology record/domain, duplicate occupied owners or wrong boundary selection.
- [ ] **Step 3: Implement domains, nested occupancy, coverage and query integration.**

A room uses a MapSpaceDoc and one MapPrismDomain. Persist local polygons/heights and the instance owner. Compile its footprint through the same exact transformed domain path as water. Normalize world XZ winding. R4-P28 ownership uses the world direction (1, infinitesimal +Z), including the convex/reflex/collinear vertex distinction. Do not apply the edge rule in local coordinates after rotation.

At a point, acquire the room, parent chain, footprint and canonical bounds before choosing. Intersect the prism [lower,upper) with its parent's actual interval. Choose the deepest containing descendant. Remove containing ancestors from the competing-owner set and return them separately in ParentSpaceIds/DomainKeys. Two incomparable descendants remain Ambiguous. A parent's remaining occupied volume excludes all descendants. Columns split into disjoint intervals, including repeated parent IDs in separate intervals, and use identical tie rules.

Lattice-only portals retain released R2 ToSpace exact-plane ownership. A portal incident to a new prism room uses the prism's R4-P28 world-edge owner for occupied membership, consistently in point, column and water partition paths. Its authored From/To normal remains a relation fact, not a competing owner rule. Add `RoomPortalPlane_UsesPrismOwnershipAfterYaw` and `LatticePortalPlane_KeepsReleasedToSpaceOwnership`, including a portal whose ToSpace points against world +X. This implements Decision 2 without changing old cave outcomes.

Coverage validates nonempty simple polygons, positive effective separation over common refinements, complete horizontal coverage by the declared parent footprint, ancestry without cycles and room/storey ownership. Use shared polygon clipping and R2 bound faces to check every refinement vertex, not corner samples or a bounding box. Rooms are semantic spaces, so their boundary need not fabricate a physical wall. Existing Cave enclosure rules still require walls/ceilings/apertures. Preserve aliases and distinguish semantic nesting from peer overlap. No implicit exterior.

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
- Create: `KhaozEngine.MapDoc/Surfaces/MapSupportResource.cs`, `KhaozEngine.MapDoc.Physics/MapPrefabSupport.cs`.
- Modify: `KhaozEngine.MapDoc.Physics/MapAssetShapes.cs:46-64`, `MapTerrainPhysics.cs:107-146` and chunk inputs, `MapPlacementGeometry.cs:64-126`, `MapWorldBuilder.cs:50-93`, `MapBuiltWorld.cs:80-158`, `MapResidencyOwnership.cs:45-96` and `:152-205`, `MapNavTiling.cs:75-128` and `:225-270`, `MapPhysicsRegistration.cs:101-112`, `README.md`.
- Modify: Task 2's binding planner and Task 3's expansion/geometry for SupportResourceIds. `KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj` links self-contained public Prefabs/*Fixtures.cs source files, without a test-project reference or TileWorld edge. `KhaozEngine.MapDoc/KhaozEngine.MapDoc.csproj:15-18` adds InternalsVisibleTo for KhaozEngine.MapDoc.Physics.Tests, so linked identity/geometry fixture helpers compile.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapSupportResourceTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPrefabSupportTests.cs`, `MapPrefabDoorwayTests.cs`, `MapPrefabResidencyTests.cs`, `PrefabPhysicsFixtures.cs`.

**Interfaces:**
- Consumes released `MapAssetShapes.Read(MapAssetClosure closure, string assetId)` at :51, `MapSurfaceCompiler.Compile(MapSurfaceRef surface, MapSurfacePatch patch) -> MapCompiledPatch` at :14, `MapTerrainPhysics.Compile(MapScopedSurfaces view, MapTerrainChunkPolicy policy) -> MapTerrainChunkSet` at :107.
- Consumes `MapWorldBuilder.Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) -> MapBuiltWorld` at :40, `MapPlacementShapes.Resolve(MapResolvedDocument document)` at MapPlacementGeometry.cs:64, `MapPhysicsRegistration.TryFaceOwner(StaticHandle handle, int faceId, out MapFaceKey face) -> bool` at :101.
- Consumes `MapPhysicalRelations.Clearance(IPhysicsQueryLease lease, IReadOnlyList<MapFramePoint> feetPath, in MoveTuning tuning) -> MapPhysicalResult` at :88, using ContactShell and the controller's released sweep filter.
- Consumes `MapResidencyOwnership.Build(MapBuiltWorld world, MapWorldGrids grids)`, `Affected(MapBuiltWorld world, MapWorldGrids grids, MapNativeEditEffects effects)`, and `MapNavTiling.AffectedTiles(MapBuiltWorld world, MapWorldGrids grids, MapNavTileOptions options, MapNativeEditEffects effects)`.
- Produces `MapSupportResource(int PayloadVersion, MapSurfaceSet Surfaces, MapRecordRef? Space)`, `MapSupportResourceFile.Read(ReadOnlyMemory<byte> bytes, string resourceId) -> MapSupportResource`, `Write(MapSupportResource resource) -> byte[]`, in MapSupportResource.cs.
- Produces `MapPrefabSupport.Install(MapEffectiveResources resources) -> MapEffectiveResources`, pure, and `Bounds(MapEffectiveResources resources) -> MapBox3`. Adds get-only `MapAssetShapes.SupportResources: IReadOnlyList<MapSupportResource>` without removing released constructors.
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

Add `CoincidentSupportOwners_Refuse`, `PaintAddsNoPhysicsFaces`, `ScaledLowItem_StillHasOneMetreReachMinimum`, `LocalChunks_Keep1024TrianglesAnd64MetreOffsets`, `RotatedBounds_NameEveryCrossedTileOnce`, `RemovedInstance_InvalidatesOldTiles` and `TerrainSupportReseat_HasAReachableNeighborCounterexample`. The latter constructs the #1348 lead, with a 150 m leaf support-bound to an edited floor at a 64 m tile seam, showing that geometry outside the edited patch really changes. Task 9 proves the transaction emits the required widening.

- [ ] **Step 2: Run red** `wa_test t5-red-doc "$MAPDOC" "FullyQualifiedName~MapSupportResourceTests"`, then `wa_test t5-red "$MP" "FullyQualifiedName~MapPrefabSupportTests|FullyQualifiedName~MapPrefabDoorwayTests|FullyQualifiedName~MapPrefabResidencyTests"`. Expect the released placement-local support refusal, missing provenance or wrong transformed bounds.
- [ ] **Step 3: Implement support resource installation and existing builder consumers.**

Surface resource payload 1 embeds the existing surface ref/patch codec and its exact units, topology and space reference. It declares no analytic terrain. Validate role, bound owner and reference closure. SupportResourceIds decode through MapDoc's codec. Remove MapAssetShapes' unconditional refusal only when those resources validate. Floor geometry is installed through MapTerrainPhysics, not a second collider mesh.

Support frames declare metres through MapLatticeFrame's units. Source-unit conversion is already encoded there and is not applied again. Collider/render bounds still use the asset's SourceUnitsToMetres once. Add `SourceUnitsAreEncodedInSupportFrames_AndNotAppliedAgain` with source units 0.01, 100 source units per metre, instance scale 1.2 and expected 1.2 m support/collider edge agreement.

Every prefab-attached support resource enumerates implicit child keys under its placement key, resource ID and local surface/record ID. The binding planner and expansion enumerate the same closure children and persist their bindings. Standalone support-bearing placements use deterministic derived string IDs from their stable placement ID and resource-local key, with no numeric allocation. Their support owner never equals a collider's unrelated face. Refuse independently authored coincident support faces, including a baked collider floor duplicated by a support resource. Do not claim arbitrary primitive edges/vertices map to floor faces.

MapWorldBuilder consumes resolved.EffectiveResources once. Terrain/strip compilation uses effective exact geometry, preserves face keys/roles and selects whole-metre chunk anchors after transform. Keep existing chunk ID grammar, sidedness, triangle and local-extent limits. Placement compounds retain jambs, lintel and offsets with source-unit/instance scale applied once. Interaction raising happens after scale, in world metres. No whole-building box.

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
    KhaozEngine.MapDoc.Physics/MapPrefabSupport.cs
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
    var conflict = PrefabFixtures.Effective(PrefabFixtures.Create("equal-priority-paint"));
    Assert.Throws<MapDocumentException>(() => MapPartialPaintCompiler.Compile(conflict.CompleteView(), "ground"));
    var ordered = PrefabFixtures.Effective(PrefabFixtures.Create("prioritized-paint"));
    Assert.NotEmpty(MapPartialPaintCompiler.Compile(ordered.CompleteView(), "ground").Regions);
}
```

Uneven-painted is a 2 by 2 m local paint rectangle at non-grid position, yaw 0.371 and scale 1 over a sloping 1 m world lattice. Its ideal area is 4 square metres, with the explicit 0.000004 square-metre rounding bound above. Clipping must conserve the quantized footprint's exact area. Assertions compare physical inputs independent of paint digest. Add `MoveUndoReload_DoesNotWriteWorldMaterialArrays`, `PaintAbsentNotch_LeavesUnderlyingMaterialVisible`, `RenderSubdivisions_LieOnTheirParentTriangle` and `PaintBudgetAndUnrepresentableIntersection_Refuse`.

- [ ] **Step 2: Run red** `wa_test t6-red "$MAPDOC" "FullyQualifiedName~MapPrefabPaintTests"`. Expect the current physical-face boundary refusal or missing material region API.
- [ ] **Step 3: Implement exact clipped material regions and compatibility adapter.**

Clip transformed paint polygons in XZ against each unchanged canonical target triangle using the shared wide rational kernel, then lift each intersection back onto that triangle's plane. Emit only material coverage and parent face keys. Do not copy paint height into target geometry. Subtract higher-priority regions and retain uncovered base material. Equal-priority positive-area conflicting materials refuse, identical coverage coalesces in ordinal ID order. Half-open semantic ownership follows R4-P28, while draw polygons may share closed edges. All emitted polygon points narrow exactly or fail.

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
- Modify: `KhaozEngine.MapDoc/Water/MapWaterDomain.cs:12-16` and `:59-130`, `MapWaterCompiler.cs:25-120` and digest composition, `MapWaterContainment.cs:23-110`, `MapWaterFootprints.cs:32-95`, `MapWaterBoundReads.cs`, `MapWaterQuery.cs:66-140`, `MapCompiledWaterBody.cs`, `KhaozEngine.MapDoc/MapBoundDocumentValidation.cs:14-74`, Task 3's effective context.
- Modify: `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs:58-93`, `MapResidencyOwnership.cs:77-123`, `MapNavTiling.cs` medium digest inputs.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabWaterTests.cs`, `PrefabWaterFixtures.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPrefabWaterWorldTests.cs`.

**Interfaces:**
- Consumes `MapWaterDomain.Compile(MapWaterDomainDoc doc, MapWaterPlacement? placement = null) -> MapWaterDomain` at :59, `MapWaterCompiler.Compile(MapScopedSurfaces view, MapWaterSet water, MapWaterCompileLimits? limits = null)` at :25, `MapWaterContainment.Validate(MapScopedSurfaces view, MapWaterSet water)` at :23.
- Consumes `MapWaterQuery(MapScopedSurfaces scoped, MapWaterSet water)`, `Sample(MapFramePoint point) -> MapWaterSample`, `Column(MapFramePoint point) -> IReadOnlyList<MapWaterSample>`, Task 4 occupied partitions and Task 3 immutable transform context.
- Produces internal `MapEffectiveWaterBody(MapWaterBodyDoc Source, MapWaterDomain Domain, MapExactValue SurfaceY, string BodyId, IReadOnlyList<MapRecordRef> Covers, string TransformDigest)`, `MapEffectiveWater.Resolve(MapScopedSurfaces view, MapWaterBodyDoc body) -> MapEffectiveWaterBody`.
- Produces internal `MapWaterOccupiedPieces.Of(MapScopedSurfaces view, string spaceId, MapExactXz min, MapExactXz max, MapRefinementLimits limits) -> IReadOnlyList<MapOccupiedPiece>`, delegating to Task 4.
- Existing public query/compiler signatures remain. They discover transforms from the view and snapshot them alongside water. Compiled/sample nominal heights are world heights and every BodyId/Covers is already binding-remapped.
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
    Assert.NotEqual(MapWaterRegionKind.FreeSurface, body.Classify("outside", point.ExactWorldXz()));
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

Declare `PoolProbe/DryCavePoint/FloodedCavePoint(MapEffectiveResources resources) -> MapFramePoint` here. Cave floors at -60 and -40, ceilings -50 and -30, exterior ocean 0 and separate local lake -35 are explicit. The dry room is not a cover. Exterior-water-dry-room uses exterior level 2 and room [0,3), so the parent water below the room can be Closed but emits no free surface through it. Add `SharedFloorWaterAndRoomEdge_AgreeExactly`, `CeilingTie_RemainsClosedAfterTransform`, `MissingLocalCoverOrParent_IsUnverifiedNeverDry`, `LocalWaterNeverBecomesAStatic`, `MoveChangesOnlyIntersectedMediumTiles`, `UnrelatedAssetChildEdit_KeepsBodyDigest`, `UnavailableAperture_PreventsCompleteContainment`, and `ExistingCompileLimits_ChargeRotatedAndAbsentBoundWork`. These invoke the full compiler/query/builder, never only a transformed clip helper.

- [ ] **Step 2: Run red** `wa_test t7-red-doc "$MAPDOC" "FullyQualifiedName~MapPrefabWaterTests"`, then `wa_test t7-red "$MP" "FullyQualifiedName~MapPrefabWaterWorldTests"`. Expect untransformed full-body data, parent water leaking through a room partition or absent effective containment.
- [ ] **Step 3: Implement R4 adapters and the shared transform convergence.**

MapWaterPlacement's public fields/signature remain, but its placed path delegates to MapResourceTransform. Preserve exact unplaced lattice geometry. Resolve a local body to its remapped covers, transformed polygon, transformed nominal level and transform digest. Apply the same path in validation, query, full compilation, containment and bounds. Never round rotated vertices back into a saved lattice frame.

Water covers occupied partitions, not raw parent footprints. Subtract uncovered descendants from parent regions, and clip a room's body against its canonical parent/prism intersection. Virtual parent fragments with no positive wet interval at the body's level emit no free surface. Fragments ending below the level are Closed. This adapter uses the existing interval and height-split kernel. Preserve unplaced R4 terrain-above-water drawing behavior. Explicit multi-space coverage still needs R4 portal/link connectivity below the level, with ties closed. Parentage alone is not a hydraulic connection and covering a parent does not cover a child. Closed/free contour splitting, exact bound reads, bed depths and work limits remain R4 algorithms.

Compile lower and upper canonical effective faces using Task 3 geometry and Task 4 bounds. Preserve R4's MissingGeometry, Incomplete, CapacityExceeded, NotRepresentable and Invalid distinctions and every before-work charge. Digest includes consumed definition, medium, remapped covers, effective transform/policy, consumed geometry metadata and compiled outcome. A local edit outside the body's dependencies leaves its digest unchanged. World water unrelated to the prefab remains unchanged.

TransformDigest and NativeResourceTransforms.SourceDigest use per-child semantic inputs. Never feed the containing prefab SHA, closure hash or whole scoped digest into a compiled body digest. A marker-only asset version change moves whole authored identity but leaves an unrelated local body's digest and wet-tile medium identities unchanged.

Built-world water uses effective data and remains absent from statics. Bounds/residency and nav medium digests use actual effective regions. Owner IDs stay `water:<bound-body-id>`, with the released static collision check. Every editor publication of a water document checks all effective bodies, even when the accepted change is only a prefab, placement or roof edit. Task 9 wires that publication. R5 adds no movement adapter, capsule classifier or optical algorithm.

- [ ] **Step 4: Run green** with both Step 2 filters as `t7-green-doc` and `t7-green`, then `wa_test t7-regress "$MAPDOC" "FullyQualifiedName~Water|FullyQualifiedName~SpaceInterval"` and `wa_test t7-world-regress "$MP" "FullyQualifiedName~WaterWorld|FullyQualifiedName~WaterMovementInputs"`. Expect exact region/query agreement, unaffected R4 fixtures and explicit scoped refusals.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t7_paths=(
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
- Create: `KhaozEngine.MapDoc/Interiors/MapRoofIntent.cs`, `MapRoofPolicy.cs`, `KhaozEngine.MapDoc/Prefabs/MapLightFile.cs`, `MapEffectiveAnnotations.cs`.
- Modify: Task 3's effective view, Task 1's annotation validation/codecs, `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs:80-158`, `MapWorldBuilder.cs:85-93`, `MapResidencyOwnership.cs:45-96`, MapDoc and MapDoc.Physics README.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabAnnotationTests.cs`, `PrefabAnnotationFixtures.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPrefabRoofStaticTests.cs`.

**Interfaces:**
- Consumes MapMembershipResult, its ParentSpaceIds/DomainKeys, remapped roof/group/light/marker records, Task 3 transforms and Task 5 bounds. No Render3D or Scene3D type crosses this boundary.
- Produces in MapDoc.Interiors `MapRoofMode { Auto, AlwaysVisible, AlwaysHidden }`, `MapRoofIntent(string RoofPlacementId, bool ColourVisible, bool CastShadows, bool CameraBoomBlocks, bool SkyPickingBlocks)`, `MapRoofIntentResult(MapMembershipStatus Status, string? SpaceId, IReadOnlyList<MapRoofIntent> Roofs, MapReadWitness Witness)`.
- Produces `MapRoofPolicy.Evaluate(MapEffectiveResources resources, MapScopedSurfaces view, MapFramePoint observer, MapRoofMode mode) -> MapRoofIntentResult`.
- Produces `MapEffectiveLight(int PayloadVersion, string Id, string? OwnerPlacementId, MapExactPoint Position, Vector3 Colour, float Intensity, float Radius, bool CastShadows)`, `MapEffectiveMarker(int PayloadVersion, string Id, string Name, MapExactPoint Position, float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags, string? InstanceId)`.
- Produces `MapLightFile.Read(ReadOnlyMemory<byte> bytes, string resourceId) -> MapLocalLightDoc`, `Write(MapLocalLightDoc light) -> byte[]`, `MapEffectiveAnnotations.Lights(MapEffectiveResources resources) -> IReadOnlyList<MapEffectiveLight>`, `Markers(...) -> IReadOnlyList<MapEffectiveMarker>`. MapBuiltWorld adds immutable Lights/Markers/RoofLinks/RoofGroups collections and AnnotationDigest.

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
[Fact] public void LocalLightAndMarker_PreserveVersionFieldsAndTransform()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Transformed(0.371f, 1.2f, new(10.25f, 5, -3.125f)));
    Assert.All(MapEffectiveAnnotations.Lights(r), x => Assert.Equal(1, x.PayloadVersion));
    var marker = MapEffectiveAnnotations.Markers(r).Single();
    Assert.Equal((1, "entry", "entrance", true, "a"),
        (marker.PayloadVersion, marker.Name, marker.Role, marker.Enabled, marker.InstanceId));
    Assert.Equal(new[] { "first", "second" }, marker.Tags);
    Assert.Equal(new MapResourceTransform(r.Transforms[marker.Id]).Point(new(new(0, 1), new(1, 1), new(0, 1))), marker.Position);
}
[Fact] public void UnresolvedObserver_KeepsRoofsVisibleWithStatus()
{
    var r = PrefabFixtures.Effective(PrefabFixtures.Create("linked-buildings"));
    var view = PrefabFixtures.AcquireWithout(r, "parent-floor");
    var result = MapRoofPolicy.Evaluate(r, view, PrefabFixtures.LinkedRoomPoint(r), MapRoofMode.Auto);
    Assert.Equal(MapMembershipStatus.MissingGeometry, result.Status);
    Assert.All(result.Roofs, roof => Assert.True(roof.ColourVisible));
    Assert.False(result.Witness.Complete);
}
```

Declare `LinkedRoomPoint(MapEffectiveResources resources) -> MapFramePoint` here. Add `UnresolvedObserver_KeepsRoofsVisibleWithStatus`, `ObserverAtUpperStorey_HidesOnlyAboveLinks`, `AlwaysModes_PreserveShadowAndSkyIntent`, `HidingNeverChangesStaticsOrInteractionGeometry`, `LightBoundsIncludeScaledRadiusAndOwnerOffsetOnce`, and `UnknownRoofRoomOrLightOwner_Refuses`. Roof-static proof compares full descriptor digests and registration handle counts before/after every mode. No pixel assertion is implied.

- [ ] **Step 2: Run red** `wa_test t8-red-doc "$MAPDOC" "FullyQualifiedName~MapPrefabAnnotationTests"`, then `wa_test t8-red "$MP" "FullyQualifiedName~MapPrefabRoofStaticTests"`. Expect missing intent/data or linked-room policy.
- [ ] **Step 3: Implement intent and effective annotations.**

Resolve groups into explicit room sets, including rooms in different instances. A link hides its roof in Auto when the resolved observer room belongs to the link/group and observer world Y is below the transformed AboveY. A parent key alone does not hide every descendant roof. Do not infer groups from portals or flood fill. An unlinked open shelter stays visible. AlwaysHidden hides roofs deliberately, AlwaysVisible shows them. Missing/ambiguous observer membership carries that status and conservatively visible Auto intent.

For hidden roofs set colour false, caster true, boom false and sky true. Visible roofs set all four true. Geometry, collision and interaction arrays remain unchanged for every mode. These flags are intent for R8's separate consumers, not a universal hide predicate.

Light resource payload 1 has strict fields and validation: finite nonnegative colour/intensity, positive finite radius and valid optional owner. Compose owner-local offsets exactly once, scale radius once, leave colour/intensity unchanged, and retain stable light IDs/shadow intent. Marker payload 1 preserves XYZ, yaw, name, role, enabled and ordered tags, with optional instance ownership. It never becomes MapSpawn. Both enter effective digest and bounds/residency, sorted by stable ID. R6 chooses role registries and spawn projections, R8 renders lights.

- [ ] **Step 4: Run green** with both Step 2 filters as `t8-green-doc` and `t8-green`. Expect both linked roofs hidden with independent flags, unchanged statics and stable effective light/marker records after reload.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t8_paths=(
    KhaozEngine.MapDoc/Interiors/MapRoofIntent.cs
    KhaozEngine.MapDoc/Interiors/MapRoofPolicy.cs
    KhaozEngine.MapDoc/Prefabs/MapLightFile.cs
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

### Task 9: Atomic commands, immutable asset publication and ke-mapedit

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapPrefabEdits.cs`, `MapPrefabEditPreparation.cs`, `MapInteriorEdits.cs`, `MapPrefabExtraction.cs`, `MapPrefabAssetEdits.cs`.
- Create: `KhaozEngine.MapEditor/NativePrefabCommands.cs`, `NativeInteriorCommands.cs`, `NativePrefabAssetCommands.cs`, `MapPrefabLocalSession.cs`.
- Create: `KhaozEngine.MapEdit.Tool/PrefabService.cs`, `PrefabAssetPublisher.cs`, `MapEditSession.NativeHistory.cs`, `MutationService.Prefabs.cs`, `Tools/PrefabTools.cs`, `Tools/InteriorTools.cs`.
- Modify: `KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs:10-23`, `MapNativeEditEffects.cs:43-85`, Task 1's root codec/schema, `KhaozEngine.MapEditor/NativeDocumentSnapshot.cs:13-78` and `:104-130`, `NativeDocumentTransaction.cs:31-85`, `NativePlacementReferences.cs:9-16`, `NativePlacementEffects.cs`, `NativeTerrainCommands.cs`, `EditorHistory.cs:27-112`.
- Modify: `KhaozEngine.MapEdit.Tool/MapEditSession.cs:23-93` and open/replacement points, `NativePlacementBoundsProvider.cs:29-60`, `Tools/McpBootstrap.cs:15-35`, MapDoc/MapEditor/MapEdit.Tool README. MapEditSession becomes partial without changing public signatures.
- Test: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabEditTests.cs`, `MapPrefabExtractionTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativePrefabCommandTests.cs`, `NativePrefabAssetCommandTests.cs`, `PrefabEditorFixtures.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/PrefabServiceTests.cs`, `PrefabWireTests.cs`.

**Interfaces:**
- Consumes internal `INativeDocumentCommand.Prepare(MapDocument candidate, bool undo) -> NativeDocumentPreparation` and `NativeDocumentTransaction.Run(MapDocument document, IEditorCommand command, bool undo, MapAssetClosure? assets, MapDocRegistry? registry = null, bool localOnly = false, INativePlacementBounds? placementBounds = null)` at NativeDocumentTransaction.cs:15-33.
- Consumes `EditorHistory.Execute(MapDocument doc, IEditorCommand command)`, `Undo(MapDocument doc) -> bool`, `Redo(MapDocument doc) -> bool`, `BindNativeAssets(MapDocument document, MapAssetClosure assets, INativePlacementBounds? placementBounds)` at :79, :89, :102, :32.
- Consumes `INativePlacementBounds.Bounds(MapDocument document, MapAssetClosure assets, MapDocRegistry registry, IReadOnlyList<string> placementIds) -> NativePlacementExtent` at NativePlacementEffects.cs:29, `MapEditSession.Mutate<T>(Func<MapDocument, MapDocRegistry, T> fn, bool worldChanged)` at :526, `ApplyNativeAssets(IEditorCommand command, MapAssetClosure assets)` at :78, and WaterApply at MutationService.Water.cs:13.
- Produces abstract `MapPrefabEdit(string Label)` and sealed edits `MapPlacePrefab(string InstanceId, string ResourceId, MapTransform Transform, MapRecordRef ParentSpace, bool NumericLeaves = true)`, `MapMovePrefab(string InstanceId, MapTransform Transform)`, `MapSetPrefabOverride(string InstanceId, MapPrefabOverride Override)`, `MapRestorePrefabChild(string InstanceId, MapPrefabChildKey Child)`, `MapRemovePrefab(string InstanceId)`, `MapUnpackPrefab(string InstanceId)`, `MapUpdatePrefabAsset(string ResourceId, string OldSha256, string NewResourceId, string NewSha256)`.
- Produces `MapPrefabEditResult(MapDocument After, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects)`, `MapPrefabEdits.Prepare(MapDocument document, MapAssetClosure assets, MapPrefabEdit edit) -> MapPrefabEditResult`, pure. Bounds consequences are completed by the injected editor seam on validated before/after snapshots.
- Produces `MapInteriorEdit(string Label)` and sealed `MapSetInterior(MapPrismDomain Domain)`, `MapRemoveInterior(string SpaceId)`, `MapSetRoofLink(MapRoofLinkDoc Link)`, `MapSetRoofGroup(MapRoofGroupDoc Group)`, and `MapInteriorEdits.Prepare(MapDocument document, MapAssetClosure assets, MapInteriorEdit edit) -> MapPrefabEditResult`.
- Produces `MapPrefabExtraction.Extract(MapDocument document, MapAssetClosure assets, IReadOnlyList<MapPrefabReference> selected, MapTransform origin, string assetId) -> MapPrefabAsset`. Partial required record/floor/roof selection refuses. External parent/paint targets remain explicit typed world references.
- Produces `MapPrefabAssetEdits.Replace(MapPrefabAsset asset, MapPrefabChild child, bool remove = false) -> MapPrefabAsset`, pure and strict about existing/add/remove keys. Produces `MapPrefabLocalSession(MapPrefabAsset asset, MapAssetClosure assets, MapDocument authoringContext, MapRecordRef parentSpace)`, `Apply(IEditorCommand command) -> MapNativeEditEffects`, `Undo() -> bool`, `Redo() -> bool`, `Export() -> MapPrefabAsset`, `Cancel() -> void`, using EditorDocument/EditorHistory without a window.
- Produces `PrefabEditCommand(MapPrefabEdit edit)`, `InteriorEditCommand(MapInteriorEdit edit)`, both EditorCommand and INativeDocumentCommand, and `PrefabAssetEditCommand(MapPrefabEdit edit, MapAssetClosure before, MapAssetClosure after)`. New internal `INativeAssetClosureCommand.Closure(bool undo) -> MapAssetClosure` supplies the correct closure for undo/redo.
- Produces `MapNativeWriteSet.Prefabs: bool`, `BuildingIntent: bool`, `NativeResourceTransforms: bool`, `NumericReservations: bool` as init properties, and effects `InstanceIds, ChildIds, RoofIds, LightIds, MarkerIds: IReadOnlyList<string>`, omitted in Describe when empty. Existing positional constructors remain unchanged.
- Produces `MapPrefabMutationResult(MutationResult Mutation, MapNativeEditEffects Effects)`, `MutationService.PrefabApply(MapPrefabEdit edit)` and `InteriorApply(MapInteriorEdit edit)`, returning that result under the native mutation lock.
- Produces `MapPrefabAssetInfo(string ResourceId, string Sha256, int PayloadVersion, int Children)`, `MapPrefabPublishResult(MapAssetRef Resource, MapPrefabMutationResult? Mutation)`, and `PrefabService(MapEditSession session)` with `List() -> IReadOnlyList<MapPrefabAssetInfo>`, `Open(string resourceId, MapRecordRef? parentSpace = null) -> MapPrefabAsset`, `Edit(MapPrefabChild child, bool remove = false) -> MapPrefabAsset`, `Cancel() -> void`, `Save(string resourceId, string relativeDirectory, bool updateOpenMap = true) -> MapPrefabPublishResult`, `Extract(IReadOnlyList<MapPrefabReference> selected, MapTransform origin, string assetId) -> MapPrefabAsset`. A missing parent may use an already-bound referring instance's parent in ordinal instance order. An unused asset requires an explicit parent, never an inferred exterior.
- Produces `IMapPrefabPublicationFiles.PublishNew(string path, ReadOnlyMemory<byte> bytes) -> void` and `Checkpoint(string name) -> void` in PrefabAssetPublisher.cs. Produces `PrefabAssetPublisher(MapEditSession session, IMapPrefabPublicationFiles? files = null)` and `Publish(MapPrefabAsset asset, string resourceId, string relativeDirectory, bool updateOpenMap) -> MapPrefabPublishResult`. The default seam exclusively publishes absent files or verifies identical existing bytes, with no-op checkpoints. It uses digest-addressed paths under the guarded session resource root.
- Produces `MapEditSession.UndoNative() -> MapNativeEditEffects?` and `RedoNative() -> MapNativeEditEffects?`, delegating to one EditorHistory stack, not reimplementing commands.

- [ ] **Step 1: Write failing pure-edit, command and wire tests.**

```csharp
[Fact] public void PrefabPlace_UndoRedoRestoresAllocation()
{
    var f = PrefabFixtures.Create("empty-map");
    var history = PrefabEditorFixtures.History(f.Document, f.Assets);
    var command = new PrefabEditCommand(new MapPlacePrefab("new", "hut",
        new MapTransform(new(10.25f, 5, -3.125f), 0.371f, 1.2f), PrefabFixtures.ParentRef(f)));
    history.Execute(f.Document, command);
    var accepted = PrefabFixtures.LiveBindings(f.Document).ToArray();
    long mark = f.Document.NumericIdHighWaterMark;
    Assert.True(history.Undo(f.Document));
    Assert.Equal(mark, f.Document.NumericIdHighWaterMark);
    Assert.True(history.Redo(f.Document));
    Assert.Equal(accepted, PrefabFixtures.LiveBindings(f.Document));
    Assert.Equal(mark, f.Document.NumericIdHighWaterMark);
}
[Fact] public void Unpack_PreservesAllTypedReferencesAndIds()
{
    var f = PrefabFixtures.Create("all-kinds");
    var before = PrefabFixtures.Effective(f);
    var result = MapPrefabEdits.Prepare(f.Document, f.Assets, new MapUnpackPrefab("a"));
    var after = MapPrefabResolver.Expand(result.After, f.Assets);
    Assert.Equal(before.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)),
        after.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)));
    Assert.Equal(before.Water!.Digest(), after.Water!.Digest());
    Assert.Equal(before.BuildingIntent.Digest(), after.BuildingIntent.Digest());
    Assert.Equal(before.Surfaces.AllRecords().Select(x => x.Id).Order(),
        after.Surfaces.AllRecords().Select(x => x.Id).Order());
    Assert.Equal(before.Placements.Count, after.Placements.Count);
}
[Fact] public void RejectedOrCancelledEdit_LeavesHistoryIdentityAndDirtyUntouched()
{
    var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
    var before = MapDocumentFile.SaveText(PrefabEditorFixtures.Document(session));
    bool dirty = session.IsDirty;
    var service = new PrefabService(session);
    service.Open("hut");
    service.Cancel();
    Assert.Equal(before, MapDocumentFile.SaveText(PrefabEditorFixtures.Document(session)));
    Assert.Equal(dirty, session.IsDirty);
    Assert.Throws<MapDocumentException>(() => new MutationService(session).PrefabApply(
        new MapSetPrefabOverride("a", new MapPrefabRemoveOverride(new(MapPrefabChildKind.Record, "missing")))));
    Assert.Equal(before, MapDocumentFile.SaveText(PrefabEditorFixtures.Document(session)));
}
```

Declare `ParentRef(PrefabFixture fixture) -> MapRecordRef` in the shared fixture. Add `AssetPublicationFailure_LeavesSessionAndOtherMapsUntouched` at checkpoints before bytes, after prefab bytes, after manifest bytes, before root switch and validation rejection. Assert unchanged root pin, bindings, dirty flag, history/event counts, identity and the second unopened map's bytes. Successful save updates two open-map instances in one command and leaves that other map pinned.

Add `FloorReseat_InvalidatesEveryChangedNeighborTile` using Task 5's reachable #1348 fixture. Compare pre/post native resolved placements and nav Partition dictionaries. Require at least one changed tile outside the edited patch, and every changed/deleted tile in effects-driven AffectedTiles. Fix the reachable cause in the native transaction's before/after resolved-placement diff, including alias-backed support, rather than forcing a full rebuild. Unknown shapes alone still yield Unbounded.

The two Review Focus 5 tests include these direct assertions, with helper return types pinned here:

```csharp
[Theory]
[InlineData("before-bytes")] [InlineData("after-prefab")]
[InlineData("after-manifest")] [InlineData("before-root-switch")]
public void AssetPublicationFailure_LeavesSessionAndOtherMapsUntouched(string checkpoint)
{
    var f = PrefabFixtures.Create("two-instances");
    var session = PrefabEditorFixtures.Session(f);
    string before = MapDocumentFile.SaveText(PrefabEditorFixtures.Document(session));
    bool dirty = session.IsDirty;
    var publisher = PrefabEditorFixtures.FaultPublisher(session, checkpoint);
    var asset = MapPrefabFile.Read(f.Assets.GetResource("hut").Bytes, "hut");
    var marker = (MapPrefabMarkerChild)asset.Children.Single(c => c.Key.Kind == MapPrefabChildKind.Marker);
    var changed = MapPrefabAssetEdits.Replace(asset, marker with { Value = marker.Value with { Position = new(0, 2, 0) } });
    Assert.Throws<MapDocumentException>(() => publisher.Publish(changed, "hut", "prefabs", true));
    Assert.Equal(before, MapDocumentFile.SaveText(PrefabEditorFixtures.Document(session)));
    Assert.Equal(dirty, session.IsDirty);
    Assert.Equal(PrefabEditorFixtures.OtherMapBefore(session), PrefabEditorFixtures.OtherMapNow(session));
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

Declare `PrefabEditorFixtures.FaultPublisher(MapEditSession session, string checkpoint) -> PrefabAssetPublisher`, `OtherMapBefore/OtherMapNow(MapEditSession session) -> string` and `SupportReseat() -> (PrefabFixture Fixture, MapTerrainEdit Edit)`. The other map is a separately saved, unopened fixture map. SupportReseat raises a world floor by 0.5 m at storage tile (0,0), reseating a 150 m leaf across 64 m tile boundaries. These helpers construct inputs and expose bytes, never compute expected invalidation through the implementation being tested.

Add `EveryChildKind_RemoveRestoreUndoReloadKeepsId`, `UnpackUndo_ReclaimsTransferredBindings`, `Extract_RefusesPartialRoomOrDanglingRoof`, `LocalWaterEdit_UsesTheNativeWaterCommand`, `AssetUndoRedo_RebindsMatchingClosure`, `OtherNativeEdits_PreservePrefabAndIntentRoots`, and `GuiServiceAndWire_HaveIdenticalDocumentsEffectsAndRejection`. Test every wire verb below, declared units and int64 strings. A zero-match tool filter is a failure.

- [ ] **Step 2: Run red** `wa_test t9-red-doc "$MAPDOC" "FullyQualifiedName~MapPrefabEditTests|FullyQualifiedName~MapPrefabExtractionTests"`, then `wa_test t9-red "$EDITOR" "FullyQualifiedName~NativePrefabCommandTests|FullyQualifiedName~NativePrefabAssetCommandTests|FullyQualifiedName~PrefabServiceTests|FullyQualifiedName~PrefabWireTests"`. Expect absent commands/verbs or missing snapshot/publication fields, not invalid fixture enclosure.
- [ ] **Step 3: Implement pure preparation, focused commands and root publication.**

The planner accepts the closure, so it enumerates explicit children and asset support-resource children identically to expansion. Place requires a typed authored ParentSpace. Persist it on the instance. Local room parents may use the reserved typed MapRecordRef ID `$parent`, which the visitor substitutes with that ParentSpace. The reserved reference is permitted only inside prefab bytes, never an effective/world record. Nested local rooms can instead name a local room parent. No lookup by tags or implicit exterior is allowed.

Preparation deep-copies the complete input and creates a named write set. Accepted place/new-child/asset-update preparations allocate once. Removed instances remain as root tombstones with init-only `MapPrefabInstance.Removed: bool`, default false, retaining every binding. Undo of first placement tombstones it instead of lowering the mark. Redo replays the accepted IDs. Unpack materializes each effective child once, transfers live identities to standalone records and retains removed reservations. Undo restores the instance and removes standalone transfers. Non-grid transformed local geometry remains a persisted effective-transform binding on standalone records after unpack, never a rounded world lattice or a procedural recreation.

Snapshot clone, whole publication and PublishWriteSet carry Prefabs, BuildingIntent, numeric reservations and standalone effective transforms. A prefab command cannot leak unlisted candidate writes. Run coverage and complete effective water containment on every accepted publication that changes these roots, plus R4's complete water check on every water-document publication. Compute old/new resolved leaves, including support-bound placements reseated by terrain changes. Add named instance/child/space/water/roof/light/marker effects, all relevant flags and bounds. Missing geometry reports unknown bounds with Unbounded, never a repair/deletion refusal solely for sizing.

NativePlacementReferences.Remap delegates to the pure typed visitor and publishes the remapped candidate. Keep NativePlacementEffects' existing seam and extend it to effective leaf/support changes. MapEditor gains no physics reference. NativePlacementBoundsProvider in the tool resolves requested effective placement IDs. History detects INativeAssetClosureCommand, validates with its matching closure and rebinds only after acceptance. Failed preparation/validation leaves command retry state, history, dirty state and events unchanged.

- [ ] **Step 4: Implement recoverable asset/local editing and thin wire adapters.**

Open creates a detached local asset editing context with the open map's explicit parent/reference closure. Existing placement, TerrainEditCommand and WaterEditCommand edit that context in local space. Child/marker/light edits use the same typed replacement preparation. Local undo/redo replays accepted values. Cancel discards the context without world history or file writes. A file-only Save publishes a verified immutable version with no world undo entry.

Publish canonical prefab bytes to an absent digest-addressed file with resource ID `prefab/<escaped-logical-asset-id>/<sha256>`, then publish the replacement manifest to an absent digest-addressed file. Retain shared leaf resource declarations without duplicate IDs across roots. Verify the new closure before preparing every instance referring to the old resource ID, including tombstoned instances whose reservations must survive. MapUpdatePrefabAsset switches both ResourceId and ResourceSha256. Accept one roots/instances/bindings/closure switch through PrefabAssetEditCommand. Existing files with different bytes refuse. Failures never overwrite old resources or other maps. Unreachable immutable files can remain after rejection and are not deleted automatically. Reopening an old map loads its pinned old digest. Save of the map remains the existing atomic document save.

PrefabService executes commands through the session's bound EditorHistory. Session replacement resets its stack and native edits use the same history as GUI commands. UndoNative/RedoNative return effects and preserve closure, dirty/event semantics. Add service registration and tool registration through McpBootstrap, not a second command path. No GUI host or renderer is created.

| Wire verb | Exact adapter signature, parameters in metres/radians | Service/command |
| --- | --- | --- |
| prefab_open | `PrefabOpen(string resourceId, string? parentRefJson = null) -> MapPrefabAsset` | Open with an explicit or already-bound parent |
| prefab_edit | `PrefabEdit(string childJson, bool remove = false) -> MapPrefabAsset` | Strict closed child JSON to Edit |
| prefab_save | `PrefabSave(string resourceId, string relativeDirectory, bool updateOpenMap = true) -> MapPrefabPublishResult` | Save |
| prefab_extract | `PrefabExtract(string selectedJson, float x, float y, float z, float yaw, float scale, string assetId) -> MapPrefabAsset` | Typed selected refs to Extract |
| prefab_place | `PrefabPlace(string instanceId, string resourceId, string parentRefJson, float x, float y, float z, float yaw, float scale, bool numericLeaves = true) -> MapPrefabMutationResult` | MapPlacePrefab |
| prefab_list | `PrefabList() -> IReadOnlyList<MapPrefabAssetInfo>` | List |
| prefab_override | `PrefabOverride(string instanceId, string overrideJson) -> MapPrefabMutationResult` | Strict override codec to MapSetPrefabOverride |
| prefab_unpack | `PrefabUnpack(string instanceId) -> MapPrefabMutationResult` | MapUnpackPrefab |
| interior_volume_add/set | `InteriorVolumeAdd(string domainJson)`, `InteriorVolumeSet(string domainJson)`, both returning MapPrefabMutationResult | MapSetInterior, add refuses existing ID and set refuses absent ID |
| interior_volume_remove | `InteriorVolumeRemove(string spaceId) -> MapPrefabMutationResult` | MapRemoveInterior |
| roof_link_set | `RoofLinkSet(string linkJson) -> MapPrefabMutationResult` | MapSetRoofLink |
| prefab_undo/redo | `PrefabUndo() -> MapNativeEditEffects?`, `PrefabRedo() -> MapNativeEditEffects?` | Session native history, labelled as whole native history |

Tools use ToolGuard.Guard and explicit raw diagnostics. JSON parsing is closed through MapPrefabJson, adding `ReadChild(string json) -> MapPrefabChild`, `ReadOverride(string json) -> MapPrefabOverride` and typed reference/domain/link readers. No arbitrary JSON string replacement or numeric-double IDs. Optional snap is an author-time transform choice, not runtime seating. R10 keeps responsibility for the rest of the verb map.

- [ ] **Step 5: Run green** with both Step 2 filters as `t9-green-doc` and `t9-green`, then `wa_test t9-regress "$EDITOR" "FullyQualifiedName~NativeDocumentTransaction|FullyQualifiedName~NativeWater|FullyQualifiedName~NativePlacement|FullyQualifiedName~NativeTerrain|FullyQualifiedName~NativeDocumentService"`. Expect identical GUI/service/wire outcomes and atomic rejection/undo/redo.
- [ ] **Step 6: Format, guards and commit these explicit paths.**

```bash
wa_r5_t9_paths=(
    KhaozEngine.MapDoc/Editing/MapPrefabEdits.cs
    KhaozEngine.MapDoc/Editing/MapPrefabEditPreparation.cs
    KhaozEngine.MapDoc/Editing/MapInteriorEdits.cs
    KhaozEngine.MapDoc/Editing/MapPrefabExtraction.cs
    KhaozEngine.MapDoc/Editing/MapPrefabAssetEdits.cs
    KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs
    KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabRecords.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapDoc/mapdoc.schema.json
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapEditor/NativePrefabCommands.cs
    KhaozEngine.MapEditor/NativeInteriorCommands.cs
    KhaozEngine.MapEditor/NativePrefabAssetCommands.cs
    KhaozEngine.MapEditor/MapPrefabLocalSession.cs
    KhaozEngine.MapEditor/NativeDocumentSnapshot.cs
    KhaozEngine.MapEditor/NativeDocumentTransaction.cs
    KhaozEngine.MapEditor/NativePlacementReferences.cs
    KhaozEngine.MapEditor/NativePlacementEffects.cs
    KhaozEngine.MapEditor/NativeTerrainCommands.cs
    KhaozEngine.MapEditor/EditorHistory.cs
    KhaozEngine.MapEditor/README.md
    KhaozEngine.MapEdit.Tool/PrefabService.cs
    KhaozEngine.MapEdit.Tool/PrefabAssetPublisher.cs
    KhaozEngine.MapEdit.Tool/MapEditSession.NativeHistory.cs
    KhaozEngine.MapEdit.Tool/MutationService.Prefabs.cs
    KhaozEngine.MapEdit.Tool/MapEditSession.cs
    KhaozEngine.MapEdit.Tool/NativePlacementBoundsProvider.cs
    KhaozEngine.MapEdit.Tool/README.md
    KhaozEngine.MapEdit.Tool/Tools/PrefabTools.cs
    KhaozEngine.MapEdit.Tool/Tools/InteriorTools.cs
    KhaozEngine.MapEdit.Tool/Tools/McpBootstrap.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabEditTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabExtractionTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativePrefabCommandTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativePrefabAssetCommandTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/PrefabEditorFixtures.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/PrefabServiceTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/PrefabWireTests.cs
)
wa_finish t9 "${wa_r5_t9_paths[@]}" &&
git add -- "${wa_r5_t9_paths[@]}" &&
git commit --only -m "feat(mapeditor): edit and publish immutable prefabs through native commands" -- "${wa_r5_t9_paths[@]}"
```

### Task 10: Public synthetic village and independent heads

**Files:**
- Create: `KhaozEngine.MapDoc.Tests/Prefabs/PrefabVillageFixtures.cs`, `MapPrefabVillageTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPrefabVillageAcceptanceTests.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/PrefabVillageWorkflowTests.cs`.
- Modify: `KhaozEngine.MapDoc/README.md`, `KhaozEngine.MapDoc.Physics/README.md`, `KhaozEngine.MapEditor/README.md`, `KhaozEngine.MapEdit.Tool/README.md`, `docs/DEPENDENCY-SEAMS.md:863-889`.
- Create test: `KhaozEngine.Tests/ArchitectureTests.Prefabs.cs`, a partial ArchitectureTests class containing the prefab runtime dependency non-edge assertion. Run the existing `KhaozEngine.MapEditor.Tests/MapDoc/NativeManifestAdapterTests.cs` as a regression without modifying it.

**Interfaces:**
- Consumes Tasks 1 to 9, including actual tool commands, effective view and builder. No TileWorld oracle or game input is used.
- Produces `PrefabFixtures.Village() -> PrefabFixture`: three invented definitions (hut, tower, bath), four instances (two huts, tower, bath) and one unlinked standalone canopy. Two storeys, cross-instance roof group, optional local water, one explicit extra-furniture override, an unpainted notch, support-resource floor, light and marker fields all participate.
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

- [ ] **Step 2: Run red** `wa_test t10-red-doc "$MAPDOC" "FullyQualifiedName~MapPrefabVillageTests"`, then `wa_test t10-red "$MP" "FullyQualifiedName~MapPrefabVillageAcceptanceTests"` and `wa_test t10-red-editor "$EDITOR" "FullyQualifiedName~PrefabVillageWorkflowTests"`. A new test that already passes records that fact, then the named missing interaction scenario must fail before any fix. Never alter an expected result to manufacture red.
- [ ] **Step 3: Build the synthetic fixture, close only cross-task contract gaps and document consumption.**

Exercise public resources from a fresh byte source and separately loaded documents, not shared mutable snapshots. Fix reachable R5 interface failures in their owning modules with a focused regression. Preserve private-data exclusion and no new runtime dependency edges. Document format-7 identity movement, strict closure, local-support ownership, partial paint, room parenting, explicit AuthoredOpen doors, bound query completeness, roof flags, marker/light payloads and typed command responses. R6 to R11 handoffs name every remaining proof.

- [ ] **Step 4: Run green** with all Step 2 filters as `t10-green-doc`, `t10-green`, `t10-green-editor`, then `wa_test t10-architecture "$RUMP" "FullyQualifiedName~ArchitectureTests"`. Expect identical independent heads and native dependency non-edges. No complete navigation route or pixels are claimed.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t10_paths=(
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
wa_finish t10 "${wa_r5_t10_paths[@]}" &&
git add -- "${wa_r5_t10_paths[@]}" &&
git commit --only -m "test(mapdoc): prove free prefab village on both headless heads" -- "${wa_r5_t10_paths[@]}"
```

### Task 11: Private shipped-building comparison with complete input closure

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

- [ ] **Step 2: Run synthetic red only** `wa_test t11-red "$ORACLE" "FullyQualifiedName~ShippedBuildingInputGuardTests"`. Expect missing comparison/closure checks. The implementer never runs the shipped test or fetches private content.
- [ ] **Step 3: Implement the complete comparison inside the private boundary.**

Use the checked membership manifest, never a nominal restamp or guessed envelope grouping. Convert only the comparison's selected building data in test code, preserving each original numeric leaf once and recording new parents separately. Reuse R2's linked LegacyOracleConverter for floor data and the R5 native asset/instance APIs for free building representation. Refuse ambiguous/unassigned membership and incomplete reference closure. This is not a production importer.

Compare every leaf's effective transform, ordered tags, kind/asset, original mesh bytes and metadata, physical collider, any authored selection geometry and original numeric ID. Revised-T4 derived envelopes compare between native heads. They need not equal legacy visual-AABB picking. Source query deltas are recorded privately for R11 named acceptance, never called lossless. Compare furniture differences, unset notch paint versus world material, room/storey cell-centre membership, each roof transform/link and all lights. Exact boundary changes implied by OA33's one-owner policy are reported separately and still await R11 import acceptance. Unexplained centre/membership differences fail.

Imported derived vertices use the spec's fixed 0.00001 m tolerance and normals 0.000001 per component. Integers, tags and preserved identities compare exactly. Emit every exceeded value privately, never widen tolerances. Use the released source collider builder for source physical geometry, not another TileWorld collision implementation.

For every checked doorway, compare the source opening and native compound shell clearance, including jamb blocking. This proves physical apertures, not native walking. For every checked room/edge/storey probe compare selected occupied owner and connected concealment intent. Verify hidden colour false/caster true/boom false/sky true, unchanged statics and visible unlinked shelter. Reload and two independent heads must preserve the same effective leaf records.

All body failures use report.Check/Record, never Assert, console or ITestOutputHelper on private values. Detailed source IDs, geometry, paths, inventory, provenance and reports stay in the private extraction/report area. The public repository/CI/logs receive only sanitized pass/fail and the private record reference. Missing private input fails acceptance and never skips. Preserve the existing four environment variables and the 0700 directory/0600 exclusive report rules.

- [ ] **Step 4: Run synthetic green** `wa_test t11-green "$ORACLE" "FullyQualifiedName~ShippedBuildingInputGuardTests|FullyQualifiedName~PrivateOracleGuardTests"`. Expect all synthetic guards passing, missing input still refused and no secret marker in public failure text.
- [ ] **Step 5: Format, guards and commit these explicit paths.**

```bash
wa_r5_t11_paths=(
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedBuildingInputs.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedBuildingComparison.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedBuildingComparisonTests.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedBuildingInputGuardTests.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj
    KhaozEngine.MapDoc.OracleHarness.Tests/README.md
    KhaozEngine.Tests/ArchitectureTests.MapDoc.cs
)
wa_finish t11 "${wa_r5_t11_paths[@]}" &&
git add -- "${wa_r5_t11_paths[@]}" &&
git commit --only -m "test(mapdoc): compare shipped prefab buildings through the private oracle" -- "${wa_r5_t11_paths[@]}"
```

## Self-review and execution handoff

Writer self-review mapped all 19 gate decisions and the three OA9 proof names to tasks, checked borrowed source signatures against 06b734c57 and pinned all new cross-task interfaces. It also corrected RayHit/MapFaceKey provenance usage, linked-fixture visibility, exact boundary query inputs, immutable version IDs, unpack transform persistence and the arithmetic rounding policy. No gate item was omitted. Compilation, test execution and runtime claims remain execution evidence, not evidence from this document-writing turn.

- [ ] Check OA33 Decisions 1 to 19 against Tasks 1 to 11. Format/closure is Task 1, bindings/identity Task 2, transforms/completeness Task 3, rooms Task 4, local support/doors/bounds Task 5, paint Task 6, full water Task 7, roof/light/marker intent Task 8, edits/tools Task 9, synthetic acceptance Task 10, private acceptance Task 11 and release below.
- [ ] Check every consumed signature against 06b734c57 and every new type/property against its producing task. Carry interface adjustments into later briefs before dispatch.
- [ ] Check every test fixture is valid for coverage and containment before interpreting red, each failure is falsifiable, each focused filter matches tests and all five Review Focus concerns have named assertions.
- [ ] Check all source paths, old line ranges, constructor overload preservation, 800-line caps, linked fixture boundaries and explicit commit path lists. No baselined-file growth is approved.
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

6. Controller-only private comparison. Freeze current game main into a new attested extraction with the complete Task 11 closure. Set the four variables to that extraction, its private provenance/digest and an absent report path under an existing 0700 directory outside git. Run once:

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
