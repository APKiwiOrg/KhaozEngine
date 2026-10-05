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
- Produces new: `MapAssetManifestAdapter.ToAssetEntry(MapAssetClosure closure, string assetId, string resourceRoot) -> AssetEntry`. Preserve-source-scale mesh loading is R3, the adapter must not promise normalized native runtime meshes in R1.
- New lifecycle fixture `NativeLifecycleFixture : IDisposable` exposes `MapEditSession Session`, `string ValidPath`, `string BadPath`, `MapResolvedAsset Asset`, `MapAssetClosure Closure`, `string ResourceRoot`, `void CorruptResource()`, `byte[] ReadSavedBytes()` and prepares a native document plus local resources in an isolated temporary directory.

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
    var adapted = MapAssetManifestAdapter.ToAssetEntry(f.Closure,f.Asset.Id,f.ResourceRoot);
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


### Task 2 accepted with one deferred minor, 2026-10-05

Independent review 1 approved Task 2 spec compliance and code quality, with no Critical/Important
finding. Controller inspected the converter and allocator and verified the existing runtime refusal
proofs. Task 2 is complete for code range `30d7d5d0b..1c28573c5`, with 28 focused and 63 regression
tests passing, zero failures/skips/warnings and changed formatting clean. No manual check is needed.

Deferred Minor, [engine #1298](https://github.com/APKiwiOrg/KhaozEngine/issues/1298). The schema's
19-digit string pattern/length checks admit `9223372036854775808`, while the runtime converter correctly
rejects it. Controller verified both field definitions and the overflowing example, searched local
and fresh remote prior art, then filed the issue. This is a schema-only precision gap, not runtime
identity corruption. The final round review must triage it. Do not silently discard or fix it in an
unrelated task.

Cross-task checks remain assigned. Task 4 must persist native high-water state and exact IDs through
both storage forms and preserve allocation after deletion/save/reload. It must also enforce native
unknown-member refusal on manifest/tile load. Task 6 and later MCP integration must use exact decimal
strings for all IDs above 2^53. These are future gates, not claims completed by Task 2.


### Task 3 implementation preserved and review started, 2026-10-05

Task BASE `264e3f647f845060ee11a06ca8c2177c0b523ac0`. Implementer returned BLOCKED on final test-file
format verification after three slot-only timeouts. No target ran during those attempts. Controller
verified actual final logs, slot history, the nine staged paths and the manually repaired multiline
initializer whitespace. Final behavioral proofs are 64 closure tests and 91 prior-class regressions,
zero failures/skips/compiler warnings. Production formatting and staged guards passed.

Ruling R1-T3-V1. Preserve the tested implementation as a held feature-branch commit and overlap its
read-only review with the queued final formatting check. This does not accept Task 3 or allow Task 4.
The remaining format result and independent spec/quality review both remain required. The reason is
durable work and avoiding idle review time during build-slot contention. If the formatting repair is
incomplete, correct it on this unreleased branch before task acceptance.

Controller commit `c6e7b5edd97da0bdcc014888efa76ed3ca17cb8b` contains the worker's staged implementation
and is pushed. The controller changed no implementation logic. The final test-file formatter is
queued as wa-r1-t3:format-tests-final, session97113, through the shared runner. Pivot coordination
confirmed its controller will leave the next opening, while its already queued workers cannot be preempted.
No lock was removed and no parallel build was started.

Fresh native Sol xhigh reviewer `/root/wa_r1_task3_review1` is reviewing
review-264e3f647..c6e7b5edd.diff against task-3-brief.md/report. Controller remains active until the
reviewer's result and format result are processed. No Task 3 completion claim is made.

| Log under /tmp/grimhollow-orch/logs/wa-r1-t3-20261005-impl | Observed exit | SHA-256 |
| --- | --- | --- |
| `wa-r1-t3-red-slot-retry-1.log` | 1 | `baf97cbd499aefd0165e0aaaa2d5d288f7bca01ac8c5057f8dc8de4df00b6c5e` |
| `wa-r1-t3-cycle-red.log` | 1 | `fd6d9641826132e2d99affbdbf58e2e9aaffb559e20348debdda62233d36eb36` |
| `wa-r1-t3-green-final.log` | 0 | `656ea6c133bdb9e435b74184a4f6245c9ad39459067f86aade208c02f5588075` |
| `wa-r1-t3-regression-final.log` | 0 | `98e42b3805c7080fb149f67177054cf303e1c9d0ed42cc2d9e0538be3772f691` |
| `wa-r1-t3-format-production.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `wa-r1-t3-format-tests.log` | 2 | `a6533aefedbb815cbefedc83a4734b95d35335ab46311c8023f2181e9ce10294` |
| `wa-r1-t3-guards-final.log` | 0 | `cf381f4506e6e547dc0dcbf3a92bc81963015344ab5f4546609dca20f85b1683` |


### Task 3 accepted and Task 4 interface refinement, 2026-10-05

Fresh native review approved Task 3 spec compliance and code quality, with no new findings. Controller
inspected publication, graph/hash and defensive-copy code. The review's pending-format note is resolved
by actual final test-file format exit0 at14:30:38 local, same reviewed code. The formatter log digest is
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855.
Task 3 is complete for `264e3f647..c6e7b5edd`, with64closure and91regression tests passing, zero failures,
skips or warnings, and production/test formatting clean. Report is task-3-review-1.md in SDD.

Ruling R1-T4-1. Task 4 must retain the explicit immutable root-reference set in MapAssetClosure and
compare document root IDs, paths, digests and payload versions exactly before resolve/hash. Looking
up named resources in the complete graph alone cannot detect a different/extra root set. Task 4 may
add the cohesive root-reference snapshot/accessor to the existing closure type and tests. This fulfils
the already approved matching-closure contract. If wrong, the cost is a small API/test adjustment.
Task 4 also owns exact high-water/tombstone storage and native manifest/tile unknown-member refusal.


### Task 4 implementation returned and review started, 2026-10-05

Task BASE `611dcfd6bdba7496a93bc3155e9e5713c30e6967`, implementation
`c4e0213927889206bceffb47407a24ff6a5e21c5`. Immutable world snapshots, normalized complete native
identity, exact immutable roots and actual native storage validation are implemented. Legacy analytic
hash remains separate. Derived schemas already inherited native metadata, so no redundant schema
source edit was needed. R1-T4-1 is fulfilled by the immutable closure Roots accessor and match checks.

The worker committed a held candidate while formatting waited, following R1-T3-V1. Final production
and test format checks then passed without post-candidate edits. Controller verified actual RED exit1
for a missing planned API, GREEN36focused and155prior regression tests with zero failures/skips/warnings,
final formatting exits0, clean tree and the13changed paths. No full solution check was repeated.

Fresh Sol xhigh review1 is pending, using review-611dcfd6b..c4e021392.diff and task-4-report.md in SDD.
Output is task-4-review-1.md. Task4 is not accepted and Task5 has not started. Baseline1293 and minor1298
remain final-round triage items.

| Log under /tmp/grimhollow-orch/logs/wa-r1-t4-20261005 | Observed exit | SHA-256 |
| --- | --- | --- |
| `red.log` | 1 | `146de424889fca3fb41739290346f1b0754550d6f29c05c57f6c4e4d7917c4b1` |
| `green.log` | 0 | `1d331675f02807657b277bb990ef79b21afc0290375db37f1b07845d05a8691d` |
| `regression.log` | 0 | `8fab8eafc4475e8eaa812766d6c073fc30f468f80a0b4bef8ee70a6334ec6d2d` |
| `format-production.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `format-tests-final.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |


### Task 4 accepted, 2026-10-05

Fresh review approved spec compliance and code quality for `611dcfd6b..c4e021392`, with no findings.
Controller inspected normalized identity, exact immutable roots, snapshot-before-callback behavior
and explicit numeric conversion in the native tiled writer. Actual tests passed 36 focused and 155
prior regressions, zero failures/skips/warnings, with final production/test formatting clean.
Task 4 is complete. Task 5 history and Task 6 lifecycle/adapter remain pending. Native custom-registry
support is not established by these new signatures, while existing analytic APIs remain separate.

OA9 was received during this review. It requires deep multi-level caves, deeper oceans, larger maps,
tiled navigation, multi-cell behavior and far-origin precision, as recorded in the game's DECISIONS.
Future round plans are being revised in the engine planning lane. R1 approval remains, with no new
cave representation or coordinate strategy selected. R1's final compatibility audit must identify
accidental fixed depth/plane/cell limits and document its current finite-float contract, without
claiming unlimited precision or implementing the later cave/water/navigation systems here.


### Task 5 context ruling R1-T5-1, 2026-10-05

Task 5 initial dispatch returned NEEDS_CONTEXT at `4a19d6db757d25ab2d095cba9d4214f67356732e`,
without code changes or build/test commands. Controller verified that IEditorCommand.Apply/Revert
receive only MapDocument, EditorDocument/EditorHistory carry no closure, and MapEditSession.Mutate
passes only document and registry. Full native candidate validation cannot infer verified assets.

R1-T5-1 authorizes an explicit immutable MapAssetClosure binding at editor/history and MCP mutation
transaction boundaries. Existing analytic signatures/behavior remain compatible. Native transactions
refuse a missing or mismatched binding. Low-level Apply/Revert perform atomic document-local checks,
while bound transaction boundaries validate the complete candidate before publication. No command
loads files, guesses source roots, infers assets from placements or invents builder options.

Use one shared render-free native bound-document validator, extracted from MapAuthoredIdentity as
needed, for root equality, asset membership and native validity. Keep builder/hash requirements with
identity. Extend EditorDocument/EditorHistory with explicit binding and MapEditSession with a narrow
BindNativeAssets seam. Validate bindings without dirty/history changes and clear session binding on
open/create replacement. Task 6 owns real lifecycle loading. Native low-level command documentation
must distinguish local validation from full bound transaction validation.

Task 5 scope now includes MapEditSession.cs and a cohesive shared MapDoc validation helper plus
MapAuthoredIdentity reuse. Preserve document object identity at publication and existing command
semantics. Failed execute/undo/redo preserves command retry state, stacks, dirty/rebuild/events and
allocator. Validate a manifest asset not already placed, missing/mismatched closures, unknown asset
rejection followed by retry, and closure clearing across document replacement. Existing Task 5
acceptance tests remain required. This is a dependency seam within the approved transaction contract,
not a new owner capability or a Task 6 lifecycle implementation.


### Task 5 implementation and fresh review, 2026-10-05

Candidate `a42fd572e17c2a6e48fa52d9cf4567492380b223` is pushed on the R1 execution branch.
The same OA8 implementer returned DONE_WITH_CONCERNS after the R1-T5-1 continuation. Controller
verified branch containment from source BASE `4a19d6db7`, clean tree, committed diff whitespace and
actual shared-slot exits. Focused tests passed 18, editor/mutation regressions 323, scene regressions
168 and MapDoc regressions 191, all without failures/skips. Final four project format checks exited 0.

Implemented allocation-safe placement commands and history, labels/remap, explicit closure binding,
shared bound validation, GUI/MCP entry paths and detached session mutations. Placement helpers and
inspector code were extracted to comply with size limits. Four existing baselines decreased.
No lifecycle source loading, version/tag, native cave/water/scale capability or full tool parity is
claimed. The current native history protocol refuses non-placement commands before mutation.
That explicit boundary is a fresh review concern against incremental C1/R1 scope. It is not silently
accepted as full-program behavior. Existing analytic documents retain their command path.

Fresh review task is
`node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-r1-task5-review1-20261005`.
Current binding Gauge assign_route returned Claude Work Opus 5.5 high, target passed unchanged.
This differs from the earlier Sol route but retains one fresh read-only spec/quality review.
Package `review-4a19d6db7..a42fd572e.diff`, report `task-5-report.md` and dispatch identity
`task-5-review-1-dispatch.json` are in R1 SDD. Task 5 is not accepted and Task 6 remains gated.

| Log under /tmp/grimhollow-orch/logs/wa-r1-t5-20261005-cont | Observed exit | SHA-256 |
| --- | --- | --- |
| `red.log` | 1 | `bc66b0d58562d9aae213141891a90d069951149147e98a64ebf1bac4a924ca81` |
| `red-entrypoints.log` | 1 | `f49b638e724e9eedc7b30d8d2b516e3a61c445beca9677089bbcb23120a1e4c3` |
| `red-retry-order.log` | 1 | `7a0bc5c96390c31565c2305c3bfbf94a1f10aa4d9dad7e811f62789cfdad3b30` |
| `green-extraction.log` | 0 | `29cd09731e87d68cc497f9dc74dc9e13184ad1c49efab518d45c9cb2792c29f7` |
| `regression-editor.log` | 0 | `423fed2189bfc5ee0176058a23a008823551309bbdd70230d3632c6ff4ead35a` |
| `regression-mapdoc.log` | 0 | `84319b18cf1a5bbc13b5fcc9e4240796549757e284faeece71adc5d5093f5608` |
| `regression-scene.log` | 0 | `08f1bfd7f14257dee0ab52d754debcea40095f074b351f3d3cf11df51a67631d` |
| `format-verify-mapdoc.log` | 0 | `f5ec71577cd15b15144bc724575f2dda6844f8e5290a8b3defacd55769a2ab37` |
| `format-verify-mapeditor.log` | 0 | `ec5c481abb37afccfd32bcf75e399389d12f684bd0c868d3f5b24581d3bb32ab` |
| `format-verify-mapedit.tool.log` | 0 | `6ff6cbb03c4a3512a3c9b486bdcbfb6c2fbb44e87e7120c3a0861422f92a644c` |
| `format-verify-mapeditor.tests.log` | 0 | `ac82e9f02fb75a2b11689e157a28d3a5c2d7591bb74a43c831f40d0c418b2728` |

Controller independently reran diff, dashes, prose, instruction-budget, doc-version and whole-tree
file-size guards after the implementation commit. All exited 0. No full suite or live run was repeated.

OA9 planning is verified and pushed at `a95a57a1218da2d6d87610a987ec49b9e148e2aa` on the engine
planning branch. Its DG9.1 to DG9.6 gates cover cave choice, water containment, tiled navigation and
server cells, precision, water appearance/cost/walker behavior, storage and estimate revision.
R1 task bodies in that planning revision are unchanged. The active execution Outcome stays here.
The final R1 review must reconcile the OA9 compatibility audit draft in SDD against Tasks 5/6 before
release readiness. No later round choice or approval is implied.


### Task 5 review 1 disposition, 2026-10-05

Fresh review found Task 5 spec-compliant and code-quality approval conditional on Important I1.
Controller verified I1 in the scene load, tool and inspector paths. Native GUI edits have no bound
closure and rejection escapes scene input dispatch. R1-T5-2 requires visible scene rejection without
uncaught frame/widget exceptions, while retaining atomic command refusal and deferring actual source
loading to lifecycle work. Same implementer owns a TDD fix, then a fresh scoped review.

M1 stale placement_rename tool descriptions and M2 SetWindow retaining a binding across successful
document replacement are verified and assigned to that fix. Two narrow N1 comment/diagnostic nits
are included. M3 whole-document cloning, including twice on MCP command-backed edits, is verified
as a code-cost observation with no latency measurement. It is deferred to R9 in [#1302](https://github.com/APKiwiOrg/KhaozEngine/issues/1302).

The placement-only native history protocol is within incremental C1/R1 scope. R2 and later native
command families must extend its atomic protocol rather than bypass it. Direct validated MCP
mutation callbacks and GUI command-backed operations currently differ. Record this at each later
round refinement before claiming GUI/MCP parity. Task 5 remains unaccepted and Task 6 is gated.
Original review, controller fix brief and eventual fix report are in R1 SDD.


### Task 5 fix 1 proof and review 2, 2026-10-05

Fix `6ac4f6b9d2ef9a89feb7717c1c1920b4752cadd8` is pushed, from parent `020ef8a77`. Worker
returned DONE. Controller checked the committed scene action/gesture changes, native exception
classification, window binding order, tool schema descriptions, clean tree and ancestry.
Expected native edit refusal becomes visible status feedback, with transient gesture/selection
recovery and no asset loading or validation bypass. General unexpected failures still propagate.

The affected regression run passed 321 tests, including all 14 new cases, with zero failures/skips.
Final four project format checks exited 0. Controller verified those logs against slot-history exits,
independently checked all 14 changed C# size bounds and diff whitespace, and inspected completed
worker command records for prose/dash/instruction/doc-version guards, whole-tree size and commit
hooks. No full suite or live proof was repeated.

Initial RED contained eleven intended behavioral failures plus one wrong fixture filename.
The fixture error was corrected separately. Corrected window RED then failed on retained binding
while failed-load preservation passed. The fixture error is not counted as behavioral RED evidence.

Affected test target, through the shared slot as `wa-r1-t5-fix1:regression`:

```bash
dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter 'FullyQualifiedName~MapEditorSceneTests|FullyQualifiedName~EditorHistoryTests|FullyQualifiedName~EditorToolTests|FullyQualifiedName~NativePlacementHistoryTests|FullyQualifiedName~MapEditSessionTests|FullyQualifiedName~McpAdapterTests|FullyQualifiedName~NativeWindowBindingTests'
```

Fresh scoped review 2 is running under the exact Gauge returned Claude Work Opus 5.5 high target,
task `node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-r1-task5-review2-20261005`.
It receives the original brief/review, fix brief/report and all dispositions, with range
`020ef8a77..6ac4f6b9d`. SDD identity is task-5-review-2-dispatch.json, eventual returned report
task-5-review-2.md. Task 5 remains unaccepted and Task 6 remains gated.

Engine planning commit `5b948eb6a` carries the atomic protocol gate into R2 and bounded edit-cost
work into R9 under #1302. Neither round is approved.

| Log under /tmp/grimhollow-orch/logs/wa-r1-t5-fix1 | Observed exit | SHA-256 |
| --- | --- | --- |
| `red.log` | 1 | `33a3fcf1088277cc6a342f107bd2f3335e19327f8961f9b47e6576e16e0ebf21` |
| `red-window.log` | 1 | `93f71c346e7709732184093fb97d8fb7a839139f641bf0834b9865e0d251a955` |
| `green.log` | 0 | `f4514cf2c96d293bb436905a6a07f0cc2a7e80f2c0d0de81a3900fe4b64d1fda` |
| `green-widgets.log` | 0 | `0767b6ff9071e7d76121928ba55eae94740315d1965ecadfb05911334cf291f4` |
| `regression.log` | 0 | `ebf04b7552d8e5e848a39c28bb5a1698cfbfea8cc8a271ccec68ec3ab8cf8a2c` |
| `verify-mapdoc.log` | 0 | `087d57a17a4f438f2169651e0cef855ed18823e2e6e6d0c5bc592fa5db3fcab9` |
| `verify-mapeditor.log` | 0 | `6aedd39d01ad40bb571118e931e8ad9094d68357b06ac558547221c9730fa02b` |
| `verify-mapedit.tool.log` | 0 | `a14f2082cf803bb000d3eb9ce766856674a14730ba4ebca73a14d519a225e4eb` |
| `verify-mapeditor.tests.log` | 0 | `8ade7faf0869c98eadece8b389d52d8cc780a3cd04381c85824f71cf85d4972c` |
| `size-final.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |


### Task 5 accepted and Task 6 preflight, 2026-10-05

Fresh review 2 approves spec compliance and code quality for `020ef8a77..6ac4f6b9d`, resolving
I1, M1, M2 and N1. Controller verified scene/command routes, binding reset order, original proofs
and the docs-only move to `34ddf13e0`. Task 5 is complete with a non-blocking remainder.

Review 2 Minor 1 is verified: rejection clears an active native gesture without sealing its merge
barrier, so a later same-ID inspector Move can merge with the previous accepted Add/Move. Filed
after local/live prior-art checks as [#1303](https://github.com/APKiwiOrg/KhaozEngine/issues/1303).
Assign it and the two cosmetic blank-line nits to final R1 triage before release readiness, alongside
#1293 and #1298. This does not block Task 6. #1302 remains the later R9 copy-cost work.

R1-T6-1 reconciles the final task with the actual immutable closure and transaction APIs:

- Adapter signature is `ToAssetEntry(MapAssetClosure closure, string assetId, string resourceRoot)`.
  Select the descriptor from that closure and obtain paths/digests through GetResource. Asset ID,
  resource ID and path are different namespaces. Require an explicit absolute root. Check every
  returned mesh/compatibility LOD path against the verified digest before returning it. Use the
  first declared LOD for AssetEntry's single LOD slot and retain all native metadata in the closure.
  No asset registry duplication, GPU access, mesh normalization or fabricated collision mapping.
- Add an immutable `AssetClosure` accessor to MapResolvedDocument, retaining the same verified
  closure passed to its constructor. This additive render-free seam lets ValidateComplete return
  its actual closure for session binding and later consumers, without a second load or duplicate
  resource registry. Task 6 owns this small MapResolvedDocument change and its tests/docs.
- Derive the native resource root explicitly from the absolute document path, file parent for
  monolithic maps and the map directory for tiled maps. Anchor it for the session. Do not resolve
  assets against later ambient working-directory changes. Preserve the declared root-path policy.
- Native Open, Save, Validate and Summary use fresh complete closure validation and the resolver.
  Session validation uses one documented stable builder/options identity for its actual R1
  analytic-support policy. Failed Open cannot replace prior document, binding, path or dirty state.
  Validate reports false plus closure findings. Summary cannot report success for stale/incomplete
  closure. Partial native loads must refuse clearly, not publish a complete result.
- SetWindow and other document replacements must validate candidate state before publication and
  then bind that candidate's newly verified closure. This supersedes the manual-rebind-only
  expectation from Task 5, never permitting old bindings to survive replacement. Update the
  affected fixtures to prove fresh binding and failed replacement preservation.
- Native conversion and retile writes are lifecycle boundaries too. Validate before any write,
  including references against a conversion destination's resource root. Refuse missing or stale
  destination resources rather than copying resources or silently rebasing references. Preserve
  prior in-memory state on failure. Keep existing analytic behavior and tiled overwrite guards.
- Native monolithic saves stage a sibling file and atomically promote after validation. Do not
  weaken the existing tiled storage guard. No native GUI asset-loading integration is added here.
- NativeDocumentSummary uses decimal-string transport for the Int64 high-water mark. Ordinary
  Int32 counts remain JSON numbers. Any added optional numeric-ID result uses its scoped Int64
  converter. Do not add global numeric conversion or promise R10 query parity.

Task 6 scope includes the shared resolved document accessor, lifecycle-related session helpers and
updated native lifecycle/window/history fixtures needed for automatic verified binding. Cohesive
partial extraction is permitted for file-size limits. Update dependency documentation for the
planned forward MapDoc reference. Focused task checks run first. The controller owns full round
verification after Task 6 and whole-branch review, so the worker must not run the full suite now.


### Task 6 candidate proof and review, 2026-10-05

Candidate `9fe1bc558d22363abbd42f42f1e6faf716652378` is pushed from `5fc007705`. Worker
returned DONE_WITH_CONCERNS. Controller verified the intended clean branch and ancestry,
committed diff/changed-file size limits, lifecycle and adapter code, and actual shared-slot
logs/exits. Passed 15 focused lifecycle/adapter tests, 192 MapDoc tests and 31 architecture tests.
Game3D build passed with zero warnings/errors. Changed-file solution format exited 0.

The broader editor regression exited 1, with 391 passed, 3 failed and 6 skipped. The failures are
WorldHash_MatchesGoldenDigest, its Swedish-culture variant and V2Document_LoadsAtV3WithDefaultTileSize.
Filed after local/live prior-art search as [#1304](https://github.com/APKiwiOrg/KhaozEngine/issues/1304).
The test/hash paths are unchanged by Task 6. R1 Task 1 introduced current format 4. The version test
hardcodes 3, while OfManifest includes document format version in hash input. Final triage must
separate fixture-input drift from canonicalization changes before updating any golden or scheme.
No separate baseline test run was performed. These are earlier R1 regressions, not released-main
failures. They block final round acceptance, not read-only Task 6 review.

The worker report's blanket claim that relative references cannot convert to tiled is refuted.
ConvertToTiled only refuses an existing tiled map, and MapTiledFile.Save allows an existing directory
with resources but no map manifest. Matching preprovisioned resources can satisfy validation. This
is a source-level correction, no positive runtime proof for that case was run by the controller.

Fresh review task is
`node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-r1-task6-review1-20261005`,
using the exact Gauge Claude Work Opus 5.5 high target. Inputs are the Task 6 brief/ruling, report,
controller notes and review-5fc007705..9fe1bc558.diff in R1 SDD. The review explicitly assesses
analytic tiled path normalization and native validation/result fields. Task 6 is not yet accepted.

Final R1 triage now includes #1293, #1298, #1303 and #1304 plus the noted cosmetic nits. #1302
stays with R9. Whole-branch review, current-main reconciliation, full serial checks and owner release
authorization are pending. No tag, full-suite pass or round completion is claimed.

| Log under /tmp/grimhollow-orch/logs/wa-r1-t6-20261005 | Observed exit | SHA-256 |
| --- | --- | --- |
| `wa-r1-t6-red2.log` | 1 | `f6eae8397dc6719b26146eab0ca094ac8a6d1e6b292e8ab5777daf28ac28f293` |
| `wa-r1-t6-green.log` | 0 | `c109a94488ac1974d4627a30d6d9005a4ece639fbb446ccddab6d6e56e41cb9d` |
| `wa-r1-t6-regress-editor.log` | 1 | `2baff922cab7de2fa75af0011b3e42ef0576508e9339279bfe8693be1b6ec529` |
| `wa-r1-t6-regress-mapdoc.log` | 0 | `952bb0dc8c122845b78c9e71bad68bc5c816836fd734b48a6adef788085912d3` |
| `wa-r1-t6-architecture.log` | 0 | `59b612e209c54022fb77221d5e5bc4e83b5a1901fd5caf34595ca6f77581c25a` |
| `wa-r1-t6-build-game3d.log` | 0 | `e8641a5a354558fdc94b608f557e0fb63f79d635a84719ea36e448bf05503caa` |
| `wa-r1-t6-format.log` | 0 | `239c224f01960886368326d9ef5ebdcce071dcd0996d157bef6a963fc1eaa49c` |


### Task 6 review 1 disposition, 2026-10-05

Review 1 approves spec compliance with a resource-storage finding classified Minor. Controller
verified the Sweep path and upgrades M1 to blocking Important because an accepted tiled Save can
delete a declared resource under tiles/. R1-T6-2 reserves the tiled writer's storage namespace,
using shared owner constants and path policy, for every reference in a native closure before
publication or writes. Target form matters during conversion. Same implementer owns the TDD fix.

N1 missing-storage Retile fallback is also assigned to the fix. Native Save must preserve its known
form, with no silent monolithic fallback for a missing tiled map. N2 atomic destination replacement
semantics will be documented, not expanded into permission/symlink-preservation infrastructure.
N3 positive relative conversion and failed-Open dirty/manifests proofs, and N4 moving the unchanged
summary DTO to Results.cs, are in the bounded fix. No final-triage issue is widened into this task.

The reviewer independently confirmed that relative resources can convert when preprovisioned.
It also refuted the reported analytic SourceDirectory deviation: BASE already normalizes that
directory in MapTiledFile.Load. Only failure-message path spelling changed, OpenResult.Path and
DocumentPath still retain the caller's string. No analytic compatibility fix is required for that
claim. Original review and exact fix brief are preserved in R1 SDD. Task 6 is not accepted.


### Task 6 fix 1 and corrected conversion ruling, 2026-10-05

Fix 1 `a1fc33d117ea08fe4b2b8db82a8489d013eaeaf3` is pushed. Worker returned DONE_WITH_CONCERNS.
The M1 data-loss repro is retained as an end-to-end resource-survival regression. Focused editor
checks passed 47 and MapDoc checks 212. Broader editor checks retain only #1304, 402 passed,
3 failed and 6 skipped. Format exited 0. Fresh review waits for the N3 conflict below.

R1-T6-3 corrects the controller and first review's earlier relative-conversion inference. Runtime
proof and MapDocumentFile.DetectForm show that every existing directory is classified Tiled.
The session therefore refused the preprovisioned target before reaching the permissive writer.
The controller's earlier statements that this case already worked were wrong. The worker correctly
kept the existing guard while asking for a ruling.

For native conversion only, allow a prepared resource directory without a map manifest. Refuse
an existing map manifest via a shared owner helper and retain the writer's other guards plus M1
resource checks. Analytic conversion keeps its existing any-directory refusal. Do not change
DetectForm globally. Same worker implements this bounded correction, with positive native and
negative existing-map/analytic/reserved-resource proofs, then a fresh review covers both fix commits.

Storage ownership currently follows the writer's normalized-path/case policy. Filesystem aliases
are not resolved. Qualify any unconditional XML guarantee accordingly and retain the documented
limit for whole-round review. No claim of complete physical-file alias protection is made.


### Task 6 combined fixes, proof and scoped review 2, 2026-10-05

Fix 2 `37dc8e5d8b5c0e8e17835c88778aeacd76837d83` is pushed. Combined fix range is
`45540c5a9..37dc8e5d8`, including code `a1fc33d11` and docs ruling `1ac143f4c`. Controller
verified branch ancestry, clean tree, combined changed C# size bounds, diff whitespace, actual
focused/MapDoc/format exits and unchanged #1304 failure identities.

Final fix 2 focused tests passed 49, MapDoc tests 213, zero failures in either selection. Broader
editor regression remains 404 passed, 3 failed and 6 skipped. The failures are the known #1304
trio. Changed-file format exited 0. Worker reports all required repository guards passed.

R1-T6-3 is implemented: native conversion accepts prepared resources without a map manifest,
refuses an existing map or reserved resource, and preserves the writer's lower guards. Analytic
conversion and DetectForm are unchanged. The positive test covers conversion, edit/save/reopen,
complete identity, high-water mark and prepared resource bytes. XML states the normalized-path
policy explicitly instead of promising protection against all filesystem aliases.

M1's committed regression keeps the disposable Open/edit/Save resource-survival scenario. The
original code deleted the verified file. A disabled-guard control made all four normalized/absolute
cases fail on resource deletion, then the guard was restored and the focused tests passed.

Filesystem aliases remain the documented policy limit, filed as lead [#1305](https://github.com/APKiwiOrg/KhaozEngine/issues/1305)
for whole-round risk disposition. No alias-based runtime proof was performed. No filesystem alias
infrastructure was added to the bounded conversion fix.

Fresh review 2 task is
`node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-r1-task6-review2-20261005`,
using the exact Gauge Claude Work Opus 5.5 high target. It receives original review 1, both fix
briefs/reports, corrected controller notes and review-45540c5a9..37dc8e5d8.diff in R1 SDD.
Task 6 is not accepted pending this review. Final R1 triage and full checks have not started.

| Log | Observed exit | SHA-256 |
| --- | --- | --- |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix1-20261005/wa-r1-t6-fix1-m1-repro.log` | 1 | `a2147640337db129cb0c1bd87fba7c197f3c1b73835d744273d7607df13e207b` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix1-20261005/wa-r1-t6-fix1-m1-regression-red2.log` | 1 | `9bd5b1fcad671da2b41af3a2a0ecca672c135838ca36d8ac93f6c9293e223e72` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix1-20261005/wa-r1-t6-fix1-final-focused.log` | 0 | `3e8f7dbabb6cf47deb890167c091b37f218739fc9c5e35f71174c66e2f0bf1a8` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix1-20261005/wa-r1-t6-fix1-mapdoc-green.log` | 0 | `85461cffa3b97707f021c301460e3864ff10dfcf5ef005eb71bca793dbcb3eb4` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix1-20261005/wa-r1-t6-fix1-regress-editor.log` | 1 | `3e1f7999f69fc3ad6b21fc79a697eaa63b8f7f672c1e5e754bf6bf535a4d24f3` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix1-20261005/wa-r1-t6-fix1-format.log` | 0 | `239c224f01960886368326d9ef5ebdcce071dcd0996d157bef6a963fc1eaa49c` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix2-20261005/wa-r1-t6-fix2-red.log` | 1 | `8c054cb8a6920c7a026e884bcd03bdc52f475538772d17a2e59ba7aec31c33e3` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix2-20261005/wa-r1-t6-fix2-final-focused.log` | 0 | `8e46ab5708a36a094cccfe437cfb6983145e79473f697089f99f1ea3dde05ca9` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix2-20261005/wa-r1-t6-fix2-mapdoc.log` | 0 | `43548cbc67824b65e4a95337eaf82589ba4007cbdc364096b13d7c0e331969ae` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix2-20261005/wa-r1-t6-fix2-regress-editor.log` | 1 | `11fc068e8690c1dc02adcb7458ff2759c07b4c1aab0ba570cb5bad810ba05176` |
| `/tmp/grimhollow-orch/logs/wa-r1-t6-fix2-20261005/wa-r1-t6-fix2-format.log` | 0 | `239c224f01960886368326d9ef5ebdcce071dcd0996d157bef6a963fc1eaa49c` |


### Task 6 accepted, 2026-10-05

Fresh review 2 approves spec and quality for `45540c5a9..37dc8e5d8`. M1 and N1 to N4 are
resolved. Controller verified the named storage-owner paths, explicit session forms, conversion
branching, tests and prior proof. Task 6 is complete. All six R1 implementation tasks are accepted.

The remaining N-A README sentence overstated physical-file protection. Controller corrected it to
refer only to references whose normalized paths are writer-owned. This docs-only correction needs
no test that duplicates wording. The #1305 lead now also records case-insensitive Linux mounts,
where the inherited ordinal policy can under-refuse, alongside aliases. No runtime proof for those
limits was claimed. Whole-round review must give an explicit disposition, with no stronger safety
promise than the implemented policy. #1302 remains R9 work.

R1 still cannot finish until #1293, #1298, #1303 and #1304 are addressed, the OA9 compatibility audit
is reconciled against final source, a fresh whole-branch review passes, current main is reconciled,
and the full serial checks pass. Only the owner authorizes the release tag. Final-triage scope is
the four recorded items, no new terrain, cave, water, prefab or game adoption capability.


### Final-fix preflight and main reconciliation, 2026-10-05

Fetched and checked local/remote main at `de78df336571d9a5db97b7b9b4f07c842f11556b`, the
SpaceGame prediction-reset change with 20.26.0 staged. Latest observed tag is v20.25.0. Merged
that main into the R1 feature worktree without conflicts at
`be72f8405b45866042255ef3878f2b3fcd18394a`. Main itself was not changed.

Pivot coordination confirms Grimhollow 0.11.0 remains pinned at 20.25.0 and needs no engine edit.
20.26.0 is SpaceGame's line, with its tag between that session and the owner. R1 takes a separate
free minor at finishing after another live-ref check. No version is reserved or tag authorized here.

The bounded [final verification fixes](2026-10-05-world-authoring-r1-final-verification-fixes.md)
cover #1293/#1298/#1303/#1304, with one serial worker and separate issue commits. New main runtime
coverage and the full R1 checks remain pending. #1305 is explicit whole-round risk disposition,
#1302 stays with R9. This is R1 finishing, not approval or execution of another round.


### OA9 compatibility audit at the R1 review candidate, 2026-10-05

Controller rechecked final native DTOs, MapBoundDocumentValidation, MapResolver, MapAuthoredIdentity,
asset descriptors and MapTileGrid after all six tasks and final fixes. This is a compatibility audit,
not a large-world performance or precision certification.

- Stored bounds/placement coordinates, asset bounds and resolved transforms remain floats/Vector3.
  Finite checks reject NaN/infinity, but do not establish supported horizontal extent, depth or
  minimum precision. Later plans must also test derived-transform arithmetic at their chosen limits.
- No four-plane, 4.5 m spacing, one-server-cell or nonnegative-Y restriction was added to native
  descriptors. Those legacy import values remain fixture facts. Existing signed Int32 document-tile
  coordinates and bounded count/index APIs remain inherited representation limits to quantify.
- The R1 support callback is XZ-only. Explicit placement Y bypasses it. R2/R3 must select layer/domain
  context for stacked cave support and cannot silently interpret this as a topmost-floor cave query.
- Native payload/resolver metadata and builder ID/version/options enter complete authored identity.
  A later precision or domain change needs explicit schema/payload/resolver migration and hash
  invalidation. The current version fields make refusal possible, they do not implement that migration.
- Changing to double or origin-relative coordinates can change public APIs, JSON normalization, asset
  bounds, support callbacks and hash stability. Quantify the contract before R2/R3/R8 certification.
  R1 chooses neither double coordinates nor floating origin, and cannot recover precision already
  discarded by old float authoring.
- Complete R1 identity/resolution/lifecycle requires every tile and the full verified resource closure.
  Scoped domains and tiled capture unions need their own explicit completeness/identity contract in
  later rounds, never a partial result labelled complete. #1302 tracks later edit-cost work.
- Storage ownership currently uses the writer's normalized-path/case policy. #1305 remains an explicit
  alias and actual-volume-case lead for whole-round disposition. No physical-file alias guarantee or
  alias runtime proof is claimed.

DG9.1 to DG9.6 on the engine planning branch remain later round gates. The R1 data model leaves
versioned evolution possible but does not certify the larger cave/ocean world. Whole-branch review
must consider these limitations before R1 release readiness.


### Final verification fixes returned, 2026-10-05

Worker returned DONE with four separate commits, now pushed:

- `545e9cb1c`, #1293 formatter-only repair. Controller compared all five files' non-whitespace
  text and inspected the diff, with no semantic change.
- `38055896f`, #1298 shared positive Int64 schema range and exact end-of-string handling.
  Schema rejects overflow and trailing newline like the existing runtime reader.
- `f977d4104`, #1303 rejected GUI gestures seal the merge barrier. The new headless proof keeps
  a later inspector move separate from an earlier accepted Add.
- `a5e16744fe937f93ee2c211f7f02d93534e4e528`, #1304 fixed historical format-3 golden input and
  current-format migration assertions. The original golden, production hashing, SchemeVersion and
  shared SampleDoc fixture are unchanged. The format version was the changed hash input.

Controller verified ancestry from e3d85b807, clean tree, changed-file size bounds, diffs and actual
slot/log exits. Combined MapEditor checks passed 456, with 6 GPU skips and 0 failures. MapDoc checks
passed 213. Current-main ClientPredictionTransitionTests passed 8. All observed compiler warning
counts were zero. Full solution build/suite and whole-branch review remain pending.

Live main remains de78df336 with SpaceGame 20.26.0 staged, latest observed tag v20.25.0. Both local
and remote v20.27.0 were free. R1's review candidate stages 20.27.0, preserving SpaceGame's separate
20.26.0 entry. Version, changelog and guarded declarations changed together. This is not a release
tag or permission to tag. Recheck refs again before final integration/release actions.

| Log under /tmp/grimhollow-orch/logs/wa-r1-t1 | Observed exit | SHA-256 |
| --- | --- | --- |
| `01-1293-verify-red.log` | 2 | `41cdb9056beef759736a9221e559d65be6384d1bd97e6b33a93c204a45abf209` |
| `03-1293-verify-green.log` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `04-1298-red.log` | 1 | `519e62562bf843ab2e62b410ae61e8df42ed57eaf780c9bcfae860fb90bc4e51` |
| `05-1298-green.log` | 0 | `a721b735a46c30fa1a9fb60314f653789914707b0cb322a9fc2b31ae4b52717d` |
| `06-1298-runtime.log` | 0 | `be9af4606252f84f5ec17086ff77d3cfdfd2c3b6faa4d1294139f8364215540d` |
| `08-1303-red.log` | 1 | `eaba86a20d79841736411258cedef20539ac4f9e19b8ba7eb94b081c7f557576` |
| `09-1303-green.log` | 0 | `7c5ea5f54dc00207b5fb53da32e078c31032d729283a6d31c559fbf6661e5073` |
| `12-1304-repro.log` | 1 | `030418b2b7dddf14b9c5a356ac1898720059aeed7d76c1aa2be8b7e04686f87e` |
| `13-1304-green.log` | 0 | `097edbb038d0455a128359c59d94ecb214b92043fd8a77e9736258556b782799` |
| `15-combined.log` | 0 | `bb903ba0c7016f5305fe0c534262d0f82d9120ec2170288f08f45c178275390f` |
| `16-mapdoc-tests.log` | 0 | `29c06f2e16c7597119399b28a45b6e0ae78a6ed4b4104a5ba845f0810e5fad81` |
| `17-client-prediction.log` | 0 | `587c12a67cfc5e9c72ef0934ed6defe351fcb74613699fdfdeae78356229d25b` |
| `18-guards.log` | 0 | `9eafd63548925867ce3af53a91376b2b647c780afd0334674e92e7f3d77395cd` |


### Whole-branch review 1 dispatched, 2026-10-05

Review range is current-main `de78df336571d9a5db97b7b9b4f07c842f11556b` through
`1b5f8d74c22aa549caa988cdb6caf6455b02830b`. It contains all R1 production/test/living-doc work,
staged 20.27.0 and the OA9 audit, excluding the already reconciled upstream prediction change.
The new version/doc guards passed through the shared slot, exit 0, at
/tmp/grimhollow-orch/logs/wa-r1-review-doc-guards.log. Full solution checks remain pending.

One fresh reviewer uses the exact Gauge Claude Work Opus 5.5 high target, task
`node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-r1-whole-branch-review1-20261005`.
SDD identity is whole-review-1-dispatch.json, package review-whole-r1.diff, eventual report
whole-review-1.md. Prior task approvals are context, not substitutes for this cross-task review.

A new source-inspected lead [#1306](https://github.com/APKiwiOrg/KhaozEngine/issues/1306) asks whether
mutating an Add command's retained caller payload Id after acceptance can make a later Move of
another placement coalesce incorrectly. The accepted native snapshot and mutable merge key are
different objects. No runtime proof or final defect verdict is claimed. Review must confirm or
refute the reachable behavior and ownership contract. #1305 also needs explicit risk disposition.
#1302 remains R9. No integration or release approval is implied by this dispatch.


### Whole-branch review 1 disposition, 2026-10-05

Review 1 approves spec and quality with three Minor findings and one Nit. Controller verified the
reported paths and chooses a bounded follow-up before full verification. M1 is recorded as
[#1307](https://github.com/APKiwiOrg/KhaozEngine/issues/1307): in-memory tile content must preserve
NumericId, AssetId and DisplayName like the disk source. M2 is resolved by documenting R1's default
registry restriction, without expanding APIs. M3 documents stricter unknown-member refusal for
legacy loads. N1 tightens the declared SHA-256 syntax in the schema with focused tests.

R1-WR1-1 chooses native Add coalescing hardening for #1306. Review correctly found no current GUI
caller retaining/mutating the payload and no MCP history. That does not require a wider caller
contract. Coalescing will use the accepted native identity, preserving failed-attempt retry and
legacy behavior. A focused public-API test will verify the proposed edge before the change.
No claim of an existing in-game trigger is made.

#1305 is non-blocking under the explicitly documented normalized-path/case policy, per review.
Keep the lead open for stronger filesystem ownership work. Do not justify the limit by assuming
all resources are in git, and do not claim later validation undoes a prior alias-related loss.
No alias-based runtime proof or physical-file protection has been claimed. The OA9 and R9 limits
remain. Root will verify the bounded fix and obtain a fresh scoped review, not another broad sweep.


### R1 whole-review fixes verified and scoped re-review, 2026-10-05

- Four follow-up commits are verified and pushed through
  `7e04bc01a9e02f8916a9207d37f3488e2162f386`. No worker remains active.
  Controller inspected actual diffs, clean status, ancestry, remote containment and slot/log exits.
- M1/#1307: `ba1ca12eb23cea20d296b8d3a4e29d61f3d46bbc` preserves NumericId, AssetId and
  DisplayName in in-memory tile reads. Two regressions failed before the fix, 65 affected tests pass.
  Detachment means already-served content stays independent. Existing live spatial buckets are unchanged.
- #1306/R1-WR1-1: `63cd0f594479a16a76ca5f9b4f567a32794c97bd` keys native Add merging on the
  accepted identity. Two regressions failed before the fix, 240 affected tests pass, including failed-Add
  retry and analytic behavior. No current GUI trigger is claimed, but the public API scenario is now
  reproduced. The worker report's broader phrase denying a reachable defect is rejected on that basis.
  The issue is confidence/verified and stays open until integration.
- N1: `7fc8df8f32ce7eea856db13d62fab7e4dcb202c6` requires a 64-character lowercase hex asset
  digest in the schema. Seven invalid cases failed before the fix, 82 schema tests pass. The unbound
  loader still checks nonempty only. Complete closure validation enforces the full digest contract.
- M2/M3: `7e04bc01a9e02f8916a9207d37f3488e2162f386` documents native default-registry limits
  and stricter legacy loading, corrects the stale README format version, and pins the accepted legacy
  load policy with six tests. No loader-policy or native API change is introduced here.
- Logs are `/tmp/grimhollow-orch/logs/wa-r1-wrf1/`. `12-combined.log` has 470 passed, 0 failed,
  6 GPU skips. `13-mapdoc-tests.log` has 219 passed, 0 failed. `14-guards.log` records all five
  required repository guards at exit 0. Changed-file format and docs guards also exited 0.
  One first slot acquisition returned 75 without running a target. Subsequent targets ran serially.
- Source HEAD is 7e04bc01a. Full Release solution build, format and suite remain pending.
  Live fetch still has main/origin-main at de78df336 and latest tag v20.25.0. R1 stages 20.27.0.
- One fresh scoped reviewer is active, Gauge-selected Claude Work Opus 5.5 high, task
  `node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-r1-whole-branch-review2-scoped-20261005`.
  Original R1 SDD contains whole-review-2-brief.md, whole-review-2-dispatch.json and
  review-1b5f8d74c..7e04bc01a.diff. The finishing SDD has whole-review-fix-1-report.md.
  Prior review, exact dispositions and the worker-report correction were supplied to this reviewer.
- Next step after clean scoped review is the full serial Release verification, then documented
  integration/pack and the owner-only release gate. A remaining material finding blocks integration.
  No main integration, release tag, feed refresh or game adoption has occurred.


### R1 scoped review accepted and full verification started, 2026-10-05

- Scoped review 2 returns spec PASS, quality APPROVED and every M1/M2/M3/N1/#1306 item ADDRESSED,
  with no new breakage. Full report is original R1 SDD whole-review-2.md. Controller accepts the
  verdict against the previously inspected diffs and proof logs. #1302 and #1305 limits are unchanged.
- Corrected the scratch report's digest-test explanation. The 63-hex-plus-newline case fails the
  required hexadecimal count. Length constraints catch 64 hex plus newline. The negative lookahead
  is a sound additional exact-end guard, not independently exercised by these cases. No code change.
- Current main and origin/main were fetched and remain de78df336. Latest tag is v20.25.0.
  Source head is 7e04bc01a, current pre-verification docs head ca33cc692. Tree was clean.
- Full verification is now active through the unchanged shared slot runner, whose SHA-256 matches
  HANDOFF. Log directory is /tmp/grimhollow-orch/logs/wa-r1-full-20261005. The optional retry wrapper
  was absent, so its invocation exited 127 without running any target. The existing slot-run.sh
  directly owns 01-build.log. Format and suite follow serially only after successful preceding checks.
- This is not an integration or release proof yet. A failing check blocks integration.


### R1 full verification result and baseline gate, 2026-10-05

- Full Release solution build passed, exit 0, zero warnings and errors. Full Release non-LiveSocket
  suite passed, exit 0, 25,360 passed, 1,275 skipped, zero failed across 28 test-project summaries.
  Required dashes, prose, file-size, agent-instruction and documentation-version guards passed, exit 0.
- Full solution format verification exited 2, with 3,530 WHITESPACE diagnostics in 273 files. Every
  affected file is unchanged from reconciled main de78df336. No R1 source file is among them. The
  formatter also emitted a generic workspace-loading warning without details at default verbosity.
- Filed [#1308](https://github.com/APKiwiOrg/KhaozEngine/issues/1308), confidence/verified, after local
  and live issue searches. #1293 was limited to the five MapDoc files already repaired in R1. This
  solution-wide baseline cleanup was not silently included in the R1 implementation scope.
- Durable proof inventory is proofs/2026-10-05-world-authoring-r1-full-format-baseline.json alongside
  this plan. It contains every affected file/count, zero changed-path overlap, command/exit and log hash.
  proofs/2026-10-05-world-authoring-r1-full-verification.json records all four check exits and log hashes.
  Actual logs remain under /tmp/grimhollow-orch/logs/wa-r1-full-20261005.
- R1-V1: after the format failure, the controller ran the independent full suite against the successful
  build to complete the verification evidence. This did not waive the format gate. No code changed
  between build and tests, only program bookkeeping. There was one full suite and no test loop.
- Integration is blocked on an explicit owner disposition of #1308. Controller recommends a separate
  whitespace-only baseline cleanup, with an exact no-nonwhitespace-difference check and a fresh format
  verification, then reconciliation through the R1 worktree. No such cleanup has been started.
- No main merge, pack, shared-feed refresh or tag occurred. 20.27.0 remains staged. No manual playtest
  is required for these headless proofs. The next owner check is the baseline-cleanup scope, before
  R1 can reach its separate release-authorization gate.


### OA10 implementation dispatch, 2026-10-05

Owner approved the separate 273-file baseline whitespace cleanup. The execution worktree is
/Users/antonio/KhaozEngine/.worktrees/wa-format-baseline, fix/wa-format-baseline, from current engine
main de78df336. Approved exact-spec plan is docs/superpowers/plans/2026-10-05-engine-format-baseline-cleanup.md.
Starting worker HEAD is 3c15218a7b6c1acf440165f716eec77640bbf79d, pushed and clean.

One Gauge-selected Claude Max 20x Opus 5.5 high worker is running, task
`node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-format-baseline-implementation-20261005`.
SDD workspace is .superpowers/sdd/2026-10-05-engine-format-baseline-cleanup in that worktree, with
task-1-brief.md, task-1-dispatch-brief.md and task-1-dispatch.json. Expected report is task-1-report.md.
The brief limits writes to the committed 273-file inventory, requires whitespace and literal/content
proofs, exact included-file formatter verification and guards through the shared slot. No baseline
full suite, version bump, ratchet exception, push, merge or release is assigned to the worker.

Controller verifies and pushes, obtains fresh scoped review, then merges into R1 and runs full combined
verification. R1's five prior MapDoc format fixes stay in R1. Main, releases and game adoption remain
unchanged. This task completion will wake the controller. No cleanup result is claimed yet.


### OA10 cleanup partial proof and KESIZE owner gate, 2026-10-05

- Cleanup worker returned partial with a concrete guard conflict. Commit 77ab2ec6ecc48a80e614acf843fc2d7cd247c1d4
  is pushed on fix/wa-format-baseline and formats 268 of the 273 exact allowlisted source files.
  Controller verified path membership and zero ASCII-nonwhitespace differences against the base.
- All 273 formatted versions passed the included-file formatter check and Roslyn token/trivia proof,
  with zero mismatches across 627,175 tokens including 35,327 literal tokens and 35 raw/verbatim strings.
  Controller inspected the helper and actual slot exits. Leading doc-comment exterior indentation is
  normalized, with comment content and markers unchanged. No source semantics change is claimed.
- Git's line-oriented whitespace-insensitive diff still detects split/joined lines, so it is not used
  as a green proof. Byte-stripped equality and exact token/trivia checks are the authoritative evidence.
- Five formatted files exceed their existing KESIZE caps. They were held back in a cleanly applicable
  patch. The 268-file commit passes the file-size guard. The other four repository guards passed.
- Exact requested baseline updates: EditorCommandsTests 2115 to 2141, EditorToolTests 1793 to 1808,
  MapEditorSceneTests 4415 to 4431, GoldenSnapshotTests 1601 to 1632, Room2DGui 1296 to 1300.
  This is 92 lines of formatter-only growth. No baseline value has been changed and no guard bypassed.
- Engine AGENTS.md states baseline growth and exemptions require owner approval. OA10 explicitly
  excluded ratchet changes, so controller requests approval for these five exact values. Arbitrary
  source splitting or blank-line removal to evade the structure guard is not used.
- Cleanup proof is preserved through 8a9130d41 in the cleanup plan Outcome and
  docs/superpowers/plans/proofs/2026-10-05-format-baseline. proof.json holds counts and log hashes.
  held-whitespace.json and proposed-ratchet.json contain exact patches as JSON string fields.
  Raw patch context lines initially tripped git diff whitespace diagnostics. Encoding preserves the
  exact patch payloads without those artifact-only warnings. The encoded-artifact diff check passed.
- Logs are /tmp/grimhollow-orch/logs/wa-format-baseline-20261005. No new full suite or build ran.
  No reviewer is dispatched yet. After the baseline decision, finish the five files, verify, obtain
  one fresh scoped review, merge into R1, then run combined full checks. Main, pack, tags and game
  adoption remain unchanged. If approval is declined, the five-file format gate still blocks R1.


### OA11, exact formatter-only file-size baselines, 2026-10-06

Owner answered "yes" to the five explicit KESIZE baseline increases needed to retain the formatter's
line splits. Approval is limited to these .filesize-baseline values and the already-proven held
whitespace patch. It does not approve behavior changes, other baseline growth, guard bypasses or tags.

| File | Old | Approved |
| --- | ---: | ---: |
| KhaozEngine.MapEditor.Tests/MapEditor/EditorCommandsTests.cs | 2115 | 2141 |
| KhaozEngine.MapEditor.Tests/MapEditor/EditorToolTests.cs | 1793 | 1808 |
| KhaozEngine.MapEditor.Tests/MapEditor/MapEditorSceneTests.cs | 4415 | 4431 |
| KhaozEngine.Render.Tests/Gpu/GoldenSnapshotTests.cs | 1601 | 1632 |
| KhaozEngine.Showcase/Room2DGui.cs | 1296 | 1300 |

Apply the exact proposed-ratchet.json and held-whitespace.json patch payloads preserved in cleanup
commit 8a9130d41. Verify all 273 formatted paths, token/literal preservation and required guards.
A fresh scoped review and combined R1 full verification still precede main integration.


### OA11 completion worker active, 2026-10-06

- Exact approved limits are recorded and pushed in cleanup plan commit 2d978efe6b38aa4efbee161bda5934d7f08fc0a4.
- Gauge selected Claude Work Opus 5.5 high for the continuation, different from the original cleanup
  account. A fresh bounded worker received the original report, preserved patches and OA11 scope.
  Task is `node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-format-baseline-oa11-implementation-20261006`.
- Cleanup SDD has task-1-oa11-brief.md and task-1-oa11-dispatch.json, expected report task-1-oa11-report.md.
  Ownership is exactly the five held source files plus .filesize-baseline. No changes to the already
  committed 268 files, no further cap growth, no push/integration/pack/tag are assigned.
- All 273 included paths, content preservation and guards are rechecked through the shared slot.
  Controller then verifies, pushes, gets one fresh scoped cleanup review and reconciles into R1.
  No result is claimed yet. The delegated completion will wake the controller.


### OA11 completion verified, cleanup review active, 2026-10-06

- Completion commit da6f46af192745016bb7feaa538dc2960b5a6c46 is verified and pushed. It changes exactly
  the five held source files and the five approved .filesize-baseline values. Previous 268 source
  files are unchanged. Cleanup now covers all 273 allowlisted files. Tree was clean.
- Controller verified every source against de78df336 after removing ASCII whitespace, every fresh
  base-snapshot file against git, exact baseline patch equivalence and each approved line count.
  Actual final token proof has zero mismatches, round-trip failures or error-count deltas across
  627,175 tokens, including 35,327 literal tokens and 35 raw/verbatim strings.
- Final all-273 included-file formatter and all five repository guards exit 0 through the shared
  slot. Formatter still emits the generic workspace-loading warning. Full combined R1 build/format/
  suite are pending. No guard bypass, extra baseline growth or behavior change was introduced.
- Proof/log hashes are cleanup docs/superpowers/plans/proofs/2026-10-05-format-baseline/oa11-proof.json.
  Logs are /tmp/grimhollow-orch/logs/wa-format-baseline-oa11-20261006. Report is cleanup SDD
  task-1-oa11-report.md. Historical partial report remains task-1-report.md.
- One fresh Gauge-selected Claude Max 20x Opus 5.5 high reviewer is active, task
  `node:delegated-task:command%3Amcp%3A87f7c6df-2d3b-43b2-8cf0-9d291f9ecd43%3Adelegate-task%3Awa-format-baseline-review1-20261006`.
  Cleanup SDD has review-1-brief.md, review-1-dispatch.json and review-3c15218a7..da6f46af1.diff.
  Review covers the complete 273-file transform, proof method and exact OA11 baselines, with no broad
  engine audit or test rerun. Root will persist its final report and verify any findings.
- Next is accepted cleanup merge into R1, current-main reconciliation and required full combined
  verification. Main, pack, tags and game adoption remain unchanged. No owner action is needed now.


### Cleanup accepted and combined R1 verification passed, 2026-10-06

- Cleanup review 1 reports no findings and approves reconciliation into R1. Full report remains
  cleanup SDD review-1.md. Scope, exact approved caps, literal/content preservation, logs and
  source-scanning test risks were inspected. The pending full suite now passes those source scans.
- Merged cleanup at 3daf2cdc55112af9c942e1d07dee8d852ad1d9fc. One .filesize-baseline conflict was
  resolved by retaining every R1 ratchet reduction and changing only the five OA11 entries.
  All 273 merged source files exactly match the reviewed cleanup. No source-code conflict occurred.
- R1-V2 records this deterministic baseline reconciliation. Main/origin-main were fetched and remain
  de78df336, already ancestors of R1. Latest tag remains v20.25.0. R1 stages 20.27.0.
- Full Release build exited 0 with zero warnings/errors. Whole-solution format exited 0 with no
  formatting diagnostics, plus the known generic workspace-loading warning. Full non-LiveSocket
  suite exited 0 with 25,671 passed, 1,328 skipped, zero failed across 30 summary records. All five
  required repository guards exited 0. Every target ran serially through the shared slot.
- Corrected the earlier full-suite aggregation. Its original log also has 25,671 passed and 1,328
  skipped. The earlier parser consumed two interleaved records while attaching assembly names.
  Summary prefixes counted independently recover all 30 results. No tests were rerun for counting.
  The earlier proof JSON is corrected, while the original command exit and log hashes are unchanged.
- Final proof is proofs/2026-10-06-world-authoring-r1-combined-verification.json. Logs are under
  /tmp/grimhollow-orch/logs/wa-r1-combined-20261006. This full rerun followed the source merge and
  conflict reconciliation, not a flake or stress loop.
- Pivot thread f61fd8f9 received a pre-integration notice. Its Grimhollow 20.25.0 pin is untouched.
  Next is documented engine main fast-forward, push and guarded pack, then owner-only tag approval.


### R1 integrated and packed, owner release gate, 2026-10-06

- Engine main was fast-forwarded and pushed to 64a5ae3895ffc422412e6f4251406ecec54b5e22 after
  verifying clean main, current refs, no incoming ignored-file collisions and verified source identity.
  It contains source merge 3daf2cdc and all reviewed R1 and baseline-cleanup commits.
- Guarded scripts/pack-local-feed.sh ran from main under the shared slot and exited 0, refreshing
  200 package files for staged 20.27.0. Proof is proofs/2026-10-06-world-authoring-r1-integration.json,
  log /tmp/grimhollow-orch/logs/wa-r1-combined-20261006/05-pack.log. No tag was created.
- Closed engine issues #1293, #1298, #1303, #1304, #1306, #1307 and #1308 as completed after the
  verified main push. #1302 remains R9 cost work. #1305 remains the documented lexical-path/physical
  alias limitation. Native custom-registry resolution and unbound digest validation limits remain
  explicit. No cave, large-world precision, GUI-native lifecycle or importer certification is claimed.
- R1 implementation, review, verification and integration are complete. Release checkbox remains open
  pending owner authorization for v20.27.0. Current main/pack SHA at that authorization is rechecked
  and recorded in PROGRAM.md. No game pin, authored world or navigation bake was changed.
- R2 remains unstarted. Its next gate is OA9 cave representation, larger/deeper-world plan reconciliation
  and estimate review before owner approval. Game adoption still waits for all engine rounds released
  and its game-main barriers. No manual playtest is needed for the completed R1 headless proof.


### Final release-candidate identity, 2026-10-06

This planning-branch copy mirrors the completed R1 execution plan and adds final release evidence.
Engine main and origin/main are a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af. The final guarded pack
exited 0 and generated 101 nupkg plus 99 snupkg files for 20.27.0. All 200 nuspec repository commits
match that main commit. Final pack log is /tmp/grimhollow-orch/logs/wa-r1-combined-20261006/06-final-pack.log.
Exact final identity and log hash are proofs/2026-10-06-r1-release-candidate.json beside this plan.
Earlier execution proof files referenced above live on engine main under docs/superpowers/plans/proofs.

R1 is integrated, reviewed, verified and packed. Owner authorization for v20.27.0 remains pending.
Do not mark the release checkbox or start R2 implementation until the appropriate owner gates pass.
The execution worktree remains for the release handoff. No game pin, world or bake changed.


### R1 owner-authorized tag, 2026-10-06

OA12 authorizes v20.27.0. The canonical tag script created and pushed annotated tag object
2da36f154a6fca514b9a4595da52afe42019fd18, peeling to
a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af. Both identities were verified on origin.
All 200 local package files still match the tag commit. No repeat pack was needed at unchanged HEAD.
Exact release identity is proofs/2026-10-06-r1-release.json beside this plan.

The initial invocation was blocked before execution by a cross-repo hook looking at Grimhollow.
An explicit leading cd /Users/antonio/KhaozEngine gave the hook the correct repository context.
No hook bypass or game release operation occurred. Future cross-repo release invocations must make
the engine directory explicit to the command parser, not only to the process workdir.

Tag CI run 37369358693 is pending. Its GitHub package publication is not yet claimed successful.
R2 begins with read-only cave-representation research only, with no implementation approval.
Research dispatch metadata and brief live in .superpowers/sdd/2026-10-06-world-authoring-r2-owner-gate
on this planning worktree. The owner gate follows evidence review.
