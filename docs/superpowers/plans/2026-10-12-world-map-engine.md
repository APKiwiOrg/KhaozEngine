# World map engine round Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship three opt-in engine packages that turn an authored world into a stylized, chunked, runtime-generated map and show it in a pan and zoom widget, riding the staged 20.33.0.

**Architecture:** `KhaozEngine.WorldMap` is a GPU-free core: map-space contracts, a deterministic CPU rasterizer, per-chunk feature anchors and a generation-tracked chunk cache with a background worker. `KhaozEngine.TileWorld.WorldMap` adapts `TileWorldDocument` to the source contract. `KhaozEngine.WorldMap.Gui` composes `PannableCanvas` for navigation, uploads chunk textures under a frame budget and lays out fixed-size icons, labels and markers.

**Tech Stack:** C# on net10.0, xunit, `KhaozEngine.Primitives` (`Color`, `Rect`, `MathUtil`), `System.Numerics`, `KhaozEngine.TileWorld`, `KhaozEngine.Gui` (`PannableCanvas`), `KhaozEngine.Render2D` (`SpriteBatch`, `Texture2D`, `SpriteFont`, `PrimitiveRenderer`), `KhaozEngine.Imaging` (tests only, PNG golden).

**Spec:** `docs/superpowers/specs/2026-10-12-world-map-design.md` (this branch). Grimhollow consumer spec: Grimhollow `feature/world-map`, same path.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/world-map`, branch `feature/world-map`. Start every task with `cd /Users/antonio/KhaozEngine/.worktrees/world-map && git branch --show-current` and stop if it does not print `feature/world-map`.
- Every build and test runs through `build-slot --label wm -- <command>`, with `--heavy` for full-solution builds and suites. Exit 75 means retry later. Never loop tests. Run `mkdir -p local-feed` once before the first build.
- `Directory.Build.props` already sets net10.0, nullable, `TreatWarningsAsErrors` and XML docs. Zero warnings. Public types and members get XML doc comments.
- KESIZE: no file over 800 lines. New behaviour goes in a new type.
- No em or en dashes anywhere. No prose semicolons in Markdown.
- The three packages are opt-in: each is added to `OptInBackends` in `KhaozEngine.Tests/ArchitectureTests.cs:35` and pinned by an exact-references fact in `KhaozEngine.Tests/ArchitectureTests.WorldMap.cs`.
- Each package gets a root `README.md` catalog row, its own `README.md` packed with `<None Include="README.md" Pack="true" PackagePath="\" />`, `<PackageReadmeFile>README.md</PackageReadmeFile>`, and `KhaozEngine.slnx` entries for the package and its test project. `scripts/check-doc-versions.sh` enforces this.
- Package csproj shape copies `KhaozEngine.MapDoc.Physics/KhaozEngine.MapDoc.Physics.csproj` (`PackageId`, `Version` = `$(KhaozEngineVersion)`, `Description`, `InternalsVisibleTo` the test project). Test csproj shape copies `KhaozEngine.MapDoc.Physics.Tests/KhaozEngine.MapDoc.Physics.Tests.csproj` (`IsPackable` false, `RootNamespace` `KhaozEngine.Tests`, xunit) and references only its package plus what this plan names.
- Map space: x east and y north in metres, north up on screen, east right. A `MapRect` has its origin at the south-west corner. Raster row 0 is the north edge of a chunk and column 0 the west edge.
- Defaults pinned by the spec: `PixelsPerMetre` 4, `ChunkMetres` 64 (256 px chunks), relief azimuth 315 degrees and altitude 45 degrees, view maximum zoom 8 screen pixels per metre, upload budget 4 chunks per frame.
- Colour math works directly on the stored 0 to 1 channel values. `Render2DSurface.CreateTexture` uploads `R8G8B8A8UNorm` with no sRGB decode, so authored hex colours reach the screen as authored. Byte conversion is `(byte)MathF.Round(MathUtil.Clamp01(v) * 255f)`.
- The same source revision and style produce identical chunk bytes on one machine. Goldens compare with a tolerance of 2 per channel to absorb cross-platform float differences.
- No GPU code in `KhaozEngine.WorldMap`. No new third-party package anywhere.
- Commit subjects use `area(scope): summary`, scope `worldmap`.

## Review Focus

1. Negative map coordinates (Hollowmere has `r_-1_*` regions): chunk addressing and bounds must floor, not truncate. Tests in Task 1 and Task 6.
2. Objects straddling chunk borders, including big roofs anchored in a neighbouring chunk: both chunks draw their part with no seam. Tests in Task 3 and Task 6.
3. Void ground (outside every region, or a missing material) beside land and water: exact void colour, and neighbouring relief stays finite. Test in Task 2.
4. A map window placed away from the screen origin and partly over missing chunks: chunk quads and overlays agree with `MapToScreen` and clip to the viewport. Tests in Task 7.
5. A world change racing a chunk build: an invalidation during a build leaves the chunk stale, and a throwing source does not kill the worker. Tests in Task 5.

---

### Task 1: `KhaozEngine.WorldMap` package, map space and contracts

**Files:**
- Create: `KhaozEngine.WorldMap/KhaozEngine.WorldMap.csproj`, `KhaozEngine.WorldMap/README.md`
- Create: `KhaozEngine.WorldMap/MapRect.cs`, `WorldMapChunking.cs`, `WorldMapSurfaceGrid.cs`, `WorldMapFootprint.cs`, `WorldMapMarker.cs`, `IWorldMapSource.cs`, `WorldMapStyle.cs`, `WorldMapSymbols.cs`, `WorldMapFeatureRules.cs` (all under `KhaozEngine.WorldMap/`)
- Create: `KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj`, `KhaozEngine.WorldMap.Tests/WorldMapChunkingTests.cs`, `KhaozEngine.WorldMap.Tests/WorldMapContractTests.cs`
- Create: `KhaozEngine.Tests/ArchitectureTests.WorldMap.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.cs:35` (`OptInBackends` gains `"WorldMap"` with a one-line reason comment), `KhaozEngine.slnx`, `README.md` (catalog row after the MapDoc.Physics row at line 63)
- Modify: `docs/superpowers/specs/2026-10-12-world-map-design.md` (contract amendments below)

**Interfaces:**
- Consumes: `KhaozEngine.Primitives.Color`, `System.Numerics.Vector2`/`Vector3`.
- Produces (namespace `KhaozEngine.WorldMap`):

```csharp
public readonly record struct MapRect(float X, float Y, float Width, float Height)
{
    public float Right { get; }            // X + Width
    public float Top { get; }              // Y + Height
    public Vector2 Center { get; }
    public bool Contains(Vector2 p);       // half-open: X <= p.X < Right, Y <= p.Y < Top
    public bool Intersects(MapRect other); // half-open, zero-area rects intersect nothing
    public MapRect Expand(float metres);
    public MapRect Union(MapRect other);
}

public readonly record struct WorldMapChunkCoord(int X, int Y);

public static class WorldMapChunking
{
    public static WorldMapChunkCoord ChunkAt(Vector2 mapPosition, float chunkMetres);   // floor on both axes
    public static MapRect ChunkRect(WorldMapChunkCoord chunk, float chunkMetres);
    public static IEnumerable<WorldMapChunkCoord> ChunksTouching(MapRect area, float chunkMetres); // row-major, south row first, west first
}

public sealed class WorldMapSurfaceGrid
{
    public WorldMapSurfaceGrid(Vector2 origin, float cellSize, int width, int height); // allocates arrays, cells start void
    public Vector2 Origin { get; }       // south-west corner of cell (0, 0)
    public float CellSize { get; }
    public int Width { get; }
    public int Height { get; }
    public Color[] Ground { get; }       // Color.Transparent marks a void cell
    public float[] SurfaceHeight { get; } // metres, NaN for void
    public float[] WaterSurface { get; } // metres, NaN where dry
    public int Index(int column, int row); // row * Width + column, row 0 is the south row
}

public enum WorldMapCollision { None, Solid, Wall }

public sealed record WorldMapFootprint(
    long Id, string ArchetypeKey, Vector2 Center, Vector2 HalfExtents, float Rotation,
    IReadOnlyList<string> Tags, bool IsRoof, WorldMapCollision Collision)
{
    public bool Blocking { get; } // Collision != None
}
// Rotation is radians, positive turns the local +x axis clockwise as seen on the north-up map.
// HalfExtents are along the local axes before rotation.

public sealed record WorldMapMarker(string Name, Vector2 Position, IReadOnlyList<string> Tags);

public interface IWorldMapSource
{
    MapRect Bounds { get; }
    float SurfaceCellSize { get; }
    long Revision { get; }
    event Action<MapRect>? Changed;
    WorldMapSurfaceGrid SampleSurface(MapRect area);                         // covers at least area, aligned to the source lattice
    void CollectFootprints(MapRect area, List<WorldMapFootprint> into);      // every surface footprint whose shape intersects area
    void CollectMarkers(MapRect area, List<WorldMapMarker> into);            // every surface marker whose position area contains
    Vector2 ToMap(Vector3 worldPosition);
    float ToMapHeading(Vector3 worldForward);                                // radians clockwise from map north, 0 for a zero vector
}

public enum WorldMapSymbolKind { Hidden, Bridge, Obstacle, Wall, Building, Tree } // declaration order after Hidden is draw order
public sealed record WorldMapSymbol(WorldMapSymbolKind Kind, Color Fill, Color Outline)
{
    public static WorldMapSymbol Hidden { get; }
}
public sealed record WorldMapFootprintMatch(
    string? ArchetypeKey = null, IReadOnlyList<string>? AllTags = null, bool? IsRoof = null, WorldMapCollision? Collision = null);
public sealed record WorldMapSymbolRule(WorldMapFootprintMatch Match, WorldMapSymbol Symbol);
public sealed record WorldMapRelief(
    float AzimuthDegrees = 315f, float AltitudeDegrees = 45f, float Strength = 0.5f, float SteepSlope = 1f, float SteepDarken = 0.75f);
public sealed record WorldMapWater(Color Shallow, Color Deep, Color Shoreline, float DeepDepthMetres = 4f, int ShorelinePixels = 1);

public enum WorldMapFeatureSource { Footprint, Marker }
public sealed record WorldMapIconRule(
    WorldMapFeatureSource Source, IReadOnlyList<string> AllTags, string IconKey, int Priority, float MergeRadiusPixels = 24f);
public sealed record WorldMapLabelTier(string Tag, int Rank, float MinPixelsPerMetre, float MaxPixelsPerMetre, float TextScale);
public sealed record WorldMapLabelRules(string LabelTag, IReadOnlyList<WorldMapLabelTier> Tiers);

public sealed record WorldMapStyle
{
    public float PixelsPerMetre { get; init; }            // 4
    public float ChunkMetres { get; init; }               // 64
    public int ChunkPixels { get; }                       // (int)MathF.Round(ChunkMetres * PixelsPerMetre)
    public Color VoidColor { get; init; }                 // #d9cba6
    public WorldMapRelief Relief { get; init; }           // new()
    public WorldMapWater Water { get; init; }             // Shallow #6f9cc0, Deep #2a5a9a, Shoreline #1f3f66
    public IReadOnlyList<WorldMapSymbolRule> SymbolRules { get; init; }   // empty
    public WorldMapSymbol FallbackBlocking { get; init; } // Obstacle, fill #6f6a60, outline #3d3a34
    public Color BuildingShadow { get; init; }            // new Color(0, 0, 0, 0.35f)
    public Vector2 BuildingShadowOffsetMetres { get; init; } // (0.6, -0.6), east and south
    public float WallWidthMetres { get; init; }           // 0.4
    public float OutlinePixels { get; init; }             // 1
    public IReadOnlyList<WorldMapIconRule> IconRules { get; init; }       // empty
    public WorldMapLabelRules? Labels { get; init; }      // null
}
```

- [ ] **Step 1: Create the package and test project skeletons**

Csproj files per Global Constraints. The package references only `../KhaozEngine.Primitives/KhaozEngine.Primitives.csproj`. The test project references only `../KhaozEngine.WorldMap/KhaozEngine.WorldMap.csproj` and `../KhaozEngine.Imaging/KhaozEngine.Imaging.csproj` (the Task 3 golden). Add both to `KhaozEngine.slnx` beside the MapDoc entries.

- [ ] **Step 2: Write the failing tests**

`WorldMapChunkingTests`:

```csharp
[Fact] public void ChunkAt_NegativeCoordinates_Floors()
{
    Assert.Equal(new WorldMapChunkCoord(-1, 0), WorldMapChunking.ChunkAt(new Vector2(-0.5f, 10f), 64f));
    Assert.Equal(new WorldMapChunkCoord(-2, -1), WorldMapChunking.ChunkAt(new Vector2(-64.01f, -0.01f), 64f));
    Assert.Equal(new WorldMapChunkCoord(1, 0), WorldMapChunking.ChunkAt(new Vector2(64f, 0f), 64f));
}
[Fact] public void ChunkRect_IsSouthWestOrigin() =>
    Assert.Equal(new MapRect(-64f, 128f, 64f, 64f), WorldMapChunking.ChunkRect(new WorldMapChunkCoord(-1, 2), 64f));
[Fact] public void ChunksTouching_EdgeOnBoundary_ExcludesNextChunk()
{
    var chunks = WorldMapChunking.ChunksTouching(new MapRect(0f, 0f, 64f, 64f), 64f).ToList();
    Assert.Equal(new[] { new WorldMapChunkCoord(0, 0) }, chunks);
}
[Fact] public void ChunksTouching_SpanningNegativeArea_RowMajorSouthFirst()
{
    var chunks = WorldMapChunking.ChunksTouching(new MapRect(-10f, -10f, 20f, 20f), 64f).ToList();
    Assert.Equal(new[] { new WorldMapChunkCoord(-1, -1), new(0, -1), new(-1, 0), new(0, 0) }, chunks);
}
[Fact] public void ChunksTouching_ZeroArea_IsEmpty() =>
    Assert.Empty(WorldMapChunking.ChunksTouching(new MapRect(5f, 5f, 0f, 3f), 64f));
```

`WorldMapContractTests`:

```csharp
[Fact] public void MapRect_HalfOpenContainsAndIntersects()
{
    var r = new MapRect(0f, 0f, 10f, 10f);
    Assert.True(r.Contains(new Vector2(0f, 0f)));
    Assert.False(r.Contains(new Vector2(10f, 5f)));
    Assert.False(r.Intersects(new MapRect(10f, 0f, 5f, 5f)));
    Assert.True(r.Intersects(new MapRect(9.9f, 9.9f, 5f, 5f)));
    Assert.Equal(new MapRect(-1f, -1f, 12f, 12f), r.Expand(1f));
}
[Fact] public void SurfaceGrid_StartsVoid_IndexIsRowMajorSouthFirst()
{
    var g = new WorldMapSurfaceGrid(new Vector2(-2f, 3f), 1f, 4, 3);
    Assert.Equal(1 * 4 + 2, g.Index(2, 1));
    Assert.All(g.Ground, c => Assert.Equal(0f, c.A));
    Assert.All(g.SurfaceHeight, h => Assert.True(float.IsNaN(h)));
    Assert.All(g.WaterSurface, w => Assert.True(float.IsNaN(w)));
}
[Fact] public void Style_Defaults_MatchSpec()
{
    var s = new WorldMapStyle();
    Assert.Equal(4f, s.PixelsPerMetre);
    Assert.Equal(64f, s.ChunkMetres);
    Assert.Equal(256, s.ChunkPixels);
    Assert.Equal(315f, s.Relief.AzimuthDegrees);
    Assert.Equal(45f, s.Relief.AltitudeDegrees);
    Assert.Equal(WorldMapSymbolKind.Obstacle, s.FallbackBlocking.Kind);
}
[Fact] public void Footprint_BlockingFollowsCollision()
{
    var f = new WorldMapFootprint(1, "rock", Vector2.Zero, Vector2.One, 0f, [], false, WorldMapCollision.None);
    Assert.False(f.Blocking);
    Assert.True((f with { Collision = WorldMapCollision.Wall }).Blocking);
}
```

`KhaozEngine.Tests/ArchitectureTests.WorldMap.cs`, modelled on `ArchitectureTests.MapDocPhysics.cs:12-39`: `WorldMap_ReferencesOnlyPrimitives`, `WorldMap_IsAnExplicitReferenceOutsideEveryUmbrella`, `WorldMapTests_ReferenceOnlyThePackageAndImaging`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release`
Expected: compile failure, the types do not exist.

- [ ] **Step 4: Implement the contracts listed under Interfaces**

One file per concern as listed under Files. `WorldMapStyle` default colours come from `Color.FromHex`. Collections default to empty arrays, never null.

- [ ] **Step 5: Run the package tests and the architecture facts**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release`
Run: `build-slot --label wm -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests"`
Expected: both PASS.

- [ ] **Step 6: Package README, catalog row and spec amendments**

`KhaozEngine.WorldMap/README.md`: purpose, map space, the source contract, the style and a link to the spec. Root `README.md` row: `| **KhaozEngine.WorldMap** | Stylized world map imagery from any authored world (opt-in, NOT in any umbrella, add explicitly). GPU-free: map-space contracts, a deterministic chunk rasterizer, feature anchors and a chunk cache. | Primitives |`.

Amend the spec's Source contract section to match the code: footprints carry `Collision` (`None`, `Solid`, `Wall`) instead of a blocking flag and drop top height and level (the source already filters to the surface), shapes are oriented rectangles only (MapDoc polygons arrive with the MapDoc adapter if its buildings need them), `ToMapHeading` takes a world forward vector so no yaw convention leaks into the contract, and the source exposes `SurfaceCellSize`.

Run: `bash scripts/check-doc-versions.sh` and `sh scripts/check-prose.sh --tree`
Expected: both pass.

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.WorldMap KhaozEngine.WorldMap.Tests KhaozEngine.Tests/ArchitectureTests.cs KhaozEngine.Tests/ArchitectureTests.WorldMap.cs KhaozEngine.slnx README.md docs/superpowers/specs/2026-10-12-world-map-design.md
git commit -m "feat(worldmap): add the map space and source contracts"
```

---

### Task 2: Rasterizer ground, relief and water

**Files:**
- Create: `KhaozEngine.WorldMap/WorldMapRasterizer.cs`, `KhaozEngine.WorldMap/WorldMapChunkImage.cs`, `KhaozEngine.WorldMap/Raster/SurfaceSampler.cs`, `KhaozEngine.WorldMap/Raster/ReliefShader.cs`, `KhaozEngine.WorldMap/Raster/WaterPainter.cs`, `KhaozEngine.WorldMap/Raster/RasterCanvas.cs`
- Create: `KhaozEngine.WorldMap.Tests/TestWorldMapSource.cs`, `KhaozEngine.WorldMap.Tests/RasterGroundTests.cs`

**Interfaces:**
- Consumes: Task 1 contracts.
- Produces:

```csharp
public sealed record WorldMapChunkImage(WorldMapChunkCoord Coord, int Size, byte[] Rgba, long Revision); // Size x Size RGBA8, row 0 north
public static class WorldMapRasterizer
{
    public static WorldMapChunkImage Rasterize(IWorldMapSource source, WorldMapStyle style, WorldMapChunkCoord chunk);
}
// internal: RasterCanvas (float RGB working buffer, Blend(x, y, Color, float coverage), ToBytes()),
// MapPoint(px, py) = (rect.X + (px + 0.5) / ppm, rect.Top - (py + 0.5) / ppm)
```

- Test helper `TestWorldMapSource : IWorldMapSource` (tests project): constructor `(MapRect bounds, float cellSize = 1f)`. `SetCell(int column, int row, Color ground, float height, float water = float.NaN)` addresses cells relative to `bounds` origin. Unset cells are void. `List<WorldMapFootprint> Footprints`, `List<WorldMapMarker> Markers`, `long Revision { get; set; }`, `Action<MapRect>? BeforeSample` (called at the start of `SampleSurface`, lets Task 5 block or throw), `void RaiseChanged(MapRect area)`, `int SampleCalls`. `SampleSurface` returns the cells covering the requested area clipped to bounds. `CollectFootprints` filters by oriented-rect bounding box intersection, `CollectMarkers` by `Contains`. `ToMap` returns `(x, -z)` and `ToMapHeading` returns `MathF.Atan2(f.X, -f.Z)`.

- [ ] **Step 1: Write the failing tests in `RasterGroundTests`**

All use `ChunkMetres = 8f` and `PixelsPerMetre = 4f` (32 px chunks) unless stated, with a source over `new MapRect(0, 0, 16, 16)` and cell size 1.

```csharp
[Fact] public void FlatGround_EveryPixelIsTheGroundColour()          // all cells #808040 height 0, every pixel (0x80, 0x80, 0x40, 255)
[Fact] public void Orientation_RowZeroIsNorth_ColumnZeroIsWest()     // only cell (0, 7) red, rest green: pixel (0, 0) red, pixel (0, 31) green, pixel (31, 0) green
[Fact] public void Blend_AcrossMaterialEdge_IsMonotonic()            // columns 0..3 #000000, 4..7 #ffffff: red along row 16 never decreases, pixel 16 within 2 of 128
[Fact] public void Relief_NorthWestFacingSlope_BrighterThanFlat_SouthEastFacingDarker()
    // ground #808080, height = 0.3 * (column - row) rises east and falls north, so the slope faces north-west
    // (gradient magnitude 0.42, under SteepSlope): centre pixel red > 0x80. height = 0.3 * (row - column): red < 0x80.
[Fact] public void Relief_SteepSlope_AppliesSteepDarken()
    // ground #808080, height = 1.4142 * (column + row): gradient magnitude 2 facing south-west, perpendicular to the light,
    // so lit = 1 / sqrt(5). Centre pixel red equals round(255 * (128/255) * (1 + 0.5 * (lit - 1)) * 0.75) within 1.
[Fact] public void Water_DeepIsDeepColour_ShallowIsCloserToShallowColour() // every cell wet at surface 0: ground -10 gives exactly Water.Deep, ground -0.5 nearer Water.Shallow than Water.Deep
[Fact] public void Water_ShorelineOnWetPixelsBesideDry()             // columns 0..3 wet, 4..7 dry: wet pixel adjacent to the dry edge is Water.Shoreline, wet pixel 8 px from the edge is not
[Fact] public void Void_IsExactVoidColour_AndNeighbourReliefStaysFinite() // columns 0..3 void, 4..7 ground on a slope: void pixels equal VoidColor exactly, every ground pixel has R, G, B > 0
[Fact] public void ChunkBorders_MatchOneLargeRaster()
    // varied colours, heights and a water patch: each 8 m chunk (32 px) equals its quadrant of the 16 m chunk (64 px)
    // byte for byte, e.g. 8 m chunk (0,0) equals rows 32..63 and columns 0..31 of 16 m chunk (0,0)
[Fact] public void Rasterize_IsDeterministic()                        // two calls produce equal SHA256 of Rgba, Revision copied from source
```

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release --filter "FullyQualifiedName~RasterGroundTests"`
Expected: compile failure, `WorldMapRasterizer` does not exist.

- [ ] **Step 3: Implement `WorldMapRasterizer.Rasterize` for ground, relief and water**

Sample `ChunkRect(chunk).Expand(2 * source.SurfaceCellSize + style.Water.ShorelinePixels / style.PixelsPerMetre)` once per chunk. The algorithm the tests do not fully pin:

- **Sampling:** bilinear over the four cells around the pixel centre, cell centres at `origin + (i + 0.5, j + 0.5) * cellSize`, dropping void cells and renormalizing weights. A pixel whose nearest cell is void is `VoidColor` and gets no relief or water.
- **Relief:** `g = ((h(x + c) - h(x - c)) / 2c, (h(y + c) - h(y - c)) / 2c)` with `c = cellSize` and bilinear heights (a void sample uses the pixel's own height). `n = normalize(-g.X, -g.Y, 1)`. `L = (cos(alt) sin(az), cos(alt) cos(az), sin(alt))` with azimuth clockwise from north. `lit = max(0, dot(n, L)) / sin(alt)` so flat ground is 1. `factor = 1 + Strength * (lit - 1)`, then `factor *= SteepDarken` when `|g| > SteepSlope`. Multiply RGB by `factor`.
- **Water:** wetness is the renormalized bilinear blend of 1 for wet cells and 0 for dry, and a pixel is wet when wetness is at least 0.5. Its surface is the renormalized blend over wet cells only. `t = Clamp01((surface - groundHeight) / DeepDepthMetres)`, colour `Color.Lerp(Shallow, Deep, t)`, no relief on water. A wet pixel with a dry, non-void pixel within `ShorelinePixels` (Chebyshev distance, evaluated across the chunk edge using the margin) becomes `Shoreline`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release --filter "FullyQualifiedName~RasterGroundTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.WorldMap KhaozEngine.WorldMap.Tests
git commit -m "feat(worldmap): rasterize ground colour, relief and water per chunk"
```

---

### Task 3: Rasterizer footprints and the golden

**Files:**
- Create: `KhaozEngine.WorldMap/Raster/FootprintPainter.cs`, `KhaozEngine.WorldMap/Raster/CoverageSampler.cs`, `KhaozEngine.WorldMap/WorldMapSymbolResolver.cs`
- Modify: `KhaozEngine.WorldMap/WorldMapRasterizer.cs` (paint footprints after water)
- Create: `KhaozEngine.WorldMap.Tests/RasterFootprintTests.cs`, `KhaozEngine.WorldMap.Tests/SymbolResolverTests.cs`, `KhaozEngine.WorldMap.Tests/GoldenChunkTests.cs`, `KhaozEngine.WorldMap.Tests/Golden/worldmap-fixture.png`

**Interfaces:**
- Consumes: Task 1 contracts, Task 2 `RasterCanvas` and `MapPoint`.
- Produces:

```csharp
public static class WorldMapSymbolResolver
{
    public static bool Matches(WorldMapFootprintMatch match, WorldMapFootprint footprint); // every non-null field must match, AllTags needs every tag (ordinal)
    public static WorldMapSymbol SymbolFor(WorldMapStyle style, WorldMapFootprint footprint); // first matching rule, else FallbackBlocking when Blocking, else Hidden
}
```

- [ ] **Step 1: Write the failing tests**

`SymbolResolverTests`: `FirstMatchingRuleWins`, `AllTags_RequiresEveryTag` (rule `["npc", "shopkeeper"]` rejects a footprint tagged only `npc`), `NoMatch_BlockingFallsBackToObstacle_NonBlockingIsHidden`, `IsRoofAndCollisionFieldsFilter`.

`RasterFootprintTests` (flat ground #808080, 8 m chunks at 4 px per metre, one rule per symbol kind with distinct fill and outline colours):

```csharp
[Fact] public void Building_QuarterTurn_SwapsExtents()            // half extents (2, 1), rotation PI/2 at (4, 4): pixels in fill or outline colour span 8 columns and 16 rows (shadow pixels excluded)
[Fact] public void Building_OutlineInside_ShadowSouthEast_NoneNorthWest()
    // pixel just inside the west edge is the outline colour, interior pixel is fill,
    // pixel 1 px beyond the south-east corner is darker than ground, pixel 1 px beyond the north-west corner equals ground
[Fact] public void Tree_IsDisc_FootprintCornersStayGround()      // 2 m square tree: centre pixel is tree fill, corner pixels of its square equal ground
[Fact] public void Hidden_LeavesGroundUntouched()                 // chunk bytes equal the bytes with no footprints
[Fact] public void DrawOrder_TreeOverBuildingOverObstacle()       // three overlapping footprints at one point: that pixel is tree fill
[Fact] public void Wall_IsLineAlongLongAxis()                     // half extents (2, 0.5) at rotation 0: pixel on the centre line is wall fill, pixel 0.4 m north of the line is ground
[Fact] public void Footprint_StraddlingChunkBorder_HasNoSeam()   // building centred on x = 8 with 8 m chunks: chunks (0,0) and (1,0) equal the matching halves of one 16 m chunk
```

`GoldenChunkTests.FixtureChunk_MatchesGolden`: one 32 m chunk at 4 px per metre with two ground materials, a hill, a pond, two buildings (one rotated), four trees, a wall and a bridge. Compare with `Golden/worldmap-fixture.png` (path from `[CallerFilePath]`) at a tolerance of 2 per channel. When `KE_UPDATE_GOLDENS=1`, write the PNG with `PngWriter.Encode` instead and pass. Decode with `PngReader.Decode`.

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release --filter "FullyQualifiedName~SymbolResolverTests|FullyQualifiedName~RasterFootprintTests"`
Expected: FAIL (types missing, then footprints not drawn).

- [ ] **Step 3: Implement the resolver and `FootprintPainter`**

- **Coverage:** 4 by 4 subsamples per pixel at offsets `(k + 0.5) / 4`. Coverage is the inside fraction. Only pixels inside the shape's bounding box are tested.
- **Shapes:** `Building` and `Bridge` are the oriented rectangle. `Tree` and `Obstacle` are the ellipse inscribed in it. `Wall` is a rectangle `WallWidthMetres` wide along the longer local axis (local x on a tie) and as long as that axis.
- **Outline:** fill coverage is the shape inset by `OutlinePixels / PixelsPerMetre`, and outline coverage is the full shape minus the inset. `Wall` has no outline.
- **Order:** every `Building` shadow first (the shape moved by `BuildingShadowOffsetMetres`, colour `BuildingShadow`), then fills and outlines by kind in `WorldMapSymbolKind` declaration order, ascending `Id` within a kind.
- **Blend:** `dst = dst * (1 - a * cov) + src * (a * cov)`. Output alpha stays 255.
- Footprints come from `source.CollectFootprints` over the chunk rect expanded by `BuildingShadowOffsetMetres.Length()` plus one pixel.

- [ ] **Step 4: Bake the golden and inspect it**

Run: `KE_UPDATE_GOLDENS=1 build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release --filter "FullyQualifiedName~GoldenChunkTests"`
Then open `KhaozEngine.WorldMap.Tests/Golden/worldmap-fixture.png` and check north is up, the rotated building is turned, shadows fall south-east, trees sit on top and the pond has a shoreline. Attach the PNG to the task report.

- [ ] **Step 5: Run all package tests**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release`
Expected: PASS, golden compared without the update variable.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.WorldMap KhaozEngine.WorldMap.Tests
git commit -m "feat(worldmap): draw footprint symbols and pin a fixture golden"
```

---

### Task 4: Feature anchors for icons and labels

**Files:**
- Create: `KhaozEngine.WorldMap/WorldMapFeatures.cs`, `KhaozEngine.WorldMap/WorldMapFeatureCollector.cs`
- Create: `KhaozEngine.WorldMap.Tests/FeatureCollectorTests.cs`

**Interfaces:**
- Consumes: Task 1 contracts, Task 3 `WorldMapSymbolResolver` tag matching semantics (ordinal, all-of).
- Produces:

```csharp
public sealed record WorldMapIconAnchor(string IconKey, Vector2 Position, int Priority, float MergeRadiusPixels);
public sealed record WorldMapLabelAnchor(string MarkerName, Vector2 Position, WorldMapLabelTier Tier);
public sealed record WorldMapChunkFeatures(IReadOnlyList<WorldMapIconAnchor> Icons, IReadOnlyList<WorldMapLabelAnchor> Labels)
{
    public static WorldMapChunkFeatures Empty { get; }
}
public static class WorldMapFeatureCollector
{
    public static WorldMapChunkFeatures Collect(IWorldMapSource source, WorldMapStyle style, WorldMapChunkCoord chunk);
}
```

Rules: a feature belongs to the chunk whose rect contains its anchor (footprint `Center`, marker `Position`), so no anchor appears in two chunks. Each feature takes the first icon rule whose `Source` and `AllTags` match. A marker carrying `Labels.LabelTag` becomes a label with the first tier whose `Tag` it carries, and is skipped when it carries none. Output is sorted by `Position.Y` descending, then `Position.X`, then key.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void FootprintIcon_AnchoredAtCentre_OnlyInOwningChunk()   // bank footprint centred at (63.5, 10) appears in chunk (0,0) only, though its shape crosses into (1,0)
[Fact] public void MarkerIcon_RequiresAllTags()                         // rule ["npc", "shopkeeper"]: marker with both gets the icon, marker with only "npc" does not
[Fact] public void OneIconPerFeature_FirstRuleWins()
[Fact] public void Label_TakesTierFromTag()                             // marker tags ["map-label", "map-label:town"] gives the town tier and MarkerName
[Fact] public void Label_WithoutTierTag_IsSkipped()
[Fact] public void NoLabelRules_NoLabels()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release --filter "FullyQualifiedName~FeatureCollectorTests"`
Expected: compile failure.

- [ ] **Step 3: Implement `WorldMapFeatureCollector.Collect`**

Query footprints and markers over the chunk rect, keep those whose anchor the chunk contains.

- [ ] **Step 4: Run the tests to verify they pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.WorldMap KhaozEngine.WorldMap.Tests
git commit -m "feat(worldmap): collect icon and label anchors per chunk"
```

---

### Task 5: Chunk cache and background worker

**Files:**
- Create: `KhaozEngine.WorldMap/WorldMapChunk.cs`, `KhaozEngine.WorldMap/WorldMapChunkCache.cs`, `KhaozEngine.WorldMap/WorldMapChunkWorker.cs`
- Create: `KhaozEngine.WorldMap.Tests/ChunkCacheTests.cs`, `KhaozEngine.WorldMap.Tests/ChunkWorkerTests.cs`

**Interfaces:**
- Consumes: `WorldMapRasterizer.Rasterize` (Task 2), `WorldMapFeatureCollector.Collect` (Task 4).
- Produces:

```csharp
public sealed record WorldMapChunk(WorldMapChunkImage Image, WorldMapChunkFeatures Features, int Generation);
public sealed class WorldMapChunkCache : IDisposable
{
    public WorldMapChunkCache(IWorldMapSource source, WorldMapStyle style); // subscribes to source.Changed -> Invalidate
    public IWorldMapSource Source { get; }
    public WorldMapStyle Style { get; }
    public void Request(WorldMapChunkCoord chunk, float priority);   // lower builds sooner, latest priority wins, ignored when current, failed or outside Source.Bounds
    public bool TryGet(WorldMapChunkCoord chunk, [NotNullWhen(true)] out WorldMapChunk? built); // latest built, possibly stale
    public bool IsCurrent(WorldMapChunkCoord chunk);                 // built and built.Generation == current generation
    public bool IsFailed(WorldMapChunkCoord chunk);
    public void Invalidate(MapRect area);                            // bumps the generation of every chunk touching area expanded by 2 * SurfaceCellSize + BuildingShadowOffsetMetres.Length(), clears failure
    public bool BuildNext();                                         // builds the lowest-priority pending chunk on the calling thread, false when none
    internal event Action? WorkQueued;                               // raised by Request and Invalidate for the worker
    public void Dispose();                                           // unsubscribes from Changed
}
public sealed class WorldMapChunkWorker : IDisposable
{
    public WorldMapChunkWorker(WorldMapChunkCache cache);            // one background thread, IsBackground = true
    public void Dispose();                                           // stops, wakes and joins the thread
}
```

Bookkeeping sits under one lock and the build runs outside it. A build captures the chunk's generation at start and publishes `WorldMapChunk` with that generation. A source exception marks the chunk failed, drops it from pending and is reported through `System.Diagnostics.Trace` with the chunk coordinate. It is not retried until an invalidation touches it.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Request_ThenBuildNext_PublishesCurrentChunk()
[Fact] public void BuildNext_TakesLowestPriorityFirst()                // request (1,0) at 5 and (0,0) at 1: first BuildNext makes (0,0) current only
[Fact] public void Request_OutsideBounds_IsIgnored()                   // BuildNext returns false
[Fact] public void Invalidate_MarksOnlyTouchedChunksPlusMargin()       // 3 by 1 chunks built, invalidate a 1 m rect in the middle of (1,0): (1,0) stale, (0,0) and (2,0) current
[Fact] public void Invalidate_AtChunkEdge_ReachesNeighbourThroughMargin() // 1 m rect touching the east edge of (0,0): (1,0) also stale
[Fact] public void Invalidate_DuringBuild_LeavesChunkStale()
    // BeforeSample blocks on a ManualResetEventSlim, BuildNext runs on Task.Run, Invalidate is called, the gate opens:
    // TryGet returns the chunk and IsCurrent is false
[Fact] public void SourceChanged_InvalidatesViaSubscription_AndDisposeUnsubscribes()
[Fact] public void SourceThrows_ChunkFailed_NotRetriedUntilInvalidated()
[Fact] public void StaleChunk_StaysReadableUntilRebuilt()               // TryGet returns the old chunk after Invalidate, the new generation after BuildNext
```

`ChunkWorkerTests.Worker_BuildsRequestedChunks`: request four chunks, `SpinWait.SpinUntil(() => all current, TimeSpan.FromSeconds(10))` is true. `ChunkWorkerTests.Worker_SurvivesThrowingSource`: the first chunk throws, the second still becomes current. `Dispose_StopsTheThread`: dispose returns within 5 seconds.

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Tests/KhaozEngine.WorldMap.Tests.csproj -c Release --filter "FullyQualifiedName~ChunkCacheTests|FullyQualifiedName~ChunkWorkerTests"`
Expected: compile failure.

- [ ] **Step 3: Implement the cache and worker**

The worker waits on an `AutoResetEvent` set by `WorkQueued`, drains `BuildNext` until it returns false, then waits again.

- [ ] **Step 4: Run the tests to verify they pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.WorldMap KhaozEngine.WorldMap.Tests
git commit -m "feat(worldmap): cache chunks by generation and build them in the background"
```

---

### Task 6: `KhaozEngine.TileWorld.WorldMap` adapter

**Files:**
- Create: `KhaozEngine.TileWorld.WorldMap/KhaozEngine.TileWorld.WorldMap.csproj`, `README.md`, `TileWorldMapSource.cs`, `TileWorldMapSurface.cs` (surface sampling and the per-region water lookup), `TileWorldMapFootprints.cs`
- Create: `KhaozEngine.TileWorld.WorldMap.Tests/KhaozEngine.TileWorld.WorldMap.Tests.csproj`, `TileWorldMapSourceTests.cs`, `TileWorldMapOrientationTests.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.cs:35` (`"TileWorld.WorldMap"`), `KhaozEngine.Tests/ArchitectureTests.WorldMap.cs`, `KhaozEngine.slnx`, `README.md`

**Interfaces:**
- Consumes: Task 1 contracts, Task 2 rasterizer (orientation test). From `KhaozEngine.TileWorld`: `TileWorldDocument.Regions`, `RegionsTouching(TileRect)`, `ObjectsIn(TileRect, int?)`, `GetUnderlay`, `GetOverlay`, `CornerHeight(x, z, plane)` (metres, SW corner), `TileRegion.Size` (64) and `Markers`, `RegionCoord.OriginX/OriginZ`, `TileWorldCatalogs.Material(ushort)`, `Archetype(string?)`, `Archetypes`, `GroundMaterial.Color` (hex string), `TileCollisionKind`, `TileFootprint.Of(a, x, z, rotation)`, `TileWaterBodies.IsWater(...)` and `Collect(document, catalogs, region, plane)` (`TileWaterBody.SurfaceY`), `TileWorldSpace.TileX/TileZ/ToWorld`.
- Produces (namespace `KhaozEngine.TileWorld.WorldMap`):

```csharp
public sealed class TileWorldMapSource : IWorldMapSource
{
    public TileWorldMapSource(TileWorldDocument document, TileWorldCatalogs catalogs, int surfacePlane = 0);
    // Revision is 0 and Changed is never raised (explicit empty add and remove accessors, the document is static in a client)
}
```

Mapping: map `(x, y)` is tile `(x, z)` times `TileSize`. Bounds are the union of loaded region rects. `SurfaceCellSize` is `TileSize`. Per tile: no region or no underlay material means void, colour is the underlay material's colour replaced by the overlay material's colour when the overlay is non-zero and resolves (overlay shapes are ignored, so an overlay covers its whole tile), height is the mean of the four corner heights on the surface plane, water surface is the `SurfaceY` of the collected body covering a tile for which `IsWater` is true. Water bodies are collected lazily per region in a `ConcurrentDictionary`, and parsed colours are cached per material id. Footprints come from `ObjectsIn` over the area's tile rect expanded by the largest archetype `Max(SizeX, SizeZ)` (computed once), keeping objects on the surface plane or whose archetype `IsRoof`, and keeping only footprints whose map rect intersects the area. A footprint's `Center` is its `TileFootprint.Of` rect centre, `HalfExtents` are the unrotated `(SizeX, SizeZ) * TileSize / 2`, `Rotation` is `Rotation * PI / 2`, `Tags` are archetype tags then object tags without duplicates, `Collision` maps `None` to `None`, `Solid` to `Solid`, and `Wall`, `WallCorner`, `Diagonal` to `Wall`. Markers on the surface plane come from the regions touching the area, positioned at tile centres. `ToMap` goes through `TileWorldSpace.TileX/TileZ`. `ToMapHeading(f)` is `Atan2(east, north)` with east `f.X` and north `-f.Z`.

- [ ] **Step 1: Create the package and test project**

The package references `../KhaozEngine.WorldMap/KhaozEngine.WorldMap.csproj` and `../KhaozEngine.TileWorld/KhaozEngine.TileWorld.csproj`. The test project references only the package. Fixtures use `TileWorldCatalogs.Greybox()` merged with `TileWorldCatalogs.LoadJson(json, "worldmap-tests")` for test archetypes, and `new TileWorldDocument { Id = "w", DisplayName = "W" }` with `GetOrCreateRegion`, following `KhaozEngine.TileWorld.Tests/TileWorld/TileWorldTestData.cs:30-46`.

- [ ] **Step 2: Write the failing tests**

`TileWorldMapSourceTests`:

```csharp
[Fact] public void Bounds_UnionOfRegions_IncludesNegativeRegions()        // regions (-1,0) and (0,0): new MapRect(-64, 0, 128, 64)
[Fact] public void Surface_UnderlayColour_OverlayReplaces_VoidOutsideRegions()
[Fact] public void Surface_HeightIsMeanOfFourCorners()
[Fact] public void Surface_WaterTileReportsBodySurface_DryTileIsNaN()
[Fact] public void Footprints_RoofOnUpperPlaneIncluded_OtherUpperPlaneObjectsExcluded()
[Fact] public void Footprints_LargeObjectAnchoredOutsideArea_IsIncluded()  // 6x6 roof anchored at tile (60, 0), area is chunk (1, 0)
[Fact] public void Footprints_QuarterTurn_CenterAndExtentsAgreeWithTileFootprint()
[Fact] public void Footprints_CollisionKindsMap()
[Fact] public void Footprints_TagsCombineArchetypeThenObject()
[Fact] public void Markers_AtTileCentre_WithTags()
```

`TileWorldMapOrientationTests`:

```csharp
[Fact] public void ToMap_NorthIsPositiveY_EastIsPositiveX()
    // TileWorldSpace.ToWorld(10.5f, 0f, 20.5f, 1f) maps to (10.5, 20.5)
[Fact] public void ToMapHeading_WorldMinusZIsNorth_PlusXIsEast()
    // (0,0,-1) gives 0, (1,0,0) gives PI/2, (0,0,1) gives PI in magnitude
[Fact] public void Rasterized_ObjectFurtherNorthIsHigher_FurtherEastIsRighter()
    // two 1x1 Solid objects at tiles (10, 40) and (30, 10) in one 64 m chunk with the default style:
    // the first object's fallback pixels have a smaller row and smaller column than the second's
```

- [ ] **Step 3: Run them to verify they fail**

Run: `build-slot --label wm -- dotnet test KhaozEngine.TileWorld.WorldMap.Tests/KhaozEngine.TileWorld.WorldMap.Tests.csproj -c Release`
Expected: compile failure.

- [ ] **Step 4: Implement `TileWorldMapSource`**

- [ ] **Step 5: Run the package tests and the architecture facts**

Run: `build-slot --label wm -- dotnet test KhaozEngine.TileWorld.WorldMap.Tests/KhaozEngine.TileWorld.WorldMap.Tests.csproj -c Release`
Run: `build-slot --label wm -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests"`
Expected: PASS. New facts: `TileWorldWorldMap_ReferencesOnlyWorldMapAndTileWorld`, `TileWorldWorldMap_IsAnExplicitReferenceOutsideEveryUmbrella`, `TileWorldWorldMapTests_ReferenceOnlyThePackage`.

- [ ] **Step 6: README, catalog row and guards**

Root row: `| **KhaozEngine.TileWorld.WorldMap** | The world map source for a TileWorld document (opt-in, NOT in any umbrella). Ground and overlay colours, corner heights, water bodies, archetype footprints and markers in north-up map space. | WorldMap, TileWorld |`. Package README covers the mapping rules above.

Run: `bash scripts/check-doc-versions.sh`
Expected: pass.

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.TileWorld.WorldMap KhaozEngine.TileWorld.WorldMap.Tests KhaozEngine.Tests KhaozEngine.slnx README.md
git commit -m "feat(worldmap): adapt TileWorld documents to the world map source"
```

---

### Task 7: `KhaozEngine.WorldMap.Gui` view, navigation and chunk textures

**Files:**
- Create: `KhaozEngine.WorldMap.Gui/KhaozEngine.WorldMap.Gui.csproj`, `README.md`, `WorldMapView.cs`, `WorldMapViewCore.cs`, `ChunkTextureStore.cs`, `WorldMapDrawList.cs`
- Create: `KhaozEngine.WorldMap.Gui.Tests/KhaozEngine.WorldMap.Gui.Tests.csproj`, `FlatTestSource.cs`, `TestInput.cs`, `WorldMapViewNavigationTests.cs`, `WorldMapViewChunkTests.cs`
- Modify: `KhaozEngine.Tests/ArchitectureTests.cs:35` (`"WorldMap.Gui"`), `KhaozEngine.Tests/ArchitectureTests.WorldMap.cs`, `KhaozEngine.slnx`, `README.md`

**Interfaces:**
- Consumes: `WorldMapChunkCache` (Task 5), `WorldMapChunking` (Task 1). From Gui and Render2D: `PannableCanvas` (`Viewport`, `ContentBounds`, `MinZoom`, `MaxZoom`, `Wheel = CanvasWheelMode.Zoom`, `Camera.Zoom`, `WorldToScreen`, `ScreenToWorld`, `CenterOn`, `Focus`, `Update(Pointer, float)`), `KhaozEngine.Windowing.Pointer`, `SpriteBatch.Draw(Texture2D, Rect, Vector4 srcUv, Color)`, `PrimitiveRenderer.DrawFilledRect`, `Render2DTextures.White(Render2DSurface)`, `Render2DSurface.CreateTexture(byte[], int, int)`.
- Produces (namespace `KhaozEngine.WorldMap.Gui`):

```csharp
public sealed class WorldMapView : IDisposable
{
    public WorldMapView(WorldMapChunkCache cache, Render2DSurface surface);
    public Rect Viewport { get; set; }                 // same space as the Pointer and the caller's open batch
    public float MaxPixelsPerMetre { get; set; }       // 8
    public int UploadBudgetPerFrame { get; set; }      // 4
    public Color PlaceholderColor { get; set; }        // cache.Style.VoidColor
    public float PixelsPerMetre { get; }               // current zoom
    public Vector2 MapToScreen(Vector2 mapPosition);
    public Vector2 ScreenToMap(Vector2 screenPosition);
    public void CenterOn(Vector2 mapPosition);
    public void ZoomBy(float factor);                  // about the viewport centre, clamped
    public void FitWorld();
    public void Update(Pointer pointer, float wheelDelta);
    public void Draw(SpriteBatch batch);               // into the caller's open batch, no Begin or End
    public void Dispose();                             // disposes every texture it created
}
internal sealed class WorldMapViewCore<TTexture> where TTexture : class
{
    internal WorldMapViewCore(WorldMapChunkCache cache, Func<byte[], int, int, TTexture> createTexture, Action<TTexture> disposeTexture);
    // every WorldMapView member except Draw and the surface, plus:
    internal IReadOnlyList<WorldMapDrawItem<TTexture>> DrawList { get; } // rebuilt by Update
}
internal readonly record struct WorldMapDrawItem<TTexture>(TTexture? Texture, Rect Destination, Vector4 SourceUv); // null texture means placeholder
```

Canvas space is `(mapX, -mapY)` in metres, so `Camera.Zoom` is screen pixels per metre. `ContentBounds` is the source bounds in canvas space. `MinZoom` is the whole-world fit `Min(Viewport.Width / Bounds.Width, Viewport.Height / Bounds.Height)`, capped at `MaxPixelsPerMetre`. The first `Update` with a usable viewport calls `FitWorld` unless `CenterOn` or `ZoomBy` already positioned the view. `Update` with a viewport under 1 pixel on either axis does nothing. Otherwise it updates the canvas, requests every chunk touching the visible map rect with priority equal to the distance from chunk centre to view centre, uploads up to `UploadBudgetPerFrame` chunks whose built generation differs from the uploaded one (nearest first, disposing the replaced texture), and rebuilds the draw list: one item per visible chunk, destination clipped to `Viewport` with the source UV shrunk to match, texture null when nothing has been uploaded for it. `Draw` draws texture items with `Color.White` and placeholder items with `PrimitiveRenderer.DrawFilledRect` in `PlaceholderColor`.

- [ ] **Step 1: Create the package and test project**

The package references `../KhaozEngine.WorldMap/KhaozEngine.WorldMap.csproj` and `../KhaozEngine.Gui/KhaozEngine.Gui.csproj`. The test project references only the package. `FlatTestSource` is a uniform-colour `IWorldMapSource` over a configurable `MapRect`. `TestInput` builds `InputState` frames for `Pointer.Update` following `KhaozEngine.Gui.Tests/Gui/ToastViewTests.cs:133`. A `FakeTexture` record counts creations and disposals.

- [ ] **Step 2: Write the failing tests**

All use a viewport at `new Rect(100, 50, 400, 300)`, a 256 m by 192 m source and 64 m chunks.

`WorldMapViewNavigationTests`:

```csharp
[Fact] public void MapToScreen_NorthUpEastRight_IncludesViewportOffset() // after CenterOn((128, 96)): that point maps to (300, 200), (138, 96) maps right of it, (128, 106) maps above it
[Fact] public void ScreenToMap_InvertsMapToScreen()
[Fact] public void Zoom_ClampsBetweenWorldFitAndMaxPixelsPerMetre()      // 50 wheel-ins: PixelsPerMetre == 8, 50 wheel-outs: == Min(400/256, 300/192)
[Fact] public void Wheel_KeepsMapPointUnderCursor()                     // map point under (180, 120) moves less than 0.01 m after one wheel step
[Fact] public void Drag_PansTheMap()
[Fact] public void ZeroSizeViewport_RequestsNothing_DoesNotThrow()
[Fact] public void FirstUpdate_FitsWorld_UnlessCenteredFirst()
```

`WorldMapViewChunkTests`:

```csharp
[Fact] public void Update_RequestsVisibleChunks_CentreChunkBuildsFirst()   // zoom 4 centred on (96, 96): first cache.BuildNext() makes chunk (1,1) current
[Fact] public void Upload_RespectsBudgetPerFrame()                         // 12 visible chunks built: textures created 4, 8, 12 after three Updates
[Fact] public void StaleChunk_KeepsOldTexture_UntilReplacementUploads()    // invalidate, rebuild, Update: draw item texture swaps once and the old one is disposed
[Fact] public void DrawList_ClipsToViewport_AndShrinksUv()                 // a chunk half outside the west edge: destination X == 100 and SourceUv.X == 0.5 within 0.001
[Fact] public void MissingChunk_IsPlaceholderItem()
[Fact] public void Dispose_DisposesEveryCreatedTexture()
```

- [ ] **Step 3: Run them to verify they fail**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Gui.Tests/KhaozEngine.WorldMap.Gui.Tests.csproj -c Release`
Expected: compile failure.

- [ ] **Step 4: Implement `WorldMapViewCore<TTexture>`, `ChunkTextureStore<TTexture>` and `WorldMapView`**

`WorldMapView` wraps `WorldMapViewCore<Texture2D>` with `surface.CreateTexture` and `t.Dispose()`, and owns one white texture from `Render2DTextures.White(surface)` for `PrimitiveRenderer`.

- [ ] **Step 5: Run the package tests and the architecture facts**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Gui.Tests/KhaozEngine.WorldMap.Gui.Tests.csproj -c Release`
Run: `build-slot --label wm -- dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureTests"`
Expected: PASS. New facts: `WorldMapGui_ReferencesOnlyWorldMapAndGui`, `WorldMapGui_IsAnExplicitReferenceOutsideEveryUmbrella`, `WorldMapGuiTests_ReferenceOnlyThePackage`.

- [ ] **Step 6: README and catalog row**

Root row: `| **KhaozEngine.WorldMap.Gui** | The world map widget (opt-in, NOT in any umbrella). Pan and zoom over cached chunks on PannableCanvas, background builds, budgeted texture uploads, fixed-size icons, labels and live markers. | WorldMap, Gui |`.

Run: `bash scripts/check-doc-versions.sh`
Expected: pass.

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.WorldMap.Gui KhaozEngine.WorldMap.Gui.Tests KhaozEngine.Tests KhaozEngine.slnx README.md
git commit -m "feat(worldmap): add the map view with navigation and budgeted chunk uploads"
```

---

### Task 8: Overlays, decluttering, markers and hover

**Files:**
- Create: `KhaozEngine.WorldMap.Gui/WorldMapOverlays.cs` (public sprite, hover and marker types), `KhaozEngine.WorldMap.Gui/WorldMapOverlayLayout.cs`
- Modify: `KhaozEngine.WorldMap.Gui/WorldMapViewCore.cs`, `KhaozEngine.WorldMap.Gui/WorldMapView.cs`
- Create: `KhaozEngine.WorldMap.Gui.Tests/OverlayLayoutTests.cs`, `KhaozEngine.WorldMap.Gui.Tests/WorldMapViewOverlayTests.cs`

**Interfaces:**
- Consumes: `WorldMapChunk.Features` (Task 4 anchors through Task 5), `WorldMapViewCore` (Task 7).
- Produces:

```csharp
public readonly record struct WorldMapSprite(Texture2D Texture, Vector4 SourceUv, Vector2 Size); // Size in screen pixels, art authored pointing north
public enum WorldMapHoverKind { Icon, Label }
public sealed record WorldMapHover(WorldMapHoverKind Kind, string Key, Vector2 MapPosition);   // Key is the icon key or marker name
// WorldMapView gains:
public Func<string, WorldMapSprite?>? IconSprites { get; set; }  // null result hides that icon
public Func<string, string?>? LabelText { get; set; }            // marker name to resolved text, null hides
public SpriteFont? LabelFont { get; set; }
public Color LabelColor { get; set; }                            // #2b2117
public Color LabelShadowColor { get; set; }                      // #f3ead2
public void SetMarker(string id, Vector2 mapPosition, float heading, WorldMapSprite sprite);
public bool RemoveMarker(string id);
public WorldMapHover? Hovered { get; }                           // set by Update from the pointer position
internal static class WorldMapOverlayLayout
{
    internal static OverlayPlacement Place(
        IReadOnlyList<ScreenIcon> icons, IReadOnlyList<ScreenLabel> labels, Rect viewport, float pixelsPerMetre, Vector2 pointer);
}
internal readonly record struct ScreenIcon(string Key, Vector2 Position, Vector2 Size, int Priority, float MergeRadius, Vector2 MapPosition);
internal readonly record struct ScreenLabel(string MarkerName, string Text, Vector2 Position, Vector2 Size, WorldMapLabelTier Tier, Vector2 MapPosition);
internal sealed record OverlayPlacement(IReadOnlyList<ScreenIcon> Icons, IReadOnlyList<ScreenLabel> Labels, WorldMapHover? Hover);
```

Layout rules: labels outside their tier's `[MinPixelsPerMetre, MaxPixelsPerMetre)` or with null text are dropped. Icons are taken by priority descending, then key, then position. One is dropped when an accepted icon with the same key lies within its merge radius, when its rect overlaps an accepted icon's rect, or when its rect is not fully inside the viewport. Labels are centred on their anchor, taken by tier rank descending then marker name, and dropped when they overlap an accepted label or leave the viewport. Hover is the last-drawn icon whose rect contains the pointer, else the last label. Markers draw last at fixed size, rotated by `heading` (clockwise from north equals clockwise on a y-down screen), skipped when their centre is outside the viewport. Label size is `LabelFont.Measure(text) * tier.TextScale`, and the core takes a `Func<string, Vector2>` measurer so tests need no font. Draw order is label text with a one-pixel shadow, then icons, then markers.

- [ ] **Step 1: Write the failing tests**

`OverlayLayoutTests`:

```csharp
[Fact] public void SameKey_WithinMergeRadius_CollapsesToOne()
[Fact] public void SameKey_BeyondMergeRadius_BothShown()
[Fact] public void DifferentKeys_Overlapping_HigherPriorityWins()
[Fact] public void Icon_PartlyOutsideViewport_IsDropped()
[Fact] public void Label_OutsideTierZoomRange_IsDropped()
[Fact] public void Labels_Colliding_HigherRankWins()
[Fact] public void Hover_PrefersIcon_ThenLabel_ElseNull()
[Fact] public void EqualPriorities_PlaceDeterministically()      // shuffled input order gives the same placement
```

`WorldMapViewOverlayTests` (core with fake textures and a fixed measurer): `Icons_FromBuiltChunks_AppearAtMapToScreen`, `Labels_ResolveThroughLabelText_NullHides`, `Marker_SetMoveRemove_FollowsMapToScreen`, `Marker_OutsideViewport_IsSkipped`, `Hovered_ReportsIconUnderPointer`.

- [ ] **Step 2: Run them to verify they fail**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Gui.Tests/KhaozEngine.WorldMap.Gui.Tests.csproj -c Release --filter "FullyQualifiedName~Overlay"`
Expected: compile failure.

- [ ] **Step 3: Implement the layout, the core overlay pass and the `WorldMapView` overlay drawing**

Sprites draw with `SpriteBatch.Draw(Texture2D, Vector2 position, Vector2 size, Vector2 originNormalized, float rotation, Vector4 srcUV, Color)` with origin `(0.5, 0.5)`. Text uses `SpriteBatch.DrawString(font, text, position, color, scale)`.

- [ ] **Step 4: Run all view tests**

Run: `build-slot --label wm -- dotnet test KhaozEngine.WorldMap.Gui.Tests/KhaozEngine.WorldMap.Gui.Tests.csproj -c Release`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.WorldMap.Gui KhaozEngine.WorldMap.Gui.Tests
git commit -m "feat(worldmap): lay out icons, labels, markers and hover on the map view"
```

---

### Task 9: Docs, changelog, follow-up issue and the round finish

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md` (new `## World map` section after the Gui section, and the `MinimapComponent` line at 1325 removed from the `HudScreen` sample with one sentence after the sample pointing at the new section), `docs/DEPENDENCY-SEAMS.md` (a short "World map package edges" section listing the three edges and why each package is opt-in), `CHANGELOG.md` (bullets under `## 20.33.0`)

- [ ] **Step 1: Write the USING section**

Show the whole consumer flow in one code block: build `TileWorldMapSource`, a `WorldMapStyle` with a symbol rule and an icon rule, `WorldMapChunkCache`, `WorldMapChunkWorker`, `WorldMapView` with `IconSprites`, `LabelText` and `LabelFont`, then per frame `Viewport`, `SetMarker` from `ToMap` and `ToMapHeading`, `Update(pointer, input.ScrollDelta)` and `Draw(batch)` inside the open UI batch, and disposal order (view, worker, cache).

- [ ] **Step 2: Add the changelog bullets**

Three bullets under `## 20.33.0`, matching the existing wrap at about 120 columns: one per package, naming the main types and the opt-in status.

- [ ] **Step 3: File the MapDoc adapter issue**

Search first: `scripts/ledger.sh search worldmap`. Then file a `kind/backlog` issue with `confidence/authored` titled "World map source for native MapDoc worlds". The body names `IWorldMapSource`, what MapDoc must supply (surface grid, water from `MapWaterQuery`, placement footprints from `MapResolvedAsset` shapes, markers once R6 lands), and links https://github.com/APKiwiOrg/Grimhollow/issues/441 as the consumer trigger with `needs/upstream`. Add the issue number to the spec's Follow-ups line.

- [ ] **Step 4: Run the guards**

Run: `sh scripts/check-dashes.sh --tree && sh scripts/check-prose.sh --tree && sh scripts/check-file-size.sh --tree && sh scripts/check-agent-instructions.sh --tree && bash scripts/check-doc-versions.sh`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add docs CHANGELOG.md
git commit -m "docs(worldmap): document the world map packages for 20.33.0"
```

- [ ] **Step 6: Merge current main and run the round gates**

```bash
git fetch origin && git merge origin/main
mkdir -p local-feed
build-slot --heavy --label wm-full -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label wm-full -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
build-slot --label wm-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

Expected: build with 0 warnings, 0 test failures, format clean. Re-run Step 4 guards after the merge.

- [ ] **Step 7: Hosted CI on the branch**

```bash
git push origin feature/world-map
gh workflow run ci.yml --ref feature/world-map
gh run list --workflow ci.yml --branch feature/world-map --limit 1
```

Expected: the run for the pushed head finishes green.

- [ ] **Step 8: Integrate**

Fast-forward engine `main` to the branch head and push `main`. The tag is not part of this plan. Report the main commit to the controller so the owner can tag 20.33.0 from a KhaozEngine-rooted thread.
