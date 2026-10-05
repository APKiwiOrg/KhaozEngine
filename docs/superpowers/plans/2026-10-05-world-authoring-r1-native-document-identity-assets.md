# World Authoring R1: Native Document, Identity and Asset Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish the native document and render-free asset identity seam while preserving analytic maps and stable placement identities.

**Architecture:** Extend MapDoc with format-4 metadata and decimal-string int64 identity. Resolve digest-verified asset closure and immutable placement snapshots before any world build. Keep the existing analytic MapRuntime path compatible and expose native data for subsequent rounds.

**Tech Stack:** C# on the repository's existing .NET target, System.Numerics, System.Text.Json, closed JSON Schema, xUnit, existing engine seams. No new third-party dependency.

**Spec:** `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md`, spec approved by the owner on 2026-10-05, under OA4, revised T4 under OA5, prefab v1/estimate under OA6 and C4 boundary policy under OA7. R1 plan approved under OA8 on 2026-10-05. Read C1, T1 to T9 and the evidence register before implementation.

## Current execution authority, 2026-10-05

Owner reply, verbatim: "apporved", to the separate R1 plan gate. OA8 approves the six tasks below,
one Astra medium implementer per task, sequentially, with fresh Sol 6.1 xhigh task reviews and a final
round review. Corrections remain within each task's review loop. Release tagging is not approved.

Execution worktree is `/Users/antonio/KhaozEngine/.worktrees/wa-r1-document-identity`, branch
`feature/wa-r1-document-identity`, from released main `b39fb1a3bde9073d9519357b546b138b2c79d957`.
This file and its spec were carried from approved planning commit `c89f260d237a70e3f2cd4800b6eed6bc24efbc8a`.
Historical pending-approval statements below describe earlier snapshots and are superseded by OA8.
The durable cross-repository tracker remains Grimhollow PROGRAM on feature/world-authoring.

## Spec approval and historical reconciliation, 2026-10-05

The exact owner answer supplied by the controller is "Approve". It approves both specs, T1 to T9 with revised T4, the rigid prefab v1 scope and 12 to 18 elapsed-week estimate, and C4 exact-boundary differential policy. It does not approve this round plan or any actual changed query result. Named import-time acceptance of changed targets, distances, occlusion, stances and water boundaries remains required. R1 to R4 are full plans. Reconcile released prerequisite signatures before their owner review. No production execution has started.

Future verification uses HANDOFF's shared slot runner, restored only if absent. Set a unique log directory from the implementation worktree before Task 1, and retain different red/green log names. These are instructions, not commands run by this documents lane.

```bash
wa_r1_log_dir="/tmp/grimhollow-orch/logs/wa-r1-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p local-feed "$wa_r1_log_dir"
test -f /tmp/grimhollow-orch/slot-run.sh
```

The 1 m allowance is a minimum vertical target reach-envelope height, preserving `MinimumObjectReachHeight`. It is not a 1 m action distance. Action range remains existing game policy and physical colliders never expand. Approval IDs are OA4 specs, OA5 revised T4, OA6 prefab/estimate and OA7 water boundary in the game DECISIONS record.

## Global Constraints

| Ruling | Decision |
| --- | --- |
| OA1 | One tool, MapEditor GUI plus ke-mapedit MCP, authors terrain, props and buildings. No two-format hybrid |
| OA2 | Props and buildings accept free position, yaw and positive uniform scale as an engine-wide capability |
| OA3 | Grimhollow leaves TileWorld completely through the full native MapDoc swap, option A |

Names below identify proposed API responsibilities, not existing public types. Core MapDoc and its runtime packages acquire no TileWorld or Grimhollow dependency. An optional offline importer is the only new package allowed to read TileWorld. Existing analytic MapDoc consumers and TileWorld consumers retain their current behavior [E1, E2, E6, E26, E31].

Formats advance at the rounds below: 5 native terrain, 6 water, 7 prefabs/interiors, 8 markers, 9 foliage. Every N to N+1 migration is pure and tested. Numbers describe this baseline-relative schema sequence, not reserved engine releases. If concurrent MapDoc work takes a number, rebase the sequence onto the next free format, preserving these semantic transitions. Future-format input refuses rather than dropping unknown fields. All newly structured payloads use closed schemas and explicit payload versions [E2, E26, E43].

Each round below is one separately reviewed implementation plan and one independently usable engine minor capability release. Contract skeletons may be additive, but no round may claim a later round's complete authoring workflow. All releases reconcile with the pivot's current main, take the next available minor and wait for the owner's tag. Engine rounds may run before game 0.11.0 without changing pivot pins. No numeric engine releases are reserved [E30, E31, G1, G14, G15].

Engine exit tests run synchronously in the owning plan's area projects. Rendering follows repository backend CI/golden policy. Build/verification is serialized through the shared slot, no stress loops or parallel local builds. This documents-only revision runs document guards, not production builds or full suites [E29, E31, G15].

The following binding lines are copied from `AGENTS.md`.

- In shared worktrees, stage and commit explicit paths. Never use the shared stash for coordination.
- Existing unrelated changes belong to their owner. Do not revert or absorb them.
- New behavior gets a headless test in the matching area test project. Test projects reference only
  the engine projects they use and keep namespaces under `KhaozEngine.Tests.*`.
- Tests that mutate process-global state use a collection definition with
  `DisableParallelization = true`.
- Third-party libraries sit behind dependency-free engine seams with opt-in backends. Read
  `docs/DEPENDENCY-SEAMS.md` before changing an edge.
- KESIZE is a structure ratchet. Put new behavior in a new type. Do not split at an arbitrary line.
  Baseline growth and exemptions require owner approval. Ratchet down freely with
  `scripts/check-file-size.sh --update`.
- Warnings are errors. Fix them at the source. Do not add blanket suppressions or disable the rule.
- CI tests Release. Validate any Debug-only behavior in Release with a configuration-independent
  observable.
- No em-dash or en-dash glyphs in shipped prose or comments. No prose semicolons in Markdown. Run the
  whole-tree guards before completion.

`AGENTS.md` routes implementation to `docs/CONTRIBUTOR-RULES.md`, including dependency seams, documentation sweep, package catalog and release rules. Player-facing GUI text uses `StringId` or `LocalizedText`, raw device input stays in `AppWindow`, and no consumer clients are launched for engine tooling work. Documentation alone does not bump the engine version. Package work rides the next available engine minor after concurrent pivot releases, re-reading main and tags at execution, with no reserved numeric engine versions. Schema numbers 4/5/6 are also baseline-relative and rebase together if occupied. Only the owner tags this program. The owning orchestrator handles integration and packing from pushed main. A delegated implementer stops at its verified commit and never merges, packs the shared feed or tags.

The selected execution method is subagent-driven-development. OA8 approves this R1 plan and its six serial Astra medium implementation dispatches, with fresh Sol xhigh task reviews and final review. The implementation worktree required by HANDOFF is `/Users/antonio/KhaozEngine/.worktrees/wa-r1-document-identity` on `feature/wa-r1-document-identity`, created by the controller from current reconciled released CellOrigin main. Do not execute from this historical planning worktree or merge its entire branch into main. Before dispatch, inspect worktree existence and recheck refs/ancestry. The source baseline below is released main, not this planning branch. No next engine version is reserved.

Future starting guard, from the assigned implementation worktree:

```bash
pwd
test "$(git branch --show-current)" = "feature/wa-r1-document-identity"
git status --short --branch
```

Line anchors below were rechecked with `git show main:<path>` at `b39fb1a3bde9073d9519357b546b138b2c79d957`. Re-locate symbols if main advances. Signatures marked **new** are produced plan contracts, not existing or released APIs. This docs reconciliation runs no code/tests/builds.

## Review Focus

- IDs above 2^53 and int64 exhaustion must roundtrip without JSON rounding or overflow (Task 2).
- Undo followed by a different edit must never reuse an accepted numeric allocation (Task 5).
- A missing transitive LOD/material resource or dependency cycle must refuse the entire closure (Task 3).
- A windowed load or stale tiled index must never mint a complete current authored identity (Task 4).
- Editing caller-owned lists after resolving must not mutate a published snapshot (Task 4).

---

## File Structure

| Path | Responsibility |
| --- | --- |
| `KhaozEngine.MapDoc/MapNativeDocument.cs`, `KhaozEngine.MapDoc/MapNativeMigration.cs`, `KhaozEngine.MapDoc/MapNumericIds.cs` | Native root metadata, format transition and numeric allocation |
| `KhaozEngine.MapDoc/Assets/MapAssetManifest.cs`, `KhaozEngine.MapDoc/Assets/MapAssetClosure.cs`, `KhaozEngine.MapDoc/Assets/MapAssetSource.cs`, `KhaozEngine.MapDoc/Assets/MapResolvedAsset.cs` | GPU-free manifest DTOs, verified dependency closure and byte source |
| `KhaozEngine.MapDoc/MapResolvedDocument.cs`, `KhaozEngine.MapDoc/MapResolver.cs`, `KhaozEngine.MapDoc/MapAuthoredIdentity.cs` | Immutable identities and deterministic native resolution |
| `KhaozEngine.MapEditor/NativePlacementCommands.cs` | Explicit stable-ID remap, label and allocation-safe history |
| `KhaozEngine.MapEdit.Tool/NativeDocumentService.cs` | Native open, summary, validate and save integration |
| `KhaozEngine.Terrain.Render3D/MapAssetManifestAdapter.cs` | One-way adaptation to existing render asset entries |
| `KhaozEngine.MapDoc.Tests/` | New headless area project, only a MapDoc project reference |
| `KhaozEngine.MapEditor.Tests/MapDoc/Native*.cs` | Session and editor identity behavior |
| Existing MapDoc DTO, schema, canonical writer and tiled reader | Carry native metadata in both storage forms |


## Source-Checked Contract and Judgement Calls

Independent ref checks on 2026-10-05 found local `main`, `origin/main` and `v20.25.0^{commit}` at `b39fb1a3bde9073d9519357b546b138b2c79d957`. Remote `main` and the peeled tag match. The annotated tag object is `5f4c2dcd2a316b7108dacca8e64bf39fc53bd94f`. `git merge-base --is-ancestor 'v20.25.0^{commit}' main` exited 0. The main source declares engine 20.25.0 and `public Vector2 CellOrigin { get; init; }` in `ShardedWorldServerConfig`. The controller reports reviewed/released/packed 20.25.0, suite 25,320/0/1,328 and 200 local package files. Those suite/package claims were not rerun or package-checked here.

### Verified existing signatures and paths

These declarations were read through `git show main:<file>`. They remain existing interfaces, distinct from all **new** task outputs.

| Source at released main | Verified declaration or behavior |
| --- | --- |
| `KhaozEngine.MapDoc/MapDocumentFile.cs:83,129,224,234` | `CurrentFormatVersion = 3`, `MapDocument LoadText(string json, MapDocumentLoadOptions? options = null, string? sourcePath = null)`, `string SaveText(MapDocument doc, MapDocRegistry? registry = null)`, `void SaveTiled(MapDocument doc, string directory, MapDocRegistry? registry = null, MapDocumentSaveOptions? save = null)` |
| `KhaozEngine.MapDoc/MapDocumentFile.cs:35` | `void MapDocumentLoadOptions.RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> step)`. The loader stamps the next version. A direct new Upgrade call below also stamps its final version so its purity test can load its returned JSON |
| `KhaozEngine.MapDoc/MapDocumentValidator.cs:13` | `IReadOnlyList<string> Validate(MapDocument doc, MapDocRegistry registry)` |
| `KhaozEngine.MapDoc/MapRuntime.cs:64,172,216` | `TerrainField BuildField(MapDocument doc, MapDocRegistry registry)`, `IReadOnlyList<PropPlacement> BuildPlacements(MapDocument doc, TerrainField field)`. Legacy `PropPlacement.Id` is Kind, not placement ID. Preserve the compatibility API |
| `KhaozEngine.MapDoc/MapDocumentHash.cs:107` | `string OfWorld(MapDocument doc, MapDocRegistry? registry = null)`. A tiled document uses stored index hashes. Native authored identity must recompute complete in-memory content |
| `KhaozEngine.MapDoc/MapCanonical.cs:51`, `MapTiledFile.cs:22`, `MapTiledFile.Save.cs:153`, `MapDocumentSchema.cs:43,79` | Globals writer, manifest reader, manifest writer and derived manifest/tile schemas. Task 1/4 carry metadata through all forms, preserving unindexed-overwrite and partial-save guards |
| `KhaozEngine.MapDoc/MapTileIndex.cs:67` | `bool IsPartial => LoadedCount < _entries.Length` |
| `KhaozEngine.MapEditor/EditorDocument.cs:29,126,141,157` | `EditorDocument(MapDocument doc, MapDocRegistry? registry = null)`, `void Execute(IEditorCommand command)`, `bool Undo()`, `bool Redo()` |
| `KhaozEngine.MapEditor/EditorCommands.cs:12,32,340,376,561` | `IEditorCommand.Apply(MapDocument doc)`, `Revert(MapDocument doc)`, `TryMerge(IEditorCommand next)`, `EditorCommand`, `AddPlacementCommand(MapPlacement placement)`, `RemovePlacementCommand(string id)`, `RenamePlacementCommand(string oldId, string newId)` |
| `KhaozEngine.MapEdit.Tool/MutationService.cs:155,1106` | `MutationResult PlacementRename(string oldId, string newId)`, `MutationResult ElementDuplicate(string kind, string? id = null, int? index = null)`. Native label handling must update the existing rename path, not merely add an unused new verb |
| `KhaozEngine.MapEdit.Tool/MapEditSession.cs:43,120,292,360` | `OpenResult Open(string path, IReadOnlyList<string>? manifestPaths = null)`, `SaveResult Save()`, `ValidateResult Validate(bool verifyWholeWorld = false)`, `MapSummary Summary()` |
| `KhaozEngine.Render3D/Models/AssetManifest.cs:70` | `AssetEntry(string id, string file, float heightMeters, string source, string license, ColliderShape? collider = null, bool surface = false, string? heightmap = null, string? collisionShape = null, string? collisionProxy = null, bool textured = false, string? category = null, string? lodFile = null)` |
| `KhaozEngine.MapEditor.Tests/MapDoc/MapDocumentFileTests.cs:14` | `internal static MapDocument SampleDoc()` in `KhaozEngine.Tests.MapDoc`. Reuse only inside that assembly. The new MapDoc.Tests project supplies its own fixture |
| Existing project graph | MapDoc.Tests does not yet exist. MapEditor.Tests does not directly reference Terrain.Render3D. Task 1 creates the CPU project with existing central xUnit/Test SDK versions, Task 6 adds the adapter-test reference |

Place the render-free manifest seam inside MapDoc. Its adapter lives in Terrain.Render3D, which adds a forward MapDoc dependency. Native resource kinds are a closed enum and payload versions are checked. R1 retains collider/interaction/support resources and policy references in closure, without implementing R3 geometry. R3 must refine revised T4 explicitly. Unsupported referenced prefab payloads remain rejected until R5. Asset-free legacy analytic maps retain their path, with native opt-in expressed by non-null ResolverIdentity. Storage identity remains the existing OfWorld API. Native authored identity is distinct and closure-bearing. Pure migration from format 3 preserves analytic parameters and stable IDs, uses native defaults but leaves ResolverIdentity null, and changes format identity explicitly.

### Task 1: Format-4 native metadata and pure migration

**Files:**
- Create: `KhaozEngine.MapDoc/MapNativeDocument.cs`, `KhaozEngine.MapDoc/MapNativeMigration.cs`
- Create: `KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj`, `KhaozEngine.MapDoc.Tests/NativeDocumentTests.cs`, `KhaozEngine.MapDoc.Tests/NativeFixtures.cs`
- Modify: `KhaozEngine.MapDoc/MapDocument.cs:11-55,167-182`, `KhaozEngine.MapDoc/MapDocumentFile.cs:28-83`, `KhaozEngine.MapDoc/MapDocumentSchema.cs:42-98`, `KhaozEngine.MapDoc/mapdoc.schema.json:7-25,171-197`
- Modify: `KhaozEngine.slnx:1-147` at the MapDoc project entries
- Test: `KhaozEngine.MapDoc.Tests/NativeDocumentTests.cs`

**Interfaces:**
- Consumes existing: `MapDocumentLoadOptions.RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> step)`, `MapDocumentValidator.Validate(MapDocument doc, MapDocRegistry registry)`.
- Produces new: root `MapBounds? PlayableBounds`, `List<MapAssetRef> NativeAssets`, `long NumericIdHighWaterMark`, `MapResolverIdentityDoc? ResolverIdentity`.
- Produces new: placement `long? NumericId`, `string? AssetId`, `string? DisplayName`, preserving `Id`, `Kind`, XYZ/yaw/scale/tags.
- Produces new: `MapNativeMigration.Upgrade(JsonObject source) -> JsonObject`, `MapResolverIdentityDoc(int PayloadVersion, int ResolverVersion)` and `MapAssetRef(string Id, string Path, string Sha256, int PayloadVersion)`.
- Test helper new: `NativeFixtures.AnalyticV3Json() -> string`, a literal baseline-v3 map with one placement and no native assets. Pin id `legacy`, bounds [-100,100] on both axes, TileSize 64, seed 7, WaterLevel -0.5, GentleAmplitude 0, a Meadow biome with BaseHeight 1.5/HillAmplitude 0, and placement `old-inn`/`building_inn` at X -30, Z 20, Y 1.5, yaw 0.371, scale 1.137 and ordered tags `first`, `second`. Do not obtain v3 JSON from the new serializer. New tests use explicit namespaces/usings for MapDoc, Terrain, Numerics, Json/Nodes and xUnit.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeV4_MigrationIsPure_AnalyticAndIdsSurvive()
{
    var original = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
    string before = original.ToJsonString();
    var upgraded = MapNativeMigration.Upgrade(original);
    Assert.Equal(before, original.ToJsonString());
    Assert.Equal(upgraded["bounds"]!.ToJsonString(), upgraded["playableBounds"]!.ToJsonString());
    var doc = MapDocumentFile.LoadText(upgraded.ToJsonString());
    Assert.Equal("old-inn", Assert.Single(doc.Placements).Id);
    Assert.Equal("building_inn", doc.Placements[0].Kind);
    Assert.Null(doc.Placements[0].NumericId);
    Assert.Equal(0L, doc.NumericIdHighWaterMark);
    Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText("""{"formatVersion":2147483647}"""));
}
```

Additional failing assertions in this task's named test files:

```csharp
[Fact]
public void NativeMigration_UnknownFieldsBoundsAndRepeatAreExplicit()
{
    var v3 = JsonNode.Parse(NativeFixtures.AnalyticV3Json())!.AsObject();
    var v4 = MapNativeMigration.Upgrade(v3);
    Assert.Equal(4, v4["formatVersion"]!.GetValue<int>());
    Assert.Equal(v4.ToJsonString(), MapNativeMigration.Upgrade(v4).ToJsonString());
    var doc = MapDocumentFile.LoadText(v4.ToJsonString());
    Assert.Null(doc.ResolverIdentity);
    Assert.Equal(7, doc.Terrain.Seed);
    Assert.Equal(-0.5f, doc.Terrain.WaterLevel);
    var field = MapRuntime.BuildField(doc, MapDocRegistry.CreateDefault());
    Assert.Equal(1.5f, field.SampleHeight(-30,20));
    var placed = Assert.Single(MapRuntime.BuildPlacements(doc,field));
    Assert.Equal("building_inn", placed.Id);
    Assert.Equal(new[] { "first", "second" }, doc.Placements[0].Tags);
    var unknown = (JsonObject)v4.DeepClone();
    unknown["misspelledNativeField"] = true;
    Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(unknown.ToJsonString()));
    var badBounds = (JsonObject)v4.DeepClone();
    badBounds["playableBounds"]!["minX"] = -101;
    Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(badBounds.ToJsonString()));
    Assert.Equal(v3["terrain"]!.ToJsonString(), v4["terrain"]!.ToJsonString());
    Assert.Equal(v3["placements"]!.ToJsonString(), v4["placements"]!.ToJsonString());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t1:red" "${wa_r1_log_dir}/wa-r1-t1-red.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeDocumentTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced properties in `MapNativeDocument.cs`, converting MapDocument/MapPlacement to partial classes at their declarations. `Upgrade(JsonObject source)` deep-clones and stamps formatVersion 4, copies absent playable bounds from storage Bounds and leaves analytic parameters and stable IDs unchanged. Register contiguous 3 to 4 migration and advance the current/schema format to 4 only if available. Close all new object schemas, reject future formats and unknown properties before deserialization. Validate finite playable bounds contained in storage Bounds. Do not widen playability to storage padding. The Step 1 assertions below pin unknown fields, invalid bounds, repeat-upgrade purity and analytic compatibility. Register converters and serializer roundtrip paths before claiming this task green. Attribute native numeric properties with Task 2 converters when that task lands. The test project uses the existing package versions, `IsPackable=false`, namespace `KhaozEngine.Tests.MapDoc` and only MapDoc as its project reference.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t1:green" "${wa_r1_log_dir}/wa-r1-t1-green.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeDocumentTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/MapNativeDocument.cs KhaozEngine.MapDoc/MapNativeMigration.cs KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj KhaozEngine.MapDoc.Tests/NativeDocumentTests.cs KhaozEngine.MapDoc.Tests/NativeFixtures.cs KhaozEngine.MapDoc/MapDocument.cs KhaozEngine.MapDoc/MapDocumentFile.cs KhaozEngine.MapDoc/MapDocumentSchema.cs KhaozEngine.MapDoc/mapdoc.schema.json KhaozEngine.slnx
git diff --cached --check
git commit -m "feat(mapdoc): add native identity document metadata"
```


### Task 2: Exact decimal int64 storage and monotonic allocator

**Files:**
- Create: `KhaozEngine.MapDoc/MapNumericIds.cs`, `KhaozEngine.MapDoc/MapNumericIdJsonConverter.cs`
- Modify: `KhaozEngine.MapDoc/MapDocumentValidator.cs:74-84`, `KhaozEngine.MapDoc/MapDocumentFile.cs:311-338` serializer options factory, `KhaozEngine.MapDoc/mapdoc.schema.json:7-25,171-197` numeric-ID definitions from Task 1
- Test: `KhaozEngine.MapDoc.Tests/NativeNumericIdTests.cs`

**Interfaces:**
- Consumes new: root/placement numeric fields from Task 1.
- Produces new: `MapNumericIds.Reserve(MapDocument document, IEnumerable<long> ids) -> void`, `MapNumericIds.Allocate(MapDocument document) -> long`.
- Produces new: `MapNumericIdJsonConverter : JsonConverter<long>` and equivalent nullable placement converter. Canonical JSON is invariant decimal strings, positive for IDs, nonnegative for the high-water mark.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeIds_LargeDecimal_DeletionAndExhaustion()
{
    var doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
    doc.Placements[0].NumericId = 9007199254740993L;
    MapNumericIds.Reserve(doc, new[] { 9007199254740993L });
    string json = MapDocumentFile.SaveText(doc);
    Assert.Contains("\"9007199254740993\"", json);
    Assert.Equal(9007199254740993L, MapDocumentFile.LoadText(json).Placements[0].NumericId);
    doc.Placements.Clear();
    Assert.Equal(9007199254740994L, MapNumericIds.Allocate(doc));
    doc.NumericIdHighWaterMark = long.MaxValue;
    Assert.Throws<OverflowException>(() => MapNumericIds.Allocate(doc));
    Assert.Equal(long.MaxValue, doc.NumericIdHighWaterMark);
}
```

Additional failing assertions in this task's named test files:

```csharp
[Theory]
[InlineData("0")]
[InlineData("-1")]
[InlineData("+1")]
[InlineData("01")]
[InlineData(" 1")]
[InlineData("1.5")]
[InlineData("9223372036854775808")]
public void NativeId_InvalidDecimalStringsRefuse(string value)
{
    var root = JsonNode.Parse(MapDocumentFile.SaveText(
        MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json())))!.AsObject();
    root["placements"]![0]!["numericId"] = value;
    Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString()));
}
[Fact]
public void NativeId_NumericJsonAndFailedReservationDoNotAllocate()
{
    var doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
    var root = JsonNode.Parse(MapDocumentFile.SaveText(doc))!.AsObject();
    root["placements"]![0]!["numericId"] = 12;
    Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString()));
    MapNumericIds.Reserve(doc,new[] { 11L });
    Assert.Throws<MapDocumentException>(() => MapNumericIds.Reserve(doc,new[] { 12L,12L }));
    Assert.Equal(11L,doc.NumericIdHighWaterMark);
    Assert.Throws<MapDocumentException>(() => MapNumericIds.Reserve(doc,new[] { 12L,0L }));
    Assert.Equal(11L,doc.NumericIdHighWaterMark);
    Assert.Equal(12L,MapNumericIds.Allocate(doc));
    doc.NumericIdHighWaterMark = 0;
    doc.Placements[0].NumericId = 11;
    Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(doc));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t2:red" "${wa_r1_log_dir}/wa-r1-t2-red.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeNumericIdTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the two allocator signatures in `MapNumericIds.cs` using checked arithmetic and prevalidation of all reservations. Reserve above imported IDs and existing high-water mark, never derive allocation from list order. Reject duplicate/zero/negative IDs, a high-water mark below any placement ID, fractions, numeric JSON tokens, leading signs/zeros, whitespace, and values beyond int64. The high-water mark alone may be `"0"`. Reserve is transactional on an invalid input sequence and reports malformed ID/reservation data as MapDocumentException. Allocate overflow reports OverflowException before mutation. Add theories for these invalid encodings and collision cases, including `long.MaxValue` reservation.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t2:green" "${wa_r1_log_dir}/wa-r1-t2-green.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeNumericIdTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/MapNumericIds.cs KhaozEngine.MapDoc/MapNumericIdJsonConverter.cs KhaozEngine.MapDoc/MapDocumentValidator.cs KhaozEngine.MapDoc/MapDocumentFile.cs KhaozEngine.MapDoc/mapdoc.schema.json KhaozEngine.MapDoc.Tests/NativeNumericIdTests.cs
git diff --cached --check
git commit -m "feat(mapdoc): preserve exact numeric identities"
```


### Task 3: Render-free digest-bearing asset closure

**Files:**
- Create: `KhaozEngine.MapDoc/MapNativeVectorConverters.cs`
- Modify: `KhaozEngine.MapDoc/MapDocumentFile.cs:311-338` native serializer registration
- Create: `KhaozEngine.MapDoc/Assets/MapAssetManifest.cs`, `KhaozEngine.MapDoc/Assets/MapAssetSource.cs`, `KhaozEngine.MapDoc/Assets/MapAssetClosure.cs`, `KhaozEngine.MapDoc/Assets/MapResolvedAsset.cs`
- Test: `KhaozEngine.MapDoc.Tests/NativeAssetClosureTests.cs`, `KhaozEngine.MapDoc.Tests/NativeAssetFixtures.cs`

**Interfaces:**
- Consumes new: `MapAssetRef` from Task 1.
- Produces new: `MapNativeVector2Converter : JsonConverter<Vector2>`, `MapNativeVector3Converter : JsonConverter<Vector3>`, `MapNativeJson.Configure(JsonSerializerOptions options) -> void`. Native vectors serialize as closed finite x/y[/z] objects, not default field-ignoring System.Text.Json serialization.
- Produces new: `IMapAssetSource.Read(MapAssetRef reference) -> ReadOnlyMemory<byte>`.
- Produces new: `MapAssetManifestDoc` with `int PayloadVersion`, `List<MapAssetDoc> Assets`, `List<MapResourceDoc> Resources`.
- Produces new: `MapResourceDoc` with `MapAssetRef Reference`, `MapResourceKind Kind`, `List<string> Dependencies`.
- Produces new: `MapAssetDoc` with `string Id`, `string MeshResourceId`, `string? CollisionResourceId`, `string? SelectionResourceId`, `IReadOnlyList<string> SupportResourceIds`, `MaterialResourceIds`, `LodResourceIds`, `LightResourceIds`, `float SourceUnitsToMetres`, `MapLocalBounds RenderBounds`, optional LOD/light bounds, `string Source`, `string License`, `string? Category`, `bool Textured`. Provenance and verified paths supply Task 6 AssetEntry fields. R3 adds versioned interaction policy/geometry to this same closure.
- Produces new: `MapLocalBounds(Vector3 Min, Vector3 Max)`, closed `MapResourceKind` (Manifest, Mesh, Material, Collider, Selection, Surface, Light, Lod, Prefab).
- Produces new: `MapAssetClosure.Load(IReadOnlyList<MapAssetRef> roots, IMapAssetSource source) -> MapAssetClosure`, `GetAsset(string assetId) -> MapResolvedAsset`, `GetResource(string resourceId) -> MapResolvedResource`, `IReadOnlyList<MapResolvedAsset> Assets`, `string Hash`, immutable parsed descriptors and defensively copied resource bytes. Produce `MapResolvedAsset` in `Assets/MapResolvedAsset.cs` in this task, retaining every MapAssetDoc field. Produce `MapResolvedResource(MapAssetRef Reference, MapResourceKind Kind, IReadOnlyList<string> Dependencies, ReadOnlyMemory<byte> Bytes)` with defensive publication. Read methods must not expose mutable backing arrays.
- Test helper new: `NativeAssetFixtures.Valid() -> (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source)`, `MissingLod()`, `Cycle()`, `StaleDigest()`, `FuturePayload()`, `MissingMaterial()`, `DuplicateId()` with the same tuple return type. The valid fixture contains two roots, nonzero render bounds and a transitive mesh/material/LOD closure with source/license metadata. Add `NativeMemoryAssetSource` with `Read(MapAssetRef)` and `Corrupt(string resourceId)` to own isolated fixture bytes, never production assets.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeClosure_RejectsMissingCyclicStaleAndFutureBeforePublish()
{
    var valid = NativeAssetFixtures.Valid();
    var a = MapAssetClosure.Load(valid.Roots, valid.Source);
    var b = MapAssetClosure.Load(valid.Roots.Reverse().ToArray(), valid.Source);
    Assert.Equal(a.Hash, b.Hash);
    foreach (var bad in new[] { NativeAssetFixtures.MissingLod(), NativeAssetFixtures.Cycle(),
        NativeAssetFixtures.StaleDigest(), NativeAssetFixtures.FuturePayload() })
        Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(bad.Roots, bad.Source));
}
```

Additional failing assertions in this task's named test files:

```csharp
[Fact]
public void NativeClosure_TransitiveMaterialDuplicateAndCallerBytesAreGuarded()
{
    var missing = NativeAssetFixtures.MissingMaterial();
    Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(missing.Roots,missing.Source));
    var duplicate = NativeAssetFixtures.DuplicateId();
    Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(duplicate.Roots,duplicate.Source));
    var valid = NativeAssetFixtures.Valid();
    var closure = MapAssetClosure.Load(valid.Roots,valid.Source);
    byte[] before = closure.GetResource("mesh").Bytes.ToArray();
    valid.Source.Corrupt("mesh");
    Assert.Equal(before,closure.GetResource("mesh").Bytes.ToArray());
    Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(valid.Roots,valid.Source));
    Assert.DoesNotContain(typeof(MapAssetClosure).Assembly.GetReferencedAssemblies(),
        a => a.Name!.Contains("Render3D") || a.Name.Contains("Gpu") || a.Name.Contains("TileWorld"));
}
[Fact]
public void NativeVectorPayload_RoundTripsNonzeroAndRefusesUnknownComponents()
{
    var options = new JsonSerializerOptions();
    MapNativeJson.Configure(options);
    var expected = new Vector3(0.23f,1.5f,-0.17f);
    Assert.Equal(expected,JsonSerializer.Deserialize<Vector3>(JsonSerializer.Serialize(expected,options),options));
    Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Vector3>(
        """{"x":0.23,"y":1.5,"z":-0.17,"w":1}""",options));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t3:red" "${wa_r1_log_dir}/wa-r1-t3-red.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeAssetClosureTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the vector converter Read/Write overrides in `MapNativeVectorConverters.cs`, registering them for document and manifest serializers without globally enabling IncludeFields. Pin nonzero vector JSON roundtrip and unknown-component refusal. Implement `MapAssetClosure.Load(...)` in `Assets/MapAssetClosure.cs`. Payload 1 owns named descriptors and resource edges. Verify SHA-256 lowercase hex over exact resource bytes, payload versions, unique resource/asset IDs, finite source units and bounds, required resource kinds and an acyclic transitive graph. Sort closure nodes by ordinal resource ID, retain semantic list/tag order, and deep-copy inputs. A source resolves relative paths against its chosen root and does not use current working directory. Collider/selection/surface bytes are closure members now, with shape interpretation delivered by R3. Reject referenced prefab payloads until R5 installs their supported handler. The additional Step 1 assertions cover missing transitive material, duplicate IDs and mutation of source bytes. Resolve fixture paths relative to an explicit isolated root. Changing process working directory, if used to prove this, requires a nonparallel process-global collection and a finally restoration. Do not acquire Render3D, GPU, TileWorld or game references.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t3:green" "${wa_r1_log_dir}/wa-r1-t3-green.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeAssetClosureTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/MapNativeVectorConverters.cs KhaozEngine.MapDoc/MapDocumentFile.cs KhaozEngine.MapDoc/Assets/MapAssetManifest.cs KhaozEngine.MapDoc/Assets/MapAssetSource.cs KhaozEngine.MapDoc/Assets/MapAssetClosure.cs KhaozEngine.MapDoc/Assets/MapResolvedAsset.cs KhaozEngine.MapDoc.Tests/NativeAssetClosureTests.cs KhaozEngine.MapDoc.Tests/NativeAssetFixtures.cs
git diff --cached --check
git commit -m "feat(mapdoc): resolve verified native asset closure"
```


### Task 4: Immutable placements and complete authored identity

**Files:**
- Create: `KhaozEngine.MapDoc/MapResolvedDocument.cs`, `KhaozEngine.MapDoc/MapResolver.cs`, `KhaozEngine.MapDoc/MapAuthoredIdentity.cs`
- Modify: `KhaozEngine.MapDoc/MapCanonical.cs:51-76`, `KhaozEngine.MapDoc/MapTiledFile.cs:91-150`, `KhaozEngine.MapDoc/MapTiledFile.Save.cs:151-180`, `KhaozEngine.MapDoc/MapDocumentSchema.cs:42-98`
- Test: `KhaozEngine.MapDoc.Tests/NativeResolverTests.cs`, `KhaozEngine.MapDoc.Tests/NativeStorageIdentityTests.cs`, `KhaozEngine.MapDoc.Tests/NativeStorageFixture.cs`, `KhaozEngine.MapDoc.Tests/NativeResolverFixtures.cs`

**Interfaces:**
- Consumes existing: `MapDocumentHash.OfWorld(MapDocument doc, MapDocRegistry? registry = null) -> string`, `MapDocument.Tiles` and its partial state.
- Consumes new: asset closure, numeric IDs and metadata.
- Produces new: `MapTransform(Vector3 Position, float YawRadians, float Scale)` with `TransformPoint(Vector3 local) -> Vector3` and `Compose(MapTransform parent,MapTransform local) -> MapTransform` using +Y yaw, scale once. Compose transforms local Position through the parent, adds yaws and multiplies positive scales.
- Consumes Task 3: immutable `MapResolvedAsset` and MapResolvedResource snapshots. This task does not define those types a second time.
- Produces new: `MapResolvedPlacement(string PlacementId, string Kind, string AssetId, long? NumericId, MapTransform Transform, IReadOnlyList<string> Tags)`.
- Produces new: `MapResolveOptions(string BuilderId, int BuilderVersion, string OptionsHash, int ResolverVersion = 1)`.
- Produces new: `MapResolver.Resolve(MapDocument document, MapAssetClosure assets, Func<float,float,float> supportHeight, MapResolveOptions options) -> MapResolvedDocument`.
- Produces new: `MapResolvedDocument.Placements`, `Assets`, `PlayableBounds`, `StorageBounds`, `AuthoredHash` as immutable properties. `MapAuthoredIdentity.Compute(MapDocument document, MapAssetClosure assets, MapResolveOptions options) -> string`.
- Test helper new: `NativeStorageFixture : IDisposable` exposes `MapDocument Complete`, `string TiledPath`, `MapDocument LoadWindow()` from a tiled native map with two occupied tiles and one loaded tile, at TileSize 64. It uses the same closure/options as NativeResolverFixtures. Dispose cleans only its own temporary directory. Add it beside NativeStorageIdentityTests.
- Test helper new: `NativeResolverFixtures.Create() -> (MapDocument Document, MapAssetClosure Assets, MapResolveOptions Options)` containing two native placements `a` and `b`, different Kind values, numeric IDs 11/12, AssetId bindings to Task 3 assets, ordered tags `first`, `second`, explicit Y 1.5 and 2.5, X/Z (-30,20) and (70,-70), playable bounds within [-100,100], TileSize 64 and ResolverIdentity(1,1). Helpers also expose `WithChangedClosure(MapDocument document, MapAssetClosure assets) -> (MapDocument Document, MapAssetClosure Assets)`, deep-cloning the document, changing one material byte, recomputing affected manifest/root digests and replacing cloned NativeAssets with matching new root references. It retains the old input document/closure untouched. An unrelated closure must be rejected, not accepted as another identity input.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeResolver_ReorderingAndCallerEditsCannotChangeSnapshot()
{
    var f = NativeResolverFixtures.Create();
    var a = MapResolver.Resolve(f.Document, f.Assets, (_, _) => 0f, f.Options);
    f.Document.Placements.Reverse();
    var b = MapResolver.Resolve(f.Document, f.Assets, (_, _) => 0f, f.Options);
    Assert.Equal(a.AuthoredHash, b.AuthoredHash);
    Assert.Equal(a.Placements.Select(p => p.PlacementId), b.Placements.Select(p => p.PlacementId));
    string oldTag = a.Placements[0].Tags[0];
    f.Document.Placements.Single(p => p.Id == a.Placements[0].PlacementId).Tags[0] = "edited";
    Assert.Equal(oldTag, a.Placements[0].Tags[0]);
    Assert.NotEqual(a.AuthoredHash, MapAuthoredIdentity.Compute(f.Document, f.Assets, f.Options));
}
[Fact]
public void NativeIdentity_PartialDocumentCannotClaimCompleteWorld()
{
    using var f = new NativeStorageFixture();
    var inputs = NativeResolverFixtures.Create();
    var partial = f.LoadWindow();
    Assert.True(partial.Tiles!.IsPartial);
    Assert.Throws<MapDocumentException>(() => MapAuthoredIdentity.Compute(partial,inputs.Assets,inputs.Options));
    Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(partial,inputs.Assets,(_,_) => 0,inputs.Options));
}
```

Additional failing assertions in this task's named test files:

```csharp
[Fact]
public void NativeIdentity_CompleteTiledDirtyClosureAndRetileAreDistinct()
{
    using var storage = new NativeStorageFixture();
    var f = NativeResolverFixtures.Create();
    var monolithic = MapDocumentFile.LoadText(MapDocumentFile.SaveText(storage.Complete));
    var tiled = MapDocumentFile.LoadTiled(storage.TiledPath);
    string expected = MapAuthoredIdentity.Compute(monolithic,f.Assets,f.Options);
    Assert.Equal(expected,MapAuthoredIdentity.Compute(tiled,f.Assets,f.Options));
    var before = MapResolver.Resolve(tiled,f.Assets,(_,_) => 7,f.Options);
    tiled.Placements[0].X += 0.23f;
    Assert.NotEqual(expected,MapAuthoredIdentity.Compute(tiled,f.Assets,f.Options));
    tiled.Placements[0].X -= 0.23f;
    // Use a fresh load rather than assuming add/subtract returns identical float bits.
    tiled = MapDocumentFile.LoadTiled(storage.TiledPath);
    tiled.DisplayName = "label only";
    tiled.Schema = "label-only-schema.json";
    Assert.Equal(expected,MapAuthoredIdentity.Compute(tiled,f.Assets,f.Options));
    var changed = NativeResolverFixtures.WithChangedClosure(tiled,f.Assets);
    Assert.NotEqual(expected,MapAuthoredIdentity.Compute(changed.Document,changed.Assets,f.Options));
    Assert.Throws<MapDocumentException>(() => MapAuthoredIdentity.Compute(tiled,changed.Assets,f.Options));
    Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(tiled,changed.Assets,(_,_) => 7,f.Options));
    tiled.TileSize = 128;
    Assert.NotEqual(expected,MapAuthoredIdentity.Compute(tiled,f.Assets,f.Options));
    Assert.Equal(before.Placements.Select(p => p.Transform),
        MapResolver.Resolve(tiled,f.Assets,(_,_) => 7,f.Options).Placements.Select(p => p.Transform));
    f.Document.Placements[0].Y = null;
    f.Document.Placements[1].Y = 2.5f;
    var snapped = MapResolver.Resolve(f.Document,f.Assets,(_,_) => 7,f.Options);
    Assert.Equal(7f,snapped.Placements.Single(p => p.PlacementId == "a").Transform.Position.Y);
    Assert.Equal(2.5f,snapped.Placements.Single(p => p.PlacementId == "b").Transform.Position.Y);
    Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(f.Document,f.Assets,(_,_) => float.NaN,f.Options));
}
[Fact]
public void NativeTransform_ComposesYawPositionAndScaleOnce()
{
    var parent = new MapTransform(new Vector3(0.23f,1.5f,0.17f),0.371f,1.137f);
    var local = new MapTransform(new Vector3(2,0.4f,3),0.2f,0.8f);
    var composed = MapTransform.Compose(parent,local);
    Assert.Equal(parent.TransformPoint(local.Position),composed.Position);
    Assert.Equal(0.371f + 0.2f,composed.YawRadians);
    Assert.Equal(1.137f * 0.8f,composed.Scale);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t4:red" "${wa_r1_log_dir}/wa-r1-t4-red.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeResolverTests|FullyQualifiedName~NativeStorageIdentityTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced resolver and identity signatures in their new files. Sort placement records by stable ordinal ID, preserve ordered tags, keep Kind/AssetId/NumericId separate and snap only null Y through the supplied supportHeight. Reject nonfinite transforms and support samples. Before resolve/hash, require the document's NativeAssets root IDs/paths/digests/payload versions to match the supplied closure. Refuse mismatches before publishing. Hash normalized complete in-memory document, TileSize, complete closure and resolver/builder/options versions. Display labels and `$schema` remain nonsemantic. Do not use a tiled document's cached world hash for edited native data. Keep legacy hash behavior for asset-free analytic maps. Carry all new globals in tiled write/read/schema. Assert monolithic versus fully loaded tiled equality at TileSize 64, dirty-content identity changes before save, re-tiling changes storage identity without moving coordinates, and a partial document refuses complete resolve/hash. Separate any explicit loaded-window identity from a complete result. Add two-independent-head identity tables and null-Y/explicit-Y tests.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t4:green" "${wa_r1_log_dir}/wa-r1-t4-green.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeResolverTests|FullyQualifiedName~NativeStorageIdentityTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/MapResolvedDocument.cs KhaozEngine.MapDoc/MapResolver.cs KhaozEngine.MapDoc/MapAuthoredIdentity.cs KhaozEngine.MapDoc/MapCanonical.cs KhaozEngine.MapDoc/MapTiledFile.cs KhaozEngine.MapDoc/MapTiledFile.Save.cs KhaozEngine.MapDoc/MapDocumentSchema.cs KhaozEngine.MapDoc.Tests/NativeResolverTests.cs KhaozEngine.MapDoc.Tests/NativeStorageIdentityTests.cs KhaozEngine.MapDoc.Tests/NativeStorageFixture.cs KhaozEngine.MapDoc.Tests/NativeResolverFixtures.cs
git diff --cached --check
git commit -m "feat(mapdoc): publish immutable native world identity"
```


### Task 5: Allocation-safe history, labels and explicit remap

**Files:**
- Create: `KhaozEngine.MapEditor/NativePlacementCommands.cs`, `KhaozEngine.MapEditor/NativePlacementReferences.cs`
- Modify: `KhaozEngine.MapEditor/EditorCommands.cs:340-417,561-597`, `KhaozEngine.MapEditor/EditorDocument.cs:126-174` (native persistent allocator dirty state), `KhaozEngine.MapEditor/EditorHistory.cs:46-84` (rejected redo/undo safety), `KhaozEngine.MapEditor/EditorCommands.Invalidation.cs:47-61` placement identity and rename effects
- Modify: `KhaozEngine.MapEdit.Tool/MutationService.cs:92-173,1106-1160`
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativePlacementHistoryTests.cs`
- Create: `KhaozEngine.MapEditor.Tests/MapDoc/NativePlacementHistoryFixture.cs`

**Interfaces:**
- Consumes existing: `EditorDocument.Execute(IEditorCommand command) -> void`, `Undo() -> bool`, `Redo() -> bool`, `AddPlacementCommand(MapPlacement placement)`, `RemovePlacementCommand(string id)`, `MutationService.ElementDuplicate(string kind, string? id = null, int? index = null) -> MutationResult`.
- Produces new: `AllocateNativePlacementCommand(MapPlacement placement, bool allocateNumericId) : EditorCommand`, `SetPlacementLabelCommand(string placementId, string label) : EditorCommand`, `RemapPlacementIdCommand(string oldId, string newId) : EditorCommand`.
- Produces new: `MutationService.PlacementLabel(string placementId,string label) -> MutationResult`, `PlacementRemapId(string oldId,string newId) -> MutationResult`, explicit native service operations.
- Produces new: `NativePlacementReferences.Remap(MapDocument document, string oldId, string newId) -> void`, one authoritative reference visitor later extended by R5/R6. Numeric identity never remaps.
- Test helper new: `NativePlacementHistoryFixture : IDisposable` in NativePlacementHistoryFixture.cs exposes `MapDocument Document`, `EditorDocument Editor`, `MapEditSession Session`, `MutationService Service`, `MapPlacement NewProp(string id)`. It starts with a complete valid native opted-in document, ResolverIdentity(1,1), matching digest-bearing roots and manifest assets for every existing placement, plus a bound `prop` asset. NewProp supplies Id, Kind=prop, AssetId=prop and ordered tags. Its isolated resource directory/session use the same declared root closure. Validate the complete initial fixture before taking before-state snapshots. Use SampleDoc only for legacy regression tests, not as an unbound native fixture. MarkSaved acknowledges the allocator high-water mark as well as history. New commands implement `Label`, internal `AffectsWorld`, `Apply(MapDocument doc)` and `Revert(MapDocument doc)` in the MapEditor assembly. Native ID remap visits only typed references, never opaque tags or arbitrary text.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeHistory_UndoRedoAndNewBranchNeverReuseNumericId()
{
    using var f = new NativePlacementHistoryFixture();
    var doc = f.Document;
    var ed = f.Editor;
    ed.Execute(new AllocateNativePlacementCommand(f.NewProp("a"), true));
    long id = doc.Placements.Single(p => p.Id == "a").NumericId!.Value;
    Assert.True(ed.Undo());
    Assert.Equal(id, doc.NumericIdHighWaterMark);
    Assert.True(ed.IsDirty);
    Assert.True(ed.Redo());
    Assert.Equal(id, doc.Placements.Single(p => p.Id == "a").NumericId);
    Assert.True(ed.Undo());
    ed.Execute(new AllocateNativePlacementCommand(f.NewProp("b"), true));
    Assert.Equal(id + 1, doc.Placements.Single(p => p.Id == "b").NumericId);
}
```

Additional failing assertions in this task's named test files:

```csharp
[Fact]
public void NativeHistory_LabelRemapAndRejectedAllocationAreAtomic()
{
    using var f = new NativePlacementHistoryFixture();
    var doc = f.Document;
    var ed = f.Editor;
    ed.Execute(new AllocateNativePlacementCommand(f.NewProp("a"),true));
    long numeric = doc.Placements.Single(p => p.Id == "a").NumericId!.Value;
    ed.Execute(new SetPlacementLabelCommand("a","Renamed"));
    Assert.Equal(numeric,doc.Placements.Single(p => p.Id == "a").NumericId);
    Assert.Equal("Renamed",doc.Placements.Single(p => p.Id == "a").DisplayName);
    ed.Execute(new RemapPlacementIdCommand("a","b"));
    Assert.Equal(numeric,doc.Placements.Single(p => p.Id == "b").NumericId);
    Assert.True(ed.Undo());
    Assert.Equal(numeric,doc.Placements.Single(p => p.Id == "a").NumericId);
    doc.NumericIdHighWaterMark = long.MaxValue;
    string before = MapDocumentFile.SaveText(doc);
    string? undo = ed.History.UndoLabel;
    string? redo = ed.History.RedoLabel;
    bool dirty = ed.IsDirty;
    Assert.Throws<OverflowException>(() => ed.Execute(new AllocateNativePlacementCommand(f.NewProp("overflow"),true)));
    Assert.Equal(before,MapDocumentFile.SaveText(doc));
    Assert.Equal(undo,ed.History.UndoLabel);
    Assert.Equal(redo,ed.History.RedoLabel);
    Assert.Equal(dirty,ed.IsDirty);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t5:red" "${wa_r1_log_dir}/wa-r1-t5-red.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativePlacementHistoryTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the three command constructors and Apply/Revert in `NativePlacementCommands.cs`. Allocate once on first successful apply, capture IDs for redo, retain accepted high-water marks on undo and deletion. Prepare and validate a fully bound clone, including its matching native asset roots, before publishing so rejected edits restore both history and allocator. Native rename edits DisplayName, with a separate explicit remap command checking all references and stable-ID uniqueness. Preserve the legacy rename behavior for maps that do not opt into native identity. Route existing GUI RenamePlacementCommand and MutationService.PlacementRename to label semantics for native opt-in, using explicit RemapPlacementIdCommand for ID changes. Persisted allocation surviving undo must leave EditorDocument dirty even when history returns to the old saved depth. Rejected execute/redo must restore stacks/dirty state and allocator atomically. Current EditorHistory pops redo before Apply, so Task 5 owns that native failure boundary rather than assuming it already exists. Update duplication to copy AssetId and ordered tags but allocate fresh stable/numeric IDs once. Add rejection-at-exhaustion, label/remap numeric invariance, duplicate-and-redo, and move/yaw/scale/delete/reload tests. Do not add a no-loss byte-roundtrip assertion across accepted numeric allocation undo, since the high-water mark intentionally persists.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t5:green" "${wa_r1_log_dir}/wa-r1-t5-green.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativePlacementHistoryTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapEditor/NativePlacementCommands.cs KhaozEngine.MapEditor/NativePlacementReferences.cs KhaozEngine.MapEditor/EditorCommands.cs KhaozEngine.MapEditor/EditorDocument.cs KhaozEngine.MapEditor/EditorHistory.cs KhaozEngine.MapEditor/EditorCommands.Invalidation.cs KhaozEngine.MapEdit.Tool/MutationService.cs KhaozEngine.MapEditor.Tests/MapDoc/NativePlacementHistoryTests.cs KhaozEngine.MapEditor.Tests/MapDoc/NativePlacementHistoryFixture.cs
git diff --cached --check
git commit -m "feat(mapeditor): preserve native identity through history"
```


### Task 6: Native lifecycle validation and render adapter

**Files:**
- Create: `KhaozEngine.MapEdit.Tool/NativeDocumentService.cs`
- Create: `KhaozEngine.Terrain.Render3D/MapAssetManifestAdapter.cs`
- Modify: `KhaozEngine.MapEdit.Tool/MapEditSession.cs:43-138,292-405`, `KhaozEngine.MapEdit.Tool/Results.cs:48-62`
- Modify: `KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj:1-19` project references, `KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj` adapter-test project reference
- Modify: `KhaozEngine.MapDoc/README.md:1-26`, `KhaozEngine.MapEdit.Tool/README.md:3-28`, `docs/USING-KHAOZENGINE.md:56-81`
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativeLifecycleTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeManifestAdapterTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeLifecycleFixture.cs`

**Interfaces:**
- Consumes existing: `MapEditSession.Open(string path, IReadOnlyList<string>? manifestPaths = null) -> OpenResult`, `Save() -> SaveResult`, `Validate(bool verifyWholeWorld = false) -> ValidateResult`, `Summary() -> MapSummary`.
- Consumes existing: `AssetEntry(string id, string file, float heightMeters, string source, string license, ColliderShape? collider = null, bool surface = false, string? heightmap = null, string? collisionShape = null, string? collisionProxy = null, bool textured = false, string? category = null, string? lodFile = null)`.
- Produces new: `NativeDocumentService.ValidateComplete(MapDocument document, IMapAssetSource source, MapResolveOptions options) -> MapResolvedDocument`, `NativeDocumentSummary(string AuthoredHash, int PlacementCount, int NumericIdCount, long NumericIdHighWaterMark, string ClosureHash)` with decimal converters on numeric fields.
- Produces new: `MapAssetManifestAdapter.ToAssetEntry(MapResolvedAsset asset, string resourceRoot) -> AssetEntry`. Preserve-source-scale mesh loading is R3, the adapter must not promise normalized native runtime meshes in R1.
- New lifecycle fixture `NativeLifecycleFixture : IDisposable` exposes `MapEditSession Session`, `string ValidPath`, `string BadPath`, `MapResolvedAsset Asset`, `string ResourceRoot`, `void CorruptResource()`, `byte[] ReadSavedBytes()` and prepares a native document plus local resources in an isolated temporary directory.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeLifecycle_StaleClosureRefusesSaveWithoutChangingBytes()
{
    using var f = new NativeLifecycleFixture();
    byte[] before = f.ReadSavedBytes();
    f.CorruptResource();
    Assert.Throws<MapDocumentException>(() => f.Session.Save());
    Assert.Equal(before, f.ReadSavedBytes());
    Assert.False(f.Session.Validate(verifyWholeWorld: true).Valid);
}
```

Additional failing assertions in this task's named test files:

```csharp
[Fact]
public void NativeLifecycle_BadOpenAndAdapterPreservePublishedSession()
{
    using var f = new NativeLifecycleFixture();
    string id = f.Session.Summary().Id;
    byte[] before = f.ReadSavedBytes();
    Assert.Throws<MapDocumentException>(() => f.Session.Open(f.BadPath));
    Assert.Equal(id,f.Session.Summary().Id);
    Assert.Equal(before,f.ReadSavedBytes());
    var adapted = MapAssetManifestAdapter.ToAssetEntry(f.Asset,f.ResourceRoot);
    Assert.Equal(f.Asset.Id,adapted.Id);
    Assert.Equal(f.Asset.Source,adapted.Source);
    Assert.Equal(f.Asset.License,adapted.License);
    Assert.True(Path.IsPathRooted(adapted.File));
    Assert.True(File.Exists(adapted.File));
    Assert.Equal((f.Asset.RenderBounds.Max.Y-f.Asset.RenderBounds.Min.Y)
        * f.Asset.SourceUnitsToMetres,adapted.HeightMeters);
    Assert.DoesNotContain(typeof(MapResolver).Assembly.GetReferencedAssemblies(),
        a => a.Name!.Contains("Render3D") || a.Name.Contains("Gpu")
            || a.Name.Contains("MapEditor") || a.Name.Contains("TileWorld"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t6:red" "${wa_r1_log_dir}/wa-r1-t6-red.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeLifecycleTests|FullyQualifiedName~NativeManifestAdapterTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `ValidateComplete(...)` in `NativeDocumentService.cs`, routing native open/save/full-validation/summary through the resolver. For R1 analytic native documents, ValidateComplete obtains supportHeight from `MapRuntime.BuildField(document, MapDocRegistry.CreateDefault()).SampleHeight`. R2 later supplies its canonical authored sampler, with no hidden bilinear fallback. Closure failures happen before session replacement or filesystem writes. Validate catches MapDocumentException as an additive false Valid/closure finding, while Open and Save throw. Do not return success from summary/whole validation when closure is incomplete. Use a staged sibling write and atomic promotion for native monolithic save, preserving the tiled unindexed-overwrite guard. Test bad open preserves the existing session and missing complete closure does not report whole-world validity. Implement `ToAssetEntry(...)` in Terrain.Render3D using verified paths and keeping collision/light/LOD references in the native snapshot, never loading meshes in MapDoc. Add the forward MapDoc project reference to Terrain.Render3D and Terrain.Render3D reference to MapEditor.Tests for adapter tests. AssetEntry.HeightMeters is computed from verified local render bounds times SourceUnitsToMetres for this compatibility adapter only. It does not activate native source-scale loading until R3. Add adapter resource tests and assembly-reference checks that MapDoc lacks GPU/Render3D/MapEditor/TileWorld. Extend lifecycle result DTOs additively. Update living API docs and perform the round verification below before handing R1 to the orchestrator.

- [ ] **Step 4: Run test to verify it passes**

Run: `bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-t6:green" "${wa_r1_log_dir}/wa-r1-t6-green.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeLifecycleTests|FullyQualifiedName~NativeManifestAdapterTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapEdit.Tool/NativeDocumentService.cs KhaozEngine.Terrain.Render3D/MapAssetManifestAdapter.cs KhaozEngine.MapEdit.Tool/MapEditSession.cs KhaozEngine.MapEdit.Tool/Results.cs KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj KhaozEngine.MapDoc/README.md KhaozEngine.MapEdit.Tool/README.md docs/USING-KHAOZENGINE.md KhaozEngine.MapEditor.Tests/MapDoc/NativeLifecycleTests.cs KhaozEngine.MapEditor.Tests/MapDoc/NativeManifestAdapterTests.cs KhaozEngine.MapEditor.Tests/MapDoc/NativeLifecycleFixture.cs
git diff --cached --check
git commit -m "feat(mapedit): validate native lifecycle and asset adaptation"
```


## Round Verification and Handoff

Run from the implementation worktree root, sequentially. Focused tests above are the task red/green cycle. The full solution suite runs once at round finish after the solution build, not once per task and never in a repeat loop. Re-run only when a subsequent code change or integration conflict requires it. The slot runner returns the target exit code. Exit 75 means no command ran because the slot was busy. Hand that result to the controller, never retry a failed test or loop verification.

```bash
mkdir -p local-feed "${wa_r1_log_dir}"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-focused-KhaozEngine.MapDoc.Tests:finish" "${wa_r1_log_dir}/wa-r1-focused-KhaozEngine.MapDoc.Tests-finish.log" -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~Native"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-focused-KhaozEngine.MapEditor.Tests:finish" "${wa_r1_log_dir}/wa-r1-focused-KhaozEngine.MapEditor.Tests-finish.log" -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~Native|FullyQualifiedName~MapDoc"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-build:finish" "${wa_r1_log_dir}/wa-r1-build-finish.log" -- dotnet build KhaozEngine.slnx -c Release
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-format:finish" "${wa_r1_log_dir}/wa-r1-format-finish.log" -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-suite:finish" "${wa_r1_log_dir}/wa-r1-suite-finish.log" -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-check-dashes:finish" "${wa_r1_log_dir}/wa-r1-check-dashes-finish.log" -- sh scripts/check-dashes.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-check-prose:finish" "${wa_r1_log_dir}/wa-r1-check-prose-finish.log" -- sh scripts/check-prose.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-check-file-size:finish" "${wa_r1_log_dir}/wa-r1-check-file-size-finish.log" -- sh scripts/check-file-size.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-check-agent-instructions:finish" "${wa_r1_log_dir}/wa-r1-check-agent-instructions-finish.log" -- sh scripts/check-agent-instructions.sh --tree
bash /tmp/grimhollow-orch/slot-run.sh "wa-r1-check-doc-versions:finish" "${wa_r1_log_dir}/wa-r1-check-doc-versions-finish.log" -- bash scripts/check-doc-versions.sh
```

Require exit 0 from every command, zero warnings, nonempty focused selections, no format diff and no guard failures. These commands are future implementation verification, not authorization to run tests in the documents lane. GPU facts are skipped by ordinary `dotnet test`. Any visual golden additions use the relevant backend CI bake from `docs/CROSS-PLATFORM.md`, serialized and without booting a consumer. No local stress or repeated suite runs.

The last task also updates the package README, `docs/USING-KHAOZENGINE.md` and every stale Markdown reference for its added APIs. An orchestrator re-reads main, tags and `Directory.Build.props`, selects the next available engine minor after pivot releases, rides an existing staged version only when it belongs to this same round, and updates `CHANGELOG.md` plus all declarations checked by `check-doc-versions.sh`. Delegated workers record verified commits in Outcome and return them for integration. Each round is its own minor capability release and does not share a pivot or another round's release number. No engine release number is reserved here and no worker tags. Build, test and guard failures block the round's exit claim.

## Self-Review

Coverage: C1 schema/migration Task 1, numeric semantics Task 2, closure Task 3, immutable identity/storage Task 4, history/remap Task 5, lifecycle/adapter Task 6. Old analytic MapDoc regression tests run in the existing MapEditor.Tests round filter. Native opt-in and the preserved allocation dirty state are explicit. The six task boundaries are retained. Every task has one test cycle and a reviewable deliverable. Existing interfaces and file paths were rechecked against released main `b39fb1a3bde9073d9519357b546b138b2c79d957`, new interfaces are explicitly produced before consumption, and the five Review Focus cases each have a named assertion in an owning task. The snippets pin behavior rather than implement algorithms. No later round's complete GUI, MCP, importer or rendering workflow is claimed here.

## Outcome

### Documentation reconciliation, 2026-10-05

- Approval stage: specs approved with revised T4. R1 plan approval and execution remain pending. No round capability release is claimed.
- Dependency caveat: R1 is reconciled against released CellOrigin main. Its owner plan review is pending. Start implementation from current reconciled engine main after the released CellOrigin change, never by merging this historical planning branch.
- Source inventory: old fixture counts are regression evidence only. R6/R11 refreeze the actual accepted shipped source, including negative x regions, before adoption acceptance.
- Actual checks: released-main `git show` signature/path review, local and remote tag/main identity and ancestry, six-task self-review, no builds/tests. Whole-tree documentation guard results for this revision are recorded below. Approval evidence is game commit `3e46fac49c1d15adf84c948fdc17f4b6606827aa`, reported pushed by the controller, plus this dispatch. That commit is not an engine implementation baseline. No package, tag, execution SHA or self-recording commit is invented.

- Reconciled requirements: Released b39fb1a3b source signatures and required HANDOFF worktree are reconciled. Six tasks are retained. Task 3 owns immutable assets, Task 4 consumes them. Resolver/history/lifecycle fixture files are explicit. The changed-closure assertion updates document root digests and separately rejects mismatches. History fixtures have complete native bindings. Malformed C# strings and shared-slot red/green logging are corrected. Self-review covered spec coverage, step granularity, types, Review Focus assertions and proportion. Owner R1 plan approval remains open.
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


### OA8 execution start, 2026-10-05

R1 is approved for execution, with no implementation task completed yet. Engine main, origin/main and
v20.25.0 were rechecked at b39fb1a3b before worktree creation. The shared slot runner SHA-256 is
c5210ccc108e7b6f5d5b72328db6e6fcfef972ae8ea18701e17343b4035f66f7, matching HANDOFF.
Task recovery uses this plan's SDD workspace progress.md and the tracked Outcome entries below.
The final Release suite runs once after reconciliation, not once per task. No baseline build was repeated
because the source is the reviewed/released 20.25.0 baseline and Task 1 begins with focused RED evidence.

## Preflight, 2026-10-05

Checked against approved c89f260d2 plan, approved native spec and released b39fb1a3b source.

| Tasks | Shared contract or file | Check |
| --- | --- | --- |
| 1 / 2 | Numeric metadata, schema and serializer | Task 1 introduces metadata. Task 2 owns exact decimal encoding and allocation. Task 1 must roundtrip its own defaults, with no forward reference to an unimplemented type |
| 1 / 3 | MapAssetRef and MapDocumentFile options | Task 1 defines root references. Task 3 consumes them and adds manifest/vector serialization without global IncludeFields |
| 1 / 4 | Root/placement metadata and storage schema | Task 4 carries the same fields through tiled globals and complete identity, preserving analytic compatibility |
| 1 / 5 | ResolverIdentity, stable/semantic/numeric IDs | Native opt-in and labels are explicit. Valid native fixtures replace unbound legacy fixture reuse |
| 1 / 6 | Native opt-in and lifecycle schema | Native lifecycle uses the metadata, while legacy analytic behavior remains compatible |
| 2 / 3 | MapDocumentFile serializer registration | Numeric-property converters and vector converters coexist without widening unrelated primitive serialization |
| 2 / 4 | Numeric identity and authored hash | Exact IDs/high-water mark enter canonical identity, with no array-position identity |
| 2 / 5 | Reserve/Allocate and transaction history | Accepted high-water marks survive undo. Rejected operations restore allocator, stacks and dirty state |
| 2 / 6 | Numeric summary fields | Lifecycle summaries use exact numeric encoding, not double tokens |
| 3 / 4 | Immutable assets and matching root closure | Task 3 owns returned asset/resource types. Task 4 checks root digest match before hash/resolve |
| 3 / 5 | Native fixture asset bindings | History fixture supplies valid roots/assets for every native placement |
| 3 / 6 | Asset provenance/paths/bounds | Adapter consumes verified descriptors. GPU-free MapDoc stays independent of the rendering adapter |
| 4 / 5 | Stable typed references and immutability | History mutates authoring data and cannot mutate already-published resolved snapshots |
| 4 / 6 | Resolve options, support sampler and authored identity | Lifecycle validates complete closure with the analytic sampler in R1. R2 later supplies authored triangles |
| 5 / 6 | Session dirty/save/undo state | Persistent allocation remains dirty across undo. Validation refusal publishes no session/file changes |

| Task | Internal agreement check |
| --- | --- |
| 1 | Pure migration, v4 stamp/repeat behavior, analytic fixture and invalid-bounds assertions match metadata/schema work. New project/file staging is explicit |
| 2 | Large int64, invalid encodings, reservation atomicity and exhaustion match the allocator/converter interfaces |
| 3 | Closure transitivity, vectors and immutable byte publication have named fixtures and files. Asset snapshot type is produced here |
| 4 | Resolver/storage fixtures have owned files. Valid changed closure updates document root digests. Unrelated closure is refused |
| 5 | Complete native fixtures, label/remap and allocation/history failures agree with command and EditorHistory/dirty-state changes |
| 6 | Lifecycle fixture paths and adapter test reference are explicit. Open/save refusal and validate-result behavior are distinct |

No unresolved preflight conflict. Implementers report evidence-backed deviations to the controller for a recorded ruling.

### Task 1 implementation and review dispatch, 2026-10-05

Implementation commit `0097921b776295eac6dd4e634ff58a7cce6a789d`, task BASE
`f29eb60c0867cfb117765ba0f2e2d81def502d22`. Controller verified commit existence, clean task tree,
13 changed source/test/project files, actual test log and slot-history exit codes. Focused native
document test RED-confirm failed solely for missing planned APIs before production implementation.
GREEN passed 20, failed 0, skipped 0, with zero warnings. Test-project formatting passed.

Full MapDoc-project formatting exited 2 with 52 WHITESPACE diagnostics in five files unchanged from
BASE. The controller verified the unchanged-file diff and filed
[engine #1293](https://github.com/APKiwiOrg/KhaozEngine/issues/1293). This remains a round-verification
concern. Two worker changed-file format attempts exited 75 before any target ran. A controller attempt
is pending. Neither slot timeout is a formatter failure or success. No full solution check was run.

Cohesive additions beyond Task 1's listed paths are MapNativeValidation, schema-driven unknown-member
preflight and the GlobalsOnly assignments required by the exercised monolithic serializer path.
Task 2 retains exact numeric encoding/allocation. Full tiled identity and closure interpretation remain
later tasks. The fresh Sol xhigh review is pending, task not complete. Review artifact and report live
in this plan's SDD workspace as review-f29eb60c0..0097921b7.diff and task-1-report.md.

| Log under /tmp/grimhollow-orch/logs/wa-r1-t1-20261005T113610-30228 | Observed exit | SHA-256 |
| --- | --- | --- |
| `wa-r1-t1-red-confirm.log` | 1 | `a1f457181fdadf9da15a12c5413c1ad0c466c2924d8ba62695944e504fc50d82` |
| `wa-r1-t1-green.log` | 0 | `3b27d656b632b8ba68fec5c2dc1bf60ffbc556cad1705e371b0f41b6df132019` |
| `wa-r1-t1-format-tests.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `wa-r1-t1-format-mapdoc.log` | 2 | `41cdb9056beef759736a9221e559d65be6384d1bd97e6b33a93c204a45abf209` |


### Task 1 fix round 1 and fresh review, 2026-10-05

Review 1 found I1 legacy casing rejection, I2 missing native member/range validation and I3 skipped
anyOf member checks. Controller verified the named paths. Ruling R1-T1-1 places basic required native
members, positive versions/optional IDs and nonnegative high-water validation in Task 1. Task 2 retains
decimal-string encoding and allocation. This makes the declared native schema effective on real
load/save paths. If wrong, the cost is relocating small validation and test code within R1.

Task 4 must explicitly prove unknown native member refusal for tiled manifest and tile load paths.
Derived schemas alone do not enforce validation. This integration proof remains open until Task 4.

Fix commit `69978ff6282c543e9cdfd8adae38bff48fb9cd88` follows evidence commit `bb452cc89`.
Controller checked actual logs and slot history. Initial regression RED exited 1 with 39 failed and
23 passed, initial GREEN exited 0 with 62 passed. A new legacy-default regression then failed alone
(62 passed, 1 failed), and final GREEN exited 0 with 63 passed, 0 failed, 0 skipped and no warnings.
Changed production and test formatting both exited 0. Unrelated baseline issue #1293 stays open.

The fix preserves case-insensitive recognized fields, verifies required native members before defaults
erase absence, validates DTO ranges/nulls on save, traverses applicable union branches and materializes
legacy omitted-coordinate defaults only in a newly generated playable-bounds block. A fresh Sol xhigh
scoped re-review is pending, using review-0097921b7..69978ff62.diff and task-1-review-2.md in SDD.
No Task 1 acceptance or later implementation is claimed yet.

The original implementer continuation did not wake the controller on completion. The user status
question exposed the idle gap. Future resumed implementation turns are actively awaited until the
controller processes their result. Fresh review rounds continue to use new delegated task IDs.

| Fix log under /tmp/grimhollow-orch/logs/wa-r1-t1-fix1-20261005T121328-70812 | Observed exit | SHA-256 |
| --- | --- | --- |
| `red.log` | 1 | `43432d07fe11c2a4966db91fdce0f3f1de6045823c1f35287dba8f571b913a77` |
| `red-legacy-defaults.log` | 1 | `3f18e8f90d2ec4acf625d0584b6cd167a2bd3e198c31ced5604a935e681a22b8` |
| `green-final.log` | 0 | `8599972b20713c0a9dc10ebfcd271d03fc2f5efd1972f8cd6cf01cb585cc0a51` |
| `format-mapdoc-final.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `format-tests.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |


### Task 1 accepted, 2026-10-05

Fresh scoped review 2 passed spec compliance and code quality. I1, I2 and I3 are addressed, with no
new Critical/Important/Minor finding or parked item. Controller checked the cited casing, required
native validation, union traversal and migration-default code against the fix and retained test logs.
Task 1 is complete for code range `f29eb60c0..69978ff62`. Final focused evidence is 63 passed,
0 failed/skipped/warnings. Final changed-file formatting passed. No manual check is needed.

Review artifacts are task-1-review-1.md and task-1-review-2.md in this plan's SDD workspace.
Task 4 carries the tiled-native unknown-member integration proof. Task 6 carries the living API docs
sweep. Baseline formatting issue #1293 and required full round verification remain open. Task 2 begins
from the accepted Task 1 branch after this bookkeeping commit, with basic range/presence checks kept
in MapNativeValidation and exact encoding/reservation/allocation added by Task 2.


### Task 2 implementation and independent review, 2026-10-05

Task BASE `30d7d5d0bc5c53cbec6d38f6de106e378b7ea7ee`, implementation commit
`1c28573c5476a988fe08c288099b124d3247d27d`. Property-scoped canonical decimal converters and a
transactional reservation/checked allocator are added. Existing native validation owns duplicate IDs
and high-water consistency. Scoped MapNativeDocument attributes replace the need to edit the global
serializer options or main validator. This preserves unrelated long fields and reuses Task 1's seam.

Controller verified commit, clean tree, six changed implementation/test/schema files, actual logs and
runner history. RED exited 1 for the missing planned MapNumericIds API. GREEN passed 28 numeric tests,
then the existing native document regression passed 63. Both had zero failures/skips/warnings. Changed
production/test formatting exited 0. The slot wait ran no overlapping test process. No full solution
check was repeated. Fresh Sol xhigh review1 is pending, task not complete.

Review package is review-30d7d5d0b..1c28573c5.diff in SDD, with task-2-brief.md, task-2-report.md and
expected task-2-review-1.md. Review task is tracked in task-2-review-1-dispatch.json. No new ruling or
baseline formatting exception was taken. Issue #1293 remains separate.

| Log under /tmp/grimhollow-orch/logs/wa-r1-t2-20261005-implementer | Observed exit | SHA-256 |
| --- | --- | --- |
| `red.log` | 1 | `0d25f9d804d8de53729e35f9d44e8b39922919fc8f7a90ea76a47677f8e2f741` |
| `green.log` | 0 | `7fddcbaca734ed59f0281152d4bb140adfe244a514dd2303f1c5ab453a309449` |
| `regression.log` | 0 | `53999ad1afd76b9d532a3dc8e0ead290b4012bdf5096688f65b9ebb8f8d22433` |
| `format.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `format-source.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
