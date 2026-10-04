# World Authoring R1: Native Document, Identity and Asset Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish the native document and render-free asset identity seam while preserving analytic maps and stable placement identities.

**Architecture:** Extend MapDoc with format-4 metadata and decimal-string int64 identity. Resolve digest-verified asset closure and immutable placement snapshots before any world build. Keep the existing analytic MapRuntime path compatible and expose native data for subsequent rounds.

**Tech Stack:** C# on the repository's existing .NET target, System.Numerics, System.Text.Json, closed JSON Schema, xUnit, existing engine seams. No new third-party dependency.

**Spec:** `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md`, approved direction at `49b045f75`, with inline clarifications on allocation undo and water boundary ownership. Read C1, T1 to T9 and the evidence register before implementation.

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

The approved execution method is subagent-driven-development. Execute serially after spec and plan approval. Start by checking the assigned absolute worktree and branch, then preserve unrelated changes. Line ranges below refer to `49b045f758f1110f74516b1447ffccda42d9dd95`. Re-locate symbols after prerequisite rounds, never edit by obsolete line number. Existing signatures are source-checked at that SHA. Signatures marked **new** are planned contracts, not claims that they already exist. No production code or tests run while authoring this plan.

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
| `KhaozEngine.MapDoc/Assets/MapAssetManifest.cs`, `KhaozEngine.MapDoc/Assets/MapAssetClosure.cs`, `KhaozEngine.MapDoc/Assets/MapAssetSource.cs` | GPU-free manifest DTOs, verified dependency closure and byte source |
| `KhaozEngine.MapDoc/MapResolvedDocument.cs`, `KhaozEngine.MapDoc/MapResolver.cs`, `KhaozEngine.MapDoc/MapAuthoredIdentity.cs` | Immutable identities and deterministic native resolution |
| `KhaozEngine.MapEditor/NativePlacementCommands.cs` | Explicit stable-ID remap, label and allocation-safe history |
| `KhaozEngine.MapEdit.Tool/NativeDocumentService.cs` | Native open, summary, validate and save integration |
| `KhaozEngine.Terrain.Render3D/MapAssetManifestAdapter.cs` | One-way adaptation to existing render asset entries |
| `KhaozEngine.MapDoc.Tests/` | New headless area project, only a MapDoc project reference |
| `KhaozEngine.MapEditor.Tests/MapDoc/Native*.cs` | Session and editor identity behavior |
| Existing MapDoc DTO, schema, canonical writer and tiled reader | Carry native metadata in both storage forms |


## Source-Checked Contract and Judgement Calls

Existing `MapDocumentFile.LoadText(string json, MapDocumentLoadOptions? options = null, string? sourcePath = null)`, `SaveText(MapDocument doc, MapDocRegistry? registry = null)`, `SaveTiled(MapDocument doc, string directory, MapDocRegistry? registry = null, MapDocumentSaveOptions? save = null)` are at `MapDocumentFile.cs:129-238`. Existing `MapRuntime.BuildPlacements(MapDocument doc, TerrainField field)` returns `IReadOnlyList<PropPlacement>` and writes Kind into PropPlacement.Id. Preserve that compatibility API. Native callers use `MapResolver`, which retains all three identities.

Place the render-free manifest seam inside MapDoc, not Render3D. The adapter lives in Terrain.Render3D, which already composes terrain and rendering, avoiding a reverse core dependency. Native resource kinds are a closed enum with payload-version validation. Later rounds extend handlers for their payloads and never make unsupported payloads silently acceptable. Native unresolved resources fail, while asset-free old analytic maps retain their existing load path. Storage identity remains `MapDocumentHash.OfWorld(MapDocument doc, MapDocRegistry? registry = null)`. Native authored identity is a distinct closure-bearing hash, computed from complete in-memory content rather than stale persisted tile hashes.

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
- Test helper new: `NativeFixtures.AnalyticV3Json() -> string`, a literal baseline-v3 map with one placement and no native assets.

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
    Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText("{"formatVersion":2147483647}"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t1 /tmp/grand-world/wa-r1-t1.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeDocumentTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced properties in `MapNativeDocument.cs`, converting MapDocument/MapPlacement to partial classes at their declarations. `Upgrade(JsonObject source)` deep-clones, copies absent playable bounds from storage Bounds and leaves analytic parameters and stable IDs unchanged. Register contiguous 3 to 4 migration and advance the current/schema format to 4 only if available. Close all new object schemas, reject future formats and unknown properties before deserialization. Validate finite playable bounds contained in storage Bounds. Do not widen playability to storage padding. Add assertions for unknown fields, invalid bounds and repeated migration. The test project uses the existing package versions, `IsPackable=false`, namespace `KhaozEngine.Tests.MapDoc` and only MapDoc as its project reference.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t1 /tmp/grand-world/wa-r1-t1.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeDocumentTests"`
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
    Assert.Contains(""9007199254740993"", json);
    Assert.Equal(9007199254740993L, MapDocumentFile.LoadText(json).Placements[0].NumericId);
    doc.Placements.Clear();
    Assert.Equal(9007199254740994L, MapNumericIds.Allocate(doc));
    doc.NumericIdHighWaterMark = long.MaxValue;
    Assert.Throws<OverflowException>(() => MapNumericIds.Allocate(doc));
    Assert.Equal(long.MaxValue, doc.NumericIdHighWaterMark);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t2 /tmp/grand-world/wa-r1-t2.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeNumericIdTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the two allocator signatures in `MapNumericIds.cs` using checked arithmetic and prevalidation of all reservations. Reserve above imported IDs and existing high-water mark, never derive allocation from list order. Reject duplicate/zero/negative IDs, a high-water mark below any placement ID, fractions, numeric JSON tokens, leading signs/zeros, whitespace, and values beyond int64. The high-water mark alone may be `"0"`. Reserve is transactional on an invalid input sequence. Add theories for these invalid encodings and collision cases, including `long.MaxValue` reservation.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t2 /tmp/grand-world/wa-r1-t2.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeNumericIdTests"`
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
- Create: `KhaozEngine.MapDoc/Assets/MapAssetManifest.cs`, `KhaozEngine.MapDoc/Assets/MapAssetSource.cs`, `KhaozEngine.MapDoc/Assets/MapAssetClosure.cs`
- Test: `KhaozEngine.MapDoc.Tests/NativeAssetClosureTests.cs`, `KhaozEngine.MapDoc.Tests/NativeAssetFixtures.cs`

**Interfaces:**
- Consumes new: `MapAssetRef` from Task 1.
- Produces new: `MapNativeVector2Converter : JsonConverter<Vector2>`, `MapNativeVector3Converter : JsonConverter<Vector3>`, `MapNativeJson.Configure(JsonSerializerOptions options) -> void`. Native vectors serialize as closed finite x/y[/z] objects, not default field-ignoring System.Text.Json serialization.
- Produces new: `IMapAssetSource.Read(MapAssetRef reference) -> ReadOnlyMemory<byte>`.
- Produces new: `MapAssetManifestDoc` with `int PayloadVersion`, `List<MapAssetDoc> Assets`, `List<MapResourceDoc> Resources`.
- Produces new: `MapResourceDoc` with `MapAssetRef Reference`, `MapResourceKind Kind`, `List<string> Dependencies`.
- Produces new: `MapAssetDoc` with `string Id`, `string MeshResourceId`, `string? CollisionResourceId`, `string? SelectionResourceId`, `IReadOnlyList<string> SupportResourceIds`, `MaterialResourceIds`, `LodResourceIds`, `LightResourceIds`, `float SourceUnitsToMetres`, `MapLocalBounds RenderBounds`, optional LOD/light bounds.
- Produces new: `MapLocalBounds(Vector3 Min, Vector3 Max)`, closed `MapResourceKind` (Manifest, Mesh, Material, Collider, Selection, Surface, Light, Lod, Prefab).
- Produces new: `MapAssetClosure.Load(IReadOnlyList<MapAssetRef> roots, IMapAssetSource source) -> MapAssetClosure`, `GetAsset(string assetId) -> MapResolvedAsset`, `string Hash`, immutable resource bytes and parsed asset descriptors.
- Test helper new: `NativeAssetFixtures.Valid() -> (IReadOnlyList<MapAssetRef> Roots, IMapAssetSource Source)`, `MissingLod()`, `Cycle()`, `StaleDigest()`, `FuturePayload()` with the same tuple return type.

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

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t3 /tmp/grand-world/wa-r1-t3.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeAssetClosureTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the vector converter Read/Write overrides in `MapNativeVectorConverters.cs`, registering them for document and manifest serializers without globally enabling IncludeFields. Pin nonzero vector JSON roundtrip and unknown-component refusal. Implement `MapAssetClosure.Load(...)` in `Assets/MapAssetClosure.cs`. Payload 1 owns named descriptors and resource edges. Verify SHA-256 lowercase hex over exact resource bytes, payload versions, unique resource/asset IDs, finite source units and bounds, required resource kinds and an acyclic transitive graph. Sort closure nodes by ordinal resource ID, retain semantic list/tag order, and deep-copy inputs. A source resolves relative paths against its chosen root and does not use current working directory. Collider/selection/surface bytes are closure members now, with shape interpretation delivered by R3. Reject referenced prefab payloads until R5 installs their supported handler. Add missing transitive material, duplicate IDs, path-root and mutation-of-source-byte tests. Do not acquire Render3D, GPU, TileWorld or game references.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t3 /tmp/grand-world/wa-r1-t3.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeAssetClosureTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/MapNativeVectorConverters.cs KhaozEngine.MapDoc/MapDocumentFile.cs KhaozEngine.MapDoc/Assets/MapAssetManifest.cs KhaozEngine.MapDoc/Assets/MapAssetSource.cs KhaozEngine.MapDoc/Assets/MapAssetClosure.cs KhaozEngine.MapDoc.Tests/NativeAssetClosureTests.cs KhaozEngine.MapDoc.Tests/NativeAssetFixtures.cs
git diff --cached --check
git commit -m "feat(mapdoc): resolve verified native asset closure"
```


### Task 4: Immutable placements and complete authored identity

**Files:**
- Create: `KhaozEngine.MapDoc/MapResolvedDocument.cs`, `KhaozEngine.MapDoc/MapResolver.cs`, `KhaozEngine.MapDoc/MapAuthoredIdentity.cs`
- Modify: `KhaozEngine.MapDoc/MapCanonical.cs:51-76`, `KhaozEngine.MapDoc/MapTiledFile.cs:91-150`, `KhaozEngine.MapDoc/MapTiledFile.Save.cs:151-180`, `KhaozEngine.MapDoc/MapDocumentSchema.cs:42-98`
- Test: `KhaozEngine.MapDoc.Tests/NativeResolverTests.cs`, `KhaozEngine.MapDoc.Tests/NativeStorageIdentityTests.cs`, `KhaozEngine.MapDoc.Tests/NativeStorageFixture.cs`

**Interfaces:**
- Consumes existing: `MapDocumentHash.OfWorld(MapDocument doc, MapDocRegistry? registry = null) -> string`, `MapDocument.Tiles` and its partial state.
- Consumes new: asset closure, numeric IDs and metadata.
- Produces new: `MapTransform(Vector3 Position, float YawRadians, float Scale)` with `TransformPoint(Vector3 local) -> Vector3` and `Compose(MapTransform parent,MapTransform local) -> MapTransform` using +Y yaw, scale once. Compose transforms local Position through the parent, adds yaws and multiplies positive scales.
- Produces new: immutable `MapResolvedAsset` snapshot of Task 3's asset fields and verified resource identities.
- Produces new: `MapResolvedPlacement(string PlacementId, string Kind, string AssetId, long? NumericId, MapTransform Transform, IReadOnlyList<string> Tags)`.
- Produces new: `MapResolveOptions(string BuilderId, int BuilderVersion, string OptionsHash, int ResolverVersion = 1)`.
- Produces new: `MapResolver.Resolve(MapDocument document, MapAssetClosure assets, Func<float,float,float> supportHeight, MapResolveOptions options) -> MapResolvedDocument`.
- Produces new: `MapResolvedDocument.Placements`, `Assets`, `PlayableBounds`, `StorageBounds`, `AuthoredHash` as immutable properties. `MapAuthoredIdentity.Compute(MapDocument document, MapAssetClosure assets, MapResolveOptions options) -> string`.
- Test helper new: `NativeStorageFixture : IDisposable` exposes `MapDocument Complete`, `MapDocument LoadWindow()` from a tiled native map with two occupied tiles and one loaded tile. Add it beside NativeStorageIdentityTests.
- Test helper new: `NativeResolverFixtures.Create() -> (MapDocument Document, MapAssetClosure Assets, MapResolveOptions Options)` containing two native placements with ordered tags and explicit Y.

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

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t4 /tmp/grand-world/wa-r1-t4.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeResolverTests|FullyQualifiedName~NativeStorageIdentityTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the produced resolver and identity signatures in their new files. Sort placement records by stable ordinal ID, preserve ordered tags, keep Kind/AssetId/NumericId separate and snap only null Y through the supplied supportHeight. Reject nonfinite transforms and support samples. Hash normalized complete in-memory document, TileSize, complete closure and resolver/builder/options versions. Display labels and `$schema` remain nonsemantic. Do not use a tiled document's cached world hash for edited native data. Keep legacy hash behavior for asset-free analytic maps. Carry all new globals in tiled write/read/schema. Assert monolithic versus fully loaded tiled equality at TileSize 64, dirty-content identity changes before save, re-tiling changes storage identity without moving coordinates, and a partial document refuses complete resolve/hash. Separate any explicit loaded-window identity from a complete result. Add two-independent-head identity tables and null-Y/explicit-Y tests.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t4 /tmp/grand-world/wa-r1-t4.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~NativeResolverTests|FullyQualifiedName~NativeStorageIdentityTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapDoc/MapResolvedDocument.cs KhaozEngine.MapDoc/MapResolver.cs KhaozEngine.MapDoc/MapAuthoredIdentity.cs KhaozEngine.MapDoc/MapCanonical.cs KhaozEngine.MapDoc/MapTiledFile.cs KhaozEngine.MapDoc/MapTiledFile.Save.cs KhaozEngine.MapDoc/MapDocumentSchema.cs KhaozEngine.MapDoc.Tests/NativeResolverTests.cs KhaozEngine.MapDoc.Tests/NativeStorageIdentityTests.cs KhaozEngine.MapDoc.Tests/NativeStorageFixture.cs
git diff --cached --check
git commit -m "feat(mapdoc): publish immutable native world identity"
```


### Task 5: Allocation-safe history, labels and explicit remap

**Files:**
- Create: `KhaozEngine.MapEditor/NativePlacementCommands.cs`, `KhaozEngine.MapEditor/NativePlacementReferences.cs`
- Modify: `KhaozEngine.MapEditor/EditorCommands.cs:340-417,561-597`, `KhaozEngine.MapEditor/EditorCommands.Invalidation.cs:47-61` placement identity and rename effects
- Modify: `KhaozEngine.MapEdit.Tool/MutationService.cs:92-173,1106-1160`
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativePlacementHistoryTests.cs`

**Interfaces:**
- Consumes existing: `EditorDocument.Execute(IEditorCommand command) -> void`, `Undo() -> bool`, `Redo() -> bool`, `AddPlacementCommand(MapPlacement placement)`, `RemovePlacementCommand(string id)`, `MutationService.ElementDuplicate(string kind, string? id = null, int? index = null) -> MutationResult`.
- Produces new: `AllocateNativePlacementCommand(MapPlacement placement, bool allocateNumericId) : EditorCommand`, `SetPlacementLabelCommand(string placementId, string label) : EditorCommand`, `RemapPlacementIdCommand(string oldId, string newId) : EditorCommand`.
- Produces new: `MutationService.PlacementLabel(string placementId,string label) -> MutationResult`, `PlacementRemapId(string oldId,string newId) -> MutationResult`, explicit native service operations.
- Produces new: `NativePlacementReferences.Remap(MapDocument document, string oldId, string newId) -> void`, one authoritative reference visitor later extended by R5/R6. Numeric identity never remaps.
- Test setup uses the existing `MapDocumentFileTests.SampleDoc() -> MapDocument` and native fields from Task 1.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void NativeHistory_UndoRedoAndNewBranchNeverReuseNumericId()
{
    var doc = MapDocumentFileTests.SampleDoc();
    var ed = new EditorDocument(doc);
    ed.Execute(new AllocateNativePlacementCommand(new MapPlacement { Id = "a", Kind = "prop" }, true));
    long id = doc.Placements.Single(p => p.Id == "a").NumericId!.Value;
    Assert.True(ed.Undo());
    Assert.Equal(id, doc.NumericIdHighWaterMark);
    Assert.True(ed.Redo());
    Assert.Equal(id, doc.Placements.Single(p => p.Id == "a").NumericId);
    Assert.True(ed.Undo());
    ed.Execute(new AllocateNativePlacementCommand(new MapPlacement { Id = "b", Kind = "prop" }, true));
    Assert.Equal(id + 1, doc.Placements.Single(p => p.Id == "b").NumericId);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t5 /tmp/grand-world/wa-r1-t5.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativePlacementHistoryTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement the three command constructors and Apply/Revert in `NativePlacementCommands.cs`. Allocate once on first successful apply, capture IDs for redo, retain accepted high-water marks on undo and deletion. Prepare and validate a clone before publishing so rejected edits restore both history and allocator. Native rename edits DisplayName, with a separate explicit remap command checking all references and stable-ID uniqueness. Preserve the legacy rename behavior for maps that do not opt into native identity. Update duplication to copy AssetId and ordered tags but allocate fresh stable/numeric IDs once. Add rejection-at-exhaustion, label/remap numeric invariance, duplicate-and-redo, and move/yaw/scale/delete/reload tests. Do not add a no-loss byte-roundtrip assertion across accepted numeric allocation undo, since the high-water mark intentionally persists.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t5 /tmp/grand-world/wa-r1-t5.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativePlacementHistoryTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapEditor/NativePlacementCommands.cs KhaozEngine.MapEditor/NativePlacementReferences.cs KhaozEngine.MapEditor/EditorCommands.cs KhaozEngine.MapEditor/EditorCommands.Invalidation.cs KhaozEngine.MapEdit.Tool/MutationService.cs KhaozEngine.MapEditor.Tests/MapDoc/NativePlacementHistoryTests.cs
git diff --cached --check
git commit -m "feat(mapeditor): preserve native identity through history"
```


### Task 6: Native lifecycle validation and render adapter

**Files:**
- Create: `KhaozEngine.MapEdit.Tool/NativeDocumentService.cs`
- Create: `KhaozEngine.Terrain.Render3D/MapAssetManifestAdapter.cs`
- Modify: `KhaozEngine.MapEdit.Tool/MapEditSession.cs:43-138,292-405`, `KhaozEngine.MapEdit.Tool/Results.cs:48-62`
- Modify: `KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj:1-19` project references
- Modify: `KhaozEngine.MapDoc/README.md:1-26`, `KhaozEngine.MapEdit.Tool/README.md:3-28`, `docs/USING-KHAOZENGINE.md:56-81`
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativeLifecycleTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeManifestAdapterTests.cs`

**Interfaces:**
- Consumes existing: `MapEditSession.Open(string path, IReadOnlyList<string>? manifestPaths = null) -> OpenResult`, `Save() -> SaveResult`, `Validate(bool verifyWholeWorld = false) -> ValidateResult`, `Summary() -> MapSummary`.
- Consumes existing: `AssetEntry(string id, string file, float heightMeters, string source, string license, ColliderShape? collider = null, bool surface = false, string? heightmap = null, string? collisionShape = null, string? collisionProxy = null, bool textured = false, string? category = null, string? lodFile = null)`.
- Produces new: `NativeDocumentService.ValidateComplete(MapDocument document, IMapAssetSource source, MapResolveOptions options) -> MapResolvedDocument`, `NativeDocumentSummary(string AuthoredHash, int PlacementCount, int NumericIdCount, long NumericIdHighWaterMark, string ClosureHash)` with decimal converters on numeric fields.
- Produces new: `MapAssetManifestAdapter.ToAssetEntry(MapResolvedAsset asset, string resourceRoot) -> AssetEntry`. Preserve-source-scale mesh loading is R3, the adapter must not promise normalized native runtime meshes in R1.
- New lifecycle fixture `NativeLifecycleFixture : IDisposable` exposes `MapEditSession Session`, `void CorruptResource()`, `byte[] ReadSavedBytes()` and prepares a native document plus local resources in an isolated temporary directory.

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

- [ ] **Step 2: Run test to verify it fails**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t6 /tmp/grand-world/wa-r1-t6.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeLifecycleTests|FullyQualifiedName~NativeManifestAdapterTests"`
Expected: FAIL for the named new contract or assertion. A missing planned type may initially fail compilation. Do not count an unrelated restore or fixture error as the red proof.

- [ ] **Step 3: Implement the contract**

Implement `ValidateComplete(...)` in `NativeDocumentService.cs`, routing native open/save/full-validation/summary through the resolver. Closure failures happen before session replacement or filesystem writes. Use a staged sibling write and atomic promotion for native monolithic save, preserving the tiled unindexed-overwrite guard. Test bad open preserves the existing session and missing complete closure does not report whole-world validity. Implement `ToAssetEntry(...)` in Terrain.Render3D using verified paths and keeping collision/light/LOD references in the native snapshot, never loading meshes in MapDoc. Add adapter resource tests and assembly-reference checks that MapDoc lacks GPU/Render3D/MapEditor/TileWorld. Extend lifecycle result DTOs additively. Update living API docs and perform the round verification below before handing R1 to the orchestrator.

- [ ] **Step 4: Run test to verify it passes**

Run: `/tmp/grand-world/slot-retry.sh wa-r1-t6 /tmp/grand-world/wa-r1-t6.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~NativeLifecycleTests|FullyQualifiedName~NativeManifestAdapterTests"`
Expected: PASS, exit 0, zero failed tests and at least one matching test. Inspect the test count so a misspelled filter cannot pass silently.

- [ ] **Step 5: Commit**

Preserve unrelated edits and stage only these paths.

```bash
git add -- KhaozEngine.MapEdit.Tool/NativeDocumentService.cs KhaozEngine.Terrain.Render3D/MapAssetManifestAdapter.cs KhaozEngine.MapEdit.Tool/MapEditSession.cs KhaozEngine.MapEdit.Tool/Results.cs KhaozEngine.Terrain.Render3D/KhaozEngine.Terrain.Render3D.csproj KhaozEngine.MapDoc/README.md KhaozEngine.MapEdit.Tool/README.md docs/USING-KHAOZENGINE.md KhaozEngine.MapEditor.Tests/MapDoc/NativeLifecycleTests.cs KhaozEngine.MapEditor.Tests/MapDoc/NativeManifestAdapterTests.cs
git diff --cached --check
git commit -m "feat(mapedit): validate native lifecycle and asset adaptation"
```


## Round Verification and Handoff

Run from the implementation worktree root, sequentially. Focused tests above are the task red/green cycle. The full solution suite runs once at round finish after the solution build, not once per task and never in a repeat loop. Re-run only when a subsequent code change or integration conflict requires it. The slot wrapper retries only lock contention, not failed tests.

```bash
mkdir -p local-feed
/tmp/grand-world/slot-retry.sh wa-r1-focused-KhaozEngine.MapDoc.Tests /tmp/grand-world/wa-r1-focused-KhaozEngine.MapDoc.Tests.log -- dotnet test KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj -c Release --filter "FullyQualifiedName~Native"
/tmp/grand-world/slot-retry.sh wa-r1-focused-KhaozEngine.MapEditor.Tests /tmp/grand-world/wa-r1-focused-KhaozEngine.MapEditor.Tests.log -- dotnet test KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj -c Release --filter "FullyQualifiedName~Native|FullyQualifiedName~MapDoc"
/tmp/grand-world/slot-retry.sh wa-r1-build /tmp/grand-world/wa-r1-build.log -- dotnet build KhaozEngine.slnx -c Release
/tmp/grand-world/slot-retry.sh wa-r1-format /tmp/grand-world/wa-r1-format.log -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
/tmp/grand-world/slot-retry.sh wa-r1-suite /tmp/grand-world/wa-r1-suite.log -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
/tmp/grand-world/slot-retry.sh wa-r1-check-dashes /tmp/grand-world/wa-r1-check-dashes.log -- sh scripts/check-dashes.sh --tree
/tmp/grand-world/slot-retry.sh wa-r1-check-prose /tmp/grand-world/wa-r1-check-prose.log -- sh scripts/check-prose.sh --tree
/tmp/grand-world/slot-retry.sh wa-r1-check-file-size /tmp/grand-world/wa-r1-check-file-size.log -- sh scripts/check-file-size.sh --tree
/tmp/grand-world/slot-retry.sh wa-r1-check-agent-instructions /tmp/grand-world/wa-r1-check-agent-instructions.log -- sh scripts/check-agent-instructions.sh --tree
/tmp/grand-world/slot-retry.sh wa-r1-check-doc-versions /tmp/grand-world/wa-r1-check-doc-versions.log -- bash scripts/check-doc-versions.sh
```

Require exit 0 from every command, zero warnings, nonempty focused selections, no format diff and no guard failures. These commands are future implementation verification, not authorization to run tests in the documents lane. GPU facts are skipped by ordinary `dotnet test`. Any visual golden additions use the relevant backend CI bake from `docs/CROSS-PLATFORM.md`, serialized and without booting a consumer. No local stress or repeated suite runs.

The last task also updates the package README, `docs/USING-KHAOZENGINE.md` and every stale Markdown reference for its added APIs. An orchestrator re-reads main, tags and `Directory.Build.props`, selects the next available engine minor after pivot releases, rides an existing staged version only when it belongs to this same round, and updates `CHANGELOG.md` plus all declarations checked by `check-doc-versions.sh`. Delegated workers record verified commits in Outcome and return them for integration. Each round is its own minor capability release and does not share a pivot or another round's release number. No engine release number is reserved here and no worker tags. Build, test and guard failures block the round's exit claim.

## Self-Review

Coverage: C1 schema/migration Task 1, numeric semantics Task 2, closure Task 3, immutable identity/storage Task 4, history/remap Task 5, lifecycle/adapter Task 6. Old analytic regression tests remain in the round filter. Every task has one test cycle and a reviewable deliverable. Existing interfaces were checked at the evidence SHA, new interfaces are explicitly produced before consumption, and the five Review Focus cases each have a named assertion in an owning task. The snippets pin behavior rather than implement algorithms. No later round's complete GUI, MCP, importer or rendering workflow is claimed here.

## Outcome
