# World Authoring R6: General Markers and Spawn Projections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend R5's single marker store into the native named-point contract, with explicit height choices, lossless spawn adoption and identical read and edit behavior on both heads.

**Architecture:** Keep world markers in `BuildingIntent.Markers` and local markers in prefab children. Marker payload 2 adds a closed height mode, an optional kind-shaped support binding and an optional archetype without rewriting payload 1. Resolve named support after effective geometry exists, publish value-only provenance on effective markers and witness-bearing support results on a separate channel, expose immutable lookup and projections over the resulting points, and publish edits through the existing closure-bound transaction with one shared before and after marker diff. Format 8 is a version-only migration. Legacy spawn adoption is a separate explicit edit.

**Tech Stack:** C#, System.Numerics, System.Text.Json, JSON Schema, xUnit, MapDoc exact points and scoped support/membership queries, MapEditor transactions and ke-mapedit MCP. No new package or third-party library.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), C6, C9, DG9.1, DG9.4 and the marker rows of the R10 verb map. The controller supplies the spec from the program branch if it is absent from this execution branch. Read [R5's executed plan](2026-10-12-world-authoring-r5-free-buildings-prefabs-interiors.md) for the released ownership and transaction contracts.

Binding private inputs are the Grimhollow program's `R6-OWNER-GATE.md`, `DECISIONS.md` OA36, `R5-LEDGER.md` and `R5-BUILDING-ORACLE.md`. The controller supplies them in briefs, never copies private content into this public repository. Research inputs are `.superpowers/sdd/2026-10-16-world-authoring-r6-refresh/research-engine.md` and `research-program.md`. The gate overrides their conflicting import-height proposals and the superseded 2026-10-05 R6 draft. Plan reviews 1 and 2 and their adjudications are applied in this revision.

## Approval and execution gate

Written against engine `1a4182c24da4584000a1c963fee1a2f8ccd014dd`, with v21.0.0 released, 21.1.0 staged, MapDoc format 7 and R1 to R5 released. OA36 approves planning, including all six decisions and smaller decisions 7 to 20. Independent plan reviews and owner approval of the reviewed plan precede code. This file approves no implementation, tag, game adoption or R11 acceptance.

The writer changes and commits this file only. The execution commands below are instructions for approved future execution. Workers stop at verified task commits. The controller owns reconciliation, pushes, integration, private evidence and the engine-rooted release handoff.

## Global Constraints

- OA36 Decision 1. One editable store, `BuildingIntent.Markers` and prefab Marker children, marker payloads 1 and 2, prefab payload 1, format 8, resolver identity (1, 2), `AuthoredBindingsV2`.
- Payload 1. Preserve constructor, canonical bytes and SHA-256 of released prefab assets. Omit every absent new member. Payload-1 effective markers carry no provenance and the payload-1 annotation digest is unchanged under `kemap/effective-annotations/1`. Do not republish immutable prefab resources to accommodate R6.
- Migration. Pure sequential 7 to 8 version-only JSON step, with no source, terrain, closure, registry or role access. Every native identity and analytic manifest hash moves once. Analytic execution stays unchanged. Future format 9 refuses with update guidance.
- OA36 Decision 2 and OA9. Payload 2 requires a closed `heightMode`, Explicit or Support. Explicit keeps `Position.Y` as the sole height authority, as payload 1 does. Support requires `supportBinding` and `Position.Y` exactly +0, the one canonical encoding. Never default a height mode, surface owner, reference height or search range.
- Marker bindings. A Surface binding writes only `kind` and `surfaceId`. A Space binding writes only `kind`, `spaceId`, finite `referenceY` and finite nonnegative bounded `searchBelow` and `searchAbove`. Absent members are omitted, explicit nulls and foreign members refuse. The placement `supportBinding` definition and its null rule stay unchanged.
- Height. Resolve through released R2 support on both heads after effective support installation and after the placement-seat context rebuild. Only Supported succeeds. A bound marker's height is the shared support query's float `WorldY`, the same value that seats support-bound placements, and its effective Y is the exact value of that float. No new support-query overload. A removed bound support refuses the edit. Explicit heights never resample. Prefab-local and framed standalone bindings are stored frame-local and pass through `WorldRequest` exactly once at expansion. `RemapWorld` renames a marker binding's typed world owner and nothing else, and load or build never changes owner IDs or binding bytes.
- OA36 Decision 3. Effective names are ordinal, case-sensitive, globally unique, nonempty and editable. IDs are immutable. Repeated prefab names need explicit replace overrides, never suffixing. Rename rows carry old and new names and come from one shared marker diff for every native transaction.
- OA36 Decisions 7 to 9. Derive occupied space and parent chain by point membership, with explicit Outside when no space is authored. Roles are caller vocabulary. Enabled views spawn, exhaustive views validate and preserve persistence keys.
- OA36 Decision 4. Analytic `MapSpawn` and `MapPlayerSpawn` remain unchanged. Native old lists still load. No native path adds new legacy records, and the legacy-list check runs again immediately before every publication, including undo and redo. One explicit undoable adoption clears both lists after every record has a destination ID and height choice. Resolver (1, 1) native documents refuse marker and adoption verbs and name resolver adoption as the prerequisite.
- OA36 Decisions 5, 10 and 11. Private import points use the game role manifest's standing point. Record source tiles. Imported markers bind to their source support. Bound homes follow game habitat policy, and habitat and spawn selection remain game policy.
- OA36 Decisions 12 and 13. Marker-only spatial edits add Residency = 16 only. Metadata-only edits add no invalidation. Never add Physics = 2, Nav = 4 or Unbounded = 64 for a marker. Report marker IDs and old/new point boxes.
- OA36 Decisions 14 to 16. Half-open marker rectangles include minima and exclude maxima, including negative coordinates. Native starts use a caller role through a pure metadata selector. Native readouts use authored support, never analytic terrain. Unadopted native spawn readouts take a caller-supplied Surface owner only. R6 opens no windowed native session. A partial native Open keeps its released refusal, `map_summary` on native documents stays a whole-load result, and windowed native editing belongs to R9. GUI readouts keep the released billboard lift 1.0 and focus lift 0.75.
- R5 W-R1 and F-R9. Every wire class joins non-parallel `McpWireCollection`. Each MCP request has its own 60 s timeout. Compare yaw by `Math.IEEERemainder(a - b, 2 * Math.PI)`, tolerance 0.000001 radians, without normalizing storage.
- Bounds. Keep MapQueryLimits defaults 256 patches, 4096 faces, 64 support intersections, 32 page reads, 64 record reads and depth 8. Keep R5 expansion limits and precision. Overflow refuses, never truncates or widens limits to pass a fixture.
- R5 F-R12 to F-R14. Broad phases enumerate nearby candidates before exact work. Query points use the acquired frame or QueryExact. Preserve bounded counters and existing paint/coverage results on real content.
- Dependencies. MapDoc remains physics, render, GPU, editor, TileWorld and game free. MapEditor gains no MapDoc.Physics reference. Only the private harness gains TileWorld.Netcode, as its sixth direct engine reference.
- Test placement. Payload, identity, adoption and membership in MapDoc.Tests, independent heads and static installation in MapDoc.Physics.Tests, commands/service/wire in MapEditor.Tests. The private harness stays nonpackable, unreferenced, outside the solution and public CI.
- Verification. Every build, test, format, bake or pack uses build-slot. Full MapDoc, MapDoc.Physics and MapEditor projects once after each accepted task, full solution Release build and suite once at the reconciled candidate. No stress loops, GPU checks or consumer windows.
- Repository. Explicit-path commits, zero warnings, no blanket suppression, new C# files at most 800 lines. Baselined files may shrink, never grow without owner approval. `ViewportWorld.cs` (730 lines) stays at most 800. No em or en dash glyphs or Markdown prose semicolons.
- Input and text. AppWindow alone reads raw devices. GUI paths use InputState, InputManager and Pointer. Player text uses StringId/LocalizedText. Tool diagnostics use the explicit raw path.
- Privacy and CD2. Public invented fixtures only. Private input missing means failure, never skip. No private names, points, tags, source inventories, geometry, content digests or detailed reports enter engine git, CI or public logs.
- Release. Ride the untagged staged minor at integration, 21.1.0 if still available, otherwise the next free minor. CHANGELOG and guarded declarations travel together. A KhaozEngine-rooted thread performs the authorized release under OA25 after plan execution approval.

## Review Focus

1. A support edit changes marker height outside its named patch report, or removes support while a higher floor remains, through any native transaction kind. Task 3 tests `BoundMarkerReseat_NamesBothPointsAndRefusesLostOwner` through actual terrain, placement, prefab and interior transactions on room-free fixtures, each move and removal with a marker-absent or explicit-marker control that must succeed, preventing the #1348 class.
2. A bound local marker is unpacked, re-extracted, moved, remapped and reloaded. Task 2 tests `BoundMarkerUnpackExtract_KeepsIntentAndTransformsOnce`, including a Space-bound transferred marker at scale 1.2 with a nonzero frame Y and typed local/world owner slots with identical text, and `RemapWorld_RenamesOnlyTheActiveMarkerOwner` for standalone and transferred Surface and Space bindings. Task 5 continues the same marker through `marker_set` and undo.
3. A disabled participant collides by name with an enabled one. Task 4 tests `DisabledParticipantNameCollisions_ValidateBeforeAnyRead` in MapDoc.Tests with a counting surface source, on one disabled prefab child against one enabled world marker and the inverse, and `DuplicateMarkerNames_FailBothHeadsBeforeStaticInstall` in MapDoc.Physics.Tests on the same two cases. Renaming or removing only the disabled participant succeeds, restoring it fails again.
4. Valid native old lists have the same source ID in different kinds, or a callback changes a legacy list after staging a permitted command. Task 6 tests `SameSourceIdInTwoLists_RequiresDistinctExplicitDestinations` and `LegacyListsChangedAfterStaging_RefuseAtPublication`, plus generic mutation and element-duplicate paths that must not add native legacy records.
5. A negative-X player start is prefab-local and support-bound outside the first loaded window. Task 7 tests the pure selector `NativeStartAnchor_SelectsFromMetadataWithUnreadableBodies` against unreadable tile bodies with a whole-load failure control, `NativeWindowing_SelectsMarkerTileAndOpenKeepsRefusal` with valid bodies, and the whole-load half `NativeWholeLoadReadouts_UseNamedSupport`, including an unadopted native spawn readout with no Surface choice.

---

## Controller rulings

These rulings were proposed by the writer or the controller and adjudicated after plan reviews 1 and 2. Gate choices above are binding already. Each brief carries the rows its task consumes.

| ID | Ruling | Status | Cost if wrong |
| --- | --- | --- | --- |
| R6-P1 | Payload 2 adds a required closed `heightMode` (Explicit, Support) and an optional `supportBinding`. Explicit keeps `Position.Y` as the sole authority, exactly as payload 1 and the `MapPlacement` Y-or-binding pattern. Support requires `supportBinding` and `Position.Y` exactly +0, documented on the DTO as carrying no height. Payload 1 has neither member. `MapMarkerHeight` remains the command and adoption input type. The effective marker position always carries the resolved height | Replaced in review 1 | A consumer reading the authored DTO for Support markers sees 0 and must read effective markers. The DTO comment and typed views make that explicit |
| R6-P2 | Effective markers carry value-only `MapMarkerHeightProvenance` (mode, world binding, face, space ID, via, and the support query's float `WorldY` under R6-P14). Witness-bearing results publish separately as `MapEffectiveResources.MarkerSupports`, mirroring `PlacementSupports`. Payload-1 markers carry no provenance, null members are omitted, and the payload-1 annotation digest is pinned unchanged. Documents with any payload-2 marker digest under `kemap/effective-annotations/2` | Replaced in review 1, height wording replaced by R6-P14 | Consumers needing query completeness read the witness channel. A second digest scheme name exists for payload-2 documents |
| R6-P3 | Validate effective names by a bounded metadata-only enumeration shared with marker expansion, before support, callback or static work. Include disabled markers, exclude removed children | Kept | One shared enumeration seam is needed. A separate expansion would risk name/pose drift |
| R6-P4 | Build an immutable median-split 2D point BVH for rectangle lookup, leaf size 8, stable ID tie breaks, node bounds rounded outward from exact points as `MapEffectiveAnnotations.Outward` does. Membership runs only for returned points and preserves refusal statuses | Kept with outward rounding | More index code than a scan. A later streaming index may replace it behind the same API |
| R6-P5 | Adoption choices name source kind/ID, destination ID, name, role, height and an optional explicit yaw. Without a yaw, NPC yaw is 0 because the source has none and player yaw is copied. No default role or destination naming | Kept with optional yaw | More explicit caller input, but no policy or silent collision repair in the engine |
| R6-P6 | Window anchoring consumes root marker metadata and verified prefab bytes without support resolution, through the pure `MapMarkerAnchors` selector of R6-P13. Select enabled matching roles by squared distance to bounds centre, then ordinal ID. Height is resolved only for whole-load readouts | Kept, narrowed by R6-P13 | Window opening has a separate bounded metadata stage. It shares transforms with resolution rather than duplicating them |
| R6-P7 | Unadopted native spawn readouts require a caller-supplied Surface owner ID and sample that owner at each spawn XZ. Without one, refuse with `spawns_adopt` and `marker_set` guidance. Never invent a topmost or zero-height choice. Preserve analytic and inclusive placement-rectangle behavior | Changed in review 1 | Native callers must supply a Surface owner or adopt. A per-record Space choice requires adoption |
| R6-P8 | Bound support is validated even for disabled markers. Creation/deletion, binding changes and spatial changes carry Residency. Archetype-only changes are metadata. Same-value edits have empty effects | Kept | Disabled stale bindings cannot be parked until later repair. A different policy needs explicit validation changes |
| R6-P9 | Use 0.00001 m for private position/height comparison and 0.000001 radians for wrapped yaw. Expected inventory and category counts live solely in private provenance | Kept | A real mismatch blocks acceptance. Tolerances cannot silently expand to hide source differences |
| R6-P10 | Execute ten serial tasks with per-task review and full three-project sweeps. Task 2 resolution and Task 3 edit effects are separate gates, and Task 7 consumer queries and Task 8 GUI readouts are separate gates. Review/fix allowance and candidate/release work sit inside the 25 to 45 engineer-days | Changed in review 1 | More transitions than eight tasks, with bounded rejection and ownership |
| R6-P11 | Add optional initial keyed overrides to the prefab-place request. A second instance can supply its marker-name replace override in the same accepted placement, before uniqueness validation | Kept | One additive request property and wire option. Without it repeated marker-bearing prefabs cannot be placed through the public command path |
| R6-P12 | The annotation digest keeps `kemap/effective-annotations/1` when no effective marker has payload 2, otherwise `/2`. Payload-1 rows serialize as released in both | Confirmed after revision 2 | Two scheme names coexist. A mixed document changes its digest name when its first payload-2 marker appears |
| R6-P13 | No windowed native session in R6. A partial native Open keeps its released refusal (`NativeDocumentService.cs:56-62`) and `map_summary` on native documents stays a whole-load result. The pure `MapMarkerAnchors` selector picks the enabled start of the caller's role from root metadata and verified prefab bytes with no support reads, `MapDocumentWindowing` uses it wherever windowing applies, a whole-loaded native document focuses the editor on that start, and `map_summary` lists markers for whole-loaded native documents. Windowed native editing belongs to R9 | New in review 2 | A large native world cannot be opened for editing through the tool in R6. The anchor still chooses the GUI window and is ready for R9 |
| R6-P14 | A support-bound marker's resolved height is the shared support query's float `WorldY`, the value that seats support-bound placements. Its effective Y is the exact value of that float and provenance records the float. No new support-query overload | New in review 2 | Marker heights inherit float rounding of the support answer, identical to placements. A later exact-height need would add a shared query result for both, not a marker-only path |

## File structure

Paths below are repository-relative. New files are proposed outputs of their task, not claims about released APIs. Existing ranges refer to `1a4182c24`. Do not edit the old program worktree to execute this plan.

| Task | New homes | Released integration homes |
| --- | --- | --- |
| 1 | MapDoc `Markers/MapMarkerHeight.cs`, `MapMarkerMigration.cs`, `MapMarkerValidation.cs`, marker format fixtures, golden and tests | Prefab records/JSON/Preflight/validation, MapDocumentFile, schema, prefab format and persisted identity tests |
| 2 | MapDoc `Markers/MapMarkerExpansion.cs`, `MapMarkerResolution.cs`, `MapMarkerSpaces.cs` | Prefab resolver, reference walker and `RemapWorld`, effective annotations/resources, unpack/extraction, Physics.Tests fixture links |
| 3 | MapDoc `Editing/MapMarkerEffects.cs`, MapEditor `NativeMarkerEffects.cs` | Native edit effects, transaction effect composition, placement effect combination, room-free derivations beside the released support-resource and standalone-prism fixtures |
| 4 | MapDoc `Markers/MapMarkerRegistry.cs`, `MapMarkerSpatialIndex.cs`, `MapMarkerProjection.cs`, `MapMarkerRoles.cs` | Prefab validation and edits, prefab-place tool, released R5 village fixtures/workflow, CHANGELOG |
| 5 | MapDoc `Editing/MapMarkerEdits.cs`, MapEditor `NativeMarkerCommands.cs`, tool `MutationService.Markers.cs`, `QueryService.Markers.cs`, `Tools/MarkerTools.cs` | Native command state, query service partial, bootstrap, verb inventory, package READMEs |
| 6 | MapDoc `Markers/MapMarkerAdoption.cs`, MapEditor `NativeMarkerAdoptionCommands.cs`, `NativeLegacySpawnAdmission.cs`, tool `MutationService.MarkerAdoption.cs` | Native write set/snapshot, transaction admission and publication, session native publication, mutation adapters, marker wire tools, generic history test |
| 7 | MapDoc `Markers/MapMarkerAnchors.cs`, `MapNativePointReadout.cs`, tool `MapEditSession.Markers.cs` | Windowing, whole-load session open, query service markers partial, results, document tools. `NativeDocumentService.cs` stays unchanged |
| 8 | MapEditor `NativeMarkerReadouts.cs`, `SpawnBillboards.cs`, `MapEditorScene.Markers.cs` | Editor options, scene document/reload/window/navigation partials, viewport spawn submission, spawn picking |
| 9 | Invented `Markers/MarkerVillageFixtures.cs`, acceptance and workflow tests | Runtime architecture guard, living handoff docs |
| 10 | Private harness `ShippedMarkerInputs.cs`, `ShippedMarkerComparison.cs`, `ShippedMarkerComparisonTests.cs`, `ShippedMarkerInputGuardTests.cs`, `ShippedMarkerGuardFixtures.cs` | Harness csproj/README, architecture reference guard, release and public API docs |

## Estimate and sequencing

| Task | Deliverable | Depends on | Engineer-days |
| --- | --- | --- | --- |
| 1 | Payload 2, format 8 and byte/identity compatibility | released R1 to R5 | 3 to 5 |
| 2 | Height resolution, provenance, witness channel and membership | 1 | 3 to 5 |
| 3 | Support-edit consequences and shared rename rows for every native transaction | 2 | 2 to 4 |
| 4 | Name validation, lookup, role findings and typed views | 3 | 3 to 5 |
| 5 | Shared marker edits, commands, `marker_list` rectangle and MCP parity | 4 | 4 to 7 |
| 6 | Explicit spawn adoption, native legacy-write fence and resolver guidance | 5 | 3 to 5 |
| 7 | Consumer queries, window anchors and tool readouts | 6 | 1 to 2 |
| 8 | Editor GUI readouts | 7 | 1 to 2 |
| 9 | Public synthetic acceptance and independent heads | 8 | 2 to 4 |
| 10 | Private comparison, candidate evidence and release preparation | 9 | 3 to 6 |
| | Total, including reviews, fix allowance and integration | | 25 to 45 |

R3 took about 14 h from approval to tag, R4 about 9 h, R5 about 59 h against its earlier 12 to 24 h forecast. Forecast R6 at 22 to 48 elapsed agent hours after reviewed-plan approval, with 38 to 48 h the safer scheduling allowance. The two extra task gates add review transitions, not scope. Review 2 moves work without changing a range: R6-P13 removes the windowed summary from Task 7 while the selector and billboard seam fit inside Tasks 7 and 8, R6-P14 removes an exact-height producer from Task 2 while `RemapWorld` takes its place, and the publication recheck and fixture derivations fit inside Tasks 6 and 3. Plan reviews, owner responses, subscription capacity, private extraction delivery and release CI can extend calendar time. Engineer-days measure scope, not literal agent runtime. Reforecast after Task 3 and after the private comparison, using actual accepted task times and findings.

## Dispatch and verification conventions

The controller supplies the approved gate, relevant R5 rulings, this plan's Global Constraints, the R6 rulings the task consumes and consumed Interfaces blocks in each bounded brief. Execute serially, with a fresh review per task under the session's routing rules. Pin each execution start to the previous accepted task commit, not to today's writer base. Do not dispatch dependent work with unresolved load-bearing findings.

Run this setup only during approved execution, in bash. `wa_run` preserves the exit code and bounded output. Every build/test uses it, including the private harness. Full project sweeps, solution gates, renders, bakes and packaging use its heavy lane.

```bash
cd /Users/antonio/KhaozEngine/.worktrees/wa-r6-markers
test "$(git branch --show-current)" = "feature/wa-r6-markers"
test "$(git rev-parse HEAD)" = "$wa_r6_expected_base"
wa_r6_log_dir="/tmp/grimhollow-orch/logs/wa-r6-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p "$wa_r6_log_dir" local-feed
MAPDOC=KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj
MP=KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj
EDITOR=KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj
ORACLE=KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj
RUMP=KhaozEngine.Tests/KhaozEngine.Tests.csproj
wa_run() {
    local n=$1 rc
    shift
    local lane=()
    if [ "${1:-}" = --heavy ]
    then
        lane=(--heavy)
        shift
    fi
    if build-slot "${lane[@]}" --label "wa-r6-$n" -- "$@" >"$wa_r6_log_dir/$n.log" 2>&1
    then rc=0
    else rc=$?
    fi
    tail -25 "$wa_r6_log_dir/$n.log"
    echo "exit $rc for $n"
    return "$rc"
}
wa_test() {
    wa_run "$1" dotnet test "$2" -c Release -m:1 --logger "trx;LogFileName=$1.trx" --results-directory "$wa_r6_log_dir" --filter "$3"
}
wa_sweep() {
    local n=$1
    wa_run "$n-mapdoc" --heavy dotnet test "$MAPDOC" -c Release -m:1 &&
    wa_run "$n-physics" --heavy dotnet test "$MP" -c Release -m:1 &&
    wa_run "$n-editor" --heavy dotnet test "$EDITOR" -c Release -m:1
}
wa_finish() {
    local n=$1 path
    shift
    local cs_paths=()
    for path in "$@"
    do
        case "$path" in *.cs) cs_paths+=("$path") ;; esac
    done
    wa_run "$n-format" dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --include "${cs_paths[@]}" &&
    wa_run "$n-dashes" sh scripts/check-dashes.sh --tree &&
    wa_run "$n-prose" sh scripts/check-prose.sh --tree &&
    wa_run "$n-size" sh scripts/check-file-size.sh --tree &&
    wa_run "$n-agent" sh scripts/check-agent-instructions.sh --tree &&
    wa_run "$n-docs" bash scripts/check-doc-versions.sh &&
    git diff --check
}
```

Red must fail at a named behavioral assertion, or compilation exclusively for newly declared task-owned APIs. Invalid fixtures, missing released signatures, unrelated failures and zero test matches are not red. Every new test and every changed released test whose expected behavior changes appears in that task's red filter and fails there before implementation. A released test changed only to preserve its coverage is named in the task and recorded as a passing regression. Green requires exit 0, nonzero matching tests and zero failed tests. Record TRX counts and the exact SHA. Run each check once. Exit 75 means nothing ran, retry later. A new product change or failure justifies a new focused check. Run the three-project sweep once after the task's last fix and review, before acceptance, with no duplicate sweep if the reviewed bytes already have that evidence. The full solution suite runs only at the candidate gate.

Each task's explicit path array is its commit ownership. Add fix-round paths explicitly. Repair formatting through `wa_run` over the same C# list, then verify. Task 10 also formats the outside-solution harness. A known analyzer-reference workspace warning is not a waiver for an unloaded changed project. Check the loaded project and formatted paths as R5 did.

## Shared fixture and signature conventions

New test files use `KhaozEngine.Tests.*`. Create `KhaozEngine.MapDoc.Tests/Markers/MarkerFixtures.cs` as `internal static partial class MarkerFixtures`. Concern files end in `Fixtures.cs`. Task 2 widens the Physics.Tests link glob so these public sources compile into Physics.Tests, and the released MapEditor.Tests recursive glob already links them. Neither project references the MapDoc.Tests assembly. Linked fixtures never read files from disk, because Physics.Tests does not copy `Fixtures/**`. A test that needs a committed file reads it from its own project's output.

Task 1 creates `MarkerFixture(MapDocument Document, MapAssetClosure Assets)` and `MarkerFixtures.Create(string variant = "basic") -> MarkerFixture`, `Reload(MarkerFixture fixture, bool tiled) -> MarkerFixture`, `Options() -> MapResolveOptions`, `Exact(float x, float y, float z) -> MapExactPoint`, `FormatSevenJson(bool analytic) -> string`, `PayloadOneAsset() -> MapPrefabAsset`. Basic is a complete resolver-2 map with one standalone payload-1 point `m` built through the released positional constructor, named `arrival`, role `entry-role`, tags `["first", "second"]`, X -0.25, Position.Y 12.25 (its authored height), Z -1.5, yaw 0.371, enabled true. Variant `prefab-basic` places `PayloadOneAsset()` (resource `golden-marker`, one payload-1 Marker child key `entry`, name `arrival-a`, local point (1, 2, -1), yaw 0.371, role `entry-role`, tags `["first", "second"]`) as instance `a` at the origin with yaw 0 and scale 1, so its world ID is `a/entry`. Options uses builder `public-marker-fixture`, version 1, hash `marker-options/1`, resolver 2. The committed golden `KhaozEngine.MapDoc.Tests/Fixtures/Markers/PayloadOne.prefab.json` is `MapPrefabFile.Write(PayloadOneAsset())` captured at `1a4182c24` before any Task 1 product edit. The existing `Fixtures/**` item copies it with no csproj edit, and only MapDoc.Tests reads it.

Task 2 adds `Effective(MarkerFixture fixture) -> MapEffectiveResources`, `Stacked() -> MarkerFixture`, `Sloped() -> MarkerFixture`, `Cave() -> MarkerFixture`, `LocalCave() -> MarkerFixture`, `TransferredSpace() -> MarkerFixture`, `TransferredSurface() -> MarkerFixture`. Stacked wraps released `SupportFixtures.EightStacked()` with `SupportFixtures.Assets()`, setting resolver identity (1, 2) and `AuthoredBindingsV2` only if the released document lacks them. Sloped applies `MapTerrainEdits.Prepare(surfaces, new MapSetCornerHeights(floor2Patch, 0, 0, 3, 3, values))` to Stacked, with the nine centimetre corner heights 2000, 2007, 2013, 2003, 2010, 2017, 2006, 2011, 2019 in its z-major order, and adds a world marker `slope` Surface-bound to floor-2 at X 0.3, Z 0.7 plus a payload-2 Explicit control `flat` at Y 12.25. Cave wraps `PrefabFixtures.Cave("stacked")`. LocalCave adapts the released prefab-cave/room ownership, retaining its real bindings and typed reference slots. TransferredSpace places a prefab whose local Marker child is Space-bound, at instance frame (-12.25, -450, 7.125), yaw 0.371, scale 1.2. TransferredSurface is the same frame with the local Marker child Surface-bound to the prefab's local floor. Do not invent replacement geometry for the three OA9 proofs.

Task 3 adds `KhaozEngine.MapDoc.Tests/Markers/MarkerEffectFixtures.cs` with `SupportEdits(string variant, bool withMarker = true) -> MarkerFixture` and `RenamedAsset(MarkerFixture fixture, string name) -> MapAssetClosure`, and adds `PrefabFixtures.RoomFreeSupportResource() -> PrefabFixture` to `PrefabSupportFixtures.cs` and `PrefabFixtures.RoomFreeStandalonePrism() -> PrefabFixture` to `PrefabCaveFixtures.cs`, both through the private released helpers beside them. Task 4 adds `DuplicateNames(string variant) -> MarkerFixture` with variants `disabled-child` and `disabled-world`, `RectPoints() -> MarkerFixture` and the test wrapper `CountingSurfaceSource(IMapSurfaceSource inner) : IMapSurfaceSource` with `ReadPatchCalls` and `FindPatchesCalls`. Task 5 creates editor-only `MarkerEditorFixtures.Session(MarkerFixture fixture) -> MapEditSession` and `State(MapEditSession session) -> string`, a canonical snapshot of document, identity, closure, history labels/cursor, events and dirty state. Task 6 adds `LegacyLists() -> MarkerFixture` with both source kinds sharing ID `old`, and `Choices() -> IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice>`. Task 7 creates editor-only `MarkerConsumerFixtures.WindowedNative(string root) -> string`, which writes valid bodies, and `CorruptBodies(string root) -> int`, which overwrites every terrain and support tile body and returns the count. Task 9 adds `Village() -> MarkerFixture`.

Assertion blocks below are contracts inside the named test methods. Variables such as `before`, `after`, `marker`, `result` and `history` are the actual released/produced objects described immediately above each block, not undeclared helper APIs. Add fixture variants to the owning task's fixture file and commit paths. No test may implement a second classifier or return hard-coded product answers from a helper.

### Task 1: Payload 2, pure format 8 and compatibility

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerHeight.cs`, `MapMarkerMigration.cs`, `MapMarkerValidation.cs`.
- Modify: `KhaozEngine.MapDoc/Prefabs/MapPrefabRecords.cs:57-58`, `MapPrefabJson.cs:30-71` (resolver modifier for the marker binding member), `MapPrefabJson.cs:185-241` (Preflight marker path), `MapPrefabValidation.cs:122,390-395`, `KhaozEngine.MapDoc/MapDocumentFile.cs:37-43,85-95`, `KhaozEngine.MapDoc/mapdoc.schema.json:10,516`. Lines 339-351 (`supportBinding`) stay unchanged.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MarkerFixtures.cs`, `MapMarkerFormatTests.cs`, `KhaozEngine.MapDoc.Tests/Fixtures/Markers/PayloadOne.prefab.json`. No csproj edit.
- Test/modify: `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabFormatTests.cs:125-127`, renaming released `FormatEight_Refuses` to `FormatNine_RefusesWithUpdateGuidance`, changing its input version to 9 and adding the guidance assertion. `KhaozEngine.MapDoc.Physics.Tests/MapPersistedIdentityTests.cs:59-100` gains the format-8 derivation. `FormatAdvanceTests.cs`, `MapDocumentFileTests.cs:58` and `MapTiledFormatTests.cs:72` use `CurrentFormatVersion` symbolically and stay unchanged as regressions.

**Interfaces:**
- Consumes released `MapLocalMarkerDoc(int PayloadVersion, string Id, string Name, Vector3 Position, float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags)` at Prefabs/MapPrefabRecords.cs:57, `MapSupportBindingKind { Surface, Space }` and `MapSupportBinding(MapSupportBindingKind Kind, string? SurfaceId, string? SpaceId, float? ReferenceY, float? SearchBelow, float? SearchAbove)` at MapSupportRecipe.cs:6-9.
- Consumes released `MapPrefabFile.Read(ReadOnlyMemory<byte> bytes, string resourceId) -> MapPrefabAsset`, `Write(MapPrefabAsset asset) -> byte[]`, `MapDocumentFile.LoadText(string json, MapDocumentLoadOptions? options = null, string? sourcePath = null) -> MapDocument`, `SaveText(MapDocument doc, MapDocRegistry? registry = null) -> string`, internal `MapPrefabJson.Preflight(JsonElement value)` at :185.
- Produces `public enum MapMarkerHeightMode { Explicit, Support }` in namespace `KhaozEngine.MapDoc.Markers`. Adds init-only `MapLocalMarkerDoc.HeightMode: MapMarkerHeightMode?`, `SupportBinding: MapSupportBinding?` and `ArchetypeId: string?`, each omitted when null, with no constructor change. The DTO comment states that payload 1 and payload-2 Explicit carry the authored height in `Position.Y`, and that payload-2 Support carries `Position.Y` exactly +0 with no height meaning, the resolved height being on `MapEffectiveMarker.Position`.
- Payload 1 forbids all three members. Payload 2 requires HeightMode. Explicit forbids SupportBinding and requires finite `Position.Y`. Support requires SupportBinding and `Position.Y` exactly +0, refusing -0. Archetype is optional and nonempty. Position.X/Z and yaw remain finite.
- Produces internal `MarkerBindingConverter : JsonConverter<MapSupportBinding>`, installed by the resolver modifier only on `MapLocalMarkerDoc.SupportBinding`. It writes `kind` then only the members of that kind, and reads closed kind-dependent members, refusing nulls, duplicates, case variants and foreign members.
- Extends Preflight so a null member inside the `supportBinding` object of any object that also has `heightMode` refuses. The released null allowance for placement `supportBinding` members is unchanged.
- Produces schema `$defs/markerSupportBinding` (`oneOf` a Surface object requiring `kind` and `surfaceId` and a Space object requiring `kind`, `spaceId`, `referenceY`, `searchBelow` and `searchAbove`, no null types, `additionalProperties: false`). `localMarker` becomes `oneOf` the unchanged payload-1 shape and a payload-2 shape with `heightMode`, `supportBinding` referencing `markerSupportBinding`, `archetypeId` with `minLength: 1`, and if/then rules for the two modes. `formatVersion` const becomes 8.
- Produces `MapMarkerHeight(float? ExplicitY, MapSupportBinding? Binding)` as the command and adoption input type, and `MapMarkerHeights.Apply(MapLocalMarkerDoc marker, MapMarkerHeight height) -> MapLocalMarkerDoc` returning the payload-2 canonical encoding. Both branches or neither refuse.
- Produces `MapMarkerMigration.Upgrade(JsonObject source) -> JsonObject`, accepting only 7 or 8 and returning an independent format-8 clone, plus `MapMarkerValidation.Validate(MapLocalMarkerDoc marker) -> IReadOnlyList<string>` shared by file, prefab and document validation.

- [ ] **Step 1: Write failing tests.** `SevenToEight_IsVersionOnlyAndDoesNotSample` builds `FormatSevenJson(false)` and replaces its `surfaces` value with the sentinel object `{"sentinel":"unreadable"}`, which any sampling or validation refuses. Upgrade returns the version-only clone with the sentinel intact, and `MapDocumentFile.LoadText` of the same sentinel text throws (positive control). `PayloadOnePrefabBytes_AreUnchanged` reads the committed golden from `Path.Combine(AppContext.BaseDirectory, "Fixtures", "Markers", "PayloadOne.prefab.json")` and compares it with a fresh write of `PayloadOneAsset()` and with a read/write round trip, by bytes and independent SHA256.

```csharp
Assert.Equal(8, upgraded["formatVersion"]!.GetValue<int>());
Assert.Equal(7, original["formatVersion"]!.GetValue<int>());
Assert.True(JsonNode.DeepEquals(expectedVersionOnlyClone, upgraded));
Assert.Equal("unreadable", upgraded["surfaces"]!["sentinel"]!.GetValue<string>());
Assert.ThrowsAny<Exception>(() => MapDocumentFile.LoadText(sentinelText));
Assert.Equal(goldenBytes, MapPrefabFile.Write(MarkerFixtures.PayloadOneAsset()));
Assert.Equal(goldenBytes, MapPrefabFile.Write(MapPrefabFile.Read(goldenBytes, "golden-marker")));
Assert.Equal(SHA256.HashData(goldenBytes), SHA256.HashData(rewrittenBytes));
Assert.DoesNotContain("\"heightMode\"", goldenJson);
Assert.DoesNotContain("\"archetypeId\"", goldenJson);
Assert.Equal(MarkerFixtures.Exact(-0.25f, 12.25f, -1.5f), basicPoint);
Assert.Equal(new[] { "first", "second" }, reloadedMarker.Tags);
```

`PayloadTwoBothModes_RoundTripMonolithicAndTiled` writes world and prefab payload-2 markers in three forms, Explicit at Y 12.25, Support Surface `floor`, and Support Space `level-2` with referenceY 20.2 and searches 0.5, then saves and reloads both storage forms. Repeated save is byte-identical. The Explicit JSON contains no `"supportBinding"`. The Surface binding JSON is exactly `{"kind":"Surface","surfaceId":"floor"}` and contains no `"referenceY"`. The Support markers carry `Position.Y` 0 in the DTO.

`MarkerPayloadClosedSchema_Refuses` covers future payload 3, unknown/case-variant/duplicate fields, payload 2 without heightMode, payload 1 with heightMode, supportBinding or archetypeId, Explicit with a binding, Support without a binding, Support with Y 1 or -0, NaN/infinity, null tags, empty archetype, a Surface binding carrying referenceY or searchBelow, a Space binding missing referenceY, an explicit null inside a marker binding, and negative or nonfinite search. Schema and runtime validation agree for every case. A placement `supportBinding` with explicit nulls still loads (control for the untouched placement definition). `FormatNine_RefusesWithUpdateGuidance` asserts the message contains `newer than this engine supports`. `SevenToEight_MovesEveryNativeIdentityOnce` covers marker-free, standalone, prefab and stored paths, deriving new pins from canonical version-only root bytes, not copied build output. Repeated load/save changes nothing. `AnalyticExecutionAndOldSpawnReaders_KeepReleasedBits` uses unchanged FormatFour recorded placements/callbacks and existing analytic lists.

`StackedCaveBuildHash_IsPinned` adds `authoredEight = Authored(RootDigest(manifest, 8), fixture, effectivePolicy: true)` and asserts `Assert.Equal(authoredEight, world.AuthoredHash)` and `Assert.Equal(Build(authoredEight), world.BuildHash)`, keeping the format-6 and format-7 controls. The literal format-8 hexes are recorded only after this derivation passes, never copied from product output.

- [ ] **Step 2: Run red** `wa_test t1-red "$MAPDOC" "FullyQualifiedName~MapMarkerFormatTests"`, then `wa_test t1-red-pins "$MP" "FullyQualifiedName~MapPersistedIdentityTests.StackedCaveBuildHash_IsPinned"`. Expect missing HeightMode/migration APIs, format-8 refusal and the format-8 hash assertion failing against the format-7 hash. The renamed `FormatNine_RefusesWithUpdateGuidance` already passes at format 7 and is recorded as a preserved regression in green. Capture the golden before implementation through released public codec behavior.
- [ ] **Step 3: Implement** the declared DTO members, closed schema/codec, Preflight marker rule and pure migration. Register 7 to 8 after MapPrefabMigration, change CurrentFormatVersion/schema to 8, preserve prior migrations and historical recorder bytes. New optional members omit null on every codec, clone, canonical and tool path. No height sampling or legacy-list conversion occurs in migration. Payload-1 positional construction continues to mean explicit Position.Y.
- [ ] **Step 4: Run green** with both Step 2 filters as `t1-green` and `t1-green-pins`, then `wa_test t1-regress "$MAPDOC" "FullyQualifiedName~FormatAdvanceTests|FullyQualifiedName~MapPrefabFormatTests"`. Run `wa_sweep t1-final` after review/fixes.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t1_paths=(
    KhaozEngine.MapDoc/Markers/MapMarkerHeight.cs
    KhaozEngine.MapDoc/Markers/MapMarkerMigration.cs
    KhaozEngine.MapDoc/Markers/MapMarkerValidation.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabRecords.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabValidation.cs
    KhaozEngine.MapDoc/MapDocumentFile.cs
    KhaozEngine.MapDoc/mapdoc.schema.json
    KhaozEngine.MapDoc.Tests/Markers/MarkerFixtures.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerFormatTests.cs
    KhaozEngine.MapDoc.Tests/Fixtures/Markers/PayloadOne.prefab.json
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabFormatTests.cs
    KhaozEngine.MapDoc.Physics.Tests/MapPersistedIdentityTests.cs
)
wa_finish t1 "${wa_r6_t1_paths[@]}" &&
git add -- "${wa_r6_t1_paths[@]}" &&
git commit --only -m "feat(mapdoc): add marker payload 2 and pure format 8 advance" -- "${wa_r6_t1_paths[@]}"
```

### Task 2: Resolve height, provenance and space

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerExpansion.cs`, `MapMarkerResolution.cs`, `MapMarkerSpaces.cs`.
- Modify: `KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs:226-287`, `MapEffectiveResources.cs:15-71`, `MapEffectiveAnnotations.cs:16-49`, `MapPrefabReferences.cs:114-160` (`RemapWorld` BuildingIntent marker branch) and `:319-354` (child walker), `KhaozEngine.MapDoc/Editing/MapPrefabEditPreparation.cs:155-204`, `MapPrefabExtraction.cs` marker capture/remap. `Support/MapSupportQuery.cs` and `Support/MapSupportTypes.cs` stay unchanged.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MarkerSupportFixtures.cs`, `MapMarkerHeightTests.cs`, `MapMarkerMembershipTests.cs`, `MapMarkerRemapTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapMarkerSupportWorldTests.cs`.
- Modify source links: `KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj:22`, replacing its Prefabs-only fixture glob with `../KhaozEngine.MapDoc.Tests/**/*Fixtures.cs`, matching the released Editor pattern. This includes SupportFixtures and its Cave/Scope/FormatFour dependency fixtures, with no test-assembly reference. FormatFourFixtures reads its files only when called, and no Physics test calls it.

**Interfaces:**
- Consumes released `MapPrefabResolver.Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits? limits = null) -> MapEffectiveResources` at :23 and internal overload `Expand(..., MapPrefabExpansionObserver? observer, IMapSurfaceSource? nativeSource = null)` at :26, `MapResolverV2.Resolve(MapDocument document, MapAssetClosure assets, IMapSurfaceSource surfaces, MapResolveOptions options) -> MapSupportedResolution` at Support/MapResolverV2.cs:17, `MapEffectiveResources.Acquire(MapSurfaceScope scope) -> MapScopedSurfaces`, `CompleteView() -> MapScopedSurfaces`, `PlacementSupports` and `MapPlacementSupport(string PlacementId, MapSupportResult Support)` at Support/MapSupportTypes.cs:66.
- Consumes internal released `MapSupportQuery.SampleSurfaceExact(MapExactPoint world, string surfaceId) -> MapSupportResult` and `SelectExact(MapSupportRequest request, MapExactPoint world) -> MapSupportResult` at Support/MapSupportQuery.cs:31-33, whose `MapSupportResult.WorldY` is a float (Support/MapSupportTypes.cs:21, narrowed at MapSupportQuery.cs:278), `MapPrefabResolver.WorldRequest(MapSupportBinding binding, MapEffectiveTransform pose) -> MapSupportBinding` at :307 and `LocalRequest` at :299. Consumes released `MapPrefabReferences.RemapWorld(MapDocument document, MapPrefabChildKind kind, string oldId, string newId) -> MapDocument` at :117, whose marker branch at :155 copies `intent.Markers` unchanged today.
- Consumes released `MapSpaceMembership(MapScopedSurfaces scoped)`, `QueryExact(MapExactPoint world) -> MapMembershipResult`, returning Status, SpaceId, ParentSpaceIds and Witness, at Spaces/MapSpaceMembership.cs:10-68. No column API.
- Produces value-only `MapMarkerHeightProvenance(MapMarkerHeightMode Mode, MapSupportBinding? Binding, MapFaceKey? Face, string? SpaceId, MapRecordRef? Via, float? WorldY)`. Binding is the world-space request. For Support, WorldY is the support query's `MapSupportResult.WorldY` and the effective position Y is `MapExactValue.FromSingle(WorldY)`. Every member is a value type or value record, so record equality holds across heads and reloads.
- Adds init-only `MapEffectiveMarker.HeightProvenance: MapMarkerHeightProvenance?` and `ArchetypeId: string?`, each with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`, preserving the released positional constructor. Payload-1 markers leave both null. Payload-2 Explicit markers carry Mode Explicit and null binding, face, space, via and WorldY, because the effective position already carries the explicit height and no support float exists.
- Extends `RemapWorld`'s BuildingIntent marker branch. A Support-mode marker whose active binding is Surface has `surfaceId` renamed when `kind` is Surface, and one whose binding is Space has `spaceId` renamed when `kind` is Record. ReferenceY, searches, the +0 carrier, name, role, tags, archetype and the pose values of the marker's `NativeResourceTransforms` frame are copied bit for bit. Explicit markers and payload-1 markers are copied unchanged. The released remaps of other roots keep their behavior.
- Produces `MapMarkerSupport(string MarkerId, MapSupportResult Support)` and `MapEffectiveResources.MarkerSupports: IReadOnlyList<MapMarkerSupport>`, ordinal-ID ordered, one row per Support-mode marker including disabled ones. This channel is excluded from annotation digests and record equality.
- Changes `MapEffectiveAnnotations.Digest` to schema `kemap/effective-annotations/1` when no effective marker has payload 2, otherwise `kemap/effective-annotations/2`. Payload-1 rows serialize exactly as released.
- Produces internal `MapMarkerCandidate(MapLocalMarkerDoc Value, MapEffectiveTransform? Frame, string? InstanceId)` and `MapMarkerExpansion.Capture(MapDocument document, MapAssetClosure assets) -> IReadOnlyList<MapMarkerCandidate>`. Capture reads root globals and verified prefab payloads, reuses released reconciliation/remapping and exact frames, does not resolve geometry or allocate. Main expansion, Task 4 name validation and Task 7 anchors share it.
- Produces internal `MapMarkerResolution.Resolve(IReadOnlyList<MapMarkerCandidate> candidates, IMapSurfaceSource surfaces, IReadOnlyList<string> assetSha256, out IReadOnlyList<MapMarkerSupport> supports) -> IReadOnlyList<MapEffectiveMarker>` and public `MapMarkerSpaces.Query(MapScopedSurfaces view, MapEffectiveMarker marker) -> MapMembershipResult`.
- Frame rule. A prefab-local marker and a framed standalone marker (one with a `NativeResourceTransforms` Marker entry) store their binding frame-local. Expansion applies `WorldRequest(binding, frame)` exactly once. An unframed standalone marker stores a world binding. Unpack copies the marker value and its precise frame verbatim, as `MapPrefabEditPreparation.cs:191,197-198` does today. Task 5 `marker_set` follows the same rule.

- [ ] **Step 1: Write failing tests.** `StackedCaveMarkerSupport_DoesNotSnapToSurface` uses Stacked at (0.5, 0.5), binding Surface floor-2, then bounded Space level-2 with ReferenceY 20.2, SearchBelow/Above 0.5. Both return Y 20 with the named support, never floor-7 at 70. `UndergroundMarker_ExplicitHeightAndLayerSurviveReload` uses the released stacked prefab-cave fixture at (2, -57.75, -2), both storage forms, returning deep-room and parent deep-cave. EightStacked uses its released positive-Z frame, while the prefab fixture uses ImportedMetreCentimetre's negative-Z rows.

```csharp
Assert.Equal(20f, marker.Position.Y.ToSingle());
Assert.Equal("floor-2", marker.HeightProvenance!.Face!.Value.OwnerId);
Assert.Equal(MapMarkerHeightMode.Support, marker.HeightProvenance.Mode);
Assert.Equal(20f, marker.HeightProvenance.WorldY);
Assert.Equal(MapSupportStatus.Supported, resources.MarkerSupports.Single(s => s.MarkerId == marker.Id).Support.Status);
Assert.Equal(-57.75f, underground.Position.Y.ToSingle());
Assert.Equal(MapMembershipStatus.Resolved, membership.Status);
Assert.Equal("deep-room", membership.SpaceId);
Assert.Equal(new[] { "deep-cave" }, membership.ParentSpaceIds);
```

`LocalCaveMarker_TransformsOnceAndKeepsVolumeAssociation` uses an actual local room marker at (2, 1, -2), instance (-12.25, -450, 7.125), yaw 0, scale 2. Expect (-8.25, -448, 3.125), its remapped room and authored parent. Author the transformed parent with the released fixture's AuthorWorldParent path. Additional 0.371 yaw and scales 0.8/1.2 compare independently calculated quantized points, not the product transform as an oracle. Query a spaceless explicit point and assert Outside with empty parents. Missing geometry, ambiguity, capacity and nonrepresentability remain distinct statuses, never Outside.

`SlopedSupport_EffectiveYEqualsQueryWorldY` resolves Sloped in both storage forms and after save/reload. The test samples the released query independently, `direct = new MapSupportQuery(Effective(fixture).CompleteView()).SampleSurfaceExact(MarkerFixtures.Exact(0.3f, 0, 0.7f), "floor-2")`, and requires a genuinely non-dyadic answer before comparing.

```csharp
Assert.Equal(MapSupportStatus.Supported, direct.Status);
Assert.NotEqual(MathF.Round(direct.WorldY * 64f) / 64f, direct.WorldY);
Assert.Equal(MapExactValue.FromSingle(direct.WorldY), slope.Position.Y);
Assert.Equal(direct.WorldY, slope.HeightProvenance!.WorldY);
Assert.Equal(slope.Position.Y, reloadedSlope.Position.Y);
Assert.Null(flat.HeightProvenance!.WorldY);
```

`RemapWorld_RenamesOnlyTheActiveMarkerOwner` in `MapMarkerRemapTests` covers an unframed standalone Surface marker on `x`, an unframed standalone Space marker on `x`, and TransferredSurface and TransferredSpace after `MapUnpackPrefab`, where unpack has already written world owner IDs into the frame-local bindings. An explicit marker named `x` with tags `["x"]` is the same-text control. `RemapWorld(document, MapPrefabChildKind.Surface, "x", "y")` renames only the Surface binding, `RemapWorld(document, MapPrefabChildKind.Record, "x", "y")` renames only the Space binding, and the transferred cases rename their world owner while ReferenceY, searches and the frame pose stay bit-identical to the independently captured pre-remap values. The input document is unchanged. Load, save, reload and `MapPrefabResolver.Expand` of the unremapped document leave every stored binding byte-identical. `Binding`, `Authored`, `FrameOf` and `BindingBytes` are local readers of the stored BuildingIntent marker, its frame entry and its canonical JSON, never a second remapper.

```csharp
Assert.Equal("y", Binding(surfaceRenamed, "s").SurfaceId);
Assert.Equal("x", Binding(surfaceRenamed, "v").SpaceId);
Assert.Equal("y", Binding(recordRenamed, "v").SpaceId);
Assert.Equal("x", Binding(recordRenamed, "s").SurfaceId);
Assert.Equal("x", Authored(surfaceRenamed, "e").Name);
Assert.Equal(new[] { "x" }, Authored(surfaceRenamed, "e").Tags);
Assert.Equal(localBindingBefore with { SpaceId = renamedOwner }, Binding(transferredRenamed, transferredId));
Assert.Equal(frameBefore, FrameOf(transferredRenamed, transferredId));
Assert.Equal(storedBindingBytes, BindingBytes(MapDocumentFile.LoadText(MapDocumentFile.SaveText(original))));
```

`BoundMarkerUnpackExtract_KeepsIntentAndTransformsOnce` exercises actual MapUnpackPrefab, extraction and replacement on TransferredSpace. The post-unpack stored binding equals independently expected world-ID bytes, and those bytes are identical after reload. TransferredSpace keeps the released Local slot, so no byte identity across unpack is claimed. ReferenceY and the search values stay frame-local and unchanged by unpack. The effective ReferenceY equals -450 + 1.2 times the local ReferenceY and each search equals 1.2 times its local value, computed independently in the test. Compare height mode, world binding, exact point, ordered tags, instance provenance and yaw modulo a turn before/after save/reload. Regrade the transferred floor and prove the marker follows. A same-text Local Surface `floor` and World Surface `floor` remap only the declared local slot at `/value/supportBinding/surfaceId`. Include owner removal/restore and invalid slot positive controls.

`PayloadOneAnnotationDigest_IsUnchanged` asserts `Effective(Create("basic")).AnnotationDigest` and `Effective(Create("prefab-basic")).AnnotationDigest` equal two literal hexes. The literals are captured by running the released digest on the accepted Task 1 head before any Task 2 product edit, after confirming `git diff 1a4182c24 -- KhaozEngine.MapDoc/Prefabs/MapEffectiveAnnotations.cs KhaozEngine.MapDoc/Prefabs/MapEffectiveResources.cs` is empty. A payload-2 control asserts a different digest and that `HeightProvenance` is null on both payload-1 markers. `ProvenanceAndDigest_IgnoreSnapshotWitness` resolves Stacked through `MapPrefabResolver.Expand` and through `MapResolverV2.Resolve` with `MapDocumentSurfaceSource.Capture` of an independently reloaded document. Provenance records are equal by record equality and AnnotationDigest is equal, while the two `MarkerSupports` witnesses carry different `SnapshotId` values.

`MarkerSupportHeads_AgreeOnPointsProvenanceAndMembership` in `MapMarkerSupportWorldTests` builds independently reloaded client and server worlds from Stacked, Sloped and Cave through `MapWorldBuilder.Build` and compares effective rows, provenance and membership by value. On each head the `slope` row's Y equals `MapExactValue.FromSingle` of that head's own direct support sample, with the same non-dyadic precondition.

- [ ] **Step 2: Run red** `wa_test t2-red "$MAPDOC" "FullyQualifiedName~MapMarkerHeightTests|FullyQualifiedName~MapMarkerMembershipTests|FullyQualifiedName~MapMarkerRemapTests"`, then `wa_test t2-red-heads "$MP" "FullyQualifiedName~MapMarkerSupportWorldTests"`. Expect missing resolution, provenance or MarkerSupports, and `RemapWorld` leaving the old binding owner. The digest test fails to compile until the task-owned `HeightProvenance` member exists, after which its two literal assertions are the payload-1 regression guard.
- [ ] **Step 3: Implement** capture, support resolution and point membership. Resolve markers after the placement-seat context rebuild at `MapPrefabResolver.cs:226-230`, so seated support placements and their owned floors are already in the source. Transform marker XZ once. Explicit local Y composes once through the same frame. Bound Y is `MapExactValue.FromSingle` of the released query's `WorldY`, the value support-bound placements seat on, with no second frame translation or scale and no marker-local sampler or new query overload. Extend the typed walker for Marker children with `/value/supportBinding/surfaceId` as kind Surface and `/value/supportBinding/spaceId` as kind Record, requiring exactly the active binding's slot. Local $parent uses the released supplied-parent rule. World slots stay world even when their text matches a local key. Preserve bindings, the +0 carrier and precise frame through unpack/extract, never bake a bound marker into explicit Y. Extend `RemapWorld`'s marker branch at :155 to rename only the active binding's typed world owner as declared above. Load and build never call it.

Acquire per-point scopes with existing budgets and exact XZ. A Surface call names only its owner. A Space call uses the complete declared vertical interval. No LegacyFallback, missing or capacity answer counts as support. Resolve disabled bindings too. The effective BuildingIntent keeps each marker's authored canonical encoding, so effective annotation validation never sees a sampled Y as a second authored height. The resolved XYZ belongs to MapEffectiveMarker. No new collider, region disc or nav geometry is created. Bound candidate/read counters, with small exact-limit and over-limit tests and unchanged F-R12/F-R13 controls.

- [ ] **Step 4: Run green** with both Step 2 filters as `t2-green` and `t2-green-heads`. Run `wa_sweep t2-final` after review/fixes. The released village digest tests (`MapPrefabVillageAcceptanceTests.cs:33`, `MapPrefabRoofStaticTests.cs:56`, `MapPrefabVillageTests.cs:52`) pass unchanged in the sweep.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t2_paths=(
    KhaozEngine.MapDoc/Markers/MapMarkerExpansion.cs
    KhaozEngine.MapDoc/Markers/MapMarkerResolution.cs
    KhaozEngine.MapDoc/Markers/MapMarkerSpaces.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveResources.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveAnnotations.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabReferences.cs
    KhaozEngine.MapDoc/Editing/MapPrefabEditPreparation.cs
    KhaozEngine.MapDoc/Editing/MapPrefabExtraction.cs
    KhaozEngine.MapDoc.Tests/Markers/MarkerSupportFixtures.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerHeightTests.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerMembershipTests.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerRemapTests.cs
    KhaozEngine.MapDoc.Physics.Tests/MapMarkerSupportWorldTests.cs
    KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj
)
wa_finish t2 "${wa_r6_t2_paths[@]}" &&
git add -- "${wa_r6_t2_paths[@]}" &&
git commit --only -m "feat(mapdoc): resolve marker support with value provenance and a witness channel" -- "${wa_r6_t2_paths[@]}"
```

### Task 3: Support-edit consequences and shared rename rows

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapMarkerEffects.cs`, `KhaozEngine.MapEditor/NativeMarkerEffects.cs`.
- Modify: `KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs:68-76,100-108`, `KhaozEngine.MapEditor/NativeDocumentTransaction.cs:91-104`, `NativePlacementEffects.cs:144-184,248-263`.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MarkerEffectFixtures.cs`, `MapMarkerEffectTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerSupportEditTests.cs`, `NativeMarkerRenameEffectTests.cs`.
- Test/modify: `KhaozEngine.MapDoc.Tests/Prefabs/PrefabSupportFixtures.cs` (additive `RoomFreeSupportResource` beside `SupportResourceFixture` at :30-50) and `PrefabCaveFixtures.cs` (additive `RoomFreeStandalonePrism` beside `StandalonePrism` at :245-259). No released fixture, ID encoding or validator changes.

**Interfaces:**
- Consumes Task 2 effective markers and MarkerSupports. Consumes released `MapNativeEditEffects` with `MarkerIds` and `Describe() -> string` at Editing/MapNativeEditEffects.cs:72-108, `NativePlacementEffects.Combine(...)` at :144 and `ResourceBounds` at :248, internal `MapPrefabResolver.ThreadExpansions` at :21.
- Consumes released commands as drivers. `TerrainEditCommand(MapTerrainEdit edit)` at NativeTerrainCommands.cs:22 with `MapSetCornerHeights` and `MapReplaceTopology(IReadOnlyList<MapSurfacePatch> UpsertPatches, IReadOnlyList<MapPatchKey> RemovePatches, IReadOnlyList<MapSurfaceRef> UpsertSurfaces, IReadOnlyList<string> RemoveSurfaces)` at Editing/MapTerrainEdits.cs:10,22. `MovePlacementCommand(string id, float newX, float newZ, float? newY)` and `RemovePlacementCommand(string id)` in EditorCommands.Placements.cs. `PrefabEditCommand(MapPrefabEdit edit)` at NativePrefabCommands.cs:13 with `MapMovePrefab`, `MapSetPrefabOverride`, `MapRemovePrefab` and `MapUnpackPrefab`. `PrefabAssetEditCommand(MapPrefabEdit edit, MapAssetClosure before, MapAssetClosure after)` at NativePrefabAssetCommands.cs:14 with `MapUpdatePrefabAsset(string ResourceId, string OldSha256, string NewResourceId, string NewSha256)`. `InteriorEditCommand(MapInteriorEdit edit)` at NativeInteriorCommands.cs:13 with `MapRemoveInterior(string SpaceId)`.
- Produces `MapMarkerNameChange(string MarkerId, string? Before, string? After)` and additive `MapNativeEditEffects.MarkerNameChanges: IReadOnlyList<MapMarkerNameChange>`, default empty. Describe writes sorted `{markerId,before,after}` rows under `markerNameChanges` only when nonempty, so every released description keeps its bytes.
- Produces pure `MapMarkerEffects.Describe(IReadOnlyList<MapEffectiveMarker> before, IReadOnlyList<MapEffectiveMarker> after) -> MapNativeEditEffects`, the one shared diff. It reports every marker whose point, yaw, enabled state, binding or provenance changed with Residency and both point boxes from `MapEffectiveAnnotations.Bounds`, every create or delete with exactly one box, and every name change as a rename row. Metadata-only differences produce IDs and rename rows without bounds.
- Produces internal `NativeMarkerEffects.Combine(MapNativeEditEffects effects, MapEffectiveResources before, MapEffectiveResources after) -> MapNativeEditEffects`, and internal `NativeAcceptedExpansions` with `Remember(MapDocument live, MapAssetClosure closure, Lazy<MapEffectiveResources> expansion) -> void` and `TryBefore(MapDocument live, MapAssetClosure closure, out Lazy<MapEffectiveResources> expansion) -> bool`, keyed by live document and closure reference in a `ConditionalWeakTable`. Every accepted publication through NativeDocumentTransaction replaces the entry, and reload or open creates a new document instance.

- [ ] **Step 1: Write failing tests.** `SupportEdits(variant, withMarker)` variants are below. `withMarker: false` builds the identical fixture without `bound`, the marker-absent control.
- `stacked-bound`. Stacked plus Surface-bound world marker `bound` at X 0.5, Z 0.5 on floor-2 and explicit `fixed` at Y 12.25.
- `deck-bound`. `PrefabFixtures.RoomFreeSupportResource()` plus a world marker Surface-bound to `PrefabFixtures.SupportFloorId`, the released effective ID of the deck's implicit floor, never a literal suffix match. RoomFreeSupportResource derives from the released `SupportResourceFixture` by removing the asset's `room`, `room-domain`, `door-bottom`, `door-top` and `room-portal` Record children and the `roof-link` child, then re-capturing through the same private `Reasset` and `AssetsWithSupport` calls. Its precondition asserts that the effective patch `new MapPatchKey(PrefabFixtures.SupportFloorId, 0, 0)` holds no records, so no topology depends on the deck floor. The deck's placement ID after unpack is the derived world ID `prefab/a/Placement/deck`, beside `SupportFloorId`, and tests select it with `document.Placements.Single(p => p.AssetId == "deck").Id` rather than a literal.
- `floor-bound`. Released `PrefabFixtures.Create()` plus a world marker Surface-bound to instance `a`'s effective `floor` surface, selected through the instance binding for child key (Surface, `floor`).
- `room-bound`. `PrefabFixtures.RoomFreeStandalonePrism()` plus a world marker Space-bound to `a/room`, with ReferenceY taken from the Supported sample of the room floor at the marker XZ and searches 0.5. RoomFreeStandalonePrism derives from the released `StandalonePrism` by removing every record other than `a/room` and its prism domain whose typed references name `a/room`, which in the released asset is exactly the `a/room-portal` cave portal. It keeps the real prism, floor and parent. Its precondition asserts no remaining `MapCavePortal` and no remaining record naming `a/room` outside the space and its prism.

If any variant's binding is not Supported before the edit, or any marker-absent control below fails, the fixture is invalid and the worker stops and reports rather than changing the binding rule, topology validation or `MapRemoveInterior`.

`BoundMarkerReseat_NamesBothPointsAndRefusesLostOwner` drives each transaction kind and asserts the bound marker's effects. Each reseat leaves marker descriptor bytes unchanged and reports ID, old and new point boxes and Residency. Physics/Nav flags already caused by the driving edit are retained, none added by markers. `fixed` and other unrelated explicit points never appear. Every move and removal below first runs on `withMarker: false` and must succeed, and every refusal names `bound`.
- Terrain. `TerrainEditCommand(new MapSetCornerHeights(floor2Patch, 0, 0, 3, 3, nine values of 2100))` reseats `bound` from Y 20 to 21. `TerrainEditCommand(new MapReplaceTopology([], [floor2Patch, ceiling2Patch], [], ["floor-2", "ceiling-2"]))` removes the floor with its level-2 records and refuses. The same removal succeeds once `bound` is explicit and on the marker-absent control.
- Support-resource placement. `PrefabEditCommand(new MapSetPrefabOverride("a", new MapPrefabPlacementOverride(deckKey, raisedTransform, null, null)))` raises the deck by 0.5 and reseats the marker. After `MapUnpackPrefab("a")`, the test selects `deckId = document.Placements.Single(p => p.AssetId == "deck").Id`, as the released unpack test does. `MovePlacementCommand(deckId, x, z, y + 0.5)` reseats again and `RemovePlacementCommand(deckId)` refuses. The override raise, the unpacked move and the unpacked removal each succeed on the marker-absent control.
- Prefab floor. `MapMovePrefab("a", transform raised by 1.5)` reseats the world marker by 1.5. `MapRemovePrefab("a")` refuses. Both succeed on the marker-absent control.
- Interior. `InteriorEditCommand(new MapRemoveInterior("a/room"))` refuses naming the Space-bound marker. The same edit succeeds with the marker changed to Explicit at the same point and on the marker-absent control.
- Every refusal preserves document, hash, closure, history, events and dirty state.

```csharp
Assert.Equal(new[] { "bound" }, effects.MarkerIds);
Assert.InRange(20d, effects.OldBounds!.Value.MinY, effects.OldBounds.Value.MaxY);
Assert.InRange(21d, effects.NewBounds!.Value.MinY, effects.NewBounds.Value.MaxY);
Assert.InRange(0.5d, effects.NewBounds.Value.MinX, effects.NewBounds.Value.MaxX);
Assert.True(effects.Invalidates.HasFlag(MapNativeInvalidation.Residency));
Assert.Equal(terrainOnly.Invalidates & (MapNativeInvalidation.Physics | MapNativeInvalidation.Nav), effects.Invalidates & (MapNativeInvalidation.Physics | MapNativeInvalidation.Nav));
Assert.Contains("bound", removal.Message, StringComparison.Ordinal);
Assert.Equal(stateBefore, stateAfterRefusal);
Assert.Empty(deckEffective.Surfaces.Patches[new MapPatchKey(PrefabFixtures.SupportFloorId, 0, 0)].Records);
Assert.DoesNotContain(prismEffective.Surfaces.AllRecords(), r => r is MapCavePortal);
Assert.True(markerAbsentHistory.CanUndo);
Assert.True(explicitMarkerHistory.CanUndo);
```

`MarkerEffects_DescribeIsPureAndOrdered` in `MapMarkerEffectTests` feeds hand-built before/after effective marker lists covering move, enable flip, binding change at equal height, rename, archetype-only change, create and delete, and asserts the exact Describe JSON including sorted `markerNameChanges`.

`PrefabOverrideRename_ReportsOldAndNewNames` applies `PrefabEditCommand(new MapSetPrefabOverride("a", new MapPrefabReplaceOverride(entryKey, renamedChild)))` to `prefab-basic`. `AssetUpdateRename_ReportsOldAndNewNames` applies `PrefabAssetEditCommand(new MapUpdatePrefabAsset("golden-marker", oldSha, "golden-marker-2", newSha), before, RenamedAsset(fixture, "arrival-renamed"))`. Each undo reverses the row and each redo restores it.

```csharp
Assert.Equal(new MapMarkerNameChange("a/entry", "arrival-a", "arrival-b"), Assert.Single(overrideEffects.MarkerNameChanges));
Assert.Equal(new MapMarkerNameChange("a/entry", "arrival-a", "arrival-renamed"), Assert.Single(assetEffects.MarkerNameChanges));
Assert.Equal(new MapMarkerNameChange("a/entry", "arrival-b", "arrival-a"), Assert.Single(undoEffects.MarkerNameChanges));
Assert.Equal(MapNativeInvalidation.None, renameOnly.Invalidates & (MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Unbounded));
```

`TerrainStroke_ReusesAcceptedExpansionAsBefore` applies three corner strokes to `stacked-bound` and asserts the `MapPrefabResolver.ThreadExpansions` delta is exactly 1 for each stroke after the first, and that each stroke's effects equal those from a cold control session that discards the cache.

- [ ] **Step 2: Run red** `wa_test t3-red-doc "$MAPDOC" "FullyQualifiedName~MapMarkerEffectTests"`, then `wa_test t3-red "$EDITOR" "FullyQualifiedName~NativeMarkerSupportEditTests|FullyQualifiedName~NativeMarkerRenameEffectTests"`. Expect omitted bound-marker effects on terrain and placement transactions, missing rename rows and an expansion delta of 2.
- [ ] **Step 3: Implement** the shared diff for every native transaction whose before and after expansions exist, regardless of write set. Remove the early return at `NativePlacementEffects.cs:159` for marker diffs, so `ResourceBounds` no longer limits markers to IDs already named. Refuse invalid post-edit marker support before publication. Add old and new resolved point extents to Residency without widening Physics/Nav merely because a marker moved. Use `NativeAcceptedExpansions.TryBefore` as the before side when it matches, otherwise the transaction's existing lazy before. Remember the accepted after expansion on publication. No expansion per marker.
- [ ] **Step 4: Run green** with both Step 2 filters as `t3-green-doc` and `t3-green`, then `wa_test t3-work "$EDITOR" "FullyQualifiedName~NativePublicationWorkTests"`. Run `wa_sweep t3-final` after review/fixes.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t3_paths=(
    KhaozEngine.MapDoc/Editing/MapMarkerEffects.cs
    KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs
    KhaozEngine.MapEditor/NativeMarkerEffects.cs
    KhaozEngine.MapEditor/NativeDocumentTransaction.cs
    KhaozEngine.MapEditor/NativePlacementEffects.cs
    KhaozEngine.MapDoc.Tests/Markers/MarkerEffectFixtures.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerEffectTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabSupportFixtures.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabCaveFixtures.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerSupportEditTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerRenameEffectTests.cs
)
wa_finish t3 "${wa_r6_t3_paths[@]}" &&
git add -- "${wa_r6_t3_paths[@]}" &&
git commit --only -m "feat(mapeditor): diff markers across every native transaction" -- "${wa_r6_t3_paths[@]}"
```

### Task 4: Unique names, lookup, roles and projections

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerRegistry.cs`, `MapMarkerSpatialIndex.cs`, `MapMarkerProjection.cs`, `MapMarkerRoles.cs`.
- Modify: Task 2's `Markers/MapMarkerExpansion.cs`, `KhaozEngine.MapDoc/Prefabs/MapPrefabValidation.cs:34-144,342-374`, `KhaozEngine.MapDoc/Identity/MapPrefabIdentity.cs` loaded/stored ownership validation hooks, `Editing/MapPrefabEdits.cs:11-12,44-52`, `Prefabs/MapPrefabJson.cs:89-96`, `KhaozEngine.MapEdit.Tool/Tools/PrefabTools.cs:29-32` for atomic initial overrides.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MarkerLookupFixtures.cs`, `MapMarkerRegistryTests.cs`, `MapMarkerProjectionTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapMarkerNameWorldTests.cs`.
- Test/modify: `KhaozEngine.MapDoc.Tests/Prefabs/PrefabVillageFixtures.cs`, `PrefabCaveFixtures.cs:185-214`, `KhaozEngine.MapEditor.Tests/MapEditTool/PrefabVillageWorkflowTests.cs:109-137,188-210`. Adapt repeated-instance fixture setup through explicit marker replace overrides, retaining every existing assertion unrelated to names.
- Modify docs: `CHANGELOG.md`, `KhaozEngine.MapDoc/README.md` marker contract and R6 handoff.

**Interfaces:**
- Consumes Task 2 candidates, `MapEffectiveAnnotations.Markers(MapEffectiveResources resources) -> IReadOnlyList<MapEffectiveMarker>` at Prefabs/MapEffectiveAnnotations.cs:27, `MapBuiltWorld.Markers` at Physics/MapBuiltWorld.cs:160 and `MapWorldBuilder.Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) -> MapBuiltWorld` at Physics/MapWorldBuilder.cs:41. The registry owns no document store.
- Consumes released `MapPrefabReplaceOverride(MapPrefabChildKey Child, MapPrefabChild Value)` at Prefabs/MapPrefabOverrides.cs:7 and `MapSetPrefabOverride(string InstanceId, MapPrefabOverride Override)` at Editing/MapPrefabEdits.cs:13.
- Produces init-only `MapPlacePrefab.InitialOverrides: IReadOnlyList<MapPrefabOverride>`, default empty, preserving its released constructor. These are request data, persisted only as the instance's existing Overrides. Add internal `MapPrefabJson.ReadOverrides(string json) -> IReadOnlyList<MapPrefabOverride>` using the same closed override codec. Preserve the released PrefabTools.PrefabPlace forwarding signature, register one `PrefabPlaceWithOverrides(string instanceId, string resourceId, float x, float y, float z, float yaw, float scale, string? parentRefJson = null, bool numericLeaves = true, string? overridesJson = null) -> MapPrefabMutationResult` under `prefab_place`. Do not register two tools with that name.
- Produces `MapMarkerRegistry(IReadOnlyList<MapEffectiveMarker> markers)` with `All` and `Enabled` read-only lists, `FindById(string id) -> MapEffectiveMarker?`, `FindByName(string name) -> MapEffectiveMarker?`, `ByRole(string role, bool enabledOnly) -> IReadOnlyList<MapEffectiveMarker>`, `ByTag(string tag, bool enabledOnly) -> IReadOnlyList<MapEffectiveMarker>`.
- Produces `MapMarkerRect(double MinX, double MinZ, double MaxX, double MaxZ)`, `MapMarkerMapPoint(MapEffectiveMarker Marker, MapMembershipResult Space)` and `MapMarkerRegistry.InRect(MapMarkerRect rect, bool enabledOnly, Func<MapEffectiveMarker, MapMembershipResult> membership) -> IReadOnlyList<MapMarkerMapPoint>`. Caller delegates to Task 2 point membership in a matching acquired view. Retain all refusal statuses in Space. A rectangle filter does not claim complete space membership.
- Produces internal `MapMarkerSpatialIndex` and test-visible `MapMarkerQueryWork(int NodesVisited, int PointsTested)`. Node bounds are rounded outward from exact coordinates with the `MapEffectiveAnnotations.Outward` rule. Instrument an internal query overload, no public performance/configuration knob.
- Produces `MapMarkerFinding(string MarkerId, string Code, string Detail)`, `MapMarkerRoleCatalog(IReadOnlyCollection<string> roles)` and `Validate(MapMarkerRegistry registry) -> IReadOnlyList<MapMarkerFinding>`. Unknown role yields code `unknown-role`, does not make MapDoc structurally invalid.
- Produces `MapPlayerStartView(MapEffectiveMarker Marker)`, `MapSpawnView(MapEffectiveMarker Marker, string? ArchetypeId)`, `MapLandmarkView(MapEffectiveMarker Marker)`, and static `MapMarkerProjection.PlayerStarts(MapMarkerRegistry registry, string role, bool enabledOnly) -> IReadOnlyList<MapPlayerStartView>`, `Spawns(MapMarkerRegistry registry, string role, bool enabledOnly) -> IReadOnlyList<MapSpawnView>`, `Landmarks(MapMarkerRegistry registry, string role, bool enabledOnly) -> IReadOnlyList<MapLandmarkView>`. These lossless wrappers retain XYZ, yaw, tags, enabled, name, ID and height provenance, rather than returning XZ-only legacy DTOs.

- [ ] **Step 1: Write failing tests.** `DisabledParticipantNameCollisions_ValidateBeforeAnyRead` runs two isolated cases, each with exactly one colliding pair and every other name unique. `disabled-child` places one instance `a` of a payload-1 marker asset whose single Marker child `entry` is disabled and named `arrival`, beside an enabled world marker `w` named `arrival`. `disabled-world` places one instance `a` whose single child `entry` is enabled and named `arrival`, beside a disabled world marker `w` named `arrival` with role `unregistered-role` and tags `["second", "first"]`. No other marker exists in either case, so a validator that skips disabled markers accepts both.

For each case the loaded path calls the internal `MapPrefabResolver.Expand(document, assets, new(), observer, counting)` and the stored path calls `MapResolverV2.Resolve(document, assets, counting, options)`, where `counting` is `CountingSurfaceSource` over `MapDocumentSurfaceSource.Capture(document)` and `observer` records stages. Both refuse before any patch read and before stage `ExpandedGeometry`. Then only the disabled participant changes. In `disabled-child` a `MapPrefabReplaceOverride(entryKey, child renamed arrival-child)` passes, and separately a `MapPrefabRemoveOverride(entryKey)` passes. In `disabled-world` renaming `w` to `arrival-world` passes, and separately removing `w` from BuildingIntent passes. Restoring the disabled participant (dropping the override, or re-adding `w` with its original ID and name) refuses again on both paths with the same counters at zero. IDs never change.

```csharp
Assert.Contains(errors, e => e.Contains("duplicate marker name", StringComparison.Ordinal));
Assert.Equal(0, counting.ReadPatchCalls);
Assert.Equal(0, counting.FindPatchesCalls);
Assert.DoesNotContain("ExpandedGeometry", stages);
Assert.Equal(new[] { "arrival", "arrival-child" }, childRenamed.All.Select(m => m.Name).Order(StringComparer.Ordinal));
Assert.Equal("w", childRenamed.FindByName("arrival")!.Id);
Assert.Null(childRenamed.FindByName("Arrival"));
Assert.Equal(2, childRenamed.All.Count);
Assert.Equal(1, childRenamed.Enabled.Count);
Assert.Equal(new[] { "arrival", "arrival-world" }, worldRenamed.All.Select(m => m.Name).Order(StringComparer.Ordinal));
Assert.Equal(new[] { "second", "first" }, worldRenamed.FindById("w")!.Tags);
Assert.Equal("unknown-role", Assert.Single(findings).Code);
Assert.Equal("unregistered-role", worldRenamed.FindById("w")!.Role);
Assert.Equal(errorsBeforeRename, errorsAfterRestore);
```

`DuplicateMarkerNames_FailBothHeadsBeforeStaticInstall` in `MapMarkerNameWorldTests` follows `MissingClosureFailsBothHeadsBeforeStaticInstall` at `MapPrefabVillageAcceptanceTests.cs:54`. For each of `disabled-child` and `disabled-world` and each of `client` and `server` it loads the colliding document independently and asserts `Assert.Throws<MapDocumentException>(() => installed = MapWorldBuilder.Build(...))`, a message containing `duplicate marker name` and `Assert.True(installed is null)`. The renamed and removed variants of each case build on both heads, and the restored variant fails again.

`TypedViews_RetainEveryValueAndUseOnlyCallerRoles` supplies `start-role`, `actor-role`, `label-role`. Check disabled values in exhaustive wrappers, enabled filtering, null archetype for tag-driven spawns and `kit.actor` for a payload-2 archetype. No engine role constant is introduced. Unknown catalog roles return stable findings in ID order. `Landmark_IsPointNotRegionDisc` checks unchanged Regions, surfaces, placements and geometry after adding a landmark.

`SecondInstance_InitialNameOverridePlacesAtomically` drives the actual MapPlacePrefab command, MutationService.PrefabApply and prefab_place wire adapter. Place a first instance named arrival-a, then a second with a marker replace override named arrival-b already in its request. Assert two accepted instances and two unique names, unchanged original asset bytes and exact binding/ID reuse after undo/redo. The same second request without overrides refuses without an instance, reservation or history advance. Duplicate initial override keys refuse before allocation. This test belongs in the existing PrefabVillageWorkflowTests, whose wire class already satisfies W-R1.

`HalfOpenRect_ContainsNegativeMinAndExcludesBothMaxEdges` uses XZ points (-1,-1), (0,-1), (-1,0), (0,0), (-0.25,-0.5). Query [-1,0) by [-1,0) returns only the first and last, ID ordered. Zero-width/height returns empty, reversed/nonfinite bounds refuse. Adjacent rectangles contain a boundary point once. `RectangleIndex_OutwardBoundsKeepBoundaryPoints` builds markers whose exact X is `new MapExactValue(1, 3)` and queries with MaxX equal to the smallest double strictly greater than 1/3, computed in the test by `MapWideRational` comparison. The exact half-open test includes the point, and the index must not cull it. `RectangleWork_IsCandidateBounded` uses 257 invented spaced points, a tiny query and a callback counter. Assert PointsTested <= 16, NodesVisited <= 32, membership calls equal result count, and equality with an independent exhaustive predicate. This is one bounded unit fixture, not a load test.

- [ ] **Step 2: Run red** `wa_test t4-red "$MAPDOC" "FullyQualifiedName~MapMarkerRegistryTests|FullyQualifiedName~MapMarkerProjectionTests"`, then `wa_test t4-red-heads "$MP" "FullyQualifiedName~MapMarkerNameWorldTests"` and `wa_test t4-red-village "$EDITOR" "FullyQualifiedName~PrefabVillageWorkflowTests.SecondInstance_InitialNameOverridePlacesAtomically"`. Expect missing views, released duplicate-name acceptance and a missing initial-override request.
- [ ] **Step 3: Implement** a defensive immutable registry, ordinal dictionaries, ID-ordered indexes and typed wrappers. Metadata-only enumeration validates effective names after current overrides but before any build/query callback. Invoke that same check on complete loaded and stored closure-bound validation, with no recursive call through full expansion. Partial views may not certify whole-world uniqueness. Keep exact points and ordered tags, with no deduplication of tags or name normalization.

Build the spatial index once per registry, choose the longest XZ extent for each median split and use ordinal IDs as ties. Query finite rectangles through outward node bounds, then exact half-open point tests. Do not enumerate every possible grid cell or scan the full marker list per chunk. Apply enabled filtering without discarding the exhaustive data. Membership uses point queries only for accepted candidates.

Update the R5 village workflow to name repeated instances distinctly through replace overrides, before publication of a duplicate state. MapPrefabEdits seeds a new instance's Overrides from the detached InitialOverrides request before planning bindings and validating names. No invalid intermediate instance is published. Immutable asset updates must remain atomic across referring instances and existing overrides. Correct other repeated-instance fixtures found by the three-project sweep in their owning fixture files, explicitly listing those paths before commit. CHANGELOG calls out the global-name compatibility change and format-8 identity move in the current staged entry, with no version bump in this task.

- [ ] **Step 4: Run green** with the three Step 2 filters as `t4-green`, `t4-green-heads` and `t4-green-village`, then `wa_test t4-village "$EDITOR" "FullyQualifiedName~PrefabVillageWorkflowTests"`. Run `wa_sweep t4-final` after review/fixes. Every old geometry assertion stays present.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t4_paths=(
    KhaozEngine.MapDoc/Markers/MapMarkerRegistry.cs
    KhaozEngine.MapDoc/Markers/MapMarkerSpatialIndex.cs
    KhaozEngine.MapDoc/Markers/MapMarkerProjection.cs
    KhaozEngine.MapDoc/Markers/MapMarkerRoles.cs
    KhaozEngine.MapDoc/Markers/MapMarkerExpansion.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabValidation.cs
    KhaozEngine.MapDoc/Identity/MapPrefabIdentity.cs
    KhaozEngine.MapDoc/Editing/MapPrefabEdits.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapEdit.Tool/Tools/PrefabTools.cs
    KhaozEngine.MapDoc.Tests/Markers/MarkerLookupFixtures.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerRegistryTests.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerProjectionTests.cs
    KhaozEngine.MapDoc.Physics.Tests/MapMarkerNameWorldTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabVillageFixtures.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabCaveFixtures.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/PrefabVillageWorkflowTests.cs
    KhaozEngine.MapDoc/README.md
    CHANGELOG.md
)
wa_finish t4 "${wa_r6_t4_paths[@]}" &&
git add -- "${wa_r6_t4_paths[@]}" &&
git commit --only -m "feat(mapdoc): enforce unique marker names and expose indexed role views" -- "${wa_r6_t4_paths[@]}"
```

### Task 5: Pure marker edits, undoable commands and wire parity

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapMarkerEdits.cs`, `KhaozEngine.MapEditor/NativeMarkerCommands.cs`, `KhaozEngine.MapEdit.Tool/MutationService.Markers.cs`, `QueryService.Markers.cs`, `Tools/MarkerTools.cs`.
- Modify: `KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs:89-96`, `KhaozEngine.MapEdit.Tool/QueryService.cs` declaration only (to `partial`), `Tools/McpBootstrap.cs:29-38`, `KhaozEngine.MapEditor.Tests/MapEditTool/McpAdapterTests.cs:57-108`.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MapMarkerEditTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerCommandTests.cs`, `MarkerEditorFixtures.cs`, `MapEditTool/MarkerServiceTests.cs`, `MarkerWireTests.cs`.
- Modify docs: MapDoc, MapEditor and MapEdit.Tool READMEs, including the verb table/count at MapEdit.Tool/README.md:311.

**Interfaces:**
- Consumes released `MapPrefabEditResult(MapDocument After, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects)` at Editing/MapPrefabEdits.cs:20, `MapNativeWriteSet.BuildingIntent`, `NativeResourceTransforms`, `Prefabs` and the released bound transaction/state pattern in NativeInteriorCommands.cs:9-23.
- Consumes internal `MapEditSession.ExecuteNativeCommand(EditorCommand command) -> MapNativeEditEffects` at MapEditSession.NativeHistory.cs:51, `MutationService.InteriorApply(MapInteriorEdit edit) -> MapPrefabMutationResult` at MutationService.Prefabs.cs:20, and the existing `MapPrefabMutationResult(MutationResult Mutation, MapNativeEditEffects Effects)`. Consumes Task 3 Describe and rename rows, and Task 4 registry and `InRect`.
- Produces `MapMarkerEdit(string Label)` abstract record, `MapSetMarker(MapLocalMarkerDoc Marker)` and `MapRemoveMarker(string Id)`, plus `MapMarkerEdits.Prepare(MapDocument document, MapAssetClosure assets, MapMarkerEdit edit) -> MapPrefabEditResult`.
- Produces `MarkerEditCommand(MapMarkerEdit edit) : EditorCommand, INativeBoundDocumentCommand`, `MutationService.MarkerApply(MapMarkerEdit edit) -> MapPrefabMutationResult` and `MapPrefabJson.ReadMarker(string json) -> MapLocalMarkerDoc` internal closed decoder sharing Task 1's marker binding converter.
- Produces `QueryService.MarkerList(string? name, string? role, string? tag, bool enabledOnly, MapMarkerRect? rect) -> IReadOnlyList<MapEffectiveMarker>` in the new partial. A rectangle filter runs Task 4's half-open `InRect`.
- Produces wire `MarkerTools.MarkerSet(string markerJson) -> MapPrefabMutationResult`, `MarkerRemove(string id) -> MapPrefabMutationResult`, `MarkerList(string? name = null, string? role = null, string? tag = null, bool enabledOnly = false, double? minX = null, double? minZ = null, double? maxX = null, double? maxZ = null) -> IReadOnlyList<MapEffectiveMarker>`, named `marker_set`, `marker_remove`, `marker_list`. The marker JSON's ID is the upsert key. Name is an exact optional filter, never a key or implicit rename. The four rectangle parameters are all present or all absent, otherwise the call refuses. No new verb.
- Resolver guard. On a native document whose resolver identity is (1, 1), `MarkerApply` refuses with a message containing `resolver adoption` and `MapResolverAdoption.ConvertToAuthoredSupport`, naming it as the prerequisite. `MarkerList` returns empty without expansion when the document has neither BuildingIntent nor Prefabs.

- [ ] **Step 1: Write failing tests.** `MarkerSet_ByIdKeepsIdentityAndExactHistory` starts with the payload-1 Basic point and sets payload-2 Explicit with name `arrival-new`, tags `["second","first","first"]`, enabled false and `Position.Y` 13.125. Apply through pure preparation, EditorHistory and session. Undo/redo must reproduce exact document bytes, payload version, height, tags and enabled state. Save/reload checks data, not persisted history. Rename changes the name, not ID `m`.

```csharp
Assert.Equal("m", marker.Id);
Assert.Equal(2, authored.PayloadVersion);
Assert.Equal(MapMarkerHeightMode.Explicit, authored.HeightMode);
Assert.Equal(new[] { "second", "first", "first" }, marker.Tags);
Assert.Equal(13.125f, marker.Position.Y.ToSingle());
Assert.False(marker.Enabled);
Assert.Equal(new MapMarkerNameChange("m", "arrival", "arrival-new"), Assert.Single(effects.MarkerNameChanges));
Assert.Equal(MapNativeInvalidation.Residency, effects.Invalidates);
Assert.Equal(new[] { "m" }, effects.MarkerIds);
Assert.Equal(MapNativeInvalidation.None, effects.Invalidates & (MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Unbounded));
```

`MetadataOnlyMarkerSet_HasIdAndRenameButNoBounds` edits name, role, tags and archetype independently. Assert Invalidates None, OldBounds/NewBounds null, MarkerIds `["m"]`, correct name changes only for the rename. Position/yaw/enabled/binding changes each get Residency and both resolved point boxes, including a binding change to equal-height support. Create/delete have exactly one null box. Never call prefab Finish unchanged.

`PrefabBoundMarkerSet_RefusesWithOverrideGuidance` addresses a live bound ID and asserts the message contains `prefab_override`. `TransferredSpaceBoundMarkerSet_KeepsFrameLocalBinding` continues Task 2's TransferredSpace after unpack and reload. A metadata `marker_set` keeps the stored binding bytes and precise frame verbatim. An absolute `marker_set` that moves the marker by world (0.5, 0, -0.25) stores the frame-local equivalent, the effective ReferenceY and searches equal the independently computed frame-applied values exactly once, and undo restores the exact prior bytes. A transferred standalone marker preserves transfer identity and retired reservation on remove. `RenameConflict_WritesNothing` compares MarkerEditorFixtures.State before/after rejection, including no history/event advance. Invalid height, duplicate ID/name and foreign ownership have positive controls.

`ResolverOneNative_MarkerVerbsNameResolverAdoption` loads released `FormatFourFixtures.Build()` (resolver (1, 1)) into a session and asserts `marker_set` and `marker_remove` refuse with `resolver adoption` and `MapResolverAdoption.ConvertToAuthoredSupport` in the message, without state advance, and `marker_list` returns empty.

`GuiServiceAndWire_HaveIdenticalMarkerDocumentsEffectsAndRejection` constructs the real command used by the GUI without booting a window, the actual service, and an MCP client. Exercise set, metadata rename, spatial move, disable, remove, undo/redo and a rejected duplicate name. Compare canonical documents, effects Describe, marker rows, identity and history labels. `MarkerListRect_WireMatchesService` uses `RectPoints()` and compares wire `marker_list` with minX -1, minZ -1, maxX 0, maxZ 0 against `QueryService.MarkerList` and an independent half-open predicate. It returns exactly the two expected IDs, excludes both max edges, and a call with only minX refuses. Wire tests use McpWire.CallAsync for each request and `[Collection(McpWireCollection.Name)]`, never one workflow-wide token. Inventory is the released 92 tools plus these three = 95.

- [ ] **Step 2: Run red** `wa_test t5-red-doc "$MAPDOC" "FullyQualifiedName~MapMarkerEditTests"`, then `wa_test t5-red "$EDITOR" "FullyQualifiedName~NativeMarkerCommandTests|FullyQualifiedName~MarkerServiceTests|FullyQualifiedName~MarkerWireTests|FullyQualifiedName~ToolRegistry_ExposesAllDocumentQueryMutationVerbs"`. Expect absent command/verbs, the pinned inventory still at 92, or overly broad effects.
- [ ] **Step 3: Implement** pure detached preparation, preserving unrelated BuildingIntent fields. Validate IDs, names, binding and post-edit geometry before returning. A native marker command requires the matching verified closure. Publish BuildingIntent plus only the transform/transfer roots actually changed. Reuse NativePrefabCommandState's accepted before/after preparation so rejection and retry do not consume history state. No raw generic callback substitute.

An absolute marker set on a transferred standalone marker reconciles its precise frame without rounding unchanged components, and stores a binding frame-local per the Task 2 frame rule. Metadata-only updates retain that frame verbatim. Remove retires its active transfer and removes only its own transform. Add/set cannot seize a dormant reservation. This lifecycle remains within released reservation rules, with no hidden ID reuse or allocation.

Return narrow effects from Task 3's shared diff and prevent later generic resource combination from converting metadata to Residency/Unbounded. Register MarkerTools with MapPrefabJson.ToolOptions. Update pinned inventory and README count together to 95. GUI inspector widgets and the actual native editor host remain R9, this task proves the command path they invoke.

- [ ] **Step 4: Run green** with both Step 2 filters as `t5-green-doc` and `t5-green`. Run `wa_sweep t5-final` after review/fixes.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t5_paths=(
    KhaozEngine.MapDoc/Editing/MapMarkerEdits.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapEditor/NativeMarkerCommands.cs
    KhaozEngine.MapEdit.Tool/MutationService.Markers.cs
    KhaozEngine.MapEdit.Tool/QueryService.Markers.cs
    KhaozEngine.MapEdit.Tool/QueryService.cs
    KhaozEngine.MapEdit.Tool/Tools/MarkerTools.cs
    KhaozEngine.MapEdit.Tool/Tools/McpBootstrap.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerEditTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerCommandTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/MarkerEditorFixtures.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/MarkerServiceTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/MarkerWireTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/McpAdapterTests.cs
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapEditor/README.md
    KhaozEngine.MapEdit.Tool/README.md
)
wa_finish t5 "${wa_r6_t5_paths[@]}" &&
git add -- "${wa_r6_t5_paths[@]}" &&
git commit --only -m "feat(mapedit): add shared marker commands and MCP verbs" -- "${wa_r6_t5_paths[@]}"
```

### Task 6: Explicit legacy spawn adoption and native write fence

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerAdoption.cs`, `KhaozEngine.MapEditor/NativeMarkerAdoptionCommands.cs`, `NativeLegacySpawnAdmission.cs`, `KhaozEngine.MapEdit.Tool/MutationService.MarkerAdoption.cs`.
- Modify: `KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs:12-27`, `KhaozEngine.MapEditor/NativeDocumentSnapshot.cs` named publication, `NativeDocumentTransaction.cs:32-33` (staged record), `:53-57` (`Publish`) and `:60-104` (`Stage`), `KhaozEngine.MapEdit.Tool/MapEditSession.NativeHistory.cs:41-49,74-115` (callback staging and `PublishNative`), `KhaozEngine.MapEdit.Tool/MutationService.cs:29-45` and existing spawn entry points only, `Tools/MarkerTools.cs` from Task 5, `Tools/MutationTools.cs:69-140,403-430` descriptions, `Prefabs/MapPrefabJson.cs` adoption decoder.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MarkerAdoptionFixtures.cs`, `MapMarkerAdoptionTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerAdoptionTests.cs`, `NativeLegacySpawnFenceTests.cs`, `MapEditTool/MarkerAdoptionWireTests.cs`.
- Test/modify: `KhaozEngine.MapEditor.Tests/MapEditTool/McpAdapterTests.cs`, `NativeGenericHistoryTests.cs:118`, changing only the mutation of `GenericTiledEdit_UsesLoadedContentRatherThanStoredTileHashes` from a player-spawn add to a standalone placement add (`generic-item`, the kind and asset ID copied from the fixture's effective leaf placement, X 2, Z -2, explicit Y 0), the remaining tile-bucketed kind a native generic callback may add. Its history, presence and undo assertions keep their shape over that placement. If the released generic path refuses that add, stop and report rather than choosing another record.
- Modify docs: MapDoc, MapEditor and MapEdit.Tool READMEs. MutationService.cs is baselined at 1450 lines and must not grow. Move one cohesive released spawn dispatch concern into the new partial if needed, keeping public signatures. If it shrinks, ratchet `.filesize-baseline:42` down with `wa_run t6-ratchet sh scripts/check-file-size.sh --update`, inspect that only task-owned baseline entries changed, and include the baseline in this commit.

**Interfaces:**
- Consumes released MapSpawn fields Id, ArchetypeId, X, Z, Enabled, Tags and MapPlayerSpawn fields Id, X, Z, Yaw, Enabled, Tags at MapDocument.cs:183-207. Neither stores Y and both are mutable classes without value equality. Consumes Task 1 `MapMarkerHeight` and `MapMarkerHeights.Apply`, and Task 5 edit/result shapes.
- Consumes released `MapResolverAdoption.ConvertToAuthoredSupport(MapDocument document, IReadOnlyDictionary<string, MapPlacementSupportChoice> choices) -> MapDocument` at Support/MapResolverAdoption.cs:13 as the detached-copy precedent and as the named prerequisite for resolver (1, 1) documents, not as a spawn converter. Consumes released routing at MutationService.cs:34, where any resolver identity takes the native path.
- Produces `MapLegacySpawnKind { Spawn, PlayerSpawn }`, `MapLegacySpawnKey(MapLegacySpawnKind Kind, string Id)`, `MapMarkerAdoptionChoice(string MarkerId, string Name, string Role, MapMarkerHeight Height, float? YawRadians = null)` and `MapMarkerAdoption.ConvertSpawns(MapDocument document, IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> choices) -> MapDocument`.
- Produces `AdoptSpawnsCommand(IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> choices) : EditorCommand, INativeBoundDocumentCommand`, with one captured accepted before/after state, plus `MutationService.SpawnsAdopt(IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> choices) -> MapPrefabMutationResult`.
- Produces wire `MarkerTools.SpawnsAdopt(string choicesJson) -> MapPrefabMutationResult`, named `spawns_adopt`. JSON is a closed array of `{kind,id,markerId,name,role,height,yawRadians?}` rows, where `height` is `{explicitY}` or `{supportBinding}` with the Task 1 marker binding shape. Unknown/duplicate fields and duplicate source keys refuse before dictionary construction. Produces internal `MapPrefabJson.ReadMarkerAdoption(string json) -> IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice>`.
- Adds `MapNativeWriteSet.LegacySpawns: bool`, default false, for both legacy lists together. Only the internal adoption command admits changed legacy lists, including its exact undo restoration. Generic native snapshots cannot use that permission. Legacy-list change detection compares canonical bytes of both lists written through the released MapDocumentFile codec, never reference or class equality.
- Produces internal `NativeLegacySpawnPermit(string BeforeLists, string AfterLists)` and init-only `NativeStagedPublication.LegacySpawns: NativeLegacySpawnPermit?`, default null. `NativeDocumentTransaction.Stage` sets it only for `AdoptSpawnsCommand`, and only when the live document's canonical lists equal the command's captured before (after, for undo) and the publication's equal its captured after (before, for undo). The permit holds immutable canonical strings, so later edits to shared list objects cannot change it.
- Produces internal `NativeLegacySpawnAdmission.Canonical(MapDocument document) -> string` and `RequireAdmitted(MapDocument live, MapDocument candidate, NativeLegacySpawnPermit? permit) -> void`. Equal canonical lists pass. Changed lists pass only when the permit's BeforeLists equals the live bytes and its AfterLists equals the candidate bytes. Anything else throws with `Guidance(live)` before any write.
- Produces internal `NativeLegacySpawnAdmission.Guidance(MapDocument document) -> string`. Resolver (1, 2) documents name `marker_set`, `marker_remove` and `spawns_adopt`. Resolver (1, 1) documents name resolver adoption through `MapResolverAdoption.ConvertToAuthoredSupport` as the first step, then the marker verbs.

- [ ] **Step 1: Write failing tests.** `SameSourceIdInTwoLists_RequiresDistinctExplicitDestinations` uses a valid monolithic native document with NPC and player source ID `old`, X -0.25, Z -1.5, NPC archetype `kit.actor`, tags `["second","first"]`, disabled NPC, player yaw 0.371. LegacyLists authors support `adoption-floor` at Y 20 covering X/Z [-2,2], with no competing support. Choose destination IDs `actor-new` and `start-new`, names `actor-home` and `player-home`, caller roles, explicit Y 12.25 and Surface-bound adoption-floor respectively. Missing/extra choices, duplicate destinations/names, existing live or reserved marker IDs and both/neither height branches refuse without mutation. A third choice set gives the NPC explicit yaw 1.25.

```csharp
Assert.Equal(2, original.Spawns.Count + original.PlayerSpawns.Count);
Assert.Empty(adopted.Spawns);
Assert.Empty(adopted.PlayerSpawns);
Assert.Equal("kit.actor", actor.ArchetypeId);
Assert.False(actor.Enabled);
Assert.Equal(new[] { "second", "first" }, actor.Tags);
Assert.Equal(0f, actor.YawRadians);
Assert.Equal(1.25f, actorWithYaw.YawRadians);
Assert.Equal(0.371f, player.YawRadians);
Assert.Equal(MapMarkerHeightMode.Explicit, actorDoc.HeightMode);
Assert.Equal(12.25f, actor.Position.Y.ToSingle());
Assert.Equal(MapMarkerHeightMode.Support, playerDoc.HeightMode);
Assert.Equal(0f, playerDoc.Position.Y);
Assert.Equal(20f, player.Position.Y.ToSingle());
Assert.Equal(originalBytes, MapDocumentFile.SaveText(original));
```

`Adoption_UndoRedoRestoresExactListsMarkersAndIdentity` executes one command, checks one history entry and one event, clears both lists, undoes to the exact source records by canonical bytes and restores the same marker IDs/height modes on redo. Support changes after adoption reseat only bound markers. `AdoptionEqualsExplicitMarkerCommands` compares complete adopted documents/effective rows with marker_set construction from the same choices after deliberately clearing source lists on a detached expected document.

`NativeLegacySpawnWrites_RefuseAcrossEveryEntry` in `NativeLegacySpawnFenceTests` drives all five spawn verbs, all six player_spawn verbs, GUI legacy commands, element_duplicate for both kinds, session generic callbacks (including the player-spawn add the released generic history test used) and complete snapshot commands. Every native attempt on a resolver (1, 2) document refuses with a message containing `marker_set`, `marker_remove` and `spawns_adopt` and not containing `resolver adoption`, with no state advance. Unchanged old lists continue loading, saving and surviving unrelated marker edits. `ResolverOneNative_AdoptionAndSpawnVerbsNameResolverAdoption` uses `FormatFourFixtures.Build()` and asserts `spawns_adopt` and a spawn add each refuse with `resolver adoption` and `MapResolverAdoption.ConvertToAuthoredSupport` in the message, without state advance. `AnalyticSpawnVerbs_KeepReleasedBehavior` performs add/move/rename/enable/yaw/remove and undo/redo on analytic maps with their existing signatures and results.

`LegacyListsChangedAfterStaging_RefuseAtPublication` in `NativeLegacySpawnFenceTests` uses LegacyLists in a session. Inside the public `session.Mutate((doc, registry) => { session.ApplyNative(command, doc, registry); change(doc); return 0; }, true)` it stages a permitted command, either `MarkerEditCommand(new MapSetMarker(probe))` for a new explicit marker `probe` or `AdoptSpawnsCommand(Choices())`, then applies one change: `doc.Spawns.Add(late NPC record)`, `doc.PlayerSpawns.Add(late player record)`, or an in-place `X += 1` on the first remaining element of each list when one exists. Every combination throws `MapDocumentException` whose message contains `spawns_adopt`, with `MarkerEditorFixtures.State` unchanged, so document, closure, history, events and dirty state are untouched. Controls: the same two commands with no change publish, and undo then redo of an accepted adoption publish the exact captured lists through `UndoNative` and `RedoNative`.

```csharp
Assert.Contains("spawns_adopt", lateRecord.Message, StringComparison.Ordinal);
Assert.Equal(stateBefore, MarkerEditorFixtures.State(session));
Assert.Equal(sourceListBytes, NativeLegacySpawnAdmission.Canonical(afterUndo));
Assert.Equal(adoptedListBytes, NativeLegacySpawnAdmission.Canonical(afterRedo));
```

- [ ] **Step 2: Run red** `wa_test t6-red-doc "$MAPDOC" "FullyQualifiedName~MapMarkerAdoptionTests"`, then `wa_test t6-red "$EDITOR" "FullyQualifiedName~NativeMarkerAdoptionTests|FullyQualifiedName~NativeLegacySpawnFenceTests|FullyQualifiedName~MarkerAdoptionWireTests|FullyQualifiedName~ToolRegistry_ExposesAllDocumentQueryMutationVerbs"`. Expect absent adoption, a generic native callback still accepting new legacy records, a post-stage list change reaching publication, guidance without marker verbs, and the inventory still at 95. The coverage-preserving change to `GenericTiledEdit_UsesLoadedContentRatherThanStoredTileHashes` is recorded as a passing regression in `t6-compat`.
- [ ] **Step 3: Implement** detached native-only adoption. Key choices by kind plus source ID. Require exact exhaustive coverage and validate all destinations against the full marker/reservation inventory before constructing output. Preserve source XZ, ordered tags, enabled and archetype. Use the choice's yaw when present, otherwise copy player yaw and use NPC yaw 0. Encode height through `MapMarkerHeights.Apply` without sampling in this pure API. Structural output validation runs here, closure/support/name validation runs in bound command preparation before acceptance. Clear both source lists only after every record converts. Partial documents refuse. Analytic adoption refuses and leaves released analytic editing available. Resolver (1, 1) documents refuse with the resolver guidance.

Publish both lists and BuildingIntent atomically through LegacySpawns. `Stage` calls `RequireAdmitted(document, publication, permit)` for early refusal. `NativeDocumentTransaction.Publish` calls it again as its first statement against the target document and `staged.Candidate`, so every publication path, including GUI history, session undo and session redo, is checked immediately before writing. `MapEditSession.PublishNative` calls it before its revalidation, hooks and checkpoint, so a refusal runs no hook. `MutateNative` also refuses when, after the callback returns, the callback document's canonical lists differ from the staged candidate's, so a post-stage edit to an unshared list is refused rather than silently dropped. No public generic flag authorizes a bypass. Undo can restore the exact old records, not invent new ones. Keep load-time validation permissive for old native lists. Add `NativeLegacySpawnAdmission.Guidance` at native spawn entry points before the old generic atomic-command refusal. Preserve analytic behavior and all released method signatures.

Update inventory and README together from 95 to 96 tools. Wire adoption tests use the same closed choices and command as the GUI/service path and W-R1 per-call timeout/collection.

- [ ] **Step 4: Run green** with both Step 2 filters as `t6-green-doc` and `t6-green`, then `wa_test t6-compat "$EDITOR" "FullyQualifiedName~NativeGenericHistoryTests|FullyQualifiedName~Spawn"`. Run `wa_sweep t6-final` after review/fixes.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t6_paths=(
    KhaozEngine.MapDoc/Markers/MapMarkerAdoption.cs
    KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapEditor/NativeMarkerAdoptionCommands.cs
    KhaozEngine.MapEditor/NativeLegacySpawnAdmission.cs
    KhaozEngine.MapEditor/NativeDocumentSnapshot.cs
    KhaozEngine.MapEditor/NativeDocumentTransaction.cs
    KhaozEngine.MapEdit.Tool/MapEditSession.NativeHistory.cs
    KhaozEngine.MapEdit.Tool/MutationService.MarkerAdoption.cs
    KhaozEngine.MapEdit.Tool/MutationService.cs
    KhaozEngine.MapEdit.Tool/Tools/MarkerTools.cs
    KhaozEngine.MapEdit.Tool/Tools/MutationTools.cs
    KhaozEngine.MapDoc.Tests/Markers/MarkerAdoptionFixtures.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerAdoptionTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerAdoptionTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeLegacySpawnFenceTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/MarkerAdoptionWireTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/McpAdapterTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/NativeGenericHistoryTests.cs
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapEditor/README.md
    KhaozEngine.MapEdit.Tool/README.md
    .filesize-baseline
)
wa_finish t6 "${wa_r6_t6_paths[@]}" &&
git add -- "${wa_r6_t6_paths[@]}" &&
git commit --only -m "feat(mapedit): adopt legacy spawns through one explicit native command" -- "${wa_r6_t6_paths[@]}"
```

### Task 7: Consumer queries, window anchors and tool readouts

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerAnchors.cs`, `MapNativePointReadout.cs`, `KhaozEngine.MapEdit.Tool/MapEditSession.Markers.cs`.
- Modify: `KhaozEngine.MapEditor/MapDocumentWindowing.cs:35-88`, `KhaozEngine.MapEdit.Tool/MapEditSession.cs:87-112,484-497,560-580`, Task 5's `QueryService.Markers.cs`, `QueryService.cs:43-75` delegation only, `Results.cs:62-77,91-104`, `Tools/DocumentTools.cs:14-18,42-44`, `KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs:89-96` for closed consumer options. `KhaozEngine.MapEdit.Tool/NativeDocumentService.cs` stays unchanged, including its partial-document refusal at :56-62.
- Test/create: `KhaozEngine.MapEditor.Tests/MapEditor/MarkerConsumerFixtures.cs`, `NativeMarkerConsumerTests.cs`, `MapEditTool/MarkerConsumerWireTests.cs`. Extend the released `MapDocumentWindowingSpawnTests.cs` and `MapWindowSpawnSearchTests.cs` with native controls, retaining analytic tests.
- Modify docs: MapDoc and MapEdit.Tool READMEs.

**Interfaces:**
- Consumes Task 2 metadata Capture, exact membership and support, Task 4 typed views. Consumes released `MapDocumentWindowing.Load(string path, MapDocumentLoadOptions options, int wholeWorldTileLimit, int windowRadius, int playerSpawnSearchTileLimit, out bool windowed, out MapTileRect? window) -> MapDocument` at :45, `MapEditSession.Open(string path, IReadOnlyList<string>? manifestPaths = null) -> OpenResult` at MapEditSession.cs:87, `QueryService.PlacementsInRect(float minX, float minZ, float maxX, float maxZ) -> PlacementsInRectResult` at QueryService.cs:47, `MapSummary` and `NativeDocumentSummary` at Results.cs:62-81, and `MapNativeResolution.Resolve(MapDocument document, MapAssetClosure assets, MapResolveOptions options, Func<float,float,float>? legacySupportHeight = null) -> MapResolvedDocument` at MapNativeResolution.cs:22.
- Produces `MapMarkerConsumerRoles(string PlayerStartRole, string SpawnRole, string LandmarkRole)`, `MapMarkerConsumerOptions(MapMarkerConsumerRoles? Roles, string? LegacySpawnSurfaceId)`, `MapMarkerAnchor(string Id, MapExactXz Position, bool Enabled, string Role)`, `MapMarkerAnchors.Read(MapDocument manifest, MapAssetClosure assets) -> IReadOnlyList<MapMarkerAnchor>` and the pure selector `MapMarkerAnchors.Select(IReadOnlyList<MapMarkerAnchor> anchors, string role, MapExactXz centre) -> MapMarkerAnchor?`, all in MapMarkerAnchors.cs. Read uses Capture's precise XZ from root metadata and verified prefab bytes, with no support, terrain or tile-body read, not a full Resolve or independent transform. Select keeps enabled anchors whose role equals the caller's ordinally, orders by exact squared distance to `centre`, then ordinal ID, and returns null when none match.
- Produces `MapDocumentWindowing.Load(string path, MapDocumentLoadOptions options, int wholeWorldTileLimit, int windowRadius, int playerSpawnSearchTileLimit, out bool windowed, out MapTileRect? window, string playerStartRole, Func<MapDocument, IReadOnlyList<MapMarkerAnchor>> nativeAnchors) -> MapDocument`. For a native manifest it centres the window on `Select(nativeAnchors(manifest), playerStartRole, boundsCentre)`, and with no match it keeps the released fallback (bounds centre, then the released legacy player-spawn search). Existing overloads retain analytic behavior. Whole loads are unchanged. This only chooses a window. It opens no session and certifies nothing.
- Produces public `MapNativePointReadout.SurfaceAt(MapEffectiveResources resources, MapExactXz point, string surfaceId) -> MapSupportResult`, sampling the named owner through the released Surface path with existing scoped budgets. A Surface sample uses the owner directly with no reference height (MapSupportQuery.cs:27). Non-Supported results remain typed.
- Adds `MapEditSession.Open(string path, IReadOnlyList<string>? manifestPaths, MapMarkerConsumerOptions markerOptions) -> OpenResult`, retaining the released overload. Stage options atomically with Open. It passes the caller's start role and a metadata anchor callback to the windowing overload, then runs the released `NativeDocumentService.Verify`, so a partial native result keeps its released refusal and the session keeps its prior document, options and history. Produces internal `MapPrefabJson.ReadMarkerOptions(string json) -> MapMarkerConsumerOptions`, closed and duplicate-safe, with `legacySpawnSurfaceId` the only support member. Preserve the released C# DocumentTools.MapOpen forwarding method, register only `MapOpenWithMarkers(string path, string[]? manifestPaths = null, string? markerOptionsJson = null) -> OpenResult` as wire map_open. No engine role defaults and no duplicate registration.
- Produces `MapMarkerSummary(int Total, int Enabled, string? PlayerStartRole, IReadOnlyList<string> PlayerStartIds, IReadOnlyList<string> EnabledPlayerStartIds)` and additive init-only `MapSummary.Markers: MapMarkerSummary?`. Analytic documents leave it null. A native session is always whole-loaded in R6, and its summary fills Markers from the same complete validation as `MapSummary.Native`. Existing list counts remain explicitly documented legacy counts.
- `QueryService.PlacementsInRect` keeps its released signature and inclusive rectangle. Its native branch in QueryService.Markers.cs reads placements from actual native resolution and old native spawns through the session's `LegacySpawnSurfaceId`. SpawnEntry/PlayerSpawnEntry gain init-only nullable `MapSupportResult? Support`, default null, preserving constructors. Without a configured Surface owner, the native branch refuses with marker and adoption guidance.

- [ ] **Step 1: Write failing tests.** `MarkerConsumerFixtures.WindowedNative(root)` writes a valid native tiled fixture over the windowing limit spanning negative storage tiles, with a prefab-local enabled `start-role` marker `start-a` whose exact X is -64.25 and whose support tile is outside the bounds-centre window, a disabled `start-disabled`, and a standalone `start-role` point placed for a stable distance/ID tie.

`NativeStartAnchor_SelectsFromMetadataWithUnreadableBodies` is the pure selector test. It writes the fixture, then `CorruptBodies(root)` overwrites every terrain and support tile body with invalid bytes, leaving the manifest and prefab payloads intact. It reads the manifest through `MapDocumentSource.OpenTiled(root, options).Manifest`, loads the closure through `MapAssetClosure.Load(manifest.NativeAssets, new MapStorageGuardedAssetSource(Path.GetFullPath(root), MapDocumentForm.Tiled))`, and calls `MapMarkerAnchors.Select(MapMarkerAnchors.Read(manifest, closure), "start-role", boundsCentre)`. It selects `start-a` in tile X -2 for TileSize 64, not 0 or -1. The released whole load of the corrupted root throws (failure control). Missing prefab bytes refuse. Disabled starts are skipped. An unknown role returns null, never a game-name guess. The tie resolves by ordinal ID.

```csharp
Assert.True(CorruptBodies(root) > 0);
Assert.Equal("start-a", selected!.Id);
Assert.Equal(-2, MapTileGrid.CoordOf((float)selected.Position.X.ToDouble(), (float)selected.Position.Z.ToDouble(), 64).X);
Assert.ThrowsAny<Exception>(() => MapDocumentFile.LoadTiled(root, options));
Assert.Null(MapMarkerAnchors.Select(anchors, "unknown-role", boundsCentre));
```

`NativeWindowing_SelectsMarkerTileAndOpenKeepsRefusal` uses the same fixture with valid bodies. The new windowing overload with role `start-role` returns a window whose centre tile X is -2. The released overload on the same root keeps its released bounds-centre window (analytic-convention control). `MapEditSession.Open(root, null, new(roles, null))` on the windowed root throws the released `Windowed native editing is not supported yet` refusal and leaves `MarkerEditorFixtures.State` unchanged. Raising `WholeWorldTileLimit` so the root loads whole lets the same Open succeed with `start-a` in its marker summary.

```csharp
Assert.True(windowed);
Assert.Equal(-2, (window!.Value.Min.X + window.Value.Max.X) / 2);
Assert.Contains("Windowed native editing is not supported yet", windowedOpen.Message, StringComparison.Ordinal);
Assert.Equal(stateBefore, MarkerEditorFixtures.State(session));
Assert.Equal(new[] { "start-a" }, wholeOpen.Summary.Markers!.EnabledPlayerStartIds);
```

`NativeWholeLoadReadouts_UseNamedSupport` opens an uncorrupted whole native document through `Open(path, manifests, new(roles, "adoption-floor"))`. An analytic field height of 900 and a native support height of 20 coexist, and a different upper floor at 70 never wins. Check bound placements through actual resolved placement transforms, old native spawns through the named Surface owner and native marker starts through typed views. The same open without a Surface owner refuses the spawn readout with marker and adoption guidance. Analytic controls keep their exact old readouts and inclusive rectangle edges.

```csharp
Assert.Equal(new[] { "start-a", "start-disabled" }, summary.Markers!.PlayerStartIds);
Assert.Equal(new[] { "start-a" }, summary.Markers.EnabledPlayerStartIds);
Assert.Equal("start-role", summary.Markers.PlayerStartRole);
Assert.Equal(20f, nativePlacement.Y);
Assert.Equal(20f, nativeSpawn.GroundY);
Assert.Equal(MapSupportStatus.Supported, nativeSpawn.Support!.Status);
Assert.Contains("spawns_adopt", missingChoiceMessage);
```

`MarkerSummary_WireMatchesService` compares wire `map_summary.Markers` and disabled IDs with the service on a whole load, and wire `map_open` on the windowed native root with the service's released refusal text, with W-R1. The marker rectangle itself is covered by Task 5's `MarkerListRect_WireMatchesService`. The C# lookup exposes name, exact point, ordered tags and membership to the world-map consumer, without adding a #1370 adapter here.

- [ ] **Step 2: Run red** `wa_test t7-red "$EDITOR" "FullyQualifiedName~NativeMarkerConsumerTests|FullyQualifiedName~MarkerConsumerWireTests|FullyQualifiedName~MapDocumentWindowingSpawnTests|FullyQualifiedName~MapWindowSpawnSearchTests"`. Expect a missing selector or windowing overload, bounds-centre anchoring, an absent marker summary or analytic native GroundY. The refusal and whole-load-failure controls already pass and stay as controls.
- [ ] **Step 3: Implement** the pure selector and root metadata anchor selection before loading the final window. The callback loads verified closure metadata under the already declared storage root and containment rules, not a developer checkout search. Reuse MapMarkerExpansion.Capture, charge existing prefab preflight and preserve missing-byte refusal. Compare distances using exact/wide or binary64 differences, never integer truncation of negative tiles. Window selection does not resolve height, certify whole-world identity or relax the tool's existing complete-native-editing requirement. Do not touch `NativeDocumentService`. Windowed native editing stays R9.

Place native query branches in QueryService.Markers.cs. Read placements from actual native resolution, including effective prefab placements. Adopted marker starts are reported in the marker summary/views without recreating old lists. Cache only against the accepted document/closure identity, with existing session locking and invalidation. Document the two rectangle conventions and the caller-owned role and Surface configuration.

- [ ] **Step 4: Run green** with Step 2's filter as `t7-green`, then `wa_sweep t7-final` after review/fixes. No windowed app is launched.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t7_paths=(
    KhaozEngine.MapDoc/Markers/MapMarkerAnchors.cs
    KhaozEngine.MapDoc/Markers/MapNativePointReadout.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapEditor/MapDocumentWindowing.cs
    KhaozEngine.MapEdit.Tool/MapEditSession.Markers.cs
    KhaozEngine.MapEdit.Tool/MapEditSession.cs
    KhaozEngine.MapEdit.Tool/QueryService.Markers.cs
    KhaozEngine.MapEdit.Tool/QueryService.cs
    KhaozEngine.MapEdit.Tool/Results.cs
    KhaozEngine.MapEdit.Tool/Tools/DocumentTools.cs
    KhaozEngine.MapEditor.Tests/MapEditor/MarkerConsumerFixtures.cs
    KhaozEngine.MapEditor.Tests/MapEditor/NativeMarkerConsumerTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/MarkerConsumerWireTests.cs
    KhaozEngine.MapEditor.Tests/MapEditor/MapDocumentWindowingSpawnTests.cs
    KhaozEngine.MapEditor.Tests/MapEditor/MapWindowSpawnSearchTests.cs
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapEdit.Tool/README.md
)
wa_finish t7 "${wa_r6_t7_paths[@]}" &&
git add -- "${wa_r6_t7_paths[@]}" &&
git commit --only -m "feat(mapedit): expose native marker starts and support-correct tool readouts" -- "${wa_r6_t7_paths[@]}"
```

### Task 8: Editor GUI readouts

**Files:**
- Create: `KhaozEngine.MapEditor/NativeMarkerReadouts.cs`, `SpawnBillboards.cs`, `MapEditorScene.Markers.cs`.
- Modify: `KhaozEngine.MapEditor/MapEditorOptions.cs:100-117`, `MapEditorScene.Document.cs:23-42`, `MapEditorScene.Reload.cs` load/replacement call, `MapEditorScene.Window.cs:18-36`, `MapEditorScene.Navigation.cs:108-125`, `ViewportWorld.cs:47-48,577-606` (lift constant and the two spawn draw loops), `EditorPicking.cs:97-125` (spawn and player-spawn pick boxes). `MapEditorScene.cs` viewport construction may pass the new provider by replacing the existing line, without growing its 2955-line baseline.
- Test/create: `KhaozEngine.MapEditor.Tests/MapEditor/NativeMarkerReadoutTests.cs`.
- Modify docs: MapEditor README. Any cohesive move that shrinks a baselined file ratchets only that entry down through `wa_run t8-ratchet sh scripts/check-file-size.sh --update`, followed by inspection of `.filesize-baseline`.

**Interfaces:**
- Consumes Task 7 `MapMarkerConsumerOptions`, `MapMarkerAnchors.Select`, `MapNativePointReadout.SurfaceAt` and the windowing overload, Task 4 typed views and Task 3 `NativeAcceptedExpansions`. Consumes released `ViewportWorld.SpawnMarkerLift` 1.0 at ViewportWorld.cs:48, the spawn focus lift 0.75 at MapEditorScene.Navigation.cs:117-122, `EditorNavigationController.Pivot` and `Frame(Vector3 pivot, float distance)` at EditorNavigationController.cs:29,140, and the headless `MapEditorScene` input-frame pattern of `MapEditorSceneNavigationTests`.
- Produces `NativeMarkerReadouts(MapDocument document, MapAssetClosure assets, MapMarkerConsumerOptions options)` in MapEditor, with `PlayerStarts(bool enabledOnly) -> IReadOnlyList<MapPlayerStartView>`, `LegacySpawnHeight(float x, float z) -> MapSupportResult` and `Diagnostic: string?`. It owns one immutable effective snapshot per accepted whole document, refreshed on edit/undo/redo/reload, never per billboard. On a partial document it holds no snapshot and reports a diagnostic.
- Produces the GPU-free seam in SpawnBillboards.cs. `internal readonly record struct SpawnBillboard(SelectionKind Kind, string Id, Vector3 Position, bool Enabled)`, `internal sealed record SpawnBillboardFrame(IReadOnlyList<SpawnBillboard> Billboards, IReadOnlyList<string> Diagnostics)`, and `internal static class SpawnBillboards` with `const float Lift = 1.0f`, `const float FocusLift = 0.75f`, `Build(MapDocument document, EditorVisibility visibility, Func<float, float, float> analyticGround, NativeMarkerReadouts? native) -> SpawnBillboardFrame` and `FocusGround(MapDocument document, SelectionKind kind, string id, Func<float, float, float> analyticGround, NativeMarkerReadouts? native, out string? diagnostic) -> float?`. Build applies the released group and element visibility gates. Analytic documents use `analyticGround(x, z) + Lift`. Resolver-2 documents use `native.LegacySpawnHeight(x, z).WorldY + Lift` only when Supported, otherwise they omit the row and add a diagnostic naming `spawns_adopt`.
- Produces `internal static void ViewportWorld.SubmitSpawnBillboards(SpawnBillboardFrame frame, Action<Vector3, float, Color> submit)`, which submits each row's position, the released size 0.6 and the released enabled, disabled and player colors. Declares `internal SpawnBillboardFrame ViewportWorld.SpawnFrame(EditorVisibility visibility)`, returning `SpawnBillboards.Build(_doc!, visibility, _field!.SampleHeight, SpawnReadouts())`. `DrawSpawnMarkers` and `DrawPlayerSpawnMarkers` are replaced by exactly `SubmitSpawnBillboards(SpawnFrame(visibility), (p, s, c) => _scene.DrawBillboard(p, s, c))`, so the viewport has no other spawn height source. The spawn and player-spawn pick boxes in EditorPicking.cs:97-125 take their height from the same `SpawnBillboardFrame`, so draw and pick stay in lockstep. Adds `ViewportWorld.SpawnReadouts: Func<NativeMarkerReadouts?>`, default `() => null`. `SpawnMarkerLift` moves to `SpawnBillboards.Lift` with the same value.
- `TrySelectionFrame`'s Spawn and PlayerSpawn cases take their ground from `SpawnBillboards.FocusGround` and add `FocusLift`. A null ground suppresses focus and sets the status text to the diagnostic. Adds `internal Vector3? NavigationPivot => _navigation.Pivot` in MapEditorScene.Markers.cs.
- `WindowCameraStart` on a whole-loaded native document with readouts starts at the enabled start of the caller's role chosen by `MapMarkerAnchors.Select` over those starts and the bounds centre, at its effective XYZ plus the released offset (0, 24, -32). With no match it keeps the released start. A windowed GUI load keeps the released windowed convention, now centred by Task 7's selector, and starts the camera at the selected anchor's XZ with Y 0 plus the released offset, as the released windowed spawn branch does. Its native readouts are unavailable with a diagnostic. GUI edits on a partial native document keep their released refusal.
- Adds optional `MapEditorOptions.MarkerOptions: MapMarkerConsumerOptions?` and `Func<MapDocument, MapAssetClosure>? MarkerAssets`. A native GUI readout needs the supplied verified closure, otherwise it shows unavailable with a developer diagnostic, never analytic fallback. A host may supply these without Physics. R9 owns the complete real GUI host and bounds integration.

- [ ] **Step 1: Write failing tests.** `NativeGuiReadouts_UseNamedSupportAndNeverTerrainField` uses the Task 7 whole-load fixture. It asserts the support answer first, then presentation as support plus the released lift. Billboards come from `SpawnBillboards.Build(document, visibility, (x, z) => 900f, readouts)`, and a recording delegate passed to `ViewportWorld.SubmitSpawnBillboards` observes exactly what the viewport submits, with no GPU device or Scene3D. Camera focus is driven through a headless `MapEditorScene` subclass in the `MapEditorSceneNavigationTests` pattern. It injects the native document and `MarkerOptions`, selects the legacy spawn through `EditorSelection.Set(SelectionKind.Spawn, id)`, presses Key.F through an input frame and reads `NavigationPivot`. The upper floor at 70 and the analytic 900 never win. Without a Surface owner the legacy spawn row is omitted, focus leaves the pivot unchanged and both report a diagnostic. In the same headless scene test, after the native load with `MarkerOptions`, the scene viewport's `SpawnFrame` has a row at 21 when readouts are configured, and an omitted row with its diagnostic when they are not. A headless pick assertion shows the spawn pick box sits at the drawn height 21 and not at the analytic 900. The analytic control submits exactly the released `groundY + 1.0` rows.

```csharp
Assert.Equal(20f, readouts.LegacySpawnHeight(spawn.X, spawn.Z).WorldY);
Assert.Equal(20f + SpawnBillboards.Lift, billboard.Position.Y);
Assert.Equal(frame.Billboards.Select(b => b.Position), submitted.Select(s => s.Position));
Assert.Contains(submitted, s => s.Position.Y == 21f);
Assert.All(submitted, s => Assert.Equal(0.6f, s.Size));
Assert.Equal(20f + SpawnBillboards.FocusLift, scene.NavigationPivot!.Value.Y);
Assert.Equal(20.75f, scene.NavigationPivot.Value.Y);
Assert.DoesNotContain(unconfigured.Billboards, b => b.Id == spawn.Id);
Assert.Contains("spawns_adopt", Assert.Single(unconfigured.Diagnostics));
Assert.Equal(pivotBefore, unconfiguredScene.NavigationPivot);
Assert.Equal(analyticGround + 1.0f, analyticRow.Position.Y);
```

`WholeNativeLoad_StartsCameraAtCallerStart` loads the whole-load fixture with `MarkerOptions` roles and asserts `scene.Camera.Position` equals `start-a`'s effective XYZ plus (0, 24, -32), and that an unknown role keeps the released start. `MarkerConsumers_RefreshAfterUndoRedoAndReload` moves a bound marker, changes support and undoes/redoes, checking no cached start, billboard row or focus ground survives from the previous snapshot. `GuiWindow_UsesCallerStartRole` opens the Task 7 windowed fixture through MapEditorScene's window path with `MarkerOptions` roles, and asserts the same window centre tile X -2, that `scene.Camera.Position` is the selected anchor's XZ with Y 0 plus (0, 24, -32), a readout diagnostic for the partial document and no native billboard rows.

- [ ] **Step 2: Run red** `wa_test t8-red "$EDITOR" "FullyQualifiedName~NativeMarkerReadoutTests"`. Expect compilation failure only on the task-owned seam and readout APIs, then analytic GroundHeight billboards and focus, the released whole-load start or a bounds-centre window at the named assertions.
- [ ] **Step 3: Implement** NativeMarkerReadouts bound through the new focused scene partial for initial load/reload and accepted snapshot changes, set as the viewport's `SpawnReadouts` provider. Route both spawn draw loops through `ViewportWorld.SpawnFrame` and `SubmitSpawnBillboards`, the spawn pick boxes through the same `SpawnBillboardFrame`, and selection framing through `FocusGround`. No implicit fallback to GroundHeight(field,...) remains on native paths. A missing or unresolved readout suppresses the misleading billboard/focus and returns a developer diagnostic. Keep the released lifts, size, colors and analytic paths intact. Do not grow the baselined scene or move unrelated UI concerns. `ViewportWorld.cs` stays at most 800 lines, and the change removes more lines than it adds.
- [ ] **Step 4: Run green** with Step 2's filter as `t8-green`, then `wa_test t8-architecture "$RUMP" "FullyQualifiedName~ArchitectureTests"`, confirming no MapEditor to Physics edge. Run `wa_sweep t8-final` after review/fixes. No windowed app is launched.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t8_paths=(
    KhaozEngine.MapEditor/NativeMarkerReadouts.cs
    KhaozEngine.MapEditor/SpawnBillboards.cs
    KhaozEngine.MapEditor/MapEditorScene.Markers.cs
    KhaozEngine.MapEditor/MapEditorScene.cs
    KhaozEngine.MapEditor/MapEditorOptions.cs
    KhaozEngine.MapEditor/MapEditorScene.Document.cs
    KhaozEngine.MapEditor/MapEditorScene.Reload.cs
    KhaozEngine.MapEditor/MapEditorScene.Window.cs
    KhaozEngine.MapEditor/MapEditorScene.Navigation.cs
    KhaozEngine.MapEditor/ViewportWorld.cs
    KhaozEngine.MapEditor/EditorPicking.cs
    KhaozEngine.MapEditor.Tests/MapEditor/NativeMarkerReadoutTests.cs
    KhaozEngine.MapEditor/README.md
    .filesize-baseline
)
wa_finish t8 "${wa_r6_t8_paths[@]}" &&
git add -- "${wa_r6_t8_paths[@]}" &&
git commit --only -m "feat(mapeditor): draw native marker and spawn readouts from named support" -- "${wa_r6_t8_paths[@]}"
```

### Task 9: Public village, stacked cave and independent heads

**Files:**
- Create: `KhaozEngine.MapDoc.Tests/Markers/MarkerVillageFixtures.cs`, `NativeMarkerAcceptanceTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapMarkerVillageAcceptanceTests.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/MarkerVillageWorkflowTests.cs`.
- Modify: `KhaozEngine.Tests/ArchitectureTests.Prefabs.cs` runtime non-edges, MapDoc, MapDoc.Physics, MapEditor and MapEdit.Tool READMEs, `docs/DEPENDENCY-SEAMS.md:974-994` marker runtime contract. Test source links from Task 2 cover the new concern fixture.

**Interfaces:**
- Consumes Tasks 1 to 8, released `MapWorldBuilder.Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) -> MapBuiltWorld` at Physics/MapWorldBuilder.cs:41 and `MapBuiltWorld.Markers`, `Surfaces`. Residency comes from `MapResidencyOwnership.BuildAnnotations(MapBuiltWorld world, MapWorldGrids grids) -> IReadOnlyList<MapResidencyEntry>` at Physics/MapResidencyOwnership.cs:99. Same builder serves both heads, with separately loaded inputs.
- Produces `MarkerFixtures.Village() -> MarkerFixture`, a complete invented native village with six uniquely named markers. Records are `start`, `actor`, `roof-label`, `cave-explicit`, `cave-bound`, `disabled-local`. They include both height modes, a payload-1 child, a payload-2 local child, negative X, an elevated point, ordered/duplicate tags and a disabled marker. Start/actor/label roles are caller values. Geometry reuses released prefab-cave and support fixtures without borrowing private content.

- [ ] **Step 1: Write failing acceptance tests.** `VillageMarkers_SaveReloadBothStorageFormsAndHeadsAgree` loads independent documents and closures for two heads, both monolithic and tiled, with the same TileSize. Compare complete value sequences, not record equality on list references. All six are exhaustive, five enabled. Verify resolved heights and value provenance, exact negative-X points, yaw modulo a turn, names, roles, tags, archetype and instance ownership. Run actual membership and half-open rectangle queries on both heads.

```csharp
Assert.Equal(6, client.Markers.Count);
Assert.Equal(5, new MapMarkerRegistry(client.Markers).Enabled.Count);
Assert.Equal(clientRows, serverRows);
Assert.Equal(clientProvenance, serverProvenance);
Assert.Equal(clientMembershipRows, serverMembershipRows);
Assert.Equal(new[] { "second", "first", "first" }, disabledLocal.Tags);
Assert.True(negativeMarker.Position.X.ToDouble() < 0);
Assert.Empty(document.Spawns);
Assert.Empty(document.PlayerSpawns);
Assert.Equal(beforeStatics, afterMarkerOnlyStatics);
Assert.Equal(beforeNavCaptureIdentities, afterMarkerOnlyNavCaptureIdentities);
```

`MarkerOnlyEdits_ChangeResidencyWithoutPhysicsOrNav` compares actual build geometry and per-nav-tile capture identities before/after marker move, with stable world geometry. Whole authored/build identity may change because markers are authored data. Confirm old marker residency owners disappear and new points/tiles are installed exactly once. The server list is root-resident even when no visual window covers a point. Do not claim #341's entire cell-residency problem is solved.

`MarkerVillageWorkflow_SharedCommandsAndWireReload` exercises creation, duplicate-name refusal, local replace override, prefab move at yaw 0.371/scales 0.8 and 1.2, unpack, marker edit, adoption, undo/redo and save/reopen through the real service and wire. It preserves all three OA9 proof names from Task 2. `MarkerRuntime_HasNoTileWorldPhysicsOrGpuDependency` checks MapDoc runtime closure and MapEditor direct non-edge. No game types appear in public fixtures.

- [ ] **Step 2: Run red** `wa_test t9-red-doc "$MAPDOC" "FullyQualifiedName~NativeMarkerAcceptanceTests"`, then `wa_test t9-red-heads "$MP" "FullyQualifiedName~MapMarkerVillageAcceptanceTests"` and `wa_test t9-red-editor "$EDITOR" "FullyQualifiedName~MarkerVillageWorkflowTests"`. Each fails at compile on the missing `Village()` fixture. A scenario already passing after the fixture lands is recorded as a passing regression. Identify a missing integration assertion before applying a fix, never alter expected values to manufacture red.
- [ ] **Step 3: Implement** the invented complete fixture and close only reachable cross-task marker contract gaps with a regression in the owning module. Preserve F-R12/F-R13 budgets on spread geometry, F-R14 acquired frames and W-R1 wire calls. Document height modes, names/overrides, enabled versus exhaustive views, role injection, adoption and consumer seams in living package docs.

Handoff to R7 through R11 is explicit. R7 consumes generic tagged points, R8 consumes resolved annotations and residency, R9 supplies the full GUI host and inspectors, R10 owns whole-tool parity, #1370 consumes indexed half-open points, #438 consumes queried space, R11/G2 refreezes private source and owns final import/point-shift acceptance. Native routes, doors, renderer pixels and game policy are not certified by these headless tests.

- [ ] **Step 4: Run green** with the three Step 2 filters as `t9-green-doc`, `t9-green-heads`, `t9-green-editor`, then `wa_test t9-architecture "$RUMP" "FullyQualifiedName~ArchitectureTests"`. Run `wa_sweep t9-final` after review/fixes.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t9_paths=(
    KhaozEngine.MapDoc.Tests/Markers/MarkerVillageFixtures.cs
    KhaozEngine.MapDoc.Tests/Markers/NativeMarkerAcceptanceTests.cs
    KhaozEngine.MapDoc.Physics.Tests/MapMarkerVillageAcceptanceTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/MarkerVillageWorkflowTests.cs
    KhaozEngine.Tests/ArchitectureTests.Prefabs.cs
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapDoc.Physics/README.md
    KhaozEngine.MapEditor/README.md
    KhaozEngine.MapEdit.Tool/README.md
    docs/DEPENDENCY-SEAMS.md
)
wa_finish t9 "${wa_r6_t9_paths[@]}" &&
git add -- "${wa_r6_t9_paths[@]}" &&
git commit --only -m "test(mapdoc): prove general markers across storage heads and authoring" -- "${wa_r6_t9_paths[@]}"
```

### Task 10: Private marker comparison and complete input contract

**Files:**
- Create: `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerInputs.cs`, `ShippedMarkerComparison.cs`, `ShippedMarkerComparisonTests.cs`, `ShippedMarkerInputGuardTests.cs`, `ShippedMarkerGuardFixtures.cs`.
- Modify: `KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj:13-19`, `README.md` private marker contract and format-8 scaffold clarification, `KhaozEngine.Tests/ArchitectureTests.MapDoc.cs:31-52` reference guard.
- Release-preparation docs: root README, `docs/USING-KHAOZENGINE.md`, `docs/INDEX.md`, `docs/DEPENDENCY-SEAMS.md`, the four affected package READMEs and CHANGELOG. Version/declaration paths are selected concretely at the reconciled candidate as described below, never an unreviewed broad add.
- Private prerequisite, owned and committed separately by the controller in the game repository: retained `proofs/r6-marker-oracle/generator/build-scaffold.py`, any C# helper/project it invokes, role/support correspondence manifest and usage README. No private generator/input is committed in the engine task. The plan writer creates none of these files.

**Interfaces:**
- Consumes released `PrivateOracleEntry.Run(Action<PrivateOracleInputs, PrivateOracleReport> body)` and its dictionary-environment overload at PrivateOracleEntry.cs:11-16, `PrivateOracleInputs.Require(IReadOnlyDictionary<string,string?> environment) -> PrivateOracleInputs`, `PrivateOracleReport.Record(string category, string detailJson) -> void`, `Check(string category, bool ok, string detailJson) -> void` at :60-68.
- Consumes `ShippedSourceInventory.ReadWorld(PrivateOracleInputs inputs) -> (TileWorldDocument Document, TileWorldCatalogs Catalogs)` at :30, `TileDocumentGroundHeight(TileWorldDocument document)` and `(TileWorldDocument document, TileWorldCatalogs catalogs)`, `HeightAt(float tileX, float tileZ, int plane) -> float` at TileWorld.Netcode/TileDocumentGroundHeight.cs:35-60. No copied height algorithm.
- Consumes Tasks 1 to 9, internal released `LegacyOracleConverter.ToNative(TileWorldDocument document, RegionCoord region, int plane) -> (MapSurfaceRef Surface, MapSurfacePatch Patch)` at Compatibility.Tests/LegacyOracleConverter.cs:21 only for source terrain geometry in the linked test-only scaffold path, and MapWorldBuilder from Task 9 for independent heads. Core MapDoc never references the source packages.
- Produces internal `ShippedMarkerInputs.Read(PrivateOracleInputs inputs) -> ShippedMarkerInputs`, with captured attested world/catalog bytes, source markers, native scaffold, role/support rules and expectations. Produces `ShippedMarkerReport(IReadOnlyList<string> Failures, string DetailJson)` and `ShippedMarkerComparison.Compare(ShippedMarkerInputs inputs) -> ShippedMarkerReport`, assertion-free.
- Produces fact `ShippedMarkers_PreserveNamesTagsStandingPointsAndHeight` inside PrivateOracleEntry.Run. Both the private fact class and synthetic guard class carry `[UnsupportedOSPlatform("windows")]`. Nothing reads process input during discovery/static initialization.
- Produces synthetic `ShippedMarkerGuardFixtures.Create(string variant) -> ShippedMarkerGuardFixture`. The new internal `ShippedMarkerGuardFixture : IDisposable` exposes `Environment: IReadOnlyDictionary<string,string?>` naming invented temporary inputs and `PublicSecretMarker: string`, a deliberately public sentinel. It owns its synthetic directories and an existing mode-0700 report parent with an absent output path. Dispose removes only that owned synthetic fixture.

**Full private input closure, required before any comparison run:**

Follow R4-P40 and the released harness README. The controller freezes current game main, recording the full source commit and engine pin privately. Do not compare historical source counts or carry the old draft's frozen marker inventory forward. Verify the manifest marker index against every region marker record, including signed region ownership and exact ordered tags. Every source point is accounted for once.

The extraction contains exactly one attested world.json, every listed region, every named catalog, source prefab definitions needed to establish marker ownership, the game-owned role/footprint/height manifest and support/space correspondence, the exact generator and imported helpers, the native monolithic format-8 scaffold, every native asset-root manifest and its complete transitive resource graph. Include every surface/patch/topology/room/parent dependency needed by marker support or membership, including any retained R5 building-semantic manifest and its supporting source files. Include every mesh, collider, selection, material, texture, light or LOD resource actually loaded by native closure construction. If only support geometry is needed, do not invent unrelated colliders or omit resources still advertised by a declared root. There is no implicit developer-checkout dependency.

Retain the generator in private game git before relying on its output. Generate through build-slot for any build/bake, never hand-edit a scaffold. Two mechanical generator outputs may be byte-compared for determinism, without repeating a test suite. Attest the exact retained scripts and every extraction file in provenance paths, not only selected marker rows. Regenerate and re-attest after any source, rule, script or dependency change.

Use all released ShippedSourceProvenance fields. `sourceRepository`, tag (possibly explicitly empty), full lowercase commit, sourceEnginePin, extractionPathspec, paths, aggregateSha256, aggregateRule, units, rowOrientation, comparison and oracleEquivalence are required. Aggregate is SHA-256 of UTF-8 `path TAB lowercase-sha256 LF` lines in ordinal path order. The provenance's own byte hash is the existing outer environment digest. All paths obey released containment, symlink, snapshot and post-loader recheck rules, with no fallback search. Native reloads use captured verified bytes.

Pin marker comparison schema 1, closed and duplicate-safe. Its comparison object requires `schemaVersion`, `nativeDocumentPath`, `roleManifestPath`, `markerManifestPath` and `expectations`. Expectations name total, player-start, spawn, landmark, label, negative-X, support-bound and enabled counts. Values and per-marker rows exist only in private provenance/manifests. The role manifest bijectively maps each source name to destination ID, role, footprint offsets, named support binding, height rule (`terrain` or `standing`), yaw, enabled and nullable archetype. The marker manifest records every source tile/plane and expected native support/space correspondence. Refuse missing, extra or duplicate rows, unknown fields/rules and invented source defaults.

The generator binds imports to explicit source support owners in the Task 1 marker binding shape. It resolves standing points using the game-supplied footprint/role offsets, leaves archetype empty for tag-driven identity, preserves tag order, and supplies authored exterior/room spaces where the comparison claims membership. It never rebinds a bound home to a surface the game habitat policy excludes. The source height baseline calls the released terrain-only or catalog-aware TileDocumentGroundHeight constructor selected by each attested rule. The comparator independently checks those rules against source points and compares the resolved native answer. Changed label/icon points under Decision 5 are recorded as deferred R11 acceptance, not described as exact preservation of the old corner convention.

- [ ] **Step 1: Write failing synthetic boundary and comparison tests.** Guard variants cover missing four-variable input, wrong provenance hash, unattested catalog/region/generator/native resource, missing support, duplicate/case-variant JSON fields, duplicate source or destination, extra native marker, changed ordered tags, negative-X shift, wrong height, wrong role/space, unsupported version, path escape, parent/final/dangling symlink and unsafe/existing report path. The valid invented extraction has six markers, both source height rules and at least one explicit synthetic native point. The actual private comparison requires imports to be bound, while the explicit case is a guard for comparator diagnostics, not a relaxation of import policy.

```csharp
Assert.Equal(6, validSyntheticMarkerCount);
Assert.Equal(0, validReport.Failures.Count);
Assert.Contains("marker-tags", changedTagsReport.Failures);
Assert.Contains("marker-height", changedHeightReport.Failures);
Assert.Empty(fullTurnYawReport.Failures);
Assert.Contains("marker-yaw", quarterTurnYawReport.Failures);
Assert.DoesNotContain(fixture.PublicSecretMarker, publicFailureMessage);
Assert.Equal(6, harnessDirectEngineReferences.Count);
```

Synthetic yaw controls use +2*pi as equivalent and pi/2 as a failure. Fixed comparison tolerances are 0.00001 m per XYZ/height component and 0.000001 radians wrapped yaw. Identity/name/role/archetype/enabled and tag sequence compare exactly. A generator that supplies matching but wrong expected counts must still fail the independent source inventory comparison. Reader tests mutate source/native bytes after attestation and check refusal before comparison or capture-consistent reload, never a reread of changed files.

- [ ] **Step 2: Run synthetic red only** `wa_test t10-red "$ORACLE" "FullyQualifiedName~ShippedMarkerInputGuardTests"`. Expect missing reader/comparator APIs or absent marker checks. The implementer never runs the shipped fact, downloads private source or runs the harness unfiltered.
- [ ] **Step 3: Implement** complete input reading and the assertion-free comparison. Check the source manifest/region inventory, bijective correspondence, effective unique name set, ID mapping, ordered tags, roles, enabled, archetype, standing-point XZ, resolved height, wrapped yaw, binding/provenance and point membership/parents. Build fresh native heads from both storage forms with matching TileSize. Compare every effective row and query, with no shared mutable head state. Check source-support fidelity using the named released source rule rather than another hand-coded sampler. Preserve negative coordinates without clamping or translation.

Use existing report.Check/Record only inside the private boundary. Private reports carry category counts and detailed failures as needed. Public output is sanitized pass/fail, fixed failure categories and secure report reference under the released boundary, never source inventory counts or source-derived exception messages. The engine repository records only pass/fail and the private record reference. No assertion, output helper, discovery data or inline test value contains private data.

Add TileWorld.Netcode as the sixth reference. ArchitectureTests must pin exactly MapDoc, MapDoc.Physics, Physics.Bepu, TileWorld, TileWorld.Netcode and TileWorld.Physics. Preserve outside-solution, unreferenced, nonpackable and public-CI exclusion checks. README explains marker schema, the four unchanged variables, format-8 native scaffold, retained generator and fixed tolerances. Clarify that old format-7 building scaffolds still load through pure migration, without regenerating the accepted R5 input merely for a version bump.

- [ ] **Step 4: Run synthetic green** `wa_test t10-green "$ORACLE" "FullyQualifiedName~ShippedMarkerInputGuardTests|FullyQualifiedName~PrivateOracleGuardTests|FullyQualifiedName~ShippedBuildingInputGuardTests"`, then `wa_test t10-architecture "$RUMP" "FullyQualifiedName~ArchitectureTests"`. Run `wa_sweep t10-final` after review/fixes. No shipped comparison runs in this worker task.
- [ ] **Step 5: Format, guards and commit.** Candidate release/doc changes are committed separately by the controller below, after current-main reconciliation. This task commit contains the harness and its reference guard only.

```bash
wa_r6_t10_paths=(
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerInputs.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerComparison.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerComparisonTests.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerInputGuardTests.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerGuardFixtures.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj
    KhaozEngine.MapDoc.OracleHarness.Tests/README.md
    KhaozEngine.Tests/ArchitectureTests.MapDoc.cs
)
wa_run t10-oracle-format dotnet format "$ORACLE" --verify-no-changes --no-restore &&
wa_finish t10 "${wa_r6_t10_paths[@]}" &&
git add -- "${wa_r6_t10_paths[@]}" &&
git commit --only -m "test(mapdoc): compare private markers through the attested oracle boundary" -- "${wa_r6_t10_paths[@]}"
```

## Self-review and execution handoff

- [ ] Map OA36 Decisions 1 to 20 to owning tasks. Payload/format is Task 1, support/provenance/space/OA9 Task 2, support-edit consequences and rename rows Task 3, names/roles/views/rectangle lookup Task 4, edits/verbs/`marker_list` rectangle Task 5, adoption/legacy fence/resolver guidance Task 6, start selector/windowing/whole-load summary/tool readouts Task 7, GUI readouts and billboard seam Task 8, public acceptance Task 9, private acceptance Task 10, release below. No approved item is silently deferred.
- [ ] Check every released signature/file/range against `1a4182c24`, and every new signature against its producing Interfaces block. Propagate accepted interface changes into later briefs. New helper variants belong to their first consuming task. Preserve old public constructors/overloads.
- [ ] Check payload-1 bytes against the independent released golden, the payload-1 annotation digest pin, version-only migration and both identity paths against canonical derivations, and analytic recorder bits unchanged. No fabricated new golden replaces a failing compatibility assertion.
- [ ] Check all five Review Focus tests, the three exact OA9 names, the #1348 counterexample across terrain, placement, prefab and interior transactions on room-free fixtures with succeeding controls, both isolated disabled-participant name cases on both heads and unadopted native spawn refusal. Confirm exact values and valid fixtures before interpreting red.
- [ ] Check bound height is the shared query's float `WorldY` with no new support overload (R6-P14), `RemapWorld` renames only the active marker owner, the legacy-list recheck runs at every publication, a partial native Open keeps its released refusal (R6-P13), and GUI presentation keeps the released lifts.
- [ ] Check no marker metadata edit adds spatial flags, bound-marker reseats include both points, post-edit support loss refuses, old/new names survive command/effect serialization and history for marker, override and asset-update renames, and the before side reuses the accepted expansion. Support edits keep their own existing Physics/Nav flags.
- [ ] Check the marker binding canonical form (kind members only, nulls refused), the untouched placement `supportBinding` definition, and the frame-local binding rule through unpack, reload, `marker_set` and undo.
- [ ] Check whole-project regression sweeps after each task, 800-line source caps, baseline growth prohibition, explicit commit path ownership and linked public fixture source boundaries.
- [ ] Check complete private closure before extraction delivery, secure reports, independent source inventory and both storage/head comparisons. No private world data, counts or digests enter this public plan or fixtures.
- [ ] Check proportion and step determinacy. No placeholder APIs, silent role/height defaults or implementation bodies whose tests already determine them. Record any later review correction to the R6-P table before dependent code.
- [ ] Record independent plan reviews, adjudicated rulings and owner reviewed-plan approval in the private controller ledger. The plan writer stops at this docs commit. Execution remains unapproved until that record exists.

Recommended execution is the program's serial subagent-driven method after owner plan approval, one bounded implementer and fresh task review at each gate. The controller independently inspects diffs, commit containment, red/green logs and actual exit codes. A reported commit or test pass is not proof until checked. The R6-P rulings above are the review 1 and review 2 adjudications and change only through a recorded controller ruling. No worker pushes, integrates, packs, tags or cleans worktrees.

## Review, candidate gates, private comparison and integration

1. Review the whole branch against the approved plan, gate and carried R5 rulings. Resolve task-related findings with focused regressions and scoped re-review. Preserve unrelated work and file newly discovered work through the owning controller's ledger process. Do not widen R6 into native routes, GUI host completion or game adoption.
2. Fetch and merge current main into the task branch before final verification. Resolve there. If format 8 was taken, stop for a controller plan amendment. Do not silently renumber an approved migration. Recheck dependencies and shared APIs after reconciliation.
3. Re-read current main/origin/main, KhaozEngineVersion, newest tags and CHANGELOG. Ride untagged staged 21.1.0 if still current, otherwise the next free minor, riding an appropriate staged minor. If only a patch is staged, retain its items under the selected minor. Update Directory.Build.props, CHANGELOG and every declaration required by scripts/check-doc-versions.sh in one explicit-path commit. State the format-8 identity move, duplicate effective-name compatibility change, payload-1 byte and annotation digest stability, the payload-2 annotation digest scheme, explicit adoption and native readout requirements.
4. Sweep Markdown for MapLocalMarkerDoc, marker payload, typed spawn, marker_set/remove/list, spawns_adopt, old format/current-version claims and the deferred R6 rows. Update root README, USING-KHAOZENGINE, docs/INDEX, dependency seams and affected package READMEs. Keep old executed plans historical. The program spec link may be absent from main, so its authoritative program-branch C6/verb/exit updates belong to the controller's program reconciliation, never a claim that this branch already contains that file. Construct the exact changed-path array, inspect it, then commit only those paths with `docs(world-authoring): publish the R6 consumer and release contract` or the selected version scope if a bump is included.
5. Run these candidate gates once, sequentially, on the reconciled code/version/docs candidate. Keep SHA, logs, warning counts and nonzero test counts.

```bash
wa_run gate-build --heavy dotnet build KhaozEngine.slnx -c Release
wa_run gate-suite --heavy dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
wa_run gate-format dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
wa_run gate-oracle-build --heavy dotnet build "$ORACLE" -c Release -m:1
wa_run gate-oracle-format dotnet format "$ORACLE" --verify-no-changes --no-restore
wa_run gate-dashes sh scripts/check-dashes.sh --tree
wa_run gate-prose sh scripts/check-prose.sh --tree
wa_run gate-size sh scripts/check-file-size.sh --tree
wa_run gate-agent sh scripts/check-agent-instructions.sh --tree
wa_run gate-docs bash scripts/check-doc-versions.sh
git diff --check
```

Expected all exit 0, zero build warnings/errors, zero failed tests and no zero-match filters. Public synthetic architecture checks continue to exclude the harness from public CI selection. Push the verified candidate through the controller and require exact-commit hosted CI. This is ordinary verification, no stress permission is implied. No GPU or game-window proof is needed for this round's contracts.

6. Controller-only private acceptance. Deliver and verify Task 10's retained generator/input closure first. Assign `wa_r6_verified_source`, `wa_r6_private_provenance`, `wa_r6_provenance_sha256` and `wa_r6_private_report` from the private attestation. The report parent already exists at 0700 outside git and the report path is absent. Run the one shipped fact once on the exact candidate.

```bash
wa_run gate-private --heavy env KHAOZ_R2_SHIPPED_SOURCE="$wa_r6_verified_source" KHAOZ_R2_ORACLE_PROVENANCE="$wa_r6_private_provenance" KHAOZ_R2_ORACLE_PROVENANCE_SHA256="$wa_r6_provenance_sha256" KHAOZ_R2_ORACLE_REPORT="$wa_r6_private_report" dotnet test "$ORACLE" -c Release --no-build --filter "FullyQualifiedName~ShippedMarkers_PreserveNamesTagsStandingPointsAndHeight"
```

Read exactly those four variables in that order through PrivateOracleEntry. Verify provenance digest before parsing, create the report exclusively at 0600, sanitize all exceptions and final failure text. Missing, unsafe or mismatched input blocks acceptance. The controller records source/engine commits, generator commit/digest, full attestation, actual category counts, tolerances, report digest and every discrepancy in private `R6-MARKER-ORACLE.md`. Public evidence is pass/fail and that record reference only. This comparison is not the R11 importer or named game acceptance. R11/G2 refreezes the actual accepted source again.

7. A private mismatch is a failed gate. Investigate the source rule, manifest or engine defect, fix its proper owner and rerun the relevant focused proof. Do not change counts, height mode or tolerances to get green. A changed candidate/input invalidates prior matching evidence and justifies one renewed affected gate, never a local repetition loop. Before integration ensure current main is contained by the verified branch. Reconcile and verify again if it advanced in a relevant module.
8. The engine controller fast-forwards main and pushes main. Pack only from clean main after origin/main contains the integrated commit, with the shared feed's own guards.

```bash
wa_run release-pack --heavy sh scripts/pack-local-feed.sh
```

9. Launch the release through a KhaozEngine-rooted thread under OA25 and the approved execution/release handoff, using release-khaozengine and current engine release rules. Fresh version/tag checks, scripts/tag-release.sh, annotated remote tag, exact-commit CI, publication, complete package inventory and required fresh tool downloads are release evidence. One owner of the staged tag coordinates concurrent lanes. Never tag from the Grimhollow-rooted controller or infer execution approval from OA36 alone.
10. Update the private PROGRAM/DECISIONS/R6 ledger with accepted rulings, task and integration commits, private comparison reference, verified release and revised R7 to R11/G-round forecast. Consumer pin adoption, game content import and owner visual/game acceptance remain separate. No worker cleanup or integration occurs before controller verification.

## Outcome and proof ledger

Filled during approved execution.

| Item | Evidence |
| --- | --- |
| Independent plan reviews and owner approval | |
| Adjudicated R6-P rulings and later corrections | |
| Task commits, red/green and three-project sweeps | |
| Payload-1 bytes, annotation digest and format-8 identity derivation | |
| Three OA9 proofs and bound-marker edit refusal across transaction kinds | |
| Command/service/wire parity, `marker_list` rectangle and 96-tool inventory | |
| Public synthetic storage/head acceptance | |
| Whole-branch review and main reconciliation | |
| Candidate guards, Release build/suite and hosted CI | |
| Private generator commit and complete input attestation | |
| Private marker comparison record | |
| Integration, package proof and engine-rooted release | |
| Remaining engine/game program forecast | |
