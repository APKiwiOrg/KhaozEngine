# R2 full plan source register (v4)

Public register for the R2 executable plan. v4 is the final targeted correction of v3. Only the revision rows, the legacy arithmetic facts, the D7 pair-check arithmetic and the contact and witness dependency section changed. Every other v3 source claim is historical evidence at `ca13d62d7` and is unchanged. It cites only public engine source and public engine documents. Private shipped-source provenance, coordinate-bearing paths, digests, counts and reports stay outside this repository under Decision D1, in the existing ignored private artifact and the private Grimhollow proofs record. Nothing here reproduces them.

Read-only research. No build, test, format, guard, pack or runtime command was run. Engine source was read with `git show` at the evidence base. One read-only parse of csproj `ProjectReference` edges at the evidence base was used to check the TileWorld containment guard's starting state. It compiled nothing.

## Inspected revisions

| Item | Value | How checked |
| --- | --- | --- |
| Planning checkout | `feature/world-authoring` at `7f60d8854f66fd6a59f37c2539986e4ea811a9b8`, clean | `git rev-parse HEAD`, `git status --short` before and after v4. No tracked file was edited by this writer |
| API evidence base | `ca13d62d7c9f5bbdc5e6adbfffdd24d5c9148efc`, `main` when v3 was written and an ancestor of current `main` | `git merge-base --is-ancestor ca13d62d7 54a1f358` |
| Current engine `main` at v4 | `54a1f35842e1ec9c8af3ad87c7f058bf757da04f`, which `main` and `origin/main` resolve to. The three commits since the evidence base are netcode command-phase presentation work (#1313), unrelated to R2 | `git log --oneline ca13d62d7..54a1f358`. `git diff --stat ca13d62d7 54a1f358 -- KhaozEngine.MapDoc KhaozEngine.MapEditor KhaozEngine.MapEdit.Tool KhaozEngine.Primitives/WorldFrame.cs KhaozEngine.TileWorld KhaozEngine.Tests scripts/ci-selective-test.sh KhaozEngine.slnx Directory.Build.props` is empty, so every cited line below holds at current `main`. The execution base check re-runs this |
| R1 tag | `v20.27.0` peels to `a87038f5a0441d76f1ca71c8ba8acf6ec7b1b6af`, an ancestor of the base. Its package publication failed (#1309), so the tag is not an owner-released prerequisite by itself. The plan's gate requires the actual owner-released R1 publication, rechecked at execution | `git merge-base --is-ancestor`, design status text |
| MapDoc API drift since R1 | none in `KhaozEngine.MapDoc`, `KhaozEngine.MapEditor`, `KhaozEngine.MapEdit.Tool`, `KhaozEngine.Primitives/WorldFrame.cs` (v2 check, unchanged base) | `git diff --stat a87038f5a ca13d62d7 -- <paths>` empty |
| Version at base | `<KhaozEngineVersion>20.27.1</KhaozEngineVersion>`, `net10.0` | `Directory.Build.props`. Staged, unreleased at planning time. The Catalog candidate 20.28.0 was not integrated or released. Versions are rechecked at the release gate |
| Test packages | xUnit 2.9.2, runner 2.8.2, Test SDK 17.11.1 | `Directory.Packages.props` |
| Swimming F3 | commit `404fa519fafc5111dc0dfb0a457ebd56773a858b`, file SHA-256 `d508ac3b5bed503a30e12e044c91a9a33924f13184efe750961a8c496559f572`, accepted semantic contract, not released, not an ancestor of `main` | `git show 404fa519f:docs/design/SWIM-ENVIRONMENT-FACADE-F3-2026-10-06.md`, `git merge-base --is-ancestor` (false) |
| Legacy oracle engine pin | engine tag `v20.25.0` peels to `b39fb1a3bde9073d9519357b546b138b2c79d957` | `git rev-parse` |

The historical planning checkout's older code, the stale six-task plan's line numbers and the research memo's calendar ranges were not used as evidence.

## Existing API facts at `ca13d62d7`

| Symbol | File:line | Fact used by the plan |
| --- | --- | --- |
| `MapResolver.Resolve(MapDocument, MapAssetClosure, Func<float,float,float>, MapResolveOptions)` | `KhaozEngine.MapDoc/MapResolver.cs:11` | Hashes first, calls the XZ callback only for null Y, placements by id |
| `MapAuthoredIdentity.Compute(MapDocument, MapAssetClosure, MapResolveOptions)` | `KhaozEngine.MapDoc/MapAuthoredIdentity.cs:13` | Validates, hashes `SaveText`, accepts resolver version 1 only |
| `MapBoundDocumentValidation.Validate(MapDocument, MapAssetClosure, MapDocRegistry? = null)`, `ValidateLocal(MapDocument, MapDocRegistry? = null)` | `KhaozEngine.MapDoc/MapBoundDocumentValidation.cs:12,23` | Refuses partial documents (line 26), requires resolver `(1, 1)` (line 27) |
| `MapDocumentValidator.Validate(MapDocument, MapDocRegistry)` returns `IReadOnlyList<string>` | `KhaozEngine.MapDoc/MapDocumentValidator.cs:13` | `formatVersion is {n}, expected {current}.` |
| `MapTransform`, `TransformPoint`, `Compose`, `MapResolveOptions(BuilderId, BuilderVersion, OptionsHash, int ResolverVersion = 1)` | `KhaozEngine.MapDoc/MapResolvedDocument.cs:10-20,51` | Float transforms unchanged, positional options record |
| `MapResolvedDocument` | `KhaozEngine.MapDoc/MapResolvedDocument.cs:54-73` | Internal constructor only, so `GetConstructors()` is empty |
| `MapResolverIdentityDoc(int PayloadVersion, int ResolverVersion)`, `MapDocument.ResolverIdentity`, `NumericIdHighWaterMark` (`long`), `MapPlacement.NumericId` (`long?`) | `KhaozEngine.MapDoc/MapNativeDocument.cs:16,19,26,36` | Record equality used in adoption assertions |
| `MapAssetRef(string Id, string Path, string Sha256, int PayloadVersion)` | `KhaozEngine.MapDoc/MapNativeDocument.cs:39` | Reserved-path refusal test input |
| `MapDocumentException` | `KhaozEngine.MapDoc/MapDocumentException.cs:8` | `sealed`, so the plan's exact overflow type derives from `ArithmeticException`, not from it |
| `MapDocumentFile.CurrentFormatVersion = 4`, `DefaultTileSize = 512f`, migrations, `Migrate` | `KhaozEngine.MapDoc/MapDocumentFile.cs:84,91,32-34,169` | Sequential migrations, future versions refused |
| `MapDocumentFile.Load`, `LoadTiled(dir)`, `LoadTiled(dir, MapTileRect)`, `SaveText`, `SaveTiled(doc, directory, registry = null, save = null)`, `SaveAs(doc, path, form, ...)`, `VerifyTiled` | `KhaozEngine.MapDoc/MapDocumentFile.cs:106,121,127,225,236,257,273` | Entry points used by tests |
| `PrepareWholeWrite` | `KhaozEngine.MapDoc/MapDocumentFile.cs:291-300` | Whole writes refuse `Tiles.IsPartial` (line 293) with a `windowed document` message |
| `MapDocumentSaveOptions.OnStep` (internal), `MapTiledSaveStep { BeforeTileWrite, AfterTileWrite, BeforeManifestRename, AfterManifestRename, DuringSweep }` (internal) | `KhaozEngine.MapDoc/MapDocumentForm.cs:53,58` | Failure injection used through MapEditor.Tests' `InternalsVisibleTo` |
| `VerifyTiled` findings `orphan tile file the manifest does not name: ...` and `stray temp file from a crashed save: ...` | `KhaozEngine.MapDoc/MapTiledFile.cs:236,238,243` | The manifest-last assertion accepts exactly these prefixes after an injected abort |
| `MapTiledFile.ReadManifest`, `ManifestName`, `Load`, `GlobalsOnly` | `KhaozEngine.MapDoc/MapTiledFile.cs:17,22,92-130,182` | The only manifest reader, window index rebuilt at 124 |
| `MapTiledFile.Save`, `ManifestTempName` (`map.json.tmp`) | `KhaozEngine.MapDoc/MapTiledFile.Save.cs:22-113,78,101,156` | Lock (40), partial guards (48, 68), unloaded entries carried (68-73), manifest temp deleted (78), `ReadPrevious` (79), rename (101), sweep after a readable previous manifest (107), index rebuilt (112) |
| `MapTileIndex` internal constructor, `IsPartial`, `SourceDirectory` | `KhaozEngine.MapDoc/MapTileIndex.cs:27,67,71` | `IsPartial` counts tile entries only. Its comment states every save entry point checks it |
| Production `IsPartial` checks | `MapTiledFile.Save.cs:48,68`, `MapBoundDocumentValidation.cs:26`, `MapDocumentFile.cs:293`, `MapEditor/GenerateDungeonCommand.cs:86`, `MapEdit.Tool/MapEditSession.cs:342,405,575,580`, `MapEdit.Tool/NativeDocumentService.cs:48` | Nine sites inherit the unified flag (D2) |
| `MapDocumentSource.OpenTiled`, `FromDocument`, `Manifest`, `Tiles` (non-null), `Refresh` | `KhaozEngine.MapDoc/MapDocumentSource.cs:62,79,96,102,179` | Reuse `ReadManifest` or build in memory |
| `MapStorageGuardedAssetSource(string storagePath, MapDocumentForm form)`, `Read(MapAssetRef)` | `KhaozEngine.MapDoc/Assets/MapStorageGuardedAssetSource.cs:21,32` | Refuses reserved paths before the inner read |
| `MapDocumentStorage.IsReserved(string storagePath, MapDocumentForm form, string path)` | `KhaozEngine.MapDoc/MapDocumentStorage.cs:39` | Reserves manifest, temp, lock and `tiles/` |
| `MapTileCoord(int X, int Z)`, `MapTileRect(MapTileCoord Min, MapTileCoord Max)`, `MapTileGrid.CoordOf` | `KhaozEngine.MapDoc/MapTileGrid.cs:12,15,39` | Window rectangles, negative flooring |
| `NativeDocumentSnapshot.Clone(MapDocument, MapDocRegistry)`, `Publish` | `KhaozEngine.MapEditor/NativeDocumentSnapshot.cs:8,16` | Clone round-trips `SaveText`, so a partial window refuses, and keeps `candidate.Tiles = document.Tiles` (12). `Publish` copies `Tiles` (34) |
| `EditorDocument(MapDocument, MapDocRegistry? = null)`, `Doc`, `IsDirty`, `Execute(IEditorCommand)`, `Undo()` and `Redo()` returning `bool` | `KhaozEngine.MapEditor/EditorDocument.cs:34,46,85,127,142,157` | Transaction test surface |
| `AddPlacementCommand : EditorCommand, INativePlacementCommand` | `KhaozEngine.MapEditor/EditorCommands.Placements.cs:16` | Released placement path through the general seam |
| `INativePlacementCommand`, `NativePlacementTransaction.Run` | `KhaozEngine.MapEditor/NativePlacementTransaction.cs:9,16` | Publishes placements and high-water only |
| `EditorHistory.Apply`, `MutationService.Apply`, `MapEditSession.ApplyNative` | `EditorHistory.cs:28`, `MutationService.cs:34`, `MapEditSession.cs:48` | Native routing. `MutationService` is partial |
| `WorldFrame(short X, short Z)`, `Grid = 128f`, `MaxLocalRadius = 512f`, `Anchor`, `Nearest(float, float)`, `ToLocal/ToWorld` | `KhaozEngine.Primitives/WorldFrame.cs:30,36,47,64,73,85-94` | Frame indices are `short`, saturating at the `short` range |
| `TileTriangulation.SplitSwNe`, `Triangulate`, `MaxTriangles`, `CornerCut` | `KhaozEngine.TileWorld/TileTriangulation.cs:49,60,146-195` | A corner cut fans from the mid-edge point, so its creases have slope 2 in cell units and no regular fine lattice can carry them (D4) |
| `TileGroundCell(Cut, Rotation, SplitSwNe, TriangleCount)`, `TileGroundTriangles.TryDescribe(document, worldX, worldZ, plane, out TileGroundCell, Span<TileLatticeTriangle>)`, `IsDrawable`, `LatticePosition`, `Build(document, RegionCoord, plane)` | `KhaozEngine.TileWorld/TileGroundTriangles.cs:14,50,31,140,174` | Public oracle assertions |
| `TileOverlayShape { Full, DiagonalHalf, CornerQuarter, CornerThreeQuarter }` | `KhaozEngine.TileWorld/TileLayers.cs:24` | `DiagonalHalf` rotation forces the split |
| `TileGroundMesh` (internal constructor), `int[] Indices` | `KhaozEngine.TileWorld/TileGroundMesh.cs:8,10,31` | Legacy triangle counts are `Indices.Length / 3` |
| `MapDocRegistry.CreateDefault()` | `KhaozEngine.MapDoc/MapDocRegistry.cs:25` | Registry for `Clone` and validation calls |
| `TileWorldDocument.CornerHeightCm`, `CornerHeight`, `HeightAt` | `KhaozEngine.TileWorld/TileWorldDocument.Heights.cs:13,22,65` | `CornerHeight` is `CornerHeightCm(...) * 0.01f` on a `short`. `HeightAt(worldX, worldZ, plane)` computes `tx = TileX(worldX)`, `tz = TileZ(worldZ)`, `x0 = (int)MathF.Floor(tx)`, `z0 = (int)MathF.Floor(tz)`, `fx = tx - x0`, `fz = tz - z0`, reads corners `(x0, z0)`, `(x0 + 1, z0)`, `(x0, z0 + 1)`, `(x0 + 1, z0 + 1)`, then `south = h00 + (h10 - h00) * fx`, `north = h01 + (h11 - h01) * fx`, `south + (north - south) * fz`, all in `float`. `MapLegacyBilinear` (D9) reproduces this operation for operation |
| `TileWorldSpace.TileX`, `TileZ` | `KhaozEngine.TileWorld/TileWorldSpace.cs:21,24` | `TileX = worldX / tileSize`, `TileZ = -worldZ / tileSize`, the row-direction convention behind the imported `NegativeZ` frame |
| `TileWorldDocument.DefaultTileSize`, `TileSize` | `KhaozEngine.TileWorld/TileWorldDocument.cs:15,24` | Default tile size 1 m, the imported recipe's cell unit |
| `RegionCoord.Of(int worldX, int worldZ)` | `KhaozEngine.TileWorld/TileCoords.cs:11` | Takes tile coordinates and floors into 64-tile regions, the convention the Task 12A compatibility test uses with `LegacyOracleCase` |
| `TileEditOps.Smooth` | `KhaozEngine.TileWorld.Editing/TileEditOps.Heights.cs:69` | J2.1 semantics |

## Test infrastructure facts

- `TiledDocFixture` (`KhaozEngine.MapEditor.Tests/MapDoc/TiledDocFixture.cs`) is `internal static`, block-scoped namespace `KhaozEngine.Tests.MapDoc`. `SampleDoc` (line 18) uses 512 m tiles with `p-a` (10, 20, `Yaw 0.5`), `p-b` (300, 40), `p-c` (-600, 20, `Kind "hut"`, `Y 3`) and `p-d` (700, -900). `InDirectory` (79), `TileFiles` (101) returns sorted relative file paths, not contents.
- `NativePlacementHistoryFixture` (`KhaozEngine.MapEditor.Tests/MapDoc/NativePlacementHistoryFixture.cs`) is `internal sealed`, `IDisposable`, exposes `Document`, `Editor`, `Session`, `Service`, `Assets`, starts at `NumericIdHighWaterMark = 10`, and `NewProp(string id)` is declared at line 80 with its initializer through line 87 (root verified it exists, the earlier line-86 citation points inside the initializer), returning a `prop` placement with `Y = -123.5f`.
- `NativeFixtures.AnalyticV3Json()` (`KhaozEngine.MapDoc.Tests/NativeFixtures.cs`), `NativeAssetFixtures` (`NativeAssetFixtures.cs:12`).
- `InternalsVisibleTo`: MapDoc grants `KhaozEngine.MapEdit.Tool` and `KhaozEngine.MapEditor.Tests`. MapEditor and MapEdit.Tool grant `KhaozEngine.MapEditor.Tests`. `KhaozEngine.MapDoc.Tests` references only MapDoc.
- KESIZE: new files under 800 lines, baseline files never grow. Baseline includes `MutationService.cs`, `EditorCommands.cs`, `MapEditorScene.cs`.

## CI, packaging and release facts

| Fact | Source |
| --- | --- |
| Push to `main`, `v*` tags, pull requests to `main` and manual dispatch trigger CI. Push and PR run the selective script, tags and dispatch run full restore, Release build, test, determinism and pack, with publish on tags only | `.github/workflows/ci.yml:13-18,85-124` |
| Selective CI sets `SLNX="KhaozEngine.slnx"` (line 27), intersects affected projects with slnx test paths parsed by `grep -oE 'Path="[^"]+"' "$SLNX"` (line 107), and forces the architecture rump whenever any `.csproj` changes (`RUMP_TESTS`, line 133). Changes to `scripts/`, workflows or `.slnx` force the full path | `scripts/ci-selective-test.sh` |
| The architecture rump parses every top-level `*.csproj` (`LoadGraph`, line 736) into `Project(Name, IsPackableLibrary, ...)` (line 720) with full csproj stems as names, locates the repo with `RepoRoot` (line 732), and computes `TransitiveClosure` (line 777) | `KhaozEngine.Tests/ArchitectureTests.cs` (partial class) |
| At the evidence base no `KhaozEngine.MapDoc*` or `KhaozEngine.MapEdit*` project reaches any `KhaozEngine.TileWorld*` project through `ProjectReference` edges, so the containment guard starts green | Read-only parse of top-level csproj files at `ca13d62d7` |
| The GPU matrix workflow is path-filtered to render and GPU projects | `.github/workflows/cross-platform-gpu.yml:210-222` |
| `scripts/pack-local-feed.sh` packs into the main checkout's shared feed, which accepts only clean commits on `origin/main`. `KHAOZENGINE_FEED=DIR` packs privately and may carry an unmerged branch | `scripts/pack-local-feed.sh:1-22` |
| Package-bearing changes: inspect `main`, version and tags, ride a staged version or take the next free one, version and changelog in one commit, update guarded declarations, sweep docs, private-feed pack proof, reconcile with `main`, verify, merge and push through the orchestrator, then pack from `main`. Tags are user-started through `scripts/tag-release.sh` | `docs/CONTRIBUTOR-RULES.md:109-151` |

## #1310 code path

At the base, a window loaded before another writer replaced an unloaded tile still carries the old entry hash. `Save` then deletes the manifest temp and reads the previous manifest, commits a manifest naming the swept file, and sweeps the newer file. Two sequential saves reproduce it. Index construction sites: `MapTiledFile.cs:85,124`, `MapTiledFile.Save.cs:112`, `MapDocumentSource.cs:67,89,183`. Carry sites: `NativeDocumentSnapshot.cs:12,34`, `MapEditSession.cs:311`. The plan's Task 1 records the reproduction before repairing it. No reproduction has been executed.

## Legacy oracle equivalence

`git diff v20.25.0 ca13d62d7` is empty for `TileTriangulation.cs`, `TileGroundTriangles.cs` and `TileWorldDocument.Heights.cs` (v2 check). The load-path changes in `TileWorldDocument`, `TileWorldFile` and `TileWorldSource` are object-initializer formatting. Recheck at execution.

## Exact arithmetic facts used by D7 fixtures

These are arithmetic facts about the plan's own synthetic fixtures, not source facts:

- For `p = long.MaxValue`, the crossing of the edge from `(1/p, 0)` to `(1, 1)` with the line `x + z = 1` is `(p/(2p-1), (p-1)/(2p-1))`. `gcd(p, 2p-1) = gcd(p-1, 2p-1) = 1` for every `p`, so the reduced denominator is `2p - 1 > long.MaxValue`.
- For `p = 2147483647` (`int.MaxValue`), `5p^2/(p-1)` is already reduced (`p` is prime and `p - 1 = 2147483646` is not divisible by 5), and `5p^2` is about `2.3e19`, above `long.MaxValue`.
- `MapExactValue.FromSingle(0.1f)` is `13421773/134217728`. `1e-30f` has a reduced binary denominator of at least `2^100`.
- Attempted pairs are counted over clipped polygons, recomputed independently of v3's positive-bounding-box count. `HalfUnderThird` cell 1: three clipped lower polygons (rectangle `[1/3, 1/2] x [0, 1/3]`, triangle `(1/2, 0) (2/3, 0) (2/3, 1/6)`, quadrilateral `(1/2, 0) (2/3, 1/6) (2/3, 1/3) (1/2, 1/3)`) and two upper triangles give 6 attempted pairs, all with positive bounding-box overlap, five positive intersections of areas 1/72, 1/24, 1/72, 1/36, 1/72 (sum 1/9) and one empty intersection. The south-midpoint tee gives 2 by 3 = 6 attempted, 6 positive bounding-box pairs and five faces of areas 1/12, 1/6, 1/4, 1/6, 1/3 (sum 1). The NW-SE against SW-NE square gives 4, 4 and 4. Coincident SW-NE squares give 4, 4 and 2. The 1/64 m floor gives 8,192 by 2 = 16,384 attempted and 16,384 positive bounding-box pairs, 8,192 faces and 65 by 65 = 4,225 vertices, because the fine diagonals on cells (i, i) lie on the coarse diagonal. Two identical 16 by 16 SW-NE grids give 512 by 512 = 262,144 attempted pairs, 1,024 positive bounding-box pairs (each triangle's box is its own cell, shared only with the two same-cell triangles), 512 positive intersections and 17 by 17 = 289 vertices.
- `100 * 0.01f`, `200 * 0.01f`, `300 * 0.01f` and `600 * 0.01f` each round to the whole metre in `float`, so the bilinear centre of the `LegacyFallback` cell is exactly 3.0 and the next cell 4.5. `1.1f - 1f` is exactly representable (0.100000023841857910156250). The float `32001.1f` is 32001.099609375 (spacing 2^-9 in that binade), so forming a world float would sample the fraction 0.099609375 instead.

## F3 facts used

`MovementDomainKey`, `MovementSpaceKey` and `MovementSupportKey` are `(WorldId, LocalId)` opaque identities. `MovementQueryLease` is acquired through a `MovementEnvironmentContext`, validates a coverage witness, owner, generations and frame, and refuses mutation while open. `MovementSupportSet` carries availability, count, required capacity and coverage identity. `MovementQueryLease.EnumerateSupport(in MovementSupportRequest, Span<MovementSupportCandidate>)` returns a `MovementSupportSet`. All are accepted contract types at `404fa519f`, not released APIs.

## Downstream contact and witness dependencies recorded for R3/G1b

| Item | Value | How checked |
| --- | --- | --- |
| Swimming Task 2 contact evidence | Engine commit `e1b6e04c838f9dd8972ba2ece3ba0b3cb816ca14` (`feat(physics): expose complete capsule contact coverage`), `docs/design/SWIM-CONTACT-BACKEND-EVIDENCE-2026-10-06.md`. Not an ancestor of `main`. Root independently checked the focused logs: corrected contacts 23/23 and regressions 46/46, with the first 21-pass, 1-failure run preserved in the document | `git cat-file -t`, `git merge-base --is-ancestor` (false), `git show e1b6e04c8:<doc>` |
| Its refusal bounds | 4,096 selected broad-phase candidates or leaves, 4,096 children per compound, 16,384 contacts, 1,024 triangles in a mesh's expanded local capsule region, unit mesh scale only, certified error at most 0.001 m, whole-query refusal when smoothing changes a retained normal without certifiable separation. Nonzero layer masks are unsupported (#1315). No native partition guarantee | The document's capability-limit table |
| Swimming Task 3 witness and pin details | Engine commit `e74e3274c805bc57ae76f339ea3e87e1a967c942` (`docs(locomotion): pin scoped environment witness details`), `docs/design/SWIM-ENVIRONMENT-TASK3-API-DETAILS-2026-10-07.md`. Not an ancestor of `main` | `git cat-file -t`, `git merge-base --is-ancestor` (false), `git show e74e3274c:<doc>` |
| Its bounds | 256 witness IDs, 1,024 UTF-8 bytes per ID, 65,536 aggregate UTF-8 bytes, 4,096 combined producer dependency entries per prepare or pin attempt. Query-policy limits, not world or page caps. `ReferenceEquals` binding, identity consistency, bounded pre-copy and sort, exception-safe cleanup that releases the physics gate | The document's finite-bounds table and cleanup section |

Both are focused generic evidence and accepted planning dependencies, not released or native capability, whole-branch, release or G1b proof. The Task 3 value tests are authored and queued, not verified or released. The plan records them as R3/G1b obligations only and changes no R2 budget, including the 65,536-face compile budget. Swimming's local log paths are not reproduced here.

## Private evidence

The shipped-source selection named in the approved design is frozen privately. Its manifest, digests, counts and reports live in the private game repository, the private local paths named in D1 and the existing ignored private scratch artifact. Nothing here substitutes for them.

## Controller arithmetic clarification

The 16 by 16 grid counts above describe mathematical demand, not work to execute. CD7 early refusal
performs zero pair comparisons. The complete accounting control uses a 2 by 2 grid with 64 attempts,
16 positive boxes, 8 intersections and 9 vertices.

TileWorldDocument.Heights.cs computes fractions with float subtraction. At tx = -2^-30 and x0 = -1,
the exact remainder 1 - 2^-30 is not binary32 representable and rounds to 1f. The declared correctly
rounded MapExactValue.ToSingle reproduces this arithmetic. An exact-conversion-only requirement
would have changed the released behavior and is not in the canonical plan.
