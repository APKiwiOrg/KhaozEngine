# World authoring R8a native scene and residency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. Independent plan reviews and owner plan approval precede implementation.

**Goal:** Draw, stream and capture native MapDoc content through one optional view, preserving canonical geometry, verified assets and independently complete simulation coverage.

**Architecture:** A GPU-free scoped source joins verified resource metadata, reverse spatial discovery and explicit completeness witnesses beside the released complete builders. The optional MapDoc.Render3D package prepares immutable draw plans, records the same submissions without a device and retains GPU resources by logical owner and intersecting memberships. The editor and ke-mapedit use its view and snapshot contract.

**Tech Stack:** C#/.NET 10, System.Numerics, System.Text.Json, xUnit, MapDoc, Physics, Terrain.Render3D, Render3D and the three native GPU backends.

**Spec:** [WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md](https://github.com/APKiwiOrg/KhaozEngine/blob/feature/world-authoring/docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md), C8, J8.1 to J8.3 in the historical R8 plan and DG9.3 to DG9.5. OA42 approves the R8 gate, planning only. Its Decision 9 supersedes the unsplit R8 outline. Source ranges below refer to released engine `5d896b333`, v21.9.0, MapDoc format 9.

## Global Constraints

- OA42 Decisions 1, 2 and 9. One optional `KhaozEngine.MapDoc.Render3D` package, no umbrella addition, no TileWorld adapter and no GPU edge into MapDoc, MapDoc.Physics, Foundation or Server.
- R8a owns native assets, scoped assembly, windowed foliage, terrain/paint, placements/lights, bounded water with today's WaterLook, foliage drawing, residency, roof channels, snapshots, minimal frontends and draw-set comparison.
- R8b owns #1300 optical design, cave sky/lighting/camera policy, #1474 interior source/cut integration, #1511 and the private appearance pack. R8a does not certify their look or implement their algorithms.
- Decision 8. Native CPU world-map source #1370 remains a separate released prerequisite before G3. R9 owns interactive authoring, R10 complete tool parity, R11 import acceptance and G1 to G4 the unshipped game cutover batch.
- Decisions 10 and 24. Verified multipart/material/all-LOD/light closure refuses before upload. Missing assets never produce terrain-only native success. MapDoc remains format 9, prefab payload 1 and foliage payload 1, with no new persisted root field.
- Decision 11. Canonical MapCompiledPatch faces and exact vertices remain the surface at every distance. Partial paint partitions rendering only, never support, physics or navigation.
- Decisions 12 and 13. Hidden roofs have colour=false, shadows=true, camera=false and sky=true. Selection passes hidden hits, physical queries retain them. Cut-aware classification is an explicit R8b extension seam.
- Decisions 14 and 15. One logical owner, every intersecting membership, release on the last membership. Bounds include all LODs, light radius and explicit motion padding. Simulation residency has its own required-domain leases.
- Decisions 16 to 18. Native snapshots carry closure, scope, framing, observer, roofs, time, wind seed, AA and required completeness. Resource readiness precedes temporal warm-up. A capped preview never reports CompleteForScope or WholeWorldComplete.
- Decisions 19 to 21. Original foliage matrices and thinning ranks survive. Explicit plants remain placements. Blade/four shrub styles, agitation and moving shadows are caller policy. Submit every resident emitter, with no nearest-N cap.
- OA9 and OA13. Sparse fixtures span X/Z -32,000 to +32,000 m and Y -500 to +500 m, with 64 m object and camera padding. This is a verification envelope, not a content limit or dense allocation mandate. Precision targets are R8a-P6.
- Preserve MapQueryLimits defaults of 256 patches, 4096 faces, 64 intersections, 32 page reads, 64 record reads and depth 8. Aggregate scene preparation has separate before-work accounting, never a raised point-query allowance.
- Preserve full MapPrefabResolver.Expand, MapWorldBuilder.Build and MapFoliageGenerationContext.Capture refusals on partial documents. No reconstructed partial MapDocument is passed to a complete constructor.
- R7 lessons. Share the derived foliage read reach, rounded binary32 chunk ownership, density dependency reads, Solid-before-Roof precedence and layer-entry invalidations. Cosmetic extents never enter navigation tile height bounds.
- New persisted or hashed text uses MapJsonTextOptions and LF. Existing analytic rendering, compatibility loaders, byte pins and every existing golden remain unchanged.
- New files are at most 800 lines. Baselined files may shrink but never grow without owner approval. New concerns get focused types, not arbitrary file splits. Zero warnings and no blanket suppressions.
- AppWindow alone reads raw devices. Other input uses InputState/InputManager/Pointer. Player text uses StringId/LocalizedText, developer findings use the explicit raw path.
- Public fixtures are invented. No private world bytes, coordinates, names, inventories, content hashes, pixels or reports enter engine git, CI or logs. A missing private input fails, never skips.
- Every local execution build, test, format, render, bake or pack uses wa_run/build-slot. Full three-project sweeps follow each accepted task. One full local Release build/suite runs at the reconciled candidate, with distinct per-project TRX files.
- Wire tests join non-parallel McpWireCollection and use a separate 60 s timeout per call. CPU tests run without a device. Required R8a GPU facts have no capability skip on the three gate backends.
- No local test loops, windows, timing or load experiments. Hosted timing/stress needs separate explicit owner approval. Ordinary backend correctness/bake gates follow the approved execution plan.
- Merge current main on the task branch before final verification. Equipment coverage and TAA own their unrelated appearance changes, which a new native golden must not silently approve.
- R8a adds a package/API and takes a minor. Ride the appropriate minor staged at integration, or take the next free minor if only a patch is staged. Version, CHANGELOG and guarded declarations move together. OA25 release runs through an engine-rooted thread.

## Review Focus

1. An unloaded anchor hides a non-solid prefab, its longer LOD or a light reaching the window. Task 2 adds `ReverseDiscovery_FindsUnloadedOwnerAndEveryEffectiveReach`, Task 8 tests its last-membership lifetime.
2. A coarse support grid reads a fine-grid cell centre outside its apparent halo. Task 3 adds `WindowedFoliage_ThreeLatticesUseOneDerivedReadClosure`, with edge fade both 0 and positive and a moved prefab owner.
3. A stale replacement passes a closure-only check after a scope, policy or source change. Task 8 adds `ReplacementFence_RejectsEveryGenerationBeforeAnyUpload`, including a completed upload waiting for publication.
4. A hidden first roof makes a ray return no result, or one shadow receiver lies outside the camera rectangle. Task 9 adds `HiddenFirstHit_ContinuesPerChannel`, Task 10 adds `CaptureScope_IncludesOffCameraShadowAndLightDependencies`.
5. A reused capture claims readiness after warm-up, or a coarse grid cannot see a missing thin feature. Task 10 adds `ReadyBeforeWarmup_FreshAndReusedAgree`, Task 11 pairs every scene with an omission control and pixel/count assertions.

## Proposed controller rulings

The gate settles the outcome, not these mechanisms. All R8a-P rows are proposals for controller adjudication before owner plan approval. A rejected row is amended in every consuming task before dispatch.

| ID | Proposed choice | Cost if wrong |
| --- | --- | --- |
| R8a-P1 | Build an in-memory reverse metadata index once per source generation. Existing tiled indexes lack placement extents, so stream and verify each occupied tile once, retain metadata only, and refuse an incomplete index. No new persisted sidecar or format field | Startup is O(stored metadata). An external paged index would need its own persisted design and 4 to 8 more days |
| R8a-P2 | Extend NativeMapAssetLoader additively for verified parts/all LODs. Preserve Load and MapAssetManifestAdapter unchanged. Opaque Material resources gain an opt-in closed render-material/1 interpretation in the new package, never a change to headless decoding | Private scaffolds must supply explicit rendering bindings. A different material convention costs 3 to 5 days of adapter/evidence rework |
| R8a-P3 | MapScopedScene is a distinct neutral snapshot, never a falsely complete MapEffectiveResources. Extract shared effective-resolution and roof-intent kernels, keep released full wrappers and their refusal order | Kernel extraction can expose parity defects. Task 2 carries 8 to 12 days including independent regression pins |
| R8a-P4 | Add a separate MapWindowFoliageContext and overloads over a common internal input kernel. Derive acquisition from the released Scope/ReadReach functions once, retaining global metadata knowledge but only scoped payloads | Wrong closure changes samples or refusal phases. Stop the GPU foliage task until exact full/windowed parity passes |
| R8a-P5 | Use the numeric source, preparation, queue, upload and resident ceilings below as conservative charged-byte limits, with injectable smaller test limits. They are defaults, not timing SLAs or total process RSS claims | Over-conservative charges refuse useful scopes. Change requires measured accounting and a recorded ruling, never silent ceiling inflation |
| R8a-P6 | Subtract exact anchors before float submission. Relative terrain/placement/light/water/pick error <=0.0001 m within a 256 m local window. Foliage preserves its released absolute binary32 matrix, with <=0.0001 m extra submission error and <=0.004 m translated near/far position error | Tighter absolute foliage precision would change generation arithmetic and require a new compatibility decision, not a renderer-only fix |
| R8a-P7 | Shared prop services gain additive native matrix/N-tier entry points. LOD thresholds remain caller-supplied and the native adapter keeps all declared tiers. HLOD is keyed and prepared from that same inventory | Broad shared-service changes cost 2 to 4 days. No art, tree LOD setting or legacy overload changes |
| R8a-P8 | Tessellate only bounded free-surface triangles on a world-locked 2 m lattice. Clip displaced water fragments to the original region union, retaining current WaterLook optics | Fine waves may need a smaller caller-selected cell and more charged vertices. Optical correctness remains R8b |
| R8a-P9 | Move generic shape bounds, envelope sweep and ray kernels down to Physics, with released MapDoc.Physics wrappers. The renderer uses the same geometry without adding a MapDoc.Physics edge to the editor | A precision/refusal mismatch costs 3 to 5 days. Frozen released shape results gate the extraction |
| R8a-P10 | A native snapshot request has explicit CompleteScope or Preview mode, Native/Quality/Balanced AA, default 16 temporal warm-up frames and a 1024-operation settle ceiling. Existing two render verbs branch to it, tool count stays 101 | Defaults may need adjustment. Readiness cannot be replaced by more warm-up frames or a sleep |
| R8a-P11 | Six native scene families use fixed public fixtures, 640x480 Native TAA, 16 warm-up frames, 0.01 GoldenCompare tolerance and additional omission-sensitive assertions | Backend noise or an insensitive region requires fixture/assertion redesign, never blessing a missing feature |
| R8a-P12 | Twelve serial tasks, 57 to 90 engineer-days and 70 to 120 elapsed agent hours after plan approval. Reforecast after Tasks 2, 3, 8 and private diagnostics | Backend queues, independent reviews and generator rule mismatches can exceed the upper elapsed forecast |
| R8a-P13 | Private comparison uses independent legacy input extraction, exact keys/counts/channels and 0.00001 m geometry/translation, 0.000001 normal component and rotation-angle tolerances. Streaming duplicate payloads must agree exactly | Any new semantic difference needs a controller rule ruling before attestation. R11 named acceptance is still separate |

## File structure and estimate

Paths are repository-relative. After a directory-qualified filename, following bare filenames in the same Files bullet share that directory. All proposed types/files are created by the named task. Existing ranges identify released symbols, follow the symbol after reconciliation.

| Task | New responsibility and homes | Released integration homes | Engineer-days |
| --- | --- | --- | --- |
| 1 | MapDoc.Render3D project, asset/material decoding, CPU recording | NativeMapAssetLoader, solution, package/dependency catalog, architecture pins | 5 to 8 |
| 2 | MapDoc/Scene scoped source, reverse metadata index and witness | Document source, effective resolver/acquisition, annotation access | 8 to 12 |
| 3 | MapDoc/Foliage window context and exact shared preparation | Generation context/distribution/support, storage packs and effects | 6 to 9 |
| 4 | MapDoc.Render3D canonical terrain and paint batches | Compiled faces/partitions, Render3D material submission | 4 to 6 |
| 5 | MapDoc.Render3D native placements, LOD/HLOD and lights | Shared prop/cluster services and exact local submission | 4 to 7 |
| 6 | MapDoc scoped water compile and bounded render mesh | Water compiler, Scene3D water queue and WaterRenderer | 4 to 6 |
| 7 | MapDoc.Render3D foliage and animated placement routing | GroundCoverRenderer retained batches and motion policy | 3 to 5 |
| 8 | Composite render plans/residency, scoped server coverage | MapDoc.Physics scoped builder and Sharding admission proof | 6 to 9 |
| 9 | Roof channels and presentation picks | MapRoofPolicy, generic Physics query extraction | 4 to 6 |
| 10 | Shared view/snapshot/readiness and minimal native frontends | ViewportWorld, MapEditorScene, RenderService and render wire results | 5 to 8 |
| 11 | Far-origin, allocation and six backend evidence families | Render.Tests, shard coverage, native family artifacts | 4 to 6 |
| 12 | Private comparison/guard contract and living docs | Oracle harness eighth edge, architecture guard, consumer docs | 4 to 8 |
| | Total including reviews, fixes, candidate and integration | Serial producer-before-consumer order | 57 to 90 |

This fits the approved 55 to 90 baseline. Engineer-days measure engineering scope, not eight-hour units of agent runtime. R5 took about 59 h approval to tag, R6 about 56 h, and R7 about 37 h execution against a 70 to 110 h forecast, excluding earlier planning and later release repair. Forecast R8a at 70 to 120 elapsed agent hours after approved-plan execution starts, using 100 to 120 h for scheduling. Capacity, owner responses and CI waits extend calendar time. R8b is excluded.

## Execution mechanics and common contracts

The writer commits only this plan. The controller owns approvals, routing, private work, pushes, reconciliation and release. Workers start at the previous accepted task SHA, implement one task, run its synchronous verification and stop at an explicit-path commit. No dependent task starts with an unresolved load-bearing finding.

Run this setup only during approved execution, in bash. Keep long sweeps in the supported command session, preserve exit status and inspect every TRX. Scratch files stay outside worktrees.

```bash
cd /Users/antonio/KhaozEngine/.worktrees/wa-r8a-native-scene
test "$(git branch --show-current)" = "feature/wa-r8a-native-scene"
test "$(git rev-parse HEAD)" = "$wa_r8a_expected_base"
wa_r8a_log_dir="/tmp/khaozengine-wa-r8a-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p "$wa_r8a_log_dir" local-feed
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
    if build-slot "${lane[@]}" --label "wa-r8a-$n" -- "$@" >"$wa_r8a_log_dir/$n.log" 2>&1
    then rc=0
    else rc=$?
    fi
    tail -25 "$wa_r8a_log_dir/$n.log"
    echo "exit $rc for $n"
    return "$rc"
}
wa_test() {
    wa_run "$1" dotnet test "$2" -c Release -m:1 --logger "trx;LogFileName=$1.trx" --results-directory "$wa_r8a_log_dir" --filter "$3"
}
wa_sweep() {
    local n=$1
    wa_run "$n-mapdoc" --heavy dotnet test "$MAPDOC" -c Release -m:1 --logger "trx;LogFileName=$n-mapdoc.trx" --results-directory "$wa_r8a_log_dir" &&
    wa_run "$n-physics" --heavy dotnet test "$MP" -c Release -m:1 --logger "trx;LogFileName=$n-physics.trx" --results-directory "$wa_r8a_log_dir" &&
    wa_run "$n-editor" --heavy dotnet test "$EDITOR" -c Release -m:1 --logger "trx;LogFileName=$n-editor.trx" --results-directory "$wa_r8a_log_dir"
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

Red means the named assertion fails, or compilation fails solely for a task-owned new API. Broken fixtures, unavailable released APIs, zero matched tests and unrelated failures are not red. Green means exit 0, nonzero matches and zero failures. Existing behavior that already passes is recorded as regression, never fabricated red. After the last code/review fix run wa_sweep once on those exact bytes plus the task's touched Render.Tests filters. Exit 75 means no work ran, retry later. No repetition-until-green loop.

For each task set `wa_r8a_tN_paths` to every full explicit path in its Files block, including approved fix additions. Run `wa_finish tN "${wa_r8a_tN_paths[@]}"`, `git add -- "${wa_r8a_tN_paths[@]}"` and `git commit --only -m "<task subject>" -- "${wa_r8a_tN_paths[@]}"`. No globs, directory-wide staging or unrelated paths. Scoped format follows R-GATE-1/#1485 only after proving the drift is main-owned. The outside-solution harness gets its own project format gate.

No new test project is needed. Neutral source/witness/foliage tests live in MapDoc.Tests, server independence in MapDoc.Physics.Tests, frontend/wire tests in MapEditor.Tests, device-free render composition and GPU facts in Render.Tests, graph checks in KhaozEngine.Tests. Task 1 links invented `Scene/*Fixtures.cs` from MapDoc.Tests into Render.Tests, which gains only the new renderer reference. The existing Physics/Editor fixture links remain. Test namespaces stay KhaozEngine.Tests.*.

Task 2 creates `NativeSceneFixture(MapDocument Document, MapAssetClosure Assets)` and partial `NativeSceneFixtures.Create(string variant) -> NativeSceneFixture`, `Reload(NativeSceneFixture fixture, bool tiled) -> NativeSceneFixture`, `Open(NativeSceneFixture fixture, bool tiled) -> MapSceneSource`. Variants and additions are specified per task. Render-specific synthetic GLBs/materials stay in Task 1's `NativeRenderAssetFixtures.Create(string variant) -> MapAssetClosure`. Fixtures never compute expected results by calling the operation under test.

Default source ceilings are 65,536 tile descriptors, 65,536 effective resource metadata rows, 262,144 total memberships, 4096 memberships per resource, 134,217,728 scanned UTF-8 bytes and 67,108,864 retained metadata bytes. Scan streams one tile at a time. Scope ceilings are 4096 selected resources, 256 acquired patch scopes, 262,144 prepared faces and 67,108,864 prepared bytes, with all intermediate/dependency work charged. No dense extent allocation. Charges are 512 bytes per metadata row plus owned UTF-8/string/array bytes, 16 per membership, 1280 per prepared face and exact copied payload bytes, counted before retention.

### Task 1: Optional package, verified rendering assets and recording seam (5 to 8 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.Render3D/KhaozEngine.MapDoc.Render3D.csproj`, `README.md`, `MapRenderAssets.cs`, `MapRenderMaterials.cs`, `MapRenderMaterialJson.cs`, `map-render-material.schema.json`, `MapRenderRecording.cs`.
- Create: `KhaozEngine.Terrain.Render3D/NativeMapAssetLoader.Parts.cs`, `KhaozEngine.Render.Tests/MapDoc/NativeRenderAssetFixtures.cs`, `MapRenderAssetTests.cs`, `KhaozEngine.Tests/ArchitectureTests.NativeMapRender.cs`.
- Create: `KhaozEngine.Render3D/Models/GltfLoader.StrictMaterialParts.cs`. Modify: `KhaozEngine.Terrain.Render3D/NativeMapAssetLoader.cs:14,27-68` to make the class partial and share validation only, `KhaozEngine.MapDoc/KhaozEngine.MapDoc.csproj:16-25` for a friend-assembly declaration only, `KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj:53-145`, `KhaozEngine.Tests/ArchitectureTests.Foliage.cs:49-59`, `KhaozEngine.slnx:70-74`, `README.md:64-66`, `docs/DEPENDENCY-SEAMS.md:1040-1055`.

**Interfaces:**
- Consumes `MapAssetClosure.Load(IReadOnlyList<MapAssetRef>, IMapAssetSource) -> MapAssetClosure`, `GetAsset(string) -> MapResolvedAsset`, `GetResource(string) -> MapResolvedResource`, `NativeMapAssetLoader.Load(MapResolvedAsset, MapAssetClosure) -> GltfMesh` and `GltfLoader.LoadPartsWithMaterials(ReadOnlyMemory<byte>, string) -> IReadOnlyList<GltfMeshPart>`.
- Produces `NativeMapAssetLoader.LoadParts(MapResolvedAsset asset, MapAssetClosure closure, string? lodResourceId = null) -> IReadOnlyList<GltfMeshPart>`, applying source units once with no recenter or height fitting. Null selects Mesh, a non-null ID must be an asset-declared Lod.
- Produces additive `GltfLoader.LoadPartsWithMaterialsStrict(ReadOnlyMemory<byte> glb, string identity) -> IReadOnlyList<GltfMeshPart>`, sharing the released material/geometry reader but refusing every referenced image that the lenient route would leave unresolved or undecoded. Its errors become MapRenderAssetRefusal at the native adapter boundary.
- Lower Render3D strict reader errors remain InvalidDataException/InvalidOperationException and NativeMapAssetLoader.LoadParts wraps them as MapDocumentException, like its released Load. Only MapRenderAssets translates these to MapRenderAssetStatus/MapRenderAssetRefusal. No lower package references an exception or DTO from MapDoc.Render3D.
- Produces `MapRenderAssetStatus { Complete, Missing, Stale, Unsupported, Invalid, CapacityExceeded }`, `MapRenderAssetLimits(long MaxDecodedBytes = 268435456, int MaxParts = 4096, int MaxLods = 16)`, `MapRenderAssetResult(MapRenderAssetStatus Status, MapRenderAssets? Assets, string? Detail)` and `MapRenderAssets.Load(MapAssetClosure closure, IReadOnlyList<string> assetIds, MapRenderAssetLimits? limits = null) -> MapRenderAssetResult`. Assets exposes `ById: IReadOnlyDictionary<string,MapRenderAsset>`, `Closure: MapAssetClosure`, immutable `MapRenderAsset(string AssetId, IReadOnlyList<MapRenderLod> Lods, MapResolvedAsset Descriptor)` and `MapRenderLod(int Index, string ResourceId, IReadOnlyList<GltfMeshPart> Parts)`, base index 0, declared LODs 1 onward.
- Produces `MapRenderAssetRefusal(MapRenderAssetStatus status, string detail) : Exception` with Status, `MapRenderMaterial(string ResourceId, Vector4 BaseColour, float Roughness, float Metallic, float AlphaCutoff, GltfMaterialMaps Maps)` and `MapRenderMaterials.Load(MapAssetClosure closure, IReadOnlyList<string> materialIds) -> IReadOnlyDictionary<string, MapRenderMaterial>`. Unsupported/malformed required materials raise this refusal before upload, never a null-texture fallback. Numeric terrain IDs are explicitly mapped to these resource IDs by caller policy in Task 4, never inferred from ordinal position.
- Produces `MapRenderKind { Terrain, Placement, Light, Roof, Water, Foliage, Marker }`, `MapRenderKey(MapRenderKind Kind, string OwnerId, string PartId, int Lod)`, flags `MapDrawChannels { None=0, Colour=1, Shadow=2, Camera=4, Sky=8, Selection=16, Physical=32 }`, readonly record struct `MapDrawRecord(MapRenderKey Key, MapDrawChannels Channels, MapSubmissionAnchor Anchor, Matrix4x4 LocalTransform, string GeometryDigest)`, `IMapDrawSink.Write(in MapDrawRecord record) -> void`, `MapDrawRecorder` implementing it and `Snapshot() -> MapDrawSet`, where `MapDrawSet(IReadOnlyList<MapDrawRecord> Draws)` copies its rows.

- [ ] **Write failing tests:** `RenderAdapter_PreservesMeshOriginUnitsPartsEveryLodAndLights`, `RequiredResourceFailure_PrecedesEveryUpload`, `OpaqueCompatibilityLoad_KeepsReleasedBehaviour`, `CpuRecorder_RequiresNoDevice`, `NativeRenderPackage_HasExactOptionalEdges`. Invent a 2-part textured GLB, two declared LODs, source scale 0.01 and a light attachment. Assert actual decoded vertices/material alpha/part order, not only counts.

```csharp
Assert.Equal(3, loaded.Assets!.ById["prop-a"].Lods.Count);
Assert.Equal(2, loaded.Assets.ById["prop-a"].Lods[0].Parts.Count);
Assert.Equal(0.5f, decodedAlphaCutoff);
Assert.Equal(new Vector3(-0.02f, 0.03f, 0.04f), decodedFirstVertex);
Assert.Equal(0, refusedUploadCalls);
Assert.Equal(0, deviceCreationCalls);
Assert.Equal(releasedFlattenedBits, unchangedLoadBits);
```

- [ ] **Run red:** `wa_test t1-red "$RENDER" "FullyQualifiedName~MapRenderAssetTests"` and `wa_test t1-red-graph "$RUMP" "FullyQualifiedName~NativeRenderPackage"`. Expect missing new APIs/graph assertions, with existing compatibility tests passing.
- [ ] **Implement** the Interfaces. Direct project edges are exactly MapDoc, Render3D and Terrain.Render3D. No third-party PackageReference, MapDoc.Physics, editor, TileWorld, backend or umbrella edge. Register README/package readme/solution, add MapDoc.Render3D to the deliberate combined-physics consumer pin because Terrain.Render3D transitively carries Physics. Add only an InternalsVisibleTo name in MapDoc for the new adapter to reuse MapJsonTextOptions, no ProjectReference in that direction. New renderer text/hash writers use that LF home. Architecture tests check direct/transitive non-edges and absence from every umbrella.
- [ ] **Implement strict opt-in decode.** Reuse the GLB byte reader, refuse external URIs and every referenced undecodable image before producing parts. Existing lenient glTF and flattened overloads retain their results. New render-material/1 JSON is closed, with required `schema="kemap/render-material"`, `payloadVersion=1`, `baseColour` four finite 0..1 components, `roughness`, `metallic`, `alphaCutoff` finite 0..1 and required nullable `albedo`, `normal`, `roughnessMap` resource IDs. Texture IDs name verified Material dependencies containing encoded image bytes. Embed the closed schema in the new package, test decoder unknown/duplicate/null/future members here and schema/evaluator parity in Task 10. No filesystem lookup or unverified decoder input. GLB embedded maps remain the prop material authority, declared native materials are decoded/validated and supply terrain/paint bindings. Refuse an unsupported opaque required material only on this opt-in native rendering path. No root/asset schema change.
- [ ] **Bound and detach** all decoded parts/maps before publication, including image dimensions and decoded pixels in MaxDecodedBytes, mesh/index buffers and all LODs. Validate actual mesh extents against declared conservative RenderBounds/LodBounds before using them for discovery. Light bytes remain closure-owned and their typed attachment validation precedes upload. Test missing/corrupt/stale mesh, each LOD, material, texture, collider, selection and light, wrong descriptor/LOD ownership, unsupported material and each limit plus one. Capture disk mutations cannot alter captured closure bytes.
- [ ] **Prevent mutable render payload escape.** GltfMesh vertices and decoded image arrays are mutable. MapRenderAssets owns private deep copies, public mesh/map access returns detached copies and internal prepared/upload paths borrow only that owned state. Test mutation of input GLB buffers, returned part/image arrays and caller collections after Load, asserting unchanged prepared bytes/digests and extent validation. Later policy/snapshot DTOs follow the same rule, including mutable WaterLook/GroundCoverRenderOptions and nested maps/interactors.
- [ ] **Run green:** both filters as green, `wa_test t1-regress "$RENDER" "FullyQualifiedName~NativeMapAssetLoader|FullyQualifiedName~MapAssetManifestAdapter|FullyQualifiedName~Gltf"`, then `wa_sweep t1-final`. Format/guards and explicit-path commit `feat(mapdoc): add optional native render assets and CPU recording`.

### Task 2: Scoped assembly, reverse discovery and completeness (8 to 12 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Scene/MapSceneSource.cs`, `MapSceneMetadataReader.cs`, `MapSceneSpatialIndex.cs`, `MapSceneScope.cs`, `MapScopedScene.cs`, `MapSceneWitness.cs`, `MapSceneLimits.cs`, `MapSceneAcquisition.cs`, `KhaozEngine.MapDoc/Prefabs/MapEffectiveResolutionKernel.cs`.
- Modify: `KhaozEngine.MapDoc/MapDocumentSource.cs:96-178`, `Prefabs/MapPrefabResolver.cs:25-326`, `Prefabs/MapEffectiveResources.cs:16-111`, `Prefabs/MapEffectiveAnnotations.cs:39-45`, `Storage/MapScopedSurfaces.cs:58-78`, only to share generation-pinned metadata/effective acquisition, not relax full entry points.
- Test/create: `KhaozEngine.MapDoc.Tests/Scene/NativeSceneFixtures.cs`, `MapSceneScopeTests.cs`, `MapSceneDiscoveryTests.cs`, `MapSceneCompletenessTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapCompleteBuilderRegressionTests.cs`.

**Interfaces:**
- Consumes `MapDocumentSource.OpenTiled(string, MapDocumentLoadOptions? = null) -> MapDocumentSource`, `FromDocument(MapDocument) -> MapDocumentSource`, `ReadTile(MapTileCoord) -> MapTileContent`, `MapPrefabResolver.Expand(MapDocument, MapAssetClosure, MapPrefabExpansionLimits? = null) -> MapEffectiveResources`, `MapEffectiveResources.Acquire(MapSurfaceScope) -> MapScopedSurfaces` and `MapScopedSurfaces.Acquire(IMapSurfaceSource, MapSurfaceScope, IReadOnlyList<string>, int = 1, int = 1) -> MapScopedSurfaces`.
- Produces `MapSceneChannels` flags Terrain=1, Placements=2, Water=4, Annotations=8, Foliage=16, Rooms=32, All=63, `MapSceneScope(MapSurfaceScope Surfaces, MapBox3 DrawBounds, MapSceneChannels Channels, double MotionPaddingMetres=0)` and `MapSceneStatus { CompleteForScope, WholeWorldComplete, Incomplete, Missing, Corrupt, Stale, CapacityExceeded, NotRepresentable, Invalid }`. Padding is finite/nonnegative, explicit in scope identity and conservative discovery, never an implicit unknown-motion default.
- Produces `MapSceneLimits(int MaxTiles=65536, int MaxMetadataRows=65536, int MaxMemberships=262144, int MaxMembershipsPerResource=4096, long MaxScannedBytes=134217728, long MaxMetadataBytes=67108864, int MaxSelectedResources=4096, int MaxPatchScopes=256, int MaxPreparedFaces=262144, long MaxPreparedBytes=67108864)`, `MapSceneWork(long TileReads, long MetadataRows, long Memberships, long ScannedBytes, long RetainedBytes, long PreparedFaces)` and `MapSceneSourceStatus { Ready, Incomplete, Missing, Corrupt, Stale, CapacityExceeded, NotRepresentable, Invalid }`. `MapSceneSourceResult(MapSceneSourceStatus Status, MapSceneSource? Source, MapSceneWork Work)` distinguishes a ready metadata index from world completeness.
- Produces `MapSceneSource.Capture(MapDocumentSource source, MapAssetClosure assets, MapSceneLimits? limits = null) -> MapSceneSourceResult`, readonly SourceGeneration/ClosureHash/RequiredAssetIds and `Acquire(MapSceneScope scope) -> MapScopedSceneResult`. `MapScopedSceneResult(MapSceneStatus Status, MapScopedScene? Scene, MapSceneWitness Witness, MapSceneWork Work)` has no usable Scene on refusal. RequiredAssetIds includes every metadata-referenced placement and foliage model across the source, enabling complete extent validation before any native rendering witness is accepted.
- Produces `MapSceneResourceKey(string Kind, string Id)`, `MapSceneResourceMetadata(MapSceneResourceKey Key, MapTileCoord StorageOwner, MapBox3 Bounds, string Digest)`, `MapSceneWitness(string ClosureHash, string SourceGeneration, string ScopeDigest, IReadOnlyList<MapSceneResourceKey> Required, IReadOnlyList<MapSceneResourceKey> Present, IReadOnlyList<MapSceneResourceKey> KnownEmpty, IReadOnlyList<MapSceneResourceKey> Unavailable, bool CompleteForScope, bool WholeWorldComplete)` and immutable `MapScopedScene` properties Scope, Witness, Surfaces, Placements, Transforms, PaintRegions, Water, BuildingIntent and Foliage. Types of those resource properties match their released MapEffectiveResources/document counterparts, with no complete-resource claim. Add `MapEffectiveAnnotations.Lights(MapScopedScene) -> IReadOnlyList<MapEffectiveLight>` and Markers overload likewise.

- [ ] **Write failing tests:** `ReverseDiscovery_FindsUnloadedOwnerAndEveryEffectiveReach`, `MissingUnloadedAndKnownEmpty_AreDifferentObservations`, `ScopedAndFullEffectiveRules_AgreeWithoutPartialDocument`, `MetadataAdmission_RefusesBeforeRetention`, `UnrelatedGrowth_DoesNotGrowWindowAcquisition`. Synthetic spanning owner is anchored at X=-65, touches windows [-64,0] and [0,64], has non-solid child, a longer LOD and a light whose radius reaches the second window. Test both storage forms, negative seams, support-bound Y and moved prefab ownership.

```csharp
Assert.Contains(scene.Placements, p => p.PlacementId == "spanning-leaf");
Assert.Contains(lights, l => l.Id == "spanning-light");
Assert.True(result.Witness.CompleteForScope);
Assert.False(result.Witness.WholeWorldComplete);
Assert.Equal(MapSceneStatus.Incomplete, unloaded.Status);
Assert.Empty(unloaded.Witness.KnownEmpty);
Assert.Null(overBudget.Source);
Assert.Equal(0, refusedRetainedRows);
Assert.Equal(nearWork, nearWorkWith128DistantOwners);
```

- [ ] **Run red:** `wa_test t2-red "$MAPDOC" "FullyQualifiedName~MapSceneScopeTests|FullyQualifiedName~MapSceneDiscoveryTests|FullyQualifiedName~MapSceneCompletenessTests"` and `wa_test t2-red-full "$MP" "FullyQualifiedName~MapCompleteBuilderRegressionTests"`.
- [ ] **Implement** one generation-pinned metadata scan, verifying tile hashes and manifest generation before/after reads. Monolithic input snapshots metadata once. Tiled input streams placements and authored global/prefab metadata without loading surface payloads or retaining sculpt arrays. Count skipped tokens/bytes too. Missing off-window owner metadata prevents an attested reverse index, not an empty window. Root surface directories and foliage pack directories retain advertised/absent knowledge. No inferred placement bounds from Kind/height or static-only inventory.
- [ ] **Build** a stable median-split bounds hierarchy, leaf size 8, over conservative effective XZ extents. Before support seating, Y is conservatively unbounded for discovery. Include parent/child transforms, all LODs, light reach, room/paint dependencies and caller motion padding when later supplied. Metadata bounds use verified descriptors, Task 1 validates actual render bytes before upload. Wide objects stay in the hierarchy, never a globally scanned overflow list. Window work counts visited nodes and selected dependencies.
- [ ] **Derive conservative generated bounds** from each density chunk's world ownership rectangle, every selectable model's source-unit bounds, ScaleMax, RootOffset and all possible support-aligned orientations. Query the reverse hierarchy against DrawBounds widened by the explicit motion allowance, then acquire the exact generated/padded subresources. Never use a density rectangle alone as final render bounds. A later renderer request must cover its largest caller motion padding or refuse before a completeness claim. Verify a blade/shrub reaching a window while its density chunk lies outside it.
- [ ] **Extract shared resolution** into MapEffectiveResolutionKernel over explicit selected owner/record inputs and a generation-pinned surface source. Full Expand still preflights and refuses partial documents before calling it. Scoped acquisition computes a bounded dependency fixed point for seats, transformed local floors, parent spaces, paint targets and roof groups. No omitted owner is interpreted as absent. A capacity or stale observation discards the provisional snapshot. Preserve released transform, numeric identity, support/refusal precedence and paint rules. Only a checked whole-source coverage witness may set WholeWorldComplete.
- [ ] **Run green:** red filters as green, `wa_test t2-regress "$MAPDOC" "FullyQualifiedName~MapPrefab|FullyQualifiedName~SupportQuery|FullyQualifiedName~MapStoredSurface|FullyQualifiedName~MapRoof"`, `wa_sweep t2-final`. Reforecast. Format/guards and explicit-path commit `feat(mapdoc): acquire bounded native scenes with completeness witnesses`.

### Task 3: Windowed foliage before GPU foliage upload (6 to 9 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Foliage/MapWindowFoliageContext.cs`, `MapFoliageWindowCapture.cs`, `IMapFoliageInputs.cs`, `KhaozEngine.MapDoc.Tests/Foliage/MapWindowFoliageTests.cs`, `WindowFoliageFixtures.cs`.
- Modify: `KhaozEngine.MapDoc/Foliage/MapFoliageGenerationContext.cs:19-126,260-310`, `MapFoliageDistribution.cs` Generate/Query/evaluation entries, `MapFoliageSupport.cs:58-176`, `MapFoliageClearance.cs` preparation entry, Task 2 scene acquisition for foliage dependency scopes only.

**Interfaces:**
- Consumes `MapFoliageGenerationContext.Capture(MapDocument, MapAssetClosure, MapFoliageContextLimits? = null) -> MapFoliageContextResult`, `MapFoliageSupport.Prepare(MapFoliageGenerationContext, MapFoliageLayer, MapFoliageChunkKey, MapFoliageLimits) -> MapPreparedFoliageSupport`, `MapFoliageDistribution.Generate(MapFoliageGenerationContext, string, MapFoliageChunkKey, MapFoliageLimits? = null, Action<MapFoliageDecision>? = null) -> MapFoliageSampleBatch`, Query with RectArea in place of chunk returning MapFoliageQueryResult, and Task 2 source/witness.
- Produces `MapWindowFoliageContextResult(MapFoliageStatus Status, MapWindowFoliageContext? Context, MapFoliageContextWork Work)`, additive `MapFoliageGenerationContext.CaptureWindow(MapSceneSource source, MapSceneScope scope, MapFoliageContextLimits? limits = null) -> MapWindowFoliageContextResult`, and readonly SnapshotId, ClosureHash and Witness on MapWindowFoliageContext.
- Produces `MapFoliageDistribution.Generate(MapWindowFoliageContext context, string layerId, MapFoliageChunkKey chunk, MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null) -> MapFoliageSampleBatch` and Query overload with RectArea in place of chunk returning MapFoliageQueryResult. Internal IMapFoliageInputs unifies acquisition, paint, owner/rule lookup and density reads without exposing a fake complete Resources property.

- [ ] **Write failing tests:** `FullAndWindowedFoliage_MatchEveryKeyMatrixRankAndDigest`, `WindowedFoliage_ThreeLatticesUseOneDerivedReadClosure`, `WindowedFoliage_MovedPrefabAndNegativeSeamsKeepOwnership`, `WindowedFoliage_AbsentZeroDiffersFromMissingPack`, `WindowedFoliage_BudgetAndStaleRefusalsPublishNothing`. Use released FoliageFixtures, SupportFixtures.EightStacked and PrefabFixtures.Cave("stacked") with invented decorations. Compare four independent heads, two storage forms, reordered windows and half-open rounded chunk edges.

```csharp
Assert.Equal(fullKeys, streamedKeys);
Assert.Equal(fullMatrixBits, streamedMatrixBits);
Assert.Equal(fullRankBits, streamedRankBits);
Assert.Equal(fullDependencyDigests, streamedDependencyDigests);
Assert.Equal(MapFoliageStatus.Unavailable, missingPack.Status);
Assert.Empty(missingPack.Samples);
Assert.Equal(0, perChunkWholeExpansions);
Assert.Equal(0, perChunkPayloadSerializations);
```

- [ ] **Run red:** `wa_test t3-red "$MAPDOC" "FullyQualifiedName~MapWindowFoliageTests"`. Expect unsupported window entry or parity/refusal failures, not failure of the released complete context.
- [ ] **Implement** the common input kernel and window capture, using Task 2 metadata knowledge and the exact source/closure generations. Reuse Scope/ReadReach from MapFoliageSupport, including normal corners, own cell centres, neighbour centres and their centres across mixed 4 m, 2 m and 0.5 m grids under yaw 0.371 and scale 1.2. Include moved rule/mask owners, edge fade 0/2, partial paint and parent-space boundaries. Derive this once, no separate ad hoc halo in the window adapter.
- [ ] **Pin** rounded binary32 generation ownership and actual bilinear raster dependencies, advertised absent zero chunks, off-raster raster-only digests, missing/corrupt packs and atomic refusal. Layer effects supersede chunk rows and invalidate all retained layer generations, including unchanged stored density with changed support. Unrelated source growth cannot add per-chunk Expand/serialization or change scoped work. Existing per-point refusal order and aggregate context/query retention caps remain. No partial document reconstruction or whole-world payload capture.
- [ ] **Run green:** red filter as green, `wa_test t3-regress "$MAPDOC" "FullyQualifiedName~MapFoliage|FullyQualifiedName~FoliagePlatformPins|FullyQualifiedName~MapPreparedSupportParity"`, then `wa_sweep t3-final`. Reforecast. Format/guards and explicit-path commit `feat(mapdoc): generate windowed foliage from complete dependency scopes`.

### Task 4: Canonical terrain, ceilings and partial paint (4 to 6 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.Render3D/NativeMapTerrain.cs`, `MapTerrainRenderBatch.cs`, `MapRenderTransform.cs`, `KhaozEngine.Render3D/RenderAnchor.cs`, `Scene3D.Anchored.cs`, `KhaozEngine.Render.Tests/MapDoc/NativeMapTerrainTests.cs`.
- Modify: `KhaozEngine.Render3D/Scene3D.RenderOrigin.cs:190-235` relative instance upload and existing Scene3D rigid queue hooks by replacement without baseline growth, preserving legacy draws.
- Test/extend: `KhaozEngine.MapDoc.Tests/Scene/NativeSceneFixtures.cs` with cuts, paint and ceiling variants. No analytic or TileWorld mesher changes.

**Interfaces:**
- Consumes `MapSurfaceCompiler.Compile(MapSurfaceRef, MapSurfacePatch) -> MapCompiledPatch`, `MapCompiledPatch.ExactTriangle(MapCompiledFace) -> MapExactTriangle`, MapCompiledPatch.Anchor/Offsets/Faces/Paint, MapPaintRegionSet.Regions and Task 2 acquired surfaces/effective transforms.
- Produces `MapFrameState(WorldFrame Frame, MapFramePoint Observer, MapRoofMode RoofMode, double TimeSeconds, int WindSeed, Vector3 Focus)`, `MapRenderTransform.ToLocal(MapEffectiveTransform pose, MapSubmissionAnchor anchor) -> Matrix4x4`, `NativeMapTerrain.Prepare(MapScopedScene scene, IReadOnlyDictionary<string, MapRenderMaterial> materials, IReadOnlyDictionary<ushort,string> materialIds) -> MapTerrainRenderBatch`. Batch exposes canonical face keys, exact triangles, render partitions/material provenance, charged bytes and `Record(MapFrameState frame, IMapDrawSink sink) -> void`. Material ID zero follows canonical no-material semantics, every used nonzero ID requires a verified explicit mapping.
- Produces renderer-neutral `RenderAnchor(long X, long Y, long Z)` and `Scene3D.DrawAnchored(in RigidInstanceDraw draw, RenderAnchor anchor) -> void`. Draw.World is local to that anchor. Scene3D reduces anchor minus current render origin in binary64 before float conversion exactly once, retaining the anchor for temporal previous-origin and shadow submissions. No MapDoc type enters Render3D. Task 5 maps MapSubmissionAnchor to this value, never re-adds an absolute float translation.

- [ ] **Write failing tests:** `DistanceNeverChangesCanonicalSurface`, `PartialPaint_PartitionsOnePhysicalFaceWithDeterministicWinner`, `CeilingSidednessAndLocalAnchor_AreExact`, `AbsentMaterial_RefusesBeforeTerrainUpload`. Invent two canonical triangles with material 10, feathered overlay 11, transformed partial paint 12 and a ceiling. Equal compatible priority ties use ascending ordinal paint ID, conflicting equal-priority overlap keeps its released refusal.

```csharp
Assert.Equal(nearFaceKeys, farFaceKeys);
Assert.Equal(canonicalExactTriangles, recordedCanonicalTriangles);
Assert.Equal("paint-a", equalPriorityWinner);
Assert.Equal(2, physicalSupportFaceCount);
Assert.True(allRenderPartitionsLieOnTheirCanonicalFace);
Assert.True(ceilingVisibleFromBelow);
Assert.InRange(maxLocalSubmissionError, 0d, 0.0001d);
```

- [ ] **Run red:** `wa_test t4-red "$RENDER" "FullyQualifiedName~NativeMapTerrainTests"`.
- [ ] **Implement** exact-anchor conversion and render-only triangulation of paint partitions. Subtract anchor in exact arithmetic before float conversion. Retain face provenance, base coverage, feather and deterministic winner. Draw no second coplanar support surface and introduce no distance topology simplification. Ceiling faces use canonical inward sidedness, walls retain their normal, colour and shadow use the same geometry. Material zero/no-underlay follows released coverage, never an invented default material.
- [ ] **Run green:** red filter as green, `wa_test t4-regress "$RENDER" "FullyQualifiedName~SplatTerrain|FullyQualifiedName~TileGroundGeometry"`, then `wa_sweep t4-final`. Format/guards and explicit-path commit `feat(mapdoc): draw canonical terrain and exact partial paint`.

### Task 5: Effective placements, every LOD, shared clusters and lights (4 to 7 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.Render3D/NativeMapPlacements.cs`, `MapPlacementRenderBatch.cs`, `NativeMapLights.cs`, `KhaozEngine.Terrain.Render3D/PropNativeInstances.cs`, `PropRenderer.Native.cs`, `PropClusterRenderer.Native.cs`, `NativePropClusterCpuBuild.cs`.
- Modify: `KhaozEngine.Terrain.Render3D/PropRenderer.cs:31,226-249,362-397`, `PropClusterRenderer.cs:26,76-129,205-253`, `PropLayer.cs:180-198,302-342` for additive shared kernels, and Task 4 Scene3D.Anchored for local lights. Existing overloads keep their defaults/results.
- Test/create: `KhaozEngine.Render.Tests/MapDoc/NativeMapPlacementTests.cs`, `NativeMapLightTests.cs`, extend `NativeSceneFixtures.cs` with a transformed building and 17 emitters.

**Interfaces:**
- Consumes `MapEffectiveAnnotations.Lights(MapEffectiveResources) -> IReadOnlyList<MapEffectiveLight>` and Task 2 scoped overload, `PropClusterRenderer.BuildCpu(PropClusterBuildRequest) -> PropClusterCpuBuild`, `Apply(PropClusterKey, PropClusterCpuBuild) -> void`, `Draw(Vector3) -> void`, `PropHlod.CrossfadeAt(float, float, float) -> float`.
- Produces `MapPlacementLayer(string Id, IReadOnlySet<string> AssetIds, IReadOnlyList<float> LodDistances, float LodCrossfadeWidth, float DrawRadius, float HlodDistance, float HlodCrossfadeWidth, float HlodWeldCell, bool CastsShadows)` and `MapPlacementPolicy(IReadOnlyList<MapPlacementLayer> Layers, float DefaultDrawRadius)`, copied/frozen with unique layer IDs, disjoint selected asset sets, increasing positive thresholds and 0 HLOD meaning disabled. Unselected ordinary props use base geometry/default radius. Empty thresholds preserve caller `LodDistance=0`. Shared Terrain.Render3D produces `NativePropInstance(string Id, long? NumericId, string AssetId, Matrix4x4 LocalTransform, RenderAnchor Anchor)` and `NativePropLod(int Index, IReadOnlyList<MeshHandle> Parts)`.
- Produces `NativeMapPlacements.Prepare(MapScopedScene scene, MapRenderAssets assets, MapPlacementPolicy policy) -> MapPlacementRenderBatch`, readonly Inventory and `Record(MapFrameState, IMapDrawSink) -> void`. Shared `PropRenderer.DrawNative(Scene3D scene, IReadOnlyList<NativePropInstance> instances, IReadOnlyDictionary<string,IReadOnlyList<NativePropLod>> lods, Vector3 focus, float drawRadius, IReadOnlyList<float> lodDistances, float crossfadeWidth) -> int` uses the existing emission/selection kernel and DrawAnchored. Its signature contains no type from MapDoc.Render3D.
- Produces shared `NativePropClusterRequest(PropClusterKey Key, long Generation, RectArea Area, PropLayer Layer, IReadOnlyList<NativePropInstance> Instances, RenderAnchor Anchor)`, immutable `NativePropClusterCpuBuild` retaining Request, merged GltfMesh and represented IDs, `PropClusterRenderer.BuildNativeCpu(NativePropClusterRequest request) -> NativePropClusterCpuBuild` and additive `Apply(PropClusterKey key, NativePropClusterCpuBuild build) -> void`. Shared native clusters keep their anchor through draw, reuse PropHlod merging/crossfade kernels and introduce no reverse dependency into the new upper package.
- Produces `NativeMapLights.Record(MapScopedScene scene, MapFrameState frame, IMapDrawSink sink) -> void`, with native light identity and full descriptor inventory. The device sink submits the same roster to Scene3D, including shadow intent and fixture owner exclusion.
- Produces shared `Scene3D.AddLightAnchored(Vector3 localPosition, RenderAnchor anchor, Color colour, float radius, float intensity, LightShadow shadow) -> void`, reducing the exact anchor at light/point-shadow binding rather than assembling an absolute float position. Released AddLight overloads remain unchanged.

- [ ] **Write failing tests:** `EffectivePlacementPartsAndEveryLod_TransformOnce`, `OrdinaryClusterAndAnimatedRoutes_AreMutuallyExclusive`, `FullEmitterRoster_UsesEffectiveTransformsWithoutNearestCap`, `NativeHlod_FallbackAndCrossfadeKeepOneIdentity`. Building pose is (-1.25,2.5,3.75), yaw 0.371, scale 1.2, source units 0.01. Pin local parts, both LODs, bounds and exact resolved identities, NumericId, Kind and ordered tags separately.

```csharp
Assert.Equal(17, recordedLightIds.Distinct().Count());
Assert.Equal(effectiveLightDescriptors, recordedLightDescriptors);
Assert.Equal(1, logicalOwnersForPlacement);
Assert.Equal(new[] { 0, 1, 2 }, availableLodIndices);
Assert.Equal(expectedOnceComposedMatrix, recordedMatrix);
Assert.Equal(individualIdentitySet, hlodRepresentedIdentitySet);
```

- [ ] **Run red:** `wa_test t5-red "$RENDER" "FullyQualifiedName~NativeMapPlacementTests|FullyQualifiedName~NativeMapLightTests"`.
- [ ] **Implement** native instance adapters to the shared prop emitter and cluster CPU build, not another culling/LOD algorithm. Support N declared tiers and complementary crossfades in colour/shadows. All source parts remain available. HLOD merges per native layer/cluster key, preserves represented IDs, and falls back to individual geometry until accepted. Caller thresholds control selection, no new tree/art tuning. Do not feed absolute rounded translations into PropPlacement and then try to recover local precision.
- [ ] **Pin routing policy** with one selected tree layer, an unselected building, a zero-LOD tree control and an explicit animated shrub. Require exactly one route for each placement and reject overlapping selected asset sets. A layer's nonempty threshold count must equal each selected asset's declared LOD count, otherwise refuse the policy rather than silently dropping a tier. Animated policy takes its declared assets out of ordinary/cluster submission once, while preserving their placement inventory.
- [ ] **Implement** every effective light once, with exact local position, scaled radius, colour/intensity/shadow intent and owner exclusion. All resident lights submit, cluster capacity failures are typed findings rather than nearest-N truncation. Bounds union all declared LODs and light reach. Explicit animated IDs are classified once and held for Task 7, never emitted by ordinary or cluster routes. Markers retain inventory identity with no invented visible 3D labels.
- [ ] **Run green:** red filter as green, `wa_test t5-regress "$RENDER" "FullyQualifiedName~PropRenderer|FullyQualifiedName~PropCluster|FullyQualifiedName~PropHlod|FullyQualifiedName~PointLightCluster"`, then `wa_sweep t5-final`. Format/guards and explicit-path commit `feat(mapdoc): compose native placements LODs and complete light rosters`.

### Task 6: Bounded native water with current optics (4 to 6 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc/Water/MapWaterScopedCompiler.cs`, `KhaozEngine.MapDoc.Render3D/NativeMapWater.cs`, `MapWaterRenderBatch.cs`, `KhaozEngine.Render3D/WaterSurfaceMesh.cs`, `Scene3D.BoundedWater.cs`, `Rendering/WaterRenderer.Bounded.cs`, `Internal/ShaderSources.BoundedWater.cs`.
- Modify: `KhaozEngine.MapDoc/Water/MapWaterCompiler.cs:26-40,46-90` through a shared compile kernel, `KhaozEngine.Render3D/Scene3D.cs:1059-1067,1622-1632` and Begin queue clearing by replacing existing blocks with focused helper calls without baseline growth, `Rendering/WaterRenderer.cs:1-730` only its queue/draw hook, `Internal/ShaderSources.Water.cs` bounded-path clip input.
- Test/create: `KhaozEngine.MapDoc.Tests/Scene/MapScopedWaterTests.cs`, `KhaozEngine.Render.Tests/MapDoc/NativeMapWaterTests.cs`.

**Interfaces:**
- Consumes `MapWaterCompiler.Compile(MapScopedSurfaces, MapWaterSet, MapWaterCompileLimits? = null) -> IReadOnlyList<MapCompiledWaterBody>`, MapCompiledWaterBody.Regions, MapWaterRegion.Kind/Triangles, `Scene3D.DrawWater(in WaterPlane) -> void` and current WaterLook.
- Produces `MapWaterScopedCompiler.Compile(MapScopedScene scene, MapWaterCompileLimits? limits = null) -> MapScopedWaterResult`, `MapScopedWaterResult(MapSceneStatus Status, IReadOnlyList<MapCompiledWaterBody> Bodies, MapSceneWitness Witness)`. Bodies are complete for the clipped requested domain, with original body/space/fragment identity. Full-body Compile remains unchanged.
- Produces `NativeMapWater.Prepare(MapScopedWaterResult water, WaterLook look, float cellMetres = 2f) -> MapWaterRenderBatch`, with immutable FreeSurface inventory/clip geometry, charged vertices/bytes and `Record(MapFrameState frame, IMapDrawSink sink) -> void`. Produces renderer-neutral `WaterSurfaceMesh(MeshHandle Mesh, RenderAnchor Anchor, Matrix4x4 LocalTransform, WaterLook Look)` and `Scene3D.DrawWaterSurface(in WaterSurfaceMesh surface) -> void`. The bounded route owns its original-domain clip buffer with the mesh. R8b replaces optical inputs behind this bounded geometry submission, not its domain/identity contract.

- [ ] **Write failing tests:** `BentWaterBody_AreaScaledWorkAndAuthoredShapeSurvive`, `ScopedWater_ProvesOnlyItsRequestedColumns`, `ClosedUnresolvedAndMissingWater_NeverBecomeVisibleOcean`, `LegacyWaterPlane_UnusedNativePathIsIdentical`. Invent a 16-cell bent body split into 1 versus 16 source pieces, a dry notch, two vertical domains and a boundary crossing. Compare covered area and emitted union, not rectangle count.

```csharp
Assert.Equal(16d, unionAreaSquareMetres);
Assert.Equal(singlePieceVertexCount, sixteenPieceVertexCount);
Assert.True(allDrawTrianglesInsideOriginalFreeSurface);
Assert.Equal(0, closedRegionDraws);
Assert.Equal(MapSceneStatus.Incomplete, missingColumn.Status);
Assert.False(missingColumn.Witness.CompleteForScope);
Assert.Equal(oldWaterPlaneDrawCommands, unusedNativeRouteDrawCommands);
```

- [ ] **Run red:** `wa_test t6-red "$MAPDOC" "FullyQualifiedName~MapScopedWaterTests"`, `wa_test t6-red-render "$RENDER" "FullyQualifiedName~NativeMapWaterTests"`.
- [ ] **Implement** a scoped compiler entry over the same containment/refinement rules, clipping to scope before bound reads. Preserve Closed/Unresolved distinctions and refuse incomplete required columns. This is the #1360 scope needed by rendering. No per-frame point/column sampler is introduced, so #1358 indexing remains a named future consumer dependency, not a linear scan hidden in Update/Draw.
- [ ] **Implement** union-aware world-locked tessellation, deduplicating shared cells/vertices before upload. Charge candidate lattice cells and clipping pairs before traversal, cap 262,144 cells and 1,048,576 emitted vertices per plan. Geometry cost depends on covered cells plus boundary complexity, not one full grid per input piece. Current WaterLook waves/shading remain, original-domain clipping prevents displaced fragments filling dry notches. New path alone binds its clip data. No horizon continuation, camera-driven level, global sea fallback or body-local optical-depth claim.
- [ ] **Run green:** both filters as green, `wa_test t6-regress "$RENDER" "FullyQualifiedName~Water|FullyQualifiedName~GoldenTileWorld"`, then `wa_sweep t6-final`. Existing water goldens at the Task 11/backend gate prove pixel preservation, the command assertion here is CPU wiring only. Format/guards and explicit-path commit `feat(mapdoc): render bounded native water with current WaterLook`.

### Task 7: Exact foliage upload and caller motion policy (3 to 5 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.Render3D/NativeMapFoliage.cs`, `MapNativeFoliageBatch.cs`, `NativeFoliagePolicy.cs`, `MapFoliageRenderCache.cs`, `KhaozEngine.Render.Tests/MapDoc/NativeMapFoliageTests.cs`.
- Modify: `KhaozEngine.Terrain.Render3D/GroundCoverRenderer.cs:70-114` only for a frame-local retained-batch overload, its `GroundCoverGpuBindings.cs` local-anchor binding, Task 5 explicit-plant route.

**Interfaces:**
- Consumes `MapFoliageRenderHandoff.Create(MapFoliageSampleBatch, IReadOnlyList<MapFoliageModelExtent>) -> MapFoliageRenderBatch`, `GroundCoverRenderer.DrawGroundCover(Scene3D, IReadOnlyList<GroundCoverInstance>, IReadOnlyDictionary<string,IReadOnlyList<MeshHandle>>, Vector3, GroundCoverRenderOptions) -> int` and `ReleaseGroundCover(Scene3D, IReadOnlyList<GroundCoverInstance>) -> void`.
- Produces immutable `NativeFoliagePolicy(GroundCoverRenderOptions options, IReadOnlyDictionary<string, MapFoliageModelExtent> extents, IReadOnlySet<string> animatedPlacementAssets)` copying options/maps/interactors, `NativeMapFoliage.Prepare(MapScopedScene scene, MapWindowFoliageContext context, MapRenderAssets assets, NativeFoliagePolicy policy) -> MapNativeFoliageBatch`. Batch exposes generated and explicit identities separately, padded bounds, generation keys and Record(MapFrameState, IMapDrawSink).
- Produces additive `GroundCoverRenderer.DrawGroundCover(Scene3D scene, GroundCoverBatch cover, IReadOnlyDictionary<string,IReadOnlyList<MeshHandle>> meshes, Vector3 focus, GroundCoverRenderOptions options, RenderAnchor anchor) -> int`. The cached batch contains original matrices and frame-local submission copies, the shared renderer never consumes MapFrameState or another upper-package type.

- [ ] **Write failing tests:** `FoliageUpload_UsesOriginalMatrixAndRank`, `ExplicitAnimatedPlants_DrawOnceOutsideDensityInventory`, `MotionStylePadding_ChangesUploadNotDistribution`, `LayerEffectsInvalidateEveryResidentBatch`. Use fixed Blade plus four distinct Shrub profiles, moving-shadow true/false and 0.35 m padding. Preserve sample matrix bits and keys before any GPU conversion.

```csharp
Assert.Equal(sourceMatrixBits, retainedOriginalMatrixBits);
Assert.Equal(sourceRankBits, uploadedRankBits);
Assert.Equal(1, explicitPlantLogicalDraws);
Assert.Equal(beforeDistributionDigest, afterStyleDistributionDigest);
Assert.NotEqual(beforeRenderDigest, afterPaddingRenderDigest);
Assert.Equal(5, frozenMotionProfiles.Count);
Assert.Equal(0, generationCallsOnDrawThread);
```

- [ ] **Run red:** `wa_test t7-red "$RENDER" "FullyQualifiedName~NativeMapFoliageTests"`.
- [ ] **Implement** direct handoff-to-GroundCoverBatch adaptation, with original matrix/rank retained and exact anchor subtraction only at submission. No quaternion reconstruction, double source-unit application or resampling. Distribution cache key is dependency digest, upload key is render digest plus generation and closure. Explicit animated plants retain authored placement IDs, per-model style/shadow policy and a single routing owner. CPU fallback must report unsupported animated policy rather than silently losing profiles. Zero motion padding is valid only when explicitly supplied.
- [ ] **Run green:** red filter as green, `wa_test t7-regress "$RENDER" "FullyQualifiedName~GroundCover|FullyQualifiedName~FoliageProfile|FullyQualifiedName~FoliageMotion|FullyQualifiedName~FoliageShadow"`, then `wa_sweep t7-final`. Format/guards and explicit-path commit `feat(mapdoc): upload native foliage with exact samples and shared style policy`.

### Task 8: Composite residency, budgets and independent cell coverage (6 to 9 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.Render3D/MapRenderPlan.cs`, `MapRenderInventory.cs`, `MapRenderPolicy.cs`, `MapRenderResidency.cs`, `MapRenderBuildQueue.cs`, `MapRenderBudgets.cs`, `MapRenderLifetime.cs`, `IMapRenderDevice.cs`, `KhaozEngine.MapDoc/Scene/MapRequiredDomainCoverage.cs`, `KhaozEngine.Render3D/Scene3D.IncrementalUpload.cs`, `SceneResourceUpload.cs`, `SceneUploadData.cs`, `KhaozEngine.Terrain.Render3D/GroundCoverRenderer.Prepared.cs`.
- Create: `KhaozEngine.MapDoc.Physics/MapScopedWorldBuilder.cs`, `MapSimulationCoverage.cs`, `KhaozEngine.Render.Tests/MapDoc/NativeMapResidencyTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapSimulationCoverageTests.cs`, `NativeCellHandoffFixtures.cs`.
- Modify: `KhaozEngine.MapDoc.Physics/MapWorldBuilder.cs:42-122` to share static preparation with the scoped builder, not relax Build, `MapNavTiling.cs:75-205` for additive scoped descriptor overloads, and Task 2 metadata for all-LOD/motion-pad memberships. No renderer reference in Physics.Tests.

**Interfaces:**
- Consumes Tasks 2 to 7 batches/witnesses, `MapResidencyOwnership.Build(MapBuiltWorld, MapWorldGrids) -> IReadOnlyList<MapResidencyEntry>`, BuildWater/BuildAnnotations with the same parameters/result, `ShardHost.SyncGhosts() -> void`, `ProcessHandoffs() -> void`, `OwnerCount(long) -> int`, and released ICellImportAdmission.
- Produces immutable `MapRenderPolicy(MapPlacementPolicy Placements, NativeFoliagePolicy Foliage, WaterLook Water, IReadOnlyDictionary<string,MapRenderMaterial> Materials, IReadOnlyDictionary<ushort,string> TerrainMaterials)` with copied policy digest, `MapRenderPlanKey(string ClosureHash, string SourceGeneration, string ScopeDigest, string PolicyDigest, long Generation)` and `MapRenderPlan.Prepare(MapSceneSource source, MapScopedScene scene, MapRenderAssets assets, MapRenderPolicy policy, long generation) -> MapRenderPlan`. Plan exposes Key, Assets, Scene, Inventory and `Record(MapFrameState frame, IMapDrawSink sink) -> void`. `MapRenderInventory(MapSceneWitness Witness, MapTerrainRenderBatch Terrain, MapPlacementRenderBatch Placements, IReadOnlyList<MapEffectiveLight> Lights, MapWaterRenderBatch Water, MapNativeFoliageBatch Foliage, IReadOnlyList<MapEffectiveMarker> Markers)` retains CPU-readable source descriptors, not only hashes or GPU handles. Preparation creates the Task 3 window context against the same source/witness, compiles Task 6 water and constructs each batch exactly once.
- Produces `MapRenderBudgets(int MaxWorkers=2, int MaxQueuedPlans=32, long MaxQueuedBytes=67108864, int MaxAppliesPerUpdate=2, long MaxUploadBytesPerUpdate=4194304, int MaxDisposalsPerUpdate=8, long MaxResidentCpuBytes=268435456, long MaxResidentGpuBytes=536870912, long MaxPlanBytes=16777216, int MaxActiveWindows=64)`.
- Produces `MapRenderResidency(MapSceneSource source, MapRenderAssets assets, MapRenderPolicy policy, IMapRenderDevice device, MapRenderBudgets? budgets=null)`, readonly Device, `SetWindows(IReadOnlyList<MapSceneScope> scopes) -> MapSceneStatus`, `Update() -> MapRenderReadiness`, `Record(MapFrameState, IMapDrawSink) -> void`, `Dispose() -> void`. `MapRenderReadiness(MapSceneStatus Status, int Pending, int Ready, long CpuBytes, long GpuBytes, long QueuedBytes)` reports replacement work even while old draws remain.
- Produces `MapRenderHandle(long Value)`, immutable `MapRenderUploadData(MapRenderKey Key, GltfMesh? Mesh, GltfMaterialMaps? Maps, ReadOnlyMemory<Matrix4x4> Transforms, ReadOnlyMemory<float> Ranks, ReadOnlyMemory<byte> ClipData)`, `IMapRenderDevice : IMapDrawSink` with `BeginUpload(MapRenderUploadData data) -> IMapRenderUpload` and `Release(MapRenderHandle handle) -> void`. IMapRenderUpload exposes Ready, `Advance(long maxBytes) -> long`, `Publish() -> MapRenderHandle` and Dispose. Task 8 uses a recording fake, Task 10 supplies the Scene3D adapter. Actual draw submission resolves a recorded key only to its accepted device handle.
- Produces renderer-neutral `SceneUploadData(GltfMesh? Mesh, GltfMaterialMaps? Maps, ReadOnlyMemory<Matrix4x4> Transforms, ReadOnlyMemory<float> Ranks, ReadOnlyMemory<byte> ClipData)`, `Scene3D.PrepareResourceUpload(SceneUploadData data) -> SceneResourceUpload`, `SceneResourceUpload.Advance(long maxBytes) -> long`, readonly Ready, `Publish() -> SceneResourceHandle` and `Dispose() -> void`. SceneResourceHandle is an opaque renderer-owned value covering mesh/maps/instance-rank/domain-clip buffers, with lifetime release through `Scene3D.ReleaseResource(SceneResourceHandle) -> void`. Before Ready, Publish refuses. Advance charges actual staging/vertex/index/texture-mip/instance/clip bytes and never exceeds maxBytes. Pending uploads reserve their full resident footprint before allocation. Existing synchronous LoadMesh remains compatible over shared upload internals, with no new MapDoc reference.
- Produces additive `GroundCoverRenderer.DrawGroundCover(Scene3D scene, GroundCoverBatch cover, IReadOnlyDictionary<string,IReadOnlyList<MeshHandle>> meshes, Vector3 focus, GroundCoverRenderOptions options, RenderAnchor anchor, SceneResourceHandle prepared) -> int`. Native residency uses this prepared overload exclusively. It refuses missing/stale prepared instance buffers rather than triggering a lazy unbudgeted GPU upload on Draw. Ordinary legacy retained-batch behavior remains unchanged.
- Produces `MapRequiredDomainCoverage.Acquire(MapSceneSource source, IReadOnlyList<MapSceneScope> domains) -> MapSceneCoverageLease`, immutable witness and IDisposable lifetime, `MapScopedWorldBuilder.Build(MapScopedScene scene, MapAssetClosure assets, MapWorldBuildOptions options) -> MapScopedWorldResult`, `MapScopedWorldResult(MapSceneStatus Status, MapScopedWorld? World, MapSceneWitness Witness)` and `MapSimulationCoverage(MapSceneSource source, MapAssetClosure assets, MapWorldBuildOptions options)` with `Acquire(IReadOnlyList<MapSceneScope>) -> MapSimulationLease`. MapScopedWorld is created in MapScopedWorldBuilder.cs with Witness: MapSceneWitness, Placements: IReadOnlyList<MapPlacementGeometry>, Statics: IReadOnlyList<MapStaticDescriptor>, Terrain: MapTerrainChunkSet, Lights: IReadOnlyList<MapEffectiveLight>, Markers: IReadOnlyList<MapEffectiveMarker>, Water: IReadOnlyList<MapCompiledWaterBody>, SourceGeneration: string and ScopeIdentity: string. It never masquerades as a complete MapBuiltWorld.
- Produces additive `MapNavTiling.Partition(MapScopedWorld world, MapWorldGrids grids, MapNavTileOptions options) -> IReadOnlyList<MapNavTile>`, `Seams(IReadOnlyList<MapNavTile> tiles, MapScopedWorld world, MapNavTileOptions options) -> IReadOnlyList<MapNavSeam>` and `Links(MapScopedWorld world, MapWorldGrids grids) -> IReadOnlyList<MapNavLink>`, reusing existing descriptor kernels only. The lease exposes separately owned statics, annotations and completeness, never uses camera windows. A seam whose padded capture domain is unavailable refuses coverage rather than certifying a partial seam.
- MapSceneCoverageLease exposes Status, Witness, scoped Scenes and `Dispose() -> void`. MapSimulationLease exposes Status, Witness, per-domain Worlds and `Dispose() -> void`, holds coverage until cell publication/last lease release and supplies the lease carried by the fixture's ICellImportAdmission implementation. MapRenderLifetime exposes monotonic Created/Disposed and current LiveHandles, CpuBytes, GpuBytes, QueuedBytes, UploadBytes and WorkerCount as readonly long counters at actual allocation/release sites.

- [ ] **Write failing tests:** `AnchorOutsideWindow_LastMembershipOwnsOneResource`, `ReplacementFence_RejectsEveryGenerationBeforeAnyUpload`, `DeepLayerResidency_LoadsOnlyApprovedWorkingSet`, `AggregateBudgetsIncludeOldReplacementAndRetirement`, `MultiCellGhostHandoff_RenderAndNavStayContinuous`. Test load A, load B, unload A, replace B, recenter C, unload B/C, dispose. Inject held completions instead of sleeps.

```csharp
Assert.Equal(1, liveHandlesForSpanningOwner);
Assert.Equal(1, handlesAfterFirstMembershipLeaves);
Assert.Equal(0, handlesAfterLastMembershipLeaves);
Assert.Equal(0, stalePlanUploadCalls);
Assert.Equal(2, peakWorkers);
Assert.InRange(updateUploadedBytes, 0L, 4_194_304L);
Assert.InRange(residentGpuBytesIncludingReplacements, 0L, 536_870_912L);
Assert.Equal(1, host.OwnerCount(101));
Assert.Equal(beforeVisualUnloadStatics, afterVisualUnloadStatics);
```

- [ ] **Run red:** `wa_test t8-red "$RENDER" "FullyQualifiedName~NativeMapResidencyTests"`, `wa_test t8-red-server "$MP" "FullyQualifiedName~MapSimulationCoverageTests"`.
- [ ] **Implement** membership by resource key, stable storage owner and every intersected tile/window. Placement origin stays its storage owner, terrain uses minimum bound corner, water triangle memberships exclude dry notches, annotations include exact point/radius and foliage uses padded render bounds. Partition generated terrain/water geometry by fixed storage-local canonical cells before any window clipping, so the same subresource key has the same complete payload in overlapping scopes. A scope acquires every intersecting subresource, not an arbitrarily clipped payload under a whole-resource key. Count memberships before enumerating. Full-load and streaming unions compare by logical key and payload, rejecting unequal duplicate payloads.
- [ ] **Validate reverse extents** before accepting the first rendering scope, requiring the supplied MapRenderAssets to cover every source.RequiredAssetId and match ClosureHash. Decode/validate distinct assets once under Task 1 limits, without GPU upload. A lying declared LOD bound in an off-window owner must refuse the render source before it can hide an intersecting mesh. CPU simulation sources still use their released descriptor/shape validation and acquire no renderer dependency.
- [ ] **Implement** detached queue work, immutable closure/source/scope/policy/generation keys and checks before allocation/upload and publication. Stale completion never uploads or revives evicted work. Old accepted draws persist through rebuild/failure until complete replacement is ready. Upload a large resource incrementally in bounded vertex/index or texture-row pieces, keeping it unpublished until complete. Resident caps include old/new, staging, HLOD fallback, textures/mips and fence-retired resources. A resource above total capacity refuses, queued work backpressures with CapacityExceeded, never evicts still-required data or truncates a scene. Cancellation/disposal releases each owned handle exactly once.
- [ ] **Exercise real upload accounting** for meshes, textures, water clip buffers and foliage matrices/ranks, including a payload larger than 4 MiB that advances across updates. Task 10's device adapter must use SceneResourceUpload, and native ground cover/HLOD must not allocate their retained buffers lazily at Draw. The per-update upload cap covers residency preparation traffic, ordinary per-frame uniforms retain the renderer's existing accounting. A stale fully uploaded result is released without publication. Tests pin both recording-device calls here and actual renderer counters at Task 11.
- [ ] **Implement** independent required-domain coverage and scoped static preparation over the same neutral effective kernel. Prove with real ShardHost cells of 64 m, actor 101 crossing X=64, observer windows at X=-128 and +128, vertically separate domains and retained cell-minimum annotation coverage without observers. Destination admission holds until its required geometry/nav descriptors are ready, then ProcessHandoffs gives one owner and the expected ghost, with continuous world coordinates/support. Missing destination coverage is Unresolved, never a floor at zero. Nav tile descriptors/digests are the released ones, no new incremental path planner or #1301 expansion. Cosmetic LOD/light/motion bounds never set static/nav tile Y bounds.
- [ ] **Run green:** both filters as green, `wa_test t8-regress "$RENDER" "FullyQualifiedName~PropCluster|FullyQualifiedName~GroundCover"`, then `wa_sweep t8-final`. Reforecast. Format/guards and explicit-path commit `feat(mapdoc): retain bounded render resources independently of simulation coverage`.

### Task 9: Roof intent and shared presentation query geometry (4 to 6 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.Render3D/NativeMapRoofs.cs`, `MapPresentationPicker.cs`, `KhaozEngine.Physics/PropQueryGeometry.cs`, `ShapeRayQueries.cs`, `ShapeQueryMath.cs`.
- Create: `KhaozEngine.MapDoc/Interiors/MapRoofEvaluationBuffer.cs`, `KhaozEngine.MapDoc.Render3D/MapPresentationGeometry.cs` for prepared verified collider/selection data. Modify: `KhaozEngine.MapDoc/Interiors/MapRoofPolicy.cs:14-40`, `KhaozEngine.MapDoc.Physics/MapInteractionEnvelope.cs:48-154`, `MapShapeQueries.cs:41-74,197-277`, `MapShapeBounds.cs`, `MapCylinderQueries.cs`, `MapConvexQueries.cs`, `MapMeshQueries.cs` only for shared extraction. Preserve public wrappers and exception categories, extend Task 8 MapRenderPlan preparation/Record with the geometry and roof buffer after this task.
- Test/create: `KhaozEngine.Render.Tests/MapDoc/NativeMapRoofTests.cs`, `NativeMapPickTests.cs`, `KhaozEngine.MapDoc.Physics.Tests/MapPresentationGeometryParityTests.cs`, `PresentationGeometryFixtures.cs`.

**Interfaces:**
- Consumes `MapRoofPolicy.Evaluate(MapEffectiveResources, MapScopedSurfaces, MapFramePoint, MapRoofMode) -> MapRoofIntentResult`, `MapShapeQueries.Raycast(MapPickRay, PhysicsShape, Pose, out double, out Vector3) -> bool`, and `PropCollisionFormat.Read(Stream) -> PhysicsShape`.
- Produces additive `MapRoofPolicy.Evaluate(MapScopedScene scene, MapFramePoint observer, MapRoofMode mode) -> MapRoofIntentResult`, delegating to the same intent kernel. `NativeMapRoofs.Record(MapScopedScene scene, MapFrameState frame, IMapDrawSink sink) -> void` applies independent channels.
- Produces `MapRoofEvaluationBuffer(MapScopedScene scene)`, `Evaluate(MapFramePoint observer, MapRoofMode mode) -> void` and readonly Status/SpaceId/Roofs, using cached visible/hidden intent rows and bounded reusable membership scratch. Full/scoped public Evaluate wrappers allocate their detached result outside hot paths. Native plan Update/Record and picker consume the buffer, so observer motion within or across prepared rooms need not allocate a fresh witness or intent array each frame.
- Produces `ShapeRay(double X, double Y, double Z, double Dx, double Dy, double Dz, double MaxDistance)`, `ShapeRayHit(double Distance, Vector3 Normal, bool Inside)`, `ShapeRayQueries.Cast(in ShapeRay ray, PhysicsShape shape, Pose pose, out ShapeRayHit hit) -> bool`, `PropQueryGeometry.Bounds(PhysicsShape, Pose) -> (double MinX,double MinY,double MinZ,double MaxX,double MaxY,double MaxZ)` and `RaiseEnvelope(PhysicsShape source, Pose pose, float minimumHeight) -> (PhysicsShape Shape, Pose Pose, float RaiseMetres)`. These Physics types have no MapDoc or Movement edge.
- Produces `MapPresentationChannel { Selection, Camera, Sky, Physical }`, `MapPresentationRay(MapFramePoint Origin, Vector3 Direction, double MaxDistance)`, `MapPresentationHit(string OwnerId, long? NumericId, double Distance, Vector3 LocalPoint, Vector3 Normal)`, `MapPresentationPicker(MapRenderPlan plan, IMapPresentationCut? cut = null)`, `Pick(MapPresentationRay ray, MapPresentationChannel channel, MapFrameState frame) -> MapPresentationHit?` and extension seam `IMapPresentationCut.Accept(string ownerId, MapFramePoint point) -> bool`. Default cut accepts all. R8b supplies cut classification and frame synchronization.

- [ ] **Freeze released synthetic shape results before extraction**, using existing envelope/query fixtures plus low boxes, tilted cylinders, compounds, aperture meshes and origin-inside rays. Capture literal distances/normals/envelope vertices against the task base, not a changed kernel. These literals and existing tests gate the shared move.
- [ ] **Write failing tests:** `HiddenFirstHit_ContinuesPerChannel`, `RoofFourChannels_PreservePhysicalAndSky`, `ObserverRoom_HidesOnlyLinkedRoofAboveThreshold`, `GenericShapeExtraction_PreservesReleasedQueries`. Synthetic ray meets hidden roof at distance 2 and selectable object at distance 5. An unlinked shelter and cave ceiling remain independent geometry. Ambiguous/missing membership keeps roofs visible.

```csharp
Assert.Equal(new[] { false, true, false, true }, hiddenRoofFourFlags);
Assert.Equal("object-behind", selectionHit!.OwnerId);
Assert.Equal("object-behind", cameraHit!.OwnerId);
Assert.Equal("roof-a", skyHit!.OwnerId);
Assert.Equal("roof-a", physicalHit!.OwnerId);
Assert.Equal(1f, lowObjectEnvelopeHeight);
Assert.Equal(originalColliderBounds, unchangedPhysicalBounds);
Assert.Equal(frozenQueryResults, extractedQueryResults);
```

- [ ] **Run red:** `wa_test t9-red "$RENDER" "FullyQualifiedName~NativeMapRoofTests|FullyQualifiedName~NativeMapPickTests"`, `wa_test t9-red-parity "$MP" "FullyQualifiedName~MapPresentationGeometryParityTests"`.
- [ ] **Implement** generic shape extraction and wrappers with unchanged released operation order and supported shapes. Preserve MapInteractionPolicy's 1 m minimum, explicit Selection before collider fallback and precise local pose. Presentation Selection uses the shared envelope, Camera/Sky/Physical use exact colliders/canonical faces with their respective roof predicates. Keep ordered candidate traversal and ordinal tie-break, continue after rejected hits with reusable visited storage. Never inflate a collider or return null merely because the closest roof is hidden.
- [ ] **Implement** roof policy over scoped room/group coverage and the same prepared membership kernel, using Task 8 plan's retained scene/assets for geometry preparation. Add the evaluation buffer to `KhaozEngine.MapDoc/Interiors/MapRoofEvaluationBuffer.cs` and route both detached wrappers and the native hot path through it. Hidden roof colour is absent and shadows use the full mesh with the same bounds/range as the building. Sky remains blocked, camera passes, statics stay unchanged. AboveY remains concealment only. Supply the identity cut extension test with a fake rejected front hit, but no MapDoc interior source, field, lighting or highlight-cut integration. Those belong to R8b.
- [ ] **Run green:** both filters as green, `wa_test t9-regress "$MP" "FullyQualifiedName~MapInteraction|FullyQualifiedName~MapWorldQuer|FullyQualifiedName~MapShape"`, `wa_test t9-render-regress "$RENDER" "FullyQualifiedName~PropRenderer"`, then `wa_sweep t9-final`. Format/guards and explicit-path commit `feat(mapdoc): preserve roof channels and native presentation picks`.

### Task 10: Shared view, deterministic snapshot readiness and frontends (5 to 8 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.Render3D/NativeMapView.cs`, `MapScene3DSink.cs`, `NativeMapSnapshot.cs`, `MapSnapshotRequest.cs`, `MapCapturePolicy.cs`, `KhaozEngine.MapEditor/ViewportWorld.Native.cs`, `KhaozEngine.MapEdit.Tool/RenderService.Native.cs`, `NativeRenderWire.cs`.
- Modify: `KhaozEngine.MapEditor/ViewportWorld.cs:234-248,409-487` and `ViewportWorld.InPlaceRefresh.cs` routing hooks, `MapEditorScene.cs:320-335,446-458` through helper replacement without baseline growth, `MapEditorOptions.cs` closure/policy inputs, `KhaozEngine.MapEdit.Tool/RenderService.cs:24,61-105,111-173` including partial declaration, `RenderStreamPlan.cs:21-73`, `Results.cs:14`, `Tools/RenderTools.cs:17` render methods, `KhaozEngine.MapEditor/KhaozEngine.MapEditor.csproj:11-22` and `KhaozEngine.MapEdit.Tool/KhaozEngine.MapEdit.Tool.csproj` reference ItemGroup.
- Test/create: `KhaozEngine.MapEditor.Tests/MapDoc/NativeViewportAdapterTests.cs`, `NativeRenderMaterialSchemaTests.cs`, `MapEditTool/NativeMapCaptureTests.cs`, `NativeMapCaptureWireTests.cs`, `KhaozEngine.Render.Tests/MapDoc/NativeMapReadinessTests.cs`.

**Interfaces:**
- Consumes `Render3DSnapshot.CaptureWithBackend(int, int, Action<Scene3D>, Action<Scene3D>, int = 1, ShadowSettings? = null) -> Render3DCapture` and `CaptureSequence(int,int,Action<Scene3D>,Action<Scene3D,int>,int,Action<int,Render3DCapture>,int = 0,ShadowSettings? = null) -> void`, Task 8 readiness/plans and Task 9 picker.
- Produces `MapCaptureRequirement { CompleteScope, Preview }`, `MapCaptureAa { Native, Quality, Balanced }`, `MapSnapshotFraming(MapFramePoint Eye, MapFramePoint Target, bool Orthographic, float VerticalSizeOrFovDegrees, int Width, int Height)`, `MapCapturePolicy(MapRenderPolicy Render, MapCaptureAa Aa, int WarmupFrames=16, int MaxSettleOperations=1024)` and `MapSnapshotRequest(MapAssetClosure Closure, MapSceneSource Source, MapSceneScope Scope, MapSnapshotFraming Framing, MapFramePoint Observer, string? ObserverSpaceId, MapRoofMode RoofMode, double TimeSeconds, int WindSeed, MapCapturePolicy Policy, MapCaptureRequirement Requirement)`.
- Produces `MapSnapshotResult(MapSceneStatus Status, Render3DCapture? Image, string? Backend, MapSceneWitness Witness, MapRenderReadiness Readiness, IReadOnlyList<string> Findings)`, `NativeMapSnapshot.Capture(MapSnapshotRequest request) -> MapSnapshotResult` and `CaptureSequence(MapSnapshotRequest request, int frames, Action<int,MapSnapshotResult> onFrame) -> void`, retaining one scene/device across the sequence.
- Produces `NativeMapView(Scene3D scene, MapRenderResidency residency)`, `Update(MapFrameState frame) -> MapRenderReadiness`, `Draw(MapFrameState frame) -> void`, `SettleForCapture(int maxOperations) -> MapRenderReadiness`, `Dispose() -> void`. Device-free `MapCapturePreparation.Prepare(MapSnapshotRequest request) -> MapSnapshotResult` is created in NativeMapSnapshot.cs and returns no Image until actual capture.
- Produces `MapScene3DSink(Scene3D scene) : IMapRenderDevice`, implementing Write/BeginUpload/Release through the shared Scene3D anchored and incremental-upload APIs. NativeMapView uses the same sink held by residency.Device, verifies it belongs to scene and rejects a different device. NativeMapSnapshot creates one sink/residency/view lifetime in that order, with failure cleanup and no second composition or unbudgeted ground-cover upload.

- [ ] **Write failing tests:** `ViewportAndSnapshot_ShareNativeInventoryAndStyle`, `ReadyBeforeWarmup_FreshAndReusedAgree`, `CaptureScope_IncludesOffCameraShadowAndLightDependencies`, `NativeCapture_MissingClosureNeverReturnsTerrainOnlyImage`, `CappedPreview_CannotClaimComplete`, `AnalyticCapture_KeepsReleasedRouting`, `NativeRenderWire_ContainsScopeBackendAndReadiness`. Inject held build completions, change size/camera/roof/time/closure and compare one prepared request with a reused scene. Each wire call has its own 60 s token in McpWireCollection.

```csharp
Assert.Equal(viewInventory, captureInventory);
Assert.Equal(viewShrubStyles, captureShrubStyles);
Assert.Equal(0, warmupFramesBeforeReady);
Assert.Equal(MapSceneStatus.Incomplete, capped.Status);
Assert.False(capped.Witness.CompleteForScope);
Assert.Null(missingClosure.Image);
Assert.Equal(101, listedToolCount);
Assert.Contains("readiness", wirePropertyNames);
```

- [ ] **Run red:** `wa_test t10-red "$EDITOR" "FullyQualifiedName~NativeViewportAdapterTests|FullyQualifiedName~NativeRenderMaterialSchemaTests|FullyQualifiedName~NativeMapCaptureTests|FullyQualifiedName~NativeMapCaptureWireTests"`, `wa_test t10-red-ready "$RENDER" "FullyQualifiedName~NativeMapReadinessTests"`. Schema tests use the released editor test evaluator against the actual embedded material schema and Task 1 decoder, checking every accepted/refused shape.
- [ ] **Implement** the public view and single capture policy. Readiness drains bounded preparation/apply/upload completion by actual completion signals, never sleeps or guessed frames. A pending or refused required resource prevents complete output. Settle ceiling exhaustion returns a finding and no successful complete image. Temporal warm-up starts only after readiness, with time/wind/observer fixed, exact framing and explicit camera cut/history reset. CompleteScope requires every declared scope dependency including shadow receivers/casters and intersecting lights. Preview may return an image but Status stays Incomplete if capped. Record actual backend, witness, requested/covered scope and readiness in results.
- [ ] **Wire** a minimal native branch into ViewportWorld/MapEditorScene with closure and policy supplied by the host. Native opening must not traverse legacy asset paths or analytic field framing. Update/rebuild/dispose route to the shared view and effects invalidate source generations. The native viewport is presentation only, preserve existing mutation/session restrictions and defer interactive native picking tools/authoring to R9. Existing analytic branch remains byte/routing compatible.
- [ ] **Wire** existing render_topdown/render_view through RenderService.Native for native documents, with additive backend/scope/readiness wire DTOs and image only when the request permits it. Use effective native bounds and explicit observer independent of camera, canonical overlay order, precise no-device refusal and no legacy manifest fallback. No new MCP verb. Snapshot API remains independent of MapEditor and is ready for game captures, no game source edits or art/icon rerenders.
- [ ] **Run green:** both filters as green, `wa_test t10-regress "$EDITOR" "FullyQualifiedName~Render|FullyQualifiedName~Viewport|FullyQualifiedName~McpAdapter"`, then `wa_sweep t10-final`. GPU fresh/aged and actual frontend captures run with Task 11. Format/guards and explicit-path commit `feat(mapdoc): share native view readiness and frontend captures`.

### Task 11: Precision, allocation and backend evidence (4 to 6 engineer-days)

**Files:**
- Create: `KhaozEngine.Render.Tests/MapDoc/NativeMapPrecisionTests.cs`, `NativeMapAllocationTests.cs`, `NativeMapGoldenGpuTests.cs`, `NativeMapGoldenScene.cs`, `NativeMapSnapshotGpuTests.cs`, `NativeMapGoldenAssertions.cs`.
- Create: six `native-map-{paint,props,roofs,water,foliage,origin}.{metal-native,direct3d11-native,vulkan-native}.txt` files under `KhaozEngine.Render.Tests/Gpu/goldens/`, explicitly list each of the 18 paths in the commit array after bake.
- Modify: `scripts/gpu-test-shards.py:93-148` backend slice rules, Task 8 lifetime counters/Task 10 sink only for verified failures. Existing Render.Tests allocation props/serialized framework already apply.

**Interfaces:**
- Consumes Tasks 1 to 10, released Scene3D.RenderOrigin and IRenderOriginAware cameras, `GoldenCompare.AssertOrUpdate` in `KhaozEngine.Render.Tests/Gpu/GoldenCompare.cs`, and test-shared AllocationRegion/AllocationMeasurement.
- Produces six scene variants through `NativeMapGoldenScene.Capture(string variant, bool omitFeature=false, bool farOrigin=false) -> Render3DCapture`, reused Scene3D fixture and fresh-scene parity. Tests read actual render counters and MapRenderLifetime counters for live/created/disposed handles and charged CPU/GPU/queued bytes, not test-maintained guesses.

- [ ] **Write failing CPU acceptance tests:** `FarOriginHlodRenderAndPick_MeetPrecisionContract`, `WarmIdleUpdateDrawPick_AllocateZero`, `LifetimeCounters_ReturnToBaselineAfterRecenterAndDispose`. Near origin (0,0), far (+32000,-32000) and opposite corner (-32000,+32000), Y=-500/+500, include lights, water, exact foliage matrices, shadow-only roofs and picks together. Keep 64 m padded extents and 256 m local windows. Compare canonical exact geometry/local values with the declared precision targets, not rounded absolute output against itself.

```csharp
Assert.InRange(maxExactToLocalErrorMetres, 0d, 0.0001d);
Assert.InRange(maxTranslatedFoliageErrorMetres, 0d, 0.004d);
Assert.Equal(0L, warmedIdleAllocatedBytes);
Assert.Equal(0L, warmedUpdateAllocatedBytes);
Assert.Equal(0L, warmedRecordAndPickAllocatedBytes);
Assert.Equal(baselineLiveHandles, disposedLiveHandles);
Assert.Equal(0L, disposedChargedBytes);
```

- [ ] **Run red:** `wa_test t11-red "$RENDER" "FullyQualifiedName~NativeMapPrecisionTests|FullyQualifiedName~NativeMapAllocationTests"`. Allocation tests join an existing DisableParallelization allocation collection and measure through AllocationRegion, including misses/hidden-hit continuation. Warm fixture/device/cache setup is outside the region, no background completion or measurement subtraction.
- [ ] **Implement** any missing exact local boundary, cached draw/pick buffers and lifetime counters at their owning seams. CPU recording/upload selection and settled frame paths allocate zero managed bytes after warm-up, including bounded observer motion across prepared room boundaries, changing roof mode, LOD threshold crossings and hidden-hit continuation. Scratch capacity is admitted during preparation. Add a GPU submission allocation fact using the same actual path and collection, measured around warmed queue/update/draw only, excluding readback. Finite lifecycle sequences, held completions and counters replace stress loops.
- [ ] **Write GPU families and omissions.** All use 640x480, Native TAA, 16 post-readiness warm-up frames, fixed time 12.0 and wind seed 17. Paint has all four cuts/rotations, feather/partial paint and a visible ceiling edge. Props has free-yaw textured multipart building and all LOD inventory. Roofs has hidden roof colour absent, shadow present and 17 local lights. Water has the bent bounded body and dry notch with today's look. Foliage has fixed motion, Blade/four Shrub styles and moving shadows. Origin repeats the mixed scene near/far. Each omission disables its feature at the actual submission seam, not in expected data.
- [ ] **Assert sensitivity** beyond 0.01 grid comparison: paint/prop/foliage ROI at least 64 pixels differ by RGB L1 >=24/255, roof shadow receiver at least 64 pixels darken by >=8/255 while its roof colour ROI remains unchanged when switching colour concealment, water dry-notch coverage exactly 0 and wet ROI at least 64 pixels, origin paired mean RGB error <=0.01 with equal semantic IDs/counts. Omission must fail its relevant positive assertion on every backend. Assert no closed/unresolved water draws and no missing lights/parts. New capture facts prove actual editor/tool adapters and fresh/aged parity, including delayed foliage/HLOD readiness and frame origin changes. No cave/cut optical acceptance is claimed.
- [ ] **Run CPU green:** red filter as green, Task 10/legacy touched regressions, then `wa_sweep t11-final`. Default local GPU skips are expected and explicitly not proof. Assign NativeMapGoldenGpuTests, NativeMapSnapshotGpuTests and the GPU fact in NativeMapAllocationTests, including all theory methods, to exactly one Render.Tests slice per backend in scripts/gpu-test-shards.py. Controller lists tests via `wa_run t11-list dotnet test "$RENDER" -c Release --no-build --list-tests`, using its saved log as the listing, then runs `python3 scripts/gpu-test-shards.py check-render <backend> <listing>` for each backend, requiring 0 unassigned and 0 doubled.
- [ ] **Bake/verify** through cross-platform-gpu with `bake=true`, `legs=all`, `renderTestFilter=FullyQualifiedName~NativeMapGoldenGpuTests`. Keep each backend's own 6 LF files, download only goldens-<backend> to /tmp/khaozengine-wa-r8a-goldens and copy only the explicit 18 new files. Do not change an existing family or hide a parallel lane's look change. Actual GPU proof, backend/adapter, nonzero cases, zero skipped required R8a facts and fresh/aged/omission results are recorded by the controller. Bake is not the full candidate gate below.
- [ ] **Format/guards and commit** explicit new code/shard paths plus the 18 baked files, `test(mapdoc): certify native scene precision lifetime and backend families`.

### Task 12: Private draw-set comparison and published consumer contract (4 to 8 engineer-days)

**Files:**
- Create: `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedSceneInputs.cs`, `ShippedSceneManifest.cs`, `ShippedSceneComparison.cs`, `ShippedSceneComparisonTests.cs`, `ShippedSceneInputGuardTests.cs`, `ShippedSceneGuardFixtures.cs`, `LegacySceneRecorder.cs`.
- Modify: harness `KhaozEngine.MapDoc.OracleHarness.Tests.csproj:13-20`, `README.md` scene extraction contract, `KhaozEngine.Tests/ArchitectureTests.MapDoc.cs:31-52`, root `README.md`, `docs/USING-KHAOZENGINE.md`, `docs/DEPENDENCY-SEAMS.md`, `docs/INDEX.md`, new package README and MapDoc/MapDoc.Physics/MapEditor/MapEdit.Tool/Terrain.Render3D package READMEs.
- Private prerequisite, controller-owned only: retained `proofs/r8a-scene-oracle/` generator, helper projects, correspondence manifest and README in the private program repository. No private file belongs to this engine commit.

**Interfaces:**
- Consumes `PrivateOracleEntry.Run(Action<PrivateOracleInputs,PrivateOracleReport>) -> void`, `PrivateOracleReport.Check(string,bool,string) -> void`, `Record(string,string) -> void`, `ShippedSourceInventory.ReadWorld(PrivateOracleInputs) -> (TileWorldDocument Document, TileWorldCatalogs Catalogs)`, Task 1 recorder and actual Task 8/10 draw-plan path. Existing terrain/building/marker/foliage readers validate their retained correspondence rules.
- Produces `ShippedSceneInputs.Read(PrivateOracleInputs inputs) -> ShippedSceneInputs`, `ShippedSceneComparison.Compare(ShippedSceneInputs inputs, PrivateOracleReport report) -> void`, private fact `ShippedScene_DrawSetsAndStreamingPreserveEveryResource` and `ShippedSceneGuardFixtures.Create(string variant) -> ShippedSceneGuardFixture`. Fixture exposes Environment/PublicSecretMarker and IDisposable cleanup like existing guards. No discovery-time environment or source reads.
- Produces `LegacySceneRecorder.Record(TileWorldDocument document, TileWorldCatalogs catalogs, ShippedSceneInputs inputs) -> LegacySceneInventory`, an internal immutable CPU inventory, and a test-only ITileWorldScene recording implementation. It exercises the released legacy ground/prop/cluster/foliage/water routes with fake handles, obtains all-LOD inventory from original catalogs/verified source meshes and light input from the independently retained source-policy extractor. No native recorder, native bounds or native comparator supplies expected rows. Guard tests prove changed native data leaves the expected inventory unchanged and still fails comparison.

- [ ] **Name and deliver the whole private closure before extraction (R4-P40).** Freeze current source commit/tag-or-explicit-empty-tag/engine pin, one attested world manifest, every indexed region, catalogs, prefab/membership/role/foliage correspondences, original mesh/every LOD/texture/collider/selection/light bytes, live render/roof/cluster/foliage/light policy inputs, retained generator/helpers and pinned engine helper build. Include native monolithic document, tiled manifest, all surface pages/patch packs/record graphs, foliage packs and complete asset/material graph. Every extraction file is in the existing ordinal path TAB SHA256 LF aggregate, provenance SHA checked before parsing, no ambient disk lookup. This is comparison scaffolding, not R11 conversion.
- [ ] **Write failing public guards:** `SceneInputs_RequireEveryDependencyAndCorrespondence`, `SceneDrawSet_RejectsMissingDuplicateOrChangedResource`, `SceneStreaming_RejectsUnequalDuplicateAndLateResurrection`, `SceneGuard_SanitizesAllPrivateFailureRoutes`. Mutate terrain/material, placement/part/LOD/transform, light, roof channel, water region, foliage key/bounds and completeness independently. Missing/unsafe/existing/symlink report, wrong digest, unknown/duplicate/case-variant JSON fields, missing helper/pack, stale source and unapproved rule difference all fail. PublicSecretMarker never appears publicly.

```csharp
Assert.Equal(8, harnessDirectEngineReferences.Count);
Assert.Equal(0, validSyntheticFailures);
Assert.Contains("scene-placement-part", missingPartCategories);
Assert.Contains("scene-roof-channel", changedRoofCategories);
Assert.Contains("scene-stream-union", missingWindowCategories);
Assert.Contains("scene-generation", resurrectedCategories);
Assert.DoesNotContain(fixture.PublicSecretMarker, publicFailureMessage);
```

- [ ] **Run red:** `wa_test t12-red "$ORACLE" "FullyQualifiedName~ShippedSceneInputGuardTests"` and `wa_test t12-red-graph "$RUMP" "FullyQualifiedName~OracleHarness"`. Implementer briefs name exactly PrivateOracleGuardTests and ShippedSceneInputGuardTests as public guard classes, never a broad ShippedScene filter that selects the private fact.
- [ ] **Implement** the eighth direct reference MapDoc.Render3D in the harness and exact graph pin. Keep the seven released references, nonpackable/outside-solution/unreferenced/public-CI exclusions. CPU recording instantiates no device, window or provider and does not emit pixels. Private appearance stays a separate R8b runner. New render package itself has no TileWorld edge.
- [ ] **Pin the closed scene manifest** with exactly `schemaVersion=1`, `generator`, `nativeDocumentPath`, `nativeTiledManifestPath`, `buildingManifestPath`, `markerManifestPath`, `foliageManifestPath`, `materials`, `placements`, `lights`, `roofs`, `water`, `foliage`, `windows`, `sequence`, `policy` and `expectations`. Paths are attested relative paths, linked manifests are required when their categories participate and null otherwise. Unknown/duplicate/missing/case-variant keys refuse. Correspondences map keys only, never expected geometry/draw lists. Validate unique source and native keys and complete source coverage before any draw comparison.
- [ ] **Define nested rows exactly.** generator is `{path,helpers}` with unique attested paths. materials rows are `{sourceMaterialId,nativeResourceId}`, positive ushort source IDs. placements rows are `{sourceObjectId,nativePlacementId,lods}`, positive decimal int64 object IDs, lods rows `{sourcePath,nativeResourceId}` in original base/LOD order, each checked against original catalogs. lights rows are `{sourceObjectId,sourceProfileId,nativeLightId}` and permit multiple declared profile instances only when original source does. roofs rows are `{sourceObjectId,nativePlacementId}`, checked against the building manifest's links/groups. water rows are `{sourceBodyKey,nativeBodyId,nativeSpaceIds}`. foliage rows are `{sourceLayerId,nativeLayerId}`, checked against the full R7 manifest. All string IDs are nonempty and ordinal. Every list is required, including empty categories.
- [ ] **Define scope/policy rows exactly.** windows rows are `{id,minX,minY,minZ,maxX,maxY,maxZ,spaceIds,motionPaddingMetres}`, finite ordered double bounds, explicit padding and null-or-unique space IDs. sequence rows are `{operation,windowId}`, operation Load/Unload/Recenter, valid named windows and state transitions. policy is `{sourcePolicyPath,nativePolicyPath,probes}`, two attested files and nonempty probes. A probe is `{id,windowId,observerX,observerY,observerZ,roofMode,timeSeconds,windSeed,focusX,focusY,focusZ}`, finite coordinates/time, roofMode Auto/AlwaysVisible/AlwaysHidden and int32 seed. Policy files contain closed serializations of the actual shared placement/material/foliage/water options, exact property sets taken from the Interfaces and released option type at this candidate. Reader checks source file against independently executed source extraction, not only against native settings. expectations has exactly nonnegative integer terrainFaces/placements/parts/lods/lights/roofs/waterRegions/foliageSamples/windows/sequenceSteps, independently counted from source with region mappings, never copied from a native run. No historical private count is a plan literal.
- [ ] **Compare independently.** Generator maps original legacy draw inputs, not the native recorder's output, and retains its source-reading logic in private git. Terrain compares canonical faces, material/paint coverage, vertices/normals and area, never arbitrary render triangulation order. Every original placement compares ID/NumericId, part/material inventory, effective transform, all available LODs and selected/HLOD represented IDs at fixed source policy probes. Lights compare full descriptors/owner, roofs inventory and four channels, water body/space/free regions/area/level and excluded closed regions, foliage keys/original matrices/rank/padded bounds. Preserve exact scalar bits except the fixed geometric/angle tolerances in P13. No cut-volume comparison in R8a. Complete versus streamed union and load A/load B/unload A/recenter C/unload B/C compare keyed payloads, coverage witnesses, duplicate counts and final zero live resources. Both native storage forms load independently twice. Success aggregates stream, detailed mismatches stay private, no full sample corpus retained.
- [ ] **Prepare private generation** with one non-attesting diagnostic draft recording every discrepancy kind/count and bounded examples at once (R7-O2). A native/legacy rule difference stops attestation for a controller ruling on the rule, never tolerance. After correction produce two fresh byte-identical extractions from the retained generator, audit full closure and generation pin, then await the final candidate. Only the controller runs the actual fact once per candidate into a fresh absent report path.
- [ ] **Publish living contracts** for package/API edges, material interpretation, source bootstrap ceiling, scoped/full witness distinction, all LOD/light/motion ownership, readiness/preview flags, roof channels and precision/allocation limits. Explicit R8b seams are bounded-water optical inputs, scoped room/roof/mesh inventory for IInteriorSource, IMapPresentationCut and domain-aware sky/camera policy. R9 consumes viewport/commands, R10 additive render findings, R11 draw-set comparison and G3 the same view/snapshot plus separate #1370. Sweep all Markdown for stale current claims without rewriting executed historical plans. No version declaration change until reconciliation below.
- [ ] **Run green:** both filters as green, `wa_test t12-regress "$ORACLE" "FullyQualifiedName~PrivateOracleGuardTests|FullyQualifiedName~ShippedBuildingInputGuardTests|FullyQualifiedName~ShippedMarkerInputGuardTests|FullyQualifiedName~ShippedFoliageInputGuardTests"`, `wa_run t12-oracle-format dotnet format "$ORACLE" --verify-no-changes --no-restore`, touched CPU Render.Tests filters and `wa_sweep t12-final`. Format/guards and explicit-path commit `test(mapdoc): gate native draw sets through the private scene oracle`.

## Self-review and execution handoff

- [ ] Map every R8a gate row to a task, with R8b optical/cave/cut/appearance scope explicitly deferred. Package/assets 1, scoped source 2, window foliage 3, terrain 4, placements/lights 5, bounded water 6, foliage upload 7, residency/server 8, roofs/picks 9, snapshot/frontends 10, backend/precision/allocation 11 and private comparison/docs 12.
- [ ] Verify every consumed source path/signature at 5d896b333 and each proposed type at its producing task. No task's red/green needs a later API. Resolve all R8a-P proposals before dispatch and carry amended Interfaces into every downstream brief.
- [ ] Check actual neutral kernel reuse, no partial-document/full-builder shortcut, metadata-index admission before retention, unloaded versus absent, support dependency fixed point and full/window foliage keyed parity. Repeat the read-reach derivation, not a copied halo number.
- [ ] Check exact units/anchors, canonical terrain/paint/ceiling faces, complete parts/LODs/lights, mutually exclusive explicit-plant routes and all-LOD/motion memberships. Bounds that render well must not widen navigation tile heights.
- [ ] Trace closure/source/scope/policy/generation fences through completion, upload, publication, unload and disposal. Account old/replacement/staging/retired resources and large upload progress. Prove real cell admission/ghost handoff under independent visual windows.
- [ ] Check four roof channels, hidden-first-hit continuation, shared generic envelope/collider kernels and R8b cut seam. Capture readiness, shadow dependency scope, temporal warm-up and preview findings must be independently testable.
- [ ] Check all five Review Focus cases, zero-allocation warm regions, sparse near/far precision, six omission-sensitive backend families, actual frontend GPU facts and explicit shard assignment. Existing goldens remain unchanged.
- [ ] Check per-task nonzero Release/TRX evidence, full three-project sweeps, separate harness guards/format and exact full backend candidate gate. No broad harness filter, private data, stress permission inference or new file over 800 lines.
- [ ] Check complete private inputs before extraction, retained independent generator, all-kind diagnostic draft, fixed tolerances, two identical extractions, secure report and controller-only actual fact. No semantic discrepancy is solved with a looser threshold.
- [ ] Check proportion and determinacy. Remove unproduced helpers, mismatched names and placeholder implementation decisions. Writer stops at the one plan commit, independent review and owner plan approval remain next.

Recommended execution is serial subagent-driven work after owner approval, one scoped implementer and fresh reviewer per task, followed by whole-branch review. Controller independently verifies diffs, commit containment, exits and TRX, pushes accepted work before continuing and records rulings in the private ledger. Workers never push, merge, tag, pack or clean worktrees unless later expressly assigned. OA42 alone authorizes planning only.

## Candidate gates, private acceptance and integration

1. Complete whole-branch review, resolve findings at their owning seams and reconcile all accepted interface changes. Reforecast R8b/R9 to R11 from actual task evidence. No undisclosed cave/interior-cut or appearance claim travels with R8a.
2. Controller fetches and merges current main into this task branch, resolves conflicts there and confirms main containment. Re-audit Scene3D/capture/outline/TAA changes from parallel lanes. Do not merge the historical world-authoring branch. If another lane changes the required material/source contract or format, obtain a recorded amendment instead of silently changing this plan.
3. Re-read main/origin/main, current version, newest tags and CHANGELOG. Ride an appropriate staged minor or select the next free minor. Commit Directory.Build.props, CHANGELOG and each concrete guarded declaration together, explicit paths chosen by scripts/check-doc-versions.sh. Declare the optional package, native scene/scope/readiness APIs, format 9 unchanged, current bounded-water limitation and R8b handoff. No game engine pin change.
4. Run one full local Release candidate build and suite sequentially, plus format/harness/guards. The TRX logger deliberately leaves filename selection to the runner, generating distinct per-assembly files. Require a result for every solution test project, no overwritten shared filename.

```bash
wa_run gate-build --heavy dotnet build KhaozEngine.slnx -c Release
wa_run gate-suite --heavy dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket" --logger trx --results-directory "$wa_r8a_log_dir/gate-suite"
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

All builds have zero warnings/errors, ordinary tests nonzero counts and zero failures. GPU skips in the local no-device suite are not GPU proof. Main-only format drift may use R-GATE-1/#1485 after byte-equality/ownership checks, then verify every explicit round-owned C# path and the separate harness. R7's historical flake waiver grants no R8a waiver.

5. Push the verified candidate through the controller. Dispatch hosted `ci` and one complete `cross-platform-gpu` with `bake=false`, `legs=all`, empty renderTestFilter and normal push tier, before integration and before accepting the private comparison. Use exact run head SHA. Require all three full-suite aggregates, shard coverage, backend/device evidence, all required native GPU cases and no skipped R8a proofs. Current workflow is 3 Metal, 7 Direct3D 11 and 14 Vulkan shard jobs plus sync/aggregates, scripts/gpu-test-shards.py remains authority if counts change. A bake or golden-only push is not this gate.
6. Only after those gates and the complete attested closure are verified, controller assigns the four private values from the attestation. Existing report parent is 0700 outside git, report path absent. Run exactly the one fact below once on this candidate. No implementer executes it.

```bash
wa_run gate-private --heavy env KHAOZ_R2_SHIPPED_SOURCE="$wa_r8a_verified_source" KHAOZ_R2_ORACLE_PROVENANCE="$wa_r8a_private_provenance" KHAOZ_R2_ORACLE_PROVENANCE_SHA256="$wa_r8a_provenance_sha256" KHAOZ_R2_ORACLE_REPORT="$wa_r8a_private_report" dotnet test "$ORACLE" -c Release --no-build --filter "FullyQualifiedName~ShippedScene_DrawSetsAndStreamingPreserveEveryResource" --logger "trx;LogFileName=gate-private-scene.trx" --results-directory "$wa_r8a_log_dir"
```

PrivateOracleEntry reads the four variables in the displayed order, validates provenance before parsing, exclusively creates a 0600 report and sanitizes every exception. Body uses report.Check/Record only, never Assert, console, ITestOutputHelper or private InlineData. Controller records source/engine/generator commits, full closure, report digest, actual categories and discrepancies privately in `R8a-SCENE-ORACLE.md`. Public evidence is pass/fail and that private record reference only. Appearance evidence is R8b and R11 import acceptance is separate.

7. A mismatch fails acceptance. Correct the actual engine/generator rule after a recorded ruling. Changed candidate/extraction invalidates affected evidence, rerun the relevant ordinary gates and one private fact into a new absent path. If main advances in relevant files, reconcile on this branch and refresh affected verification, including the full backend gate when rendered/platform behavior changes. No automatic tolerance/count update, stress loop or repeated private acceptance hunt.
8. Controller checks release-lock status, integrates the verified branch to main and pushes main. Pack only from clean main once origin/main contains the integrated commit, through `wa_run release-pack --heavy sh scripts/pack-local-feed.sh`. No bare pack or released-feed overwrite.
9. Release through a KhaozEngine-rooted thread under OA25 and release-khaozengine. Re-read exact version/tags/lock, require exact-commit ci and full three-backend pre-tag gates, guarded annotated tag, publication/package proof and fresh required tool downloads. A game-rooted session never tags the engine. No release action is authorized for this plan-writing task.
10. Controller updates private PROGRAM/DECISIONS/R8a ledger with adjudicated proposals, task/candidate/integration commits, backend run IDs, private record reference, verified engine release and reforecast. R8b planning starts after this release. Game adoption still waits for R11 and the G-round gates.

## Outcome and proof ledger

Filled during approved execution.

| Item | Evidence |
| --- | --- |
| Independent plan reviews, adjudicated R8a-P proposals and owner plan approval | |
| Task commits, nonzero red/green TRX and per-task three-project sweeps | |
| Optional package/architecture, strict verified parts/materials/every LOD/light | |
| Reverse metadata discovery, scoped witnesses and unchanged complete builders | |
| Full/windowed foliage four-head parity, derived halo and refusal phases | |
| Canonical terrain/paint/ceilings and exact local anchors | |
| Placements, N-tier LOD/HLOD, full light roster and single explicit-plant routing | |
| Bounded current-look water, area work and scoped column completeness | |
| Exact foliage upload, caller styles/moving shadows and digest generations | |
| Composite memberships, budgets, stale fences and resource retirement | |
| Independent required-domain statics/nav and real cell ghost handoff | |
| Four roof channels, shared query geometry and hidden-first-hit picks | |
| Native snapshot readiness, policy parity, frontends and truthful preview findings | |
| Near/far precision, warm no-allocation and lifetime counters | |
| Six new native backend families, omissions, shard coverage and preserved goldens | |
| Complete candidate three-backend gate before integration/private acceptance | |
| Retained independent private generator, diagnostic draft and full input closure | |
| Private exhaustive scene draw-set and streaming comparison record | |
| Main reconciliation, final verification, package proof and engine-rooted release | |
| R8b seams, separate world-map prerequisite and remaining program forecast | |
