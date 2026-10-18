# World authoring R7 native foliage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. This plan is for independent review and owner approval before implementation.

**Goal:** Preserve authored density and legacy blade decisions in native MapDoc, with sparse storage, explicit support ownership and shared undoable foliage commands.

**Architecture:** Format 9 adds root foliage payload 1 independently of analytic scatter, companion layers and explicit scenery. A GPU-free prepared sampler evaluates a named support, native paint and layer-owned clearance rules once per bounded chunk, then uses the unchanged GroundCoverDistribution channels. Immutable samples and a separate dependency digest feed R8, with no physical or navigation contribution.

**Tech Stack:** C#/.NET, System.Numerics, System.Text.Json, JSON Schema, xUnit, MapDoc, MapEditor and ke-mapedit MCP. No new production package edge.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](../../design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), C7, J7.1 to J7.3 in the historical R7 draft, DG9.1 to DG9.6 and the R10 foliage verb rows. The authoritative spec is on the world-authoring program branch if this relative link is absent on main. OA39 approves the R7 gate's Decisions 1 to 7 and smaller decisions 8 to 21, planning only. The gate supersedes historical raster counts and per-owner-mask recommendations. Source ranges below refer to engine `f1cd6656b`, not the old planning branch.

## Global Constraints

- OA39 Decision 1. Root density payload 1, format 9, pure 8 to 9 migration. Absent foliage stays absent. Analytic scatter, companion layers and prefab payload 1 remain untouched.
- OA39 Decision 2 and OA9. Every layer carries `MapSupportBinding` (Surface or Space). Imports bind to their source surface. Missing, unavailable, ambiguous, over-budget and unrepresentable support refuse with typed statuses. Outside a known domain yields no sample. Never choose another floor.
- OA39 Decision 3. Imports use a named, versioned compatibility policy reproducing legacy interpolation, smoothed corner normals and eligibility over native data. A later geometric policy is a separate version and decision.
- OA39 Decision 4, R7-G1. Clearance rules live in the density payload, keyed by asset ID and placement tag, applied to effective placements through their effective transform once. Owner-bound exception masks are exceptional. Released asset payloads and digests do not change.
- OA39 Decision 5, R7-G2. Fixed-size raster chunks align to the layer lattice. Absent chunks read as zero. Raster rows advance positive world Z. An advertised but unavailable chunk is not absent.
- OA39 Decisions 6 and 7. Private comparison now through the seventh test-only TileWorld.Render3D reference. Keys, bytes, choices and decisions compare exactly. Positions use 0.00001 m per component and orientations use 0.000001 radians. Never widen tolerances after a mismatch.
- Smaller decisions 8 to 10. Layer ID, support owner and candidate address form the sample key. Layer ID never enters randomness. Stable comparison address is separate from versioned identity. Foliage changes authored/whole identity and its own dependency digest, never released annotation digests, statics, nav captures or world bounds.
- Smaller decisions 11 to 14. Narrow old/new chunk effects, one undo step, atomic refusal, detached reads, late-callback regression, five verbs, 96 to 101 tools. Brush uses world metres, bytes 0 to 255, hardness 0 to 1 and positive-world-Z rows. CPU preview only. Windowed painting stays R8 and R9.
- Smaller decisions 15, 16 and 18. R8 supplies motion, shrub profiles and shadow policy. Live client options define later appearance acceptance. R8 receives immutable keys, poses, model/material references, thinning rank, dependency digest, padded mesh bounds and completeness. Interior cut adapter stays R8 (#1474) and does not cut foliage.
- Smaller decision 17 and OA13. Prepare surfaces, paint and rules once per chunk, no full support query per candidate. Count work and refuse before oversized allocation. Sparse large-domain fixtures only. No timing, load or stress test is authorized.
- Smaller decision 19. Payload, identity, surfaces and rules in MapDoc.Tests. Both-head and collider checks in MapDoc.Physics.Tests. Commands and wire in MapEditor.Tests. Legacy distributor regressions in Render.Tests.
- Smaller decision 20. Preserve the three exact OA9 proof names on the released stacked-support and prefab-cave fixtures. No shipped underground density fixture is required or invented as shipped evidence.
- OA28 and OA33. Authored spaces only, one occupied owner, nested rooms and shared membership. No implicit exterior or second foliage room graph. NoDraw and Blocked are ignored exactly as TileAllowed does.
- OA5 revised T4. The 1 m minimum vertical interaction envelope never becomes a foliage blocker. Explicit trees, bushes and flora remain placements, including their IDs and collision. Tree `LodDistance = 0` remains an author choice.
- Dependencies. MapDoc stays free of TileWorld, physics, render, GPU, editor and game dependencies. MapEditor gains no MapDoc.Physics reference. Private harness remains nonpackable, unreferenced, outside the solution and public CI.
- Released budgets. MapQueryLimits remains 256 candidate patches, 4096 inspected faces, 64 support intersections, 32 page reads, 64 record reads and depth 8. Keep R5 expansion/precision limits. Refusal never truncates or inflates a budget to pass.
- Repository. Explicit-path commits, zero warnings, no blanket suppressions, new files at most 800 lines. Baselined source files may shrink but cannot grow without owner approval. New concerns get new types, not arbitrary line splits.
- Input/text. AppWindow alone reads raw devices. Other code uses InputState, InputManager and Pointer. Player text uses StringId/LocalizedText. Developer diagnostics use the explicit raw path.
- Privacy/CD2. Invented public fixtures only. No private world bytes, coordinates, identifiers, inventories, content digests or detailed reports in engine git, CI or public output. Missing private input fails, never skips. A generator rule mismatch stops before attestation, for a controller ruling on the rule, never the tolerance.
- Verification. Every execution build, test, format, bake or pack uses build-slot. Full MapDoc, MapDoc.Physics and MapEditor projects after each task, with TRX evidence. Full Release solution build and suite once at the reconciled candidate. No local repetition loops or windows.
- Release. R7 adds API and a format and takes a minor. Ride the minor staged at integration. If main stages only a patch, take the next free minor and reconcile with the interior cut lane, which plans 21.6.0. CHANGELOG and guarded declarations move together. Tag through a KhaozEngine-rooted thread under OA25, after execution approval.

## Review Focus

1. A missing advertised chunk or normal halo looks like empty density. Task 2 adds `AdvertisedChunkMissing_RefusesWithoutPublishingZero`, Task 4 adds `UnavailableNormalHalo_IsNotAnEdgeExtension` and Task 7 proves unchanged history on refusal.
2. A NoDraw cell, rotated half overlay or cross-patch normal quietly uses physical-face policy. Task 4 adds `LegacyFlagsCutsAndBorderNormals_MatchTheNamedPolicy`, including all cuts/rotations, and Task 10 independently calls the real legacy sampler.
3. A moved roof or unpacked cave clears another level or leaves old blades alive. Task 5 adds `OwnerLocalClearance_UnpackRemapAndRemovalAreAtomic`, Task 7 checks old/new effects, and Task 9 exercises the three OA9 proofs through real transactions.
4. A callback changes density after staging, leaving effects and redo from an earlier snapshot. Task 7 adds `LateCallbackFoliage_PublishesOneSnapshotEffectsAndRedo`, including the next edit's before-state.
5. A policy upgrade changes every versioned key and hides changed rows as remove/add pairs, or a chunk partition duplicates jittered candidates. Tasks 3 and 6 add `UnalignedPartitionUnion_PreservesEveryOldChannel` and `PolicyDifferential_JoinsStableAddressesBeforeVersionedKeys`, with actual changed, added and removed rows.

## Proposed controller rulings

These are proposals, not owner decisions. Approve or amend each before its consuming task. A rejected proposal changes the plan before code.

| ID | Proposed choice | Cost if wrong |
| --- | --- | --- |
| R7-P1 | 32 by 32 byte chunks, 1024 bytes each. Positive int Width/Depth at most 1,048,576, at most 16,384 stored chunks per layer, 4096 region memberships per chunk, 256 layers and 67,108,864 decoded density bytes per document. Each layer allows 256 models, 65,535 material rows, 4096 rules, 4096 masks and 65,536 owner correspondences. The foliage JSON section is at most 134,217,728 UTF-8 bytes and each base64 chunk token at most 1368 characters. Checked arithmetic precedes allocation. Last chunks have zero padding outside the declared sample domain. Explicit all-zero chunks are legal and retain authored identity | Different granularity or ceilings require a new payload/storage contract. These admit the 64 km sparse verification envelope without allocating its area |
| R7-P2 | Root `foliage` is optional. Set/layer payload versions both 1. Monolithic layers embed chunks. Tiled metadata advertises each chunk's raw-byte SHA-256 and region memberships, with one fixed 1024-byte .bin payload under its canonical region. Identity hashes logical bytes/settings, not filenames or storage packing | New storage/copy code is required. Omitting partial-state bookkeeping would lose unloaded chunks |
| R7-P3 | Policy ID `legacy-tile`, version 1, records `normalBoundaryHeight` and an exhaustive native material-ID/resource/kind table. Foliage requires resolver identity (1, 2) and AuthoredBindingsV2, older native resolvers receive explicit adoption guidance. Corner smoothing uses the bound surface frame, not the density cell size. NoDraw compatibility triangles reuse the canonical triangulation kernel through an additive compiler entry, without altering physical Compile | Adds small compatibility metadata and a compiler seam. Incorrect boundary height or material classification changes blade decisions |
| R7-P4 | Solid and door rules require the same support owner as the blade. Use effective placement support provenance. Roof target ownership comes from its linked room floor or an explicit correspondence, never every lower floor at that XZ. Explicit-Y source owners may carry a layer-local typed `ownerSupports` correspondence, with no geometry, resolved and validated like mask owners. Legacy solid/roof footprint eligibility tests the selected support cell centre, reproducing TileAllowed's tile sets, with owner-local half-open rectangles. Rules still apply automatically to newly bound placements. RoofAbove requires a strictly higher rule plane. DoorClearance is owner-local metres, scaled once, while EdgeFade stays world metres | Explicit-Y imports need support correspondence rows. Legacy clearing stays cell-based for freely moved owners, a later geometric policy may change it only with a differential. A controller rejecting the correspondence table must supply an explicit domain rule |
| R7-P5 | Stable comparison address is `(layerId, supportOwnerId, gx, gz)`. Versioned sample key adds distribution version 1 and policy ID/version. Trace records every scanned address, with OutsideArea separate from owned decisions. All seven channel values are exposed without reseeding | A wider trace seam adds compatibility work in Terrain. Conflating halo scans with owned decisions would duplicate comparison rows |
| R7-P6 | One generation request owns one density chunk's half-open world rectangle, clipped to the declared raster. Default limits per chunk are 4,000,000 scanned candidates, 262,144 output samples, 256 preparation scopes, 262,144 prepared faces, 4096 rule candidates and 4096 mask candidates. Area preview visits at most 64 logical chunks. A streamed full-layer differential allows 64,000,000 candidates and 4,000,000 change rows in total, retaining only one chunk at a time. Each acquired scope retains released MapQueryLimits. Calls refuse atomically with counters and no usable partial batch | Dense requests may need smaller caller partitions. These are bounded capability ceilings, not performance claims |
| R7-P7 | Add Foliage invalidation flag 256 and separate typed chunk effects with old/new XZ bounds. Do not union cosmetic extents into geometry OldSpatialBounds/NewSpatialBounds. Cache dependencies include only intersecting chunks and their halos, selected support/paint/room/rule/mask/marker/asset inputs | Effects consumers must learn the new channel. A whole-root dependency would defeat narrow invalidation |
| R7-P8 | Brush target blending matches the legacy linear hardness rim, uses MathF.Round with ToEven and clamps to 0 to 255. A brush visits at most 64 chunks and 65,536 raster addresses per mutation. Policy-changing set requires a complete differential digest bound to the before identity and exact proposed settings | Large strokes refuse and must be partitioned explicitly. Different quantization changes authored bytes |
| R7-P9 | Scale and thinning rank compare exact binary32 bits, including across platform fixtures. Position/orientation use the gate tolerances. Synthetic pins are fixed source literals captured from the released generator, independently verified by channel arithmetic, never regenerated to bless a mismatch | Cross-platform scale/rank drift blocks acceptance and requires a rule ruling, not tolerance expansion |
| R7-P10 | R8 receives source-unit-adjusted model bounds transformed by the exact CPU matrix and an explicit caller motion padding in metres. Missing extents or padding is an incomplete/refused handoff, never zero padding by default. Generation remains independent of styling | Callers must provide a complete bounds policy. Incorrect padding causes residency/culling loss |
| R7-P11 | Task 10F is a conditional, independently reviewed GPU-free legacy-seam extraction inside Task 10's allowance. It runs only if the seventh reference actually starts graphics/native window state. No copied oracle policy | Extraction can exhaust the allowance and requires reforecast before continuing. No private comparison waiver |
| R7-P12 | Eleven serial tasks, 35 to 60 engineer-days including reviews/fixes/integration, 48 to 80 elapsed agent hours after plan approval, with 65 to 80 the scheduling allowance | Capacity stalls, extraction delivery and comparison defects can exceed the forecast. Reforecast after Tasks 4 and 7 and private comparison |

## File structure and estimate

All new names below are task outputs. Existing paths/ranges are pinned to `f1cd6656b`. A filename following a full directory in a Files bullet shares that directory. Tests use namespace `KhaozEngine.Tests.*`.

| Task | New responsibility and homes | Released integration homes | Engineer-days |
| --- | --- | --- | --- |
| 1 | Foliage DTOs, closed codec/validation and migration in MapDoc/Foliage | Document partial, persisted order, file codec, schema and validation | 3 to 5 |
| 2 | Foliage storage, semantic projection and identity in MapDoc/Foliage | Tiled writer/index, canonical root, stored identities, snapshots/write sets/history | 4 to 6 |
| 3 | Candidate/decision trace in Terrain | GroundCoverDistribution and Render.Tests/Terrain | 2 to 3 |
| 4 | Prepared named support, smoothing and material sampler in MapDoc/Foliage and Support | Shared support selection, surface compiler and partial paint | 4 to 7 |
| 5 | Clearance rules, typed owner correspondences and masks in MapDoc/Foliage | Effective resources and prefab reference/lifecycle edits | 4 to 6 |
| 6 | Immutable distribution, dependency digest and differential in MapDoc/Foliage | Task 3 distributor and Tasks 4/5 prepared inputs | 3 to 5 |
| 7 | Pure foliage edits and effects, MapEditor/NativeFoliageCommands.cs | Native transactions, publication and history | 4 to 7 |
| 8 | Tool mutation/read adapters and Tools/FoliageTools.cs | MCP bootstrap, pinned inventory and README | 2 to 4 |
| 9 | Public invented acceptance and cross-platform pins | MapDoc.Tests, Physics.Tests, MapEditor.Tests, CI workflow | 3 to 5 |
| 10 | Attested private foliage comparison and conditional 10F | OracleHarness.Tests, architecture reference guard | 4 to 7 |
| 11 | R8 padded batch handoff and living consumer docs | MapDoc README, API/dependency docs and release preparation | 2 to 5 |
| | Total including reviews, fixes, candidate and integration | Serial dependency order 1 through 11 | 35 to 60 |

R4 took about 9 h from approval to tag, R5 about 59 h and R6 about 56 h against its 22 to 48 h forecast. R7's prepared sampling, trace and private comparison are larger than R6. Forecast 48 to 80 elapsed agent hours, favouring 65 to 80, not a promise of active compute time. Plan reviews, owner responses, capacity, input preparation and release CI extend calendar time. Conditional 10F consumes Task 10's allowance and triggers reforecast if it cannot fit.

## Execution mechanics

The writer only commits this plan. The controller records independent plan reviews, adjudicated R7-P choices and owner reviewed-plan approval before any following command executes. Workers start at the previous accepted task SHA and stop at verified explicit-path commits. The controller routes every dispatch under the session rules and owns pushing, main reconciliation and release.

Use bash during approved execution. Preserve synchronous exit status and TRX evidence. A project sweep can exceed 20 minutes under shared machine load. Keep it running through the supported long command session, without timeout-based retries or shortened coverage.

```bash
cd /Users/antonio/KhaozEngine/.worktrees/wa-r7-foliage
test "$(git branch --show-current)" = "feature/wa-r7-foliage"
test "$(git rev-parse HEAD)" = "$wa_r7_expected_base"
wa_r7_log_dir="/tmp/wa-r7-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p "$wa_r7_log_dir" local-feed
MAPDOC=KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj
MP=KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj
EDITOR=KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj
RENDER=KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj
ORACLE=KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj
RUMP=KhaozEngine.Tests/KhaozEngine.Tests.csproj
wa_run() {
    local n=$1 rc
    shift
    local lane=()
    if [ "${1:-}" = --heavy ]; then lane=(--heavy); shift; fi
    if build-slot "${lane[@]}" --label "wa-r7-$n" -- "$@" >"$wa_r7_log_dir/$n.log" 2>&1
    then rc=0
    else rc=$?
    fi
    tail -25 "$wa_r7_log_dir/$n.log"
    echo "exit $rc for $n"
    return "$rc"
}
wa_test() {
    wa_run "$1" dotnet test "$2" -c Release -m:1 --logger "trx;LogFileName=$1.trx" --results-directory "$wa_r7_log_dir" --filter "$3"
}
wa_sweep() {
    local n=$1
    wa_run "$n-mapdoc" --heavy dotnet test "$MAPDOC" -c Release -m:1 --logger "trx;LogFileName=$n-mapdoc.trx" --results-directory "$wa_r7_log_dir" &&
    wa_run "$n-physics" --heavy dotnet test "$MP" -c Release -m:1 --logger "trx;LogFileName=$n-physics.trx" --results-directory "$wa_r7_log_dir" &&
    wa_run "$n-editor" --heavy dotnet test "$EDITOR" -c Release -m:1 --logger "trx;LogFileName=$n-editor.trx" --results-directory "$wa_r7_log_dir"
}
wa_finish() {
    local n=$1 path
    shift
    local cs_paths=()
    for path in "$@"; do case "$path" in *.cs) cs_paths+=("$path");; esac; done
    if [ ${#cs_paths[@]} -gt 0 ]; then
        wa_run "$n-format" dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --include "${cs_paths[@]}" || return $?
    fi
    wa_run "$n-dashes" sh scripts/check-dashes.sh --tree &&
    wa_run "$n-prose" sh scripts/check-prose.sh --tree &&
    wa_run "$n-size" sh scripts/check-file-size.sh --tree &&
    wa_run "$n-agent" sh scripts/check-agent-instructions.sh --tree &&
    wa_run "$n-docs" bash scripts/check-doc-versions.sh && git diff --check
}
```

Red requires the named behavioral assertion or compilation solely for a newly declared task API. Invalid fixtures, missing released APIs, unrelated failures and zero matched tests are not red. Every added/changed behavioral test appears in its owning red filter. Green requires exit 0, nonzero matching tests and zero failures. Preserve passing released regressions. After the last fix/review, `wa_sweep tN-final` runs once on those bytes before acceptance. Do not duplicate already valid evidence. Exit 75 means nothing ran, retry later. No repeated-until-green loops.

Each task lists its commit paths in an explicit array, including any reviewed fix-round additions. Scoped format follows R-GATE-1 if unrelated main files drift (#1485). Repair only the owned C# list through wa_run, then verify it. The outside-solution harness gets its own project format command. No unloaded changed project can be waived by an analyzer workspace warning.

## Shared fixtures and contract notation

Task 1 creates `FoliageFixture(MapDocument Document, MapAssetClosure Assets)` and partial `FoliageFixtures` in MapDoc.Tests/Foliage. `Create(string variant = "basic") -> FoliageFixture`, `Reload(FoliageFixture fixture, bool tiled) -> FoliageFixture` and `Options() -> MapResolveOptions` are the shared fixture entry points. Use the already released `**/*Fixtures.cs` source links in Physics.Tests and MapEditor.Tests, no test-assembly reference or disk-dependent linked fixtures. Concern files end in Fixtures.cs and stay under 800 lines.

Basic is invented: layer `cover-a`, Surface `floor`, origin (-3, -5), cellSize 2, Width 35, Depth 3, row bytes `(byte)(z * 35 + x)`, seed 17, spacing 0.5, scale 0.8 to 1.4, rootOffset -0.01, ordered models `reed-a` weight 3 and `reed-b` weight 1. Materials 10/11 are ordinary and 12 water, with invented verified Material resources. The native floor covers world X [-8,72] and Z [-8,8], height 1, using ImportedMetreCentimetre, underlay 10 and complete patches, with resolver identity (1, 2) and AuthoredBindingsV2. Options uses builder `foliage-fixture`, version 1, hash `foliage-fixture/1`, ResolverVersion 2. Fixture construction uses released native fixture helpers, not private source.

Task 4 adds `SlopedBorder()`, `FlagsAndCuts()`, `UnavailableHalo()` returning FoliageFixture. Task 5 adds `Clearance(string variant)`. Task 9 adds `Stacked()`, wrapping `SupportFixtures.EightStacked()` with its released assets, and `Cave()`, wrapping `PrefabFixtures.Cave("stacked")`, retaining their real topology and ownership. Tasks adapt density/placement decorations around those fixtures, never replace their cave geometry with a toy floor. Task 7 creates editor-only `FoliageEditorFixtures.Session(FoliageFixture) -> MapEditSession` and `State(MapEditSession) -> string`, a canonical snapshot of document, closure, identity, dirty state, cursor/depth/labels and events. Assertions below use actual produced values, not hard-coded product-result helpers.

### Task 1: Closed payload 1 and pure format 9 (3 to 5 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/MapDocument.Foliage.cs`, `Foliage/MapFoliagePayload.cs`, `MapFoliageRules.cs`, `MapFoliageJson.cs`, `MapFoliageValidation.cs`, `MapFoliageMigration.cs`.
- Modify: `KhaozEngine.MapDoc/MapPersistedOrder.cs:9-38`, `MapDocumentFile.cs:36-44,96,350-371`, `MapDocumentValidator.cs:13-25`, `MapBoundDocumentValidation.cs:16-41`, `mapdoc.schema.json:10` and new foliage definitions/root property.
- Test/create: `KhaozEngine.MapDoc.Tests/Foliage/FoliageFixtures.cs`, `MapFoliagePayloadTests.cs`. Modify `KhaozEngine.MapDoc.Tests/Prefabs/MapPrefabFormatTests.cs:125-135` for future-format 10 refusal, preserving its assertion.

**Interfaces:**
- Consumes released `MapSupportBinding(MapSupportBindingKind Kind, string? SurfaceId, string? SpaceId, float? ReferenceY, float? SearchBelow, float? SearchAbove)` at MapSupportRecipe.cs:9, `MapDocumentFile.LoadText(string, MapDocumentLoadOptions? = null, string? = null) -> MapDocument`, `SaveText(MapDocument, MapDocRegistry? = null) -> string`, `MapBoundDocumentValidation.Validate(MapDocument, MapAssetClosure, MapDocRegistry? = null) -> void`.
- Produces nullable `MapDocument.Foliage: MapFoliageSet?`, order 28, omitted when null. `MapFoliageSet(int PayloadVersion, IReadOnlyList<MapFoliageLayer> Layers)` and `Clone() -> MapFoliageSet` own deep immutable copies. Layers retain authoring order, IDs are ordinal and unique.
- Produces `MapFoliageChunkKey(int X, int Z)` and sealed class `MapFoliageChunk` with constructor `MapFoliageChunk(MapFoliageChunkKey key, ReadOnlySpan<byte> density)`, readonly `Key`, copied 1024 bytes, `DensityAt(int localX, int localZ) -> byte` and `CopyDensity() -> byte[]`. There is no public memory/array view of its backing bytes. Set/layer collection properties likewise wrap copied arrays rather than returning arrays as IReadOnlyList.
- Produces `MapFoliageModel(string AssetId, float Weight)`, `MapFoliageMaterialKind { Ordinary, Water }`, `MapFoliageMaterial(ushort Id, string ResourceId, MapFoliageMaterialKind Kind)`, `MapFoliagePolicy(string Id, int Version, float NormalBoundaryHeight)`.
- Produces `MapFoliageLayer(int PayloadVersion, string Id, MapSupportBinding Support, float OriginX, float OriginZ, float CellSize, int Width, int Depth, MapRowDirection RowDirection, int Seed, float Spacing, float ScaleMin, float ScaleMax, float RootOffset, IReadOnlyList<MapFoliageModel> Models, IReadOnlyList<ushort> AllowedVisibleMaterials, IReadOnlyList<MapFoliageMaterial> Materials, MapFoliagePolicy Policy, bool ExcludeIndoors, bool ExcludeSolidObjects, float DoorClearance, float EdgeFade, IReadOnlyList<MapFoliageClearanceRule> ClearanceRules, IReadOnlyList<MapFoliageMask> ExceptionMasks, IReadOnlyList<MapFoliageOwnerSupport> OwnerSupports, IReadOnlyList<MapFoliageChunk> Chunks)` with copied immutable state and `DensityAt(int x, int z) -> byte`.
- Task 1 declares rule/mask/owner DTO shapes consumed by Task 5. `MapFoliageRect(float MinX, float MinZ, float MaxX, float MaxZ)`, `MapFoliageClearanceKind { Solid, Door, RoofAbove }`, `MapFoliageClearanceRule(string Id, string AssetId, string? PlacementTag, MapFoliageClearanceKind Kind, MapFoliageRect Footprint, float LocalPlaneY)`, `MapFoliageOwnerSupport(MapPrefabReference Owner, MapSupportBinding Support)`, `MapFoliageMask(string Id, MapPrefabReference Owner, MapSupportBinding Support, MapFoliageClearanceKind Kind, MapFoliageRect Footprint, float LocalPlaneY)`. Owner refs are World scope, only Placement, Surface, Record or Marker as appropriate, never untyped strings.
- Produces `MapFoliageValidation.Validate(MapFoliageSet set) -> IReadOnlyList<string>`, `ValidateBound(MapDocument document, MapAssetClosure assets) -> IReadOnlyList<string>`, internal `MapFoliageJson.Read(JsonElement) -> MapFoliageSet`, `Write(Utf8JsonWriter, MapFoliageSet) -> void`, `MapFoliageMigration.Upgrade(JsonObject source) -> JsonObject`. Bound validation checks metadata/resource roles without calling MapPrefabResolver recursively. Task 5 validates effective owners after expansion. Only legacy-tile/1 is supported. Task 6 tests future version joins as data inputs to the generic differential core, without weakening document validation.

- [ ] **Write failing tests.** `DensityRaster_NonSquareNegativeOriginKeepsEveryWorldAddress`, `PayloadClosedSchema_RefusesMalformedAndFutureMembers`, `EightToNine_IsVersionOnlyAndLeavesAbsentFoliageAbsent`, `OldAnalyticAndPrefabPayloadOne_KeepReleasedBytes`. Test every byte, not selected endpoints only.

```csharp
Assert.Equal((byte)0, layer.DensityAt(0, 0));
Assert.Equal((byte)104, layer.DensityAt(34, 2));
Assert.Equal(65f, layer.OriginX + 34 * layer.CellSize);
Assert.Equal(-1f, layer.OriginZ + 2 * layer.CellSize);
Assert.Equal(9, upgraded["formatVersion"]!.GetValue<int>());
Assert.Equal(8, original["formatVersion"]!.GetValue<int>());
Assert.False(upgraded.ContainsKey("foliage"));
Assert.True(JsonNode.DeepEquals(expectedVersionOnlyClone, upgraded));
Assert.Equal(releasedPrefabBytes, rewrittenPrefabBytes);
```

Cover unknown/duplicate/case-variant/null fields, payload 2, future format 10, empty/duplicate IDs, zero/negative/nonfinite sizes, scales/weights, weight-sum overflow, unknown material/model/support, byte length 1023/1025, base64 errors, invalid numeric bytes -1/256/fraction, duplicate/out-of-domain chunk keys, nonzero padding, wrong row direction and every P1 ceiling plus one. Surface binding forbids foreign members. Space requires finite reference/search with released bounds. Rules/masks reject future kinds, invalid rectangles, local or wrong-kind owners. Schema/runtime agree, with evaluator parity completed in Task 9. The migration accepts 8 or 9, deep-clones and changes only formatVersion, preserving an unreadable sentinel section without sampling. Loading that sentinel still refuses as a positive control.

- [ ] **Run red:** `wa_test t1-red "$MAPDOC" "FullyQualifiedName~MapFoliagePayloadTests"`. Expect missing task-owned APIs or format/payload assertions failing. Capture any released synthetic byte pins before product changes.
- [ ] **Implement** the Interfaces in their named files. Register migration 8 after the released 7 step. Set CurrentFormatVersion and schema const to 9. Closed decoder validates P1's JSON/token/count limits before nested materialization or base64 allocation. Non-null foliage requires resolver (1, 2) and AuthoredBindingsV2, with explicit adoption guidance otherwise. Domain is sample indices [0, Width) by [0, Depth), with endpoint-clamped bilinear interpolation later. No whole-domain array. Allowed materials are a sorted distinct nonzero set, model order remains authored. Preserve prefab payload and existing placement binding codecs. The 1024-byte chunk is a small bounded edit/read unit and gives a 64 m sample-address span at 2 m cells, unlike an area-sized raster or thousands of tiny files.
- [ ] **Run green:** Step 2 filter as `t1-green`, then `wa_test t1-regress "$MAPDOC" "FullyQualifiedName~FormatAdvanceTests|FullyQualifiedName~MapPrefabFormatTests|FullyQualifiedName~MapMarkerFormatTests"` and `wa_sweep t1-final` after review.
- [ ] **Format, guard and commit** the explicit Files list, no other paths, using `wa_finish t1 "${wa_r7_t1_paths[@]}" && git add -- "${wa_r7_t1_paths[@]}" && git commit --only -m "feat(mapdoc): add foliage payload 1 and pure format 9" -- "${wa_r7_t1_paths[@]}"`. Set `wa_r7_t1_paths` to each full path enumerated above before running it. This explicit-array convention applies to every task below.

### Task 2: Sparse storage, every copy path and both identities (4 to 6 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageStorage.cs`, `MapFoliageStorageIndex.cs`, `MapFoliageProjection.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageStorageTests.cs`, `MapFoliageIdentityTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeFoliageSnapshotTests.cs`.
- Modify: `KhaozEngine.MapDoc/MapTiledFile.cs:26-109,205-235`, `MapTiledFile.Save.cs:24-137,177-205,237-254`, `MapTileIndex.cs:28-78`, `MapCanonical.cs:53-108`, `Storage/MapSurfaceRootProjection.cs:13-25`, `Storage/MapStoredSurfaceSource.Content.cs:19-78`, `Identity/MapAuthoredIdentityV2.cs:17-49`, `Editing/MapNativeWriteSet.cs:10-26`, `KhaozEngine.MapEditor/NativeDocumentSnapshot.cs:13-94,117-145`, `KhaozEngine.MapEdit.Tool/MapEditSession.NativeHistory.cs:145-169`. Extend the Task 1 root codec/schema for the storage variant.
- Test/modify: `KhaozEngine.MapDoc.Physics.Tests/MapPersistedIdentityTests.cs:59-120` for independent format-9 derivation, preserving earlier version controls.

**Interfaces:**
- Consumes Task 1 immutable set and released `MapSurfaceSemantics.RootDigest(MapDocument) -> string`, both `MapAuthoredIdentityV2.Compute(MapDocument, MapAssetClosure, MapResolveOptions) -> string` and `Compute(MapStoredSurfaceSource, MapAssetClosure, MapResolveOptions) -> string` at Identity/MapAuthoredIdentityV2.cs:17,32.
- Produces `MapFoliageChunkRef(string LayerId, MapFoliageChunkKey Key, MapTileCoord OwnerRegion, IReadOnlyList<MapTileCoord> Regions, string Sha256)`, immutable `MapFoliageStorageIndex` with `IsPartial: bool`, `Read(string layerId, MapFoliageChunkKey key) -> MapFoliageChunkRead`, and `MapFoliageChunkRead(MapFoliageReadStatus Status, MapFoliageChunk? Chunk)` where status is Found, Absent, Unavailable, Invalid or CapacityExceeded. Read verifies captured-generation bytes and does not mutate the accepted index.
- Adds `MapFoliageChunkDigest(MapFoliageChunkKey Key, string Sha256)` and readonly `MapFoliageLayer.ChunkDigests: IReadOnlyList<MapFoliageChunkDigest>` for all advertised chunks, distinct from resident Chunks. Public construction derives digests from copied bytes. Internal `MapFoliageLayer.FromStorage(MapFoliageLayer metadata, IReadOnlyList<MapFoliageChunkDigest> advertised, IReadOnlyList<MapFoliageChunk> resident) -> MapFoliageLayer` preserves complete immutable metadata through partial loads and clones. DensityAt throws MapDocumentException for an advertised but nonresident chunk, never returns zero. Only a key absent from the complete directory is zero.
- Produces internal `MapFoliageStorage.ReadManifest(JsonElement value) -> (MapFoliageSet Set, MapFoliageStorageIndex Index)`, `Prepare(MapDocument document, string directory) -> MapFoliagePreparedStorage`, `Load(MapDocument document, string directory, MapTileRect? window) -> void`, `WriteManifest(Utf8JsonWriter writer, MapFoliageSet set, MapFoliageStorageIndex index) -> void`, `MapFoliageProjection.Digest(MapFoliageSet?) -> string` and `Write(Utf8JsonWriter, MapFoliageSet?) -> void`. `MapFoliagePreparedStorage(MapFoliageStorageIndex Index, IReadOnlyList<(string RelativePath, ReadOnlyMemory<byte> Bytes)> Payloads, IReadOnlySet<string> Keep)` owns captured immutable bytes/references until manifest publication. Projection uses scheme `kemap/foliage-authored/1`.
- Adds internal `MapCanonical.WriteNativeGlobals(Utf8JsonWriter writer, MapDocument document, JsonSerializerOptions options, bool includeFoliage) -> void`, preserving the released three-argument entry as includeFoliage true. Canonical globals write the logical foliage projection. The tiled manifest calls includeFoliage false and then WriteManifest for the stored variant. Monolithic serialization uses MapFoliageJson.Write for inline bytes. This keeps storage-specific references out of root/history semantics without emitting the section twice.
- Adds init-only `MapTileIndex.Foliage: MapFoliageStorageIndex?`, includes it in IsPartial, and `MapNativeWriteSet.Foliage: bool`. NativeDocumentSnapshot clones/publishes the complete set for both named write sets and complete snapshots, without aliasing.

- [ ] **Write failing tests.** `ChunkedMonolithicAndTiled_RoundTripEveryByteAndSetting`, `AdvertisedChunkMissing_RefusesWithoutPublishingZero`, `UnloadedChunkPartialSave_CarriesVerifiedBytesAndRefusesUnindexedOverwrite`, `EveryNativeCopyPath_RetainsIndependentFoliage`, `FoliageChangesBothIdentityPathsButNotAnnotationDigest`, `FormatNine_MovesNativeIdentitiesExactlyOnce`.

```csharp
Assert.Equal(monolithicAuthored, storedAuthored);
Assert.Equal(beforeAnnotations, foliageOnlyAnnotations);
Assert.NotEqual(beforeAuthored, foliageOnlyAuthored);
Assert.Equal(MapFoliageReadStatus.Absent, absent.Status);
Assert.Equal(MapFoliageReadStatus.Unavailable, missingAdvertised.Status);
Assert.Equal(beforeBytes, bytesAfterFailedSave);
Assert.Equal(9, reloaded.FormatVersion);
Assert.Equal(derivedFormatNineIdentity, actualIdentity);
```

Exercise GlobalsOnly, WriteGlobals, WriteNativeGlobals, root projection, clone, named publication, whole publication, local asset session history, SemanticHash, save/reload and both identity overloads. Mutate each field, rule, owner mapping and byte independently. Preserve payload-1 and payload-2 annotation digest pins. Test a chunk spanning storage regions, negative region coordinates, retile, conversion both ways, stale digest, tampered payload, failed manifest-last save, unloaded ownership and no orphan-file sweep of retained chunks. A null foliage set adds no foliage member.

- [ ] **Run red:** `wa_test t2-red "$MAPDOC" "FullyQualifiedName~MapFoliageStorageTests|FullyQualifiedName~MapFoliageIdentityTests"`, `wa_test t2-red-snapshot "$EDITOR" "FullyQualifiedName~NativeFoliageSnapshotTests"`, `wa_test t2-red-pin "$MP" "FullyQualifiedName~MapPersistedIdentityTests.StackedCaveBuildHash_IsPinned"`.
- [ ] **Implement** storage/identity/copy outputs. Monolithic `foliage.layers[].chunks` embeds key and 1024 base64 bytes. Tiled `foliage.layers[]` has identical metadata and chunk references instead of bytes, mutually exclusive closed shapes, with payloads `tiles/foliage/r_<x>_<z>/<sha256>.bin`. Each file is exactly the 1024 raw bytes, with no key/owner envelope, and its name is their SHA-256. Root/layer payload 1 defines its interpretation, so future versions refuse before reading it. Same bytes in the same region share one file safely. ReadManifest parses the stored variant before monolithic-layer decoding, retaining all ChunkDigests and only resident Chunks. Owner region contains the chunk's lower sample address using floor(world/TileSize). Membership lists every intersected region, including a boundary endpoint. Check the 4096 membership ceiling before enumeration. A window follows memberships to the one payload. Do not add foliage-only MapTileEntry geometry rows.
- [ ] **Implement** semantic projection as ordered layer metadata plus complete chunk digests sorted (Z, X). Each digest is SHA-256 of exactly the raw 1024 bytes, excluding key/storage envelope, with the key included separately in the projection. In-memory identity recomputes and verifies resident bytes and refuses incomplete advertised data. Stored identity verifies every advertised payload even when unloaded. Root projection excludes paths/region packing and includes the full logical directory, so an unloaded byte cannot disappear from identity. Preserve released root/annotation digest scheme labels and make format 9 the single all-native identity move. Annotation schemes /1 and /2 remain unchanged. Derive the format-9 hash from canonical bytes, never copy an actual hash as its own oracle.
- [ ] **Run green:** repeat the three red filters with green labels, then `wa_sweep t2-final`. Use fault injection already available in tiled save options, no sleeps.
- [ ] **Format/guards/commit:** `wa_finish t2 "${wa_r7_t2_paths[@]}"` over the explicit Files list, then explicit-path add/commit with `feat(mapdoc): persist sparse foliage across storage and native history`.

### Task 3: Additive candidate trace with unchanged legacy output (2 to 3 engineer-days)

**Files:**
- Create: `KhaozEngine.Terrain/GroundCoverTrace.cs`, `KhaozEngine.Render.Tests/Terrain/GroundCoverTraceTests.cs`, invented `KhaozEngine.Render.Tests/Terrain/GroundCoverTraceFixtures.cs`, `GroundCoverReleasedBaselineTests.cs` in the same test directory.
- Modify: `KhaozEngine.Terrain/GroundCoverDistribution.cs:38-100,140-184`, `KhaozEngine.Render.Tests/Terrain/GroundCoverDistributionTests.cs:1-160`. Keep the existing Generate signature and every old channel bit.

**Interfaces:**
- Consumes released `GroundCoverDistribution.Generate(RectArea area, GroundCoverSettings settings, Func<float, float, GroundCoverSample> surface) -> IReadOnlyList<GroundCoverInstance>`, MaxCandidateCount = 4_000_000, `GroundCoverSample(float Height, Vector3 Normal, float Density)` and `GroundCoverInstance(string ModelId, Vector3 Position, Matrix4x4 Transform, float ThinningRank)`.
- Produces `GroundCoverCandidate(int X, int Z, float WorldX, float WorldZ, float JitterX, float JitterZ, float DensityPick, float ModelPick, float ScalePick, float YawPick, float Rank)`, `GroundCoverSurfaceInput(GroundCoverSample? Sample)` where null is a known outside-domain rejection, `GroundCoverDecisionKind { OutsideArea, OutsideSurface, ZeroDensity, DensityRejected, Accepted }`, and `GroundCoverDecision(GroundCoverCandidate Candidate, GroundCoverDecisionKind Kind, GroundCoverSample? Sample, GroundCoverInstance? Instance, float Scale, float YawRadians)`.
- Produces `GroundCoverDistribution.GenerateTraced(RectArea area, GroundCoverSettings settings, Func<GroundCoverCandidate, GroundCoverSurfaceInput> surface, Action<GroundCoverDecision>? observe = null) -> IReadOnlyList<GroundCoverInstance>`. Both old and new entry points share one arithmetic kernel. Observer absence creates no decision collection. Native typed refusals throw to the batch boundary, never become null or a fake zero-height sample.

- [ ] **Capture the released public baseline before writing tests that reference missing APIs.** Add `GroundCoverReleasedBaselineTests.ReleasedGenerator_MatchesIndependentChannels` using only released Generate, an invented RectArea(-2,-2,2,2), seed 17, spacing 0.5, the basic model/scale/root settings and a UnitY flat surface at height 1/density 1. An independent scalar hash fixture computes all seven channel values from the pinned constants and verifies model/scale/yaw/rank and transformed rows. Print the invented row values as C# bit literals through ITestOutputHelper, freeze them in `GroundCoverTraceFixtures.ReleasedBits` and retain the pre-product-edit SHA/log. No private data or new product API participates.
- [ ] **Run the passing baseline once:** `wa_run t3-baseline dotnet test "$RENDER" -c Release -m:1 --filter "FullyQualifiedName~GroundCoverReleasedBaselineTests" --logger "console;verbosity=detailed" --logger "trx;LogFileName=t3-baseline.trx" --results-directory "$wa_r7_log_dir"`. Expect nonzero tests, exit 0 and independently matched public bits. This is a preserved regression, not feature red.
- [ ] **Write failing tests.** `Trace_EqualsReleasedGenerateForEveryOldChannel`, `UnalignedPartitionUnion_PreservesEveryOldChannel`, `OutsideSurface_IsNotAnInventedFloor`, `TraceObserver_DoesNotChangeCallbackOrderOrOutput`. Consume the frozen public bits, not a baseline generated by the changed kernel.

```csharp
Assert.Equal(releasedInstances, tracedInstances);
Assert.Equal(releasedCallbackPoints, tracedCallbackPoints);
Assert.Equal(wholeOwnedAddresses, partitionOwnedAddresses);
Assert.Equal(wholeOwnedAddresses.Length, partitionOwnedAddresses.Distinct().Count());
Assert.All(outside, row => Assert.Null(row.Instance));
Assert.Equal(4_000_000, GroundCoverDistribution.MaxCandidateCount);
```

Pin salts exactly: JitterX 0x60B7A3D53B14C2A1, JitterZ 0x91E10DA5C79E7B1D, Density 0xD18A40B4F35062C7, Model 0xA4FBCB9528163E09, Scale 0x9C3C8D7E5F2A61B4, Yaw 0x3D17F0A58C6B42E9 and Rank 0xE2A86149B7D30C5F. Preserve signed-coordinate packing, 24-bit division by 16,777,216, weighted list order, all 16 matrix components and thinning-rank bits. Cases include negative addresses, zero density, threshold equality, noncoplanar normals, spacing 0.3 and unaligned splits at X 0.17/Z -0.23. Compare owned jittered points only in partition unions, not duplicate OutsideArea halo scans.

- [ ] **Run red:** `wa_test t3-red "$RENDER" "FullyQualifiedName~GroundCoverTraceTests"`. Existing GroundCoverDistributionTests are passing regressions.
- [ ] **Implement** the additive seam. Preserve candidate loop bounds and traversal Z then X, half-open clipping, density >= rejection and Scale * RotationY * alignment * Translation order. Hashes are stateless, exposing channels must not alter a random stream or old surface call count. Preserve legacy argument failures and empty-area behavior.
- [ ] **Run green:** red filter as `t3-green`, `wa_test t3-regress "$RENDER" "FullyQualifiedName~GroundCoverDistributionTests"`, then `wa_sweep t3-final`.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t3`, then `feat(terrain): expose cover candidate decisions without rerolling samples`.

### Task 4: Prepared owned support, compatibility normals and visible paint (4 to 7 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Support/MapPreparedSupport.cs`, `MapSupportPreparation.cs`, `MapSupportSelection.cs`, `KhaozEngine.MapDoc/Foliage/MapFoliageSupport.cs`, `MapFoliageNormals.cs`, `MapFoliageMaterials.cs`, `MapFoliageLimits.cs`, `KhaozEngine.MapDoc/Surfaces/MapFoliageTriangles.cs`.
- Modify: `KhaozEngine.MapDoc/Support/MapSupportQuery.cs:24-34,77-209`, `Surfaces/MapSurfaceCompiler.cs:14-38,145-178`, without changing physical Compile behavior.
- Test/create: `KhaozEngine.MapDoc.Tests/Foliage/FoliageSurfaceFixtures.cs`, `MapFoliageSurfaceTests.cs`, `MapFoliageSupportTests.cs`. Existing support/paint tests remain regressions.

**Interfaces:**
- Consumes released `MapSupportQuery.SampleSurfaceExact(MapExactPoint world, string surfaceId) -> MapSupportResult` (internal :31), `Select(MapSupportRequest) -> MapSupportResult` (:24), `MapPartialPaintCompiler.Compile(MapScopedSurfaces view, string targetSurfaceId, MapRefinementLimits? limits = null) -> MapPaintRegionSet` (:16), `MapEffectiveResources.Acquire(MapSurfaceScope) -> MapScopedSurfaces` (:100), `MapSurfaceCompiler.Compile(MapSurfaceRef, MapSurfacePatch) -> MapCompiledPatch` (:14).
- Exposes `MapSupportQuery.SampleSurface(MapFramePoint point, string surfaceId) -> MapSupportResult` and SampleSurfaceExact publicly with their released signatures. Produces `MapSupportPreparation.Prepare(MapScopedSurfaces view, MapSupportBinding binding) -> MapPreparedSupport`, internal `PrepareFoliage(MapScopedSurfaces view, MapSupportBinding binding) -> MapPreparedSupport` using the compatibility triangles, and `MapPreparedSupport.SampleExact(MapExactPoint point) -> MapSupportResult`. It extracts shared selection/membership arithmetic into internal `MapSupportSelection.Select(MapPreparedSupport prepared, MapExactPoint point, MapSupportBinding binding) -> MapSupportResult`, preserving released statuses/witnesses and using prepared spatial indices rather than Compute per candidate. PrepareFoliage retains explicit LegacyFallback provenance for logical NoDraw support and never certifies it as capture support. No Surface reference height or Space search interval is invented.
- Produces `MapFoliageStatus { Complete, MissingSupport, Unavailable, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }`, `MapFoliageReason { Accepted, OutsideDomain, RasterZero, MissingUnderlay, MaterialNotAllowed, WaterMaterial, Indoor, Solid, RoofAbove, Door, EdgeZero, DensityRejected }`, `MapFoliageSurfacePoint(MapFoliageStatus Status, MapFoliageReason Reason, string? SupportOwnerId, MapFaceKey? Face, float Height, Vector3 Normal, float Density, ushort VisibleMaterial)`.
- Produces `MapFoliageLimits(int MaxCandidates = 4_000_000, int MaxSamples = 262_144, int MaxPrepareScopes = 256, int MaxFaces = 262_144, int MaxRuleCandidates = 4096, int MaxMaskCandidates = 4096, int MaxQueryChunks = 64, long MaxDifferentialCandidates = 64_000_000, long MaxChangeRows = 4_000_000)` and `MapFoliageWork(long Candidates, long Samples, long PrepareScopes, long Faces, long RuleCandidates, long MaskCandidates, long SurfaceQueries)`. Counters charge attempts before work. `MapFoliageSupport.Prepare(MapDocument document, MapEffectiveResources resources, MapFoliageLayer layer, MapFoliageChunkKey chunk, MapAssetClosure assets, MapFoliageLimits limits) -> MapPreparedFoliageSupport`, exposing `Status`, `Work`, `DependencyParts: IReadOnlyList<string>` and `Sample(float worldX, float worldZ) -> MapFoliageSurfacePoint`.
- Produces internal `MapFoliageRefusal(MapFoliageStatus status, MapFoliageWork work, string detail) : Exception` with readonly Status/Work, used only to unwind a traced candidate call to the atomic native batch boundary. Complete point results never encode refusal as zero density, NaN or a fallback height.
- Produces `MapFoliageTriangles.Compile(MapSurfaceRef surface, MapSurfacePatch patch) -> MapCompiledPatch` over the shared triangulation machinery, including authored NoDraw compatibility cells, with no collider installation or physical compiler changes. `MapFoliageNormals.Corner(MapScopedSurfaces view, string surfaceId, long x, long z, float boundaryHeight) -> Vector3` stays internal.

- [ ] **Write failing tests.** `NamedSurfaceAndSpaceBindings_RefuseWithoutFallingThrough`, `UnavailableNormalHalo_IsNotAnEdgeExtension`, `LegacyFlagsCutsAndBorderNormals_MatchTheNamedPolicy`, `PreparedSurface_DoesNotQuerySupportPerCandidate`.

```csharp
Assert.Equal(MapFoliageStatus.MissingSupport, missing.Status);
Assert.Equal(MapFoliageStatus.Unavailable, unavailable.Status);
Assert.Equal(MapFoliageStatus.Ambiguous, ambiguous.Status);
Assert.Equal(MapFoliageStatus.CapacityExceeded, limited.Status);
Assert.Equal(MapFoliageStatus.NotRepresentable, overflow.Status);
Assert.Equal(MapFoliageReason.OutsideDomain, knownOutside.Reason);
Assert.Equal(noFlagsDecision, noDrawAndBlockedDecision);
Assert.Equal(Vector3.Normalize(new Vector3(-0.01f, 1f, 0.015f)), cornerNormal);
Assert.Equal(0, candidatePhaseWork.SurfaceQueries);
```

Test same XZ at surface, bridge, cave and room with asymmetric Space searches, missing owner while another higher floor exists, unloaded patch versus known hole, outside acquired scope versus known domain, current-support seam ties and equal-height ambiguity. All four cuts by four rotations, actual overlay point versus centre, partial paint priorities, unpainted notches, zero underlay even with overlay, water kind, Indoor, NoDraw and Blocked. Keep material selection separate from normal/height triangle sampling. Prepared scopes subdivide a chunk deterministically when the unchanged per-scope face budget needs it, count those scopes, and refuse if P6 preparation totals are exceeded.

- [ ] **Run red:** `wa_test t4-red "$MAPDOC" "FullyQualifiedName~MapFoliageSurfaceTests|FullyQualifiedName~MapFoliageSupportTests"`.
- [ ] **Implement** named-owner and Space selection using the released rules, no invented Select interval. Missing owner is MissingSupport. Recorded unavailable data is Unavailable. LegacyFallback for an authored NoDraw compatibility cell uses its actual triangle, never bilinear movement height. A known hole is OutsideDomain only after complete acquisition. Failure never turns into density 0, even if the raster byte is 0.
- [ ] **Implement** the compatibility arithmetic exactly. Density uses float `u=(x-originX)/cellSize`, `v=(z-originZ)/cellSize`, zero outside [0, Width-1] and [0, Depth-1], last-sample clamp, `a+(b-a)*t` twice then /255. Height uses bound canonical triangles, in legacy triangle order on ties. Corner normal uses central differences over the bound surface lattice: hx = h(x+1,z)-h(x-1,z), hz = h(x,z+1)-h(x,z-1), span = 2 * surface cell metres, Normalize((-hx/span, 1, +hz/span)) for NegativeZ, with negative hz for PositiveZ. Convert native height units to metres before the same float operations as the legacy sampler.
- [ ] **Implement** cross-patch/global corner reads through canonical corner owners/dependencies. For legacy frames, use the source global-lattice precedence (own slot, west, south, south-west, then explicit NormalBoundaryHeight), including region-edge extension. Unavailable advertised dependencies refuse. A same-surface border shares identical normals. Coarse/fine seams interpolate their declared dependency vertices before differences, no neighbouring-floor search. Edge midpoint normals are Normalize(nA+nB), midpoint heights average endpoints, interior sample normals are normalized barycentric corner/midpoint blends. Legacy barycentric inclusion is >= -1e-5, overlay containment is +/-1e-6. Rotate/scale normals through MapResourceTransform.Normal once. Keep physical geometric normals unchanged.
- [ ] **Run green:** red filter as `t4-green`, `wa_test t4-regress "$MAPDOC" "FullyQualifiedName~MapSupport|FullyQualifiedName~MapPartialPaint|FullyQualifiedName~MapSurfaceCompiler"`, then `wa_sweep t4-final`. Reforecast against actual scope/read counts, no stopwatch or allocation benchmark.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t4`, then `feat(mapdoc): prepare owned foliage support and legacy surface policy`.

### Task 5: Layer-owned clearing rules and exceptional typed masks (4 to 6 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageClearance.cs`, `MapFoliageOwners.cs`, `MapFoliageReferences.cs`, `KhaozEngine.MapDoc.Tests/Foliage/FoliageClearanceFixtures.cs`, `MapFoliageClearanceTests.cs`, `MapFoliageOwnerTests.cs`.
- Modify: Task 1 validation and Task 4 prepared surface, `KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs:270-275`, `Prefabs/MapPrefabReferences.cs:114-160,319-354`, `Editing/MapPrefabEditPreparation.cs:155-204`, `Editing/MapPrefabEdits.cs:32-103`, `Editing/MapInteriorEdits.cs:20-70` to validate/remap dependent root foliage references without changing prefab payload 1.
- Test/create: `KhaozEngine.MapDoc.Physics.Tests/MapFoliagePhysicalBoundaryTests.cs`.

**Interfaces:**
- Consumes Task 1 rule/mask DTOs, Task 4 prepared support, released `MapResourceTransform.Point(MapExactPoint) -> MapExactPoint`, `Height(MapExactValue) -> MapExactValue`, `Normal(Vector3) -> Vector3` at Prefabs/MapResourceTransform.cs:14,34,70, and `MapPrefabEdits.Prepare(MapDocument, MapAssetClosure, MapPrefabEdit) -> MapPrefabEditResult` (:32).
- Produces `MapFoliageClearance.Prepare(MapDocument document, MapEffectiveResources resources, MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageLimits limits) -> MapPreparedFoliageClearance`, immutable indexed rules/masks/rooms with `Evaluate(MapFoliageSurfacePoint point, float worldX, float worldZ) -> MapFoliageSurfacePoint`, `Work`, `DependencyParts` and `Bounds: MapFoliageRect`.
- Produces internal `MapFoliageReferences.Remap(MapFoliageSet set, IReadOnlyDictionary<MapPrefabReference, MapPrefabReference> changes) -> MapFoliageSet` and `ValidateOwners(MapDocument, MapEffectiveResources) -> IReadOnlyList<string>`. The common resolver return validates the already-built resources before returning them, without recursively expanding or sampling candidates. Both heads therefore refuse dangling support/owner references before static installation, even when the caller never generates cosmetic blades. References use typed World owners, including effective child/transfer IDs, and follow the active namespace only. No new prefab child kind or asset descriptor member.

- [ ] **Write failing tests.** `OwnerLocalClearance_UnpackRemapAndRemovalAreAtomic`, `NewBoundPlacement_UsesLayerRulesWithoutAnOwnerMask`, `DoorDistance_IsStrictAndFourNeighbourFadeUsesCentreEligibility`, `InteractionEnvelopeOneMetre_IsNeverAFoliageBlocker`.

```csharp
Assert.Equal(MapFoliageReason.Door, insideRadius.Reason);
Assert.NotEqual(MapFoliageReason.Door, exactlyAtRadius.Reason);
Assert.NotEqual(MapFoliageReason.Door, outsideRadius.Reason);
Assert.Equal(0.5f, halfRimDensity / originalDensity);
Assert.Equal(beforeAssetBytes, afterAssetBytes);
Assert.Equal(beforeColliderDigest, afterMaskColliderDigest);
Assert.Equal(beforeUnpackDecisions, afterUnpackDecisions);
Assert.Equal(beforeState, stateAfterOrphanRefusal);
```

Use invented door clearance 0.75, edge fade 0.25, samples at radius minus 0.0001, exactly radius, radius plus 0.0001, and 0.125 m from an ineligible cardinal neighbour. Test rotated/non-square footprint, translated/rotated/scaled prefab at (-12.25,-450,7.125), yaw 0.371 and scale 1.2, override removal/restoration, unpack, extract, remap, owner deletion with/without same-transaction mask removal, and identical text IDs in different typed namespaces. A 0.1 m visual/physical prop with a 1 m interaction envelope must produce exactly the configured clearing, including accepted blades inside the envelope but outside the footprint. Test rooms' half-open vertical bounds, upper roof over a lower floor, a same-height roof and unrelated cave at identical XZ.

- [ ] **Run red:** `wa_test t5-red "$MAPDOC" "FullyQualifiedName~MapFoliageClearanceTests|FullyQualifiedName~MapFoliageOwnerTests"`, `wa_test t5-red-physical "$MP" "FullyQualifiedName~MapFoliagePhysicalBoundaryTests"`.
- [ ] **Implement** rules in deterministic authored order, matching AssetId ordinally and optional PlacementTag with the legacy case-insensitive tag rule. Solid tests the selected support cell centre against the effective owner-local half-open footprint, matching source tile sets even on an exact world tile boundary. Door tests the actual sample point against the footprint centre with strict world distance < scaled DoorClearance. RoofAbove tests the support cell centre against its footprint and requires strictly higher transformed LocalPlaneY, independent of roof render visibility. This is explicitly the legacy cell policy for free transforms too. Layer booleans gate solid/indoor/roof behavior. Every rule/mask first matches its explicit target support owner to the sampled owner. Solid/door ownership uses effective placement provenance, roof ownership uses linked room floors, with typed OwnerSupports for otherwise explicit owners. A roof over the surface cannot clear an unrelated deep cave. Imported higher-plane roofs may explicitly target each source layer they cleared. Correspondences contain no geometry, follow owner identity and never infer highest-XZ, a guessed Y interval or interaction shapes. Conflicting/missing correspondence refuses.
- [ ] **Implement** indoor rejection as native Indoor flag or shared R5 room membership at the selected support point, no invented exterior. After base tile eligibility, DoorFactor then edge factor multiply density. Edge factor takes the minimum of 1 and distance/EdgeFade for each of four ineligible cardinal support cells, using neighbour-centre visible material/flags/solid/roof tests, not door tests or arbitrary geometry distance. Normalize density only with the legacy clamp. Mask/owner support references transform once and survive transfers. Owner removal refuses before publication unless the same candidate removes its dependants. Root-owned correspondence contains no clearing geometry and does not replace asset/tag rules.
- [ ] **Run green:** both red filters as green, then `wa_sweep t5-final` after review. Released prefab/cave tests run in that sweep.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t5`, then `feat(mapdoc): apply foliage clearance through effective owner transforms`.

### Task 6: Immutable samples, dependency identity and policy differential (3 to 5 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageSamples.cs`, `MapFoliageDistribution.cs`, `MapFoliageDependencies.cs`, `MapFoliageDifferential.cs`, `MapFoliageDifferentialCore.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageDistributionTests.cs`, `MapFoliageDifferentialTests.cs`, `MapFoliageBudgetTests.cs`.
- Modify: Task 4/5 preparation outputs only where necessary to publish immutable captured inputs and counters.

**Interfaces:**
- Consumes Task 3 GenerateTraced, Tasks 4/5 prepared support/clearance and Task 2 chunk read outcomes. `MapPrefabResolver.Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits? limits = null) -> MapEffectiveResources` is released at Prefabs/MapPrefabResolver.cs:25.
- Produces `MapFoliageAddress(string LayerId, string? SupportOwnerId, int X, int Z)`, `MapFoliageSampleKey(MapFoliageAddress Address, int DistributionVersion, MapFoliagePolicy Policy)`, `MapFoliageSample(MapFoliageSampleKey Key, MapFaceKey? Face, string AssetId, IReadOnlyList<string> MaterialResourceIds, ushort VisibleMaterial, Vector3 Position, Quaternion Orientation, float Scale, Matrix4x4 Transform, float ThinningRank)`. Accepted keys require nonempty resolved support owner. Rejected Surface-bound addresses retain the requested owner, unresolved Space-bound rejections may carry null, never a fabricated floor. Null sorts before ordinal owner IDs in diagnostic joins.
- Produces immutable `MapFoliageSampleBatch(string LayerId, MapFoliageChunkKey Chunk, MapFoliageStatus Status, string DependencyDigest, IReadOnlyList<MapFoliageSample> Samples, MapFoliageWork Work)` and `MapFoliageDecision(MapFoliageAddress Address, MapFoliageReason Reason, GroundCoverDecision Distribution, MapFoliageSample? Sample)`. Refused batches expose no usable Samples. Pose retains the original matrix, not a reconstructed quaternion round trip.
- Produces `MapFoliageDistribution.Generate(MapDocument document, MapAssetClosure assets, string layerId, MapFoliageChunkKey chunk, MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null) -> MapFoliageSampleBatch`, preparing one captured effective view for the chunk and its halo. Also `Query(MapDocument document, MapAssetClosure assets, string layerId, RectArea area, MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null) -> MapFoliageQueryResult`, where `MapFoliageQueryResult(MapFoliageStatus Status, IReadOnlyList<MapFoliageSampleBatch> Batches, MapFoliageWork Work)` covers at most 64 logical chunks. Query covers its entire half-open requested area, including zero-density addresses outside the raster, so the private comparator can inspect every source-region candidate. Logical query chunks may have negative indices, persisted chunks may not. `MapFoliageDependencies.Compute(MapPreparedFoliageSupport support, MapPreparedFoliageClearance clearance, MapFoliageLayer layer, MapFoliageChunkKey chunk) -> string` uses scheme `kemap/foliage-batch/1`.
- Produces `MapFoliageChangeKind { Added, Removed, Changed }`, `MapFoliageChange(MapFoliageAddress Address, MapFoliageChangeKind Kind, MapFoliageDecision? Before, MapFoliageDecision? After)`, `MapFoliageDifferentialResult(MapFoliageStatus Status, long Added, long Removed, long Changed, string Digest, string BeforeIdentity, string ProposalDigest)` and `MapFoliageDifferential.Compare(MapDocument before, MapDocument after, MapAssetClosure assets, string layerId, MapFoliageLimits? limits = null, Action<MapFoliageChange>? observe = null) -> MapFoliageDifferentialResult`. Observer rows are provisional until the final Complete result, and ordinary mutation dry-runs return counts/digest rather than millions of rows.
- Produces internal `MapFoliageDifferentialCore.CompareChunk(IReadOnlyList<MapFoliageDecision> before, IReadOnlyList<MapFoliageDecision> after, MapFoliageLimits limits) -> IReadOnlyList<MapFoliageChange>`, the production join used by Compare, bounded to one chunk. Its tests supply invented version-1/version-2 decision records as data, proving changed rows despite changed versioned keys. They do not deserialize an unsupported document or bypass validation. Integration uses a known legacy-tile/1 policy-definition change in NormalBoundaryHeight plus controlled density edits. Unknown public policy versions still refuse. No policy registration framework or geometric policy ships in R7.

- [ ] **Write failing tests.** `SampleBatch_IsIndependentOfChunkOrderReloadAndLayerName`, `PolicyDifferential_JoinsStableAddressesBeforeVersionedKeys`, `DependencyDigest_ChangesOnlyForIntersectingInputs`, `LargeSparseDomain_RefusesBeforeAreaAllocation`.

```csharp
Assert.Equal(wholePoses, reorderedAndReloadedPoses);
Assert.Equal(oldPositions, renamedLayerPositions);
Assert.NotEqual(oldKeys, renamedLayerKeys);
Assert.Contains(observedChanges, c => c.Kind == MapFoliageChangeKind.Changed);
Assert.Contains(observedChanges, c => c.Kind == MapFoliageChangeKind.Added);
Assert.Contains(observedChanges, c => c.Kind == MapFoliageChangeKind.Removed);
Assert.Equal(MapFoliageStatus.CapacityExceeded, refused.Status);
Assert.Empty(refused.Samples);
Assert.Equal(0, refused.Work.Samples);
```

Exercise empty raster, absent chunks, corner/last-row interpolation, chunk-edge jitter roots, one candidate owned exactly once, independent mutable inputs, stale source snapshots, exact rank/scale, reordered model weights, seed/policy/material/paint/room/roof/door/mask/marker changes. The invented versioned decision pair changes normal on one existing address, rejects one and permits one previously rejected address. Assert stable-address joins before versioned keys through the actual core. Separately integrate the supported policy-definition change with density edits through complete Compare. Use a 1,048,576 by 1,048,576 logical raster with exactly two 1024-byte chunks, inspect one chunk, and exceed each P6 budget by one with counted fakes. Assert no iteration over Width*Depth, no domain-sized allocation and bounded read counters, without an allocation benchmark.

- [ ] **Run red:** `wa_test t6-red "$MAPDOC" "FullyQualifiedName~MapFoliageDistributionTests|FullyQualifiedName~MapFoliageDifferentialTests|FullyQualifiedName~MapFoliageBudgetTests"`.
- [ ] **Implement** one prepared chunk pipeline with no full support query per candidate. Trace captures native eligibility reasons before distribution thinning, retaining OutsideDomain/RasterZero distinction. Do not collect rejected rows in ordinary batches. Requests own half-open rectangles partitioning the valid raster intervals, with the terminal sample available only for interpolation. Dependency digests include the exact interpolation halo bytes, normal neighbours, prepared paint/material/room/owner/rule/mask/marker values, effective transforms, model closure and algorithm/policy versions, excluding unrelated global root/annotation digests.
- [ ] **Implement** fail-closed streaming differential across the bounded union of affected chunks, sorted by stable address within (chunk Z, chunk X), no truncation. It binds old identity and proposal semantics, includes accepted and rejection changes and version changes on retained samples, and returns complete or typed refusal. Retain one chunk's rows, streaming the final digest/counts and optional keyed changes. Enforce P6's 64,000,000 aggregate candidate and 4,000,000 change-row ceilings before adding each row. A failed stream cannot issue an acceptance token. The synthetic fixture observes a tiny list, the real comparison never retains all rows. Query partitions even off-raster area without inventing samples or skipping source candidate addresses.
- [ ] **Run green:** red filter as `t6-green`, then `wa_sweep t6-final`.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t6`, then `feat(mapdoc): publish deterministic foliage batches and keyed differentials`.

### Task 7: Pure edits, one-step commands and dependent chunk effects (4 to 7 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapFoliageEdits.cs`, `MapFoliageBrush.cs`, `MapFoliageEffects.cs`, `KhaozEngine.MapEditor/NativeFoliageCommands.cs`, `NativeFoliageEffects.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageEditTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/FoliageEditorFixtures.cs`, `NativeFoliageCommandTests.cs`, `NativeFoliageEffectTests.cs`.
- Modify: `KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs:13-32,47-83,85-143`, Task 2 snapshot/write-set integration, `KhaozEngine.MapEditor/NativeDocumentTransaction.cs:71-135`, `KhaozEngine.MapEdit.Tool/MapEditSession.NativeHistory.cs:79-169`. Preserve marker/water effect composition and the final R6 publication fix.
- Test/create: `KhaozEngine.MapEditor.Tests/MapEditTool/NativeFoliagePublicationTests.cs`. Modify the released native publication/history tests only to extend their existing coverage, no relaxed assertions.

**Interfaces:**
- Consumes Tasks 1 to 6, released `MapPrefabEditResult(MapDocument After, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects)` at Editing/MapPrefabEdits.cs:27, and bound command pattern `MarkerEditCommand(MapMarkerEdit edit)` in MapEditor/NativeMarkerCommands.cs:11-30. `INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) -> NativeDocumentPreparation` remains internal and unchanged.
- Produces abstract `MapFoliageEdit(string Label)`, `MapSetFoliageLayer(MapFoliageLayer Layer, string? ExpectedDifferentialDigest = null)`, `MapSetFoliageDensity(string LayerId, int X, int Z, int Width, int Depth, IReadOnlyList<int> Density)`, `MapPaintFoliage(string LayerId, float WorldX, float WorldZ, float RadiusMetres, byte Target, float Hardness)`, `MapRemoveFoliage(string LayerId)`, `MapFoliageEditSequence(IReadOnlyList<MapFoliageEdit> Edits, MapPrefabEdit? OwnerEdit)` inheriting MapFoliageEdit with fixed developer labels.
- Produces `MapFoliageEdits.Prepare(MapDocument document, MapAssetClosure assets, MapFoliageEdit edit) -> MapPrefabEditResult`, `PreviewPolicyChange(MapDocument document, MapAssetClosure assets, MapSetFoliageLayer edit) -> MapFoliageDifferentialResult`, `MapFoliageBrush.Apply(MapFoliageLayer layer, MapPaintFoliage brush) -> MapFoliageLayer`, and public `FoliageEditCommand(MapFoliageEdit edit) : EditorCommand` implementing the bound command interface.
- Produces `MapFoliageChunkChange(string LayerId, MapFoliageChunkKey Chunk, MapFoliageRect? OldBounds, MapFoliageRect? NewBounds, string? BeforeDigest, string? AfterDigest)`. Adds `MapNativeInvalidation.Foliage = 256`, init-only `MapNativeEditEffects.FoliageChunks: IReadOnlyList<MapFoliageChunkChange>`, omitted from Describe when empty, and internal `MapFoliageEffects.Describe(MapDocument before, MapDocument after, MapEffectiveResources oldResources, MapEffectiveResources newResources, MapNativeEditEffects trigger) -> IReadOnlyList<MapFoliageChunkChange>`.
- Produces `NativeFoliageEffects.Combine(MapNativeEditEffects trigger, IReadOnlyList<MapFoliageChunkChange> chunks) -> MapNativeEditEffects`. Native transaction composition calls it for terrain, paint, placement, prefab, room, roof, marker, closure and foliage edits, including undo/redo and complete-snapshot callbacks, on the exact final publication candidate.

- [ ] **Write failing tests.** `MetreBrush_QuantizesPositiveZAndDoesNotAllocateDomain`, `FoliageCommand_DirectSessionUndoRedoAndRefusalAreAtomic`, `EveryDependencyTransaction_ReportsOldAndNewFoliageChunks`, `LateCallbackFoliage_PublishesOneSnapshotEffectsAndRedo`.

```csharp
Assert.Equal((byte)128, fullHardnessCentre);
Assert.Equal((byte)64, halfLinearRim);
Assert.Equal((byte)0, outsideBrush);
Assert.Equal(beforeCursor + 1, acceptedCursor);
Assert.Equal(beforeState, refusedState);
Assert.Equal(MapNativeInvalidation.Foliage, densityOnly.Invalidates);
Assert.Equal(beforeGeometryBounds, densityOnly.NewSpatialBounds);
Assert.Equal(publishedBytes, redoBytes);
Assert.Equal(publishedChunkEffects, redoChunkEffects);
Assert.Equal(publishedDependencyDigest, nextEditBeforeDigest);
```

Use a zero raster, target 128, radius 2 m, hardness 0.5. Radius boundary has zero weight, inner radius has full weight, sample at distance 1.5 m gives byte 64. Test hardness 0 and 1, ties to even, negative origin, non-square rows, 255, wrong numeric-byte lengths/ranges, 65 chunks and 65,537 addresses refused before writes. For policy set, missing/stale differential token refuses without history or command-state capture. A valid token accepts once against the exact before identity. No-op mutations emit empty effects and do not add a history entry.

For each real terrain/paint/placement/prefab/interior/roof/marker transaction, use a valid control without foliage that succeeds, then add a dependent layer and verify bounded changed chunks on both old and new footprints. A marker referenced as a mask owner participates, an unrelated marker does not. Removing a required support refuses while another floor remains. A sequence removing mask/correspondence and owner together succeeds as one command, and undo restores both. Callback test edits density and a rule after staging a different native command, checks save/reload, effects, redo and next-edit before-state against the single published snapshot.

- [ ] **Run red:** `wa_test t7-red-pure "$MAPDOC" "FullyQualifiedName~MapFoliageEditTests"`, `wa_test t7-red-editor "$EDITOR" "FullyQualifiedName~NativeFoliageCommandTests|FullyQualifiedName~NativeFoliageEffectTests|FullyQualifiedName~NativeFoliagePublicationTests"`.
- [ ] **Implement** pure detached edits with strict ranges, one shared brush, no tool-owned arithmetic. Sequence preparation applies its foliage edits to one detached candidate before its optional owner edit, validates the complete result, and emits a complete-snapshot publication. Direct Apply/Revert without the required verified closure refuses before mutation. Acceptance alone captures command snapshots. Preserve numeric reservations/tombstones under snapshot undo as released.
- [ ] **Implement** narrow effects through the central native transaction and the final revalidated callback publication path, reusing accepted before-expansion when its semantic fingerprint is valid. Density/rules/seed/masks set only Foliage, not Physics/Nav/Unbounded. Geometry edits retain their own existing flags. Put cosmetic bounds solely in FoliageChunks, never expand world geometry or nav height bounds. Include foliage in SemanticHash and compare the final candidate once, then derive effects and redo from that same snapshot.
- [ ] **Implement** dependency selection from typed owners and old/new support/paint/room/rule/mask/marker intersections, not every layer at the same XZ. Widen changed raster rectangles by one density cell for bilinear interpolation, two bound support cells for central-difference corner influence, EdgeFade and the maximum transformed door radius. Intersect that conservative halo with each owned domain and return sorted (layer ID, Z, X) chunk rows. Include both old and new owner footprints and digest values. Refuse a dependency scan exceeding P6 limits, never silently omit chunks or emit an empty success.
- [ ] **Run green:** both red filters as green, `wa_test t7-regress "$EDITOR" "FullyQualifiedName~NativePublicationWorkTests|FullyQualifiedName~NativeSnapshotPublicationTests|FullyQualifiedName~NativeSessionHistoryTests|FullyQualifiedName~NativeMarkerSupportEditTests"`, then `wa_sweep t7-final`. Reforecast after this gate.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t7`, then `feat(mapeditor): publish atomic foliage commands and bounded effects`.

### Task 8: Five native foliage verbs and real wire parity (2 to 4 engineer-days)

**Files:**
- Create: `KhaozEngine.MapEdit.Tool/MutationService.Foliage.cs`, `QueryService.Foliage.cs`, `Tools/FoliageTools.cs`, `FoliageResults.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/FoliageToolTests.cs`, `FoliageWireTests.cs`.
- Modify: `KhaozEngine.MapEdit.Tool/Tools/McpBootstrap.cs:43-54`, `KhaozEngine.MapEditor.Tests/MapEditTool/McpAdapterTests.cs:32-115` pinned verb inventory and count, `KhaozEngine.MapEdit.Tool/README.md:384` inventory and foliage API table. QueryService is already partial at :16 and needs no declaration edit. Keep one pinned inventory.

**Interfaces:**
- Consumes Task 7 Prepare/FoliageEditCommand and Task 6 Generate/PreviewPolicyChange, released `McpWire.CallAsync(McpClient, string name, IReadOnlyDictionary<string, object?> arguments) -> Task<CallToolResult>` and ListAsync in MapEditor.Tests/MapEditTool/McpWire.cs:24-29. CallTimeout remains TimeSpan.FromSeconds(60), collection McpWireCollection.Name is non-parallel.
- Produces `MapFoliageMutationResult(bool Applied, MapFoliageStatus Status, string Identity, string Effects, bool Dirty, string? UndoLabel, string? RedoLabel, MapFoliageDifferentialResult? Differential)`, sealed `MapFoliageLayerInfo(MapFoliageLayer layer)` copying exactly the Task 1 layer metadata properties/types except Chunks, plus readonly `ChunkKeys: IReadOnlyList<MapFoliageChunkKey>` from the full ChunkDigests directory, with no DensityAt API, and `MapFoliageReadResult(MapFoliageLayerInfo Layer, IReadOnlyList<MapFoliageChunk> Chunks, MapFoliageSampleBatch? Preview)` with detached copies. Metadata reads cannot accidentally return all density bytes or masquerade as a zero-filled layer.
- Produces service `MutationService.FoliageApply(MapFoliageEdit edit, bool dryRun = false) -> MapFoliageMutationResult`, `QueryService.FoliageGet(string layerId, int? x = null, int? z = null, int? width = null, int? depth = null, MapFoliageChunkKey? previewChunk = null) -> MapFoliageReadResult`. No rectangle returns metadata only, chunks empty, unless a single previewChunk is supplied. All four rectangle fields travel together and obey brush/read limits.
- Produces Tools methods `FoliageLayerSet(string layerJson, string? expectedDifferentialDigest = null, bool dryRun = false) -> MapFoliageMutationResult`, `FoliageGet(string layerId, int? x = null, int? z = null, int? width = null, int? depth = null, string? previewChunkJson = null) -> MapFoliageReadResult`, `FoliageDensitySet(string layerId, int x, int z, int width, int depth, int[] density) -> MapFoliageMutationResult`, `FoliagePaint(string layerId, float worldX, float worldZ, float radiusMetres, int target, float hardness) -> MapFoliageMutationResult`, `FoliageRemove(string layerId) -> MapFoliageMutationResult`. Attributes name exactly `foliage_layer_set`, `foliage_get`, `foliage_density_set`, `foliage_paint`, `foliage_remove`. Layer JSON uses the Task 1 closed monolithic layer shape and base64 chunks. Reject target outside 0 to 255 before byte conversion.

- [ ] **Write failing tests.** `FiveFoliageVerbs_MakeExactInventory101`, `FoliageWire_MatchesDirectCommandsBytesEffectsAndHistory`, `FoliageRead_IsDetachedAndPreviewIsCpuOnly`, `FoliageWire_RefusalAndDryRunChangeNothing`.

```csharp
Assert.Equal(101, listedTools.Count);
Assert.Equal(expectedVerbs.OrderBy(x => x), listedNames.OrderBy(x => x));
Assert.Equal(directState, wireState);
Assert.Equal(directEffects, wireEffects);
Assert.Equal(beforeState, refusedState);
Assert.Equal(beforeState, dryRunState);
Assert.Equal(TimeSpan.FromSeconds(60), McpWire.CallTimeout);
```

Exercise real JSON/base64, numeric rows, partial/malformed rectangles, fraction/nonfinite fields, 256/-1 target, unknown policy, orphan owner, missing support, future payload, duplicate JSON member, no-match removal, repeated read mutation, undo/redo and save/reopen. Compare pure command, bound EditorHistory command and MCP results. A wire class joins McpWireCollection and every call uses McpWire's own 60 s token. No whole-workflow timeout and no window boot.

- [ ] **Run red:** `wa_test t8-red "$EDITOR" "FullyQualifiedName~FoliageToolTests|FullyQualifiedName~FoliageWireTests|FullyQualifiedName~McpAdapterTests.ToolRegistry_ExposesAllDocumentQueryMutationVerbs"`. Expect the 101-tool assertion and new verbs to fail before implementation. Zero matches are failure.
- [ ] **Implement** thin adapters only. Use the shared closed decoder, pure edits, CPU preview and native publication. No independent mutation/brush or hidden terrain regeneration. A policy-changing dry-run returns the complete differential identity without changing state. Mutation must present its exact digest. Register tools, update count/inventory and README together.
- [ ] **Run green:** the same matched red filters with green labels, then `wa_sweep t8-final`.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t8`, then `feat(mapedit): add five native foliage verbs with command parity`.

### Task 9: Public synthetic acceptance, OA9 and three-platform pins (3 to 5 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.Tests/Foliage/FoliageAcceptanceFixtures.cs`, `NativeFoliageAcceptanceTests.cs`, `FoliagePlatformPinsTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapFoliageWorldTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/FoliageSchemaParityTests.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/FoliageAcceptanceWorkflowTests.cs`, `KhaozEngine.Tests/ArchitectureTests.Foliage.cs`, `.github/workflows/foliage-conformance.yml`.
- Modify: the living acceptance/handoff tables in `KhaozEngine.MapDoc/README.md`, `KhaozEngine.MapDoc.Physics/README.md` and `KhaozEngine.MapEditor/README.md`. Existing fixture source links cover Foliage fixtures.

**Interfaces:**
- Consumes Tasks 1 to 8, released `MapWorldBuilder.Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) -> MapBuiltWorld` at MapDoc.Physics/MapWorldBuilder.cs:42, `MapBuiltWorld` geometry/statics/annotations, released stacked and prefab-cave fixtures and the existing schema evaluator used by MapEditor.Tests. No new test project or broad project reference.
- Produces the three exact named OA9 facts, `DensitySeedRuleAndMask_NeverChangeStaticsNavOrWorldBounds`, `IndependentHeadsAndStorageForms_ProduceIdenticalOwnedFoliage`, `FoliageRuntime_HasNoPhysicsRenderGpuTileWorldDependency`, and a bounded public conformance fixture run on Linux, macOS and Windows. No shipped data or counts become pins.

- [ ] **Write failing acceptance assertions** in `StackedLayerFoliageAndExclusions_RemainInOwnedDomain`, `SurfaceDensity_DoesNotPopulateDeepCave`, `CaveOwnerTransform_InvalidatesOnlyDependentSamples`. Use independent client/server loads in each storage form and the original released topology. Surface, bridge, upper room and cave share XZ. Give each an explicit layer binding, with solid, room, roof, door marker and exceptional mask affecting just the intended domain.

```csharp
Assert.All(caveSamples, s => Assert.Equal(caveOwner, s.Key.Address.SupportOwnerId));
Assert.DoesNotContain(surfaceSamples, s => s.Key.Address.SupportOwnerId == caveOwner);
Assert.Equal(unrelatedBatchBefore, unrelatedBatchAfter);
Assert.Contains(effects.FoliageChunks, c => c.LayerId == caveLayer);
Assert.DoesNotContain(effects.FoliageChunks, c => c.LayerId == otherCaveLayer);
Assert.Equal(beforeStatics, afterDensitySeedRuleAndMaskStatics);
Assert.Equal(beforeNavCapture, afterDensitySeedRuleAndMaskNavCapture);
Assert.Equal(beforeWorldBounds, afterDensitySeedRuleAndMaskWorldBounds);
Assert.Equal(4, independentHeads.Count);
```

Move/rotate/scale the original cave owner, then override, unpack, remap, save/reload and remove. Assert old/new dependent chunk bounds and poses, and independent mutable state on four heads. Remove or unload surface support while a deep cave remains, require typed refusal and no transfer. Test dry cave under surface water and separately flooded cave. Compatibility rejects water material only, it does not add an unapproved global or contained-medium rejection rule. Density extents, mesh padding and high markers must never enter navigation tile height bounds. Physical/pick queries contain no generated blade targets. Explicit scenery retains original statics/picking.

- [ ] **Write schema/platform tests.** Evaluate every Task 1 malformed/valid case against the actual schema and runtime. Pin exact keys, candidates, decisions, model choices, scale/rank bits and toleranced poses from an invented negative-origin, non-square, twisted and cut/rotated fixture. Include border interpolation, NoDraw/Blocked and non-quarter-turn effective owners. Each platform checks the same committed expectations, no per-OS regeneration. Sensitivity controls change one density byte, decision, model and orientation beyond tolerance and must fail comparison.
- [ ] **Run red:** `wa_test t9-red "$MAPDOC" "FullyQualifiedName~NativeFoliageAcceptanceTests|FullyQualifiedName~FoliagePlatformPinsTests"`, `wa_test t9-red-world "$MP" "FullyQualifiedName~MapFoliageWorldTests"`, `wa_test t9-red-schema "$EDITOR" "FullyQualifiedName~FoliageSchemaParityTests|FullyQualifiedName~FoliageAcceptanceWorkflowTests"`, `wa_test t9-red-architecture "$RUMP" "FullyQualifiedName~FoliageRuntime"`. Missing new acceptance wiring/pins is red, a correctly implemented earlier behavior may already pass and is recorded as regression rather than fabricated red.
- [ ] **Implement** the invented acceptance fixtures, actual workflow assertions and architecture guard. The architecture guard walks direct/transitive production edges and absence of sample-driven colliders, not source-name guesses. Create `foliage-conformance` for push, pull_request and workflow_dispatch, matrix `ubuntu-26.04`, `macos-26`, `windows-latest`, .NET 10.0.x and the repository's current pinned checkout/setup-dotnet/upload-artifact actions. Restore/build the named area projects, run only FoliagePlatformPinsTests plus GroundCoverTraceTests once per OS, upload TRX, and require nonzero tests and no skips. Hosted jobs do not call the dev Mac's build-slot. No private harness selection, credentials or load loop.
- [ ] **Run green:** the four red filters as green and `wa_sweep t9-final`. Review the workflow for the exact hosted matrix, read-only contents permission, pinned actions, portable bash commands and no private harness selection. Controller requires successful workflow parsing and all three hosted conformance legs on the exact pushed candidate before final acceptance, no local attempt to simulate other operating systems.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t9`, then `test(mapdoc): prove native foliage ownership and platform conformance`.

### Task 10: Independent legacy access and private comparison contract (4 to 7 engineer-days)

**Files:**
- Create: `KhaozEngine.TileWorld.Render3D/TileFoliageDecision.cs`, `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedFoliageInputs.cs`, `ShippedFoliageComparison.cs`, `ShippedFoliageComparisonTests.cs`, `ShippedFoliageInputGuardTests.cs`, `ShippedFoliageGuardFixtures.cs`, `ShippedFoliageManifest.cs`. Split by concern before 800 lines, never add unowned helpers silently.
- Modify: `KhaozEngine.TileWorld.Render3D/TileFoliageSurface.cs:9,43-117,240-283`, harness `KhaozEngine.MapDoc.OracleHarness.Tests.csproj:13-20`, harness `README.md` private foliage contract, `KhaozEngine.Tests/ArchitectureTests.MapDoc.cs:31-52`. Update obsolete private synthetic format pins to 9 while preserving earlier readers' policies.
- Test/create: `KhaozEngine.Render.Tests/Terrain/TileFoliageDecisionTests.cs` for the additive legacy diagnostic seam, independent of private data.
- Private prerequisite, controller-owned in the game repository only: retained `proofs/r7-foliage-oracle/generator/build-scaffold.py`, every helper/project, correspondence manifests and usage README. The engine worker neither creates nor commits private inputs.

**Interfaces:**
- Consumes released `TileFoliageSurface(TileWorldDocument doc, TileWorldCatalogs catalogs, TileFoliageLayer layer, bool smoothNormals = true)` and `Sample(float worldX, float worldZ) -> GroundCoverSample`, plus Task 3 GenerateTraced. Adds `SampleDetailed(float worldX, float worldZ) -> TileFoliageDecision`, with `TileFoliageDecision(GroundCoverSample Sample, TileFoliageRejection Reason, ushort VisibleMaterial)` and an enum matching the actual legacy rule stages. Old Sample delegates without changing arithmetic or results. No native sampler generates expected rows.
- Consumes released `PrivateOracleEntry.Run(Action<PrivateOracleInputs, PrivateOracleReport>)`, `PrivateOracleInputs.Require(IReadOnlyDictionary<string,string?>) -> PrivateOracleInputs`, `PrivateOracleReport.Check(string category, bool ok, string detailJson) -> void` and `Record(string category, string detailJson) -> void`, and `ShippedSourceInventory.ReadWorld(PrivateOracleInputs) -> (TileWorldDocument Document, TileWorldCatalogs Catalogs)`.
- Produces internal `ShippedFoliageInputs.Read(PrivateOracleInputs inputs) -> ShippedFoliageInputs`, `ShippedFoliageComparison.Compare(ShippedFoliageInputs inputs, PrivateOracleReport report) -> void`, fact `ShippedFoliage_PreservesEveryByteCandidatePoseAndDecision`, and `ShippedFoliageGuardFixtures.Create(string variant) -> ShippedFoliageGuardFixture`. Guard fixture exposes only invented Environment and PublicSecretMarker, owns secure temporary paths and IDisposable cleanup. Private fact/guards carry UnsupportedOSPlatform("windows"), like the released secure-report entry. Public platform pins are separate.

- [ ] **Write failing public synthetic guards** for complete invented extraction, missing each of four variables, wrong provenance digest, duplicate/case-variant/unknown members, missing/unattested region/catalog/helper/resource/chunk, incomplete owner correspondence, extra/missing native sample, wrong seed/model/rank/reason, one changed byte, unsafe/existing/symlink report path, escaped dependency, stale input after loader and failed generator self-check. Discovery/static initialization reads no environment or files. `TileFoliageDecisionTests` proves SampleDetailed.Sample equals released Sample bit-for-bit for every exclusion class and all channel regressions.

```csharp
Assert.Equal(7, harnessDirectEngineReferences.Count);
Assert.Equal(0, validSyntheticFailures);
Assert.Contains("foliage-byte", changedByteCategories);
Assert.Contains("foliage-decision", changedDecisionCategories);
Assert.Contains("foliage-rank", changedRankCategories);
Assert.DoesNotContain(fixture.PublicSecretMarker, publicFailureMessage);
Assert.Equal(legacySample, detailed.Sample);
```

- [ ] **Run red:** `wa_test t10-red-guard "$ORACLE" "FullyQualifiedName~ShippedFoliageInputGuardTests"`, `wa_test t10-red-edge "$RUMP" "FullyQualifiedName~OracleHarness_IsUnreferencedAndNotPackable"`, `wa_test t10-red-legacy "$RENDER" "FullyQualifiedName~TileFoliageDecisionTests"`. Never run the oracle project unfiltered, including during solution/candidate verification.
- [ ] **Implement** seventh test-only reference to `KhaozEngine.TileWorld.Render3D`, exact architecture list and README together. Add detailed legacy decisions by exposing the real TileAllowed/DoorFactor/EdgeFactor stages, not reimplementing them in the comparator. Source expected distribution calls the actual legacy sampler and Task 3 channel kernel. Native actual distribution calls only native adapters. Public synthetic scalar/channel pins and legacy bit regressions guard their shared distribution arithmetic against circular agreement.
- [ ] **Conditional Task 10F, GPU-free legacy seam fallback.** If a headless synthetic construction actually initializes GPU/window state, stop Task 10 at that evidence. With controller confirmation of P11, create `KhaozEngine.TileWorld/TileFoliageSampling.cs` and `TileFoliageSamplingNormals.cs`, move only CPU sampling/normal/index inputs there, retain TileFoliageSurface as its source-compatible wrapper, and reference that seam in the harness. Add `LegacyCpuSampling_DoesNotLoadGpuOrWindowing` and bit-exact wrapper parity tests in Render.Tests. Run their focused red/green filters through wa_test, the three-project sweep and architecture guards, commit those explicit paths as `refactor(tileworld): expose gpu-free legacy foliage sampling`, and independently review before resuming Task 10. Keep the approved seventh reference only if it is inert, otherwise amend the reference guard to the proven GPU-free set under a recorded ruling. Never copy policy or waive private acceptance.

The full private input closure is declared before extraction, carrying R4-P40 and R6's retained-generator lesson. Every path is extraction-relative, contained and symlink-free. Attest exactly one source world manifest, all listed region bytes needed for cells/corners and their normal halo, every referenced catalog, all source prefab definitions and effective placed-member/override data that determine footprint/collision/tag/plane, every relevant model/mesh/material/texture/support resource, independent layer/material/model/support/owner/rule/mask correspondences, the generator and every imported helper/project/tool version, a monolithic format-9 scaffold, all tiled manifests/chunks, and every native advertised transitive resource. Record full source commit, explicit tag or empty tag, engine pin, generator commit and candidate engine SHA. No fallback checkout search or unattested helper is allowed.

The retained generator reads source archetype footprints, collision kinds and case-insensitive door/roof tags and maps them to owner-local layer clearance rules independently of the native comparator. It records exact source-plane to native-support ownership, original settings and positive-Z density addresses, preserving all bytes without baking predicate rejections into the raster. Exception masks require an explicit source reason not representable by a rule. It creates no replacement art or guessed rooms. Self-check compares source footprint/door/roof/normal rules against generated native correspondence before attesting. A difference beyond fixed tolerance stops generation and asks the controller to adjudicate the rule (R-GEN-2). Retain the generator in private git before producing and attesting its output. Two fresh extractions must be byte-identical without hand edits.

Provenance keeps every released top-level field/aggregate rule. Its closed foliage `comparison` object has exactly `schemaVersion` (1), `nativeDocumentPath`, `nativeTiledManifestPath`, `foliageManifestPath`, nullable `buildingManifestPath` and `expectations`. All paths are attested extraction-relative paths, no absolute paths. `expectations` has exactly nonnegative integer `layers`, `rasterBytes`, `storedChunks`, `sourceOwners`, `models`, `ownedCandidates`, `accepted` and `rejected`. Counts come from the independently enumerated source, with accepted/rejected from the actual legacy sampler, never native results. Private source tile size 1 is required by the released terrain scaffold conversion, it is not a native engine limit.

The closed foliage manifest has exactly `schemaVersion` (1), `generator`, `layers` and `owners`. `generator` has exactly `path` (`generator/build-scaffold.py`) and unique attested `helpers`. Each layer has exactly `sourceLayerId`, `nativeLayerId`, `support`, ordered `models`, ordered `materials`, `ruleIds`, `maskIds`, `ownerIds` and `candidateConvention` (`world-lattice-v1`). `support` is the closed Surface binding. A model row has exactly `sourceArchetypeId`, `nativeAssetId`. A material row has exactly `sourceMaterialId`, `nativeResourceId`, `kind` (Ordinary or Water), and the source catalog must independently agree. Settings/bytes come from source itself, not redundant expected copies.

Each owner has exactly `sourceObjectId` (positive decimal int64 string), `sourcePlane`, `sourceArchetypeId`, `nativeOwner` (closed World MapPrefabReference), `targets`, `ruleIds` and `maskIds`. Each target has exactly `nativeLayerId` and `support`. IDs and source inventory must correspond bijectively, including source overrides and transformed footprint centres. Rules are checked against the original source footprint/collision/tags, not against a generator-authored expected footprint. A higher-plane roof explicitly lists only the source layers it cleared. Rule/mask IDs are exhaustive effective references, no ignored rows. If a target depends on R5 room/building correspondence, buildingManifestPath is required and its complete released building contract is validated against the same extraction. Otherwise it is null.

Unknown rules, extra/missing/duplicate correspondences, non-Surface private import bindings and incomplete closure refuse. Secure boundary verifies provenance SHA before parsing, snapshots/hashes all attested bytes, verifies before and after source loaders and reloads native inputs from captured bytes. Reuse the released strict path, duplicate-member, aggregate digest and report rules. The comparator invokes Task 6 Query over each source region's declared half-open area, internally partitioned by chunk, so even off-raster zero-density candidates are checked. It compares canonical rejection stages from the real legacy detailed sampler and native trace, without substituting expected records generated by the native adapter.

The comparison asserts the following, chunk by chunk, on two independently loaded heads for each storage form. Settings and every raster byte compare before sample work. Every in-area candidate address has exactly one row, including rejection, with exact eligibility/material/rejection category, seven channel values, model choice, support owner, stable address and versioned key. Accepted position compares within 0.00001 m per component. Orientation compares the full surface-aligned rotation angle, `2*acos(min(1,abs(dot(qSource,qNative))))`, within 0.000001 radians. Normalize extracted rotations and evaluate norm/dot/acos in binary64 only for comparison, never storage. Scale and thinning rank compare exact binary32 bits, using the trace's scale channel rather than re-extracting scale from a rounded matrix. Model/material references and their correspondences compare exactly. Same-process legacy-wrapper regression matrices remain bit-exact. Finite checks reject NaN/infinity before any tolerance arithmetic. Root offset participates in the position check. No matrix-component tolerance is invented as a substitute for angle comparison.

Category counts include layers, bytes, chunks, scanned/owned candidates, accepted, each rejection stage, per-model choices, support owners, storage/head matches and each comparison failure. Keep details/counts only in the private report. Stream successes as aggregates, retain detailed mismatches, and never store the complete million-row corpus to log or memory. One-byte and one-decision synthetic sensitivity controls must fail. The current source is refrozen at execution, historical dimensions are public invented shape regressions only. Comparison is neither R11 importer acceptance nor game adoption.

- [ ] **Run green:** red filters as green, `wa_test t10-regress "$ORACLE" "FullyQualifiedName~PrivateOracleGuardTests|FullyQualifiedName~ShippedBuildingInputGuardTests|FullyQualifiedName~ShippedMarkerInputGuardTests"`, `wa_sweep t10-final`, and `wa_run t10-oracle-format dotnet format "$ORACLE" --verify-no-changes --no-restore`. Expect nonzero guard tests and zero failures. Actual private fact waits for the final reconciled candidate below.
- [ ] **Format/guards/commit:** explicit engine Files paths through `wa_finish t10`, plus the separately verified harness format, then `test(mapdoc): compare foliage through the attested private oracle`. Controller separately verifies the retained private generator commit and closure delivery.

### Task 11: R8 padded immutable handoff and consumer contract (2 to 5 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageRenderHandoff.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageHandoffTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapFoliageHandoffBoundaryTests.cs`.
- Modify: `KhaozEngine.MapDoc/README.md`, `KhaozEngine.MapDoc.Physics/README.md`, `KhaozEngine.MapEditor/README.md`, `KhaozEngine.MapEdit.Tool/README.md`, `KhaozEngine.Terrain/README.md`, root `README.md`, `docs/USING-KHAOZENGINE.md`, `docs/DEPENDENCY-SEAMS.md`, `docs/INDEX.md`. Release declarations are chosen concretely after main reconciliation below, not an unreviewed glob.

**Interfaces:**
- Consumes Task 6 MapFoliageSampleBatch, released `MapResolvedAsset.RenderBounds: MapLocalBounds`, `SourceUnitsToMetres: float`, `MaterialResourceIds: IReadOnlyList<string>` at Assets/MapResolvedAsset.cs:15,18-19, and Task 7 FoliageChunks.
- Produces `MapFoliageModelExtent(string AssetId, MapLocalBounds BoundsInSourceUnits, float SourceUnitsToMetres, float MotionPaddingMetres)`, `MapFoliageRenderBatch(MapFoliageSampleBatch Samples, MapFoliageStatus Status, MapBox3? Bounds, string DependencyDigest)` and `MapFoliageRenderHandoff.Create(MapFoliageSampleBatch batch, IReadOnlyList<MapFoliageModelExtent> extents) -> MapFoliageRenderBatch`. Lists and nested records are detached/immutable. No renderer/GPU type appears in the API.

- [ ] **Write failing tests.** `HandoffBounds_IncludeSourceUnitsExactPoseAndMotionPadding`, `HandoffMissingExtentOrPadding_RefusesInsteadOfCulling`, `HandoffCosmeticExtents_NeverChangeNavOrStatics`, `HandoffRetainsKeysMatricesMaterialsRankAndCompleteness`.

```csharp
Assert.Equal(batch.Samples, handoff.Samples.Samples);
Assert.Equal(batch.DependencyDigest, unchangedDistributionDigest);
Assert.NotEqual(unpaddedHandoffDigest, paddedHandoffDigest);
Assert.Equal(expectedTransformedAndPaddedBounds, handoff.Bounds);
Assert.Equal(MapFoliageStatus.Invalid, missingExtent.Status);
Assert.Null(missingExtent.Bounds);
Assert.Equal(beforeNavCapture, afterPaddingNavCapture);
Assert.Equal(beforeStatics, afterPaddingStatics);
```

Use a non-cubic invented mesh box, source units 0.01, instance scale 1.2, yaw 0.371 and motion padding 0.35 m. Transform all eight box corners with source units applied once and the exact CPU matrix, then expand XYZ by 0.35. Include negative/root-offset pose, multiple models, empty complete batch (null bounds), missing and duplicate extents, nonfinite/negative padding and refused/incomplete source batch. Finite zero padding is valid only when explicitly supplied. Dependency digest includes extents and padding, separate from distribution identity.

- [ ] **Run red:** `wa_test t11-red "$MAPDOC" "FullyQualifiedName~MapFoliageHandoffTests"`, `wa_test t11-red-boundary "$MP" "FullyQualifiedName~MapFoliageHandoffBoundaryTests"`.
- [ ] **Implement** the immutable handoff and conservative bounds using the released asset units contract, no resampling or quaternion-to-matrix rebuild. Document exact R8 requirements: styling remains caller policy, preserve live blade/shrub profiles and shadow flags, never change CPU sample selection, use dependency digest plus generation to reject stale work, keep one owned batch with all intersected residency memberships, and release only after last membership. Actual residency/upload, viewport drawing, captures and interior-cut adapter #1474 remain R8, interactive painting stays R9, complete verb parity stays R10, import/actual source acceptance stays R11/G2 and appearance stays R8/G3.
- [ ] **Update** living APIs, five verbs/101 inventory, payload/format/storage/limits/refusal semantics, compatibility policy, cosmetic boundary, exact private harness edge and deferred handoff rows. Sweep all Markdown for changed API names and old foliage/format/tool-count claims. Do not rewrite executed plans as current reference docs. Controller reconciles C7, J7.x, R10 verb rows and PROGRAM in the private program worktree separately.
- [ ] **Run green:** both red filters as green, then `wa_sweep t11-final` after review and `wa_finish t11` over the exact code/doc paths. Run doc-version guard on the final release declarations again at candidate gate, not by hiding stale declarations.
- [ ] **Commit** explicit Files paths with `feat(mapdoc): publish the padded R8 foliage handoff and consumer contract`.

## Self-review and execution handoff

- [ ] Map every OA39 decision to tasks. Payload/format/limits Task 1, storage/copies/both identity paths Task 2, unchanged channels Task 3, support/normals/materials Task 4, rules/masks/ownership Task 5, sample/digest/differential Task 6, commands/effects/callback Task 7, five verbs/wire Task 8, OA9/public/platform proofs Task 9, private closure/comparison/fallback Task 10, R8 handoff/docs Task 11 and minor release below.
- [ ] Verify every released path, range and consumed signature at f1cd6656b and every new type/property against its producing Interfaces block. Reconcile accepted interface changes into all downstream briefs. Test variables are actual fixture/product outputs, never answer-producing fakes.
- [ ] Check every byte/world address, positive-Z row, last-row clamp, chunk boundary/padding, sparse allocation refusal, storage memberships/partial saves, copy paths and canonical identities. Absent data and unavailable advertised data are separate outcomes.
- [ ] Check all five Review Focus regressions and three exact OA9 names. Preserve NoDraw/Blocked legacy behavior and the exact central-difference/global-corner rule. Compiler physical behavior and annotation digest pins remain unchanged.
- [ ] Check rules operate on effective placements once, typed support ownership prevents cross-floor leakage, ownerSupports has no clearing geometry, masks cannot dangle, prefab payload-1 bytes/digests stay fixed, and the 1 m interaction envelope is not queried as foliage geometry.
- [ ] Check policy differential joins stable addresses, reports changed accepted/rejected decisions, binds before/proposal identity and cannot publish a partial comparison. Per-chunk preparation and counted budgets replace per-candidate full support queries.
- [ ] Check command/undo/refusal state, late-callback final snapshot/effects/redo, old/new widened dependent chunk effects, no cosmetic union into world/nav bounds, per-call 60 s non-parallel wire tests, exact inventory 101 and detached reads.
- [ ] Check test placement, source-link boundaries, 800-line cap, no baselined growth, zero warnings, actual nonzero Release filters/TRX and one three-project sweep after each task. No loops, load tests, public private-input selection or agent window boot.
- [ ] Check full private closure before extraction, retained generator and independent rule mapping, fixed tolerances/sensitivity controls, four fresh storage/head results, secure report lifecycle and rule adjudication before attestation. No private inventory, digest or source rows in this plan.
- [ ] Check R8 contract includes exact matrices/poses, references/rank, extents/padding, completeness, narrow digest and explicit styling handoff. Public CPU proof is not a visual or windowed acceptance claim.
- [ ] Check proportion and determinacy, no placeholder code steps, mismatched types, invented released API or reference to an unproduced helper. Record remaining objections/ruling changes in the controller ledger before execution. The plan writer stops after its one docs commit.

Recommended execution is serial subagent-driven work after owner plan approval, one scoped implementer and fresh reviewer per task, followed by whole-branch review. Each task can be rejected independently. Controller independently inspects diffs, commit containment, red/green exits and TRX, and pushes accepted work before moving on. No worker merges, tags, packs, pushes or cleans worktrees unless its later explicit brief assigns that work. OA39 alone does not authorize implementation, release or game adoption.

## Candidate gates, private acceptance and integration

1. Complete whole-branch review against the approved plan/gate and recorded rulings. Resolve task findings with focused tests and targeted re-review. Report adjacent work to the controller rather than changing scope. Reforecast remaining R8 to R11/G rounds from accepted task evidence.
2. Controller fetches and merges current main into the task branch, resolves there, and rechecks shared APIs, format ownership and the interior-cut lane. If format 9 was taken, stop for a recorded plan amendment, never silently renumber. Main must be contained before final verification.
3. Re-read current main/origin/main, KhaozEngineVersion, newest tags and CHANGELOG. R7 is a minor. Ride an appropriate staged minor, reconciling 21.6.0 with interior cut. If only a patch is staged, select the next free minor and carry its changes forward. Commit Directory.Build.props, CHANGELOG and every declaration selected by scripts/check-doc-versions.sh together, explicit paths. State format-9 identity movement, absent-foliage behavior, payload 1, compatibility policy, five verbs, sparse limits and cosmetic/nav guarantees. No game pin changes.
4. Run the following once, sequentially, on the reconciled code/version/docs candidate. Keep exact SHA, logs, TRX counts, warnings and every exit. The harness remains outside the public suite and is never tested unfiltered.

```bash
wa_run gate-build --heavy dotnet build KhaozEngine.slnx -c Release
wa_run gate-suite --heavy dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket" --logger "trx;LogFileName=gate-suite.trx" --results-directory "$wa_r7_log_dir"
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

All commands exit 0, builds have zero warnings/errors, tests have zero failures with nonzero matched counts. If main-only format drift persists under R-GATE-1/#1485, record the actual unrelated drift and verify the explicit round-owned C# list instead of changing unrelated files. The harness has its separate verified project. Controller pushes the verified candidate and requires exact-commit CI and all three foliage-conformance legs.

5. Deliver and verify Task 10's complete private input closure and retained generator before running acceptance. Assign `wa_r7_verified_source`, `wa_r7_private_provenance`, `wa_r7_provenance_sha256`, `wa_r7_private_report` from that private attestation. Parent is an existing 0700 directory outside git, report path absent. Run this one fact once against the exact candidate, through the released boundary, reading exactly these variables in order.

```bash
wa_run gate-private --heavy env KHAOZ_R2_SHIPPED_SOURCE="$wa_r7_verified_source" KHAOZ_R2_ORACLE_PROVENANCE="$wa_r7_private_provenance" KHAOZ_R2_ORACLE_PROVENANCE_SHA256="$wa_r7_provenance_sha256" KHAOZ_R2_ORACLE_REPORT="$wa_r7_private_report" dotnet test "$ORACLE" -c Release --no-build --filter "FullyQualifiedName~ShippedFoliage_PreservesEveryByteCandidatePoseAndDecision" --logger "trx;LogFileName=gate-private.trx" --results-directory "$wa_r7_log_dir"
```

PrivateOracleEntry verifies provenance SHA before parsing, creates the report exclusively at 0600, sanitizes all exceptions/final output and owns report completion. Body uses report.Check/Record only, no Assert on private values, console, ITestOutputHelper or private InlineData. Missing/unsafe/stale input is failure. Controller privately records source/engine/generator commits, complete attestation, actual categories/counts, fixed tolerances, report digest and all discrepancies in `R7-FOLIAGE-ORACLE.md`. Public evidence is pass/fail and that private record reference only. This is comparison scaffolding, not the R11 importer or named game acceptance.

6. A mismatch fails the gate. Fix the proper rule, manifest or engine owner, never tolerance/count expectations to make green. A changed candidate or extraction invalidates matching evidence. Rerun affected ordinary gates and the private comparison once into a fresh absent report path (R-ORACLE-1). No repetition loop, timing experiment or stress run. If main advances in relevant files, reconcile on the task branch and refresh affected evidence before integration.
7. Engine controller checks release-lock status, fast-forwards main and pushes main. Only after origin/main contains the clean integrated commit, run `wa_run release-pack --heavy sh scripts/pack-local-feed.sh` from main under the shared feed guards. No bare pack or overwritten released bytes.
8. Release through a KhaozEngine-rooted thread under OA25 using release-khaozengine and current engine release rules. Fresh version/tag/lock checks, guarded tag script, remote annotated tag, exact-commit CI, publication, complete package proof and fresh required tool downloads form the release report. The game-rooted controller never tags the engine. Plan/gate approval never implies game adoption or R11 acceptance.
9. Controller updates private PROGRAM/DECISIONS/R7 ledger with approved rulings, all accepted task/candidate/integration commits, private record pointer, verified release and revised forecast. R8 takes the immutable sample/bounds contract, R9 takes commands, R10 takes the verbs and R11/G2 refreezes actual accepted source. Worker cleanup waits for verified integration.

## Outcome and proof ledger

Filled during approved execution.

| Item | Evidence |
| --- | --- |
| Independent plan reviews and owner approval | |
| Adjudicated R7-P rulings and amendments | |
| Task commits, red/green TRX and three-project sweeps | |
| Payload/format, sparse storage, every copy path and both identities | |
| Old channel bits, compatibility normals/materials and complete decision trace | |
| Transformed rules/masks, owner lifecycle and 1 m envelope separation | |
| Atomic commands, late callback, dependent effects and 101-tool wire parity | |
| Three OA9 proofs, both storage forms and four independent heads | |
| Linux, macOS and Windows conformance at exact candidate | |
| Conditional Task 10F evidence or proof seventh reference is inert | |
| Retained private generator and complete attested input closure | |
| Private exhaustive foliage comparison record | |
| R8 immutable poses/references/rank/digest/padded bounds/completeness | |
| Whole-branch review, main reconciliation and candidate verification | |
| Integration, package proof and engine-rooted release | |
| Remaining engine/game program forecast | |
