# World Authoring R4: Bounded Water Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Native MapDoc water bodies, each with its own surface height, medium and render material, contained by authored R2 spaces so a surface ocean, a dry cave below it and a flooded cave lake can share one XZ column, read identically by both heads, the built world, navigation identity and the editor.

**Architecture:** Format 6 adds an optional root `water` section. Its presence selects bounded water, and its absence keeps the legacy global `MapTerrain.WaterLevel`. A body is a surface height, an exact half-open XZ domain stored in lattice units, and the authored R2 spaces it covers. The point query resolves the occupied space and its exact floor and ceiling, then reports a wet interval shaped like F3's `MovementWaterInterval`. A compiler clips each body to its covered footprints and to the exact contour of cave ceilings, giving free-surface and closed regions with counted, limited work. `KhaozEngine.MapDoc.Physics` carries water as built-world data, never as a physics static, with residency, a navigation tile medium digest and a `Medium` invalidation flag. Editor commands and tool services edit water through the existing native transaction.

**Tech Stack:** C# on the repository's .NET target, System.Numerics (including `BigInteger` for the exact water kernel), System.Text.Json, xUnit, R2's exact geometry (`MapExactValue`, `MapLatticeFrame`, `MapCommonRefinement`), `KhaozEngine.Physics.Bepu` as the test backend for the interface proof. No new package and no new third-party dependency.

**Spec:**
- `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md` (C4, DG9.2, DG9.5).
- `docs/design/WORLD-AUTHORING-R2-CAVES-SCALE-2026-10-06.md` ("Occupied spaces and gameplay domains" and the R4 row of the downstream allocation).
- The R4 owner gate and rulings, Grimhollow `feature/world-authoring` `docs/superpowers/programs/world-authoring/R4-OWNER-GATE.md` and `DECISIONS.md` (OA7, OA9, SM1, SM1a, SM2, OA24 to OA29).
- `docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md` (ownership of movement in water).

This plan replaces `2026-10-05-world-authoring-r4-bounded-water-medium.md` on `feature/world-authoring`, which was written against unreleased R3 names and runs no task as written against v20.31.0.

## Approval and execution gate

The owner approved the R4 gate decisions as OA27 on 2026-10-11 and asked for this rewrite. After plan review 1 the owner ruled OA28 (water covers authored spaces only, superseding OA27 decision 4) and OA29 (the G1b adapter certifies SM1 A3 and SM2). Execution needs the owner's approval of this plan. Execution is then subagent-driven and serial (OA8 method), with direct bounded verification (OA20). Approval of this plan does not authorize game adoption or native G1b acceptance. Engine releases follow OA25 through a KhaozEngine-rooted thread.

## Global Constraints

| Rule | Requirement |
| --- | --- |
| OA1 to OA3 | No TileWorld or Grimhollow reference from any production `KhaozEngine.MapDoc*`, `KhaozEngine.MapEditor` or `KhaozEngine.MapEdit.Tool` project. Only `KhaozEngine.MapDoc.Compatibility.Tests` and `KhaozEngine.MapDoc.OracleHarness.Tests` read TileWorld |
| OA27 decision 1 | A body is `SurfaceY`, an XZ domain and a set of covered spaces. A point is wet in body B when membership resolves space S, S is covered by B, the domain contains the point's XZ, and `lowerY(S) <= y < min(SurfaceY, upperY(S))` in exact arithmetic. `UpperIsFreeSurface` holds where `SurfaceY < upperY(S)` or the top is open. A tie closes the top. Missing, ambiguous or unrepresentable membership is reported, never dry |
| Wet boundary rule | The wet set is the intersection of the domain under OA7 and the covered spaces under R2 membership. Where a domain edge coincides with a space boundary, R2's ownership of that boundary governs, so water never leaves its space. Rendering and capture follow the same intersection |
| OA27 decision 2 | R4 ships data and queries only. No `MovementMedium` delegate for native worlds and no reference to any type that exists only on `feature/swimming-contract`. Resolver-1 maps keep `MapTerrain.WaterLevel` through an opt-in legacy move context |
| OA27 decision 3 | Public tests use a synthetic TileWorld oracle. Shipped Hollowmere data, paths, digests and counts never enter the engine repository. The shipped comparison runs only in `KhaozEngine.MapDoc.OracleHarness.Tests` under the R2 private-input rules |
| OA28 | Bodies cover authored spaces only. No implicit exterior and no change to released membership or support results. A body whose domain meets no covered footprint is refused |
| OA29 | R4 supplies exact inputs for SM1 A2, A3 and SM2: one level per body, free and closed regions split at every transition with ties closed, wet intervals with boundary ids, and explicit unresolved outcomes. R4 adds no capsule overlap classifier. The G1b adapter certifies `Tangent`, `Overlapping` and the span certificate |
| OA27 decisions 5 to 16 | Water is never a `MapStaticDescriptor`. Residency entries, a navigation tile medium digest and `MapNativeInvalidation.Medium`. One-level bodies may cover portal-connected spaces. R4 supplies #1300 data and the five DG9.5 scenes, never an optical algorithm. Bounded, counted compile work for #1297. Exact containment. A medium table in the document and a render material resolved through the asset closure. SM1 A1 and SM1a facts. Closed-top intervals representable. Prefab transforms under yaw and uniform scale. Release in the next free minor |
| OA7 boundary rule | World rectangles include minimum X and Z and exclude maximum X and Z, including negative Z. Polygon rings normalize to counter-clockwise XZ winding, meaning positive `sum(x[i] * z[i+1] - x[i+1] * z[i])`. An edge owns a boundary point when its direction has negative Z, or zero Z and positive X. A vertex belongs to a ring only when every incident edge owns it. Every legacy boundary sample whose owner changes is reported, never waived |
| SM1 and SM1a | One body id names one level. Every fact for a body carries the same `NominalSurfaceY`, its stored float. Adjacent equal-level bodies are passable by exact float equality of their `SurfaceY` |
| Native requirement | A water section requires `ResolverIdentity` with payload 1 and resolver 2 and `SupportRecipe` `AuthoredBindingsV2`. Resolver-1 documents, including native resolver-1 documents, keep legacy global water |
| Format | `MapDocumentFile.CurrentFormatVersion` becomes 6 through a pure 5 to 6 migration that adds nothing. The `water` member is optional, closed by schema, and written only when present by one canonical codec, media by key and bodies by id. `MapLatticeAddress` gains a closed JSON converter that reads through `MapLatticeAddress.Create`. A present section, even with no bodies, means bounded water and a dry world wherever no body covers |
| Identity | Water enters `MapDocumentHash` through `MapCanonical.WriteGlobals`, and through it the tiled manifest, the native root digest and the resolver-2 authored identity. It enters the built-world hash only when present. Navigation capture identity changes only for tiles that water meets |
| Precision | Domain vertices are lattice addresses in a declared `MapLatticeFrame`. Every water predicate, intersection, area and orientation is evaluated in one internal `BigInteger` rational kernel and narrowed to `MapExactValue` only when stored or emitted, and a value that cannot narrow is `NotRepresentable`, never a float fallback. `SurfaceY` must convert through `MapExactValue.FromSingle`. Floats appear only in stored `SurfaceY`, in placement transforms and in sample outputs, where a wet interval whose float bounds collapse is `NotRepresentable` |
| Immutability | A query, a compile and a built world capture a deep copy of the water set and its digest at construction. Later mutation of the caller's set never changes their results |
| Repository | Warnings are errors. New behavior gets a headless test in the matching test project under `KhaozEngine.Tests.*` namespaces. New files stay under 800 lines with no `.filesize-baseline` growth. No dash glyphs and no prose semicolons in Markdown. The released 20.31.0 public API changes only additively |
| Verification | Every local build or test runs through `build-slot`, serially, once per step. No GPU tests, windows, stress or repeated runs. Full suites run once at the candidate gate |

## Review Focus

These inputs are the most likely to hurt a user. Each has a named test in its owning task.

1. A cave directly below the ocean, with floor and ceiling under the ocean surface, must stay dry, while a cave lake below it is wet at its own level, and the lake's free surface must stop exactly where its ceiling meets the water (Task 3 `Ocean_DoesNotWetTheDryCaveBelowIt`, `CaveLake_IsItsOwnBody_AtItsOwnLevel`, `ContourTie_IsClosed`, Task 5 `CaveLake_SplitsAtTheCeilingContour`, `MixedLattices_SplitAtTheSameContour`).
2. Feet exactly at the surface, including on a bridge deck, must be dry, and a point just below must be wet (Task 3 `FeetAtTheSurface_AreDry_AndJustBelowAreWet`, Task 7 `WalkerRuleInputs_AreAvailableUnderTheReleasedLease`).
3. Negative-Z rectangle edges, region seams and space boundaries must own each boundary point once in the actual query, and every legacy ownership change must be reported (Task 2 `Rect_OwnsMinimumAndExcludesMaximum_IncludingNegativeZ`, `AdjacentPieces_OwnASharedSeamOnce`, Task 3 `BodyEdges_FollowSpaceOwnership`, Task 10 `NegativeZEdges_ReportEveryOwnershipChange`).
4. A terrain edit under a body must name the body for invalidation but never move its saved level, and an unrelated edit must never drop the water set (Task 8 `TerrainEditUnderABody_NamesTheBody_AndKeepsItsLevel`, `UnrelatedTransactions_KeepTheExactWaterSet`).
5. A huge ocean must emit the same surface geometry as a small one, a river of many rectangles must emit per rectangle, and oversized work must refuse rather than run unbounded (Task 5 `OceanSurface_IsTwoTriangles_AtAnySize`, `RiverOfManyRects_EmitsPerPiece`, `CompileLimits_RefuseOversizedWork`).

---

## Ownership and deferred work

| Item | Owner | Where it lands |
| --- | --- | --- |
| Wade, swim, water entry and exit, the deep-water walker wall (#1299) | #438 phase 4 with `feature/swimming-contract` | Consumes `MapWaterQuery` through the G1b adapter |
| Native `IMovementEnvironmentProvider` adapter: centre water, traced water contacts, SM1 A3 `Tangent` and `Overlapping`, the SM2 span certificate | G1b after #438 phase 4 releases F3 (OA29) | R4 supplies exact samples, exact region triangles and explicit unresolved outcomes |
| Layered navigation capture with water per surface, walker and aquatic profiles | #438 phase 5 with #1301 | R4 supplies intervals and tile medium digests |
| Bounded Scene3D drawing, bed-depth optics (#1300), goldens and the five DG9.5 captures | R8 | R4 supplies free-surface triangles, `DeepestBedY`, the column query and the five scenes as data |
| Render material payload format | R8 | R4 references a `Material` resource in the asset closure |
| Bounded water preview, the boundary brush and creating exterior spaces with terrain | R9 | R4 hides the legacy plane in bounded documents |
| MCP wire tools for water | R10 | R4 ships typed services, as R3 did for `TerrainApply` |
| Import of shipped bodies and named acceptance of boundary and floor differences | R11 and G2 | R4 proves the representation and the report shape |
| Prefab-local water and covered prefab interiors | R5 | Consumes `MapWaterPlacement` |
| An implicit exterior space | A possible later R2 amendment (OA28) | Not R4 |
| Different-level transitions between adjacent bodies | A later C4 decision under SM1a | Bodies at different levels never share a passable seam in R4 |
| River carve and the deep river | Swimming lane after WH1 and G5 | Not R4 |

## File Structure

| Path | Responsibility |
| --- | --- |
| `KhaozEngine.MapDoc/Water/MapWaterRecords.cs` | Format-6 records, the water set, deep copies and digests |
| `KhaozEngine.MapDoc/Water/MapWaterJson.cs` | The canonical water codec and the `MapLatticeAddress` converter |
| `KhaozEngine.MapDoc/MapDocument.Water.cs` | The root `Water` property |
| `KhaozEngine.MapDoc/Water/MapWaterMigration.cs` | The pure 5 to 6 step |
| `KhaozEngine.MapDoc/Water/MapWaterValidation.cs` | Document-level validation without geometry |
| `KhaozEngine.MapDoc/Water/MapWaterExact.cs` | The internal `BigInteger` rational kernel and narrowing |
| `KhaozEngine.MapDoc/Water/MapWaterDomain.cs`, `MapWaterPolygons.cs` | Exact domain pieces, the half-open predicate, simplicity, overlap, triangulation, clipping, height splitting, placement transforms |
| `KhaozEngine.MapDoc/Spaces/MapSpaceInterval.cs` | Exact floor and ceiling of the occupied space at a point, and the column of spaces |
| `KhaozEngine.MapDoc/Water/MapWaterQuery.cs`, `MapWaterSample.cs` | Point and column water facts with witness |
| `KhaozEngine.MapDoc/Water/MapWaterContainment.cs`, `MapWaterFootprints.cs` | Geometric validation against acquired spaces, merged footprint rectangles |
| `KhaozEngine.MapDoc/Water/MapWaterCompiler.cs`, `MapCompiledWaterBody.cs` | Free, closed and unresolved regions with work counters and limits |
| `KhaozEngine.MapDoc/Editing/MapWaterEdits.cs` | Pure water edits, write sets and effects |
| `KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs`, `MapNativeWriteSet.cs`, `MapCanonical.cs`, `MapTiledFile.cs`, `MapDocumentFile.cs`, `MapDocumentMembers.cs`, `MapDocumentSchema.cs`, `mapdoc.schema.json`, `MapBoundDocumentValidation.cs`, `MapDocumentValidator.cs`, `Spaces/MapRelationApertures.cs` | Additive hooks |
| `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs`, `MapWorldBuilder.cs`, `MapResidencyOwnership.cs`, `MapNavTiling.cs`, `MapPhysicsRegistration.cs`, `README.md` | Water as built-world data, residency, nav identity, legacy medium |
| `KhaozEngine.MapEditor/NativeWaterCommands.cs`, `NativeDocumentSnapshot.cs`, `NativeTerrainCommands.cs`, `ViewportWorld.cs` | Undoable water commands, snapshot and publication of water, terrain effects naming bodies, legacy plane suppression |
| `KhaozEngine.MapEdit.Tool/MutationService.Water.cs`, `WaterService.cs`, `Tools/McpBootstrap.cs` | Typed water services |
| `KhaozEngine.MapDoc.Tests/Water/` | Format, domain, query, containment, compiler, edit and optics-scene tests with `WaterFixtures.cs` and `WaterOpticsScenes.cs`, plus the format-6 updates in `FormatAdvanceTests.cs`, `NativeDocumentTests.cs` and `NativeNumericIdTests.cs` |
| `KhaozEngine.MapDoc.Physics.Tests/MapWaterWorldTests.cs`, `MapWaterMovementInputsTests.cs`, `NativeWorldFixtures.Water.cs`, `NativeWorldFixtures.World.cs` | Built world, residency, nav, legacy medium and the interface proof |
| `KhaozEngine.MapEditor.Tests/MapDoc/NativeWaterCommandTests.cs`, `MapEditTool/WaterServiceTests.cs` | Command and service parity. The project's existing `*Fixtures.cs` glob already links the MapDoc fixtures |
| `KhaozEngine.MapDoc.Compatibility.Tests/LegacyWaterOracleWorld.cs`, `LegacyWaterConverter.cs`, `LegacyWaterComparison.cs`, `LegacyWaterOracleTests.cs` | The public synthetic oracle |
| `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedWaterComparisonTests.cs`, `README.md` | The private shipped comparison |

## Task dependency and estimate

Estimates are engineer-day judgments, not measurements, in the same form as R2's and R3's.

| Task | Depends on | Engineer-days |
| --- | --- | --- |
| 1 Format 6 records, persistence and validation | v20.31.0 | 3 to 4 |
| 2 Exact kernel and domains | 1 | 4 to 6 |
| 3 Space intervals and the water query | 2 | 4 to 6 |
| 4 Containment and closure validation | 3 | 2 to 4 |
| 5 Region compiler | 4 | 5 to 8 |
| 6 Built world, residency, nav identity and invalidation | 5 | 3 to 5 |
| 7 Legacy medium and the interface proof | 6 | 1 to 2 |
| 8 Water edits and editor commands | 4, 6 | 3 to 5 |
| 9 Tool services | 8 | 1 to 2 |
| 10 Synthetic legacy oracle | 5 | 2 to 4 |
| 11 Private shipped comparison | 10 | 1 to 2 |
| Reviews, release preparation and integration | all | 4 to 6 |
| Total | | 33 to 54 |

## Dispatch contract

Each task is executed by a fresh worker that sees only its brief. The controller's brief for every task carries, verbatim: the Global Constraints table, the worktree and branch guard, the whole Verification conventions block, the shared fixture contract below, and the Interfaces blocks of every earlier task the task consumes. Each worker runs the setup block in its own shell before its first command. Nothing is inherited from another agent's shell or context.

## Verification conventions

Run once per worker session from the worktree root `/Users/antonio/KhaozEngine/.worktrees/wa-r4-bounded-water`:

```bash
test "$(git branch --show-current)" = "feature/wa-r4-bounded-water"
wa_r4_log_dir="/tmp/grimhollow-orch/logs/wa-r4-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p "$wa_r4_log_dir"
MAPDOC=KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj
MP=KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj
EDITOR=KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj
COMPAT=KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj
ORACLE=KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj
RUMP=KhaozEngine.Tests/KhaozEngine.Tests.csproj
wa_run()  { n=$1; shift; local rc; if build-slot --label "wa-r4-$n" -- "$@" >"$wa_r4_log_dir/$n.log" 2>&1; then rc=0; else rc=$?; fi; tail -25 "$wa_r4_log_dir/$n.log"; echo "exit $rc for $n"; return "$rc"; }
wa_test() { wa_run "$1" dotnet test "$2" -c Release -m:1 --logger "trx;LogFileName=$1.trx" --results-directory "$wa_r4_log_dir" --filter "$3"; }
```

A red step is valid only when the named tests fail for the stated reason or the build fails only on a type this task introduces. A green step needs exit 0, zero failed tests and a nonzero matching count read from the log. A filter matching nothing is a failure. Each command runs once. Exit 75 from `build-slot` means nothing ran, so retry later. Every task ends with format on its changed files and the guards:

```bash
wa_run tN-format dotnet format KhaozEngine.slnx --verify-no-changes --no-restore --include <changed .cs files>
wa_run tN-dashes sh scripts/check-dashes.sh --tree
wa_run tN-prose sh scripts/check-prose.sh --tree
wa_run tN-file-size sh scripts/check-file-size.sh --tree
wa_run tN-agent sh scripts/check-agent-instructions.sh --tree
wa_run tN-doc-versions bash scripts/check-doc-versions.sh
```

`KhaozEngine.MapDoc.OracleHarness.Tests` is outside `KhaozEngine.slnx`, so Task 11 formats it with `dotnet format KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj --verify-no-changes --no-restore`.

## Shared fixture contract

MapDoc water fixtures live in `KhaozEngine.MapDoc.Tests/Water/WaterFixtures.cs` and grow task by task. Task 1 creates a builder that the existing `CaveFixtures` helpers cannot replace, because those hard-code a positive-Z frame and slot (0, 0):

- `WaterFixtures.Surface(MapSurfaceSet set, string id, MapSurfaceRole role, MapLatticeFrame frame, long cellMinX, long cellMinZ, long cellMaxX, long cellMaxZ, Func<long, long, int> heightUnits)` adds a surface ref and every patch the lattice cell range touches, splitting the range across 64-cell slots, with heights in the frame's height units at lattice corners.
- `WaterFixtures.Footprints(MapSurfaceSet set, string spaceId, MapSpaceKind kind, MapLatticeFrame frame, long cellMinX, long cellMinZ, long cellMaxX, long cellMaxZ, MapBoundRef lower, MapBoundRef upper, MapPatchKey anchor)` adds the `MapSpaceDoc` once, anchored at `anchor`, and one `MapSpaceFootprint` per touched slot with that slot's cells.
- `WaterFixtures.Document(MapSurfaceSet set, MapWaterSet? water)` seals refs and digests the way `CaveFixtures.Document` does, sets `ResolverIdentity` to payload 1 and resolver 2 and `SupportRecipe` to `AuthoredBindingsV2`, and attaches the water set.
- `WaterFixtures.Point(float x, float y, float z)` returns the `MapFramePoint` for world metres. `WaterFixtures.Query(MapDocument doc)` returns `new MapWaterQuery(CaveFixtures.View(doc), doc.Water!)`. `WaterFixtures.Broken(string variant)` grows with each task's named variants. `WaterFixtures.TempDirectory()` returns a fresh temporary directory.
- `WaterFixtures.LowerSeabedAt(float x, float z, float metres)` returns a `MapTerrainEdit` built from `MapSetCornerHeights` that lowers the four `seabed` corners of the cell holding world (x, z), converting world Z to the negative lattice row and supplying the patch-local 2 by 2 corner block.

Every lattice uses `MapLatticeFrame.ImportedMetreCentimetre` (1 m cells, centimetre heights, rows toward negative Z) unless a fixture says otherwise. Every space and footprint record anchors in its space's floor patch, so an unloaded roof leaves the records readable.

### `WaterFixtures.OceanOverCaves()`

All XZ in world metres. Lattice row r covers world z from -(r + 1) to -r.

| Element | Geometry |
| --- | --- |
| Surface `seabed` (SupportFloor) | Cells x 0 to 12, world z -4 to 0. Height -20 m for x up to 8, rising linearly to +2 m at x 10 and flat to x 12 |
| Space `sea` (Exterior) | Footprints over every `seabed` cell, lower `seabed`, upper `OpenTop` |
| Surfaces `dry-floor` -60 m and `dry-roof` -50 m | Cells x 0 to 6, world z -4 to 0 |
| Space `dry-cave` (Cave) | Lower `dry-floor`, upper `dry-roof`, walls closing x 0 to 6 |
| Surfaces `lake-floor` -40 m and `lake-roof` | Cells x 0 to 6, world z -4 to 0. The roof rises 1 m per metre from -36 m at x 0 to -32 m at x 4, then stays at -32 m to x 6 |
| Space `lake-cave` (Cave) | Lower `lake-floor`, upper `lake-roof` |
| Surfaces `flood-floor` -40 m and `flood-roof` -25 m | Cells x 6 to 8, world z -4 to 0 |
| Space `flooded-cave` (Cave) | Lower `flood-floor`, upper `flood-roof` |
| Medium `sea-water` | Kind `Water`, `WadeSpeedScale` 1 |
| Material resource `water-material` | A `Material` resource in the fixture's asset closure |
| Body `ocean` | `SurfaceY` 0, one rectangle over x 0 to 12 and world z -4 to 0, covers `sea` |
| Body `cave-lake` | `SurfaceY` -34, one rectangle over x 0 to 6, covers `lake-cave` |
| Body `flooded` | `SurfaceY` -20, one rectangle over x 6 to 8, covers `flooded-cave` |

Every body's `RenderMaterial` is `water-material`. `WaterFixtures.Assets()` returns the closure holding it.

---

### Task 1: Format 6 records, persistence and validation

**Files:**
- Create: `KhaozEngine.MapDoc/Water/MapWaterRecords.cs`, `KhaozEngine.MapDoc/Water/MapWaterJson.cs`, `KhaozEngine.MapDoc/MapDocument.Water.cs`, `KhaozEngine.MapDoc/Water/MapWaterMigration.cs`, `KhaozEngine.MapDoc/Water/MapWaterValidation.cs`
- Modify: `KhaozEngine.MapDoc/MapDocumentFile.cs` (constant, doc comment, registration), `MapCanonical.cs` (`WriteGlobals`), `MapTiledFile.cs` (`GlobalsOnly` and the tiled root reader), `MapDocumentMembers.cs` (`IsNativeBlock`), `MapDocumentSchema.cs` and `mapdoc.schema.json`, `MapDocumentValidator.cs`
- Modify tests: `KhaozEngine.MapDoc.Tests/FormatAdvanceTests.cs` (lines 20, 36, 65, 101), `NativeDocumentTests.cs` (line 74), `NativeNumericIdTests.cs` (line 177). Output and current-format assertions become `MapDocumentFile.CurrentFormatVersion`. Historical input fixtures and their recorded resolver behaviour stay byte-identical.
- Test: `KhaozEngine.MapDoc.Tests/Water/MapWaterFormatTests.cs`, `KhaozEngine.MapDoc.Tests/Water/WaterFixtures.cs`

**Interfaces:**
- Consumes: `MapLatticeFrame`, `MapLatticeAddress.Create`, `MapRecordRef`, `MapNumericIdJsonConverter`, `MapDocumentFile.LoadText`, `SaveText`, `SaveTiled`, `LoadTiled`, `MapDocumentHash.OfWorld`, `MapSurfaceSemantics.RootDigest` (internal, visible to the MapDoc tests), `MapDocumentLoadOptions.RegisterMigration`.
- Produces, in namespace `KhaozEngine.MapDoc.Water`:
  - `public enum MapWaterMediumKind : byte { Water }`
  - `public sealed record MapWaterMediumDoc(string Key, MapWaterMediumKind Kind, float WadeSpeedScale)`
  - `public readonly record struct MapWaterRect(long MinX, long MinZ, long MaxX, long MaxZ)` in lattice cell units, corners through `MapLatticeAddress.Corner`.
  - `public sealed record MapWaterRing(IReadOnlyList<MapLatticeAddress> Vertices)`
  - `public sealed record MapWaterDomainDoc(MapLatticeFrame Frame, IReadOnlyList<MapWaterRect> Rects, IReadOnlyList<MapWaterRing> Rings)`
  - `public sealed record MapWaterBodyDoc(string Id, float SurfaceY, string Medium, string RenderMaterial, MapWaterDomainDoc Domain, IReadOnlyList<MapRecordRef> Covers, string? DisplayName = null)`. `RenderMaterial` names a `Material` resource in the document's asset closure, checked in Task 4.
  - `public sealed class MapWaterSet` with `List<MapWaterMediumDoc> Media`, `List<MapWaterBodyDoc> Bodies`, `MapWaterSet Clone()` (a deep copy whose nested lists are fresh arrays), `string Digest()` (SHA-256 hex of `kemap/water-set/1` and the codec's canonical bytes), `static string BodyDigest(MapWaterBodyDoc body)` (`kemap/water-body-doc/1`) and `static string MediumDigest(MapWaterMediumDoc medium)` (`kemap/water-medium/1`).
  - `public sealed class MapWaterSetJsonConverter : JsonConverter<MapWaterSet>` and `internal static class MapWaterJson` with `void Write(Utf8JsonWriter w, MapWaterSet water)`, the one canonical writer: media by key and bodies by id (ordinal), each body's fields in a fixed order, rects, rings and covers in stored order. Lattice addresses are objects `{ "x", "z", "d" }` whose `x` and `z` use the `MapNumericIdJsonConverter` representation of int64, read back through `MapLatticeAddress.Create`, so a missing, unknown or duplicate member or an invalid denominator refuses. Rect bounds use the same int64 representation.
  - `public static class MapWaterMigration` with `JsonObject Upgrade(JsonObject source)`.
  - `internal static class MapWaterValidation` with `Validate(MapDocument doc, List<string> errors)`.
- Produces on `MapDocument`: `public MapWaterSet? Water { get; set; }`, serialized through `MapWaterSetJsonConverter`, null by default and omitted when null.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace KhaozEngine.Tests.MapDoc.Water;

public class MapWaterFormatTests
{
    [Fact]
    public void FormatFive_MigratesToSix_WithNoWaterSection()
    {
        MapDocument doc = MapDocumentFile.LoadText(WaterFixtures.FormatFiveNativeJson());
        Assert.Equal(6, doc.FormatVersion);
        Assert.Null(doc.Water);
        Assert.DoesNotContain("\"water\"", MapDocumentFile.SaveText(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedWater_RoundTripsMonolithicAndTiled_ByteStable()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string text = MapDocumentFile.SaveText(doc);
        Assert.Equal(text, MapDocumentFile.SaveText(MapDocumentFile.LoadText(text)));
        string dir = WaterFixtures.TempDirectory();
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument tiled = MapDocumentFile.LoadTiled(dir);
        Assert.Equal(doc.Water!.Digest(), tiled.Water!.Digest());
        Assert.Equal(MapDocumentHash.OfWorld(doc), MapDocumentHash.OfWorld(tiled));
    }

    [Fact]
    public void Rings_RoundTripExactly_MonolithicAndTiled()
    {
        MapDocument doc = WaterFixtures.WithRingBody(MapLatticeAddress.Create(-1, 1, 2), MapLatticeAddress.Create(704, 64, 64),
            MapLatticeAddress.Create(11, 3, 1), MapLatticeAddress.Create(1, 10, 3));
        MapWaterRing ring = doc.Water!.Bodies.Single(b => b.Id == "ring").Domain.Rings.Single();
        MapDocument loaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
        Assert.Equal(ring.Vertices, loaded.Water!.Bodies.Single(b => b.Id == "ring").Domain.Rings.Single().Vertices);
        string dir = WaterFixtures.TempDirectory();
        MapDocumentFile.SaveTiled(doc, dir);
        Assert.Equal(ring.Vertices, MapDocumentFile.LoadTiled(dir).Water!.Bodies.Single(b => b.Id == "ring").Domain.Rings.Single().Vertices);
    }

    [Theory]
    [InlineData("address-missing-d")]
    [InlineData("address-unknown-member")]
    [InlineData("address-denominator-128")]
    public void LatticeAddressJson_RefusesBadMembers(string variant) =>
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(WaterFixtures.CorruptAddressJson(variant)));

    [Fact]
    public void UnknownWaterMember_Refuses()
    {
        string text = MapDocumentFile.SaveText(WaterFixtures.OceanOverCaves())
            .Replace("\"surfaceY\":", "\"tide\":1,\"surfaceY\":", StringComparison.Ordinal);
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(text));
    }

    [Theory]
    [InlineData("legacy-document", "water: bounded water requires a resolver-2 native document")]
    [InlineData("native-resolver-one", "water: bounded water requires a resolver-2 native document")]
    [InlineData("duplicate-medium", "water medium 'sea-water' is duplicated")]
    [InlineData("zero-wade", "water medium 'sea-water': wadeSpeedScale must be in (0, 1]")]
    [InlineData("duplicate-body", "water body 'ocean' is duplicated")]
    [InlineData("infinite-level", "water body 'ocean': surfaceY must be finite within 1000000 m")]
    [InlineData("tiny-level", "water body 'ocean': surfaceY is not representable")]
    [InlineData("unknown-medium", "water body 'ocean': unknown medium 'brine'")]
    [InlineData("empty-material", "water body 'ocean': renderMaterial must be non-empty")]
    [InlineData("empty-domain", "water body 'ocean': domain is empty")]
    [InlineData("empty-rect", "water body 'ocean': rect 0 is empty")]
    [InlineData("short-ring", "water body 'ocean': ring 0 needs at least 3 vertices")]
    [InlineData("no-cover", "water body 'ocean': covers no space")]
    [InlineData("repeated-cover", "water body 'ocean': cover 'sea' is repeated")]
    public void Validation_RefusesBadMediaAndBodies(string variant, string message)
    {
        IReadOnlyList<string> errors = MapDocumentValidator.Validate(WaterFixtures.Broken(variant), MapDocRegistry.CreateDefault());
        Assert.Contains(errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void WaterSection_EntersTheWorldHashAndTheNativeRoot()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        (string world, string root) = (MapDocumentHash.OfWorld(doc), MapSurfaceSemantics.RootDigest(doc));
        doc.Water!.Bodies[0] = doc.Water.Bodies[0] with { SurfaceY = 0.25f };
        Assert.NotEqual(world, MapDocumentHash.OfWorld(doc));
        Assert.NotEqual(root, MapSurfaceSemantics.RootDigest(doc));
    }

    [Fact]
    public void WaterOrder_ChangesNeitherBytesNorHash()
    {
        MapDocument a = WaterFixtures.OceanOverCaves(), b = WaterFixtures.OceanOverCaves();
        b.Water!.Bodies.Reverse();
        b.Water.Media.Insert(0, new MapWaterMediumDoc("brine", MapWaterMediumKind.Water, 0.5f));
        a.Water!.Media.Add(new MapWaterMediumDoc("brine", MapWaterMediumKind.Water, 0.5f));
        Assert.Equal(MapDocumentFile.SaveText(a), MapDocumentFile.SaveText(b));
        Assert.Equal(MapDocumentHash.OfWorld(a), MapDocumentHash.OfWorld(b));
    }

    [Fact]
    public void PresentEmptySection_DiffersFromAbsent()
    {
        MapDocument absent = WaterFixtures.OceanOverCaves(), empty = WaterFixtures.OceanOverCaves();
        absent.Water = null;
        empty.Water = new MapWaterSet();
        Assert.NotEqual(MapDocumentHash.OfWorld(absent), MapDocumentHash.OfWorld(empty));
        Assert.Contains("\"water\"", MapDocumentFile.SaveText(empty), StringComparison.Ordinal);
    }
}
```

`FormatFiveNativeJson()` is the smallest valid format-5 resolver-2 document text. `WithRingBody(...)` is `OceanOverCaves()` plus body `ring` whose domain is one convex ring of the given addresses covering `dry-cave`, so it overlaps no other body in its space. `CorruptAddressJson(variant)` is the saved text of that document with the first ring address damaged as named. `Broken(variant)` applies the named defect to `OceanOverCaves()`: `legacy-document` clears `ResolverIdentity`, `native-resolver-one` sets it to payload 1 and resolver 1, and `tiny-level` sets `SurfaceY` to `1e-20f`, which `MapExactValue.FromSingle` refuses. Build `OceanOverCaves()` now as the shared fixture contract describes, so later tasks only add queries and variants.

- [ ] **Step 2: Run red** `wa_test t1-red "$MAPDOC" "FullyQualifiedName~MapWaterFormatTests"`

- [ ] **Step 3: Implement the records, codec, migration, persistence and validation**

`CurrentFormatVersion` becomes 6 and the doc comment adds "v6 adds bounded water". `MapDocumentLoadOptions` registers `RegisterMigration(5, MapWaterMigration.Upgrade)`, which accepts only format 5 or 6 input like `MapSurfaceMigration` and adds no member. `MapCanonical.WriteGlobals` ends by writing `water` through `MapWaterJson.Write` when `doc.Water` is not null. Because `MapDocumentHash.OfManifest`, the tiled manifest and `MapSurfaceRootProjection` all call `WriteGlobals`, water enters the world hash, the manifest and the native root digest once each, and no other native global joins the legacy manifest hash. The monolithic file uses the same codec through the property converter. `GlobalsOnly` copies `Water`, and the tiled root reader restores it. The schema closes every water object, and `IsNativeBlock` lists `water`.

`MapWaterValidation.Validate` runs from `MapDocumentValidator.Validate` after `MapNativeValidation.Validate` and produces exactly the messages in the test. A present section requires `ResolverIdentity` payload 1 and resolver 2 and `SupportRecipe` `AuthoredBindingsV2`. A medium key is non-empty and unique, its kind is defined, and `WadeSpeedScale` is finite with `0 < s <= 1`. A body id is non-empty and unique, `SurfaceY` is finite with absolute value at most 1,000,000 and converts through `MapExactValue.FromSingle`, `Medium` names a medium, `RenderMaterial` is non-empty, the domain has at least one rect or ring, a rect has `MinX < MaxX` and `MinZ < MaxZ`, a ring has at least 3 vertices, the frame is valid, covers are non-empty and distinct by id. Geometry rules arrive in Task 2, containment and the material closure in Task 4.

Update the three existing test files so their current-format and saved-output assertions read `MapDocumentFile.CurrentFormatVersion`, and add nothing else to them.

- [ ] **Step 4: Run green** `wa_test t1-green "$MAPDOC" "FullyQualifiedName~MapWaterFormatTests"` (expect 24), then `wa_test t1-regress "$MAPDOC" "FullyQualifiedName~FormatAdvance|FullyQualifiedName~NativeDocumentTests|FullyQualifiedName~NativeNumericId|FullyQualifiedName~Tiled|FullyQualifiedName~Hash"`.

- [ ] **Step 5: Format, guards and commit** `feat(mapdoc): add format 6 bounded water records`

---

### Task 2: Exact kernel and domains

**Files:**
- Create: `KhaozEngine.MapDoc/Water/MapWaterExact.cs`, `KhaozEngine.MapDoc/Water/MapWaterDomain.cs`, `KhaozEngine.MapDoc/Water/MapWaterPolygons.cs`
- Modify: `KhaozEngine.MapDoc/Water/MapWaterValidation.cs`
- Test: `KhaozEngine.MapDoc.Tests/Water/MapWaterDomainTests.cs`

**Interfaces:**
- Consumes: Task 1 records, `MapLatticeFrame.WorldXz`, `MapLatticeAddress.Corner`, `MapExactValue`, `MapExactXz`, `MapExactPoint`.
- Produces:
  - `internal readonly struct MapWideRational : IComparable<MapWideRational>`, a normalized `BigInteger` fraction with `From(MapExactValue)`, `Add`, `Subtract`, `Multiply`, `Divide`, `Sign`, `CompareTo` and `bool TryNarrow(out MapExactValue value)`. `internal readonly record struct MapWideXz(MapWideRational X, MapWideRational Z)` and `internal readonly record struct MapWidePoint(MapWideRational X, MapWideRational Y, MapWideRational Z)`. Every water predicate and construction uses these, and only narrowing can fail.
  - `public readonly record struct MapWaterPlacement(Vector3 Position, float Yaw, float Scale)` with `float SurfaceY(float local) => Position.Y + Scale * local`. A local XZ point (x, z) maps to `Vector3.Transform(new Vector3(x, 0, z) * Scale, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Yaw)) + Position` in float, and `Scale` is positive.
  - `public sealed record MapWaterPiece(IReadOnlyList<MapExactXz> Ring)`, counter-clockwise in world XZ.
  - `public sealed class MapWaterDomain` with `static MapWaterDomain Compile(MapWaterDomainDoc doc, MapWaterPlacement? placement = null)`, `IReadOnlyList<MapWaterPiece> Pieces`, `MapExactXz Min`, `MapExactXz Max`, `bool Contains(MapExactXz point)`, `bool PieceContains(int piece, MapExactXz point)` and `static bool OverlapsPositively(MapWaterDomain a, MapWaterDomain b)`. `Compile` throws `MapDocumentException` containing "not representable" when a transformed corner cannot convert through `MapExactValue.FromSingle`.
  - `internal static class MapWaterPolygons` with, all on the wide types: signed area, simplicity, the OA7 edge and vertex rule, point-in-ring, positive-area intersection, exact ear-clipping triangulation, `ClipToRect(IReadOnlyList<MapWideXz> convex, MapWideXz min, MapWideXz max)` and `SplitAtHeight(IReadOnlyList<MapWidePoint> convex, MapWideRational y)`, which splits a planar convex polygon and returns the part strictly above `y` and the rest as convex polygons. Task 5 uses the last three.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWaterDomainTests
{
    static readonly MapLatticeFrame Up = new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
    static MapExactXz Xz(float x, float z) => new(MapExactValue.FromSingle(x), MapExactValue.FromSingle(z));
    static MapWaterDomain Rects(MapLatticeFrame frame, params MapWaterRect[] rects) =>
        MapWaterDomain.Compile(new(frame, rects, Array.Empty<MapWaterRing>()));
    static MapWaterDomain Ring(params (long X, long Z)[] points) => MapWaterDomain.Compile(new(Up, Array.Empty<MapWaterRect>(),
        new[] { new MapWaterRing(points.Select(p => MapLatticeAddress.Corner(p.X, p.Z)).ToArray()) }));

    [Fact]
    public void Rect_OwnsMinimumAndExcludesMaximum_IncludingNegativeZ()
    {
        // Lattice rows 3 to 7 run toward negative Z, so the world rectangle is x [2, 5) by z [-7, -3).
        MapWaterDomain d = Rects(MapLatticeFrame.ImportedMetreCentimetre, new MapWaterRect(2, 3, 5, 7));
        Assert.True(d.Contains(Xz(2, -7)));
        Assert.True(d.Contains(Xz(2, -5)));
        Assert.True(d.Contains(Xz(4.5f, -3.5f)));
        Assert.False(d.Contains(Xz(5, -7)));
        Assert.False(d.Contains(Xz(2, -3)));
        Assert.False(d.Contains(Xz(3, -3)));
    }

    [Fact]
    public void Polygon_NormalizesWindingAndOwnsEdgesByDirection()
    {
        MapWaterDomain clockwiseSquare = Ring((0, 0), (0, 4), (4, 4), (4, 0));
        MapWaterDomain lower = Ring((0, 0), (4, 0), (0, 4));
        MapWaterDomain upper = Ring((4, 0), (4, 4), (0, 4));
        foreach (var (x, z, inside) in new[] { (0f, 0f, true), (2f, 0f, true), (0f, 2f, true), (4f, 0f, false), (0f, 4f, false), (4f, 2f, false), (2f, 4f, false) })
            Assert.Equal(inside, clockwiseSquare.Contains(Xz(x, z)));
        Assert.False(lower.Contains(Xz(2, 2)));
        Assert.True(upper.Contains(Xz(2, 2)));
        Assert.True(lower.Contains(Xz(0, 0)));
        Assert.False(lower.Contains(Xz(4, 0)));
        Assert.False(upper.Contains(Xz(4, 0)));
    }

    [Fact]
    public void AdjacentPieces_OwnASharedSeamOnce()
    {
        var pieces = new[] { Ring((0, 0), (4, 0), (0, 4)), Ring((4, 0), (4, 4), (0, 4)), Rects(Up, new MapWaterRect(4, 0, 8, 4)) };
        for (int qx = 0; qx < 32; qx++)
            for (int qz = 0; qz < 16; qz++)
                Assert.Equal(1, pieces.Count(d => d.Contains(Xz(qx / 4f, qz / 4f))));
    }

    [Fact]
    public void Overlap_RefusesPositiveAreaAndAllowsTouching()
    {
        MapWaterDomain a = Rects(Up, new MapWaterRect(0, 0, 4, 4));
        Assert.False(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(4, 0, 8, 4))));
        Assert.False(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(4, 4, 8, 8))));
        Assert.True(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(3, 0, 5, 4))));
        Assert.True(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(0, 0, 4, 4))));
        Assert.True(MapWaterDomain.OverlapsPositively(a, Rects(Up, new MapWaterRect(1, 1, 2, 2))));
        Assert.False(MapWaterDomain.OverlapsPositively(Ring((0, 0), (4, 0), (0, 4)), Ring((4, 0), (4, 4), (0, 4))));
        Assert.True(MapWaterDomain.OverlapsPositively(Ring((0, 0), (4, 0), (0, 4)), Rects(Up, new MapWaterRect(1, 1, 3, 3))));
    }

    [Fact]
    public void Placement_TransformsOnceUnderYawAndScale()
    {
        var placement = new MapWaterPlacement(new Vector3(10.23f, 5f, -3.17f), 0.371f, 1.137f);
        MapWaterDomain d = MapWaterDomain.Compile(new(Up, new[] { new MapWaterRect(0, 0, 2, 1) }, Array.Empty<MapWaterRing>()), placement);
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, placement.Yaw);
        MapExactXz World(float x, float z)
        {
            Vector3 w = Vector3.Transform(new Vector3(x, 0, z) * placement.Scale, yaw) + placement.Position;
            return Xz(w.X, w.Z);
        }
        Assert.Equal(new[] { World(0, 0), World(2, 0), World(2, 1), World(0, 1) }.Order(), d.Pieces.Single().Ring.Order());
        Assert.True(d.Contains(World(1, 0.5f)));
        Assert.Equal(5f + 1.137f * 0.5f, placement.SurfaceY(0.5f));
    }

    [Fact]
    public void Placement_WithUnrepresentableCorners_Refuses()
    {
        var tiny = new MapWaterPlacement(Vector3.Zero, 0.371f, 1e-20f);
        var error = Assert.Throws<MapDocumentException>(() =>
            MapWaterDomain.Compile(new(Up, new[] { new MapWaterRect(0, 0, 2, 1) }, Array.Empty<MapWaterRing>()), tiny));
        Assert.Contains("not representable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitAtHeight_NearThirtyTwoKilometres_KeepsExactArea()
    {
        static MapWideRational Q(long n, long d) => MapWideRational.From(new MapExactValue(n, d));
        var a = new MapWidePoint(Q(32000 * 61 + 1, 61), Q(-2, 5), Q(1, 61));
        var b = new MapWidePoint(Q(32001 * 63 + 1, 63), Q(-3, 10), Q(1, 63));
        var c = new MapWidePoint(Q(32000 * 59 + 1, 59), Q(-1, 5), Q(58, 59));
        MapWideRational surface = MapWideRational.From(MapExactValue.FromSingle(-0.37f));
        var (above, rest) = MapWaterPolygons.SplitAtHeight(new[] { a, b, c }, surface);
        Assert.Equal(WaterFixtures.WideArea(a, b, c), WaterFixtures.WideArea(above).Add(WaterFixtures.WideArea(rest)));
        Assert.All(above.Concat(rest).SelectMany(p => p), p => Assert.True(p.X.TryNarrow(out _) && p.Z.TryNarrow(out _)));
        Assert.Contains(above.SelectMany(p => p), p => p.Y.CompareTo(surface) == 0);
    }

    [Theory]
    [InlineData("bow-tie", "water body 'ocean': ring 0 self-intersects")]
    [InlineData("collinear", "water body 'ocean': ring 0 has zero area")]
    [InlineData("overlapping-pieces", "water body 'ocean': pieces 0 and 1 overlap")]
    [InlineData("overlapping-bodies", "water bodies 'cave-lake' and 'second-lake' overlap in space 'lake-cave'")]
    public void Validation_RefusesBadGeometry(string variant, string message)
    {
        IReadOnlyList<string> errors = MapDocumentValidator.Validate(WaterFixtures.Broken(variant), MapDocRegistry.CreateDefault());
        Assert.Contains(errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void BodiesInDifferentSpaces_MayShareXz() =>
        Assert.Empty(MapDocumentValidator.Validate(WaterFixtures.OceanOverCaves(), MapDocRegistry.CreateDefault()));
}
```

The split case is the review's counterexample: its first origin-relative product has a denominator beyond `long`, while every emitted coordinate fits after cancellation. `WaterFixtures.WideArea` sums the exact XZ areas of a triangle or of polygons in wide arithmetic. `Broken("overlapping-bodies")` adds a body `second-lake` that also covers `lake-cave` with a rectangle overlapping `cave-lake`. `OceanOverCaves()` already has `ocean` overlapping `cave-lake` and `flooded` in XZ while covering a different space.

- [ ] **Step 2: Run red** `wa_test t2-red "$MAPDOC" "FullyQualifiedName~MapWaterDomainTests"`

- [ ] **Step 3: Implement the kernel, domains and geometry validation**

`MapWideRational` normalizes by the greatest common divisor after every operation, so wide intermediates cancel before narrowing. A rect converts its four lattice corners through `Frame.WorldXz`, and the world rectangle is then ordered by world coordinates, so a negative-Z frame owns its world minimum. A ring converts each address the same way. Both normalize to counter-clockwise under the Global Constraints sign rule. With a placement, each converted corner goes to float, through the `MapWaterPlacement` expression, and back to exact through `MapExactValue.FromSingle`, and a refusal becomes the "not representable" error. Containment uses only exact comparisons and the OA7 edge and vertex rule. `OverlapsPositively` is false for shared edges and vertices and true for any positive-area intersection, including equal and nested pieces, with an exact bounds test first. `SplitAtHeight` intersects edges with the plane exactly and keeps the strictly-above part separate, so a vertex exactly at `y` belongs to the rest. `MapWaterValidation` gains self-intersection and zero-area checks per ring, a positive-area check between pieces of one body, and a positive-area check between bodies that share a cover id. Contour intersections stay wide or `MapExactXz` and never pass through `MapLatticeFrame.AddressOf`.

- [ ] **Step 4: Run green** `wa_test t2-green "$MAPDOC" "FullyQualifiedName~MapWaterDomainTests"` (expect 12)

- [ ] **Step 5: Format, guards and commit** `feat(mapdoc): add the exact water kernel and half-open domains`

---

### Task 3: Space intervals and the water query

**Files:**
- Create: `KhaozEngine.MapDoc/Spaces/MapSpaceInterval.cs`, `KhaozEngine.MapDoc/Water/MapWaterSample.cs`, `KhaozEngine.MapDoc/Water/MapWaterQuery.cs`
- Modify: `KhaozEngine.MapDoc/Spaces/MapSpaceMembership.cs` (one internal entry point that returns the selected candidate's exact bounds)
- Test: `KhaozEngine.MapDoc.Tests/Spaces/MapSpaceIntervalTests.cs`, `KhaozEngine.MapDoc.Tests/Water/MapWaterQueryTests.cs`

**Interfaces:**
- Consumes: `MapSpaceMembership.QueryCore` internals, `MapSpacePointBounds.Height`, Task 1 and Task 2 types, `MapScopedSurfaces.ReadWitness`.
- Produces:
  - `public sealed record MapSpaceInterval(MapMembershipStatus Status, string? SpaceId, MapExactValue Lower, MapExactValue? Upper, string? LowerBoundaryId, string? UpperBoundaryId, string? Detail, MapReadWitness Witness)`. `Upper` is null for an open top. A boundary id is the bound's `SurfaceId`, else its opening record id, else `open-top`. A point resolved inside a portal band reports the band bottom and top with the band chain record ids.
  - `public static class MapSpaceIntervals` with `MapSpaceInterval At(MapScopedSurfaces view, MapFramePoint point)` and `IReadOnlyList<MapSpaceInterval> Column(MapScopedSurfaces view, MapFramePoint point)`, the second listing every footprint interval at the point's XZ in ascending `Lower`, or one failure entry.
  - `public enum MapWaterStatus { Dry, Wet, MissingGeometry, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }`
  - `public sealed record MapWaterSample(MapWaterStatus Status, MapFramePoint Point, string? BodyId, string? SpaceId, string? Medium, float LowerY, float UpperY, float NominalSurfaceY, bool UpperIsFreeSurface, string? LowerBoundaryId, string? UpperBoundaryId, string? Detail, MapReadWitness Witness, string WaterDigest)`
  - `public sealed class MapWaterQuery` with `MapWaterQuery(MapScopedSurfaces scoped, MapWaterSet water)`, which deep-copies `water` and fixes its digest at construction, `string WaterDigest`, `MapWaterSample Sample(MapFramePoint point)` and `IReadOnlyList<MapWaterSample> Column(MapFramePoint point)`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWaterQueryTests
{
    static readonly MapDocument Doc = WaterFixtures.OceanOverCaves();
    static MapWaterSample At(float x, float y, float z) => WaterFixtures.Query(Doc).Sample(WaterFixtures.Point(x, y, z));

    [Fact]
    public void Ocean_DoesNotWetTheDryCaveBelowIt()
    {
        MapWaterSample s = At(2, -55, -2);
        Assert.Equal((MapWaterStatus.Dry, "dry-cave"), (s.Status, s.SpaceId));
        Assert.Null(s.BodyId);
    }

    [Fact]
    public void CaveLake_IsItsOwnBody_AtItsOwnLevel()
    {
        MapWaterSample s = At(5, -35, -2);
        Assert.Equal((MapWaterStatus.Wet, "cave-lake", "lake-cave"), (s.Status, s.BodyId, s.SpaceId));
        Assert.Equal((-40f, -34f, -34f, true), (s.LowerY, s.UpperY, s.NominalSurfaceY, s.UpperIsFreeSurface));
        Assert.Equal(("lake-floor", "water:cave-lake"), (s.LowerBoundaryId, s.UpperBoundaryId));
    }

    [Fact]
    public void ContourTie_IsClosed()
    {
        MapWaterSample s = At(2, -35, -2);
        Assert.Equal((MapWaterStatus.Wet, -34f, false, "lake-roof"), (s.Status, s.UpperY, s.UpperIsFreeSurface, s.UpperBoundaryId));
    }

    [Fact]
    public void FloodedCave_HasAClosedTop()
    {
        MapWaterSample s = At(7, -30, -2);
        Assert.Equal((MapWaterStatus.Wet, "flooded"), (s.Status, s.BodyId));
        Assert.Equal((-25f, -20f, false, "flood-roof"), (s.UpperY, s.NominalSurfaceY, s.UpperIsFreeSurface, s.UpperBoundaryId));
    }

    [Fact]
    public void FeetAtTheSurface_AreDry_AndJustBelowAreWet()
    {
        Assert.Equal(MapWaterStatus.Dry, At(4, 0f, -2).Status);
        Assert.Equal(MapWaterStatus.Wet, At(4, -0.0001f, -2).Status);
        Assert.Equal(MapWaterStatus.Dry, At(5, -34f, -2).Status);
        Assert.Equal(MapWaterStatus.Wet, At(5, MathF.BitDecrement(-34f), -2).Status);
        Assert.Equal(MapWaterStatus.Dry, At(11, 2.5f, -2).Status);
    }

    [Fact]
    public void BodyEdges_FollowSpaceOwnership()
    {
        // The ocean's domain owns world z -4 and excludes z 0. The sea footprint owns lattice rows 0 to 3, which
        // includes world z 0 and excludes z -4. The wet set is the intersection, so both edges are dry.
        MapWaterSample minEdge = At(4, -1, -4), maxEdge = At(4, -1, 0), inside = At(4, -1, -2);
        Assert.Equal((MapWaterStatus.Dry, null), (minEdge.Status, minEdge.SpaceId));
        Assert.Equal((MapWaterStatus.Dry, "sea"), (maxEdge.Status, maxEdge.SpaceId));
        Assert.Equal(MapWaterStatus.Wet, inside.Status);
        Assert.Equal(new[] { "ocean" }, WaterFixtures.Query(Doc).Column(WaterFixtures.Point(9, 0, -2)).Select(s => s.BodyId));
    }

    [Fact]
    public void OutsideEverySpace_IsDry_AndOutsideTheAcquisitionIsMissing()
    {
        MapWaterSample outside = At(40, -1, -2);
        Assert.Equal((MapWaterStatus.Dry, null), (outside.Status, outside.SpaceId));
        Assert.Equal(MapMembershipStatus.Outside, new MapSpaceMembership(CaveFixtures.View(Doc)).Query(WaterFixtures.Point(40, -1, -2)).Status);
        Assert.Equal(MapWaterStatus.MissingGeometry, At(80, -1, -2).Status);
    }

    [Fact]
    public void AmbiguousGeometry_IsNeverDry()
    {
        MapDocument peers = WaterFixtures.PeerChambersWithWater();
        MapWaterSample s = new MapWaterQuery(CaveFixtures.View(peers), peers.Water!).Sample(WaterFixtures.PeerPoint());
        Assert.Equal(MapWaterStatus.Ambiguous, s.Status);
    }

    [Fact]
    public void Column_ListsStackedWaterInOrder()
    {
        IReadOnlyList<MapWaterSample> column = WaterFixtures.Query(Doc).Column(WaterFixtures.Point(5, 0, -2));
        Assert.Equal(new[] { "cave-lake", "ocean" }, column.Select(s => s.BodyId));
        Assert.All(column, s => Assert.Equal(MapWaterStatus.Wet, s.Status));
    }

    [Fact]
    public void WetIntervals_SatisfyTheMovementIntervalContract()
    {
        MapWaterQuery query = WaterFixtures.Query(Doc);
        MapWaterSample[] wet = WaterFixtures.ProbeGrid(Doc).Select(query.Sample).Where(s => s.Status == MapWaterStatus.Wet).ToArray();
        Assert.Equal(new[] { "cave-lake", "flooded", "ocean" }, wet.Select(s => s.BodyId).Distinct().Order());
        foreach (MapWaterSample s in wet)
        {
            Assert.True(s.LowerY < s.UpperY && s.UpperY <= s.NominalSurfaceY);
            Assert.True(!s.UpperIsFreeSurface || s.UpperY == s.NominalSurfaceY);
            Assert.False(string.IsNullOrWhiteSpace(s.LowerBoundaryId) || string.IsNullOrWhiteSpace(s.UpperBoundaryId));
            Assert.Equal(Doc.Water!.Bodies.Single(b => b.Id == s.BodyId).SurfaceY, s.NominalSurfaceY);
        }
    }

    [Fact]
    public void Query_CarriesItsWitness_AndIgnoresLaterMutation()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapScopedSurfaces view = CaveFixtures.View(doc);
        var query = new MapWaterQuery(view, doc.Water!);
        string digest = doc.Water!.Digest();
        doc.Water.Bodies[0] = doc.Water.Bodies[0] with { SurfaceY = -39f };
        doc.Water.Bodies.RemoveAt(2);
        MapWaterSample s = query.Sample(WaterFixtures.Point(5, -35, -2));
        Assert.Same(view.ReadWitness, s.Witness);
        Assert.Equal((digest, digest), (query.WaterDigest, s.WaterDigest));
        Assert.Equal((MapWaterStatus.Wet, -34f), (s.Status, s.NominalSurfaceY));
    }
}

public class MapSpaceIntervalTests
{
    [Fact]
    public void Interval_ReportsExactBoundsAndBoundaryIds()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapSpaceInterval i = MapSpaceIntervals.At(CaveFixtures.View(doc), WaterFixtures.Point(2, -38, -2));
        Assert.Equal(("lake-cave", "lake-floor", "lake-roof"), (i.SpaceId, i.LowerBoundaryId, i.UpperBoundaryId));
        Assert.Equal(new MapExactValue(-40, 1), i.Lower);
        Assert.Equal(new MapExactValue(-34, 1), i.Upper);
        Assert.Null(MapSpaceIntervals.At(CaveFixtures.View(doc), WaterFixtures.Point(11, 3, -2)).Upper);
    }

    [Fact]
    public void Column_ListsEverySpaceAtXzInAscendingOrder()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        var column = MapSpaceIntervals.Column(CaveFixtures.View(doc), WaterFixtures.Point(3, 0, -2));
        Assert.Equal(new[] { "dry-cave", "lake-cave", "sea" }, column.Select(c => c.SpaceId));
    }

    [Fact]
    public void Interval_AgreesWithMembershipEverywhere()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapScopedSurfaces view = CaveFixtures.View(doc);
        foreach (MapFramePoint p in WaterFixtures.ProbeGrid(doc))
            Assert.Equal(new MapSpaceMembership(view).Query(p).SpaceId, MapSpaceIntervals.At(view, p).SpaceId);
    }
}
```

`ProbeGrid(doc)` returns points every 0.5 m in x and z inside the fixture and every 1 m in y from -62 to 4, plus each body's surface height and one float step below it. `PeerChambersWithWater()` is `CaveFixtures.WithPeerChamber(null)` with resolver-2 identity, one medium and a body covering the chamber. `PeerPoint()` is a point in the shared column that membership reports as ambiguous. `CaveFixtures.View` acquires whole 64-cell slots, so x 40 is inside the acquisition and outside every footprint, while x 80 is outside the acquisition. At x 2 the lake roof is exactly -34, the tie the contour test pins. Subnormal heights such as `MathF.BitDecrement(0f)` are outside `MapExactValue.FromSingle` and are not test inputs.

- [ ] **Step 2: Run red** `wa_test t3-red "$MAPDOC" "FullyQualifiedName~MapWaterQueryTests|FullyQualifiedName~MapSpaceIntervalTests"`

- [ ] **Step 3: Implement intervals and the query**

`MapSpaceMembership` gains one internal method that runs the existing `QueryCore` and also returns the selected candidate's exact lower and upper heights and bound refs, so membership and intervals cannot drift. `MapSpaceIntervals.Column` evaluates every candidate footprint at the XZ without the Y filter and sorts by `Lower`. The query's constructor clones the set, compiles each body's domain once, and fixes the digest. `Sample` maps a membership failure to the same-named water status, `Outside` to `Dry`, then finds the bodies covering the resolved space id whose domain contains the XZ. None gives `Dry`, more than one gives `Ambiguous` with detail "ambiguous water bodies". For the one body, `top = min(FromSingle(SurfaceY), Upper)`, wet when `Lower <= y < top`, free when `Upper` is null or `FromSingle(SurfaceY) < Upper`, `UpperY` the surface when free and `Upper` otherwise. Floats come from `ToSingle`, and a wet sample whose float `LowerY >= UpperY` becomes `NotRepresentable` with detail "wet interval below float resolution". `Column` emits one wet sample per space interval whose body has water above its floor, with `Point.Y` set to that floor.

- [ ] **Step 4: Run green** `wa_test t3-green "$MAPDOC" "FullyQualifiedName~MapWaterQueryTests|FullyQualifiedName~MapSpaceIntervalTests"` (expect 14), then `wa_test t3-spaces "$MAPDOC" "FullyQualifiedName~Spaces|FullyQualifiedName~Support"` to show membership and support are unchanged.

- [ ] **Step 5: Format, guards and commit** `feat(mapdoc): sample contained water against occupied spaces`

---

### Task 4: Containment and closure validation

**Files:**
- Create: `KhaozEngine.MapDoc/Water/MapWaterContainment.cs`, `KhaozEngine.MapDoc/Water/MapWaterFootprints.cs`
- Modify: `KhaozEngine.MapDoc/MapBoundDocumentValidation.cs`, `KhaozEngine.MapDoc/Spaces/MapRelationApertures.cs` (an exact minimum aperture bottom)
- Test: `KhaozEngine.MapDoc.Tests/Water/MapWaterContainmentTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3, `MapSpaceFootprint`, `MapCavePortal`, `MapVerticalLink`, `MapRelationApertures.Compile`, `MapApertureGeometry.Columns` (exact `Bottom` points), `MapAssetClosure.GetResource`, `MapResourceKind.Material`, `MapBoundDocumentValidation.Validate` and `ValidateLocal`, the test type `FilteredSurfaceSource(IMapSurfaceSource inner, params MapPatchKey[] unloaded)`.
- Produces:
  - `public sealed record MapWaterContainmentReport(bool Complete, IReadOnlyList<string> Errors)`
  - `public static class MapWaterContainment` with `MapWaterContainmentReport Validate(MapScopedSurfaces view, MapWaterSet water)`.
  - `internal static MapExactValue BottomOf(MapPortalFact fact)` on `MapRelationApertures`, the exact minimum of the fact's column bottoms, never the rounded `Summary.BottomY`.
  - `internal sealed record MapWaterFootprintRect(string SpaceId, MapSpaceFootprint Footprint, MapLatticeFrame Frame, long CellMinX, long CellMinZ, long CellMaxX, long CellMaxZ, MapExactXz Min, MapExactXz Max, MapBoundRef Lower, MapBoundRef Upper)` and `internal static class MapWaterFootprints` with `IReadOnlyList<MapWaterFootprintRect> Of(MapScopedSurfaces view, string spaceId, out bool complete, out int cellsInspected)`. Task 5 compiles against these rectangles. `Footprint` is one representative record of the merged group, used for bound face reads.
  - `MapBoundDocumentValidation.Validate(document, assets)` additionally requires each body's `RenderMaterial` to resolve through `assets.GetResource` to a `Material` resource. `ValidateLocal` checks containment only, as it does not bind assets.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWaterContainmentTests
{
    static MapWaterContainmentReport Check(MapDocument doc) => MapWaterContainment.Validate(CaveFixtures.View(doc), doc.Water!);

    [Fact]
    public void SharedFixtures_Validate()
    {
        MapWaterContainmentReport ocean = Check(WaterFixtures.OceanOverCaves());
        Assert.Equal((true, 0), (ocean.Complete, ocean.Errors.Count));
        Assert.Empty(Check(WaterFixtures.SeaConnectedCave(portalBottomY: -5f)).Errors);
    }

    [Fact]
    public void SeaConnectedCave_IsOneBodyAcrossThePortal()
    {
        MapDocument doc = WaterFixtures.SeaConnectedCave(portalBottomY: -5f);
        MapWaterSample s = new MapWaterQuery(CaveFixtures.View(doc), doc.Water!).Sample(WaterFixtures.GrottoPoint(y: -1f));
        Assert.Equal((MapWaterStatus.Wet, "ocean", "grotto"), (s.Status, s.BodyId, s.SpaceId));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(null)]
    public void SeaConnectedCave_RefusesWhenTheConnectionIsDryOrAbsent(float? portalBottomY) =>
        Assert.Contains(Check(WaterFixtures.SeaConnectedCave(portalBottomY)).Errors,
            e => e.Contains("water body 'ocean': covered spaces are not connected below its surface", StringComparison.Ordinal));

    [Theory]
    [InlineData("domain-off-footprint", "water body 'ocean' covers no space footprint")]
    [InlineData("missing-cover", "water body 'ocean': cover 'nowhere' is not a space")]
    [InlineData("alias-cover", "water body 'ocean': cover 'sea-alias' is an alias")]
    public void BadCovers_Refuse(string variant, string message) =>
        Assert.Contains(Check(WaterFixtures.Broken(variant)).Errors, e => e.Contains(message, StringComparison.Ordinal));

    [Fact]
    public void IncompleteView_IsUnverified_NotValid()
    {
        MapWaterContainmentReport report = MapWaterContainment.Validate(WaterFixtures.ViewWithUnloaded("lake-floor"), WaterFixtures.OceanOverCaves().Water!);
        Assert.False(report.Complete);
        Assert.Contains(report.Errors, e => e.Contains("water body 'cave-lake': unverified", StringComparison.Ordinal));
    }

    [Fact]
    public void BoundValidation_RunsContainmentOnBoundedDocuments()
    {
        var error = Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.ValidateLocal(WaterFixtures.Broken("domain-off-footprint")));
        Assert.Contains("covers no space footprint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedRenderMaterial_Refuses()
    {
        MapBoundDocumentValidation.Validate(WaterFixtures.OceanOverCaves(), WaterFixtures.Assets());
        var error = Assert.Throws<MapDocumentException>(() =>
            MapBoundDocumentValidation.Validate(WaterFixtures.Broken("unknown-material"), WaterFixtures.Assets()));
        Assert.Contains("water body 'ocean': render material 'no-such-material' is not a Material resource", error.Message, StringComparison.Ordinal);
    }
}
```

`SeaConnectedCave(portalBottomY)` is an exterior space `sea` over a floor at -10 m for x 0 to 8 and a cave `grotto` for x 8 to 12 with floor -10 m and roof +5 m, joined at x 8 by a wall portal whose band runs from `portalBottomY` to +3 m. A null `portalBottomY` omits the portal and closes the wall. Body `ocean` has `SurfaceY` 0, a rectangle over x 0 to 12, and covers `sea` and `grotto`. `GrottoPoint(y)` is (10, y, -2). `ViewWithUnloaded(surfaceId)` acquires `OceanOverCaves()` through `FilteredSurfaceSource` over its stored source, with that surface's patch keys unloaded. `Broken("alias-cover")` adds space `sea-alias` with `AliasOf` pointing at `sea` and covers it. `Broken("unknown-material")` sets the ocean's `RenderMaterial` to `no-such-material`.

- [ ] **Step 2: Run red** `wa_test t4-red "$MAPDOC" "FullyQualifiedName~MapWaterContainmentTests"`

- [ ] **Step 3: Implement footprints, containment and the closure check**

`MapWaterFootprints.Of` gathers the space's acquired footprints, groups their slot cells by (lattice frame, lower bound, upper bound) and merges each group into maximal row-run rectangles in cell coordinates across slots, rows by ascending Z then X, the same greedy rule as `TileWaterBodies.Rectangles`. It converts each rectangle to an exact world rectangle through the frame, reports the slot cells it read, and clears `complete` when a footprint, its patch or its space record is unavailable. `Validate` checks, per body: every cover resolves to an acquired `MapSpaceDoc` that is not an alias. The domain meets at least one footprint rectangle of a covered space with positive area. When a body covers several spaces, they form one connected set through `MapCavePortal` and `MapVerticalLink` records joining covered spaces, counting only connections whose exact aperture bottom from `MapRelationApertures.BottomOf` is below `FromSingle(SurfaceY)`. A check that needs unavailable records adds "water body '{id}': unverified" and clears `Complete`. `MapBoundDocumentValidation.Validate` and `ValidateLocal` run containment over `MapScopedSurfaces.CompleteView(document.Surfaces)` when `Water` is not null and throw `MapDocumentException` with the first error, and `Validate` also runs the material check. A whole document always yields a complete view, so the builder and the editor never see `unverified`.

- [ ] **Step 4: Run green** `wa_test t4-green "$MAPDOC" "FullyQualifiedName~MapWaterContainmentTests"` (expect 10), then `wa_test t4-relations "$MAPDOC" "FullyQualifiedName~Relation"`.

- [ ] **Step 5: Format, guards and commit** `feat(mapdoc): validate water containment and its render material`

---

### Task 5: Region compiler

**Files:**
- Create: `KhaozEngine.MapDoc/Water/MapCompiledWaterBody.cs`, `KhaozEngine.MapDoc/Water/MapWaterCompiler.cs`, `KhaozEngine.MapDoc.Tests/Water/WaterOpticsScenes.cs`
- Modify: `KhaozEngine.MapDoc/Water/MapWaterPolygons.cs` (convex clipping against a convex polygon)
- Test: `KhaozEngine.MapDoc.Tests/Water/MapWaterCompilerTests.cs`, `KhaozEngine.MapDoc.Tests/Water/WaterOpticsSceneTests.cs`

**Interfaces:**
- Consumes: Task 2 kernel, triangulation, `ClipToRect` and `SplitAtHeight`, Task 4 `MapWaterFootprints`, `MapCommonRefinement.BoundFaces(MapScopedSurfaces view, MapSpaceFootprint footprint, MapBoundRef bound, MapExactXz min, MapExactXz max, MapRefinementLimits limits)` returning `MapBoundFaceSet(Status, Faces, Compatibility, Detail)`, `MapBoundFace`, `MapExactTriangle`, `MapBox3`.
- Produces:
  - `internal static IReadOnlyList<IReadOnlyList<MapWideXz>> ClipToConvex(IReadOnlyList<MapWideXz> subject, IReadOnlyList<MapWideXz> clip)` on `MapWaterPolygons`.
  - `public enum MapWaterRegionKind : byte { FreeSurface, Closed, Unresolved }`
  - `public sealed record MapWaterRegion(string SpaceId, MapWaterRegionKind Kind, IReadOnlyList<MapExactTriangle> Triangles)`. Every triangle lies at `Y = FromSingle(SurfaceY)` and faces up, meaning `cross(B - A, C - A).Y > 0`. Regions are closed point sets for drawing and certification, and a point's own classification always comes from `MapWaterQuery`, which closes a tie.
  - `public enum MapWaterCompileStatus { Complete, Incomplete, CapacityExceeded, NotRepresentable }`
  - `public sealed record MapWaterWork(int Pieces, int FootprintRects, int FootprintCells, int UpperFaces, int LowerFaces, int Triangles)`
  - `public sealed record MapWaterCompileLimits(int MaxFootprintCells = 4_194_304, int MaxBoundFaces = 1_048_576, int MaxTriangles = 1_048_576)`
  - `public sealed record MapCompiledWaterBody(string BodyId, float SurfaceY, MapWaterMediumDoc Medium, string RenderMaterial, IReadOnlyList<MapWaterRegion> Regions, float DeepestBedY, MapBox3 Bounds, MapWaterCompileStatus Status, string? Detail, MapWaterWork Work, string Digest)`
  - `public static class MapWaterCompiler` with `IReadOnlyList<MapCompiledWaterBody> Compile(MapScopedSurfaces view, MapWaterSet water, MapWaterCompileLimits? limits = null)`, bodies in id order, working on a deep copy of `water`.
  - `internal static class WaterOpticsScenes` with `MapDocument Build(string scene)` for the five DG9.5 scenes, which R8 links.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWaterCompilerTests
{
    static MapCompiledWaterBody Body(MapDocument doc, string id, MapWaterCompileLimits? limits = null) =>
        MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!, limits).Single(b => b.BodyId == id);
    static IEnumerable<MapExactTriangle> Of(MapCompiledWaterBody b, MapWaterRegionKind kind) =>
        b.Regions.Where(r => r.Kind == kind).SelectMany(r => r.Triangles);
    static HashSet<MapExactPoint> Points(IEnumerable<MapExactTriangle> t) => t.SelectMany(x => new[] { x.A, x.B, x.C }).ToHashSet();

    [Theory]
    [InlineData(512f)]
    [InlineData(4096f)]
    public void OceanSurface_IsTwoTriangles_AtAnySize(float sizeMetres)
    {
        MapCompiledWaterBody sea = Body(WaterFixtures.OpenSea(sizeMetres), "sea");
        int cells = (int)(sizeMetres / 8) * (int)(sizeMetres / 8);
        Assert.Equal((1, 1, cells, 0, 2), (sea.Work.Pieces, sea.Work.FootprintRects, sea.Work.FootprintCells, sea.Work.UpperFaces, sea.Work.Triangles));
        Assert.Equal(MapWaterRegionKind.FreeSurface, Assert.Single(sea.Regions).Kind);
        Assert.Equal(MapWaterCompileStatus.Complete, sea.Status);
    }

    [Fact]
    public void RiverOfManyRects_EmitsPerPiece()
    {
        MapCompiledWaterBody river = Body(WaterFixtures.ManyRectRiver(), "river");
        Assert.Equal((46, 1, 92), (river.Work.Pieces, river.Work.FootprintRects, river.Work.Triangles));
    }

    [Fact]
    public void CaveLake_SplitsAtTheCeilingContour()
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.OceanOverCaves(), "cave-lake");
        Assert.Equal(new MapExactValue(16, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.FreeSurface)));
        Assert.Equal(new MapExactValue(8, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Closed)));
        var seam = Points(Of(lake, MapWaterRegionKind.FreeSurface)).Intersect(Points(Of(lake, MapWaterRegionKind.Closed))).ToArray();
        Assert.All(seam, p => Assert.Equal(new MapExactValue(2, 1), p.X));
        Assert.Contains(seam, p => p.Z == new MapExactValue(-4, 1));
        Assert.Contains(seam, p => p.Z == new MapExactValue(0, 1));
    }

    [Fact]
    public void MixedLattices_SplitAtTheSameContour()
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.OceanOverCavesWithThirdMetreRoof(), "cave-lake");
        Assert.Equal(new MapExactValue(16, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.FreeSurface)));
        Assert.Equal(new MapExactValue(8, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Closed)));
    }

    [Fact]
    public void CeilingExactlyAtTheSurface_IsClosed()
    {
        MapCompiledWaterBody lake = Body(WaterFixtures.Broken("flat-roof-at-surface"), "cave-lake");
        Assert.Empty(Of(lake, MapWaterRegionKind.FreeSurface));
        Assert.Equal(new MapExactValue(24, 1), WaterFixtures.Area(Of(lake, MapWaterRegionKind.Closed)));
    }

    [Fact]
    public void FloodedCave_DrawsNoSurface()
    {
        MapCompiledWaterBody flooded = Body(WaterFixtures.OceanOverCaves(), "flooded");
        Assert.Empty(Of(flooded, MapWaterRegionKind.FreeSurface));
        Assert.Equal(new MapExactValue(8, 1), WaterFixtures.Area(Of(flooded, MapWaterRegionKind.Closed)));
    }

    [Fact]
    public void MissingCeilingGeometry_IsUnresolved()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapCompiledWaterBody lake = MapWaterCompiler.Compile(WaterFixtures.ViewWithUnloaded("lake-roof"), doc.Water!).Single(b => b.BodyId == "cave-lake");
        Assert.Equal(MapWaterCompileStatus.Incomplete, lake.Status);
        Assert.Contains(lake.Regions, r => r.Kind == MapWaterRegionKind.Unresolved);
    }

    [Fact]
    public void CompileLimits_RefuseOversizedWork()
    {
        MapCompiledWaterBody ocean = Body(WaterFixtures.OceanOverCaves(), "ocean", new MapWaterCompileLimits(MaxFootprintCells: 10));
        Assert.Equal((MapWaterCompileStatus.CapacityExceeded, "water footprint cells"), (ocean.Status, ocean.Detail));
        Assert.Empty(ocean.Regions);
    }

    [Fact]
    public void Compile_IsDeterministic_AndSurvivesTiledSaveReload()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string dir = WaterFixtures.TempDirectory();
        MapDocumentFile.SaveTiled(doc, dir);
        MapDocument loaded = MapDocumentFile.LoadTiled(dir);
        Assert.Equal(MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!).Select(b => b.Digest),
            MapWaterCompiler.Compile(CaveFixtures.View(loaded), loaded.Water!).Select(b => b.Digest));
    }

    [Fact]
    public void MediumDefinition_EntersTheDigest()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string before = Body(doc, "ocean").Digest;
        doc.Water!.Media[0] = doc.Water.Media[0] with { WadeSpeedScale = 0.5f };
        MapCompiledWaterBody after = Body(doc, "ocean");
        Assert.NotEqual(before, after.Digest);
        Assert.Equal(0.5f, after.Medium.WadeSpeedScale);
    }

    [Fact]
    public void DeepestBed_BoundsTheBody()
    {
        MapCompiledWaterBody ocean = Body(WaterFixtures.OceanOverCaves(), "ocean");
        Assert.Equal(-20f, ocean.DeepestBedY);
        Assert.Equal((-20d, 0d), (ocean.Bounds.MinY, ocean.Bounds.MaxY));
    }

    [Fact]
    public void FreeSurfaceTriangles_FaceUp()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        foreach (MapExactTriangle t in MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!).SelectMany(b => b.Regions).SelectMany(r => r.Triangles))
            Assert.True(WaterFixtures.NormalY(t).Sign > 0);
    }
}

public class WaterOpticsSceneTests
{
    [Theory]
    [InlineData("ledge", -2f)]
    [InlineData("river", -2f)]
    [InlineData("shelf", -100f)]
    [InlineData("cave-lake", -40f)]
    [InlineData("cross-region", -2f)]
    public void OpticsScenes_ValidateCompileAndReportTheirDepths(string scene, float deepestBed)
    {
        MapDocument doc = WaterOpticsScenes.Build(scene);
        Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
        Assert.Empty(MapWaterContainment.Validate(CaveFixtures.View(doc), doc.Water!).Errors);
        MapCompiledWaterBody[] bodies = MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!).ToArray();
        Assert.Equal(deepestBed, bodies.Min(b => b.DeepestBedY));
        Assert.Contains(bodies.SelectMany(b => b.Regions), r => r.Kind == MapWaterRegionKind.FreeSurface && r.Triangles.Count > 0);
    }
}
```

`OpenSea(size)` uses a lattice frame of 8 m cells and centimetre heights with rows toward negative Z, a flat floor at -10 m covering a square of the given size from the origin across as many slots as it needs, one exterior space `open` whose footprints cover every cell, and body `sea` with `SurfaceY` 0 and one rectangle over the whole square. `ManyRectRiver()` is a 64 m flat exterior floor at -2 m and body `river` with 46 one-row rectangles `(i, i, i + 8, i + 1)` for i from 0 to 45. `OceanOverCavesWithThirdMetreRoof()` builds `lake-roof` on a frame of one-third metre cells and 1/300 m height units with the same heights, so the footprint lattice and the ceiling lattice differ. `Broken("flat-roof-at-surface")` sets every `lake-roof` corner to -34 m. `Area` sums exact triangle areas and `NormalY` returns the exact `cross(B - A, C - A).Y`.

The five DG9.5 scenes: `ledge` is a floor at -0.3 m for x 0 to 4 beside -2 m for x 4 to 8. `river` is a 2 m deep channel with 0 m banks. `shelf` uses 8 m cells, a -30 m shelf for x 0 to 256 and -100 m ocean for x 256 to 512. `cross-region` is one body crossing the slot seam at x 64 over a -2 m floor. Those four have `SurfaceY` 0 over an exterior floor. `cave-lake` is `OceanOverCaves()`'s cave lake alone, a cave with an enclosing roof, at its own `SurfaceY` -34.

- [ ] **Step 2: Run red** `wa_test t5-red "$MAPDOC" "FullyQualifiedName~MapWaterCompilerTests|FullyQualifiedName~WaterOpticsSceneTests"`

- [ ] **Step 3: Implement the compiler**

For each body: refuse with `CapacityExceeded` and detail `water footprint cells` when the covered footprints hold more cells than the limit, before reading geometry. Compile the domain (Task 2), triangulate each piece, and clip every triangle against every footprint rectangle of every covered space. A rectangle whose upper bound is `OpenTop` gives `FreeSurface`. For a ceiling or a horizontal opening, read the upper bound's canonical faces with `MapCommonRefinement.BoundFaces` over the clipped area, one storage slot at a time so each read stays within the default `MapRefinementLimits`. Clip each piece against each face's XZ projection with `ClipToConvex`, lift the result onto the face plane, and split it with `SplitAtHeight` at `FromSingle(SurfaceY)`. The strictly-above part is `FreeSurface` and the rest is `Closed`, so the split follows the exact contour on any lattice. A read that is not `Complete` yields an `Unresolved` region over that area and the status `Incomplete`, and any piece area outside the view scope does the same. The lower bound never clips the surface, so terrain above the water occludes it as in TileWorld. `DeepestBedY` reads the lower bound's faces the same slot-by-slot way and takes the `ToSingle` of the exact minimum vertex height over the clipped area, using legacy corner heights for compatibility-tagged cells. Counters record every footprint cell, upper and lower face and emitted triangle, and crossing `MaxBoundFaces` or `MaxTriangles` refuses with `CapacityExceeded`. A value that cannot narrow gives `NotRepresentable`. `Bounds` spans the region triangles in XZ and `[DeepestBedY, SurfaceY]` in Y. `Digest` is the canonical SHA-256 of `kemap/water-body/1`, `MapWaterSet.BodyDigest`, `MapWaterSet.MediumDigest` of its medium, the view's `Identity.Digest`, and the regions in order with exact coordinates as strings.

- [ ] **Step 4: Run green** `wa_test t5-green "$MAPDOC" "FullyQualifiedName~MapWaterCompilerTests|FullyQualifiedName~WaterOpticsSceneTests"` (expect 18)

- [ ] **Step 5: Format, guards and commit** `feat(mapdoc): compile free and closed water regions`

---

### Task 6: Built world, residency, nav identity and invalidation

**Files:**
- Modify: `KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs`, `KhaozEngine.MapDoc.Physics/MapBuiltWorld.cs`, `MapWorldBuilder.cs`, `MapResidencyOwnership.cs`, `MapNavTiling.cs`, `README.md`, `KhaozEngine.MapDoc.Physics.Tests/NativeWorldFixtures.World.cs` (`Build(NativeFixture)` becomes internal)
- Create: `KhaozEngine.MapDoc.Physics.Tests/NativeWorldFixtures.Water.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapWaterWorldTests.cs`, `KhaozEngine.MapDoc.Tests/Editing/MapWaterEffectsTests.cs`

**Interfaces:**
- Consumes: Tasks 4 and 5, `MapWorldBuilder.Build`, `MapResidencyOwnership.Build` and `Affected`, `MapNavTiling.Partition` and `AffectedTiles`, `MapWorldGrids`, `NativeWorldFixtures.Options`, `FloorSurfaces`, `OnFloor`, `Resolve`.
- Produces:
  - `MapNativeInvalidation.Medium = 128`, and on `MapNativeEditEffects` the init-only `public IReadOnlyList<string> WaterBodyIds { get; init; } = Array.Empty<string>();`. `Describe()` writes `waterBodyIds` after `dependencyIds`, ordinal, only when non-empty, so every existing description is byte-identical.
  - On `MapBuiltWorld`: `IReadOnlyList<MapCompiledWaterBody> Water` (empty without a section), `IReadOnlyList<MapWaterMediumDoc> WaterMedia` (the section's media in key order), `string WaterDigest` (empty without a section, else `MapWaterSet.Digest()`), `float? LegacyWaterLevel` (`Terrain.WaterLevel` for a resolver-1 world, null for a native one).
  - `public static IReadOnlyList<MapResidencyEntry> BuildWater(MapBuiltWorld world, MapWorldGrids grids)` on `MapResidencyOwnership`.
  - On `MapNavTile` the init-only `public string MediumDigest { get; init; } = "";`.
  - Fixtures in `NativeWorldFixtures.Water.cs`: `internal sealed record WaterWorldFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved, MapScopedSurfaces View) : NativeFixture(Document, Assets, Resolved, View)` with `MapFramePoint Point(float x, float y, float z)`. `internal static WaterWorldFixture BridgeOverRiver()`, `BuildBridgeOverRiver()`, `LRiver(float wadeSpeedScale = 1f)`, `BuildLRiver()`, `BuildDryTwinOfLRiver()`, `internal static WaterWorldFixture Reresolve(WaterWorldFixture fixture)`, `internal static MapNativeEditEffects RiverLevelEditEffects()` and `internal static MapStaticOwner DownwardHit(MapPhysicsRegistration registration, IPhysicsQueryLease lease, Vector3 from)`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWaterWorldTests
{
    static readonly MapWorldGrids Grids = new(64f, 2, 4, Vector2.Zero);
    static readonly MapNavTileOptions Nav = new(0.5f, "test-profile", "test-controller");
    static readonly MapNavTileCoord[] Wet = { new(0, -1), new(0, 0) };

    [Fact]
    public void Water_IsNeverAPhysicsStatic()
    {
        MapBuiltWorld world = NativeWorldFixtures.BuildBridgeOverRiver();
        Assert.DoesNotContain(world.Statics, s => s.OwnerId == "river");
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Assert.Equal(world.Statics.Count, registration.Handles.Count);
        Assert.Equal(MapStaticKind.TerrainChunk, NativeWorldFixtures.DownwardHit(registration, lease, new Vector3(-3f, 10f, 6f)).Kind);
    }

    [Fact]
    public void BuildHash_FollowsTheWater()
    {
        WaterWorldFixture fixture = NativeWorldFixtures.BridgeOverRiver();
        MapBuiltWorld a = NativeWorldFixtures.Build(fixture), b = NativeWorldFixtures.Build(fixture);
        Assert.Equal((a.BuildHash, a.WaterDigest), (b.BuildHash, b.WaterDigest));
        fixture.Document.Water!.Bodies[0] = fixture.Document.Water.Bodies[0] with { SurfaceY = -0.5f };
        MapBuiltWorld c = NativeWorldFixtures.Build(NativeWorldFixtures.Reresolve(fixture));
        Assert.NotEqual(a.BuildHash, c.BuildHash);
        Assert.NotEqual(a.WaterDigest, c.WaterDigest);
        MapBuiltWorld dry = NativeWorldFixtures.BuildBridge();
        Assert.Equal(("", 0), (dry.WaterDigest, dry.Water.Count));
    }

    [Fact]
    public void WaterResidency_FollowsTheWetRegions()
    {
        MapResidencyEntry river = Assert.Single(MapResidencyOwnership.BuildWater(NativeWorldFixtures.BuildLRiver(), Grids));
        Assert.Equal(new[] { new MapTileCoord(0, -1), new MapTileCoord(0, 0), new MapTileCoord(1, 0) },
            river.Membership.OrderBy(t => t.X).ThenBy(t => t.Z));
    }

    [Fact]
    public void NavTiles_CarryAMediumDigestOnlyWhereWaterMeetsThem()
    {
        var wet = MapNavTiling.Partition(NativeWorldFixtures.BuildLRiver(), Grids, Nav).ToDictionary(t => t.Coord);
        var dry = MapNavTiling.Partition(NativeWorldFixtures.BuildDryTwinOfLRiver(), Grids, Nav).ToDictionary(t => t.Coord);
        Assert.Equal(dry.Keys.OrderBy(c => c.X).ThenBy(c => c.Z), wet.Keys.OrderBy(c => c.X).ThenBy(c => c.Z));
        Assert.Equal(Wet, wet.Values.Where(t => t.MediumDigest.Length > 0).Select(t => t.Coord).OrderBy(c => c.X).ThenBy(c => c.Z));
        foreach (var (coord, tile) in wet)
        {
            Assert.Equal(dry[coord].GeometryDigest, tile.GeometryDigest);
            Assert.Equal(!Wet.Contains(coord), dry[coord].CaptureIdentity == tile.CaptureIdentity);
        }
    }

    [Fact]
    public void MediumEdit_ChangesWetTileIdentityOnly()
    {
        var before = MapNavTiling.Partition(NativeWorldFixtures.BuildLRiver(), Grids, Nav).ToDictionary(t => t.Coord);
        var after = MapNavTiling.Partition(NativeWorldFixtures.Build(NativeWorldFixtures.LRiver(wadeSpeedScale: 0.5f)), Grids, Nav).ToDictionary(t => t.Coord);
        foreach (var (coord, tile) in before)
            Assert.Equal(!Wet.Contains(coord), after[coord].CaptureIdentity == tile.CaptureIdentity);
    }

    [Fact]
    public void WaterEdits_InvalidateExactlyTheirTiles()
    {
        MapBuiltWorld world = NativeWorldFixtures.BuildLRiver();
        MapNativeEditEffects effects = NativeWorldFixtures.RiverLevelEditEffects();
        Assert.Contains("river", MapResidencyOwnership.Affected(world, Grids, effects).Owners);
        Assert.Equal(Wet, MapNavTiling.AffectedTiles(world, Grids, Nav, effects).OrderBy(c => c.X).ThenBy(c => c.Z));
    }
}

public class MapWaterEffectsTests
{
    [Fact]
    public void Describe_AddsWaterOnlyWhenPresent()
    {
        var plain = new MapNativeEditEffects(null, null, Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<MapDigestChange>(), MapNativeInvalidation.Terrain);
        Assert.Equal("{\"oldBounds\":null,\"newBounds\":null,\"patches\":[],\"spaceIds\":[],\"dependencyIds\":[],\"digestChanges\":[],\"invalidates\":1}",
            plain.Describe());
        string water = (plain with { WaterBodyIds = new[] { "b", "a" }, Invalidates = MapNativeInvalidation.Medium }).Describe();
        Assert.Contains("\"dependencyIds\":[],\"waterBodyIds\":[\"a\",\"b\"],\"digestChanges\"", water, StringComparison.Ordinal);
        Assert.Contains("\"invalidates\":128", water, StringComparison.Ordinal);
    }
}
```

`BridgeOverRiver()` keeps `BuildBridge()`'s deck and parapets at their explicit heights over a new floor surface from x -8 to 8 and z -8 to 8 whose channel x -4 to 4 sits at -1.3 m with banks at 0 m beyond x 5 and linear slopes between. It adds an exterior space `bank-exterior` with one footprint per floor patch over every cell, lower the floor and upper `OpenTop`, medium `river-water`, material resource `river-material`, and body `river` with `SurfaceY` -0.37, one rectangle over the channel and cover `bank-exterior`, then reseals digests and resolves. `LRiver(wadeSpeedScale)` is a document with `TileSize` 64, storage and playable bounds x 0 to 256 and z -64 to 64, a flat floor at -2 m with an exterior space over every cell, medium `river-water` with the given scale, and body `river` made of the world rectangles x 8 to 24 by z -48 to 48 and x 24 to 104 by z 8 to 24. It meets storage tiles (0, -1), (0, 0) and (1, 0) and navigation tiles (0, -1) and (0, 0), while navigation tiles (1, -1) and (1, 0) stay dry. `BuildDryTwinOfLRiver()` is the same document with an empty bounded water set. `RiverLevelEditEffects()` is the effects of raising `river` by 0.1 m with `WaterBodyIds` ["river"], both bounds the river's XZ at its surface, and `Invalidates` `Medium | Residency | Nav`. `DownwardHit` casts a ray straight down under the lease and returns the hit's `MapStaticOwner`.

- [ ] **Step 2: Run red** `wa_test t6-red "$MP" "FullyQualifiedName~MapWaterWorldTests"` and `wa_test t6-red-doc "$MAPDOC" "FullyQualifiedName~MapWaterEffectsTests"`

- [ ] **Step 3: Implement water in the built world**

The builder keeps its whole-document contract. For a native document with a water section, the resolver's authored identity already ran `MapBoundDocumentValidation`, so containment and the material closure are verified. It compiles with `MapWaterCompiler` over its complete view and throws `MapDocumentException` with "water: " and the detail when any body's status is not `Complete`. `BuildHash` gains `waterDigest` and a `water` array of `[bodyId, digest]` pairs after `terrain`, written only when the section is present. `BuildWater` gives one entry per compiled body, owner id the body id, membership the storage tiles any region triangle's XZ bounds reaches under the existing minimum-inclusive rule, storage owner the tile holding the body's minimum bounds corner, nav tiles and cells derived from the membership tiles. `Affected` treats `Medium` like `Residency`, adds `WaterBodyIds` to its owners and their current and reported bounds to its tiles. `Partition` places each water body in every capture tile any of its region triangles meets, `MediumDigest` is the canonical SHA-256 of `kemap/nav-tile-medium/1` over the sorted `[bodyId, digest]` pairs of the bodies meeting the tile, and `CaptureIdentity` writes `mediumDigest` after `geometryDigest` only when it is non-empty. `AffectedTiles` adds the tiles the reported water bounds meet when `Medium` is set. The README replaces the deferred row "Water medium and walker profiles" with a short Water section: water is built-world data and never a static, its media travel with the built world, nav identity carries a medium digest, and movement in water arrives with #438 phase 4.

- [ ] **Step 4: Run green** `wa_test t6-green "$MP" "FullyQualifiedName~MapWaterWorldTests"` (expect 6) and `wa_test t6-green-doc "$MAPDOC" "FullyQualifiedName~MapWaterEffectsTests"` (expect 1), then `wa_test t6-physics "$MP" "FullyQualifiedName~Residency|FullyQualifiedName~NavTiling|FullyQualifiedName~WorldBuilder"`.

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): carry bounded water as built-world data`

---

### Task 7: Legacy medium and the interface proof

**Files:**
- Modify: `KhaozEngine.MapDoc.Physics/MapPhysicsRegistration.cs`, `KhaozEngine.MapDoc.Physics.Tests/NativeWorldFixtures.Water.cs`
- Test: `KhaozEngine.MapDoc.Physics.Tests/MapWaterMovementInputsTests.cs`

**Interfaces:**
- Consumes: Task 6 (`WaterWorldFixture`, `BridgeOverRiver`, `Build`, `DownwardHit`), `GroundMoveContext`, `MovementMedium`, `IPhysicsQueryLeaseSource.AcquireQueryReadLease`, `NativeWorldFixtures.BuildLegacyStrip` patterns.
- Produces: `public GroundMoveContext CreateLegacyMoveContext(bool legacyGlobalWater)` on `MapPhysicsRegistration`. The parameterless overload keeps its released behaviour and calls it with false. With true, the medium is `feetY < level ? new MovementMedium(level, true) : MovementMedium.Dry` for `level = World.LegacyWaterLevel`. A native world still throws, as the parameterless overload does.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWaterMovementInputsTests
{
    [Fact]
    public void LegacyContext_WadesBelowTheGlobalLevelOnlyWhenAsked()
    {
        MapBuiltWorld world = NativeWorldFixtures.BuildLegacyStripWithWaterLevel(0.5f);
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        Assert.Null(registration.CreateLegacyMoveContext().Medium);
        var medium = registration.CreateLegacyMoveContext(legacyGlobalWater: true).Medium!;
        Assert.Equal(new MovementMedium(0.5f, true), medium(1f, 1f, 0.2f));
        Assert.Equal(MovementMedium.Dry, medium(1f, 1f, 0.5f));
        Assert.Equal(0.5f, world.LegacyWaterLevel);
    }

    [Fact]
    public void NativeWorld_RefusesTheLegacyMedium()
    {
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildBridgeOverRiver(), physics);
        Assert.Throws<MapDocumentException>(() => registration.CreateLegacyMoveContext(legacyGlobalWater: true));
    }

    [Fact]
    public void WalkerRuleInputs_AreAvailableUnderTheReleasedLease()
    {
        WaterWorldFixture fixture = NativeWorldFixtures.BridgeOverRiver();
        MapBuiltWorld world = NativeWorldFixtures.Build(fixture);
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var water = new MapWaterQuery(world.Surfaces, fixture.Document.Water!);

        Assert.Equal(MapWaterStatus.Dry, water.Sample(fixture.Point(0f, 2.825f, 0f)).Status);
        Assert.Equal("bridge-deck", NativeWorldFixtures.DownwardHit(registration, lease, new Vector3(0f, 10f, 0f)).OwnerId);
        MapWaterSample wading = water.Sample(fixture.Point(-3f, -1.2f, 6f));
        Assert.Equal((MapWaterStatus.Wet, "river", -1.3f, -0.37f), (wading.Status, wading.BodyId, wading.LowerY, wading.NominalSurfaceY));
        Assert.Equal(MapWaterStatus.Dry, water.Sample(fixture.Point(-3f, -0.37f, 6f)).Status);
        Assert.Equal(world.Surfaces.ReadWitness.ScopedDigest, wading.Witness.ScopedDigest);
        Assert.Equal(world.WaterDigest, wading.WaterDigest);
    }
}
```

`BuildLegacyStripWithWaterLevel(level)` is the legacy strip fixture with `Terrain.WaterLevel` set and no water section. The interface proof stands in for the walker profile in R4's exit proof: it shows the depth and the deck facts the #438 phase 4 walker rule needs come from one acquisition, the released lease and the built world, with no type from `feature/swimming-contract`.

- [ ] **Step 2: Run red** `wa_test t7-red "$MP" "FullyQualifiedName~MapWaterMovementInputsTests"`

- [ ] **Step 3: Implement the opt-in legacy medium**

The builder sets `LegacyWaterLevel` from `document.Terrain.WaterLevel` for resolver-1 documents. `CreateLegacyMoveContext(bool)` passes the medium delegate only when asked and keeps the playable clamp and every other argument as released.

- [ ] **Step 4: Run green** `wa_test t7-green "$MP" "FullyQualifiedName~MapWaterMovementInputsTests"` (expect 3)

- [ ] **Step 5: Format, guards and commit** `feat(mapdocphysics): offer legacy global water and prove walker inputs`

---


### Task 8: Water edits and editor commands

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapWaterEdits.cs`, `KhaozEngine.MapEditor/NativeWaterCommands.cs`
- Modify: `KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs`, `KhaozEngine.MapEditor/NativeDocumentSnapshot.cs` (`Clone`, `Publish` and `PublishWriteSet`), `KhaozEngine.MapEditor/NativeTerrainCommands.cs`, `KhaozEngine.MapEditor/ViewportWorld.cs`
- Test: `KhaozEngine.MapDoc.Tests/Editing/MapWaterEditTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/NativeWaterCommandTests.cs`. The editor test project's existing `../KhaozEngine.MapDoc.Tests/**/*Fixtures.cs` glob already compiles `WaterFixtures.cs` and its dependencies, so its project file does not change.

**Interfaces:**
- Consumes: Tasks 1 to 6, `INativeDocumentCommand`, `NativeDocumentTransaction.Run`, `NativeDocumentPreparation`, `TerrainEditCommand`, `MapTerrainEdits.Prepare`, `MapSetCornerHeights`, `WaterFixtures.LowerSeabedAt`.
- Produces:
  - `public abstract record MapWaterEdit(string Label)` with `MapUpsertWaterBody(MapWaterBodyDoc Body)`, `MapRemoveWaterBody(string BodyId)`, `MapUpsertWaterMedium(MapWaterMediumDoc Medium)`, `MapRemoveWaterMedium(string Key)` and `MapSetBoundedWater(bool Bounded)`, all sealed records.
  - `public sealed record MapWaterEditResult(MapWaterSet? After, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects)`.
  - `public static class MapWaterEdits` with `MapWaterEditResult Prepare(MapDocument document, MapWaterEdit edit)`, pure, and `MapNativeEditEffects NameAffectedBodies(MapWaterSet? water, MapNativeEditEffects effects)`.
  - On `MapNativeWriteSet` the init-only `public bool Water { get; init; }`.
  - `public sealed class WaterEditCommand : EditorCommand, INativeDocumentCommand` with `WaterEditCommand(MapWaterEdit edit)`.
  - `internal static float? LegacyWaterLevel(MapDocument doc)` on `ViewportWorld`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MapWaterEditTests
{
    [Fact]
    public void Upsert_ReportsBodyBoundsAndMediumInvalidation_AndStaysPure()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapWaterBodyDoc raised = doc.Water!.Bodies.Single(b => b.Id == "cave-lake") with { SurfaceY = -33f };
        MapWaterEditResult r = MapWaterEdits.Prepare(doc, new MapUpsertWaterBody(raised));
        Assert.Equal(new[] { "cave-lake" }, r.Effects.WaterBodyIds);
        Assert.Equal(new[] { "lake-cave" }, r.Effects.SpaceIds);
        Assert.True(r.Effects.Invalidates.HasFlag(MapNativeInvalidation.Medium));
        Assert.Equal((-34d, -33d), (r.Effects.OldBounds!.Value.MaxY, r.Effects.NewBounds!.Value.MaxY));
        Assert.True(r.WriteSet.Water);
        Assert.Equal(-34f, doc.Water.Bodies.Single(b => b.Id == "cave-lake").SurfaceY);
    }

    [Fact]
    public void Remove_ReportsNoNewBounds()
    {
        MapWaterEditResult r = MapWaterEdits.Prepare(WaterFixtures.OceanOverCaves(), new MapRemoveWaterBody("flooded"));
        Assert.DoesNotContain(r.After!.Bodies, b => b.Id == "flooded");
        Assert.Null(r.Effects.NewBounds);
    }

    [Theory]
    [InlineData("remove-used-medium", "water medium 'sea-water' is used by 'cave-lake'")]
    [InlineData("legacy-with-bodies", "remove every water body before using legacy global water")]
    [InlineData("remove-unknown-body", "no water body 'nowhere'")]
    [InlineData("upsert-without-section", "bounded water is off")]
    [InlineData("bounded-on-resolver-one", "bounded water requires a resolver-2 native document")]
    public void InvalidEdits_Refuse(string variant, string message) =>
        Assert.Contains(message, Assert.Throws<MapDocumentException>(() => WaterFixtures.ApplyEdit(variant)).Message, StringComparison.Ordinal);

    [Fact]
    public void NameAffectedBodies_UsesCoveredSpaces()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapNativeEditEffects terrain = MapTerrainEdits.Prepare(doc.Surfaces, WaterFixtures.LowerSeabedAt(2, -2, metres: 1f)).Effects;
        MapNativeEditEffects named = MapWaterEdits.NameAffectedBodies(doc.Water, terrain);
        Assert.Contains("ocean", named.WaterBodyIds);
        Assert.DoesNotContain("cave-lake", named.WaterBodyIds);
        Assert.True(named.Invalidates.HasFlag(MapNativeInvalidation.Medium));
    }
}

public sealed class NativeWaterCommandTests
{
    [Fact]
    public void Snapshot_PreservesWater()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapDocument clone = NativeDocumentSnapshot.Clone(doc, MapDocRegistry.CreateDefault());
        Assert.Equal(doc.Water!.Digest(), clone.Water!.Digest());
        Assert.NotSame(doc.Water, clone.Water);
        Assert.NotSame(doc.Water.Bodies, clone.Water.Bodies);
    }

    [Fact]
    public void WaterCommand_UndoRedoRestoresTheExactSet()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string before = doc.Water!.Digest();
        var command = new WaterEditCommand(new MapUpsertWaterBody(doc.Water.Bodies.Single(b => b.Id == "cave-lake") with { SurfaceY = -33f }));
        command.Apply(doc);
        Assert.Equal(-33f, doc.Water!.Bodies.Single(b => b.Id == "cave-lake").SurfaceY);
        command.Revert(doc);
        Assert.Equal(before, doc.Water!.Digest());
        command.Apply(doc);
        Assert.Equal(-33f, doc.Water!.Bodies.Single(b => b.Id == "cave-lake").SurfaceY);
    }

    [Fact]
    public void RejectedEdit_LeavesTheDocumentUntouched()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string before = MapDocumentFile.SaveText(doc);
        var overlapping = new WaterEditCommand(new MapUpsertWaterBody(WaterFixtures.SecondLakeOverlappingCaveLake()));
        Assert.Throws<MapDocumentException>(() => overlapping.Apply(doc));
        Assert.Equal(before, MapDocumentFile.SaveText(doc));
    }

    [Fact]
    public void TerrainEditUnderABody_NamesTheBody_AndKeepsItsLevel()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string water = doc.Water!.Digest();
        MapNativeEditEffects effects = NativeDocumentTransaction.Run(doc, new TerrainEditCommand(WaterFixtures.LowerSeabedAt(2, -2, metres: 1f)), false, null, localOnly: true);
        Assert.Contains("ocean", effects.WaterBodyIds);
        Assert.True(effects.Invalidates.HasFlag(MapNativeInvalidation.Medium));
        Assert.Equal(water, doc.Water!.Digest());
    }

    [Fact]
    public void UnrelatedTransactions_KeepTheExactWaterSet()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string water = doc.Water!.Digest();
        NativeDocumentTransaction.Run(doc, new TerrainEditCommand(WaterFixtures.LowerSeabedAt(11, -2, metres: 0.5f)), false, null, localOnly: true);
        Assert.Equal(water, doc.Water!.Digest());
        NativeDocumentTransaction.Run(doc, WaterFixtures.AddMarkerPlacementCommand(), false, null, localOnly: true);
        Assert.Equal(water, doc.Water!.Digest());
    }

    [Fact]
    public void BoundedDocument_HidesTheLegacyPlane()
    {
        MapDocument bounded = WaterFixtures.OceanOverCaves();
        Assert.Null(ViewportWorld.LegacyWaterLevel(bounded));
        bounded.Water = null;
        bounded.Terrain.WaterLevel = 1.5f;
        Assert.Equal(1.5f, ViewportWorld.LegacyWaterLevel(bounded));
    }

    [Fact]
    public void SaveReload_AfterWaterEdits_IsByteStable()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        new WaterEditCommand(new MapRemoveWaterBody("flooded")).Apply(doc);
        string text = MapDocumentFile.SaveText(doc);
        Assert.Equal(text, MapDocumentFile.SaveText(MapDocumentFile.LoadText(text)));
    }
}
```

`ApplyEdit(variant)` runs `MapWaterEdits.Prepare` on `OceanOverCaves()`, on a copy without a water section for `upsert-without-section`, or `MapSetBoundedWater(true)` on a native resolver-1 copy for `bounded-on-resolver-one`. `SecondLakeOverlappingCaveLake()` is the `second-lake` body from Task 2. `AddMarkerPlacementCommand()` is the smallest native placement command the existing placement transaction tests use, placing a non-solid marker on the seabed at x 11.

- [ ] **Step 2: Run red** `wa_test t8-red-doc "$MAPDOC" "FullyQualifiedName~MapWaterEditTests"` and `wa_test t8-red "$EDITOR" "FullyQualifiedName~NativeWaterCommandTests"`

- [ ] **Step 3: Implement edits and commands**

`Prepare` never mutates its input. An upsert replaces the body with the same id or appends it. A body edit reports old and new bounds as the domain's XZ bounds at the old and new `SurfaceY`, `WaterBodyIds` the body id, `SpaceIds` the union of old and new cover ids, a `water:{id}` digest change from `MapWaterSet.BodyDigest`, and `Medium | Residency | Nav`, plus `Material` when the body is added, removed or changes `RenderMaterial`. A medium edit names every body that uses the key. Switching to bounded water creates an empty section and requires a resolver-2 native document, switching back requires no bodies, and both report `Medium | Unbounded`. The write set is empty except `Water = true`. `NameAffectedBodies` adds every body whose covers meet `effects.SpaceIds` and sets `Medium`.

`NativeDocumentSnapshot.Clone` copies `Water.Clone()` onto the clone after its round trip, `Publish` copies the candidate's water for a whole-document publication, and `PublishWriteSet` copies it when the write set has `Water`. Every copy is deep. `WaterEditCommand` mirrors `TerrainEditCommand`: `Apply` and `Revert` run `NativeDocumentTransaction.Run` with `localOnly: true`, and `Prepare` keeps the set before and after the first application so undo restores it exactly, with old and new bounds swapped. `TerrainEditCommand` passes its effects through `NameAffectedBodies` with the candidate's water. `ViewportWorld` draws the camera-centred plane only when `LegacyWaterLevel` returns a value.

- [ ] **Step 4: Run green** `wa_test t8-green-doc "$MAPDOC" "FullyQualifiedName~MapWaterEditTests"` (expect 8) and `wa_test t8-green "$EDITOR" "FullyQualifiedName~NativeWaterCommandTests"` (expect 7), then `wa_test t8-editor "$EDITOR" "FullyQualifiedName~NativeDocumentTransaction|FullyQualifiedName~Terrain|FullyQualifiedName~Placement"`.

- [ ] **Step 5: Format, guards and commit** `feat(mapeditor): edit bounded water through native transactions`

---

### Task 9: Tool services

**Files:**
- Create: `KhaozEngine.MapEdit.Tool/MutationService.Water.cs`, `KhaozEngine.MapEdit.Tool/WaterService.cs`
- Modify: `KhaozEngine.MapEdit.Tool/Tools/McpBootstrap.cs` (`AddMapEditServices` registers `WaterService`)
- Test: `KhaozEngine.MapEditor.Tests/MapEditTool/WaterServiceTests.cs`

**Interfaces:**
- Consumes: Task 8, `MapEditSession.Mutate`, `session.ApplyNative`, `MutationResult`, `TerrainApply` as the pattern.
- Produces:
  - `public sealed record MapWaterMutationResult(MutationResult Mutation, MapNativeEditEffects Effects)` and `public MapWaterMutationResult WaterApply(MapWaterEdit edit)` on `MutationService`, verb `water_apply`, refusing any document without resolver-2 identity with "Water edits require a resolver-2 native document.".
  - `public sealed class WaterService(MapEditSession session)` with `IReadOnlyList<MapWaterBodyDoc> List()`, `MapWaterBodyDoc Get(string id)` and `MapWaterSampleInfo Sample(float x, float y, float z)`.
  - `public sealed record MapWaterSampleInfo(MapWaterStatus Status, string? BodyId, string? SpaceId, float LowerY, float UpperY, float NominalSurfaceY, bool UpperIsFreeSurface, string? Detail)`.
  - No MCP tool registration. R10 adds wire schemas for terrain and water together.

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class WaterServiceTests
{
    [Fact]
    public void WaterApply_MatchesTheGuiCommand()
    {
        var edit = new MapUpsertWaterBody(WaterFixtures.OceanOverCaves().Water!.Bodies.Single(b => b.Id == "cave-lake") with { SurfaceY = -33f });
        MapEditSession session = WaterToolFixtures.Session(WaterFixtures.OceanOverCaves());
        MapWaterMutationResult service = new MutationService(session).WaterApply(edit);
        MapDocument gui = WaterFixtures.OceanOverCaves();
        MapNativeEditEffects guiEffects = NativeDocumentTransaction.Run(gui, new WaterEditCommand(edit), false, null, localOnly: true);
        Assert.Equal(guiEffects.Describe(), service.Effects.Describe());
        Assert.Equal(gui.Water!.Digest(), WaterToolFixtures.Document(session).Water!.Digest());
    }

    [Fact]
    public void Sample_ReportsTheSameFactsAsTheQuery()
    {
        MapWaterSampleInfo s = new WaterService(WaterToolFixtures.Session(WaterFixtures.OceanOverCaves())).Sample(5f, -35f, -2f);
        Assert.Equal((MapWaterStatus.Wet, "cave-lake", -34f, true), (s.Status, s.BodyId, s.UpperY, s.UpperIsFreeSurface));
    }

    [Fact]
    public void ListAndGet_ReturnTheStoredBodies()
    {
        var water = new WaterService(WaterToolFixtures.Session(WaterFixtures.OceanOverCaves()));
        Assert.Equal(new[] { "cave-lake", "flooded", "ocean" }, water.List().Select(b => b.Id));
        Assert.Equal(-20f, water.Get("flooded").SurfaceY);
    }

    [Fact]
    public void LegacyDocument_RefusesWaterEdits()
    {
        var mutation = new MutationService(WaterToolFixtures.Session(WaterToolFixtures.LegacyAnalytic()));
        var error = Assert.Throws<MapDocumentException>(() => mutation.WaterApply(new MapSetBoundedWater(true)));
        Assert.Equal("Water edits require a resolver-2 native document.", error.Message);
    }
}
```

`WaterToolFixtures` opens a `MapEditSession` over a document the way the existing `MutationService` tests do, and reads its current document back.

- [ ] **Step 2: Run red** `wa_test t9-red "$EDITOR" "FullyQualifiedName~WaterServiceTests"`

- [ ] **Step 3: Implement the services**

`WaterApply` copies `TerrainApply`, checking for payload 1, resolver 2 and `AuthoredBindingsV2` instead of a non-null identity. `WaterService` reads the session's document under the session's lock the way `QueryService` reads, converts world metres to a `MapFramePoint` in the complete view's frame, and builds `MapWaterQuery` over `MapScopedSurfaces.CompleteView(document.Surfaces)`. A document without a water section lists nothing and samples `Dry`.

- [ ] **Step 4: Run green** `wa_test t9-green "$EDITOR" "FullyQualifiedName~WaterServiceTests"` (expect 4)

- [ ] **Step 5: Format, guards and commit** `feat(mapedit): add typed water services`

---


### Task 10: Synthetic legacy oracle

**Files:**
- Create: `KhaozEngine.MapDoc.Compatibility.Tests/LegacyWaterOracleWorld.cs`, `LegacyWaterConverter.cs`, `LegacyWaterComparison.cs`, `LegacyWaterOracleTests.cs`
- Test: `KhaozEngine.MapDoc.Compatibility.Tests/LegacyWaterOracleTests.cs`

**Interfaces:**
- Consumes: `TileWaterBodies.Collect`, `TileWaterBody`, `TileRect`, `LegacyOracleConverter.ToNative` and `RegionPlanes`, `MapLegacyExteriorRecipe`, Tasks 1 to 5.
- Produces:
  - `internal sealed class LegacyWaterOracleWorld` with `TileWorldDocument Document`, `TileWorldCatalogs Catalogs`, `static LegacyWaterOracleWorld Create()`, `static bool IsRiver(LegacyWaterSource source)`, `static bool IsShoal(LegacyWaterSource source)`, `static IEnumerable<MapExactXz> SeamSamples()` and `static IEnumerable<MapFramePoint> CellCentres(LegacyWaterSource source, float y)`.
  - `internal sealed record LegacyWaterSource(string BodyId, RegionCoord Region, int Plane, int Index, TileWaterBody Body)`.
  - `internal static class LegacyWaterConverter` with `(MapDocument Document, IReadOnlyList<LegacyWaterSource> Sources) ToNative(TileWorldDocument document, TileWorldCatalogs catalogs)` and `string BodyId(RegionCoord region, int plane, int index)` giving `legacy-water/{rx}/{rz}/{plane}/{index}` from the region's coordinates.
  - `internal enum LegacyWaterDifferenceKind { BoundaryOwner, FloorAboveSurface }`, `internal sealed record LegacyWaterDifference(LegacyWaterDifferenceKind Kind, string BodyId, MapExactXz Point, bool Legacy, bool Native)`, `internal sealed record LegacyWaterReport(int Bodies, int Rects, int Cells, int BoundarySamples, IReadOnlyList<LegacyWaterDifference> Differences, IReadOnlyList<string> Failures)` and `internal static class LegacyWaterComparison` with `LegacyWaterReport Compare(MapDocument native, IReadOnlyList<LegacyWaterSource> sources)`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class LegacyWaterOracleTests
{
    static readonly LegacyWaterOracleWorld World = LegacyWaterOracleWorld.Create();
    static readonly (MapDocument Document, IReadOnlyList<LegacyWaterSource> Sources) Native = LegacyWaterConverter.ToNative(World.Document, World.Catalogs);
    static readonly LegacyWaterReport Report = LegacyWaterComparison.Compare(Native.Document, Native.Sources);

    [Fact]
    public void RegionPieces_KeepOneBodyEach_AndEveryLevelExactly()
    {
        Assert.Equal(4, Native.Sources.Count);
        foreach (LegacyWaterSource source in Native.Sources)
            Assert.Equal(source.Body.SurfaceY, Native.Document.Water!.Bodies.Single(b => b.Id == source.BodyId).SurfaceY);
        float river = Assert.Single(Native.Sources.Where(LegacyWaterOracleWorld.IsRiver).Select(s => s.Body.SurfaceY).Distinct());
        Assert.Equal(2, Native.Sources.Count(LegacyWaterOracleWorld.IsRiver));
        Assert.All(Native.Sources.Where(s => !LegacyWaterOracleWorld.IsRiver(s)), s => Assert.NotEqual(river, s.Body.SurfaceY));
    }

    [Fact]
    public void EveryCellCentre_IsWetInItsOwnBody_ExceptTheShoalCorners()
    {
        Assert.Empty(Report.Failures);
        Assert.Equal(Native.Sources.Sum(s => s.Body.Rects.Sum(r => r.Width * r.Height)), Report.Cells);
        LegacyWaterDifference[] dry = Report.Differences.Where(d => d.Kind == LegacyWaterDifferenceKind.FloorAboveSurface).ToArray();
        Assert.Equal(4, dry.Length);
        string shoal = Native.Sources.Single(LegacyWaterOracleWorld.IsShoal).BodyId;
        Assert.All(dry, d => Assert.Equal((shoal, true, false), (d.BodyId, d.Legacy, d.Native)));
    }

    [Fact]
    public void NegativeZEdges_ReportEveryOwnershipChange()
    {
        LegacyWaterDifference[] owners = Report.Differences.Where(d => d.Kind == LegacyWaterDifferenceKind.BoundaryOwner).ToArray();
        Assert.NotEmpty(owners);
        Assert.All(owners, d => Assert.Equal(1, d.Point.Z.Denominator));
        Assert.All(owners, d => Assert.NotEqual(d.Legacy, d.Native));
        Assert.Contains(owners, d => d.Legacy && !d.Native);
        Assert.Contains(owners, d => !d.Legacy && d.Native);
    }

    [Fact]
    public void SeamBetweenRegionPieces_IsOwnedOnce_AtOneLevel()
    {
        MapWaterBodyDoc[] pieces = Native.Sources.Where(LegacyWaterOracleWorld.IsRiver)
            .Select(s => Native.Document.Water!.Bodies.Single(b => b.Id == s.BodyId)).ToArray();
        Assert.Equal(pieces[0].SurfaceY, pieces[1].SurfaceY);
        var query = new MapWaterQuery(MapScopedSurfaces.CompleteView(Native.Document.Surfaces), Native.Document.Water!);
        foreach (MapExactXz p in LegacyWaterOracleWorld.SeamSamples())
        {
            Assert.Equal(1, pieces.Count(b => MapWaterDomain.Compile(b.Domain).Contains(p)));
            MapWaterSample s = query.Sample(new MapFramePoint(default, new Vector3(p.X.ToSingle(), pieces[0].SurfaceY - 0.01f, p.Z.ToSingle())));
            Assert.Equal(MapWaterStatus.Wet, s.Status);
        }
    }

    [Fact]
    public void SurfaceFeet_StayDry()
    {
        var query = new MapWaterQuery(MapScopedSurfaces.CompleteView(Native.Document.Surfaces), Native.Document.Water!);
        foreach (LegacyWaterSource source in Native.Sources)
            foreach (MapFramePoint p in LegacyWaterOracleWorld.CellCentres(source, y: source.Body.SurfaceY))
                Assert.Equal(MapWaterStatus.Dry, query.Sample(p).Status);
    }

    [Fact]
    public void ConvertedWater_PassesContainment_AndEmitsTwoTrianglesPerRect()
    {
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(Native.Document.Surfaces);
        MapWaterContainmentReport containment = MapWaterContainment.Validate(view, Native.Document.Water!);
        Assert.Equal((true, 0), (containment.Complete, containment.Errors.Count));
        Assert.Equal(2 * Native.Sources.Sum(s => s.Body.Rects.Count), MapWaterCompiler.Compile(view, Native.Document.Water!).Sum(b => b.Work.Triangles));
    }
}
```

`LegacyWaterOracleWorld.Create()` is a synthetic TileWorld document `public-legacy-water-oracle` with two regions side by side along X on plane 0 and a catalog whose material 1 is `GroundMaterialKind.Water`. It holds four bodies after region clipping:

- A river four cells wide crosses the region seam at tile x 64 and bends, so it is two region pieces at one level.
- An eight by eight cell pond in region 1 has a rim 0.4 m higher than the river's.
- River and pond use the basin recipe: exactly one boundary corner of each body sits at its rim, every other corner of its water cells sits at -50 cm or lower, so with the converter's `Auto` topology every cell centre lies at -50 cm or lower, below `SurfaceY - 0.01`.
- A three by three cell shoal in region 0 is the control. Its twelve boundary corners sit at its rim and its four interior corners at -130 cm, so `Auto` puts each corner cell's centre on a rim-to-rim diagonal at the rim, above its surface, while its edge and centre cells are wet. Its four corner cells are the only `FloorAboveSurface` differences.

`IsRiver` and `IsShoal` identify those sources. `SeamSamples()` returns points every 0.25 m on tile x 64 strictly inside the river. `CellCentres(source, y)` returns each cell centre of the source's rects at height y. The converter takes surfaces from `LegacyOracleConverter.ToNative` for every region-plane, adds one exterior space `legacy-world` with `LegacyExteriorV1` lower and `OpenTop` upper footprints over every cell, one medium `legacy` of kind `Water` and wade scale 1, one material resource `legacy-water`, and one body per collected `TileWaterBody` with frame `ImportedMetreCentimetre`, one `MapWaterRect` per `TileRect` in stored order, the legacy float as `SurfaceY`, and the exterior space as its cover. The comparison runs the actual `MapWaterQuery`, never domain containment alone. Each cell centre is sampled at `SurfaceY - 0.01` and must be `Wet` in exactly its own body, or is recorded as `FloorAboveSurface` when the floor at the centre is at or above the surface. Each rect corner and edge midpoint is checked for ownership, meaning membership resolves the covered space at the point and the body's domain contains it, the same two tests the query applies before height, and compared with the legacy rule (tile x in `[X, X + Width)`, tile z in `[Z, Z + Height)`). Every disagreement there is a `BoundaryOwner` difference, so floor height never masks or mimics an ownership change. Any other disagreement, a level mismatch or a piece count mismatch is a failure.

- [ ] **Step 2: Run red** `wa_test t10-red "$COMPAT" "FullyQualifiedName~LegacyWaterOracleTests"`

- [ ] **Step 3: Implement the world, converter and comparison** as described above, in test code only.

- [ ] **Step 4: Run green** `wa_test t10-green "$COMPAT" "FullyQualifiedName~LegacyWaterOracleTests"` (expect 6)

- [ ] **Step 5: Format, guards and commit** `test(mapdoc): prove bounded water against a synthetic TileWorld oracle`

---

### Task 11: Private shipped comparison

**Files:**
- Create: `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedWaterComparisonTests.cs`
- Modify: `KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj` (link `LegacyWaterConverter.cs` and `LegacyWaterComparison.cs`), `ShippedSourceInventory.cs` (expose the verified document and catalogs if it does not already), `README.md`

**Interfaces:**
- Consumes: Task 10, `PrivateOracleEntry.Run`, `PrivateOracleInputs`, `PrivateOracleReport.Record` and `Check`, `ShippedSourceInventory`.
- Produces: `[UnsupportedOSPlatform("windows")] public sealed class ShippedWaterComparisonTests` with `[Fact] ShippedWater_IsExpressibleWithBoundedBodies()`.

- [ ] **Step 1: Write the test**

```csharp
[UnsupportedOSPlatform("windows")]
public sealed class ShippedWaterComparisonTests
{
    [Fact]
    public void ShippedWater_IsExpressibleWithBoundedBodies() => PrivateOracleEntry.Run((inputs, report) =>
    {
        // inputs.SourceRoot was verified against the private provenance inside Require.
        var (document, catalogs) = ShippedSourceInventory.ReadWorld(inputs);
        var (native, sources) = LegacyWaterConverter.ToNative(document, catalogs);
        LegacyWaterReport water = LegacyWaterComparison.Compare(native, sources);
        report.Record("water", JsonSerializer.Serialize(new
        {
            bodies = water.Bodies, rects = water.Rects, cells = water.Cells, boundarySamples = water.BoundarySamples,
            boundaryOwner = water.Differences.Count(d => d.Kind == LegacyWaterDifferenceKind.BoundaryOwner),
            floorAboveSurface = water.Differences.Count(d => d.Kind == LegacyWaterDifferenceKind.FloorAboveSurface),
        }));
        report.Check(water.Failures.Count == 0, "water comparison failures");   // only report.Check and report.Record, never Assert on source values
    });
}
```

- [ ] **Step 2: Build only** `wa_run t11-build dotnet build "$ORACLE" -c Release`. The shipped comparison needs private inputs, so the implementer does not run it.

- [ ] **Step 3: Document the runs**

The README gains a water section: the test reads the same private inputs as the terrain comparison, records only counts, and runs once per pinned source. The controller runs it at the candidate gate against two sources with their own provenance, the historical game commit 74f57ee2 and current game main, and records provenance SHA-256s, report SHA-256s and counts in the private Grimhollow program folder as `R4-WATER-ORACLE.md`. Counts and paths never enter this repository.

- [ ] **Step 4: Run the harness guards** `wa_test t11-guards "$ORACLE" "FullyQualifiedName~PrivateOracleGuardTests"` (expect 17), then format the project with the command in Verification conventions.

- [ ] **Step 5: Commit** `test(mapdoc): compare shipped water in the private harness`

---


## Review, release preparation, candidate gates and integration

1. Whole-branch review on the most capable model against this plan, the spec list and OA27 to OA29, with one fix dispatch and one scoped re-review for its findings.
2. Release preparation, in one commit after `git fetch`, reading `<KhaozEngineVersion>`, `git tag` and the newest CHANGELOG heading on current `main` and `origin/main`:
   - If main already stages an unreleased minor above 20.31.0, ride it and append to its entry. Otherwise set the next minor, 20.32.0 at the time of writing, and add its entry.
   - Update every declaration `scripts/check-doc-versions.sh` guards, including the root README and `docs/USING-KHAOZENGINE.md` package examples, to that one version.
   - Run the documentation sweep `docs/CONTRIBUTOR-RULES.md` requires over every Markdown file, public API comment and behaviour note the round touched. That includes the MapDoc README water section, the MapDoc.Physics README water section from Task 6, C4 and DG9.2 and the R4 rows and plan links in `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md` (including the exit row near line 346), and the R4 row in `docs/design/WORLD-AUTHORING-R2-CAVES-SCALE-2026-10-06.md`, all updated to OA27 to OA29.
3. Candidate gates, each once: `build-slot --heavy --label wa-r4-gate-build -- dotnet build KhaozEngine.slnx -c Release` with zero warnings, `build-slot --heavy --label wa-r4-gate-suite -- dotnet test KhaozEngine.slnx -c Release`, the guards, hosted CI on the pushed branch, and the controller's two private water runs with their reports recorded in Grimhollow `R4-WATER-ORACLE.md`.
4. Integration: merge current `main` into the branch, rerun the focused water tests and, if main moved, the full suite once. Fast-forward `main`, push, and pack the shared feed from `main` with `build-slot --heavy --label release-pack -- sh scripts/pack-local-feed.sh`. The tag follows OA25: a KhaozEngine-rooted thread re-reads version and tags on current main and runs `scripts/tag-release.sh` when the program needs the release.
5. Grimhollow `PROGRAM.md`, `DECISIONS.md` and project memory record the integrated commit, the gates, the private oracle reports and the release.

## Outcome and proof ledger

Filled during execution.

| Item | Evidence |
| --- | --- |
| Plan approval | |
| Task commits | |
| Whole-branch review | |
| Candidate gates | |
| Private water oracle, historical and current | |
| Integration and release | |
