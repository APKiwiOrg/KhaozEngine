# World authoring R7 native foliage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. This plan is for independent review and owner approval before implementation.

**Goal:** Preserve authored density and legacy blade decisions in native MapDoc, with sparse storage, explicit support ownership and shared undoable foliage commands.

**Architecture:** Format 9 adds root foliage payload 1 independently of analytic scatter, companion layers and explicit scenery. One budgeted immutable generation context captures a fully loaded snapshot and is reused for scoped chunk preparation. Render batches are owned by world rectangles, differentials by stable candidate-lattice blocks. Region packs preserve logical chunk identity, and a public MapDoc finalizer supplies the same dependent effects to pure consumers, MapEditor and MCP.

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
- R7-R1. R7 generation requires a fully loaded snapshot. Capture its effective resources once under construction budgets, reuse the context across Generate/Query calls and never serialize the full foliage payload per chunk. Partial generation refuses with PartialResidency. R8 owns a future windowed context constructor.
- R7-R3 to R7-R5. Differential blocks own stable (gx, gz) addresses independently of generation rectangles. Canonical sample support identity is the selected support surface for both binding kinds, occupied space is separate. Compatibility roof eligibility uses an explicit source-plane ordering witness, never geometric Y.
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

1. A seed, spacing or density-origin edit moves a retained candidate across generation chunks. Task 7 adds `Compare_SeedSpacingAndOriginCrossingsHaveOneChangedRow`, exercising real Compare and exactly one Changed row, never a Removed/Added pair for the retained address.
2. A tiny chunk request repeatedly expands unrelated owners/paint or Query retains many individually legal batches. Task 4 adds `ContextAcquisition_UnrelatedGrowthDoesNotGrowChunkWork` and `PartialSnapshot_RefusesBeforeCapture`, Task 7 adds full `GenerationContext_UnrelatedGrowthDoesNotGrowChunkWork` and `Query_AggregateRetentionRefusesIndividuallyLegalChunks`.
3. A valid stored pack is rejected as inline data or reported as orphan, while a corrupt pack is missed. Task 2 adds `InlineAndStoredSchemas_AgreeWithTheirOwnDecoders` and `VerifyTiled_RegistersAndChecksEveryFoliagePack`, Task 10 adds real `map_validate` controls.
4. A Space binding labels a floor by its room ID, or a roof uses height instead of source order. Tasks 4 to 6 add `SurfaceAndSpaceBindings_NormalizeToOneSupportSurface` and `RoofOrdering_IsOrdinalRatherThanGeometric`, including nested rooms and identical Surface/Record text IDs.
5. Pure callers, editor callbacks and wire replies disagree about affected chunks or lose density/pose fields. Task 9 adds `DocumentFinalizer_PureEditorWireEffectsAgree` and `LateCallbackFoliage_PublishesOneSnapshotEffectsAndRedo`, Task 10 adds `FoliageWire_ContainsEveryByteQuaternionAndMatrixComponent`. Task 3 separately freezes the actual legacy sampler before diagnostic refactoring.

## Controller rulings and surviving proposals

R7-P numbering remains stable. R7-R1 to R7-R6 are binding controller adjudications of review 1, not new owner choices. The table states each proposal's current disposition. The revised plan still needs independent review and owner approval before code.

| ID | Proposed choice | Cost if wrong |
| --- | --- | --- |
| R7-P1 | Retained with M1 clarification. 32 by 32 byte chunks, 1024 bytes each. Width/Depth at most 1,048,576, 16,384 stored chunks per layer, 4096 region memberships per chunk, 256 layers and 67,108,864 decoded density bytes per document. Per layer: 256 models, 65,535 materials, 4096 rules, 4096 masks, 65,536 owner correspondences. Foliage JSON at most 134,217,728 UTF-8 bytes, base64 chunk token at most 1368 characters. Checked arithmetic, zero padding and legal explicit all-zero chunks retain authored identity | At 2 m cells over 64 km there are roughly one million logical chunks. The 16,384 stored chunks are a sparse subset, not full-area coverage. Different ceilings need a payload decision |
| R7-P2 | Replaced by R7-R2. Optional root, payload 1, inline monolithic bytes and one verified content-addressed region pack per (layer, owner region). Logical chunk digests and identity are independent of packing | Missing partial-state bookkeeping loses unloaded chunks. Region packs add one indirection |
| R7-P3 | Amended by R7-R1/R4 and I3/I4. legacy-tile/1 keeps explicit boundary/material metadata, resolver (1, 2), full-request shared support selection and additive NoDraw triangles. Freeze released TileFoliageSurface bits before changing it | Wrong request semantics or boundary normals change support/poses despite wrapper equality |
| R7-P4 | Amended by R7-R4/R5. Clearing uses canonical selected surface ownership and exceptional typed provenance correspondences, with no geometry in those correspondences. Roof eligibility uses a source ordinal witness, never geometric Y. Solid/roof clearing stays cell-based, DoorClearance scales once and EdgeFade stays world metres | Binding-text comparison or geometric roof tests clear the wrong floor. Later geometry policies need their own differential |
| R7-P5 | Amended by R7-R3. Stable-address comparison blocks are 64 by 64 lattice addresses, independent of both proposals' jittered points and world chunks. Generation retains world-rectangle ownership. Generate, Query and Compare observers are provisional until Complete | A chunk-owned join turns retained addresses into spurious remove/add pairs |
| R7-P6 | Amended by R7-R1 and I8. Separately budget context construction. Keep per-chunk limits, add Query totals of 262,144 samples and 67,108,864 retained bytes, charged before retention. Differential permits 64,000,000 candidates, 4,000,000 changes and 134,217,728 reconciliation bytes per lattice block | A small valid payload could otherwise retain gigabytes. Refusal discards all usable output, byte charges are explicit conservative accounting, not measured GC allocation |
| R7-P7 | Amended by I7. Foliage flag 256 and separate cosmetic chunk bounds remain. Public MapDocumentEditFinalizer in MapDoc is mandatory for complete document transactions and shared by pure paths and MapEditor | Editor-only effects leave headless consumers stale. Whole-root dependencies defeat narrow invalidation |
| R7-P8 | Amended by M2 and R7-R3. ToEven, bytes 0 to 255, 64 chunks/65,536 addresses, before/proposal-bound differential token. Hardness 1 includes the exact radius at full strength, hardness below 1 has zero weight there | A different boundary or differential join changes authored bytes or hides sample changes |
| R7-P9 | Scale and thinning rank compare exact binary32 bits, including across platform fixtures. Position/orientation use the gate tolerances. Synthetic pins are fixed source literals captured from the released generator, independently verified by channel arithmetic, never regenerated to bless a mismatch | Cross-platform scale/rank drift blocks acceptance and requires a rule ruling, not tolerance expansion |
| R7-P10 | R8 receives source-unit-adjusted model bounds transformed by the exact CPU matrix and an explicit caller motion padding in metres. Missing extents or padding is an incomplete/refused handoff, never zero padding by default. Generation remains independent of styling | Callers must provide a complete bounds policy. Incorrect padding causes residency/culling loss |
| R7-P11 | Amended by M4. Conditional Task 12F has a fresh CPU-only probe executable with an exact dependency guard. Render.Tests keeps frozen-bit/wrapper parity, not the clean-host claim | Extraction can exhaust the allowance and needs reforecast. No oracle waiver |
| R7-P12 | Amended by R7-R6. Thirteen serial tasks, 49 to 82 engineer-days, 70 to 110 elapsed agent hours with 90 to 110 the scheduling allowance | Exceeds the original 35 to 60 baseline. Capacity, review and private discrepancies can extend it |
| R7-R1 | Binding, amends P3/P6. The generation context is captured once per immutable document snapshot from the effective resources and reused across chunk calls. Its construction is counted and budgeted before allocation, and it never re-serializes the whole foliage payload per chunk. R7 generation requires a fully loaded document, as MapPrefabResolver.Expand does today, and a partially resident document refuses with a typed status. Windowed and streamed generation over partial residency is an R8 input | R8 adds a windowed context constructor beside the full one |
| R7-R2 | Binding, replaces P2. Tiled storage packs a layer's chunks into one content-addressed pack per (layer, storage region), bounded by the region's chunk count, using the surface store's packing and verification pattern. Logical chunk digests stay independent of packing and are what identity hashes | One more indirection when reading a single chunk |
| R7-R3 | Binding, amends P5/P8. The policy and version differential is owned by stable lattice address, not by generation chunk. Comparison blocks partition the candidate lattice (gx, gz) independently of both proposals, and each block evaluates its lattice addresses wherever their jittered points land, with bounded reconciliation. Generation ownership by world rectangle stays for render batches | Comparison enumerates a halo of lattice addresses per block |
| R7-R4 | Binding, amends P3/P4. The canonical sample support identity is the selected support surface owner for both binding kinds. The occupied space is recorded separately where a rule or the trace needs it. Clearance rules resolve their owners' bindings to that identity and never compare binding text IDs | A later geometric policy needing space-level ownership adds a second field |
| R7-R5 | Binding, amends P4. The compatibility roof rule is ordinal. The importer admits a roof rule only from a source plane higher than the layer's source plane and keeps an ordering witness on the rule. Support ownership, not geometric height, keeps it off caves | A later geometric policy replaces the witness with a height test, with its own differential |
| R7-R6 | Binding, amends P12. The writer may split Tasks 4, 7 and 10 where they combine distinct contracts, up to thirteen tasks, and must reforecast. The 35 to 60 engineer-day baseline stands unless the added storage, comparison and context work needs more, in which case the plan says so plainly for the owner | Added contracts increase this plan to 49 to 82 engineer-days |

## File structure and estimate

All new names are task outputs. Existing paths/ranges refer to `f1cd6656b`, follow the named symbol if a range ends within its declaration. A filename following a full directory in Files shares that directory. Test namespaces stay `KhaozEngine.Tests.*`.

| Task | Independently reviewed deliverable and new homes | Released integration homes | Engineer-days |
| --- | --- | --- | --- |
| 1 | Closed foliage DTO/codec/validation/migration in MapDoc/Foliage | Document partial/order, schema and codec | 3 to 5 |
| 2 | Region packs, derived storage schema, verification and identities | Tiled writer/index/verifier, canonical root, snapshots/history | 5 to 8 |
| 3 | Candidate evaluator/trace and frozen released sampler bits | Terrain distributor, Render.Tests/Terrain | 3 to 5 |
| 4 | Budgeted immutable context and full-request prepared support | Prefab expansion inputs, effective resources, support selection | 5 to 8 |
| 5 | Compatibility interpolation, normals and visible paint | Surface compiler and native scoped paint | 4 to 7 |
| 6 | Canonical-owner clearance and ordinal roof rules | Prefab references/lifecycle, effective ownership | 4 to 7 |
| 7 | Batches, stable-lattice differential and aggregate retention | Context and candidate evaluator | 4 to 7 |
| 8 | Pure layer/raster/brush edits | MapDoc/Editing/MapFoliageEdits.cs and MapFoliageBrush.cs | 3 to 5 |
| 9 | Public document finalization, commands and publication effects | Pure dependent-edit paths, native transactions/history | 5 to 8 |
| 10 | Five verbs, detached discovery and complete wire DTOs | MCP bootstrap, pinned 101-tool inventory and README | 3 to 5 |
| 11 | OA9, independent heads, schema and platform acceptance | Area tests, architecture guard, hosted conformance | 3 to 5 |
| 12 | Real legacy diagnostics, private harness and conditional 12F | Oracle harness/reference guard, optional CPU-only probe | 5 to 8 |
| 13 | R8 padded immutable handoff and living consumer contract | Package/API/dependency docs and release preparation | 2 to 4 |
| | Total including reviews, fixes, candidate and integration | Serial dependency order 1 through 13 | 49 to 82 |

**This exceeds the original 35 to 60 engineer-day baseline.** Review 1 exposed missing storage schema/verification, immutable-context construction, full support-request parity, stable-lattice comparison, public effects and aggregate retention contracts. Tasks 4 and 9 are new independent gates split from the original combined tasks. The 49 to 82 range includes their implementation, per-task reviews, fixes and integration, not a claim that all 15 findings are trivial edits.

R4 took about 9 h from approval to tag, R5 about 59 h and R6 about 56 h against 22 to 48 h. Reforecast R7 at **70 to 110 elapsed agent hours** after reviewed-plan approval, with 90 to 110 the scheduling allowance. Thirteen gates, complete private comparison and the now explicit context/storage contracts justify the increase. Plan review, owner responses, subscription capacity and input delivery extend calendar time. Reforecast after Tasks 4 and 9 and after private comparison. Conditional 12F consumes Task 12's allowance and triggers a new estimate if it cannot fit.

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

Task 5 adds `SlopedBorder()`, `FlagsAndCuts()`, `UnavailableHalo()` returning FoliageFixture. Task 6 adds `Clearance(string variant)`. Task 11 adds `Stacked()`, wrapping `SupportFixtures.EightStacked()` with its released assets, and `Cave()`, wrapping `PrefabFixtures.Cave("stacked")`, retaining their real topology and ownership. Tasks adapt density/placement decorations around those fixtures, never replace their cave geometry with a toy floor. Task 9 creates editor-only `FoliageEditorFixtures.Session(FoliageFixture) -> MapEditSession` and `State(MapEditSession) -> string`, a canonical snapshot of document, closure, identity, dirty state, cursor/depth/labels and events. Assertions below use actual produced values, not hard-coded product-result helpers.

Task availability is part of the contract. Task 1 tests monolithic reload only, Task 2 enables/tests the tiled fixture branch. Tasks 5/6 test prepared points/clearance directly, not the future Task 7 distributor. Task 4 measures its own scoped acquisition, Task 7 repeats the full generation regression. Task 8 tests pure edits, editor command/history proofs belong to Task 9. No task's red or green depends on an API owned by a later task.

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
- Task 1 declares rule/mask/owner DTO shapes consumed by Task 6. `MapFoliageRect(float MinX, float MinZ, float MaxX, float MaxZ)`, `MapFoliageClearanceKind { Solid, Door, RoofAbove }`, `MapFoliageRoofOrder(int LayerOrdinal, int RoofOrdinal)`, `MapFoliageClearanceRule(string Id, string AssetId, string? PlacementTag, MapFoliageClearanceKind Kind, MapFoliageRect Footprint, MapFoliageRoofOrder? RoofOrder)`, `MapFoliageOwnerSupport(MapPrefabReference Owner, MapSupportBinding Support)`, `MapFoliageMask(string Id, MapPrefabReference Owner, MapSupportBinding Support, MapFoliageClearanceKind Kind, MapFoliageRect Footprint, MapFoliageRoofOrder? RoofOrder)`. RoofAbove requires a nonnegative ordinal witness, other kinds forbid it. Policy activates only RoofOrdinal > LayerOrdinal, never height. Owner refs are World scope, only Placement, Surface, Record or Marker as appropriate, never untyped strings.
- Produces `MapFoliageValidation.Validate(MapFoliageSet set) -> IReadOnlyList<string>`, `ValidateBound(MapDocument document, MapAssetClosure assets) -> IReadOnlyList<string>`, internal `MapFoliageJson.Read(JsonElement) -> MapFoliageSet`, `Write(Utf8JsonWriter, MapFoliageSet) -> void`, `MapFoliageMigration.Upgrade(JsonObject source) -> JsonObject`. Bound validation checks metadata/resource roles without calling MapPrefabResolver recursively. Task 6 validates effective owners after expansion. Only legacy-tile/1 is supported. Task 7 tests future version joins as data inputs to the generic differential core, without weakening document validation.

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

Cover unknown/duplicate/case-variant/null fields, payload 2, future format 10, empty/duplicate IDs, zero/negative/nonfinite sizes, scales/weights, weight-sum overflow, unknown material/model/support, byte length 1023/1025, base64 errors, invalid numeric bytes -1/256/fraction, duplicate/out-of-domain chunk keys, nonzero padding, wrong row direction and every P1 ceiling plus one. Surface binding forbids foreign members. Space requires finite reference/search with released bounds. Rules/masks reject future kinds, invalid rectangles, local or wrong-kind owners. Schema/runtime agree, with evaluator parity completed in Task 11. The migration accepts 8 or 9, deep-clones and changes only formatVersion, preserving an unreadable sentinel section without sampling. Loading that sentinel still refuses as a positive control.

- [ ] **Run red:** `wa_test t1-red "$MAPDOC" "FullyQualifiedName~MapFoliagePayloadTests"`. Expect missing task-owned APIs or format/payload assertions failing. Capture any released synthetic byte pins before product changes.
- [ ] **Implement** the Interfaces in their named files. Register migration 8 after the released 7 step. Set CurrentFormatVersion and schema const to 9. Closed decoder validates P1's JSON/token/count limits before nested materialization or base64 allocation. Non-null foliage requires resolver (1, 2) and AuthoredBindingsV2, with explicit adoption guidance otherwise. Domain is sample indices [0, Width) by [0, Depth), with endpoint-clamped bilinear interpolation later. No whole-domain array. Allowed materials are a sorted distinct nonzero set, model order remains authored. Preserve prefab payload and existing placement binding codecs. The 1024-byte chunk is a bounded edit/read unit with 64 m sample-address span at 2 m cells. Task 2 region packs, not individual chunk files, address file-count costs.
- [ ] **Run green:** Step 2 filter as `t1-green`, then `wa_test t1-regress "$MAPDOC" "FullyQualifiedName~FormatAdvanceTests|FullyQualifiedName~MapPrefabFormatTests|FullyQualifiedName~MapMarkerFormatTests"` and `wa_sweep t1-final` after review.
- [ ] **Format, guard and commit** the explicit Files list, no other paths, using `wa_finish t1 "${wa_r7_t1_paths[@]}" && git add -- "${wa_r7_t1_paths[@]}" && git commit --only -m "feat(mapdoc): add foliage payload 1 and pure format 9" -- "${wa_r7_t1_paths[@]}"`. Set `wa_r7_t1_paths` to each full path enumerated above before running it. This explicit-array convention applies to every task below.

### Task 2: Region packs, storage schemas, verification and both identities (5 to 8 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageStorage.cs`, `MapFoliageRegionPack.cs`, `MapFoliageStorageIndex.cs`, `MapFoliageProjection.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageStorageTests.cs`, `MapFoliageIdentityTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeFoliageSnapshotTests.cs`, `FoliageStorageSchemaTests.cs`.
- Modify: `KhaozEngine.MapDoc/MapDocumentSchema.cs:43-75`, `MapDocumentMembers.cs:16-20,154-158`, `MapTiledFile.cs:26-109,156-200,205-235`, `MapTiledFile.Save.cs:24-137,177-205,237-254`, `MapTileIndex.cs:28-78`, `MapCanonical.cs:53-108`, `Storage/MapSurfaceRootProjection.cs:13-25`, `Storage/MapStoredSurfaceSource.Content.cs:19-78`, `Identity/MapAuthoredIdentityV2.cs:17-49`, `Editing/MapNativeWriteSet.cs:10-26`, `KhaozEngine.MapEditor/NativeDocumentSnapshot.cs:13-94,117-145`, `KhaozEngine.MapEdit.Tool/MapEditSession.NativeHistory.cs:145-169`, Task 1 foliage codec/schema definitions.
- Test/modify: `KhaozEngine.MapDoc.Physics.Tests/MapPersistedIdentityTests.cs:59-120` for independently derived format-9 identity, preserving prior-version controls. Schema evaluation stays in MapEditor.Tests, whose released references already provide the evaluator.

**Interfaces:**
- Consumes released `MapDocumentSchema.GetJson() -> string`, `GetManifestJson() -> string`, `MapDocumentFile.VerifyTiled(string directory, MapDocRegistry? registry = null) -> IReadOnlyList<string>`, `MapSurfaceSemantics.RootDigest(MapDocument) -> string`, both `MapAuthoredIdentityV2.Compute(MapDocument, MapAssetClosure, MapResolveOptions) -> string` and `Compute(MapStoredSurfaceSource, MapAssetClosure, MapResolveOptions) -> string`. The verification pattern is `MapSurfaceTiledStore.Verify(string root, MapDocument doc, MapTileIndex tiles, HashSet<string> named, List<string> report) -> void` at :284.
- Produces `MapFoliageChunkDigest(MapFoliageChunkKey Key, string Sha256)` and readonly `MapFoliageLayer.ChunkDigests` for every advertised chunk, distinct from resident Chunks. Internal `MapFoliageLayer.FromStorage(MapFoliageLayer metadata, IReadOnlyList<MapFoliageChunkDigest> advertised, IReadOnlyList<MapFoliageChunk> resident) -> MapFoliageLayer` preserves both through clones. DensityAt throws on advertised nonresident data, only absent directory keys read as zero.
- Produces `MapFoliagePackRef(string LayerId, MapTileCoord Region, string PayloadSha256, int ChunkCount, int ByteLength)` and `MapFoliageChunkRef(string LayerId, MapFoliageChunkKey Key, MapTileCoord OwnerRegion, IReadOnlyList<MapTileCoord> Regions, string Sha256, string PackSha256, int DataOffset)`. `MapFoliageStorageIndex.IsPartial: bool` and `Read(string layerId, MapFoliageChunkKey key) -> MapFoliageChunkRead` are immutable-generation operations. `MapFoliageChunkRead(MapFoliageReadStatus Status, MapFoliageChunk? Chunk)` uses Found, Absent, Unavailable, Invalid or CapacityExceeded.
- Produces internal `MapFoliageStorage.ReadManifest(JsonElement value) -> (MapFoliageSet Set, MapFoliageStorageIndex Index)`, `Prepare(MapDocument document, string directory) -> MapFoliagePreparedStorage`, `Load(MapDocument document, string directory, MapTileRect? window) -> void`, `WriteManifest(Utf8JsonWriter writer, MapFoliageSet set, MapFoliageStorageIndex index) -> void`, `Verify(string root, MapDocument doc, MapTileIndex tiles, HashSet<string> named, List<string> report) -> void`. `MapFoliagePreparedStorage(MapFoliageStorageIndex Index, IReadOnlyList<(string RelativePath, ReadOnlyMemory<byte> Bytes)> Payloads, IReadOnlySet<string> Keep)` owns region-pack bytes until manifest publication.
- Produces internal `MapFoliageRegionPack.Encode(IReadOnlyList<MapFoliageChunk> chunks) -> byte[]`, `Decode(ReadOnlyMemory<byte> bytes) -> IReadOnlyList<MapFoliageChunk>` and `MapFoliageProjection.Digest(MapFoliageSet?) -> string`, `Write(Utf8JsonWriter, MapFoliageSet?) -> void`, scheme `kemap/foliage-authored/1`. Adds init-only MapTileIndex.Foliage, includes it in IsPartial, and MapNativeWriteSet.Foliage. Preserve the three-argument WriteNativeGlobals entry with an additive fourth `bool includeFoliage` overload.

- [ ] **Write failing tests:** `ChunkedMonolithicAndTiled_RoundTripEveryByteAndSetting`, `RegionPack_RewriteAndPartialCarryPreserveLogicalDigests`, `AdvertisedChunkMissing_RefusesWithoutPublishingZero`, `EveryNativeCopyPath_RetainsIndependentFoliage`, `FoliageChangesBothIdentityPathsButNotAnnotationDigest`, `FormatNine_MovesNativeIdentitiesExactlyOnce`, `VerifyTiled_RegistersAndChecksEveryFoliagePack`, `InlineAndStoredSchemas_AgreeWithTheirOwnDecoders`.

```csharp
Assert.Equal(monolithicAuthored, storedAuthored);
Assert.Equal(beforeAnnotations, foliageOnlyAnnotations);
Assert.Equal(beforeLogicalChunkDigests, repackedLogicalChunkDigests);
Assert.Equal(68_108, defaultRegionPack.Length);
Assert.Empty(cleanVerifyErrors);
Assert.Contains(missingVerifyErrors, e => e.Contains("foliage", StringComparison.Ordinal));
Assert.False(inlineSchemaAcceptsStored);
Assert.False(manifestSchemaAcceptsInline);
Assert.True(inlineSchemaAcceptsInline && manifestSchemaAcceptsStored);
Assert.Equal(beforeBytes, bytesAfterFailedSave);
```

Exercise all GlobalsOnly/canonical/root/snapshot/named-write/whole-publication/local-history/SemanticHash paths and both identity overloads. Change every field/rule/byte independently. Verify clean, missing, corrupt, orphan and `.tmp` packs through public VerifyTiled. Check wrong lengths, offsets, count, key ordering, duplicate keys and chunk versus pack digests. Test schema evaluators against actual GetJson/GetManifestJson, both positive forms, both cross-form refusals and unknown/null/duplicate members. Native monolithic LoadText refuses stored refs, tiled ReadManifest refuses inline density. Task 10 adds the same five storage cases through real map_validate wire calls.

- [ ] **Run red:** `wa_test t2-red "$MAPDOC" "FullyQualifiedName~MapFoliageStorageTests|FullyQualifiedName~MapFoliageIdentityTests"`, `wa_test t2-red-editor "$EDITOR" "FullyQualifiedName~NativeFoliageSnapshotTests|FullyQualifiedName~FoliageStorageSchemaTests"`, `wa_test t2-red-pin "$MP" "FullyQualifiedName~MapPersistedIdentityTests.StackedCaveBuildHash_IsPinned"`.
- [ ] **Implement closed storage forms.** Define `$defs/foliageInline` and `$defs/foliageStored`, each closed. The authored root property references foliageInline only. GetManifestJson replaces that property with foliageStored, as it already replaces surfacePatches with surfaceStorage. Inline set is exactly payloadVersion/layers, with base64 chunks. Stored set is exactly payloadVersion/layers/packs, with region-pack/chunk refs. Mark foliage as a native block in MapDocumentMembers. Do not admit both forms in the monolithic schema or decoder.
- [ ] **Implement R7-R2 region packs.** Group by (layer, canonical owner region), sorted (chunk Z, X), one active content-addressed file `tiles/foliage/r_<x>_<z>/<pack-sha256>.pack`. Binary layout is ASCII `KEFOLPK1` (8 bytes), int32 little-endian count, then records of int32 X, int32 Z, 32 raw digest bytes and 1024 density bytes. DataOffset is `52 + index * 1064`, length `12 + count * 1064`. Maximum count 16,384 and bytes 17,432,588, checked before allocation. Chunk SHA hashes only the 1024 density bytes, pack SHA hashes the whole pack. Metadata binds layer/key/region, so identical packs may safely share a path.
- [ ] **Implement carry/verification/identity.** Owner region uses floor(lower sample address/TileSize), memberships include every intersected region and the terminal sample, limited to 4096 before enumeration. A partial save reads/verifies the bounded pack before rewriting it and carries unloaded records unchanged, refusing unavailable or stale data. Publish manifest last, then sweep only unreferenced files. MapTiledFile.Verify calls MapFoliageStorage.Verify before CollectStrays, registers each normalized shared pack path once and checks every advertised reference/raw digest. Orphan/temporary files remain errors. Logical root projection hashes ordered metadata plus complete (key, raw-chunk-SHA) rows, never pack paths/offsets. In-memory identity refuses incomplete advertised bytes, stored identity verifies all packs. Canonical globals emit logical foliage once, tiled manifest emits stored foliage separately. Annotation digests /1 and /2 remain unchanged.
- [ ] **Record the packing tradeoff.** At 2 m density cells and 512 m storage tiles, 64 chunks occupy a region. Individual payloads require 64 opens/files and rewrite 1024 bytes per changed chunk. One region pack requires one open/file and rewrites 68,108 bytes for a one-chunk edit, including its 12-byte header and 40-byte records. Very sparse one-chunk regions still cost one file each, and tiny density cells can reach the explicit pack ceiling. This quantitative tradeoff, not a benchmark, implements R7-R2.
- [ ] **Run green:** the three red filters as green, then `wa_sweep t2-final` after review. Derive format-9 identity from independent canonical bytes, never copy actual product output into expected pins.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t2`, then `feat(mapdoc): persist and verify foliage region packs and storage schemas`.

### Task 3: Candidate evaluator, trace and frozen released surface bits (3 to 5 engineer-days)

**Files:**
- Create: `KhaozEngine.Terrain/GroundCoverTrace.cs`, `GroundCoverCandidateEvaluator.cs`, `KhaozEngine.Render.Tests/Terrain/GroundCoverTraceTests.cs`, `GroundCoverTraceFixtures.cs`, `GroundCoverReleasedBaselineTests.cs`, `TileFoliageReleasedBaselineTests.cs`, `TileFoliageReleasedFixtures.cs` in the same test directory.
- Modify: `KhaozEngine.Terrain/GroundCoverDistribution.cs:38-100,140-184`, `KhaozEngine.Render.Tests/Terrain/GroundCoverDistributionTests.cs:1-160`. Keep the existing Generate signature and every old channel bit.

**Interfaces:**
- Consumes released `GroundCoverDistribution.Generate(RectArea area, GroundCoverSettings settings, Func<float, float, GroundCoverSample> surface) -> IReadOnlyList<GroundCoverInstance>`, MaxCandidateCount = 4_000_000, `GroundCoverSample(float Height, Vector3 Normal, float Density)` and `GroundCoverInstance(string ModelId, Vector3 Position, Matrix4x4 Transform, float ThinningRank)`.
- Produces `GroundCoverCandidate(int X, int Z, float WorldX, float WorldZ, float JitterX, float JitterZ, float DensityPick, float ModelPick, float ScalePick, float YawPick, float Rank)`, `GroundCoverSurfaceInput(GroundCoverSample? Sample)` where null is a known outside-domain rejection, `GroundCoverDecisionKind { OutsideArea, OutsideSurface, ZeroDensity, DensityRejected, Accepted }`, and `GroundCoverDecision(GroundCoverCandidate Candidate, GroundCoverDecisionKind Kind, GroundCoverSample? Sample, GroundCoverInstance? Instance, float Scale, float YawRadians)`.
- Produces `GroundCoverDistribution.GenerateTraced(RectArea area, GroundCoverSettings settings, Func<GroundCoverCandidate, GroundCoverSurfaceInput> surface, Action<GroundCoverDecision>? observe = null) -> IReadOnlyList<GroundCoverInstance>`. Both old and new entry points share one arithmetic kernel. Observer absence creates no decision collection. Native typed refusals throw to the batch boundary, never become null or a fake zero-height sample.
- Produces `GroundCoverDistribution.Prepare(GroundCoverSettings settings) -> GroundCoverCandidateEvaluator`, immutable copied settings/model order, `GroundCoverCandidateEvaluator.Candidate(int x, int z) -> GroundCoverCandidate` and `Evaluate(int x, int z, Func<GroundCoverCandidate, GroundCoverSurfaceInput> surface) -> GroundCoverDecision`. Evaluate has no area clipping, so comparison can own a lattice address wherever its jittered point lands. Generate/GenerateTraced use that same kernel plus their released half-open area check. Native generation uses the evaluator directly, without an intermediate accepted-instance list.

- [ ] **Capture the released public baseline before writing tests that reference missing APIs.** Add `GroundCoverReleasedBaselineTests.ReleasedGenerator_MatchesIndependentChannels` using only released Generate, an invented RectArea(-2,-2,2,2), seed 17, spacing 0.5, the basic model/scale/root settings and a UnitY flat surface at height 1/density 1. An independent scalar hash fixture computes all seven channel values from the pinned constants and verifies model/scale/yaw/rank and transformed rows. Print the invented row values as C# bit literals through ITestOutputHelper, freeze them in `GroundCoverTraceFixtures.ReleasedBits` and retain the pre-product-edit SHA/log. No private data or new product API participates.
- [ ] **Run the passing baseline once:** `wa_run t3-baseline dotnet test "$RENDER" -c Release -m:1 --filter "FullyQualifiedName~GroundCoverReleasedBaselineTests" --logger "console;verbosity=detailed" --logger "trx;LogFileName=t3-baseline.trx" --results-directory "$wa_r7_log_dir"`. Expect nonzero tests, exit 0 and independently matched public bits. This is a preserved regression, not feature red.
- [ ] **Freeze the actual released TileFoliageSurface independently (I4).** Before adding diagnostic APIs or changing its code, require an empty `git diff f1cd6656b HEAD -- KhaozEngine.TileWorld.Render3D/TileFoliageSurface.cs KhaozEngine.TileWorld.Render3D/TileGroundMesher.cs`. `TileFoliageReleasedFixtures.Cases() -> IReadOnlyList<TileFoliageReleasedCase>` builds invented slopes, all four cuts by four rotations, NoDraw, Blocked, zero raster, solid, roof, door, edge-fade and region-edge cases. `TileFoliageReleasedCase` stores name, source document/catalog/layer, sample points and literal int bits for Height, Normal.X/Y/Z and Density. One isolated predicate per rejection case plus zero-raster/solid overlap pins precedence. Capture with only released Sample and CornerNormal, then freeze C# literals in TileFoliageReleasedFixtures. Record source SHA and pre-edit log, never regenerate after refactoring to bless a mismatch.
- [ ] **Run that capture once before new API references:** `wa_run t3-surface-baseline dotnet test "$RENDER" -c Release -m:1 --filter "FullyQualifiedName~TileFoliageReleasedBaselineTests" --logger "console;verbosity=detailed" --logger "trx;LogFileName=t3-surface-baseline.trx" --results-directory "$wa_r7_log_dir"`. `ReleasedTileFoliageBits_MatchPinnedSource` subsequently asserts the literals without a recorder path. Task 12 detailed/wrapper tests consume them. This source-anchored oracle, not Sample-versus-delegated-SampleDetailed equality, proves preservation.
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
- [ ] **Pin evaluator parity** in `SingleAddressEvaluator_EqualsTracedOwnedDecision`, including addresses (128,0), seeds 0 and 1, spacing 0.5 and 0.51. All seven channels and accepted matrices/ranks match the frozen/released formula. Keep multiply/add order, no FMA or reassociation. All observer rows are provisional until the enclosing Generate/Query result is Complete.
- [ ] **Run green:** red filter as `t3-green`, `wa_test t3-regress "$RENDER" "FullyQualifiedName~GroundCoverDistributionTests"`, then `wa_sweep t3-final`.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t3`, then `feat(terrain): expose cover candidate decisions without rerolling samples`.

### Task 4: Budgeted immutable generation context and full-request support (5 to 8 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageGenerationContext.cs`, `MapFoliageContextPreflight.cs`, `MapFoliageLimits.cs`, `KhaozEngine.MapDoc/Support/MapPreparedSupport.cs`, `MapSupportPreparation.cs`, `MapSupportSelection.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageContextTests.cs`, `MapPreparedSupportParityTests.cs`.
- Modify: `KhaozEngine.MapDoc/Support/MapSupportQuery.cs:24-34,52-209` to share selection after acquisition, and Task 1 MapFoliageJson for an internal thread-local whole-payload write counter. Preserve the query's validation/acquisition order and point-level budgets, never force a released one-point query to acquire every chunk face.

**Interfaces:**
- Consumes released `MapPrefabResolver.Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits? limits = null) -> MapEffectiveResources` (:25), `MapEffectiveResources.Acquire(MapSurfaceScope) -> MapScopedSurfaces` (:100), `MapSupportRequest(MapFramePoint Point, string? SurfaceId, string? SpaceId, MapFaceKey? CurrentSupport, float MaxStepUp, float MaxDropDown, float? MaxSlopeRadians)` at Support/MapSupportTypes.cs:11, `MapSupportQuery.Select(MapSupportRequest) -> MapSupportResult` (:24), and its internal SampleSurface/SampleSurfaceExact methods (:27-32).
- Produces `MapFoliageStatus { Complete, MissingSupport, Unavailable, PartialResidency, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }`, `MapFoliageContextLimits(long MaxSourceUtf8Bytes = 134_217_728, int MaxChunkDescriptors = 65_536, int MaxPlacements = 65_536, int MaxSurfacePatches = 65_536, int MaxPaintTargets = 4096)`, `MapFoliageContextWork(long SourceUtf8Bytes, long ChunkDescriptors, long Placements, long SurfacePatches, long PaintTargets, long Expansions, long SnapshotSerializations)`, `MapFoliageContextMeasure(MapFoliageStatus Status, MapFoliageContextWork Work)` and `MapFoliageContextResult(MapFoliageStatus Status, MapFoliageGenerationContext? Context, MapFoliageContextWork Work)`.
- Produces `MapFoliageGenerationContext.Capture(MapDocument document, MapAssetClosure assets, MapFoliageContextLimits? limits = null) -> MapFoliageContextResult`. Complete always has a context, refusal has null. Context owns immutable foliage, verified closure/effective resources, spatial owner/paint metadata, SnapshotId and construction Work. Internal `MapFoliageContextPreflight.Measure(MapDocument document, MapAssetClosure assets, MapFoliageContextLimits limits) -> MapFoliageContextMeasure` refuses before expansion/cloning/staging. SourceUtf8Bytes is a conservative preflight charge: 8 bytes per JSON node, 64 per number and 6*UTF-16 length+2 per string/property name, including base64 length for density. Count verified prefab metadata/expansion demand before instance allocation, keeping released expansion limits. No full serialized buffer. One context per accepted immutable snapshot is reused, never rebuilt inside Generate/Query.
- Context produces `Acquire(MapSurfaceScope scope) -> MapScopedSurfaces`, `PaintFor(MapSurfaceScope scope) -> IReadOnlyDictionary<string, MapPaintRegionSet>` and `PlacementsIn(MapFoliageRect bounds) -> IReadOnlyList<MapResolvedPlacement>`, using captured spatial metadata, not full scans. Owner proxy bounds use unions of applicable rule/mask footprints and door reach, never interaction envelopes. SnapshotId derives from already captured root/content/closure facts without another Expand. Internal `MapFoliageJson.ThreadPayloadWrites: long` counts whole-inline-payload serialization, alongside released `MapPrefabResolver.ThreadExpansions: long` at :23. These count actual calls, not a context field assumed to stay unchanged.
- Produces internal `MapFoliageGenerationContext.CaptureEffective(MapDocument document, MapAssetClosure assets, MapEffectiveResources resources, MapFoliageContextLimits? limits = null) -> MapFoliageContextResult` for exact resources already captured by a pure/native transaction. It checks snapshot/closure correspondence, runs context preflight and builds immutable indices without another Expand. The transaction must run Measure before its own expansion/cloning allocations. This is not a public route to inject unrelated effective resources. Task 9 carries these contexts in the finalization stamp.
- Produces `MapSupportPreparation.Prepare(MapScopedSurfaces view) -> MapPreparedSupport`, `MapPreparedSupport.Sample(MapSupportRequest request, bool namedSurfaceOnly = false, MapExactPoint? exactWorld = null) -> MapSupportResult`, and internal `MapSupportSelection.Select(MapPreparedSupport prepared, MapSupportRequest request, bool namedSurfaceOnly, MapExactPoint? exactWorld = null) -> MapSupportResult`. The full request, exact point and named-owner mode reach the common kernel. Expose released SampleSurface(MapFramePoint,string) and SampleSurfaceExact(MapExactPoint,string) publicly without changing signatures.
- Produces `MapFoliageLimits(int MaxCandidates = 4_000_000, int MaxSamples = 262_144, int MaxPrepareScopes = 256, int MaxFaces = 262_144, int MaxRuleCandidates = 4096, int MaxMaskCandidates = 4096, int MaxQueryChunks = 64, long MaxQuerySamples = 262_144, long MaxQueryRetainedBytes = 67_108_864, long MaxDifferentialCandidates = 64_000_000, long MaxChangeRows = 4_000_000, int MaxComparisonPreparedChunks = 64, long MaxComparisonRetainedBytes = 134_217_728)` and `MapFoliageWork(long Candidates, long Samples, long PrepareScopes, long Faces, long RuleCandidates, long MaskCandidates, long SurfaceQueries, long RetainedBytes)`.

- [ ] **Write failing tests:** `ContextAcquisition_UnrelatedGrowthDoesNotGrowChunkWork`, `PartialSnapshot_RefusesBeforeCapture`, `ContextBudget_RefusesBeforeExpansionOrStagingAllocation`, `PreparedSupport_FullRequestMatchesReleasedSelection`, `SurfaceAndSpaceBindings_NormalizeToOneSupportSurface`.

```csharp
Assert.Equal(MapFoliageStatus.PartialResidency, partial.Status);
Assert.Null(partial.Context);
Assert.Equal(0, refused.Work.Expansions);
Assert.Equal(1, captured.Work.Expansions);
Assert.Equal(0, chunkPhaseExpansions);
Assert.Equal(0, chunkPhaseSnapshotSerializations);
Assert.Equal(fixedChunkWork, unrelatedGrowthChunkWork);
Assert.Equal(released.Status, prepared.Status);
Assert.Equal(released.Face, prepared.Face);
Assert.Equal(released.SpaceId, prepared.SpaceId);
```

Grow unrelated stored chunks 2 to 64, placements 8 to 128 and paint targets 1 to 32 while holding the requested chunk/halo fixed. At this task use Acquire/PaintFor/PlacementsIn, which exist now, not future Generate. Capture work accounts for growth once, repeated scoped calls have equal demanded patches/faces/owner candidates and zero deltas in ThreadExpansions/ThreadPayloadWrites. Task 7 repeats the same regression through complete Generate/Query. Lower each context limit to one below measured demand and refuse before staging. Both monolithic and fully loaded tiled contexts succeed. A partially resident tile, surface or foliage pack refuses with PartialResidency before reads/expansion, never a zero-density or streamed success.

Prepared-versus-released parity cases use slope limits 0 and MathF.PI/4, an explicit current seam face, equal-height independent-owner ambiguity, automatic occupied-space selection with SpaceId null, asymmetric up/down searches, SurfaceId filters and invalid/out-of-scope inputs. Compare Status, Face, WorldY bits, Normal, SpaceId, Via and witness outcomes. Test the same floor under Surface and Space bindings, nested room membership, and Surface/Record namespaces both containing text `shared`. The prepared request cannot replace CurrentSupport or slope with binding-only hidden state.

- [ ] **Run red:** `wa_test t4-red "$MAPDOC" "FullyQualifiedName~MapFoliageContextTests|FullyQualifiedName~MapPreparedSupportParityTests"`.
- [ ] **Implement** capture/preflight and shared selection. Expand exactly once after context budgets pass, capture source bytes only for the snapshot, reuse its effective owner/transform and paint metadata, and acquire chunk-specific support/paint from that context. Supplied mutable documents may not be edited during capture. Later edits create a new context, never mutate the old one. Foliage Surface requests use the explicit owner with namedSurfaceOnly true and no invented interval. Space requests use referenceY, SearchAbove/Below, CurrentSupport null and slope null. The common kernel still accepts every field of an ordinary released request.
- [ ] **Run green:** red filter as `t4-green`, `wa_test t4-regress "$MAPDOC" "FullyQualifiedName~SupportQueryTests|FullyQualifiedName~MapSupport"`, then `wa_sweep t4-final`. Reforecast after this gate. No timing/load test.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t4`, then `feat(mapdoc): capture bounded foliage contexts and preserve support requests`.

### Task 5: Compatibility interpolation, normals and visible materials (4 to 7 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageSupport.cs`, `MapFoliageSupportIdentity.cs`, `MapFoliageNormals.cs`, `MapFoliageMaterials.cs`, `KhaozEngine.MapDoc/Surfaces/MapFoliageTriangles.cs`, `KhaozEngine.MapDoc.Tests/Foliage/FoliageSurfaceFixtures.cs`, `MapFoliageSurfaceTests.cs`, `MapFoliageSupportTests.cs`.
- Modify: `KhaozEngine.MapDoc/Surfaces/MapSurfaceCompiler.cs:14-38,145-178` only for a shared triangulation entry, and Task 4 preparation for internal `PrepareFoliage(MapScopedSurfaces view) -> MapPreparedSupport`. Physical Compile remains unchanged.

**Interfaces:**
- Consumes Task 4 context/full-request selector and released `MapPartialPaintCompiler.Compile(MapScopedSurfaces view, string targetSurfaceId, MapRefinementLimits? limits = null) -> MapPaintRegionSet` (:16), `MapSurfaceCompiler.Compile(MapSurfaceRef, MapSurfacePatch) -> MapCompiledPatch` (:14), `MapResourceTransform.Normal(Vector3) -> Vector3` (:70).
- Produces `MapFoliageReason { Accepted, OutsideDomain, RasterZero, MissingUnderlay, MaterialNotAllowed, WaterMaterial, Indoor, Solid, RoofAbove, Door, EdgeZero, DensityRejected }`, `MapFoliageSurfacePoint(MapFoliageStatus Status, MapFoliageReason Reason, string? SupportOwnerId, string? OccupiedSpaceId, MapFaceKey? Face, float Height, Vector3 Normal, float Density, ushort VisibleMaterial)` and `MapFoliageSupport.Prepare(MapFoliageGenerationContext context, MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageLimits limits) -> MapPreparedFoliageSupport`. Prepared exposes Status, Work, DependencyParts and `Sample(float worldX, float worldZ) -> MapFoliageSurfacePoint`.
- Produces internal `MapFoliageSupportIdentity.From(MapSupportResult result) -> string?`. Canonical SupportOwnerId is selected Face.OwnerId for both binding kinds. Logical NoDraw uses Compatibility.Patch.SurfaceId, explicitly marked LegacyFallback, never capture support. SpaceId becomes OccupiedSpaceId only. Rejected Surface-bound points may retain the requested surface, unresolved Space points may have null, accepted samples cannot. Clearance resolves bindings through this normalization, never compares binding text IDs.
- Produces `MapFoliageTriangles.Compile(MapSurfaceRef surface, MapSurfacePatch patch) -> MapCompiledPatch`, internal `MapFoliageNormals.Corner(MapScopedSurfaces view, string surfaceId, long x, long z, float boundaryHeight) -> Vector3` and `MapFoliageRefusal(MapFoliageStatus status, MapFoliageWork work, string detail) : Exception` with readonly Status/Work for atomic batch unwinding.

- [ ] **Write failing tests:** `NamedSurfaceAndSpaceBindings_RefuseWithoutFallingThrough`, `UnavailableNormalHalo_IsNotAnEdgeExtension`, `LegacyFlagsCutsAndBorderNormals_MatchTheNamedPolicy`, `PreparedSurface_DoesNotQuerySupportPerCandidate`, `LegacyBorder63To65_DoesNotExtendAnAbsentSlot`.

```csharp
Assert.Equal(MapFoliageStatus.MissingSupport, missing.Status);
Assert.Equal(MapFoliageStatus.Unavailable, unavailable.Status);
Assert.Equal(MapFoliageStatus.Ambiguous, ambiguous.Status);
Assert.Equal(MapFoliageReason.OutsideDomain, knownOutside.Reason);
Assert.Equal(surfacePoint.SupportOwnerId, spacePoint.SupportOwnerId);
Assert.Equal("floor-1", surfacePoint.SupportOwnerId);
Assert.Equal("level-1", spacePoint.OccupiedSpaceId);
Assert.Equal(noFlagsDecision, noDrawAndBlockedDecision);
Assert.Equal(0, candidatePhaseWork.SurfaceQueries);
```

All four cuts by four rotations, partial-paint priority, unpainted notches, zero underlay with overlay, water kind, Indoor and NoDraw/Blocked are covered. Named support missing while another floor remains refuses, a complete known hole yields no sample, unavailable advertised data refuses even at zero density. For the border, use source slot 0 with 100 cm corners, x=64 at 130 cm, z=32 and slot 1 absent. Heights at x=63,64,65 are 1,1.3,0 metres, not 1,1.3,1.3. Corner normals are Normalize((-(1.3f-1f)/2,1,0)), Normalize((0.5f,1,0)), Normalize((1.3f/2,1,0)). Repeat with slot 1 advertised but unavailable, requiring Unavailable. These mirror Task 3's pinned released edge cases. normalBoundaryHeight is known-absence compatibility metadata, never missing-data recovery.

- [ ] **Run red:** `wa_test t5-red "$MAPDOC" "FullyQualifiedName~MapFoliageSurfaceTests|FullyQualifiedName~MapFoliageSupportTests"`.
- [ ] **Implement** legacy density with float u/v, zero outside [0,Width-1]/[0,Depth-1], endpoint-clamped bilinear `a+(b-a)*t` twice then /255. Height follows bound canonical triangles in legacy tie order. NoDraw logical triangles reuse the compiler kernel without physical installation. Corner differences use bound surface cell metres, not raster cell size: hx=h(x+1,z)-h(x-1,z), hz=h(x,z+1)-h(x,z-1), span=2*cell, Normalize((-hx/span,1,+hz/span)) for NegativeZ and negative hz for PositiveZ. Convert native height units before the same float operations.
- [ ] **Implement** global corner precedence own slot, west, south, south-west, then explicit known-absence boundary height. Preserve corner owners/dependencies across patches, coarse/fine declared dependency interpolation and no floor search. Midpoint normals normalize nA+nB, midpoint heights average, sampled normals normalize barycentric blends. Barycentric inclusion >= -1e-5 and overlay containment +/-1e-6 stay legacy. Transform normals once. Use the captured context's scoped paint partition and material table, not geometric face normals or whole-face paint substitutes. Per-scope subdivision retains MapQueryLimits and P6 preparation totals.
- [ ] **Run green:** red filter as `t5-green`, `wa_test t5-regress "$MAPDOC" "FullyQualifiedName~MapPartialPaint|FullyQualifiedName~MapSurfaceCompiler"`, then `wa_sweep t5-final`.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t5`, then `feat(mapdoc): preserve foliage compatibility normals and visible paint`.

### Task 6: Canonical-owner clearance and ordinal roof rules (4 to 7 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageClearance.cs`, `MapFoliageOwners.cs`, `MapFoliageReferences.cs`, `KhaozEngine.MapDoc.Tests/Foliage/FoliageClearanceFixtures.cs`, `MapFoliageClearanceTests.cs`, `MapFoliageOwnerTests.cs`.
- Modify: Task 1 validation and Task 5 prepared surface, `KhaozEngine.MapDoc/Prefabs/MapPrefabResolver.cs:270-275`, `Prefabs/MapPrefabReferences.cs:114-160,319-354`, `Editing/MapPrefabEditPreparation.cs:155-204`, `Editing/MapPrefabEdits.cs:32-103`, `Editing/MapInteriorEdits.cs:20-70` to validate/remap dependent root foliage references without changing prefab payload 1.
- Test/create: `KhaozEngine.MapDoc.Physics.Tests/MapFoliagePhysicalBoundaryTests.cs`.

**Interfaces:**
- Consumes Task 1 rule/mask DTOs, Task 5 prepared support, released `MapResourceTransform.Point(MapExactPoint) -> MapExactPoint`, `Height(MapExactValue) -> MapExactValue`, `Normal(Vector3) -> Vector3` at Prefabs/MapResourceTransform.cs:14,34,70, and `MapPrefabEdits.Prepare(MapDocument, MapAssetClosure, MapPrefabEdit) -> MapPrefabEditResult` (:32).
- Produces `MapFoliageClearance.Prepare(MapFoliageGenerationContext context, MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageLimits limits) -> MapPreparedFoliageClearance`, immutable indexed rules/masks/rooms with `Evaluate(MapFoliageSurfacePoint point, float worldX, float worldZ) -> MapFoliageSurfacePoint`, `Work`, `DependencyParts` and `Bounds: MapFoliageRect`. Resolve an owner's Surface or Space binding through Task 4/5 support selection and MapFoliageSupportIdentity, never compare its SurfaceId/SpaceId text to a sample owner.
- Produces internal `MapFoliageReferences.Remap(MapFoliageSet set, IReadOnlyDictionary<MapPrefabReference, MapPrefabReference> changes) -> MapFoliageSet` and `ValidateOwners(MapDocument, MapEffectiveResources) -> IReadOnlyList<string>`. The common resolver return validates the already-built resources before returning them, without recursively expanding or sampling candidates. Both heads therefore refuse dangling support/owner references before static installation, even when the caller never generates cosmetic blades. References use typed World owners, including effective child/transfer IDs, and follow the active namespace only. No new prefab child kind or asset descriptor member.

- [ ] **Write failing tests.** `OwnerLocalClearance_UnpackRemapAndRemovalAreAtomic`, `NewBoundPlacement_UsesLayerRulesWithoutAnOwnerMask`, `DoorDistance_IsStrictAndFourNeighbourFadeUsesCentreEligibility`, `InteractionEnvelopeOneMetre_IsNeverAFoliageBlocker`, `RoofOrdering_IsOrdinalRatherThanGeometric`, `ClearanceBindings_ResolveCanonicalSurfaceRatherThanTextIds`.

```csharp
Assert.Equal(MapFoliageReason.Door, insideRadius.Reason);
Assert.NotEqual(MapFoliageReason.Door, exactlyAtRadius.Reason);
Assert.NotEqual(MapFoliageReason.Door, outsideRadius.Reason);
Assert.Equal(0.5f, halfRimDensity / originalDensity);
Assert.Equal(beforeAssetBytes, afterAssetBytes);
Assert.Equal(beforeColliderDigest, afterMaskColliderDigest);
Assert.Equal(beforeUnpackDecisions, afterUnpackDecisions);
Assert.Equal(beforeState, stateAfterOrphanRefusal);
Assert.Equal(MapFoliageReason.RoofAbove, higherOrdinalLowerY.Reason);
Assert.NotEqual(MapFoliageReason.RoofAbove, sameOrdinalHigherY.Reason);
Assert.Equal(surfaceBoundClearing, spaceBoundClearing);
```

Use invented door clearance 0.75, edge fade 0.25, samples at radius minus 0.0001, exactly radius, radius plus 0.0001, and 0.125 m from an ineligible cardinal neighbour. Test rotated/non-square footprint, translated/rotated/scaled prefab at (-12.25,-450,7.125), yaw 0.371 and scale 1.2, override/removal/restoration, unpack, extract, remap and owner deletion with/without dependent removal. A 0.1 m prop's 1 m interaction envelope must not enlarge clearing. Roof fixture has layer ordinal 1, ground Y 20, roof ordinal 2 at Y -2 (clears), and roof ordinal 1 at Y 30 (does not clear). Ownership stays the selected surface in both cases and an unrelated cave never clears. Repeat solid/door/roof matching with Surface floor-1 and Space level-1, nested room membership and Surface/Record owners whose text IDs both equal shared.

- [ ] **Run red:** `wa_test t6-red "$MAPDOC" "FullyQualifiedName~MapFoliageClearanceTests|FullyQualifiedName~MapFoliageOwnerTests"`, `wa_test t6-red-physical "$MP" "FullyQualifiedName~MapFoliagePhysicalBoundaryTests"`.
- [ ] **Implement** deterministic asset/tag rules, ordinal AssetId and legacy case-insensitive tag matching. Solid/roof test the selected support cell centre against the owner-local half-open footprint, Door tests the actual point with strict distance < scaled DoorClearance. RoofAbove additionally requires RoofOrder.RoofOrdinal > RoofOrder.LayerOrdinal, never a height comparison. The private importer admits only source-higher rules and preserves their exact witness, native nonnegative same-ordinal witnesses are inactive. Layer booleans keep legacy gating. Normalize both owner and sample to the selected support surface via From, with OccupiedSpaceId used only for membership. Linked room floors or exceptional OwnerSupports supply target bindings. No per-owner correspondence is required for an already bound new placement. Neither binding text equality, world Y, roof visibility nor an interaction envelope can select a target domain.
- [ ] **Implement** indoor rejection as native Indoor flag or shared R5 room membership at the selected support point, no invented exterior. After base tile eligibility, DoorFactor then edge factor multiply density. Edge factor takes the minimum of 1 and distance/EdgeFade for each of four ineligible cardinal support cells, using neighbour-centre visible material/flags/solid/roof tests, not door tests or arbitrary geometry distance. Normalize density only with the legacy clamp. Mask/owner support references transform once and survive transfers. Owner removal refuses before publication unless the same candidate removes its dependants. Root-owned correspondence contains no clearing geometry and does not replace asset/tag rules.
- [ ] **Run green:** both red filters as green, then `wa_sweep t6-final` after review. Released prefab/cave tests run in that sweep.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t6`, then `feat(mapdoc): apply foliage clearance through effective owner transforms`.

### Task 7: Immutable batches, stable-lattice differential and aggregate retention (4 to 7 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageSamples.cs`, `MapFoliageDistribution.cs`, `MapFoliageDependencies.cs`, `MapFoliageDifferential.cs`, `MapFoliageDifferentialCore.cs`, `MapFoliageComparisonBlock.cs`, `MapFoliageRetentionBudget.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageDistributionTests.cs`, `MapFoliageDifferentialTests.cs`, `MapFoliageBudgetTests.cs`, `FoliageComparisonFixtures.cs`.
- Modify: Task 4/5/6 captured/prepared outputs only to publish immutable inputs and charged counters. No per-chunk Expand or whole-document serialization.

**Interfaces:**
- Consumes Task 3 GroundCoverCandidateEvaluator and Tasks 4 to 6 context, support, clearance and limits. Chunk generation prepares scoped data once from the context, then evaluates addresses directly, never materializes an uncharged GenerateTraced instance list.
- Produces `MapFoliageAddress(string LayerId, string? SupportOwnerId, int X, int Z)`, `MapFoliageComparisonAddress(string LayerId, int X, int Z)`, `MapFoliageSampleKey(MapFoliageAddress Address, int DistributionVersion, MapFoliagePolicy Policy)`, `MapFoliageSample(MapFoliageSampleKey Key, string? OccupiedSpaceId, MapFaceKey? Face, string AssetId, IReadOnlyList<string> MaterialResourceIds, ushort VisibleMaterial, Vector3 Position, Quaternion Orientation, float Scale, Matrix4x4 Transform, float ThinningRank)`. Accepted SupportOwnerId is always the canonical selected surface. Occupied space never replaces it.
- Produces immutable `MapFoliageSampleBatch(string LayerId, MapFoliageChunkKey Chunk, MapFoliageStatus Status, string DependencyDigest, IReadOnlyList<MapFoliageSample> Samples, MapFoliageWork Work)` and `MapFoliageDecision(MapFoliageAddress Address, string? OccupiedSpaceId, MapFoliageReason Reason, GroundCoverDecision Distribution, MapFoliageSample? Sample)`. Refusal exposes no usable Samples. Keep the original matrix, never reconstruct it from a quaternion.
- Produces `MapFoliageDistribution.Generate(MapFoliageGenerationContext context, string layerId, MapFoliageChunkKey chunk, MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null) -> MapFoliageSampleBatch`, `Query(MapFoliageGenerationContext context, string layerId, RectArea area, MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null) -> MapFoliageQueryResult`, and `MapFoliageQueryResult(MapFoliageStatus Status, IReadOnlyList<MapFoliageSampleBatch> Batches, MapFoliageWork Work)`. Query covers its entire half-open area, including off-raster zero density, at most 64 logical chunks. Generation still owns jittered points by world rectangle, logical query chunk indices may be negative, persisted indices may not.
- Produces `MapFoliageDependencies.Compute(MapPreparedFoliageSupport support, MapPreparedFoliageClearance clearance, MapFoliageLayer layer, MapFoliageChunkKey chunk) -> string`, scheme kemap/foliage-batch/1. Digest contains exact intersecting interpolation/normal/paint/material/room/owner/rule/mask/marker/model inputs and versions, never an unrelated whole-context/root digest.
- Produces `MapFoliageComparisonBlock(int X, int Z)` owning 64 by 64 integer candidate addresses using signed floor division, `MapFoliageChangeKind { Added, Removed, Changed }`, `MapFoliageChange(MapFoliageComparisonAddress Address, MapFoliageChangeKind Kind, MapFoliageDecision? Before, MapFoliageDecision? After)` and `MapFoliageDifferentialResult(MapFoliageStatus Status, long Added, long Removed, long Changed, string Digest, string BeforeIdentity, string ProposalDigest, MapFoliageWork Work)`.
- Produces `MapFoliageDifferential.Compare(MapDocument before, MapDocument after, MapAssetClosure assets, string layerId, MapFoliageLimits? limits = null, Action<MapFoliageChange>? observe = null) -> MapFoliageDifferentialResult`, capturing each side once, plus `Compare(MapFoliageGenerationContext before, MapFoliageGenerationContext after, string layerId, MapFoliageLimits? limits = null, Action<MapFoliageChange>? observe = null) -> MapFoliageDifferentialResult` for reuse. Internal `CompareBlock(MapFoliageGenerationContext before, MapFoliageGenerationContext after, string layerId, MapFoliageComparisonBlock block, MapFoliageLimits limits) -> IReadOnlyList<MapFoliageChange>` evaluates the same addresses on both sides wherever their points land, then uses `MapFoliageDifferentialCore.JoinBlock(IReadOnlyList<MapFoliageDecision> before, IReadOnlyList<MapFoliageDecision> after) -> IReadOnlyList<MapFoliageChange>`, bounded to 4096 address pairs. There is no generation-chunk join. Unknown public policy versions refuse, generic join tests supply invented versioned decision records to JoinBlock without bypassing document validation.
- Produces `MapFoliageRetentionBudget(long maxSamples, long maxBytes)`, static `ChargeSample(string layerId, string surfaceId, string assetId, string policyId, IReadOnlyList<string> materials) -> long`, `TryRetainBatch() -> bool` and `TryRetain(long sampleCharge) -> bool`. Charge is 512 + 8*material count + 2*sum UTF-8 byte counts of those IDs/materials, plus 256 bytes per retained batch. Strings reference immutable context tables, no per-sample text copies. Query applies both MaxQuerySamples 262,144 and MaxQueryRetainedBytes 67,108,864 before sample/key/list allocation and retention.

- [ ] **Write failing tests:** `SampleBatch_IsIndependentOfChunkOrderReloadAndLayerName`, `PolicyDifferential_JoinsStableAddressesBeforeVersionedKeys`, `Compare_SeedSpacingAndOriginCrossingsHaveOneChangedRow`, `DependencyDigest_ChangesOnlyForIntersectingInputs`, `LargeSparseDomain_RefusesBeforeAreaAllocation`, `GenerationContext_UnrelatedGrowthDoesNotGrowChunkWork`, `Query_AggregateRetentionRefusesIndividuallyLegalChunks`. Full generation repeats Task 4's unrelated-growth variants and asserts actual ThreadExpansions/ThreadPayloadWrites deltas zero, with equal scoped counters and samples for the fixed chunk.

```csharp
Assert.Equal(wholePoses, reorderedAndReloadedPoses);
Assert.Equal(oldPositions, renamedLayerPositions);
Assert.NotEqual(oldKeys, renamedLayerKeys);
Assert.Single(targetChanges);
Assert.Equal(MapFoliageChangeKind.Changed, targetChanges[0].Kind);
Assert.DoesNotContain(targetChanges, c => c.Kind is MapFoliageChangeKind.Added or MapFoliageChangeKind.Removed);
Assert.Equal(MapFoliageStatus.CapacityExceeded, refusedQuery.Status);
Assert.Empty(refusedQuery.Batches);
Assert.Equal(560, chargedTwoMaterialSampleBytes);
```

Real Compare crossing fixture is an invented flat floor, layer origin (0,-2), cellSize 2, Width 66/Depth 3, full density, candidate (128,0), support floor. Seed 0 to 1 at spacing 0.5 moves X 63.8536376953125 to 64.07940673828125 across X=64. Seed 0 with spacing 0.5 to 0.51 moves X to 65.130706787109375. Origin X 0 to 1, seed 1/spacing 0.5, moves that address's generation ownership from chunk 1 to 0. For the origin case set sample column 32 to byte 254 and all others 255, so interpolated density changes while both remain accepted. Each real Compare gives exactly one Changed row for (layer,128,0), no Removed/Added pair. A uniform-raster origin control has no semantic change row for that address despite moved batch ownership. Prove both sides accepted and old/new chunk ownership differs before asserting the differential.

The retention control uses two individually legal two-sample chunks and MaxQuerySamples 3, then a separate low retained-byte budget. No large allocation: fail before retaining row four or the next byte charge, return empty Batches, preserve work counters and no complete digest. For a/r/f IDs and policy legacy-tile with materials m/n, the exact sample charge is 512+16+2*(1+1+1+11+2)=560. Also cover a 1,048,576-square logical raster with two stored chunks, every per-call/context ceiling plus one, absent/unavailable data, selected models, exact scale/rank, cuts, reload, versioned keys and unrelated dependency stability.

- [ ] **Run red:** `wa_test t7-red "$MAPDOC" "FullyQualifiedName~MapFoliageDistributionTests|FullyQualifiedName~MapFoliageDifferentialTests|FullyQualifiedName~MapFoliageBudgetTests"`.
- [ ] **Implement R7-R3 comparison ownership.** Form the union of the before/after inclusive lattice ranges using each proposal's released floor(min/spacing-0.5)/ceiling(max/spacing+0.5) bounds. Merge block iterators rather than scanning a gap-filled bounding rectangle. Each block evaluates every union address on both sides using Evaluate, even if its jittered point belongs to a different density chunk. Acquire the actual point's prepared chunk with a bounded cache of at most 64 chunks per proposal/block. Charge two candidate evaluations per address against the 64,000,000 aggregate ceiling, checked with wide arithmetic before work. Cache eviction/refusal never changes address ownership. No world-distance halo estimate may omit a large spacing/origin move.
- [ ] **Bound reconciliation retention too.** Across both proposals in one block, cap prepared-cache and decision-row charge at MaxComparisonRetainedBytes 134,217,728. Before allocation charge 1024 bytes per prepared face plus array/index overhead, 1024 per retained decision plus its accepted sample charge, and 256 per change row. Evict prepared entries in deterministic access order or refuse, never retain 64 individually maximal preparations without an aggregate check. Test a low injected byte ceiling on small legal blocks, no large allocation. Work.RetainedBytes reports peak charge, not GC measurement. Context memory is separately admitted by Task 4.
- [ ] **Implement the join** by (layer,gx,gz), support/space as before/after attributes. One accepted side is Added/Removed, two accepted changed records are Changed, two rejected records with changed reason/material/support are diagnostic Changed. Compare scalar bits, normalized sampled density, face/owner/space, pose, ordered model/material references and policy/key values by value, never record/list reference identity. A batch-ownership/digest change alone is not a semantic row. Unchanged rejected channels need no row. Accepted version changes count as Changed even with equal pose. Stream (block Z,X,gz,gx), at most 4,000,000 changes, bind final digest to before/proposal. Refusal issues no token. Generate/Query/Compare observations are provisional until Complete.
- [ ] **Implement aggregate retention** in the common candidate-to-sample sink, before allocating or appending a sample, key, material list or list growth. Query shares one budget across all legal chunks, discards previously retained batches on failure and releases preparation for each completed chunk. Do not generate a whole uncharged intermediate instance list and check it afterwards. Context lifetime/geometry preparation is separately budgeted by Task 4. Candidate-phase support queries remain zero.
- [ ] **Run green:** red filter as `t7-green`, then `wa_sweep t7-final`.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t7`, then `feat(mapdoc): compare foliage by stable lattice and bound query retention`.

### Task 8: Pure layer, raster and metre-brush edits (3 to 5 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapFoliageEdits.cs`, `MapFoliageBrush.cs`, `MapFoliageEffects.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageEditTests.cs`.
- Modify: `KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs:13-32,47-83,85-143`, Task 2 write-set/snapshot support only for direct foliage effects.

**Interfaces:**
- Consumes Tasks 1 to 7 and released `MapPrefabEditResult(MapDocument After, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects)` at Editing/MapPrefabEdits.cs:27. Preserve that positional constructor.
- Produces abstract `MapFoliageEdit(string Label)`, `MapSetFoliageLayer(MapFoliageLayer Layer, string? ExpectedDifferentialDigest = null)`, `MapSetFoliageDensity(string LayerId, int X, int Z, int Width, int Depth, IReadOnlyList<int> Density)`, `MapPaintFoliage(string LayerId, float WorldX, float WorldZ, float RadiusMetres, byte Target, float Hardness)`, `MapRemoveFoliage(string LayerId)` and `MapFoliageEditSequence(IReadOnlyList<MapFoliageEdit> Edits, MapPrefabEdit? OwnerEdit)` inheriting MapFoliageEdit with fixed developer labels.
- Produces `MapFoliageEdits.Prepare(MapDocument document, MapAssetClosure assets, MapFoliageEdit edit) -> MapPrefabEditResult`, `PreviewPolicyChange(MapDocument document, MapAssetClosure assets, MapSetFoliageLayer edit) -> MapFoliageDifferentialResult`, and `MapFoliageBrush.Apply(MapFoliageLayer layer, MapPaintFoliage brush) -> MapFoliageLayer`.
- Produces `MapFoliageChunkChange(string LayerId, MapFoliageChunkKey Chunk, MapFoliageRect? OldBounds, MapFoliageRect? NewBounds, string? BeforeDigest, string? AfterDigest)`, `MapNativeInvalidation.Foliage = 256`, init-only `MapNativeEditEffects.FoliageChunks: IReadOnlyList<MapFoliageChunkChange>`, omitted from Describe when empty. Internal `MapFoliageEffects.Direct(MapDocument before, MapDocument after, MapAssetClosure assets) -> IReadOnlyList<MapFoliageChunkChange>` reports direct layer/byte/settings changes. Task 9 adds document-wide dependent finalization.

- [ ] **Write failing tests:** `MetreBrush_QuantizesPositiveZAndDoesNotAllocateDomain`, `HardnessOne_IncludesExactRadiusAndExcludesJustOutside`, `PureFoliageEdits_RefuseAtomicallyAndBindDifferentialIdentity`.

```csharp
Assert.Equal((byte)128, fullHardnessCentre);
Assert.Equal((byte)64, halfLinearRim);
Assert.Equal((byte)0, softExactRadius);
Assert.Equal((byte)128, hardnessOneExactRadius);
Assert.Equal((byte)0, hardnessOneJustOutside);
Assert.Equal(beforeState, refusedState);
Assert.Equal(MapNativeInvalidation.Foliage, densityOnly.Invalidates);
```

Use zero raster, target 128, radius 2 m and hardness 0.5. At 1.5 m the byte is 64, exactly radius is zero for hardness below 1. Hardness 1 gives 128 at exactly 2 m, at 2.0001 m it stays zero. Also test hardness 0, ties to even, negative/non-square rasters, 255, invalid numeric bytes/lengths, 65 chunks and 65,537 visited addresses refused before writes. Supported NormalBoundaryHeight policy-definition changes require a complete before/proposal-bound differential digest, missing/stale tokens refuse. No-op edits report empty effects.

- [ ] **Run red:** `wa_test t8-red "$MAPDOC" "FullyQualifiedName~MapFoliageEditTests"`.
- [ ] **Implement** strict detached edits and shared brush arithmetic. Skip only distance > radius, full strength for distance <= hardness*radius or hardness == 1, otherwise linear rim. MathF.Round uses ToEven, then clamp bytes. Sequence preparation applies all foliage/owner changes on one detached candidate and validates them together, with no tentative command-state acceptance. Use the stable-lattice Compare, never a chunk-local differential. No backing arrays leak and no full-domain raster is created.
- [ ] **Run green:** red filter as `t8-green`, then `wa_sweep t8-final`.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t8`, then `feat(mapdoc): add pure foliage edits and exact legacy brush boundaries`.

### Task 9: Public document finalization, commands and publication effects (5 to 8 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapDocumentEditFinalizer.cs`, `MapDocumentTerrainEdits.cs`, `KhaozEngine.MapEditor/NativeFoliageCommands.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageFinalizationTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/FoliageEditorFixtures.cs`, `NativeFoliageCommandTests.cs`, `NativeFoliageEffectTests.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/NativeFoliagePublicationTests.cs`, `FoliageDependentWireTests.cs`.
- Modify: Task 8 MapFoliageEffects, `KhaozEngine.MapDoc/Editing/MapPrefabEdits.cs:27-32,104-145`, `MapPrefabEditPreparation.cs:27-78`, `MapInteriorEdits.cs:20-84`, `MapMarkerEdits.cs:24-81`, `KhaozEngine.MapEditor/NativeDocumentTransaction.cs:71-135`, `NativeTerrainCommands.cs`, `NativePlacementTransaction.cs:58-112`, `KhaozEngine.MapEdit.Tool/MapEditSession.NativeHistory.cs:79-169`, and Task 2 snapshots/write sets. Preserve marker/water composition and R6's final-callback publication fix.

**Interfaces:**
- Consumes Task 8 Prepare and effects, Task 4 contexts, released `MapTerrainEdits.Prepare(MapSurfaceSet surfaces, MapTerrainEdit edit) -> MapTerrainEditResult` at :36, `MapPrefabEdits.Prepare(MapDocument, MapAssetClosure, MapPrefabEdit) -> MapPrefabEditResult` (:32), `MapInteriorEdits.Prepare(MapDocument, MapAssetClosure, MapInteriorEdit) -> MapPrefabEditResult` (:20), `MapMarkerEdits.Prepare(MapDocument, MapAssetClosure, MapMarkerEdit) -> MapPrefabEditResult` (:24), and bound MarkerEditCommand pattern.
- Produces public `MapDocumentEditFinalizer.Finalize(MapDocument before, MapPrefabEditResult prepared, MapAssetClosure assets) -> MapPrefabEditResult` and overload `Finalize(MapDocument before, MapPrefabEditResult prepared, MapAssetClosure beforeAssets, MapAssetClosure afterAssets) -> MapPrefabEditResult` for closure changes. Both validate the complete document and finalize direct/dependent foliage against exact before/after snapshots. This is the mandatory common boundary for document-aware pure callers and MapEditor, no editor dependency.
- Produces public `MapDocumentTerrainEdits.Prepare(MapDocument document, MapAssetClosure assets, MapTerrainEdit edit) -> MapPrefabEditResult`, wrapping surface-only preparation and the common finalizer. The released MapTerrainEdits method remains a low-level surface operation, explicitly not a complete document transaction. Raw placement candidate callers must finalize their complete candidate/write-set/base effects through Finalize.
- Produces `FoliageEditCommand(MapFoliageEdit edit) : EditorCommand`, implementing unchanged internal `INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) -> NativeDocumentPreparation`. Adds an internal nonserialized finalization stamp to MapPrefabEditResult and native preparation, carrying before/after identities and reusable contexts, without changing public positional constructors.
- Produces public `MapFoliageEffects.Describe(MapDocument before, MapDocument after, MapFoliageGenerationContext oldContext, MapFoliageGenerationContext newContext, MapNativeEditEffects trigger) -> IReadOnlyList<MapFoliageChunkChange>`. Finalize replaces/deduplicates its own foliage channel idempotently, preserving every unrelated base effect/flag. Contexts must match the selected snapshot and closure, never reuse a stale stamp.

- [ ] **Write failing tests:** `PureDependentEdits_IncludeFoliageEffects`, `DocumentFinalizer_PureEditorWireEffectsAgree`, `EveryDependencyTransaction_ReportsOldAndNewFoliageChunks`, `FoliageCommand_DirectSessionUndoRedoAndRefusalAreAtomic`, `LateCallbackFoliage_PublishesOneSnapshotEffectsAndRedo`.

```csharp
Assert.Equal(pure.Effects.FoliageChunks, editorEffects.FoliageChunks);
Assert.Equal(editorEffects.FoliageChunks, wireEffects.FoliageChunks);
Assert.Equal(baseGeometryFlags, finalized.Invalidates & ~MapNativeInvalidation.Foliage);
Assert.Equal(finalized.FoliageChunks, finalizedAgain.FoliageChunks);
Assert.Equal(beforeCursor + 1, acceptedCursor);
Assert.Equal(beforeState, refusedState);
Assert.Equal(publishedBytes, redoBytes);
Assert.Equal(publishedChunkEffects, redoChunkEffects);
Assert.Equal(publishedDependencyDigest, nextEditBeforeDigest);
```

Parity matrix has a dependent standalone placement move, room edit, roof-link edit and referenced marker move. Pure placement test builds a valid detached unframed standalone candidate, MapNativeWriteSet.PlacementsOnly and matching base bounds, then calls public Finalize. Room/roof/marker public Prepare results are already finalized. Editor and actual released placement/interior/roof/marker wire paths must return equal foliage rows/flag 256 for each, with old/new bounds and identity deltas, while retaining each path's own base geometry effects. A control without foliage succeeds. Unrelated markers produce no foliage rows. Lost support refuses despite another floor. No-op commands add no history/events. A sequence removes owner and mask/correspondence in one undo step and restores both on undo. Callback changes density and a rule after staging a different command, then compares final bytes/effects/redo and next-edit before-state to the one published snapshot.

- [ ] **Run red:** `wa_test t9-red-pure "$MAPDOC" "FullyQualifiedName~MapFoliageFinalizationTests"`, `wa_test t9-red-editor "$EDITOR" "FullyQualifiedName~NativeFoliageCommandTests|FullyQualifiedName~NativeFoliageEffectTests|FullyQualifiedName~NativeFoliagePublicationTests|FullyQualifiedName~FoliageDependentWireTests"`.
- [ ] **Implement** Finalize in MapDoc. Route public prefab/interior/marker/foliage preparations through it, including asset updates with both closures, and expose the document-aware terrain wrapper. Preflight before their existing whole-snapshot copy/Expand steps, then use CaptureEffective for those exact resources, not another expansion. Compose sequences through raw preparation then finalize once. Reuse the stamp when before/after identities and closures still match, so staging an already finalized result captures no duplicate context. No global mutable context. Foliage-absent local-only legacy paths stay valid, foliage without a verified closure refuses before mutation.
- [ ] **Implement** central editor finalization through that public boundary for raw placement/terrain commands, undo/redo, complete snapshots and the final revalidated callback candidate. A changed final fingerprint invalidates only stale after-context/effects, retaining the correct before context. The same final snapshot determines effects, accepted command state and redo. Do not add a second editor-only dependency algorithm.
- [ ] **Implement** dependent selection from typed owners and old/new support/paint/room/rule/mask/marker intersections. Widen by one density cell, two support cells, EdgeFade and maximum transformed door radius, intersect owned domains, sort (layer,Z,X). Include both moved footprints and digests. Over-budget scanning refuses atomically. Density/seed/rule/mask changes add only Foliage, never Physics/Nav/Unbounded. Foliage bounds stay solely in FoliageChunks, never world/nav spatial bounds. Existing geometry changes retain their flags. All wire tests use McpWireCollection and per-call 60 s timeouts.
- [ ] **Run green:** both red filters as green, `wa_test t9-regress "$EDITOR" "FullyQualifiedName~NativePublicationWorkTests|FullyQualifiedName~NativeSnapshotPublicationTests|FullyQualifiedName~NativeSessionHistoryTests|FullyQualifiedName~NativeMarkerSupportEditTests"`, then `wa_sweep t9-final`. Reforecast here.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t9`, then `feat(mapdoc): share document foliage finalization with editor transactions`.

### Task 10: Five verbs, detached discovery and complete wire DTOs (3 to 5 engineer-days)

**Files:**
- Create: `KhaozEngine.MapEdit.Tool/MutationService.Foliage.cs`, `QueryService.Foliage.cs`, `Tools/FoliageTools.cs`, `FoliageResults.cs`, `FoliageWire.cs`, `FoliageWireDtos.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/FoliageToolTests.cs`, `FoliageWireTests.cs`.
- Modify: `KhaozEngine.MapEdit.Tool/Tools/McpBootstrap.cs:30-54`, `KhaozEngine.MapEditor.Tests/MapEditTool/McpAdapterTests.cs:32-115` pinned inventory/count, `KhaozEngine.MapEdit.Tool/README.md:384` inventory and foliage table, Task 1 shared binding codec only to expose its closed writer/reader to the dedicated tool converter. QueryService is already partial (:16).

**Interfaces:**
- Consumes Task 8 pure edits, Task 9 FoliageEditCommand/finalizer, Task 4 reusable contexts and Task 7 CPU Query/Generate. Released McpWire.CallAsync/ListAsync use one 60 s timeout per request and McpWireCollection is non-parallel. Released `MapEditSession.Validate(bool verifyWholeWorld = false) -> ValidateResult` at :418 supplies Valid/WholeWorldChecked/WholeWorldValid.
- Produces `MapFoliageMutationResult(bool Applied, MapFoliageStatus Status, string Identity, string Effects, bool Dirty, string? UndoLabel, string? RedoLabel, MapFoliageDifferentialResult? Differential)`, sealed `MapFoliageLayerInfo(MapFoliageLayer layer)` with copied Task 1 metadata except Chunks plus complete ChunkKeys, `MapFoliageReadMode { List, Layer }`, `MapFoliageReadResult(MapFoliageReadMode Mode, IReadOnlyList<MapFoliageLayerInfo> Layers, MapFoliageLayerInfo? Layer, IReadOnlyList<MapFoliageChunk> Chunks, MapFoliageSampleBatch? Preview)`.
- Produces `MutationService.FoliageApply(MapFoliageEdit edit, bool dryRun = false) -> MapFoliageMutationResult`, `QueryService.FoliageGet(string? layerId = null, int? x = null, int? z = null, int? width = null, int? depth = null, MapFoliageChunkKey? previewChunk = null) -> MapFoliageReadResult`. Omitted ID lists detached metadata in authoring order, with no density/preview and Layer null. Reject rectangle/preview without an ID. Named mode has Layers empty, Layer metadata, and only explicitly requested chunks/preview. All four rectangle fields travel together and obey read limits.
- Produces `FoliageChunkWire(int X, int Z, byte[] Density, string Sha256)` with copied bytes, `FoliageVectorWire(float X, float Y, float Z)`, `FoliageQuaternionWire(float X, float Y, float Z, float W)`, `FoliageMatrixWire(float M11, float M12, float M13, float M14, float M21, float M22, float M23, float M24, float M31, float M32, float M33, float M34, float M41, float M42, float M43, float M44)`.
- Produces `FoliageSampleWire(MapFoliageSampleKey Key, string? OccupiedSpaceId, MapFaceKey? Face, string AssetId, string[] MaterialResourceIds, int VisibleMaterial, FoliageVectorWire Position, FoliageQuaternionWire Orientation, float Scale, FoliageMatrixWire Transform, float ThinningRank)`, `FoliageBatchWire(string LayerId, int ChunkX, int ChunkZ, string Status, string DependencyDigest, FoliageSampleWire[] Samples, MapFoliageWork Work)`, `FoliageLayerWire(MapFoliageLayerInfo info)` copying its metadata properties, and `FoliageReadWireResult(string Mode, FoliageLayerWire[] Layers, FoliageLayerWire? Layer, FoliageChunkWire[] Chunks, FoliageBatchWire? Preview)`.
- Produces `FoliageWire.ToWire(MapFoliageReadResult result) -> FoliageReadWireResult` and internal `FoliageWire.ToolOptions: JsonSerializerOptions`, copied from McpJsonUtilities.DefaultOptions with camelCase properties, string enums, explicit default-valued pose members and a foliage-only closed MapSupportBinding converter shared with Task 1's codec. Byte[] serializes as base64, quaternion as x/y/z/w, matrix as m11 through m44. No core chunk memory is exposed. Register `builder.WithTools<FoliageTools>(FoliageWire.ToolOptions)` explicitly.
- Tools produce `FoliageLayerSet(string layerJson, string? expectedDifferentialDigest = null, bool dryRun = false) -> MapFoliageMutationResult`, `FoliageGet(string? layerId = null, int? x = null, int? z = null, int? width = null, int? depth = null, string? previewChunkJson = null) -> FoliageReadWireResult`, `FoliageDensitySet(string layerId, int x, int z, int width, int depth, int[] density) -> MapFoliageMutationResult`, `FoliagePaint(string layerId, float worldX, float worldZ, float radiusMetres, int target, float hardness) -> MapFoliageMutationResult`, `FoliageRemove(string layerId) -> MapFoliageMutationResult`. Attributes name exactly foliage_layer_set, foliage_get, foliage_density_set, foliage_paint, foliage_remove. Count stays 101, no sixth list verb.

- [ ] **Write failing tests:** `FiveFoliageVerbs_MakeExactInventory101`, `FoliageWire_MatchesDirectCommandsBytesEffectsAndHistory`, `FoliageWire_ContainsEveryByteQuaternionAndMatrixComponent`, `FoliageGet_OmittedIdListsDetachedMetadataInAuthoringOrder`, `FoliageWire_MapValidateChecksRegionPacks`, `FoliageWire_RefusalAndDryRunChangeNothing`.

```csharp
Assert.Equal(101, listedTools.Count);
Assert.Equal(JsonValueKind.String, densityJson.ValueKind);
Assert.Equal(expected1024Bytes, Convert.FromBase64String(densityJson.GetString()!));
Assert.Equal(4, orientationJson.EnumerateObject().Count());
Assert.Equal(16, transformJson.EnumerateObject().Count());
Assert.Equal("list", listBody.GetProperty("mode").GetString());
Assert.Equal(authoringOrderIds, listedIds);
Assert.True(cleanValidation.Valid && cleanValidation.WholeWorldValid);
Assert.False(corruptValidation.Valid);
Assert.Equal(beforeState, refusedState);
```

Parse the actual MCP response JSON body, not an in-memory service result. Check all 1024 bytes and every position/quaternion/matrix/scale/rank component against independent expected single bits, using nonidentity yaw 0.371, tilt 0.2, scale 1.2 and translation (-1.25,2.5,3.75). Matrix zero entries and m44=1 must still be present, quaternion has all four fields. Mutating decoded read arrays changes no session state. List two layers in authored order with omitted ID, then fetch one by ID, reject nameless raster/preview, and prove no density in list mode. For map_validate, open a valid tiled fixture first, then separately leave clean, remove a pack, corrupt it, add an orphan, or add a temporary pack on disk. Call real map_validate with verifyWholeWorld true. Clean is valid, every changed case sets WholeWorldChecked true and WholeWorldValid/Valid false with the expected category. No save/reload-only substitute.

- [ ] **Run red:** `wa_test t10-red "$EDITOR" "FullyQualifiedName~FoliageToolTests|FullyQualifiedName~FoliageWireTests|FullyQualifiedName~McpAdapterTests.ToolRegistry_ExposesAllDocumentQueryMutationVerbs"`.
- [ ] **Implement** thin adapters, the dedicated DTO mapping/options and exact registration. Input layer JSON remains the closed inline Task 1 shape, independent of result serialization. Convert target only after range 0 to 255 validation. Reuse one context per accepted session snapshot for preview, invalidate on edit/undo/redo/reload. Policy dry-run returns complete identity-bound differential without writes. Public finalizer supplies dependent effects, never a tool-owned variant. All wire classes join McpWireCollection and every request uses McpWire's per-call 60 s token.
- [ ] **Run green:** matched red filters as green, then `wa_sweep t10-final`.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t10`, then `feat(mapedit): expose complete foliage wire poses and layer discovery`.

### Task 11: Public synthetic acceptance, OA9 and three-platform pins (3 to 5 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.Tests/Foliage/FoliageAcceptanceFixtures.cs`, `NativeFoliageAcceptanceTests.cs`, `FoliagePlatformPinsTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapFoliageWorldTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/FoliageSchemaParityTests.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/FoliageAcceptanceWorkflowTests.cs`, `KhaozEngine.Tests/ArchitectureTests.Foliage.cs`, `.github/workflows/foliage-conformance.yml`.
- Modify: the living acceptance/handoff tables in `KhaozEngine.MapDoc/README.md`, `KhaozEngine.MapDoc.Physics/README.md` and `KhaozEngine.MapEditor/README.md`. Existing fixture source links cover Foliage fixtures.

**Interfaces:**
- Consumes Tasks 1 to 10, released `MapWorldBuilder.Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options) -> MapBuiltWorld` at MapDoc.Physics/MapWorldBuilder.cs:42, `MapBuiltWorld` geometry/statics/annotations, released stacked and prefab-cave fixtures and the existing schema evaluator used by MapEditor.Tests. No new test project or broad project reference.
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
- [ ] **Run red:** `wa_test t11-red "$MAPDOC" "FullyQualifiedName~NativeFoliageAcceptanceTests|FullyQualifiedName~FoliagePlatformPinsTests"`, `wa_test t11-red-world "$MP" "FullyQualifiedName~MapFoliageWorldTests"`, `wa_test t11-red-schema "$EDITOR" "FullyQualifiedName~FoliageSchemaParityTests|FullyQualifiedName~FoliageAcceptanceWorkflowTests"`, `wa_test t11-red-architecture "$RUMP" "FullyQualifiedName~FoliageRuntime"`. Missing new acceptance wiring/pins is red, a correctly implemented earlier behavior may already pass and is recorded as regression rather than fabricated red.
- [ ] **Implement** the invented acceptance fixtures, actual workflow assertions and architecture guard. The architecture guard walks direct/transitive production edges and absence of sample-driven colliders, not source-name guesses. Create `foliage-conformance` for push, pull_request and workflow_dispatch, matrix `ubuntu-26.04`, `macos-26`, `windows-latest`, .NET 10.0.x and the repository's current pinned checkout/setup-dotnet/upload-artifact actions. Restore/build the named area projects, run only FoliagePlatformPinsTests plus GroundCoverTraceTests once per OS, upload TRX, and require nonzero tests and no skips. Hosted jobs do not call the dev Mac's build-slot. No private harness selection, credentials or load loop.
- [ ] **Run green:** the four red filters as green and `wa_sweep t11-final`. Review the workflow for the exact hosted matrix, read-only contents permission, pinned actions, portable bash commands and no private harness selection. Controller requires successful workflow parsing and all three hosted conformance legs on the exact pushed candidate before final acceptance, no local attempt to simulate other operating systems.
- [ ] **Format/guards/commit:** explicit Files paths through `wa_finish t11`, then `test(mapdoc): prove native foliage ownership and platform conformance`.

### Task 12: Independent legacy access and private comparison contract (5 to 8 engineer-days)

**Files:**
- Create: `KhaozEngine.TileWorld.Render3D/TileFoliageDecision.cs`, `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedFoliageInputs.cs`, `ShippedFoliageComparison.cs`, `ShippedFoliageComparisonTests.cs`, `ShippedFoliageInputGuardTests.cs`, `ShippedFoliageGuardFixtures.cs`, `ShippedFoliageManifest.cs`. Split by concern before 800 lines, never add unowned helpers silently.
- Modify: `KhaozEngine.TileWorld.Render3D/TileFoliageSurface.cs:9,43-117,240-283`, harness `KhaozEngine.MapDoc.OracleHarness.Tests.csproj:13-20`, harness `README.md` private foliage contract, `KhaozEngine.Tests/ArchitectureTests.MapDoc.cs:31-52`. Update obsolete private synthetic format pins to 9 while preserving earlier readers' policies.
- Test/create: `KhaozEngine.Render.Tests/Terrain/TileFoliageDecisionTests.cs` for the additive legacy diagnostic seam, independent of private data.
- Private prerequisite, controller-owned in the game repository only: retained `proofs/r7-foliage-oracle/generator/build-scaffold.py`, every helper/project, correspondence manifests and usage README. The engine worker neither creates nor commits private inputs.

**Interfaces:**
- Consumes released `TileFoliageSurface(TileWorldDocument doc, TileWorldCatalogs catalogs, TileFoliageLayer layer, bool smoothNormals = true)` and `Sample(float worldX, float worldZ) -> GroundCoverSample`, plus Task 3 GenerateTraced. Adds `SampleDetailed(float worldX, float worldZ) -> TileFoliageDecision`, with `TileFoliageDecision(GroundCoverSample Sample, TileFoliageRejection Reason, ushort VisibleMaterial)` and an enum matching the actual legacy rule stages. Old Sample delegates without changing arithmetic or results. No native sampler generates expected rows.
- Consumes released `PrivateOracleEntry.Run(Action<PrivateOracleInputs, PrivateOracleReport>)`, `PrivateOracleInputs.Require(IReadOnlyDictionary<string,string?>) -> PrivateOracleInputs`, `PrivateOracleReport.Check(string category, bool ok, string detailJson) -> void` and `Record(string category, string detailJson) -> void`, and `ShippedSourceInventory.ReadWorld(PrivateOracleInputs) -> (TileWorldDocument Document, TileWorldCatalogs Catalogs)`.
- Produces internal `ShippedFoliageInputs.Read(PrivateOracleInputs inputs) -> ShippedFoliageInputs`, `ShippedFoliageComparison.Compare(ShippedFoliageInputs inputs, PrivateOracleReport report) -> void`, fact `ShippedFoliage_PreservesEveryByteCandidatePoseAndDecision`, and `ShippedFoliageGuardFixtures.Create(string variant) -> ShippedFoliageGuardFixture`. Guard fixture exposes only invented Environment and PublicSecretMarker, owns secure temporary paths and IDisposable cleanup. Private fact/guards carry UnsupportedOSPlatform("windows"), like the released secure-report entry. Public platform pins are separate.

- [ ] **Write failing public synthetic guards** for complete invented extraction, missing each of four variables, wrong provenance digest, duplicate/case-variant/unknown members, missing/unattested region/catalog/helper/resource/chunk/pack, incomplete owner correspondence, extra/missing native sample, wrong seed/model/rank/reason, one changed byte, unsafe/existing/symlink report path, escaped dependency, stale input after loader and failed generator self-check. Discovery/static initialization reads no environment or files. `TileFoliageDecisionTests.LegacySurface_DetailedAndWrapperMatchFrozenReleasedBits` asserts Task 3's independently frozen f1cd6656b literals for all source cases, including slopes, all cuts/rotations, NoDraw/Blocked, region edges, zero raster, solid/ordinal-roof/door rejection and edge fade. Wrapper equality is an additional routing check, never the preservation oracle.

```csharp
Assert.Equal(7, harnessDirectEngineReferences.Count);
Assert.Equal(0, validSyntheticFailures);
Assert.Contains("foliage-byte", changedByteCategories);
Assert.Contains("foliage-decision", changedDecisionCategories);
Assert.Contains("foliage-rank", changedRankCategories);
Assert.DoesNotContain(fixture.PublicSecretMarker, publicFailureMessage);
Assert.Equal(frozenReleasedSampleBits, detailedSampleBits);
Assert.Equal(legacySample, detailed.Sample);
```

- [ ] **Run red:** `wa_test t12-red-guard "$ORACLE" "FullyQualifiedName~ShippedFoliageInputGuardTests"`, `wa_test t12-red-edge "$RUMP" "FullyQualifiedName~OracleHarness_IsUnreferencedAndNotPackable"`, `wa_test t12-red-legacy "$RENDER" "FullyQualifiedName~TileFoliageDecisionTests"`. Never run the oracle project unfiltered, including during solution/candidate verification.
- [ ] **Implement** seventh test-only reference to `KhaozEngine.TileWorld.Render3D`, exact architecture list and README together. Add detailed legacy decisions by exposing the real TileAllowed/DoorFactor/EdgeFactor stages, not reimplementing them in the comparator. Source expected distribution calls the actual legacy sampler and Task 3 channel kernel. Native actual distribution calls only native adapters. Public synthetic scalar/channel pins and legacy bit regressions guard their shared distribution arithmetic against circular agreement.
- [ ] **Conditional Task 12F, independently reviewed CPU extraction.** If synthetic headless construction starts GPU/window state, stop at that evidence and activate P11 under the controller. Create `KhaozEngine.TileWorld/TileFoliageSampling.cs`, `TileFoliageSamplingNormals.cs`, `TileFoliageCpuSample.cs`, moving only the actual CPU sampling/normal/index implementation. Produce `TileFoliageCpuSample(float Height, Vector3 Normal, float Density)`, `TileFoliageCpuDecision(TileFoliageCpuSample Sample, TileFoliageRejection Reason, ushort VisibleMaterial)` and `TileFoliageSampling(TileWorldDocument document, TileWorldCatalogs catalogs, TileFoliageLayer layer, bool smoothNormals = true)` with `SampleDetailed(float worldX, float worldZ) -> TileFoliageCpuDecision`. The existing renderer wrapper maps these values to GroundCoverSample without arithmetic. TileGroundMesher.CornerNormal delegates to the extracted CPU normal kernel. Do not add Terrain/Render/GPU edges to TileWorld merely to reuse a result type.
- [ ] **Prove 12F in a genuinely clean host (M4).** Create `tools/FoliageCpuProbe/FoliageCpuProbe.csproj`, `Program.cs`, `FoliageCpuProbeFixtures.cs`. Console project is nonpackable, outside umbrellas, exactly one direct ProjectReference to KhaozEngine.TileWorld and no PackageReference. Add a build-only ProjectReference with ReferenceOutputAssembly=false and CopyFoliageCpuProbe target to `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj`, staging the probe output closure under its test output/FoliageCpuProbe. This builds the child without loading it in the render host. Extend `KhaozEngine.Tests/ArchitectureTests.Foliage.cs` with `FoliageCpuProbe_ReferencesOnlyTileWorldAndHasNoGpuWindowingClosure`, checking exact direct reference and transitive absence of Gpu, Render2D, Render3D, Windowing and Silk.NET.
- [ ] **Write failing 12F tests before extraction.** `LegacyCpuSampling_FreshProcessDoesNotLoadGpuOrWindowing` in `KhaozEngine.Render.Tests/Terrain/FoliageCpuProbeTests.cs` starts only the already-built probe DLL as a fresh child with ProcessStartInfo.ArgumentList, no test-time build/run. Child records loaded assemblies before and after invented sampling and returns fixed public JSON. Both forbidden sets must be empty, exit 0 and bits equal Task 3 literals. `LegacyCpuSampling_WrapperMatchesFrozenReleasedBits` remains in Render.Tests. Run `wa_test t12f-red "$RENDER" "FullyQualifiedName~FoliageCpuProbeTests|FullyQualifiedName~LegacyCpuSampling"` before moving the CPU implementation, expecting missing task-owned APIs or a named clean-host/bit assertion. The scope/Interfaces above declare the extraction, they do not authorize implementing it before this red.
- [ ] **Implement and verify 12F.** Move the declared actual CPU code, add the dependency-free sample DTO and wrapper mapping, then `wa_run t12f-probe-build --heavy dotnet build tools/FoliageCpuProbe/FoliageCpuProbe.csproj -c Release -m:1`, the same focused filter as t12f-green, `wa_test t12f-architecture "$RUMP" "FullyQualifiedName~FoliageCpuProbe_ReferencesOnlyTileWorldAndHasNoGpuWindowingClosure"`, `wa_sweep t12f-final` and wa_finish over every named extraction/probe/wrapper/csproj/guard path. Commit `refactor(tileworld): expose gpu-free legacy foliage sampling` and independently review before resuming Task 12. The GPU-loaded render host supplies only wrapper parity, the fresh child and exact graph guard supply the CPU-only claim. Keep the seventh harness reference only if inert, otherwise amend its guard under a ruling. No copied policy or oracle waiver.

The full private input closure is declared before extraction, carrying R4-P40 and R6's retained-generator lesson. Every path is extraction-relative, contained and symlink-free. Attest exactly one source world manifest, all listed region bytes needed for cells/corners and their normal halo, every referenced catalog, all source prefab definitions and effective placed-member/override data that determine footprint/collision/tag/plane, every relevant model/mesh/material/texture/support resource, independent layer/material/model/support/owner/rule/mask correspondences, the generator and every imported helper/project/tool version, a monolithic format-9 scaffold, all tiled manifests and region packs, and every native advertised transitive resource. Record full source commit, explicit tag or empty tag, engine pin, generator commit and candidate engine SHA. No fallback checkout search or unattested helper is allowed.

The retained generator reads source archetype footprints, collision kinds and case-insensitive door/roof tags and maps them to owner-local rules independently of the comparator. It admits a roof rule only when original object.Plane > layer.Plane and stores those exact LayerOrdinal/RoofOrdinal values, never derives order from Y. The reader independently checks the witness against source, including higher-ordinal/lower-Y and same-ordinal/higher-Y synthetic controls. It records source-support ownership, settings and positive-Z bytes without baking rejections into density. Exceptional masks need an explicit non-rule source reason. No replacement art or guessed rooms. Self-check compares original footprint/door/ordinal-roof/normal rules before attestation, a discrepancy stops for controller adjudication on the rule, never tolerance (R-GEN-2). Retain generator and helpers in private git first, require two fresh byte-identical extractions without hand edits.

Provenance keeps every released top-level field/aggregate rule. Its closed foliage `comparison` object has exactly `schemaVersion` (1), `nativeDocumentPath`, `nativeTiledManifestPath`, `foliageManifestPath`, nullable `buildingManifestPath` and `expectations`. All paths are attested extraction-relative paths, no absolute paths. `expectations` has exactly nonnegative integer `layers`, `rasterBytes`, `storedChunks`, `sourceOwners`, `models`, `ownedCandidates`, `accepted` and `rejected`. Counts come from the independently enumerated source, with accepted/rejected from the actual legacy sampler, never native results. Private source tile size 1 is required by the released terrain scaffold conversion, it is not a native engine limit.

The closed foliage manifest has exactly `schemaVersion` (1), `generator`, `layers` and `owners`. `generator` has exactly `path` (`generator/build-scaffold.py`) and unique attested `helpers`. Each layer has exactly `sourceLayerId`, `nativeLayerId`, `support`, ordered `models`, ordered `materials`, `ruleIds`, `maskIds`, `ownerIds` and `candidateConvention` (`world-lattice-v1`). `support` is the closed Surface binding. A model row has exactly `sourceArchetypeId`, `nativeAssetId`. A material row has exactly `sourceMaterialId`, `nativeResourceId`, `kind` (Ordinary or Water), and the source catalog must independently agree. Settings/bytes come from source itself, not redundant expected copies.

Each owner has exactly `sourceObjectId` (positive decimal int64 string), `sourcePlane`, `sourceArchetypeId`, `nativeOwner` (closed World MapPrefabReference), `targets`, `ruleIds` and `maskIds`. Each target has exactly `nativeLayerId` and `support`. IDs and source inventory must correspond bijectively, including source overrides and transformed footprint centres. Rules are checked against the original source footprint/collision/tags, not against a generator-authored expected footprint. A higher-plane roof explicitly lists only the source layers it cleared. Rule/mask IDs are exhaustive effective references, no ignored rows. If a target depends on R5 room/building correspondence, buildingManifestPath is required and its complete released building contract is validated against the same extraction. Otherwise it is null.

Unknown rules, extra/missing/duplicate correspondences, wrong ordinal witnesses, non-Surface private imports and incomplete closure refuse. Verify provenance SHA before parsing, snapshot/hash all attested bytes and recheck around source loaders. Reload native inputs from captured bytes, with strict paths/duplicates/aggregate/report rules unchanged. Capture exactly one full generation context for each of the four native heads and reuse it across all region/chunk calls. Source-region comparison streams smaller Query rectangles/chunks when aggregate retention requires it, preserving exact half-open candidate ownership and checking off-raster zero density too. No per-chunk Expand/serialization and no expected records generated by the native adapter.

The comparison asserts the following, chunk by chunk, on two independently loaded heads for each storage form. Settings and every raster byte compare before sample work. Every in-area candidate address has exactly one row, including rejection, with exact eligibility/material/rejection category, seven channel values, model choice, support owner, stable address and versioned key. Accepted position compares within 0.00001 m per component. Orientation compares the full surface-aligned rotation angle, `2*acos(min(1,abs(dot(qSource,qNative))))`, within 0.000001 radians. Normalize extracted rotations and evaluate norm/dot/acos in binary64 only for comparison, never storage. Scale and thinning rank compare exact binary32 bits, using the trace's scale channel rather than re-extracting scale from a rounded matrix. Model/material references and their correspondences compare exactly. Same-process legacy-wrapper regression matrices remain bit-exact. Finite checks reject NaN/infinity before any tolerance arithmetic. Root offset participates in the position check. No matrix-component tolerance is invented as a substitute for angle comparison.

Category counts include layers, bytes, chunks, scanned/owned candidates, accepted, each rejection stage, per-model choices, support owners, storage/head matches and each comparison failure. Keep details/counts only in the private report. Stream successes as aggregates, retain detailed mismatches, and never store the complete million-row corpus to log or memory. One-byte and one-decision synthetic sensitivity controls must fail. The current source is refrozen at execution, historical dimensions are public invented shape regressions only. Comparison is neither R11 importer acceptance nor game adoption.

- [ ] **Run green:** red filters as green, `wa_test t12-regress "$ORACLE" "FullyQualifiedName~PrivateOracleGuardTests|FullyQualifiedName~ShippedBuildingInputGuardTests|FullyQualifiedName~ShippedMarkerInputGuardTests"`, `wa_sweep t12-final`, and `wa_run t12-oracle-format dotnet format "$ORACLE" --verify-no-changes --no-restore`. Expect nonzero guard tests and zero failures. Actual private fact waits for the final reconciled candidate below.
- [ ] **Format/guards/commit:** explicit engine Files paths through `wa_finish t12`, plus the separately verified harness format, then `test(mapdoc): compare foliage through the attested private oracle`. Controller separately verifies the retained private generator commit and closure delivery.

### Task 13: R8 padded immutable handoff and consumer contract (2 to 4 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapFoliageRenderHandoff.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapFoliageHandoffTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapFoliageHandoffBoundaryTests.cs`.
- Modify: `KhaozEngine.MapDoc/README.md`, `KhaozEngine.MapDoc.Physics/README.md`, `KhaozEngine.MapEditor/README.md`, `KhaozEngine.MapEdit.Tool/README.md`, `KhaozEngine.Terrain/README.md`, root `README.md`, `docs/USING-KHAOZENGINE.md`, `docs/DEPENDENCY-SEAMS.md`, `docs/INDEX.md`. Release declarations are chosen concretely after main reconciliation below, not an unreviewed glob.

**Interfaces:**
- Consumes Task 7 MapFoliageSampleBatch, released `MapResolvedAsset.RenderBounds: MapLocalBounds`, `SourceUnitsToMetres: float`, `MaterialResourceIds: IReadOnlyList<string>` at Assets/MapResolvedAsset.cs:15,18-19, and Task 9 FoliageChunks.
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

- [ ] **Run red:** `wa_test t13-red "$MAPDOC" "FullyQualifiedName~MapFoliageHandoffTests"`, `wa_test t13-red-boundary "$MP" "FullyQualifiedName~MapFoliageHandoffBoundaryTests"`.
- [ ] **Implement** the immutable handoff and conservative bounds using the released asset units contract, no resampling or quaternion-to-matrix rebuild. Document exact R8 requirements: styling remains caller policy, preserve live blade/shrub profiles and shadow flags, never change CPU sample selection, use dependency digest plus generation to reject stale work, keep one owned batch with all intersected residency memberships, and release only after last membership. Actual residency/upload, viewport drawing, captures and interior-cut adapter #1474 remain R8, interactive painting stays R9, complete verb parity stays R10, import/actual source acceptance stays R11/G2 and appearance stays R8/G3.
- [ ] **Record the R7-R1 residency handoff explicitly.** R7 context capture supports complete snapshots only and returns PartialResidency for partial terrain/content/foliage. R8 must add a windowed context constructor and its immutable closure/completeness contract before claiming streamed native foliage generation. Sparse addressability and partial storage save/verification are not partial-generation proof. Carry the public finalizer, full-request support semantics, canonical surface identity and observer atomic-completion contract into that constructor.
- [ ] **Update** living APIs, five verbs/101 inventory, payload/format/storage/limits/refusal semantics, compatibility policy, cosmetic boundary, exact private harness edge and deferred handoff rows. Sweep all Markdown for changed API names and old foliage/format/tool-count claims. Do not rewrite executed plans as current reference docs. Controller reconciles C7, J7.x, R10 verb rows and PROGRAM in the private program worktree separately.
- [ ] **Run green:** both red filters as green, then `wa_sweep t13-final` after review and `wa_finish t13` over the exact code/doc paths. Run doc-version guard on the final release declarations again at candidate gate, not by hiding stale declarations.
- [ ] **Commit** explicit Files paths with `feat(mapdoc): publish the padded R8 foliage handoff and consumer contract`.

## Self-review and execution handoff

- [ ] Map every OA39 decision to tasks. Payload/format Task 1, packs/storage schema/verification/copies/identity Task 2, unchanged channels and frozen sampler Task 3, immutable contexts/full support request Task 4, normals/materials Task 5, canonical ownership/ordinal roofs Task 6, lattice differential/retention Task 7, pure brush edits Task 8, public finalization/commands/callback Task 9, wire/discovery Task 10, OA9/platform proofs Task 11, private comparison/12F Task 12, R8 handoff Task 13 and release below.
- [ ] Check all accepted review-1 findings C1 to C3, I1 to I8 and M1 to M4 against their owning tests and the separate response matrix. R7-R1 to R7-R6 are binding, P numbers stay stable and the 49 to 82 estimate explicitly exceeds the old baseline. No failed requirement is resolved by weakening validation or a gate.
- [ ] Verify every released path, range and consumed signature at f1cd6656b and every new type/property against its producing Interfaces block. Reconcile accepted interface changes into all downstream briefs. Test variables are actual fixture/product outputs, never answer-producing fakes.
- [ ] Check every byte/world address, positive-Z row, last-row clamp, chunk boundary/padding, sparse allocation refusal, storage memberships/partial saves, copy paths and canonical identities. Absent data and unavailable advertised data are separate outcomes.
- [ ] Check all five Review Focus regressions and three exact OA9 names. Preserve NoDraw/Blocked legacy behavior and the exact central-difference/global-corner rule. Compiler physical behavior and annotation digest pins remain unchanged.
- [ ] Check rules operate on effective placements once, typed support ownership prevents cross-floor leakage, ownerSupports has no clearing geometry, masks cannot dangle, prefab payload-1 bytes/digests stay fixed, and the 1 m interaction envelope is not queried as foliage geometry.
- [ ] Check lattice-owned comparison evaluates both sides wherever points move, actual seed/spacing/origin crossings have one Changed row per retained address, and no partial observation issues a token. Capture once per full snapshot, count actual expansion/serialization, and refuse partial generation. Query and comparison both have aggregate retained-byte guards, not only per-chunk ceilings.
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

5. Deliver and verify Task 12's complete private input closure and retained generator before running acceptance. Assign `wa_r7_verified_source`, `wa_r7_private_provenance`, `wa_r7_provenance_sha256`, `wa_r7_private_report` from that private attestation. Parent is an existing 0700 directory outside git, report path absent. Run this one fact once against the exact candidate, through the released boundary, reading exactly these variables in order.

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
| Adjudicated R7-P proposals, binding R7-R1 to R7-R6 and review responses | |
| Task commits, red/green TRX and three-project sweeps | |
| Payload/format, sparse storage, every copy path and both identities | |
| Region-pack schema/evaluator/VerifyTiled/map_validate cross-form and corruption proofs | |
| Full-context budgets, partial refusal and full-request prepared-support parity | |
| Old channel bits, compatibility normals/materials and complete decision trace | |
| Transformed rules/masks, owner lifecycle and 1 m envelope separation | |
| Atomic commands, late callback, dependent effects and 101-tool wire parity | |
| Stable-lattice crossing differential, aggregate retention and complete byte/pose wire proof | |
| Three OA9 proofs, both storage forms and four independent heads | |
| Linux, macOS and Windows conformance at exact candidate | |
| Conditional Task 12F evidence or proof seventh reference is inert | |
| Retained private generator and complete attested input closure | |
| Private exhaustive foliage comparison record | |
| R8 immutable poses/references/rank/digest/padded bounds/completeness | |
| Whole-branch review, main reconciliation and candidate verification | |
| Integration, package proof and engine-rooted release | |
| Remaining engine/game program forecast | |
