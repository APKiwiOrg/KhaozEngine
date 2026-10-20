# TileWorld Clearance Check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An authoring-time check that refuses a tile world in which a tagged subject mesh (a tree) penetrates a
tagged obstacle mesh (a wall or roof), declared per world, enforced by ke-tileedit on save, and shipped as
KhaozEngine 21.2.0.

**Architecture:** A format-free geometry core in `KhaozEngine.Primitives` rasterizes each obstacle mesh into a
per-cell solid and measures how deep sampled subject points sit inside it. `KhaozEngine.TileWorld` owns the
manifest `clearance` block, the rule model and the TileWorld adapter that places meshes with
`TileObjectPlacement.LocalToWorld` and reports `TileWorldIssue`s. Mesh bytes come through an `ITileMeshGeometry`
seam, with the GLB implementation in `KhaozEngine.TileWorld.Render3D`. ke-tileedit gains clearance verbs and runs
the check on `world_save` and `world_validate`.

**Tech Stack:** .NET 10 (Primitives also targets net8.0), System.Numerics, System.Text.Json, xUnit,
ModelContextProtocol C# SDK, the engine's `GltfLoader` (SharpGLTF).

**Spec:** `/Users/antonio/Grimhollow/.worktrees/tree-building-clearance/docs/superpowers/specs/2026-10-16-tree-building-clearance-design.md`,
section "Engine design (KhaozEngine 21.2.0)". Task 6 copies that section into this repo as a design doc.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/tileworld-clearance`, branch `feature/tileworld-clearance`.
  Every command runs from that root. Stage explicit paths only. Never use the shared stash.
- Every build, test or pack runs through `build-slot --label <what> -- <command>`, with `--heavy` for the full
  solution build or test. One focused test run at a time. No repeated or looped test runs.
- Warnings are errors. No blanket suppressions.
- KESIZE: a new file stays under 800 lines. New behaviour goes in new types and files.
- Test namespaces stay under `KhaozEngine.Tests.*`. Test projects reference only the engine projects they use.
- No em dash or en dash glyphs anywhere. No semicolons in Markdown prose.
- Commit subjects use `area(scope): summary`.
- The engine interprets no tag strings. Tags in rules are compared ordinally against archetype `Tags`.
- The `clearance` block never enters `TileWorldHash.OfWorld`, and `TileWorldValidator.Validate` never runs the
  check. No TileWorld format version bump.
- Exact values: `cellSize` default 0.1, valid 0.02 to 1 inclusive. `tolerance` default 0, finite, at or above 0.
  Issue codes `clearance.overlap`, `clearance.mesh`, `clearance.rule`.
- Workers do not merge to `main`, push `main`, pack, or tag. The orchestrator does.

## Review Focus

- An object whose archetype is missing from the catalogs: the check skips it (the validator already reports
  `archetype.missing`) and never throws. Test in Task 3.
- An archetype carrying both a subject tag and an obstacle tag: an object is never checked against itself. Test in
  Task 3.
- A GLB primitive with positions and no index accessor: it loads as a triangle list, not as an empty mesh. Test in
  Task 4.
- An obstacle so large at the chosen `cellSize` that its grid would exceed 4,194,304 cells: reported as
  `clearance.mesh` with the archetype and the cell count, never an out-of-memory. Test in Tasks 1 and 3.
- A mesh with a non-finite position or an index out of range: `ClearanceMesh` refuses it with
  `ArgumentException`, and the adapter reports `clearance.mesh` rather than crashing. Test in Tasks 1 and 3.

---

### Task 1: Clearance geometry core in Primitives

**Files:**
- Create: `KhaozEngine.Primitives/Clearance/ClearanceMesh.cs`
- Create: `KhaozEngine.Primitives/Clearance/ClearanceSolid.cs`
- Create: `KhaozEngine.Primitives/Clearance/ClearanceSamples.cs`
- Create: `KhaozEngine.Primitives/Clearance/ClearanceProbe.cs`
- Test: `KhaozEngine.Foundation.Tests/Clearance/ClearanceGeometryTests.cs` (Primitives tests live in this project)

**Interfaces:**
- Produces (namespace `KhaozEngine.Primitives`):

```csharp
public sealed class ClearanceMesh
{
    public ClearanceMesh(IReadOnlyList<Vector3> positions, IReadOnlyList<int> indices); // ArgumentException on
        // indices.Count % 3 != 0, an index outside positions, a non-finite position, or zero triangles
    public IReadOnlyList<Vector3> Positions { get; }
    public IReadOnlyList<int> Indices { get; }
    public Vector3 Min { get; }
    public Vector3 Max { get; }
}

public sealed class ClearanceSolid
{
    public const int MaxCells = 4_194_304;
    public static ClearanceSolid Build(ClearanceMesh mesh, float cellSize); // ArgumentOutOfRangeException when
        // cellSize is outside 0.02..1, ClearanceTooLargeException when the planar grid exceeds MaxCells
    public float CellSize { get; }
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public float DepthAt(Vector3 local); // 0 when outside
}

public sealed class ClearanceTooLargeException : Exception { public long Cells { get; } }

public sealed class ClearanceSamples
{
    public static ClearanceSamples Build(ClearanceMesh mesh, float pitch);
    public IReadOnlyList<Vector3> Points { get; }
}

public readonly record struct ClearancePenetration(float Depth, Vector3 WorldPoint);

public static class ClearanceProbe
{
    public static ClearancePenetration? Deepest(ClearanceSamples subject, Matrix4x4 subjectToWorld,
        ClearanceSolid obstacle, Matrix4x4 obstacleToWorld, float tolerance);
    public static (Vector2 Min, Vector2 Max) PlanarBounds(Vector3 localMin, Vector3 localMax, Matrix4x4 toWorld);
}
```

Algorithm (the parts the signatures do not fix):
- **Triangle sampling** (shared by both builders): for each triangle, a barycentric grid with `n = max(1,
  ceil(longestEdge / pitch))` steps per side, every `(i, j)` with `i + j <= n`, which includes vertices and edges.
- **Solid:** the planar grid covers `[Min.x, Max.x] x [Min.z, Max.z]` in `cellSize` cells. Sample every triangle at
  pitch `cellSize / 2`. Each sample marks its cell filled and raises that cell's top to the sample's y. The base is
  `Min.y`. A two-pass chamfer distance transform (weights 1 and sqrt 2, grid padded with one empty ring) gives each
  filled cell its distance in cells to the nearest empty cell centre. Planar depth is that distance times
  `cellSize`, minus `cellSize / 2`, floored at 0.
- **DepthAt(p):** 0 when `p` falls outside the grid, in an empty cell, below the base or at or above the cell top.
  Otherwise the minimum of `top - p.y`, `p.y - base` and the cell's planar depth.
- **Deepest:** transform each subject point by `subjectToWorld`, then by the inverse of `obstacleToWorld`, take
  `DepthAt`. Return the deepest sample whose depth is greater than `tolerance`, with its world point, or null.
  Throw `ArgumentException` when `obstacleToWorld` is not invertible.
- **PlanarBounds:** transform the eight corners of the local box and return the xz extent.

- [ ] **Step 1: Write the failing tests** in `ClearanceGeometryTests` (namespace `KhaozEngine.Tests.Clearance`),
  with small helper meshes built in the test file (`Box(min, max)` returning 12 triangles, `Gable(width, depth,
  eaveY, ridgeY)` returning a closed triangular prism):
  - `MeshRefusesBadInput`: index count not a multiple of 3, index out of range, a NaN position and zero triangles
    each throw `ArgumentException`.
  - `BoxDepthIsTheNearestExit`: solid of `Box((0,0,0),(1,4,0.3))` at cell 0.1. `DepthAt((0.5, 2, 0.15))` is
    0.15 within 0.051. `DepthAt((0.5, 3.95, 0.15))` is 0.05 within 0.051. `DepthAt((0.5, 4.2, 0.15))` is 0.
    `DepthAt((1.4, 2, 0.15))` is 0.
  - `GableAtticCountsAsSolid`: a point under the ridge and above the eave is inside with positive depth. A point
    above the slope at the eave is outside.
  - `TooLargeGridThrows`: `Box((0,0,0),(500,1,500))` at cell 0.1 throws `ClearanceTooLargeException` with
    `Cells` above `MaxCells`.
  - `DeepestRespectsTolerance`: obstacle `Box((0,0,0),(4,4,4))`, subject `Box((3.8,1,1),(4.8,2,2))` reaching
    0.2 m into its +x face, identity transforms, cell 0.1. `Deepest(..., tolerance: 0.1)` returns a depth within
    0.06 of 0.2 (cell quantization). With tolerance 0.3 it returns null. Note in the test that a thin obstacle caps
    depth near half its thickness, which is why this case uses a thick block.
  - `DeepestFollowsBothTransforms`: the same pair moved 10 m along x and turned a quarter turn about y as a rigid
    pair returns the same depth within 0.02.
  - `PlanarBoundsOfAQuarterTurn`: local box x 0..2, z 0..1 under a quarter turn about y at the origin spans 1 in x
    and 2 in z.

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label clearance-core -- dotnet test KhaozEngine.Foundation.Tests -c Release --filter "FullyQualifiedName~ClearanceGeometryTests"`
Expected: build failure, the `Clearance*` types do not exist.

- [ ] **Step 3: Implement the four files to the interfaces and algorithm above.** Each type has a summary doc
  comment explaining intent, including the filled-solid limit (a piece is solid below its top surface) on
  `ClearanceSolid`.

- [ ] **Step 4: Run the tests to verify they pass** (same command). Expected: all pass, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Primitives/Clearance KhaozEngine.Foundation.Tests/Clearance
git commit -m "feat(primitives): mesh clearance solids, samples and penetration probe"
```

### Task 2: Clearance settings in the TileWorld document and manifest

**Files:**
- Create: `KhaozEngine.TileWorld/TileClearanceSettings.cs` (rule record and settings class)
- Create: `KhaozEngine.TileWorld/TileWorldDocument.Clearance.cs`
- Create: `KhaozEngine.TileWorld/TileWorldFile.Clearance.cs` (DTO mapping and parse validation)
- Modify: `KhaozEngine.TileWorld/TileWorldManifest.cs` (two DTOs and one manifest property)
- Modify: `KhaozEngine.TileWorld/TileWorldFile.cs` (`ReadManifest` near line 174, `Save` manifest literal lines
  109-124)
- Modify: `KhaozEngine.TileWorld/TileWorldSource.cs` (after the foliage mapping at lines 78-79), plus any other
  path that maps manifest DTOs onto a document
- Test: `KhaozEngine.TileWorld.Tests/TileWorld/TileWorldClearanceSettingsTests.cs`

**Interfaces:**
- Produces (namespace `KhaozEngine.TileWorld`):

```csharp
public sealed record TileClearanceRule(string Name, IReadOnlyList<string> SubjectTags,
    IReadOnlyList<string> ObstacleTags, float Tolerance = 0f);

public sealed class TileClearanceSettings
{
    public const float DefaultCellSize = 0.1f, MinCellSize = 0.02f, MaxCellSize = 1f;
    public TileClearanceSettings(string meshRoot, float cellSize = DefaultCellSize, string? waiverTag = null,
        IReadOnlyList<TileClearanceRule>? rules = null); // ArgumentException on: empty meshRoot while rules exist,
        // cellSize outside the range, duplicate or empty rule names, an empty tag list, an empty tag string,
        // a negative or non-finite tolerance, an empty waiverTag string
    public string MeshRoot { get; }
    public float CellSize { get; }
    public string? WaiverTag { get; }
    public IReadOnlyList<TileClearanceRule> Rules { get; }
    public TileClearanceRule? Rule(string name);
    public TileClearanceSettings WithRule(TileClearanceRule rule);   // replaces by name or appends
    public TileClearanceSettings WithoutRule(string name);           // ArgumentException on an unknown name
    public TileClearanceSettings With(string meshRoot, float cellSize, string? waiverTag); // keeps the rules
}

// TileWorldDocument.Clearance.cs
public TileClearanceSettings? Clearance { get; }
public void SetClearance(TileClearanceSettings? settings);
```

- Internal DTOs in `TileWorldManifest.cs`: `TileWorldClearanceDto { string MeshRoot = ""; float? CellSize;
  string? WaiverTag; List<TileClearanceRuleDto> Rules = new(); }` and `TileClearanceRuleDto { string Name = "";
  List<string> SubjectTags = new(); List<string> ObstacleTags = new(); float? Tolerance; }`. The manifest gets
  `public TileWorldClearanceDto? Clearance { get; set; }`.
- `TileWorldFile.Clearance.cs`: `internal static TileWorldClearanceDto ClearanceToDto(TileClearanceSettings s)`,
  `internal static TileClearanceSettings ClearanceFromDto(TileWorldClearanceDto dto)`, and a private
  `ValidateClearanceDto(TileWorldClearanceDto? dto, string path)` that wraps `ArgumentException` as
  `TileWorldException`, called from `ReadManifest` beside `ValidateFoliageDtos`. Save writes
  `Clearance = doc.Clearance is null ? null : ClearanceToDto(doc.Clearance)`. Default values are written out
  explicitly so a saved block is self-describing.

- [ ] **Step 1: Write the failing tests** in `TileWorldClearanceSettingsTests` (namespace
  `KhaozEngine.Tests.TileWorld`), using `TileWorldTestData.FlatWorld()` and a temp directory:
  - `SettingsRefuseInvalidValues`: one `[Theory]` row per refusal listed on the constructor.
  - `WithRuleReplacesByNameAndWithoutRuleRemoves`.
  - `ManifestRoundTripsTheBlock`: set settings with meshRoot `../../models`, cellSize 0.1, waiverTag
    `clearance-waived` and one rule `trees-clear-buildings` (`tree` vs `structure`, tolerance 0.05). Save, load,
    assert every field equal. Save again and assert the `world.json` bytes are identical.
  - `AbsentBlockStaysAbsent`: a saved world without settings has no `clearance` key in `world.json`.
  - `WorldHashIgnoresTheBlock`: `TileWorldHash.OfWorld` is equal before and after `SetClearance`, and after
    changing a rule's tolerance.
  - `BadBlockFailsToLoad`: a `world.json` whose block has a duplicate rule name throws `TileWorldException` naming
    the file.

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label clearance-settings -- dotnet test KhaozEngine.TileWorld.Tests -c Release --filter "FullyQualifiedName~TileWorldClearanceSettingsTests"`
Expected: build failure on the missing types.

- [ ] **Step 3: Implement the settings, document property, DTOs, mapping, parse validation, load and save.**

- [ ] **Step 4: Run the tests to verify they pass** (same command), then the whole TileWorld test project once:
  `build-slot --label tileworld-tests -- dotnet test KhaozEngine.TileWorld.Tests -c Release`. Expected: all pass,
  existing world hash literals unchanged.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.TileWorld/TileClearanceSettings.cs KhaozEngine.TileWorld/TileWorldDocument.Clearance.cs \
  KhaozEngine.TileWorld/TileWorldFile.Clearance.cs KhaozEngine.TileWorld/TileWorldManifest.cs \
  KhaozEngine.TileWorld/TileWorldFile.cs KhaozEngine.TileWorld/TileWorldSource.cs \
  KhaozEngine.TileWorld.Tests/TileWorld/TileWorldClearanceSettingsTests.cs
git commit -m "feat(tileworld): declare clearance rules in the world manifest"
```

### Task 3: The TileWorld clearance check

**Files:**
- Create: `KhaozEngine.TileWorld/ITileMeshGeometry.cs`
- Create: `KhaozEngine.TileWorld/TileWorldClearance.cs`
- Modify: `KhaozEngine.TileWorld/TileWorldValidator.cs` (the documented code list, lines 10-17, gains the three
  `clearance.*` codes with a note that they come from `TileWorldClearance.Check`, never from `Validate`)
- Test: `KhaozEngine.TileWorld.Tests/TileWorld/TileWorldClearanceTests.cs`

**Interfaces:**
- Consumes: Task 1 core types, Task 2 `TileClearanceSettings` and `TileWorldDocument.Clearance`.
- Produces:

```csharp
public interface ITileMeshGeometry
{
    bool TryLoad(string meshRef, [NotNullWhen(true)] out ClearanceMesh? mesh, [NotNullWhen(false)] out string? error);
}

public static class TileWorldClearance
{
    public const string OverlapCode = "clearance.overlap", MeshCode = "clearance.mesh", RuleCode = "clearance.rule";
    public static IReadOnlyList<TileWorldIssue> Check(TileWorldDocument doc, TileWorldCatalogs catalogs,
        ITileMeshGeometry meshes);
    public static string ResolveMeshRoot(string worldDirectory, TileClearanceSettings settings);
        // Path.GetFullPath(Path.Combine(worldDirectory, settings.MeshRoot))
}
```

Behaviour:
- Empty result when `doc.Clearance` is null or has no rules.
- Per rule: subjects are loaded objects (`doc.AllObjects()`) whose catalog archetype has any subject tag, obstacles
  likewise. An object whose archetype the catalogs lack is skipped. When no catalog archetype matches a side, emit
  one `clearance.rule` issue naming the rule and the side, and skip the rule.
- Skip a pair when the ids are equal or either object's instance `Tags` contain the settings' `WaiverTag`.
- Placement: `TileObjectPlacement.LocalToWorld(doc, archetype, o)` for both objects. Pairs across planes are
  checked.
- Per archetype, within one `Check` call, cache the `ClearanceMesh` (`meshes.TryLoad(archetype.MeshRef)`), the
  `ClearanceSolid` at `CellSize` and the `ClearanceSamples` at pitch `CellSize`. A failed load, an empty
  `MeshRef`, a refused mesh or a `ClearanceTooLargeException` emits one `clearance.mesh` issue per archetype
  (naming archetype, meshRef and reason) and skips that archetype's pairs.
- Prefilter pairs with `ClearanceProbe.PlanarBounds`. Use a coarse planar bucket of obstacles (for example by
  region) so the cost stays near linear in object count.
- An overlap issue: code `clearance.overlap`, `Region` and `Tile` of the subject, message built with invariant
  culture exactly as
  `rule 'trees-clear-buildings': tree_pine #1234 at (128, 98) plane 0 is 0.31 m inside wall #567 at (126, 98) plane 0`.
- Order: rule order, then subject id, then obstacle id. Issues of one kind are not truncated.

- [ ] **Step 1: Write the failing tests** in `TileWorldClearanceTests` (namespace `KhaozEngine.Tests.TileWorld`).
  Use an in-test `FakeMeshes : ITileMeshGeometry` returning box and cone meshes by meshRef, catalogs built with
  `TileWorldCatalogs.LoadJson` (a `tree` archetype tagged `tree`, a `wall` 1x1 and a `roof` 6x6 with `isRoof`
  tagged `structure`) and `TileWorldTestData.FlatWorld()`:
  - `NoSettingsNoIssues` and `NoRulesNoIssues`.
  - `TreeIntoWallIsReported`: one issue, code `clearance.overlap`, message matches the format with the real ids.
  - `CrownOverWallTopIsClear`: a cone crown whose lowest point is above the wall top reports nothing.
  - `RoofOnPlaneOneCatchesAPlaneZeroCrown`: roof placed on plane 1 over a plane 0 tree reports the overlap.
  - `ToleranceIsHonoured`: the same scene passes with a tolerance above the depth.
  - `WaiverOnEitherObjectSkipsThePair`, and `NoWaiverTagMeansNothingIsWaived` when the instance carries the tag but
    the settings name none.
  - `EveryQuarterTurnGivesTheSameDepth`: the wall at rotations 0 to 3 around a symmetric subject gives depths equal
    within 0.02. Plus one row with an archetype `yawOffsetDegrees` of 90.
  - `MissingArchetypeIsSkipped` (Review Focus): an object with an unknown archetype id does not throw and adds no
    issue.
  - `ObjectNeverChecksItself` (Review Focus): an archetype tagged both `tree` and `structure` produces no
    self-pair issue.
  - `UnreadableMeshIsReported` and `TooLargeObstacleIsReported` (Review Focus): one `clearance.mesh` issue each,
    no exception.
  - `RuleMatchingNothingIsReported`: obstacle tag `strucutre` gives one `clearance.rule` issue.
  - `IssuesAreOrdered`: two trees in two walls come back by subject id then obstacle id.

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label clearance-check -- dotnet test KhaozEngine.TileWorld.Tests -c Release --filter "FullyQualifiedName~TileWorldClearanceTests"`
Expected: build failure on `TileWorldClearance`.

- [ ] **Step 3: Implement `ITileMeshGeometry`, `TileWorldClearance` and the validator doc list.**

- [ ] **Step 4: Run the tests to verify they pass** (same command).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.TileWorld/ITileMeshGeometry.cs KhaozEngine.TileWorld/TileWorldClearance.cs \
  KhaozEngine.TileWorld/TileWorldValidator.cs KhaozEngine.TileWorld.Tests/TileWorld/TileWorldClearanceTests.cs
git commit -m "feat(tileworld): check placed meshes against declared clearance rules"
```

### Task 4: GLB mesh geometry for the check

**Files:**
- Create: `KhaozEngine.TileWorld.Render3D/GltfTileMeshGeometry.cs`
- Test: `KhaozEngine.Render.Tests/TileWorld/GltfTileMeshGeometryTests.cs`
- Test helper: `KhaozEngine.Render.Tests/TileWorld/ClearanceGlbFixtures.cs` (`WriteBox(string path, Vector3 min,
  Vector3 max, bool indexed = true)`, a hand-built binary glTF modelled on
  `KhaozEngine.TileEdit.Tests/ArchetypeHeightToolsTests.cs:373` `WriteTriangleGlb`, with an index accessor when
  `indexed`, and a node translation option for the baked-transform test)

**Interfaces:**
- Consumes: `ITileMeshGeometry`, `ClearanceMesh`.
- Produces:

```csharp
public sealed class GltfTileMeshGeometry : ITileMeshGeometry
{
    public GltfTileMeshGeometry(string rootDirectory);
    public bool TryLoad(string meshRef, [NotNullWhen(true)] out ClearanceMesh? mesh, [NotNullWhen(false)] out string? error);
}
```

- Path through `new GltfMeshResolver(rootDirectory).PathFor(meshRef)`. Positions from `GltfLoader.Load(path)`
  `Vertices[i].Position`, triangles from `Indices32`. Node transforms are already baked by the loader. Cache by
  meshRef, including failures. The error text names the meshRef and the full path, and for a missing file says it
  is missing. Any loader exception becomes an error, never a throw.

- [ ] **Step 1: Write the failing tests** in `GltfTileMeshGeometryTests` (namespace `KhaozEngine.Tests.TileWorld`):
  - `LoadsAnIndexedBox`: 8 positions, 12 triangles, bounds equal to the written box.
  - `LoadsANonIndexedPrimitive` (Review Focus): a box written without indices loads with 12 triangles.
  - `BakesNodeTransforms`: a box under a node translated by (2, 0, 0) has `Min.X` near 2.
  - `MissingFileIsAnError`: `TryLoad` returns false and the error contains the meshRef and "missing".
  - `CorruptFileIsAnError`: random bytes give false, no throw.

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label clearance-glb -- dotnet test KhaozEngine.Render.Tests -c Release --filter "FullyQualifiedName~GltfTileMeshGeometryTests"`
Expected: build failure on `GltfTileMeshGeometry`.

- [ ] **Step 3: Implement `GltfTileMeshGeometry`.** If `GltfLoader.Load` returns no triangles for a non-indexed
  primitive, fix the loader at its source rather than working around it here.

- [ ] **Step 4: Run the tests to verify they pass** (same command). Also run
  `build-slot --label arch-tests -- dotnet test KhaozEngine.Tests -c Release --filter "FullyQualifiedName~ArchitectureTests"`
  and confirm no project graph expectation changed (no new ProjectReference was needed).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.TileWorld.Render3D/GltfTileMeshGeometry.cs KhaozEngine.Render.Tests/TileWorld/GltfTileMeshGeometryTests.cs \
  KhaozEngine.Render.Tests/TileWorld/ClearanceGlbFixtures.cs
git commit -m "feat(tileworld-render3d): read clearance meshes from glb files"
```

### Task 5: ke-tileedit clearance verbs and save enforcement

**Files:**
- Create: `KhaozEngine.TileWorld.Editing/Commands/ClearanceCommands.cs` (`SetClearanceCommand`)
- Create: `KhaozEngine.TileEdit.Tool/Tools/ClearanceTools.cs`
- Create: `KhaozEngine.TileEdit.Tool/MutationService.Clearance.cs`
- Create: `KhaozEngine.TileEdit.Tool/QueryService.Clearance.cs`
- Create: `KhaozEngine.TileEdit.Tool/Results.Clearance.cs`
- Modify: `KhaozEngine.TileEdit.Tool/TileEditSession.cs` (`Save` lines 108-118, `Validate` lines 132-141, plus one
  new method)
- Modify: `KhaozEngine.TileEdit.Tool/Tools/McpBootstrap.cs` (`builder.WithTools<ClearanceTools>()` beside the
  foliage registration)
- Test: `KhaozEngine.TileEdit.Tests/ClearanceToolsTests.cs`, helper `KhaozEngine.TileEdit.Tests/ClearanceGlbFixtures.cs`
  (the same `WriteBox` shape as Task 4, since test projects do not share code)

**Interfaces:**
- Consumes: Tasks 2 to 4.
- Produces:

```csharp
// KhaozEngine.TileWorld.Editing
public sealed class SetClearanceCommand : TileCommandBase
{
    public SetClearanceCommand(TileWorldDocument doc, TileClearanceSettings? settings); // label "Set clearance",
        // no dirty rects, undo restores the previous settings
}

// KhaozEngine.TileEdit.Tool, Results.Clearance.cs
public sealed record ClearanceRuleInfo(string Name, string[] SubjectTags, string[] ObstacleTags, float Tolerance);
public sealed record ClearanceInfo(string MeshRoot, float CellSize, string? WaiverTag, ClearanceRuleInfo[] Rules);
public sealed record ClearanceCheckResult(bool Clear, IReadOnlyList<string> Issues); // "[code] message"

// TileEditSession
public IReadOnlyList<TileWorldIssue> ClearanceIssues(); // empty when the world declares no rules
```

MCP verbs in `ClearanceTools` (`[McpServerToolType] public sealed class ClearanceTools(QueryService query,
MutationService mutate)`), each with a `Description` in the style of `FoliageTools`:
- `clearance_get` returns `ClearanceInfo?`, null when the world has no block.
- `clearance_set(string meshRoot, float cellSize = 0.1f, string? waiverTag = null)` creates or updates the block and
  keeps its rules. One undo step.
- `clearance_rule_set(string name, string[] subjectTags, string[] obstacleTags, float tolerance = 0f)` adds or
  replaces a rule. Refuses when the world has no block, telling the caller to run `clearance_set` first.
- `clearance_rule_remove(string name)` refuses an unknown name.
- `clearance_check` returns `ClearanceCheckResult` without writing anything.

Session behaviour:
- `ClearanceIssues()` resolves `TileWorldClearance.ResolveMeshRoot(_path, settings)`, refuses with a
  `clearance.mesh` issue when that directory does not exist, and otherwise runs `TileWorldClearance.Check` with a
  `GltfTileMeshGeometry` on that root.
- `Save` runs `ClearanceIssues()` after `ValidateOrThrow`. Any issue throws `TileWorldException` whose message
  starts `world '<id>' has <n> clearance issues:` and lists EVERY issue as `[code] message`, one per line. Nothing
  is written.
- `Validate` appends the clearance issues to the validator's.

- [ ] **Step 1: Write the failing tests** in `ClearanceToolsTests` (namespace `KhaozEngine.Tests.TileEdit`), with
  `TileEditTestWorld.NewSession` and a catalog plus GLBs written under the temp world's `models` sibling:
  - `ClearanceVerbsEditAndUndo`: set, rule_set, rule_remove change `clearance_get`, and `undo` restores each prior
    state.
  - `RuleSetWithoutBlockRefuses`.
  - `SaveRefusesAnOverlapAndWritesNothing`: a tree box inside a wall box, rule declared. `world_save` throws, the
    message contains `clearance.overlap` and both ids, and the `world.json` bytes on disk are unchanged.
  - `SaveSucceedsOnceWaived`: tag the tree with the waiver tag through `object_set_tags`, then save succeeds.
  - `MissingMeshRootRefusesSave`: meshRoot pointing at a missing directory gives a `clearance.mesh` refusal.
  - `ValidateListsClearanceIssues` and `CheckReportsWithoutSaving` (the dirty flag stays set and nothing is
    written).

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label clearance-tools -- dotnet test KhaozEngine.TileEdit.Tests -c Release --filter "FullyQualifiedName~ClearanceToolsTests"`
Expected: build failure on the missing types.

- [ ] **Step 3: Implement the command, services, results, tools, registration and session changes.**

- [ ] **Step 4: Run the tests to verify they pass** (same command), then the whole TileEdit test project once:
  `build-slot --label tileedit-tests -- dotnet test KhaozEngine.TileEdit.Tests -c Release`.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.TileWorld.Editing/Commands/ClearanceCommands.cs KhaozEngine.TileEdit.Tool/Tools/ClearanceTools.cs \
  KhaozEngine.TileEdit.Tool/MutationService.Clearance.cs KhaozEngine.TileEdit.Tool/QueryService.Clearance.cs \
  KhaozEngine.TileEdit.Tool/Results.Clearance.cs KhaozEngine.TileEdit.Tool/TileEditSession.cs \
  KhaozEngine.TileEdit.Tool/Tools/McpBootstrap.cs KhaozEngine.TileEdit.Tests/ClearanceToolsTests.cs \
  KhaozEngine.TileEdit.Tests/ClearanceGlbFixtures.cs
git commit -m "feat(tileedit): clearance verbs and save refusal"
```

### Task 6: Documentation, design record, version 21.2.0 and full verification

**Files:**
- Create: `docs/design/TILEWORLD-CLEARANCE-DESIGN-2026-10-16.md` (the spec's engine section, rewritten for this
  repo: owner rulings TC1 to TC3, the block, the solid model and its limit, the seam, ke-tileedit behaviour)
- Modify: `docs/INDEX.md` (a design row with status "Shipped in 21.2.0")
- Modify: `KhaozEngine.TileWorld/README.md` (a "Clearance" section: block fields and defaults, rule matching,
  waiver, issue codes, solid model limit, not part of `Validate`, not hashed)
- Modify: `KhaozEngine.TileEdit.Tool` README or the tool section of `docs/USING-KHAOZENGINE.md`, wherever the
  existing verb list lives (the five verbs and the save refusal)
- Modify: `KhaozEngine.Primitives/README.md` (the clearance types, if the README lists types)
- Modify: `Directory.Build.props` (`<KhaozEngineVersion>21.2.0</KhaozEngineVersion>`), `CHANGELOG.md` (new
  `## 21.2.0` entry), `README.md` and `docs/USING-KHAOZENGINE.md` version declarations

- [ ] **Step 1: Re-read the version state.** `git fetch origin`, then
  `git show origin/main:Directory.Build.props | grep KhaozEngineVersion` and `git tag --sort=-creatordate | head -3`.
  If `origin/main` already stages an unreleased version above 21.1.0, ride it and stop to report instead of
  choosing a number. Otherwise use 21.2.0.

- [ ] **Step 2: Write the docs and the design record.** The CHANGELOG entry is flat bullets wrapped near 110
  columns, covering: the manifest `clearance` block, `TileWorldClearance.Check` and its three codes, the
  Primitives core, `GltfTileMeshGeometry`, the five ke-tileedit verbs and the save refusal, and that the block is
  outside the world hash.

- [ ] **Step 3: Sweep Markdown for stale claims.** `grep -rn -i "clearance\|ValidateOrThrow\|world_save" --include='*.md' .`
  outside `.worktrees`, and fix any text the change makes wrong.

- [ ] **Step 4: Run the full verification**

```bash
mkdir -p local-feed
build-slot --heavy --label engine-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label engine-test -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: build with zero warnings, every test passes, every guard passes. Record the passed and skipped counts.

- [ ] **Step 5: Commit**

```bash
git add docs/design/TILEWORLD-CLEARANCE-DESIGN-2026-10-16.md docs/INDEX.md KhaozEngine.TileWorld/README.md \
  Directory.Build.props CHANGELOG.md README.md docs/USING-KHAOZENGINE.md <any other swept doc>
git commit -m "release(21.2.0): tile world clearance check"
```

## Orchestrator finish (not a worker task)

1. Whole-branch review, then fix rounds.
2. File the MapDoc adapter follow-up issue in KhaozEngine (`kind/backlog`, `confidence/authored`), linking the
   design doc.
3. Fetch, merge `origin/main` into the branch, re-run Task 6 Step 4, merge to `main`, push `main`.
4. `scripts/pack-local-feed.sh` from the main checkout after `origin/main` contains the commit.
5. Tag `v21.2.0` through the `release-khaozengine` handoff (owner approved this one tag on 2026-10-16), then verify
   the remote annotated tag, exact-commit CI, publish workflow, packages and tool downloads before Grimhollow
   adopts the pin.
