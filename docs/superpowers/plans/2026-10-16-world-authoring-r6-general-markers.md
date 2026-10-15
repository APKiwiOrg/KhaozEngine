# World Authoring R6: General Markers and Spawn Projections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend R5's single marker store into the native named-point contract, with explicit height choices, lossless spawn adoption and identical read and edit behavior on both heads.

**Architecture:** Keep world markers in `BuildingIntent.Markers` and local markers in prefab children. Marker payload 2 adds a closed height choice and optional archetype without rewriting payload 1. Resolve named support after effective geometry exists, expose immutable lookup and projections over the resulting points, and publish edits through the existing closure-bound transaction. Format 8 is a version-only migration. Legacy spawn adoption is a separate explicit edit.

**Tech Stack:** C#, System.Numerics, System.Text.Json, JSON Schema, xUnit, MapDoc exact points and scoped support/membership queries, MapEditor transactions and ke-mapedit MCP. No new package or third-party library.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), C6, C9, DG9.1, DG9.4 and the marker rows of the R10 verb map. The controller supplies the spec from the program branch if it is absent from this execution branch. Read [R5's executed plan](2026-10-12-world-authoring-r5-free-buildings-prefabs-interiors.md) for the released ownership and transaction contracts.

Binding private inputs are the Grimhollow program's `R6-OWNER-GATE.md`, `DECISIONS.md` OA36, `R5-LEDGER.md` and `R5-BUILDING-ORACLE.md`. The controller supplies them in briefs, never copies private content into this public repository. Research inputs are `.superpowers/sdd/2026-10-16-world-authoring-r6-refresh/research-engine.md` and `research-program.md`. The gate overrides their conflicting import-height proposals and the superseded 2026-10-05 R6 draft.

## Approval and execution gate

Written against engine `1a4182c24da4584000a1c963fee1a2f8ccd014dd`, with v21.0.0 released, 21.1.0 staged, MapDoc format 7 and R1 to R5 released. OA36 approves planning, including all six decisions and smaller decisions 7 to 20. Independent plan reviews and owner approval of the reviewed plan precede code. This file approves no implementation, tag, game adoption or R11 acceptance.

The writer changes and commits this file only. The execution commands below are instructions for approved future execution. Workers stop at verified task commits. The controller owns reconciliation, pushes, integration, private evidence and the engine-rooted release handoff.

## Global Constraints

- OA36 Decision 1. One editable store, `BuildingIntent.Markers` and prefab Marker children, marker payloads 1 and 2, prefab payload 1, format 8, resolver identity (1, 2), `AuthoredBindingsV2`.
- Payload 1. Preserve constructor, canonical bytes and SHA-256 of released prefab assets. Omit every absent new member. Do not republish immutable prefab resources to accommodate R6.
- Migration. Pure sequential 7 to 8 version-only JSON step, with no source, terrain, closure, registry or role access. Every native identity and analytic manifest hash moves once. Analytic execution stays unchanged. Future format 9 refuses with update guidance.
- OA36 Decision 2 and OA9. Payload 2 requires exactly one explicit Y or `MapSupportBinding`. Never default a height mode, surface owner, reference height or search range. Surface means the named owner. Space requires finite ReferenceY and finite nonnegative bounded search.
- Height. Resolve through released R2 support on both heads after effective support installation. Only Supported succeeds. A removed bound support refuses the edit. Explicit heights never resample. Local distances and coordinates transform once.
- OA36 Decision 3. Effective names are ordinal, case-sensitive, globally unique, nonempty and editable. IDs are immutable. Repeated prefab names need explicit replace overrides, never suffixing. Rename effects carry old and new names.
- OA36 Decisions 7 to 9. Derive occupied space and parent chain by point membership, with explicit Outside when no space is authored. Roles are caller vocabulary. Enabled views spawn, exhaustive views validate and preserve persistence keys.
- OA36 Decision 4. Analytic `MapSpawn` and `MapPlayerSpawn` remain unchanged. Native old lists still load. No native path adds new legacy records. One explicit undoable adoption clears both lists after every record has a destination ID and height choice.
- OA36 Decisions 5, 10 and 11. Private import points use the game role manifest's standing point. Record source tiles. Imported markers bind to their source support, waterfowl homes remain bed-bound, habitat and spawn selection remain game policy.
- OA36 Decisions 12 and 13. Marker-only spatial edits add Residency = 16 only. Metadata-only edits add no invalidation. Never add Physics = 2, Nav = 4 or Unbounded = 64 for a marker. Report marker IDs and old/new point boxes.
- OA36 Decisions 14 to 16. Half-open marker rectangles include minima and exclude maxima, including negative coordinates. Native starts use a caller role. Native readouts use authored support, never analytic terrain.
- R5 W-R1 and F-R9. Every wire class joins non-parallel `McpWireCollection`. Each MCP request has its own 60 s timeout. Compare yaw by `Math.IEEERemainder(a - b, 2 * Math.PI)`, tolerance 0.000001 radians, without normalizing storage.
- Bounds. Keep MapQueryLimits defaults 256 patches, 4096 faces, 64 support intersections, 32 page reads, 64 record reads and depth 8. Keep R5 expansion limits and precision. Overflow refuses, never truncates or widens limits to pass a fixture.
- R5 F-R12 to F-R14. Broad phases enumerate nearby candidates before exact work. Query points use the acquired frame or QueryExact. Preserve bounded counters and existing paint/coverage results on real content.
- Dependencies. MapDoc remains physics, render, GPU, editor, TileWorld and game free. MapEditor gains no MapDoc.Physics reference. Only the private harness gains TileWorld.Netcode, as its sixth direct engine reference.
- Test placement. Payload, identity, adoption and membership in MapDoc.Tests, independent heads in MapDoc.Physics.Tests, commands/service/wire in MapEditor.Tests. The private harness stays nonpackable, unreferenced, outside the solution and public CI.
- Verification. Every build, test, format, bake or pack uses build-slot. Full MapDoc, MapDoc.Physics and MapEditor projects once after each accepted task, full solution Release build and suite once at the reconciled candidate. No stress loops, GPU checks or consumer windows.
- Repository. Explicit-path commits, zero warnings, no blanket suppression, new C# files at most 800 lines. Baselined files may shrink, never grow without owner approval. No em or en dash glyphs or Markdown prose semicolons.
- Input and text. AppWindow alone reads raw devices. GUI paths use InputState, InputManager and Pointer. Player text uses StringId/LocalizedText. Tool diagnostics use the explicit raw path.
- Privacy and CD2. Public invented fixtures only. Private input missing means failure, never skip. No private names, points, tags, source inventories, geometry, content digests or detailed reports enter engine git, CI or public logs.
- Release. Ride the untagged staged minor at integration, 21.1.0 if still available, otherwise the next free minor. CHANGELOG and guarded declarations travel together. A KhaozEngine-rooted thread performs the authorized release under OA25 after plan execution approval.

## Review Focus

1. A support edit changes marker height outside its named patch report, or removes support while a higher floor remains. Task 2 tests `BoundMarkerReseat_NamesBothPointsAndRefusesLostOwner` through actual terrain transactions, preventing the #1348 class.
2. A bound local marker is unpacked, re-extracted, moved and reloaded. Task 2 tests `BoundMarkerUnpackExtract_KeepsIntentAndTransformsOnce`, including typed local/world owner slots with identical text.
3. Disabled prefab children collide by name with a world marker. Task 3 tests `DisabledAndOverriddenNames_ValidateBeforeAnyBuild`, including replace/remove/restore controls and unchanged IDs.
4. Valid native old lists have the same source ID in different kinds. Task 5 tests `SameSourceIdInTwoLists_RequiresDistinctExplicitDestinations`, plus generic mutation and element-duplicate paths that must not add native legacy records.
5. A negative-X player start is prefab-local and support-bound outside the first loaded window. Task 6 tests `NativeWindowAnchor_UsesRootMarkerWithoutWholeGeometry`, including a missing closure and an unadopted native spawn readout with no support choice.

---

## Proposed controller rulings

These are judgment calls for explicit acceptance or correction before dependent dispatches. Gate choices above are binding already.

| ID | Proposal | Cost if wrong |
| --- | --- | --- |
| R6-P1 | Preserve the released Vector3 constructor. Add optional `Height` and `ArchetypeId`. Payload 2 uses Position.X/Z, requires Position.Y = 0 as a neutral carrier, and stores its sole Y authority in the closed Height choice. Payload 1 requires both new members absent | A future cleaner DTO needs a payload transition. This avoids changing released positional APIs or encoding two competing heights |
| R6-P2 | Publish world-space height provenance on effective markers. Keep runtime read witnesses out of annotation digests, but hash stable selected face, binding and resolved position. No stored membership | Provenance shape becomes a public contract. Consumers must retain its witness when claiming query completeness |
| R6-P3 | Validate effective names by a bounded metadata-only enumeration shared with marker expansion, before support, callback or static work. Include disabled markers, exclude removed children | One shared enumeration seam is needed. A separate expansion would risk name/pose drift |
| R6-P4 | Build an immutable median-split 2D point BVH for rectangle lookup, leaf size 8, stable ID tie breaks. Membership runs only for returned points and preserves refusal statuses | More index code than a scan. A later streaming index may replace it behind the same API |
| R6-P5 | Adoption choices name source kind/ID, destination ID, name, role and height explicitly. NPC yaw is 0 because the source has no yaw. Player yaw is copied. No default role or destination naming | More explicit caller input, but no policy or silent collision repair in the engine |
| R6-P6 | Window anchoring consumes root marker metadata and verified prefab bytes without support resolution. Select enabled matching roles by squared distance to bounds centre, then ordinal ID. Height is resolved only for later readouts | Window opening has a separate bounded metadata stage. It must share transforms with resolution rather than duplicate them |
| R6-P7 | Unadopted native spawn readouts require a caller-supplied support binding. Without one, refuse with `spawns_adopt` and `marker_set` guidance. Never invent a topmost/zero-height choice. Preserve analytic and inclusive placement-rectangle behavior | Native callers must supply support or adopt. Existing misleading native readouts become explicit refusals |
| R6-P8 | Bound support is validated even for disabled markers. Creation/deletion, binding changes and spatial changes carry Residency. Archetype-only changes are metadata. Same-value edits have empty effects | Disabled stale bindings cannot be parked until later repair. A different policy needs explicit validation changes |
| R6-P9 | Use 0.00001 m for private position/height comparison and 0.000001 radians for wrapped yaw. Expected inventory and category counts live solely in private provenance | A real mismatch blocks acceptance. Tolerances cannot silently expand to hide source differences |
| R6-P10 | Execute eight serial tasks with per-task review and full three-project sweeps. Include review/fix allowance and candidate/release work in their 25 to 45 engineer-days | More transitions than a broad implementation batch, with bounded rejection and ownership |
| R6-P11 | Add optional initial keyed overrides to the prefab-place request. A second instance can supply its marker-name replace override in the same accepted placement, before uniqueness validation | One additive request property and wire option. Without it repeated marker-bearing prefabs cannot be placed through the public command path |

## File structure

Paths below are repository-relative. New files are proposed outputs of their task, not claims about released APIs. Existing ranges refer to `1a4182c24`. Do not edit the old program worktree to execute this plan.

| Task | New homes | Released integration homes |
| --- | --- | --- |
| 1 | MapDoc `Markers/MapMarkerHeight.cs`, `MapMarkerMigration.cs`, `MapMarkerValidation.cs`, marker format fixtures/tests | Prefabs records/JSON/validation, MapDocumentFile, schema and format identity tests |
| 2 | MapDoc `Markers/MapMarkerExpansion.cs`, `MapMarkerResolution.cs`, `MapMarkerSpaces.cs`, `Editing/MapMarkerEffects.cs`, MapEditor `NativeMarkerEffects.cs` | Prefab resolver/reference walker, effective annotations/resources, unpack/extraction, transaction/effect composition |
| 3 | MapDoc `Markers/MapMarkerRegistry.cs`, `MapMarkerSpatialIndex.cs`, `MapMarkerProjection.cs`, `MapMarkerRoles.cs` | Prefab validation, released R5 village fixtures/workflow, CHANGELOG |
| 4 | MapDoc `Editing/MapMarkerEdits.cs`, MapEditor `NativeMarkerCommands.cs`, tool `MutationService.Markers.cs`, `Tools/MarkerTools.cs` | Native command state, bootstrap, verb inventory, package READMEs |
| 5 | MapDoc `Markers/MapMarkerAdoption.cs`, MapEditor `NativeMarkerAdoptionCommands.cs`, tool `MutationService.MarkerAdoption.cs` | Native write set/snapshot, transaction admission, mutation adapters, marker wire tools |
| 6 | MapDoc `Markers/MapMarkerAnchors.cs`, `MapNativePointReadout.cs`, MapEditor `NativeMarkerReadouts.cs`, tool `QueryService.Markers.cs`, `MapEditSession.Markers.cs` | Windowing/options/open/reload, query tools/results, viewport spawn readouts |
| 7 | Invented `Markers/*Fixtures.cs`, acceptance and workflow tests | Test source links, runtime architecture guard, living handoff docs |
| 8 | Private harness `ShippedMarkerInputs.cs`, `ShippedMarkerComparison.cs`, `ShippedMarkerComparisonTests.cs`, `ShippedMarkerInputGuardTests.cs`, `ShippedMarkerGuardFixtures.cs` | Harness csproj/README, architecture reference guard, release and public API docs |

## Estimate and sequencing

| Task | Deliverable | Depends on | Engineer-days |
| --- | --- | --- | --- |
| 1 | Payload 2, format 8 and byte/identity compatibility | released R1 to R5 | 3 to 5 |
| 2 | Height, provenance, membership and support-edit invalidation | 1 | 5 to 9 |
| 3 | Name validation, lookup, role findings and typed views | 2 | 3 to 5 |
| 4 | Shared marker edits, commands and MCP parity | 3 | 4 to 7 |
| 5 | Explicit spawn adoption and native legacy-write fence | 4 | 3 to 5 |
| 6 | Rectangle consumers, window anchoring and native readouts | 5 | 2 to 4 |
| 7 | Public synthetic acceptance and independent heads | 6 | 2 to 4 |
| 8 | Private comparison, candidate evidence and release preparation | 7 | 3 to 6 |
| | Total, including reviews, fix allowance and integration | | 25 to 45 |

R3 took about 14 h from approval to tag, R4 about 9 h, R5 about 59 h against its earlier 12 to 24 h forecast. Forecast R6 at 20 to 45 elapsed agent hours after reviewed-plan approval, with 35 to 45 h the safer scheduling allowance. Plan reviews, owner responses, subscription capacity, private extraction delivery and release CI can extend calendar time. Engineer-days measure scope, not literal agent runtime. Reforecast after Task 2 and after the private comparison, using actual accepted task times and findings.

## Dispatch and verification conventions

The controller supplies the approved gate, relevant R5 rulings, this plan's Global Constraints, accepted R6-P rulings and consumed Interfaces blocks in each bounded brief. Execute serially, with a fresh review per task under the session's routing rules. Pin each execution start to the previous accepted task commit, not to today's writer base. Do not dispatch dependent work with unresolved load-bearing findings.

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

Red must fail at a named behavioral assertion, or compilation exclusively for newly declared task-owned APIs. Invalid fixtures, missing released signatures, unrelated failures and zero test matches are not red. Green requires exit 0, nonzero matching tests and zero failed tests. Record TRX counts and the exact SHA. Run each check once. Exit 75 means nothing ran, retry later. A new product change or failure justifies a new focused check. Run the three-project sweep once after the task's last fix and review, before acceptance, with no duplicate sweep if the reviewed bytes already have that evidence. The full solution suite runs only at the candidate gate.

Each task's explicit path array is its commit ownership. Add fix-round paths explicitly. Repair formatting through `wa_run` over the same C# list, then verify. Task 8 also formats the outside-solution harness. A known analyzer-reference workspace warning is not a waiver for an unloaded changed project. Check the loaded project and formatted paths as R5 did.

## Shared fixture and signature conventions

New test files use `KhaozEngine.Tests.*`. Create `KhaozEngine.MapDoc.Tests/Markers/MarkerFixtures.cs` as `internal static partial class MarkerFixtures`. Concern files end in `Fixtures.cs`. Link these public sources into Physics.Tests and MapEditor.Tests explicitly. Neither project references the MapDoc.Tests assembly.

Task 1 creates `MarkerFixture(MapDocument Document, MapAssetClosure Assets)` and `MarkerFixtures.Create(string variant = "basic") -> MarkerFixture`, `Reload(MarkerFixture fixture, bool tiled) -> MarkerFixture`, `Options() -> MapResolveOptions`, `Exact(float x, float y, float z) -> MapExactPoint`, `FormatSevenJson(bool analytic) -> string`, `ReleasedPayloadOneBytes() -> byte[]`. The last returns a committed invented payload-1 golden captured from `1a4182c24`, not R6 output. Basic is a complete resolver-2 map with one standalone explicit point `m` named `arrival`, role `entry-role`, tags `["first", "second"]`, X -0.25, Y 12.25, Z -1.5, yaw 0.371, enabled true. Options uses builder `public-marker-fixture`, version 1, hash `marker-options/1`, resolver 2.

Task 2 adds `Effective(MarkerFixture fixture) -> MapEffectiveResources`, `Stacked() -> MarkerFixture`, `Cave() -> MarkerFixture`, `LocalCave() -> MarkerFixture`. Stacked wraps released `SupportFixtures.EightStacked()` and its Assets. Cave wraps `PrefabFixtures.Cave("stacked")`. LocalCave adapts the released prefab-cave/room ownership, retaining its real bindings and typed reference slots. Do not invent replacement geometry for the three OA9 proofs.

Task 3 adds `DuplicateNames(string variant) -> MarkerFixture` and `RectPoints() -> MarkerFixture`. Task 4 creates editor-only `MarkerEditorFixtures.Session(MarkerFixture fixture) -> MapEditSession` and `State(MapEditSession session) -> string`, a canonical snapshot of document, identity, closure, history labels/cursor, events and dirty state. Task 5 adds `LegacyLists() -> MarkerFixture` with both source kinds sharing ID `old`, and `Choices() -> IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice>`. Task 7 adds `Village() -> MarkerFixture`.

Assertion blocks below are contracts inside the named test methods. Variables such as `before`, `after`, `marker`, `result` and `history` are the actual released/produced objects described immediately above each block, not undeclared helper APIs. Add fixture variants to the owning task's fixture file and commit paths. No test may implement a second classifier or return hard-coded product answers from a helper.

### Task 1: Payload 2, pure format 8 and compatibility

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerHeight.cs`, `MapMarkerMigration.cs`, `MapMarkerValidation.cs`.
- Modify: `KhaozEngine.MapDoc/Prefabs/MapPrefabRecords.cs:55-60`, `MapPrefabJson.cs:30-71,185-257`, `MapPrefabValidation.cs:122,390-395`, `KhaozEngine.MapDoc/MapDocumentFile.cs:37-43,85-95`, `KhaozEngine.MapDoc/mapdoc.schema.json:10,516-536`.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MarkerFixtures.cs`, `MapMarkerFormatTests.cs`, `Fixtures/PayloadOne.prefab.json`. Modify `KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj:21-23` to embed that golden with logical name `KhaozEngine.Tests.MapDoc.Markers.PayloadOne.prefab.json`, read by ReleasedPayloadOneBytes from the current test assembly.
- Test/modify: `KhaozEngine.MapDoc.Tests/FormatAdvanceTests.cs:17-121`, `Prefabs/MapPrefabFormatTests.cs:125-127`, `KhaozEngine.MapDoc.Physics.Tests/MapPersistedIdentityTests.cs:59-100`, `KhaozEngine.MapEditor.Tests/MapDoc/MapDocumentFileTests.cs:58`, `MapTiledFormatTests.cs:72`. Historical inputs remain historical. Rename the released FormatEight_Refuses test to FormatNine_Refuses and change only its future-version input to 9.

**Interfaces:**
- Consumes released `MapLocalMarkerDoc(int PayloadVersion, string Id, string Name, Vector3 Position, float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags)` at Prefabs/MapPrefabRecords.cs:57 and `MapSupportBinding(MapSupportBindingKind Kind, string? SurfaceId, string? SpaceId, float? ReferenceY, float? SearchBelow, float? SearchAbove)` at MapSupportRecipe.cs:9.
- Consumes released `MapPrefabFile.Read(ReadOnlyMemory<byte> bytes, string resourceId) -> MapPrefabAsset`, `Write(MapPrefabAsset asset) -> byte[]`, `MapDocumentFile.LoadText(string json, MapDocumentLoadOptions? options = null, string? sourcePath = null) -> MapDocument`, `SaveText(MapDocument doc, MapDocRegistry? registry = null) -> string`.
- Produces `MapMarkerHeight(float? ExplicitY, MapSupportBinding? Binding)` in namespace MapDoc.Markers. Adds init-only nullable `MapLocalMarkerDoc.Height` and `string? ArchetypeId`, omitted when null. Payload 1 forbids either. Payload 2 requires Height, exactly one branch, finite explicit Y, Position.Y exactly 0 and optional nonempty archetype. Position.X/Z and yaw remain finite. No constructor change.
- Produces `MapMarkerMigration.Upgrade(JsonObject source) -> JsonObject`, accepting only 7 or 8 and returning an independent format-8 clone, plus `MapMarkerValidation.Validate(MapLocalMarkerDoc marker) -> IReadOnlyList<string>` shared by file, prefab and document validation.

- [ ] **Step 1: Write failing tests.** `SevenToEight_IsVersionOnlyAndDoesNotSample` compares input and result after changing only a cloned version. Assert no callback or source invocation. `PayloadOnePrefabBytes_AreUnchanged` reads the committed released golden, writes it again and compares bytes and independent SHA256. Pin both world and prefab payload 2 in monolithic/tiled forms.

```csharp
Assert.Equal(8, upgraded["formatVersion"]!.GetValue<int>());
Assert.Equal(7, original["formatVersion"]!.GetValue<int>());
Assert.True(JsonNode.DeepEquals(expectedVersionOnlyClone, upgraded));
Assert.Equal(releasedBytes, MapPrefabFile.Write(MapPrefabFile.Read(releasedBytes, "golden-marker")));
Assert.Equal(SHA256.HashData(releasedBytes), SHA256.HashData(rewrittenBytes));
Assert.DoesNotContain("\"height\"", releasedJson);
Assert.DoesNotContain("\"archetypeId\"", releasedJson);
Assert.Equal(MarkerFixtures.Exact(-0.25f, 12.25f, -1.5f), explicitPoint);
Assert.Equal(new[] { "first", "second" }, reloadedMarker.Tags);
```

Add `MarkerPayloadClosedSchema_Refuses` for future payload 3, unknown/case-variant/duplicate fields, absent/both height choices, nonzero carrier Y, NaN/infinity, null tags, invalid Surface/Space shapes and missing/unbounded search. Schema and runtime validation agree. `FormatNine_RefusesWithUpdateGuidance` checks format 9. `SevenToEight_MovesEveryNativeIdentityOnce` covers marker-free, standalone, prefab and stored paths. Derive the new pins from canonical version-only root bytes, not copied build output. Repeated load/save changes nothing. `AnalyticExecutionAndOldSpawnReaders_KeepReleasedBits` uses unchanged FormatFour recorded placements/callbacks and existing analytic lists.

- [ ] **Step 2: Run red** `wa_test t1-red "$MAPDOC" "FullyQualifiedName~MapMarkerFormatTests|FullyQualifiedName~FormatAdvanceTests"`. Expect missing Height/migration APIs or format/payload refusal. Capture the released golden before implementation through existing public codec behavior.
- [ ] **Step 3: Implement** the declared DTO extensions, closed schema/codec and pure migration. Register 7 to 8 after MapPrefabMigration, change CurrentFormatVersion/schema to 8, preserve prior migrations and historical recorder bytes. Every nested support object is closed and duplicate-safe. New optional members omit null on every codec, clone, canonical and tool path. The Height union emits exactly one of explicitY or binding, omitting its null branch. No height sampling or legacy-list conversion occurs in migration. Payload-1 positional construction continues to mean explicit Position.Y.
- [ ] **Step 4: Run green** with Step 2's filter as `t1-green`, then `wa_test t1-pins "$MP" "FullyQualifiedName~MapPersistedIdentityTests"`. All independent pin derivations and analytic controls pass. Run `wa_sweep t1-final` after review/fixes.
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
    KhaozEngine.MapDoc.Tests/Markers/Fixtures/PayloadOne.prefab.json
    KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj
    KhaozEngine.MapDoc.Tests/FormatAdvanceTests.cs
    KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabFormatTests.cs
    KhaozEngine.MapDoc.Physics.Tests/MapPersistedIdentityTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/MapDocumentFileTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/MapTiledFormatTests.cs
)
wa_finish t1 "${wa_r6_t1_paths[@]}" &&
git add -- "${wa_r6_t1_paths[@]}" &&
git commit --only -m "feat(mapdoc): add marker payload 2 and pure format 8 advance" -- "${wa_r6_t1_paths[@]}"
```

### Task 2: Resolve height, space and support-edit consequences

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerExpansion.cs`, `MapMarkerResolution.cs`, `MapMarkerSpaces.cs`, `KhaozEngine.MapDoc/Editing/MapMarkerEffects.cs`, `KhaozEngine.MapEditor/NativeMarkerEffects.cs`.
- Modify: `KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs:240-287`, `MapEffectiveResources.cs:28-71`, `MapEffectiveAnnotations.cs:16-47`, `MapPrefabReferences.cs:319-354`, `KhaozEngine.MapDoc/Editing/MapPrefabEditPreparation.cs:155-204`, `MapPrefabExtraction.cs` marker capture/remap, `KhaozEngine.MapEditor/NativeDocumentTransaction.cs:96-117`, `NativePlacementEffects.cs:144-184,248-263`.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MarkerSupportFixtures.cs`, `MapMarkerHeightTests.cs`, `MapMarkerMembershipTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapMarkerSupportWorldTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerSupportEditTests.cs`.
- Modify source links: `KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj:22`, replacing its Prefabs-only fixture glob with `../KhaozEngine.MapDoc.Tests/**/*Fixtures.cs`, matching the released Editor test pattern. This includes SupportFixtures and its Cave/Scope/FormatFour dependency fixtures, with no test-assembly reference. Editor's existing recursive glob already includes Markers, so it needs no project edit.

**Interfaces:**
- Consumes released `MapPrefabResolver.Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits? limits = null) -> MapEffectiveResources` at :24, `MapEffectiveResources.Acquire(MapSurfaceScope scope) -> MapScopedSurfaces`, `CompleteView() -> MapScopedSurfaces`.
- Consumes internal released `MapSupportQuery.SampleSurfaceExact(MapExactPoint world, string surfaceId) -> MapSupportResult` and `SelectExact(MapSupportRequest request, MapExactPoint world) -> MapSupportResult` at Support/MapSupportQuery.cs:31-33, `MapPrefabResolver.WorldRequest(MapSupportBinding binding, MapEffectiveTransform pose) -> MapSupportBinding` at :307 and `LocalRequest` at :299.
- Consumes released `MapSpaceMembership(MapScopedSurfaces scoped)`, `QueryExact(MapExactPoint world) -> MapMembershipResult`, returning Status, SpaceId, ParentSpaceIds and Witness, at Spaces/MapSpaceMembership.cs:10-68. No column API.
- Produces `MapMarkerHeightKind { Explicit, Support }`, `MapMarkerHeightProvenance(MapMarkerHeightKind Kind, MapSupportBinding? Binding, MapSupportResult? Support)` and additive init-only `MapEffectiveMarker.HeightProvenance: MapMarkerHeightProvenance?` and `ArchetypeId: string?`. Expansion always fills provenance, including payload 1 as Explicit. Preserve the released positional constructor.
- Produces internal `MapMarkerCandidate(MapLocalMarkerDoc Value, MapEffectiveTransform Frame, string? InstanceId)` and `MapMarkerExpansion.Capture(MapDocument document, MapAssetClosure assets) -> IReadOnlyList<MapMarkerCandidate>`. Capture reads root globals and verified prefab payloads, reuses released reconciliation/remapping and exact frames, does not resolve geometry or allocate. Its bound slots and preflight match the main expansion. Main expansion and Task 6 anchors share it.
- Produces internal `MapMarkerResolution.Resolve(IReadOnlyList<MapMarkerCandidate> candidates, IMapSurfaceSource surfaces, IReadOnlyList<string> assetSha256) -> IReadOnlyList<MapEffectiveMarker>` and public `MapMarkerSpaces.Query(MapScopedSurfaces view, MapEffectiveMarker marker) -> MapMembershipResult`.
- Produces pure `MapMarkerEffects.Describe(IReadOnlyList<MapEffectiveMarker> before, IReadOnlyList<MapEffectiveMarker> after) -> MapNativeEditEffects`, and internal `NativeMarkerEffects.Combine(MapNativeEditEffects effects, MapEffectiveResources before, MapEffectiveResources after) -> MapNativeEditEffects`. Task 2 establishes the spatial diff. Task 4 adds name-change rows to that same diff.

- [ ] **Step 1: Write failing tests.** `StackedCaveMarkerSupport_DoesNotSnapToSurface` uses EightStacked at (0.5,0.5), binding Surface floor-2, then bounded Space level-2 with ReferenceY 20.2, SearchBelow/Above 0.5. Both return Y 20 with the named support, never floor-7 at 70. Removing floor-2 must refuse, with no fallback. `UndergroundMarker_ExplicitHeightAndLayerSurviveReload` uses the released stacked prefab-cave fixture at (2,-57.75,-2), both storage forms, returning deep-room and parent deep-cave. EightStacked uses its released positive-Z frame, while the prefab fixture uses ImportedMetreCentimetre's negative-Z rows.

```csharp
Assert.Equal(20f, marker.Position.Y.ToSingle());
Assert.Equal("floor-2", marker.HeightProvenance!.Support!.Face!.Value.OwnerId);
Assert.Equal(MapSupportStatus.Supported, marker.HeightProvenance.Support.Status);
Assert.Equal(MapMarkerHeightKind.Support, marker.HeightProvenance.Kind);
Assert.Equal(-57.75f, underground.Position.Y.ToSingle());
Assert.Equal(MapMembershipStatus.Resolved, membership.Status);
Assert.Equal("deep-room", membership.SpaceId);
Assert.Equal(new[] { "deep-cave" }, membership.ParentSpaceIds);
```

`LocalCaveMarker_TransformsOnceAndKeepsVolumeAssociation` uses an actual local room marker at (2,1,-2), instance (-12.25,-450,7.125), yaw 0, scale 2. Expect (-8.25,-448,3.125), its remapped room and authored parent. Author the transformed parent with the released fixture's AuthorWorldParent path. Additional 0.371 yaw and scales 0.8/1.2 compare independently calculated quantized points, not the product transform as an oracle. Query a spaceless explicit point and assert Outside with empty parents. Missing geometry, ambiguity, capacity and nonrepresentability remain distinct statuses, never Outside.

`BoundMarkerUnpackExtract_KeepsIntentAndTransformsOnce` exercises actual MapUnpackPrefab, extraction and replacement. Compare height mode, world binding, exact point, ordered tags, instance provenance and yaw modulo a turn before/after save/reload. Regrade the transferred floor and prove the marker follows. A same-text Local Surface `floor` and World Surface `floor` remap only the declared local slot. Include local Space reference/search scaling, owner removal/restore and invalid slot positive controls.

`BoundMarkerReseat_NamesBothPointsAndRefusesLostOwner` executes a real TerrainEditCommand that raises floor-2 from 20 to 21 at a storage seam. Use a Surface-bound marker for this regrade so the separate bounded-Space fixture's search interval is not accidentally exceeded. Marker descriptor bytes stay unchanged, but effects include ID `bound`, old/new point boxes at Y 20/21 and Residency. Compare all pre/post effective markers, not only IDs already named by the terrain edit. Physics/Nav flags already caused by terrain are retained, none added by markers. The removal candidate removes dependent space topology too, so the same edit succeeds without the bound marker and the marker binding is the actual refusal. Failed removal preserves document, hash, closure, history, events and dirty state. Add an unrelated explicit point at Y 12.25 that stays fixed.

- [ ] **Step 2: Run red** `wa_test t2-red "$MAPDOC" "FullyQualifiedName~MapMarkerHeightTests|FullyQualifiedName~MapMarkerMembershipTests"`, then `wa_test t2-red-editor "$EDITOR" "FullyQualifiedName~NativeMarkerSupportEditTests"`. Expect missing resolution/provenance or omitted bound-marker effects.
- [ ] **Step 3: Implement** capture, support resolution and point membership. Install effective geometry first, then transform marker XZ once. Explicit local Y composes once through the same frame. Bound Y is the world support answer, with no second frame translation or scale. Extend the typed walker with `/value/height/binding/surfaceId` as kind Surface and `/value/height/binding/spaceId` as kind Record, requiring exactly the active binding's slot. Local $parent uses the released supplied-parent rule. World slots stay world even when their text matches a local key. Transform local ReferenceY/search distances once via WorldRequest. Preserve bindings, neutral carrier and precise frame through unpack/extract, never bake a bound marker into explicit Y.

Acquire per-point scopes with existing budgets and exact XZ, keeping the returned witness. A Surface call names only its owner. A Space call uses the complete declared vertical interval. No LegacyFallback, missing or capacity answer counts as support. Resolve disabled bindings too. Publish immutable provenance and include stable selection facts in annotation identity, excluding snapshot-specific witness data. For payload 2, effective BuildingIntent retains neutral Position.Y and its world-space Height choice. The resolved XYZ belongs to MapEffectiveMarker, so effective annotation validation cannot mistake a sampled Y for a second authored height. Preserve payload-1 effective behavior. No new collider, region disc or nav geometry is created.

Combine pre/post marker diffs for every native transaction with either marker set or support dependencies, even when writes.BuildingIntent is false. Do not retain the early return at NativePlacementEffects.cs:159 for a terrain edit that can reseat markers. Refuse invalid post-edit support before publication. Add old and new resolved point extents to Residency without widening Physics/Nav merely because the marker moved. Reuse the transaction's lazy before/after expansions, not another expansion per marker. Bound candidate/read counters, with small exact-limit and over-limit tests and unchanged F-R12/F-R13 controls.

- [ ] **Step 4: Run green** with both Step 2 filters as `t2-green` and `t2-green-editor`, then `wa_test t2-heads "$MP" "FullyQualifiedName~MapMarkerSupportWorldTests"`. Independently reloaded client/server builds expose identical points, provenance and membership. Run `wa_sweep t2-final` after review/fixes.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t2_paths=(
    KhaozEngine.MapDoc/Markers/MapMarkerExpansion.cs
    KhaozEngine.MapDoc/Markers/MapMarkerResolution.cs
    KhaozEngine.MapDoc/Markers/MapMarkerSpaces.cs
    KhaozEngine.MapDoc/Markers/MapMarkerHeight.cs
    KhaozEngine.MapDoc/Editing/MapMarkerEffects.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveResources.cs
    KhaozEngine.MapDoc/Prefabs/MapEffectiveAnnotations.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabReferences.cs
    KhaozEngine.MapDoc/Editing/MapPrefabEditPreparation.cs
    KhaozEngine.MapDoc/Editing/MapPrefabExtraction.cs
    KhaozEngine.MapEditor/NativeMarkerEffects.cs
    KhaozEngine.MapEditor/NativeDocumentTransaction.cs
    KhaozEngine.MapEditor/NativePlacementEffects.cs
    KhaozEngine.MapDoc.Tests/Markers/MarkerSupportFixtures.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerHeightTests.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerMembershipTests.cs
    KhaozEngine.MapDoc.Physics.Tests/MapMarkerSupportWorldTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerSupportEditTests.cs
    KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj
)
wa_finish t2 "${wa_r6_t2_paths[@]}" &&
git add -- "${wa_r6_t2_paths[@]}" &&
git commit --only -m "feat(mapdoc): resolve marker support and publish bounded edit consequences" -- "${wa_r6_t2_paths[@]}"
```

### Task 3: Unique names, lookup, roles and projections

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerRegistry.cs`, `MapMarkerSpatialIndex.cs`, `MapMarkerProjection.cs`, `MapMarkerRoles.cs`.
- Modify: Task 2's `Markers/MapMarkerExpansion.cs`, `KhaozEngine.MapDoc/Prefabs/MapPrefabValidation.cs:34-144,342-374`, `KhaozEngine.MapDoc/Identity/MapPrefabIdentity.cs` loaded/stored ownership validation hooks, `Editing/MapPrefabEdits.cs:11-12,44-52`, `Prefabs/MapPrefabJson.cs:89-96`, `KhaozEngine.MapEdit.Tool/Tools/PrefabTools.cs:29-32` for atomic initial overrides.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MarkerLookupFixtures.cs`, `MapMarkerRegistryTests.cs`, `MapMarkerProjectionTests.cs`.
- Test/modify: `KhaozEngine.MapDoc.Tests/Prefabs/PrefabVillageFixtures.cs`, `PrefabCaveFixtures.cs:185-214`, `KhaozEngine.MapEditor.Tests/MapEditTool/PrefabVillageWorkflowTests.cs:109-137,188-210`. Adapt repeated-instance fixture setup through explicit marker replace overrides, retaining every existing assertion unrelated to names.
- Modify docs: `CHANGELOG.md`, `KhaozEngine.MapDoc/README.md` marker contract and R6 handoff.

**Interfaces:**
- Consumes Task 2 candidates, `MapEffectiveAnnotations.Markers(MapEffectiveResources resources) -> IReadOnlyList<MapEffectiveMarker>` at Prefabs/MapEffectiveAnnotations.cs:27 and `MapBuiltWorld.Markers` at Physics/MapBuiltWorld.cs:160. The registry owns no document store.
- Consumes released `MapPrefabReplaceOverride(MapPrefabChildKey Child, MapPrefabChild Value)` and `MapSetPrefabOverride(string InstanceId, MapPrefabOverride Override)` in Prefabs/MapPrefabOverrides.cs and Editing/MapPrefabEdits.cs:14.
- Produces init-only `MapPlacePrefab.InitialOverrides: IReadOnlyList<MapPrefabOverride>`, default empty, preserving its released constructor. These are request data, persisted only as the instance's existing Overrides. Add internal `MapPrefabJson.ReadOverrides(string json) -> IReadOnlyList<MapPrefabOverride>` using the same closed override codec. Preserve the released PrefabTools.PrefabPlace forwarding signature, register one `PrefabPlaceWithOverrides(string instanceId, string resourceId, float x, float y, float z, float yaw, float scale, string? parentRefJson = null, bool numericLeaves = true, string? overridesJson = null) -> MapPrefabMutationResult` under `prefab_place`. Do not register two tools with that name.
- Produces `MapMarkerRegistry(IReadOnlyList<MapEffectiveMarker> markers)` with `All` and `Enabled` read-only lists, `FindById(string id) -> MapEffectiveMarker?`, `FindByName(string name) -> MapEffectiveMarker?`, `ByRole(string role, bool enabledOnly) -> IReadOnlyList<MapEffectiveMarker>`, `ByTag(string tag, bool enabledOnly) -> IReadOnlyList<MapEffectiveMarker>`.
- Produces `MapMarkerRect(double MinX, double MinZ, double MaxX, double MaxZ)`, `MapMarkerMapPoint(MapEffectiveMarker Marker, MapMembershipResult Space)` and `MapMarkerRegistry.InRect(MapMarkerRect rect, bool enabledOnly, Func<MapEffectiveMarker, MapMembershipResult> membership) -> IReadOnlyList<MapMarkerMapPoint>`. Caller delegates to Task 2 point membership in a matching acquired view. Retain all refusal statuses in Space. A rectangle filter does not claim complete space membership.
- Produces internal `MapMarkerSpatialIndex` and test-visible `MapMarkerQueryWork(int NodesVisited, int PointsTested)`. Instrument an internal query overload, no public performance/configuration knob.
- Produces `MapMarkerFinding(string MarkerId, string Code, string Detail)`, `MapMarkerRoleCatalog(IReadOnlyCollection<string> roles)` and `Validate(MapMarkerRegistry registry) -> IReadOnlyList<MapMarkerFinding>`. Unknown role yields code `unknown-role`, does not make MapDoc structurally invalid.
- Produces `MapPlayerStartView(MapEffectiveMarker Marker)`, `MapSpawnView(MapEffectiveMarker Marker, string? ArchetypeId)`, `MapLandmarkView(MapEffectiveMarker Marker)`, and static `MapMarkerProjection.PlayerStarts(MapMarkerRegistry registry, string role, bool enabledOnly) -> IReadOnlyList<MapPlayerStartView>`, `Spawns(MapMarkerRegistry registry, string role, bool enabledOnly) -> IReadOnlyList<MapSpawnView>`, `Landmarks(MapMarkerRegistry registry, string role, bool enabledOnly) -> IReadOnlyList<MapLandmarkView>`. These lossless wrappers retain XYZ, yaw, tags, enabled, name, ID and height provenance, rather than returning XZ-only legacy DTOs.

- [ ] **Step 1: Write failing tests.** `DisabledAndOverriddenNames_ValidateBeforeAnyBuild` places the same payload-1 marker in two live instances, then a disabled world marker with the same name. Loaded and stored bound validation refuse duplicates before a support source callback or static installer runs. Give instance b an explicit replace override named `arrival-b`, and the world marker `arrival-world`, to pass. Remove/restore reuses ID and restores the same conflict if no override remains.

```csharp
Assert.Contains(errors, e => e.Contains("duplicate marker name", StringComparison.Ordinal));
Assert.Equal(0, supportReads);
Assert.Equal(0, staticInstalls);
Assert.Equal(new[] { "arrival-a", "arrival-b", "arrival-world" }, registry.All.Select(m => m.Name).Order(StringComparer.Ordinal));
Assert.Equal("a/entry", registry.FindByName("arrival-a")!.Id);
Assert.Null(registry.FindByName("Arrival-a"));
Assert.Equal(3, registry.All.Count);
Assert.Equal(2, registry.Enabled.Count);
Assert.Equal(new[] { "second", "first" }, disabled.Tags);
Assert.Equal("unknown-role", Assert.Single(findings).Code);
Assert.Equal("unregistered-role", registry.FindById("disabled")!.Role);
```

`TypedViews_RetainEveryValueAndUseOnlyCallerRoles` supplies `start-role`, `actor-role`, `label-role`. Check disabled values in exhaustive wrappers, enabled filtering, null archetype for tag-driven spawns and `kit.actor` for a payload-2 archetype. No engine role constant is introduced. Unknown catalog roles return stable findings in ID order. `Landmark_IsPointNotRegionDisc` checks unchanged Regions, surfaces, placements and geometry after adding a landmark.

`SecondInstance_InitialNameOverridePlacesAtomically` drives the actual MapPlacePrefab command, MutationService.PrefabApply and prefab_place wire adapter. Place a first instance named arrival-a, then a second with a marker replace override named arrival-b already in its request. Assert two accepted instances and two unique names, unchanged original asset bytes and exact binding/ID reuse after undo/redo. The same second request without overrides refuses without an instance, reservation or history advance. Duplicate initial override keys refuse before allocation. This test belongs in the existing PrefabVillageWorkflowTests, whose wire class already satisfies W-R1.

`HalfOpenRect_ContainsNegativeMinAndExcludesBothMaxEdges` uses XZ points (-1,-1), (0,-1), (-1,0), (0,0), (-0.25,-0.5). Query [-1,0) by [-1,0) returns only the first and last, ID ordered. Zero-width/height returns empty, reversed/nonfinite bounds refuse. Adjacent rectangles contain a boundary point once. `RectangleWork_IsCandidateBounded` uses 257 invented spaced points, a tiny query and a callback counter. Assert PointsTested <= 16, NodesVisited <= 32, membership calls equal result count, and equality with an independent exhaustive predicate. This is one bounded unit fixture, not a load test.

- [ ] **Step 2: Run red** `wa_test t3-red "$MAPDOC" "FullyQualifiedName~MapMarkerRegistryTests|FullyQualifiedName~MapMarkerProjectionTests"`. Expect missing views or released duplicate-name acceptance.
- [ ] **Step 3: Implement** a defensive immutable registry, ordinal dictionaries, ID-ordered indexes and typed wrappers. Metadata-only enumeration validates effective names after current overrides but before any build/query callback. Invoke that same check on complete loaded and stored closure-bound validation, with no recursive call through full expansion. Partial views may not certify whole-world uniqueness. Keep exact points and ordered tags, with no deduplication of tags or name normalization.

Build the spatial index once per registry, choose the longest XZ extent for each median split and use ordinal IDs as ties. Query finite rectangles through node bounds, then exact half-open point tests. Do not enumerate every possible grid cell or scan the full marker list per chunk. Apply enabled filtering without discarding the exhaustive data. Membership uses point queries only for accepted candidates.

Update the R5 village workflow to name repeated instances distinctly through replace overrides, before publication of a duplicate state. MapPrefabEdits seeds a new instance's Overrides from the detached InitialOverrides request before planning bindings and validating names. No invalid intermediate instance is published. Immutable asset updates must remain atomic across referring instances and existing overrides. Correct other repeated-instance fixtures found by the three-project sweep in their owning fixture files, explicitly listing those paths before commit. CHANGELOG calls out the global-name compatibility change and format-8 identity move in the current staged entry, with no version bump in this task.

- [ ] **Step 4: Run green** with Step 2's filter as `t3-green`, then `wa_test t3-village "$EDITOR" "FullyQualifiedName~PrefabVillageWorkflowTests"`. Run `wa_sweep t3-final` after review/fixes. Every old geometry assertion stays present.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t3_paths=(
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
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabVillageFixtures.cs
    KhaozEngine.MapDoc.Tests/Prefabs/PrefabCaveFixtures.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/PrefabVillageWorkflowTests.cs
    KhaozEngine.MapDoc/README.md
    CHANGELOG.md
)
wa_finish t3 "${wa_r6_t3_paths[@]}" &&
git add -- "${wa_r6_t3_paths[@]}" &&
git commit --only -m "feat(mapdoc): enforce unique marker names and expose indexed role views" -- "${wa_r6_t3_paths[@]}"
```

### Task 4: Pure marker edits, undoable commands and wire parity

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapMarkerEdits.cs`, `KhaozEngine.MapEditor/NativeMarkerCommands.cs`, `KhaozEngine.MapEdit.Tool/MutationService.Markers.cs`, `Tools/MarkerTools.cs`.
- Modify: `KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs:68-76,100-108`, Task 2's `Editing/MapMarkerEffects.cs`, `KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs:89-96`, `KhaozEngine.MapEdit.Tool/Tools/McpBootstrap.cs:29-38`, `KhaozEngine.MapEditor.Tests/MapEditTool/McpAdapterTests.cs:94-108`.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MapMarkerEditTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerCommandTests.cs`, `MarkerEditorFixtures.cs`, `MapEditTool/MarkerServiceTests.cs`, `MarkerWireTests.cs`.
- Modify docs: MapDoc, MapEditor and MapEdit.Tool READMEs, including the verb table/count at MapEdit.Tool/README.md:311.

**Interfaces:**
- Consumes released `MapPrefabEditResult(MapDocument After, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects)` at Editing/MapPrefabEdits.cs:20, `MapNativeWriteSet.BuildingIntent`, `NativeResourceTransforms`, `Prefabs` and the released bound transaction/state pattern in NativeInteriorCommands.cs:9-23.
- Consumes internal `MapEditSession.ExecuteNativeCommand(EditorCommand command) -> MapNativeEditEffects` at MapEditSession.NativeHistory.cs:51, `MutationService.InteriorApply(MapInteriorEdit edit) -> MapPrefabMutationResult` at MutationService.Prefabs.cs:20, and the existing `MapPrefabMutationResult(MutationResult Mutation, MapNativeEditEffects Effects)`.
- Produces `MapMarkerEdit(string Label)` abstract record, `MapSetMarker(MapLocalMarkerDoc Marker)` and `MapRemoveMarker(string Id)`, plus `MapMarkerEdits.Prepare(MapDocument document, MapAssetClosure assets, MapMarkerEdit edit) -> MapPrefabEditResult`.
- Produces `MarkerEditCommand(MapMarkerEdit edit) : EditorCommand, INativeBoundDocumentCommand`, `MutationService.MarkerApply(MapMarkerEdit edit) -> MapPrefabMutationResult` and `MapPrefabJson.ReadMarker(string json) -> MapLocalMarkerDoc` internal closed decoder.
- Produces wire `MarkerTools.MarkerSet(string markerJson) -> MapPrefabMutationResult`, `MarkerRemove(string id) -> MapPrefabMutationResult`, `MarkerList(string? name = null, string? role = null, string? tag = null, bool enabledOnly = false) -> IReadOnlyList<MapEffectiveMarker>`, named `marker_set`, `marker_remove`, `marker_list`. The marker JSON's ID is the upsert key. Name is an exact optional filter, never a key or implicit rename.
- Produces `MapMarkerNameChange(string MarkerId, string? Before, string? After)` and additive `MapNativeEditEffects.MarkerNameChanges: IReadOnlyList<MapMarkerNameChange>`, default empty. Describe writes sorted `{markerId,before,after}` rows only when nonempty. Undo reverses them, redo restores them.

- [ ] **Step 1: Write failing tests.** `MarkerSet_ByIdKeepsIdentityAndExactHistory` starts with the basic point, changes name to `arrival-new`, tags to `["second","first","first"]`, enabled false and explicit Y 13.125. Apply through pure preparation, EditorHistory and session. Undo/redo must reproduce exact document bytes, height, tags and enabled state. Save/reload checks data, not persisted history. Rename changes the name, not ID `m`.

```csharp
Assert.Equal("m", marker.Id);
Assert.Equal(new[] { "second", "first", "first" }, marker.Tags);
Assert.Equal(13.125f, marker.Position.Y.ToSingle());
Assert.False(marker.Enabled);
Assert.Equal(new MapMarkerNameChange("m", "arrival", "arrival-new"), Assert.Single(effects.MarkerNameChanges));
Assert.Equal(MapNativeInvalidation.Residency, effects.Invalidates);
Assert.Equal(new[] { "m" }, effects.MarkerIds);
Assert.Equal(MapNativeInvalidation.None, effects.Invalidates & (MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Unbounded));
```

`MetadataOnlyMarkerSet_HasIdAndRenameButNoBounds` edits name, role, tags and archetype independently. Assert Invalidates None, OldBounds/NewBounds null, MarkerIds `["m"]`, correct name changes only for the rename. Position/yaw/enabled/binding changes each get Residency and both resolved point boxes, including a binding change to equal-height support. Create/delete have exactly one null box. Never call prefab Finish unchanged.

`PrefabBoundMarkerSet_RefusesWithOverrideGuidance` addresses a live bound ID and asserts the message contains `prefab_override`. A transferred standalone marker is editable, preserving precise frame, transfer identity and retired reservation on remove. `RenameConflict_WritesNothing` compares MarkerEditorFixtures.State before/after rejection, including no history/event advance. Invalid height, duplicate ID/name and foreign ownership have positive controls.

`GuiServiceAndWire_HaveIdenticalMarkerDocumentsEffectsAndRejection` constructs the real command used by the GUI without booting a window, the actual service, and an MCP client. Exercise set, metadata rename, spatial move, disable, remove, undo/redo and a rejected duplicate name. Compare canonical documents, effects Describe, marker rows, identity and history labels. Wire tests use McpWire.CallAsync for each request and `[Collection(McpWireCollection.Name)]`, never one workflow-wide token. Inventory is the released 92 tools plus these three = 95.

- [ ] **Step 2: Run red** `wa_test t4-red-doc "$MAPDOC" "FullyQualifiedName~MapMarkerEditTests"`, then `wa_test t4-red "$EDITOR" "FullyQualifiedName~NativeMarkerCommandTests|FullyQualifiedName~MarkerServiceTests|FullyQualifiedName~MarkerWireTests"`. Expect absent command/verbs or overly broad effects.
- [ ] **Step 3: Implement** pure detached preparation, preserving unrelated BuildingIntent fields. Validate IDs, names, binding and post-edit geometry before returning. A native marker command requires the matching verified closure. Publish BuildingIntent plus only the transform/transfer roots actually changed. Reuse NativePrefabCommandState's accepted before/after preparation so rejection and retry do not consume history state. No raw generic callback substitute.

An absolute marker set on a transferred standalone marker must reconcile its precise frame without rounding unchanged components or double-transforming the new world point. Metadata-only updates retain that frame verbatim. Remove retires its active transfer and removes only its own transform. Add/set cannot seize a dormant reservation. This lifecycle remains within released reservation rules, with no hidden ID reuse or allocation.

Return narrow effects from Task 2's diff with explicit metadata name rows, and prevent later generic resource combination from converting metadata to Residency/Unbounded. Register MarkerTools with MapPrefabJson.ToolOptions. Update pinned inventory and README count together to 95. GUI inspector widgets and actual native editor host remain R9, this task proves the command path they invoke.

- [ ] **Step 4: Run green** with both Step 2 filters as `t4-green-doc` and `t4-green`, then `wa_test t4-inventory "$EDITOR" "FullyQualifiedName~ToolRegistry_ExposesAllDocumentQueryMutationVerbs"`. Run `wa_sweep t4-final` after review/fixes.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t4_paths=(
    KhaozEngine.MapDoc/Editing/MapMarkerEdits.cs
    KhaozEngine.MapDoc/Editing/MapMarkerEffects.cs
    KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapEditor/NativeMarkerCommands.cs
    KhaozEngine.MapEdit.Tool/MutationService.Markers.cs
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
wa_finish t4 "${wa_r6_t4_paths[@]}" &&
git add -- "${wa_r6_t4_paths[@]}" &&
git commit --only -m "feat(mapedit): add shared marker commands and MCP verbs" -- "${wa_r6_t4_paths[@]}"
```

### Task 5: Explicit legacy spawn adoption and native write fence

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerAdoption.cs`, `KhaozEngine.MapEditor/NativeMarkerAdoptionCommands.cs`, `NativeLegacySpawnAdmission.cs`, `KhaozEngine.MapEdit.Tool/MutationService.MarkerAdoption.cs`.
- Modify: `KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs:12-27`, `KhaozEngine.MapEditor/NativeDocumentSnapshot.cs` named publication, `NativeDocumentTransaction.cs:58-117`, `KhaozEngine.MapEdit.Tool/MutationService.cs` existing spawn entry points only, `Tools/MarkerTools.cs` from Task 4, `Tools/MutationTools.cs:69-140,403-430` descriptions, `Prefabs/MapPrefabJson.cs` adoption decoder.
- Test/create: `KhaozEngine.MapDoc.Tests/Markers/MarkerAdoptionFixtures.cs`, `MapMarkerAdoptionTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerAdoptionTests.cs`, `MapEditTool/MarkerAdoptionWireTests.cs`.
- Test/modify: `KhaozEngine.MapEditor.Tests/MapEditTool/McpAdapterTests.cs`, `NativeGenericHistoryTests.cs:118` native legacy-list mutation expectation.
- Modify docs: MapDoc, MapEditor and MapEdit.Tool READMEs. MutationService.cs is baselined at 1450 lines and must not grow, move one cohesive released spawn dispatch concern into the new partial if needed, keeping public signatures. If it shrinks, ratchet `.filesize-baseline:42` down with `wa_run t5-ratchet sh scripts/check-file-size.sh --update`, inspect that only task-owned baseline entries changed, and include the baseline in this commit.

**Interfaces:**
- Consumes released MapSpawn fields Id, ArchetypeId, X, Z, Enabled, Tags and MapPlayerSpawn fields Id, X, Z, Yaw, Enabled, Tags at MapDocument.cs:183-207. Neither stores Y. Consumes Task 1 height choice and Task 4 edit/result shapes.
- Consumes released `MapResolverAdoption.ConvertToAuthoredSupport(MapDocument document, IReadOnlyDictionary<string, MapPlacementSupportChoice> choices) -> MapDocument` at Support/MapResolverAdoption.cs:13 as the detached-copy precedent, not as a spawn converter.
- Produces `MapLegacySpawnKind { Spawn, PlayerSpawn }`, `MapLegacySpawnKey(MapLegacySpawnKind Kind, string Id)`, `MapMarkerAdoptionChoice(string MarkerId, string Name, string Role, MapMarkerHeight Height)` and `MapMarkerAdoption.ConvertSpawns(MapDocument document, IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> choices) -> MapDocument`.
- Produces `AdoptSpawnsCommand(IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> choices) : EditorCommand, INativeBoundDocumentCommand`, with one captured accepted before/after state, plus `MutationService.SpawnsAdopt(IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> choices) -> MapPrefabMutationResult`.
- Produces wire `MarkerTools.SpawnsAdopt(string choicesJson) -> MapPrefabMutationResult`, named `spawns_adopt`. JSON is a closed array of `{kind,id,markerId,name,role,height}` rows, refusing unknown/duplicate fields and duplicate source keys before dictionary construction. Produces internal `MapPrefabJson.ReadMarkerAdoption(string json) -> IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice>`.
- Adds `MapNativeWriteSet.LegacySpawns: bool`, default false, for both legacy lists together. Only the internal adoption command admits changed legacy lists, including its exact undo restoration. Generic native snapshots cannot use that permission.

- [ ] **Step 1: Write failing tests.** `SameSourceIdInTwoLists_RequiresDistinctExplicitDestinations` uses a valid monolithic native document with NPC and player source ID `old`, X -0.25, Z -1.5, NPC archetype `kit.actor`, tags `["second","first"]`, disabled NPC, player yaw 0.371. LegacyLists authors support `adoption-floor` at Y 20 covering X/Z [-2,2], with no competing support. Choose destination IDs `actor-new` and `start-new`, names `actor-home` and `player-home`, caller roles, explicit Y 12.25 and bound adoption-floor respectively. Missing/extra choices, duplicate destinations/names, existing live or reserved marker IDs and both/neither height branches refuse without mutation.

```csharp
Assert.Equal(2, original.Spawns.Count + original.PlayerSpawns.Count);
Assert.Empty(adopted.Spawns);
Assert.Empty(adopted.PlayerSpawns);
Assert.Equal("kit.actor", actor.ArchetypeId);
Assert.False(actor.Enabled);
Assert.Equal(new[] { "second", "first" }, actor.Tags);
Assert.Equal(0f, actor.YawRadians);
Assert.Equal(0.371f, player.YawRadians);
Assert.Equal(12.25f, actor.Position.Y.ToSingle());
Assert.Equal(20f, player.Position.Y.ToSingle());
Assert.Equal(originalBytes, MapDocumentFile.SaveText(original));
```

`Adoption_UndoRedoRestoresExactListsMarkersAndIdentity` executes one command, checks one history entry and one event, clears both lists, undoes to the exact source records and restores the same marker IDs/height modes on redo. Support changes after adoption reseat only bound markers. `AdoptionEqualsExplicitMarkerCommands` compares complete adopted documents/effective rows with marker_set construction from the same choices after deliberately clearing source lists on a detached expected document.

`NativeLegacySpawnWrites_RefuseAcrossEveryEntry` drives all five spawn verbs, all six player_spawn verbs, GUI legacy commands, element_duplicate for both kinds, session generic callbacks and complete snapshot commands. Every native attempt refuses with a message containing `marker_set`, `marker_remove` and `spawns_adopt`, no state advance. Unchanged old lists continue loading, saving and surviving unrelated marker edits. `AnalyticSpawnVerbs_KeepReleasedBehavior` performs add/move/rename/enable/yaw/remove and undo/redo on analytic maps with their existing signatures and results.

- [ ] **Step 2: Run red** `wa_test t5-red-doc "$MAPDOC" "FullyQualifiedName~MapMarkerAdoptionTests"`, then `wa_test t5-red "$EDITOR" "FullyQualifiedName~NativeMarkerAdoptionTests|FullyQualifiedName~MarkerAdoptionWireTests"`. Expect absent adoption or a generic native callback still accepting new legacy records.
- [ ] **Step 3: Implement** detached native-only adoption. Key choices by kind plus source ID. Require exact exhaustive coverage and validate all destinations against the full marker/reservation inventory before constructing output. Preserve source XZ, ordered tags, enabled and archetype, copy player yaw, use NPC yaw 0. Copy Height without sampling in this pure API. Structural output validation runs here, closure/support/name validation runs in bound command preparation before acceptance. Clear both source lists only after every record converts. Partial documents refuse. Analytic adoption refuses and leaves released analytic editing available.

Publish both lists and BuildingIntent atomically through LegacySpawns. The outer native transaction compares before/after legacy lists and rejects changes unless the staged command is the internal adoption command replaying its captured state. No public generic flag authorizes a bypass. Undo can restore the exact old records, not invent new ones. Keep load-time validation permissive for old native lists. Add explicit marker guidance at native spawn entry points before the old generic atomic-command refusal. Preserve analytic behavior and all released method signatures.

Update inventory and README together from 95 to 96 tools. Wire adoption tests use the same closed choices and command as the GUI/service path and W-R1 per-call timeout/collection.

- [ ] **Step 4: Run green** with both Step 2 filters as `t5-green-doc` and `t5-green`, then `wa_test t5-compat "$EDITOR" "FullyQualifiedName~NativeGenericHistoryTests|FullyQualifiedName~ToolRegistry_ExposesAllDocumentQueryMutationVerbs|FullyQualifiedName~Spawn"`. Run `wa_sweep t5-final` after review/fixes.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t5_paths=(
    KhaozEngine.MapDoc/Markers/MapMarkerAdoption.cs
    KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapEditor/NativeMarkerAdoptionCommands.cs
    KhaozEngine.MapEditor/NativeLegacySpawnAdmission.cs
    KhaozEngine.MapEditor/NativeDocumentSnapshot.cs
    KhaozEngine.MapEditor/NativeDocumentTransaction.cs
    KhaozEngine.MapEdit.Tool/MutationService.MarkerAdoption.cs
    KhaozEngine.MapEdit.Tool/MutationService.cs
    KhaozEngine.MapEdit.Tool/Tools/MarkerTools.cs
    KhaozEngine.MapEdit.Tool/Tools/MutationTools.cs
    KhaozEngine.MapDoc.Tests/Markers/MarkerAdoptionFixtures.cs
    KhaozEngine.MapDoc.Tests/Markers/MapMarkerAdoptionTests.cs
    KhaozEngine.MapEditor.Tests/MapDoc/NativeMarkerAdoptionTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/MarkerAdoptionWireTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/McpAdapterTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/NativeGenericHistoryTests.cs
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapEditor/README.md
    KhaozEngine.MapEdit.Tool/README.md
    .filesize-baseline
)
wa_finish t5 "${wa_r6_t5_paths[@]}" &&
git add -- "${wa_r6_t5_paths[@]}" &&
git commit --only -m "feat(mapedit): adopt legacy spawns through one explicit native command" -- "${wa_r6_t5_paths[@]}"
```

### Task 6: Native consumer queries, window anchors and readouts

**Files:**
- Create: `KhaozEngine.MapDoc/Markers/MapMarkerAnchors.cs`, `MapNativePointReadout.cs`, `KhaozEngine.MapEditor/NativeMarkerReadouts.cs`, `MapEditorScene.Markers.cs`, `KhaozEngine.MapEdit.Tool/QueryService.Markers.cs`, `MapEditSession.Markers.cs`.
- Modify: `KhaozEngine.MapEditor/MapDocumentWindowing.cs:35-88`, `MapEditorOptions.cs:100-117`, `MapEditorScene.Document.cs:23-42`, `MapEditorScene.Reload.cs` load/replacement call, `MapEditorScene.Window.cs:18-36`, `MapEditorScene.Navigation.cs:108-125`, `ViewportWorld.cs:577-606`.
- Modify: `KhaozEngine.MapEdit.Tool/MapEditSession.cs:88-112,484-497,560-580`, `QueryService.cs:43-75`, `Results.cs:62-77,91-110`, `Tools/DocumentTools.cs:14-18,42-44`, `Tools/QueryTools.cs:29-34`, `KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs:89-96` for closed consumer options.
- Test/create: `KhaozEngine.MapEditor.Tests/MapEditor/NativeMarkerConsumerTests.cs`, `MapEditTool/MarkerConsumerWireTests.cs`. Extend the released `MapDocumentWindowingSpawnTests.cs` and `MapWindowSpawnSearchTests.cs` with native controls, retaining analytic tests.
- Modify docs: MapDoc, MapEditor and MapEdit.Tool READMEs. `MapEditorScene.cs` viewport construction may pass the new provider by replacing the existing line, without growing its 2955-line baseline. Any cohesive move that shrinks a baselined file ratchets only that entry down through `wa_run t6-ratchet sh scripts/check-file-size.sh --update`, followed by inspection of `.filesize-baseline`.

**Interfaces:**
- Consumes Task 2 metadata Capture, exact membership and support, Task 3 rectangle query and typed views. Consumes released `MapDocumentWindowing.Load(string path, MapDocumentLoadOptions options, int wholeWorldTileLimit, int windowRadius, int playerSpawnSearchTileLimit, out bool windowed, out MapTileRect? window) -> MapDocument` at :44.
- Consumes released `QueryService.PlacementsInRect(float minX, float minZ, float maxX, float maxZ) -> PlacementsInRectResult` at QueryService.cs:43 and `MapNativeResolution.Resolve(MapDocument document, MapAssetClosure assets, MapResolveOptions options, Func<float,float,float>? legacySupportHeight = null) -> MapResolvedDocument` at MapNativeResolution.cs:22.
- Produces `MapMarkerConsumerRoles(string PlayerStartRole, string SpawnRole, string LandmarkRole)`, `MapMarkerAnchor(string Id, MapExactXz Position, bool Enabled, string Role)` and `MapMarkerAnchors.Read(MapDocument manifest, MapAssetClosure assets) -> IReadOnlyList<MapMarkerAnchor>`. Uses Capture's precise XZ, not a full Resolve or independent transform.
- Produces `MapDocumentWindowing.Load(string path, MapDocumentLoadOptions options, int wholeWorldTileLimit, int windowRadius, int playerSpawnSearchTileLimit, out bool windowed, out MapTileRect? window, string playerStartRole, Func<MapDocument, IReadOnlyList<MapMarkerAnchor>> nativeAnchors) -> MapDocument`. Existing overloads retain analytic behavior. Metadata anchors may read a partial manifest and verified prefab payloads, never terrain/support tiles.
- Produces public `MapNativePointReadout.Support(MapEffectiveResources resources, MapExactPoint point, MapSupportBinding binding) -> MapSupportResult`, using the same released support path and scoped budgets. Invalid choices refuse explicitly, non-Supported results remain typed.
- Produces `NativeMarkerReadouts(MapDocument document, MapAssetClosure assets, MapMarkerConsumerRoles? roles, MapSupportBinding? legacySpawnSupport)` in MapEditor, with `PlayerStarts(bool enabledOnly) -> IReadOnlyList<MapPlayerStartView>` and `LegacySpawnHeight(float x, float z) -> MapSupportResult`. It owns one immutable effective snapshot per accepted document, refreshed on edit/undo/redo/reload, never per billboard.
- Adds optional `MapEditorOptions.MarkerRoles`, `Func<MapDocument, MapAssetClosure>? MarkerAssets`, and `MapSupportBinding? LegacySpawnSupport`. A native GUI readout needs the supplied verified closure, otherwise it refuses or shows unavailable, never analytic fallback. A host may supply these without Physics. R9 owns the complete real GUI host/bounds integration.
- Adds `MapEditSession.Open(string path, IReadOnlyList<string>? manifestPaths, MapMarkerConsumerRoles? roles, MapSupportBinding? legacySpawnSupport) -> OpenResult`, retaining the released two-argument overload. Stage options atomically with Open. Produces `MapMarkerConsumerOptions(MapMarkerConsumerRoles? Roles, MapSupportBinding? LegacySpawnSupport)` in MapMarkerAnchors.cs and internal `MapPrefabJson.ReadMarkerOptions(string json) -> MapMarkerConsumerOptions`, closed and duplicate-safe. Preserve the released C# DocumentTools.MapOpen forwarding method, register only `MapOpenWithMarkers(string path, string[]? manifestPaths = null, string? markerOptionsJson = null) -> OpenResult` as wire map_open. No engine role defaults and no duplicate registration.
- Produces `MapMarkerSummary(int Total, int Enabled, string? PlayerStartRole, IReadOnlyList<string> PlayerStartIds, IReadOnlyList<string> EnabledPlayerStartIds)` and additive init-only `MapSummary.Markers: MapMarkerSummary?`. Existing list counts remain explicitly documented legacy counts. Native marker starts and exhaustive/enabled IDs appear in Markers, rather than being omitted or conflated with the old lists.
- Adds `QueryService.PlacementsInRect(float minX, float minZ, float maxX, float maxZ, MapMarkerConsumerRoles? roles, MapSupportBinding? legacySpawnSupport) -> PlacementsInRectResult`. The original delegates through session options. SpawnEntry/PlayerSpawnEntry gain init-only nullable `MapSupportResult? Support`, default null, preserving constructors. Wire placements_in_rect uses session options and keeps its inclusive rectangle semantics. The new marker rectangle API remains half-open.

- [ ] **Step 1: Write failing tests.** `NativeWindowAnchor_UsesRootMarkerWithoutWholeGeometry` writes a native tiled fixture spanning negative storage tiles, with a prefab-local enabled `start-role` marker whose exact X is -64.25 and whose support tile is outside the initially selected window. Supply verified prefab bytes and a source that throws on any terrain read during anchoring. Assert the selected tile is X -2 for TileSize 64, not 0 or -1. Missing prefab bytes refuse. Disabled starts are skipped. No matching caller role uses the documented bounds-centre fallback, never a game-name guess. Include a standalone point with the same selection result and stable distance/ID ties.

```csharp
Assert.Equal(-2, selectedTile.X);
Assert.Equal(0, supportReadsDuringAnchor);
Assert.Equal(new[] { "start-a", "start-disabled" }, summary.Markers!.PlayerStartIds);
Assert.Equal(new[] { "start-a" }, summary.Markers.EnabledPlayerStartIds);
Assert.Equal("start-role", summary.Markers.PlayerStartRole);
Assert.Equal(20f, nativePlacement.Y);
Assert.Equal(20f, nativeSpawn.GroundY);
Assert.Equal(MapSupportStatus.Supported, nativeSpawn.Support!.Status);
Assert.Contains("spawns_adopt", missingChoiceMessage);
```

`NativeReadouts_UseNamedSupportAndNeverTerrainField` gives an analytic field height 900 and a native support height 20. Check bound placements through actual resolved placement transforms, old native spawns through the explicit named binding, native marker starts through typed views, viewport billboard heights and selection camera focus through NativeMarkerReadouts. A different upper floor at 70 never wins. No legacy native support choice produces a clear refusal with marker/adoption guidance. Analytic controls keep their exact old readouts and inclusive rectangle edges.

`MarkerConsumers_RefreshAfterUndoRedoAndReload` moves a bound marker, changes support and undoes/redoes, checking no cached start/summary point survives from the previous snapshot. `MarkerRectAndMapSummary_WireMatchService` compares actual wire query/summary results and disabled IDs, with W-R1. New marker lookup exposes name, exact point, ordered tags and membership to the world-map consumer, without adding a #1370 adapter here.

- [ ] **Step 2: Run red** `wa_test t6-red "$EDITOR" "FullyQualifiedName~NativeMarkerConsumerTests|FullyQualifiedName~MarkerConsumerWireTests|FullyQualifiedName~MapDocumentWindowingSpawnTests|FullyQualifiedName~MapWindowSpawnSearchTests"`. Expect bounds-centre anchoring, absent marker summary or analytic native GroundY.
- [ ] **Step 3: Implement** root metadata anchor selection before loading the final window. The callback loads verified closure metadata under the already declared storage root and containment rules, not a developer checkout search. Reuse MapMarkerExpansion.Capture, charge existing prefab preflight and preserve missing-byte refusal. Compare distances using exact/wide or binary64 differences, never integer truncation of negative tiles. Window selection does not resolve height, certify whole-world identity or relax the tool's existing complete-native-editing requirement.

Bind NativeMarkerReadouts through the new focused scene partial for initial load/reload and accepted snapshot changes. Viewport spawn drawing and selection framing consume its support results on resolver 2. No implicit fallback to GroundHeight(field,...) remains on those native paths. A missing/unresolved readout suppresses the misleading billboard/focus and returns a developer diagnostic. Keep analytic paths intact. Do not grow the baselined scene or move unrelated UI concerns.

Refactor QueryService to partial and place native branches in QueryService.Markers.cs. Read placements from actual native resolution, including effective prefab placements. Old native spawns need the configured named support. Adopted marker starts are reported in the marker summary/views without recreating old lists. Cache only against the accepted document/closure identity, with existing session locking and invalidation. Document the two rectangle conventions and the caller-owned role configuration.

- [ ] **Step 4: Run green** with Step 2's filter as `t6-green`, then `wa_sweep t6-final` after review/fixes. Architecture confirms no MapEditor to Physics edge. No windowed app is launched.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t6_paths=(
    KhaozEngine.MapDoc/Markers/MapMarkerAnchors.cs
    KhaozEngine.MapDoc/Markers/MapNativePointReadout.cs
    KhaozEngine.MapDoc/Prefabs/MapPrefabJson.cs
    KhaozEngine.MapEditor/NativeMarkerReadouts.cs
    KhaozEngine.MapEditor/MapEditorScene.Markers.cs
    KhaozEngine.MapEditor/MapEditorScene.cs
    KhaozEngine.MapEditor/MapDocumentWindowing.cs
    KhaozEngine.MapEditor/MapEditorOptions.cs
    KhaozEngine.MapEditor/MapEditorScene.Document.cs
    KhaozEngine.MapEditor/MapEditorScene.Reload.cs
    KhaozEngine.MapEditor/MapEditorScene.Window.cs
    KhaozEngine.MapEditor/MapEditorScene.Navigation.cs
    KhaozEngine.MapEditor/ViewportWorld.cs
    KhaozEngine.MapEdit.Tool/QueryService.Markers.cs
    KhaozEngine.MapEdit.Tool/MapEditSession.Markers.cs
    KhaozEngine.MapEdit.Tool/MapEditSession.cs
    KhaozEngine.MapEdit.Tool/QueryService.cs
    KhaozEngine.MapEdit.Tool/Results.cs
    KhaozEngine.MapEdit.Tool/Tools/DocumentTools.cs
    KhaozEngine.MapEdit.Tool/Tools/QueryTools.cs
    KhaozEngine.MapEditor.Tests/MapEditor/NativeMarkerConsumerTests.cs
    KhaozEngine.MapEditor.Tests/MapEditTool/MarkerConsumerWireTests.cs
    KhaozEngine.MapEditor.Tests/MapEditor/MapDocumentWindowingSpawnTests.cs
    KhaozEngine.MapEditor.Tests/MapEditor/MapWindowSpawnSearchTests.cs
    KhaozEngine.MapDoc/README.md
    KhaozEngine.MapEditor/README.md
    KhaozEngine.MapEdit.Tool/README.md
    .filesize-baseline
)
wa_finish t6 "${wa_r6_t6_paths[@]}" &&
git add -- "${wa_r6_t6_paths[@]}" &&
git commit --only -m "feat(mapedit): expose native marker starts and support-correct readouts" -- "${wa_r6_t6_paths[@]}"
```

### Task 7: Public village, stacked cave and independent heads

**Files:**
- Create: `KhaozEngine.MapDoc.Tests/Markers/MarkerVillageFixtures.cs`, `NativeMarkerAcceptanceTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapMarkerVillageAcceptanceTests.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/MarkerVillageWorkflowTests.cs`.
- Modify: `KhaozEngine.Tests/ArchitectureTests.Prefabs.cs` runtime non-edges, MapDoc, MapDoc.Physics, MapEditor and MapEdit.Tool READMEs, `docs/DEPENDENCY-SEAMS.md:974-994` marker runtime contract. Test source links from Task 2 cover the new concern fixture.

**Interfaces:**
- Consumes Tasks 1 to 6, released `MapWorldBuilder.Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) -> MapBuiltWorld` at Physics/MapWorldBuilder.cs:41 and `MapBuiltWorld.Markers`, `Surfaces`. Residency comes from `MapResidencyOwnership.BuildAnnotations(MapBuiltWorld world, MapWorldGrids grids) -> IReadOnlyList<MapResidencyEntry>` at Physics/MapResidencyOwnership.cs:99. Same builder serves both heads, with separately loaded inputs.
- Produces `MarkerFixtures.Village() -> MarkerFixture`, a complete invented native village with six uniquely named markers. Records are `start`, `actor`, `roof-label`, `cave-explicit`, `cave-bound`, `disabled-local`. They include both height modes, a payload-1 child, a payload-2 local child, negative X, an elevated point, ordered/duplicate tags and a disabled marker. Start/actor/label roles are caller values. Geometry reuses released prefab-cave and support fixtures without borrowing private content.

- [ ] **Step 1: Write failing acceptance tests.** `VillageMarkers_SaveReloadBothStorageFormsAndHeadsAgree` loads independent documents and closures for two heads, both monolithic and tiled, with the same TileSize. Compare complete value sequences, not record equality on list references. All six are exhaustive, five enabled. Verify resolved heights and height provenance, exact negative-X points, yaw modulo a turn, names, roles, tags, archetype and instance ownership. Run actual membership and half-open rectangle queries on both heads.

```csharp
Assert.Equal(6, client.Markers.Count);
Assert.Equal(5, new MapMarkerRegistry(client.Markers).Enabled.Count);
Assert.Equal(clientRows, serverRows);
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

- [ ] **Step 2: Run red** `wa_test t7-red-doc "$MAPDOC" "FullyQualifiedName~NativeMarkerAcceptanceTests"`, then `wa_test t7-red-heads "$MP" "FullyQualifiedName~MapMarkerVillageAcceptanceTests"` and `wa_test t7-red-editor "$EDITOR" "FullyQualifiedName~MarkerVillageWorkflowTests"`. A scenario already passing is recorded as a passing regression. Identify a missing integration assertion before applying a fix, never alter expected values to manufacture red.
- [ ] **Step 3: Implement** the invented complete fixture and close only reachable cross-task marker contract gaps with a regression in the owning module. Preserve F-R12/F-R13 budgets on spread geometry, F-R14 acquired frames and W-R1 wire calls. Document height modes, names/overrides, enabled versus exhaustive views, role injection, adoption and consumer seams in living package docs.

Handoff to R7 through R11 is explicit. R7 consumes generic tagged points, R8 consumes resolved annotations and residency, R9 supplies the full GUI host and inspectors, R10 owns whole-tool parity, #1370 consumes indexed half-open points, #438 consumes queried space, R11/G2 refreezes private source and owns final import/point-shift acceptance. Native routes, doors, renderer pixels and game policy are not certified by these headless tests.

- [ ] **Step 4: Run green** with the three Step 2 filters as `t7-green-doc`, `t7-green-heads`, `t7-green-editor`, then `wa_test t7-architecture "$RUMP" "FullyQualifiedName~ArchitectureTests"`. Run `wa_sweep t7-final` after review/fixes.
- [ ] **Step 5: Format, guards and commit.**

```bash
wa_r6_t7_paths=(
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
wa_finish t7 "${wa_r6_t7_paths[@]}" &&
git add -- "${wa_r6_t7_paths[@]}" &&
git commit --only -m "test(mapdoc): prove general markers across storage heads and authoring" -- "${wa_r6_t7_paths[@]}"
```

### Task 8: Private marker comparison and complete input contract

**Files:**
- Create: `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerInputs.cs`, `ShippedMarkerComparison.cs`, `ShippedMarkerComparisonTests.cs`, `ShippedMarkerInputGuardTests.cs`, `ShippedMarkerGuardFixtures.cs`.
- Modify: `KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj:13-19`, `README.md` private marker contract and format-8 scaffold clarification, `KhaozEngine.Tests/ArchitectureTests.MapDoc.cs:31-52` reference guard.
- Release-preparation docs: root README, `docs/USING-KHAOZENGINE.md`, `docs/INDEX.md`, `docs/DEPENDENCY-SEAMS.md`, the four affected package READMEs and CHANGELOG. Version/declaration paths are selected concretely at the reconciled candidate as described below, never an unreviewed broad add.
- Private prerequisite, owned and committed separately by the controller in the game repository: retained `proofs/r6-marker-oracle/generator/build-scaffold.py`, any C# helper/project it invokes, role/support correspondence manifest and usage README. No private generator/input is committed in the engine task. The plan writer creates none of these files.

**Interfaces:**
- Consumes released `PrivateOracleEntry.Run(Action<PrivateOracleInputs, PrivateOracleReport> body)` and its dictionary-environment overload at PrivateOracleEntry.cs:11-16, `PrivateOracleInputs.Require(IReadOnlyDictionary<string,string?> environment) -> PrivateOracleInputs`, `PrivateOracleReport.Record(string category, string detailJson) -> void`, `Check(string category, bool ok, string detailJson) -> void` at :60-68.
- Consumes `ShippedSourceInventory.ReadWorld(PrivateOracleInputs inputs) -> (TileWorldDocument Document, TileWorldCatalogs Catalogs)` at :30, `TileDocumentGroundHeight(TileWorldDocument document)` and `(TileWorldDocument document, TileWorldCatalogs catalogs)`, `HeightAt(float tileX, float tileZ, int plane) -> float` at TileWorld.Netcode/TileDocumentGroundHeight.cs:35-60. No copied height algorithm.
- Consumes Tasks 1 to 7, internal released `LegacyOracleConverter.ToNative(TileWorldDocument document, RegionCoord region, int plane) -> (MapSurfaceRef Surface, MapSurfacePatch Patch)` at Compatibility.Tests/LegacyOracleConverter.cs:21 only for source terrain geometry in the linked test-only scaffold path, and MapWorldBuilder from Task 7 for independent heads. Core MapDoc never references the source packages.
- Produces internal `ShippedMarkerInputs.Read(PrivateOracleInputs inputs) -> ShippedMarkerInputs`, with captured attested world/catalog bytes, source markers, native scaffold, role/support rules and expectations. Produces `ShippedMarkerReport(IReadOnlyList<string> Failures, string DetailJson)` and `ShippedMarkerComparison.Compare(ShippedMarkerInputs inputs) -> ShippedMarkerReport`, assertion-free.
- Produces fact `ShippedMarkers_PreserveNamesTagsStandingPointsAndHeight` inside PrivateOracleEntry.Run. Both the private fact class and synthetic guard class carry `[UnsupportedOSPlatform("windows")]`. Nothing reads process input during discovery/static initialization.
- Produces synthetic `ShippedMarkerGuardFixtures.Create(string variant) -> ShippedMarkerGuardFixture`. The new internal `ShippedMarkerGuardFixture : IDisposable` exposes `Environment: IReadOnlyDictionary<string,string?>` naming invented temporary inputs and `PublicSecretMarker: string`, a deliberately public sentinel. It owns its synthetic directories and an existing mode-0700 report parent with an absent output path. Dispose removes only that owned synthetic fixture.

**Full private input closure, required before any comparison run:**

Follow R4-P40 and the released harness README. The controller freezes current game main, recording the full source commit and engine pin privately. Do not compare historical source counts or carry the old draft's frozen marker inventory forward. Verify the manifest marker index against every region marker record, including signed region ownership and exact ordered tags. Every source point is accounted for once.

The extraction contains exactly one attested world.json, every listed region, every named catalog, source prefab definitions needed to establish marker ownership, the game-owned role/footprint/height manifest and support/space correspondence, the exact generator and imported helpers, the native monolithic format-8 scaffold, every native asset-root manifest and its complete transitive resource graph. Include every surface/patch/topology/room/parent dependency needed by marker support or membership, including any retained R5 building-semantic manifest and its supporting source files. Include every mesh, collider, selection, material, texture, light or LOD resource actually loaded by native closure construction. If only support geometry is needed, do not invent unrelated colliders or omit resources still advertised by a declared root. There is no implicit developer-checkout dependency.

Retain the generator in private game git before relying on its output. Generate through build-slot for any build/bake, never hand-edit a scaffold. Two mechanical generator outputs may be byte-compared for determinism, without repeating a test suite. Attest the exact retained scripts and every extraction file in provenance paths, not only selected marker rows. Regenerate and re-attest after any source, rule, script or dependency change.

Use all released ShippedSourceProvenance fields. `sourceRepository`, tag (possibly explicitly empty), full lowercase commit, sourceEnginePin, extractionPathspec, paths, aggregateSha256, aggregateRule, units, rowOrientation, comparison and oracleEquivalence are required. Aggregate is SHA-256 of UTF-8 `path TAB lowercase-sha256 LF` lines in ordinal path order. The provenance's own byte hash is the existing outer environment digest. All paths obey released containment, symlink, snapshot and post-loader recheck rules, with no fallback search. Native reloads use captured verified bytes.

Pin marker comparison schema 1, closed and duplicate-safe. Its comparison object requires `schemaVersion`, `nativeDocumentPath`, `roleManifestPath`, `markerManifestPath` and `expectations`. Expectations name total, player-start, spawn, landmark, label, negative-X, support-bound and enabled counts. Values and per-marker rows exist only in private provenance/manifests. The role manifest bijectively maps each source name to destination ID, role, footprint offsets, named support binding, height rule (`terrain` or `standing`), yaw, enabled and nullable archetype. The marker manifest records every source tile/plane and expected native support/space correspondence. Refuse missing, extra or duplicate rows, unknown fields/rules and invented source defaults.

The generator binds imports to explicit source support owners. It resolves standing points using the game-supplied footprint/role offsets, leaves archetype empty for tag-driven identity, preserves tag order, and supplies authored exterior/room spaces where the comparison claims membership. It never maps a bound home to a water surface. The source height baseline calls the released terrain-only or catalog-aware TileDocumentGroundHeight constructor selected by each attested rule. The comparator independently checks those rules against source points and compares the resolved native answer. Changed label/icon points under Decision 5 are recorded as deferred R11 acceptance, not described as exact preservation of the old corner convention.

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

- [ ] **Step 2: Run synthetic red only** `wa_test t8-red "$ORACLE" "FullyQualifiedName~ShippedMarkerInputGuardTests"`. Expect missing reader/comparator APIs or absent marker checks. The implementer never runs the shipped fact, downloads private source or runs the harness unfiltered.
- [ ] **Step 3: Implement** complete input reading and the assertion-free comparison. Check the source manifest/region inventory, bijective correspondence, effective unique name set, ID mapping, ordered tags, roles, enabled, archetype, standing-point XZ, resolved height, wrapped yaw, binding/provenance and point membership/parents. Build fresh native heads from both storage forms with matching TileSize. Compare every effective row and query, with no shared mutable head state. Check source-support fidelity using the named released source rule rather than another hand-coded sampler. Preserve negative coordinates without clamping or translation.

Use existing report.Check/Record only inside the private boundary. Private reports carry category counts and detailed failures as needed. Public output is sanitized pass/fail, fixed failure categories and secure report reference under the released boundary, never source inventory counts or source-derived exception messages. The engine repository records only pass/fail and the private record reference. No assertion, output helper, discovery data or inline test value contains private data.

Add TileWorld.Netcode as the sixth reference. ArchitectureTests must pin exactly MapDoc, MapDoc.Physics, Physics.Bepu, TileWorld, TileWorld.Netcode and TileWorld.Physics. Preserve outside-solution, unreferenced, nonpackable and public-CI exclusion checks. README explains marker schema, the four unchanged variables, format-8 native scaffold, retained generator and fixed tolerances. Clarify that old format-7 building scaffolds still load through pure migration, without regenerating the accepted R5 input merely for a version bump.

- [ ] **Step 4: Run synthetic green** `wa_test t8-green "$ORACLE" "FullyQualifiedName~ShippedMarkerInputGuardTests|FullyQualifiedName~PrivateOracleGuardTests|FullyQualifiedName~ShippedBuildingInputGuardTests"`, then `wa_test t8-architecture "$RUMP" "FullyQualifiedName~ArchitectureTests"`. Run `wa_sweep t8-final` after review/fixes. No shipped comparison runs in this worker task.
- [ ] **Step 5: Format, guards and commit.** Candidate release/doc changes are committed separately by the controller below, after current-main reconciliation. This task commit contains the harness and its reference guard only.

```bash
wa_r6_t8_paths=(
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerInputs.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerComparison.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerComparisonTests.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerInputGuardTests.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/ShippedMarkerGuardFixtures.cs
    KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj
    KhaozEngine.MapDoc.OracleHarness.Tests/README.md
    KhaozEngine.Tests/ArchitectureTests.MapDoc.cs
)
wa_run t8-oracle-format dotnet format "$ORACLE" --verify-no-changes --no-restore &&
wa_finish t8 "${wa_r6_t8_paths[@]}" &&
git add -- "${wa_r6_t8_paths[@]}" &&
git commit --only -m "test(mapdoc): compare private markers through the attested oracle boundary" -- "${wa_r6_t8_paths[@]}"
```

## Self-review and execution handoff

- [ ] Map OA36 Decisions 1 to 20 to owning tasks. Payload/format is Task 1, support/provenance/space/OA9 Task 2, names/roles/views/rectangle lookup Task 3, edits/effects/verbs Task 4, adoption/legacy fence Task 5, windowing/summary/readouts Task 6, public acceptance Task 7, private acceptance Task 8, release below. No approved item is silently deferred.
- [ ] Check every released signature/file/range against `1a4182c24`, and every new signature against its producing Interfaces block. Propagate accepted interface changes into later briefs. New helper variants belong to their first consuming task. Preserve old public constructors/overloads.
- [ ] Check payload-1 bytes against the independent released golden, version-only migration and both identity paths against canonical derivations, and analytic recorder bits unchanged. No fabricated new golden replaces a failing compatibility assertion.
- [ ] Check all five Review Focus tests, the three exact OA9 names, the #1348 marker effect counterexample, disabled duplicate names and unadopted native spawn refusal. Confirm exact values and valid fixtures before interpreting red.
- [ ] Check no marker metadata edit adds spatial flags, bound-marker reseats include both points, post-edit support loss refuses, and old/new names survive command/effect serialization and history. Support edits keep their own existing Physics/Nav flags.
- [ ] Check whole-project regression sweeps after each task, 800-line source caps, baseline growth prohibition, explicit commit path ownership and linked public fixture source boundaries.
- [ ] Check complete private closure before extraction delivery, secure reports, independent source inventory and both storage/head comparisons. No private world data, counts or digests enter this public plan or fixtures.
- [ ] Check proportion and step determinacy. No placeholder APIs, silent role/height defaults or implementation bodies whose tests already determine them. Record any review correction to the R6-P table before dependent code.
- [ ] Record independent plan reviews, accepted/corrected rulings and owner reviewed-plan approval in the private controller ledger. The plan writer stops at this docs commit. Execution remains unapproved until that record exists.

Recommended execution is the program's serial subagent-driven method after owner plan approval, one bounded implementer and fresh task review at each gate. The controller independently inspects diffs, commit containment, red/green logs and actual exit codes. A reported commit or test pass is not proof until checked. R6-P1 onward remain proposals until explicitly adjudicated. No worker pushes, integrates, packs, tags or cleans worktrees.

## Review, candidate gates, private comparison and integration

1. Review the whole branch against the approved plan, gate and carried R5 rulings. Resolve task-related findings with focused regressions and scoped re-review. Preserve unrelated work and file newly discovered work through the owning controller's ledger process. Do not widen R6 into native routes, GUI host completion or game adoption.
2. Fetch and merge current main into the task branch before final verification. Resolve there. If format 8 was taken, stop for a controller plan amendment. Do not silently renumber an approved migration. Recheck dependencies and shared APIs after reconciliation.
3. Re-read current main/origin/main, KhaozEngineVersion, newest tags and CHANGELOG. Ride untagged staged 21.1.0 if still current, otherwise the next free minor, riding an appropriate staged minor. If only a patch is staged, retain its items under the selected minor. Update Directory.Build.props, CHANGELOG and every declaration required by scripts/check-doc-versions.sh in one explicit-path commit. State the format-8 identity move, duplicate effective-name compatibility change, payload-1 byte stability, explicit adoption and native readout requirements.
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

6. Controller-only private acceptance. Deliver and verify Task 8's retained generator/input closure first. Assign `wa_r6_verified_source`, `wa_r6_private_provenance`, `wa_r6_provenance_sha256` and `wa_r6_private_report` from the private attestation. The report parent already exists at 0700 outside git and the report path is absent. Run the one shipped fact once on the exact candidate.

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
| Accepted or corrected R6-P rulings | |
| Task commits, red/green and three-project sweeps | |
| Payload-1 bytes and format-8 identity derivation | |
| Three OA9 proofs and bound-marker edit refusal | |
| Command/service/wire parity and 96-tool inventory | |
| Public synthetic storage/head acceptance | |
| Whole-branch review and main reconciliation | |
| Candidate guards, Release build/suite and hosted CI | |
| Private generator commit and complete input attestation | |
| Private marker comparison record | |
| Integration, package proof and engine-rooted release | |
| Remaining engine/game program forecast | |
