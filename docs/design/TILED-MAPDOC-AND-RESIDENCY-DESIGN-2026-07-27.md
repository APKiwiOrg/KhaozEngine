# Tiled map document format and document residency (2026-07-27)

Status: design approved, implementation pending. Program issues:
[#334](https://github.com/APKiwiOrg/KhaozEngine/issues/334) (tiled format) and
[#335](https://github.com/APKiwiOrg/KhaozEngine/issues/335) (residency). Both are engine sub-projects of the
Ruinborne 100 km world program, [Ruinborne#242](https://github.com/APKiwiOrg/Ruinborne/issues/242).

Joint spec because the tile granularity decision is shared: the format's tile is the residency unit, and
deciding one without the other guarantees two grids.

Measurement evidence: `Ruinborne/docs/design/2026-07-26-world-scale-spike-findings.md` plus the CSV beside it,
measured on engine 16.3.1 against unmodified production code.

**Revised after an adversarial review, and three areas were redesigned rather than amended.** Section 7's save
ordering did not survive a crash at several points and is rebuilt around content-addressed tile names with the
manifest rename as the sole commit point. Section 11's residency ring was declared Euclidean, which at the
specced default guarantees zero coverage, and is now Chebyshev with every number re-derived. Section 10's
editor story carried two paths that destroyed a world on a save that reported success. The findings that were
CHECKED AND REFUTED are recorded in place rather than dropped, in section 9 (the architecture test's home) and
in section 11's ring table (the proposed Euclidean repair is itself wrong at radius 4), because a refuted
finding that leaves no trace gets re-raised by the next reader.

## 1. Problem

`MapDocumentFile.SaveText` returns one `JsonSerializer.Serialize` string (`MapDocumentFile.cs:136`). Every
public entry point on that class is whole-document (`Load`, `LoadText`, `Save`, `SaveText`,
`MapDocumentFile.cs:60-137`). There is no tiled, regional, or windowed read anywhere in the package.

What that costs, measured:

| Case | Sculpt tiles | Placements | Measure | Time | Allocated | Outcome |
|---|---|---|---|---|---|---|
| density-6400 | 6,400 | 492,800 | SaveText | 974 ms | 948 MB | ok |
| density-64000 | 64,000 | 4,928,000 | SaveText | 6,397 ms | 3.99 GB | `OutOfMemoryException` |
| extent-100000m | 24,398 | 1,878,646 | LoadText | 6,371 ms | 2.20 GB | ok, 420 MB retained |
| extent-100000m | 24,398 | 1,878,646 | WorldHash | 3,342 ms | 6.14 GB | ok |

The save ceiling sits between 6,400 and 64,000 authored sculpt tiles at a fixed extent. The findings doc reads
that as the .NET single-object ceiling on the one big string rather than a real memory wall, and calls it a
plausible inference rather than a confirmed mechanism. **The arithmetic confirms it, and this spec adds that
confirmation.** `MapDocumentFile.CreateOptions` sets `WriteIndented = true` on the write path
(`MapDocumentFile.cs:148`), and `MapTerrainOverridesConverter.Write` emits each of a tile's 1,024 deltas as its
own `WriteNumberValue` into that writer (`MapTerrainOverridesConverter.cs:118-119`), so every delta lands on its
own indented line. Check against the real file: Ruinborne's `island.map.json` carries 64 sculpt tiles (65,536
deltas) plus 4,921 placements and is 2,545,747 bytes, which is about 20 bytes per delta line and about 250 bytes
per placement. Scale that: 64,000 tiles is 65.5 M deltas at roughly 20 bytes, about 1.3 GB, plus 4.93 M
placements at roughly 250 bytes, about 1.2 GB. At extent-100000m the same arithmetic gives roughly 970 MB,
which is exactly the case that succeeded.

**The wall is a contiguous-buffer element count, not a memory wall, and that distinction is what makes it
structural.** `JsonSerializer.Serialize` writes UTF-8 into one pooled byte buffer that grows by doubling and
then transcodes the whole thing to a string, so the ceiling is `int.MaxValue` elements in a single array
(about 2.1 GB of UTF-8), and after that the roughly 2 GB cap on a single `string`. A 2.5 GB document clears
both. `gcAllowVeryLargeObjects` does not move either one: it raises the total byte size of an object, not the
element count, and it explicitly does not raise the string limit. The measured 3.99 GB **cumulative**
allocation corroborates the mechanism rather than contradicting it, because doubling growth allocates roughly
twice the final buffer on the way up, so a 3.99 GB cumulative figure is the signature of a final buffer around
2 GB. A bigger machine does not move any of this.

The other two costs are not ceilings but are paid by both heads at boot before a player sees anything: 6.4 s and
2.2 GB to parse the 100 km document, and a further full serialize plus roughly three more full-size buffer
copies to hash it. The engine has no document hash at all today. `RuinborneWorldIdentity.ComputeHash` takes the
canonical save text as a parameter, so the consumer pays `SaveText` purely to get bytes to hash.

Content count is the variable, not extent. The 100 km extent case passed while a 64,000 tile case in a five
times smaller world died. Analytic base plus sparse authored sculpt is what makes 100 km reachable, so this
design keeps that model. No baked per-tile heightmaps anywhere.

## 2. What exists to build on, verified

Every claim below was checked against the code on `main` at 16.5.0 rather than taken from the issues.

- `MapTerrainOverrides` is already tile-keyed and sparse: a `Dictionary<long, MapSculptTile>` on a packed
  `(tileX << 32) | tileZ` key with O(1) `TryGetTile` (`MapTerrainOverrides.cs:22`, `:80`, `:118`). The data shape
  is right, only the container is monolithic. Confirmed.
- Placements are a flat unkeyed `List<MapPlacement>` (`MapDocument.cs:25`) and `MapRuntime.BuildPlacements`
  walks all of them with no region filter (`MapRuntime.cs:162-173`). `Spawns`, `PlayerSpawns`, `Exclusions`,
  `ScatterOverrides` and `Regions` are flat too. Confirmed.
- The v1 to v2 migration hook is the precedent for a format bump: `MapDocumentLoadOptions` registers built-in
  steps in its constructor and the loader runs a contiguity-checked chain, stamping `formatVersion` itself
  (`MapDocumentFile.cs:25`, `:42-47`, `:96-104`). Confirmed.
- `ChunkCoord` and Sharding's `CellCoord` share the `floor(world / size)` convention, origin anchored
  (`ChunkCoord.cs:5-6`, `ChunkGrid.cs:13-14`, `CellCoord.FromWorld`). Confirmed.
- `PlacementBuckets` (`KhaozEngine.Terrain.Render3D`, internal) already buckets a placement list by chunk coord
  once at sink construction, using `ChunkGrid.CoordOf` so tiling reproduces the whole list exactly. That is the
  working precedent for the spatial index this spec makes public at the document layer.

Three things in the issues did not survive the check.

- **#334 says "sculpt tiles are 2 m cells today". That is Ruinborne's document, not the engine.**
  `MapTerrainOverrides.DefaultCellSize` is 0.5 m (`MapTerrainOverrides.cs:20`) and `TerrainSculpt.TileSize` is 32
  cells (`TerrainSculpt.cs:17`), so the engine default sculpt tile spans 16 m. Ruinborne's `island.map.json`
  declares `cellSize: 2`, giving a 64 m span, and the spike's `WorldSynth.SculptCellSize` is also 2. The
  consequence for this design is real: **the sculpt tile has no fixed size in meters**, so a document tile grid
  cannot be derived from it and must be declared independently. Section 3 does that.
- **#335 says the concrete enabler for residency is extracting the streamer core. Checked, the strict
  dependency is much smaller.** Document residency needs `ChunkRing` (the Gameplay/Decor vocabulary) and the
  `IChunkBuildDispatcher` seam, both trivially small. It does not need `TerrainStreamer`, `StreamerConfig`,
  `IChunkSink` or `ChunkBuildScheduler` (section 9 explains why reusing the streamer outright is wrong).
  `ChunkBuildScheduler<T>` in particular is keyed on `ChunkCoord` with `int lod` in its build signature
  (`ChunkBuildScheduler.cs:58`, `:89`), so reusing it would mean widening it to `ChunkBuildScheduler<TCoord, T>`,
  a public break that buys nothing. The extraction still ships, and section 8 justifies it on its own merits
  rather than on this one.
- **#335's grid-discipline note is right but understated.** The alignment that matters is the floor rule and the
  origin anchor, not a shared coordinate type. Sharing `ChunkCoord` between a 60 m chunk grid and a 512 m
  document grid would make `ChunkCoord(3, 4)` mean two different rects with no compile-time distinction.

## 3. The document tile grid

A document tile is a square of world XZ, edge length `tileSize` meters, declared once in the document.

```csharp
namespace KhaozEngine.MapDoc;

/// <summary>Integer index of a square document tile. (X, Z) maps to the world region whose -X/-Z corner is
/// (X * tileSize, Z * tileSize). Same floor(world / size) convention as ChunkCoord and Sharding's CellCoord,
/// deliberately a DISTINCT type so a chunk coord cannot be passed where a tile coord is meant.</summary>
public readonly record struct MapTileCoord(int X, int Z);

/// <summary>Inclusive rectangular range of document tiles.</summary>
public readonly record struct MapTileRect(MapTileCoord Min, MapTileCoord Max);

/// <summary>Document-tile grid math. Delegates to ChunkGrid so the floor rule has exactly one implementation.</summary>
public static class MapTileGrid
{
    public static MapTileCoord CoordOf(float worldX, float worldZ, float tileSize);
    public static RectArea AreaOf(MapTileCoord c, float tileSize);        // half-open [origin, origin + size)
    public static Vector2 CenterOf(MapTileCoord c, float tileSize);
    public static MapTileRect RectOf(RectArea area, float tileSize);      // inclusive tile range covering the rect
}
```

`MapTileGrid.CoordOf` calls `ChunkGrid.CoordOf` and wraps the result, so there is no second copy of the floor
math to drift. An architecture test fuzzes both over positive, negative and exactly-on-boundary positions and
asserts they agree.

**Default `tileSize` is 512 m**, exposed as `MapDocumentFile.DefaultTileSize`. Derivation, not taste: at
Ruinborne's authored density (77 placements and one 64 m sculpt tile per 64 m cell) a fully authored 512 m tile
holds 64 sculpt tiles and about 4,900 placements, which by the byte arithmetic in section 1 is about 2.5 MB,
the size of today's whole `island.map.json`. That file's measured `LoadText` is 37 ms. So the worst realistic
tile costs about 40 ms to parse, on a worker thread, with a dictionary insert on the frame thread. Smaller tiles
multiply file count without making any single load meaningfully cheaper. Larger tiles push a single load past
the frame budget the ring is trying to hide.

A document may declare any positive finite `tileSize`. The validator enforces `tileSize > 0`, finite, and
`tileSize >= TerrainSculpt.TileSize * sculptCellSize` (a document tile must be at least one sculpt tile wide,
otherwise the ownership rule below assigns sculpt tiles to document tiles that do not cover them).

**Sculpt tile ownership.** `tileSize` need not be an integer multiple of the sculpt span, because a legal v2
document can carry any `cellSize` (0.75 m is legal and 512 / 24 is not an integer). A sculpt tile is owned by
the document tile containing its **origin corner**, that is
`MapTileGrid.CoordOf(tileX * TerrainSculpt.TileSize * sculptCellSize, tileZ * ..., tileSize)`. Single owner, no
splitting of a delta array, deterministic for every cell size. A straddling sculpt tile therefore contributes
deltas slightly outside its owning document tile, which is harmless because `TerrainSculpt` composites by world
position and does not care which file a tile arrived in.

**The consequence for residency is a real inset, not a rounding detail, and the first draft of this spec waved
it away.** A resident document tile's low-X and low-Z edges are covered by sculpt owned by the neighbour on
that side. When the neighbour is not resident, the first `sculptSpan` metres of the resident tile carry no
authored deltas even though the tile itself is loaded, where `sculptSpan = TerrainSculpt.TileSize *
sculptCellSize`. At Ruinborne's `cellSize` 2 that is 64 m, at `cellSize` 0.75 it is 24 m, and the validator's
own floor (`tileSize >= sculptSpan`) permits it to be a whole tile wide. The earlier claim that this "sits well
inside the hysteresis band" was wrong twice over: hysteresis is an UNLOAD-side allowance and buys the load side
nothing, and at the legal extreme the inset is a full tile. So the inset is subtracted explicitly in two
places, section 11's `MapResidencyConfig.ValidateAgainst` (the startup check) and section 11's chunk build gate
(the per-chunk check), rather than being absorbed by a band that does not absorb it.

Rejected: keying document tiles on sculpt tile coordinates directly. It ties the document layout to a per-world
authoring choice, so two worlds with different `cellSize` get different file granularity for the same physical
area, and changing `cellSize` would rewrite every file.

## 4. Decision 1: on-disk layout

**A document is either a single file or a directory. Both are first class and both are supported forever.**

```
island.map/                        the tiled form: a directory
  map.json                         root manifest, and the ONLY file a save ever mutates
  tiles/
    s_0_0/                         shard dir, shard = tile >> 4 (arithmetic, floors for negatives)
      t_0_0.<64 hex>.json          content-addressed: the suffix IS the tile's canonical hash
      t_3_-2.<64 hex>.json
    s_-1_0/
      ...
```

- Shard directories cap a directory at 256 tile coordinates. They are a filesystem and git nicety, never a
  load unit. Nothing ever reads a shard.
- File names carry the signed tile coord verbatim (`t_-3_12.`) followed by that tile's full canonical SHA-256
  in lower hex. Section 7 derives why the name has to encode the content: it is what makes the manifest the
  only mutation, so a crash at any instant leaves a document that is entirely the old version or entirely the
  new one. Names never shorten the digest, because a truncated digest reintroduces the overwrite hazard at low
  probability, and a low-probability data-loss path inside a save routine is worse than a 77-character
  file name nobody types.
- **Every integer in a file name is formatted with `CultureInfo.InvariantCulture`, and no code path anywhere
  parses an integer back out of a file name.** The manifest is the sole authority on which tiles exist, what
  they hash to, and therefore what each one is called. Section 8 has the culture argument in full.
- JSONC is tolerated on read for the manifest and every tile file, through the same `Jsonc.ParseNode` the
  monolithic loader uses. The engine never writes JSONC, matching `Jsonc`'s own stated read-time-only policy.
- Tile files are written indented, because human-diffable git-committed content is a founding promise of this
  format. The hash (section 8) is over a separate compact canonicalization, so indentation never affects
  identity.

**Which form a document is in is decided by the path, not by a flag.** `MapDocumentFile.Load(path)` on a file
loads monolithic. On a directory it loads tiled and fails loudly if there is no `map.json`. A flag inside the
manifest declaring the form would be redundant with the path and could disagree with it.

```csharp
/// <summary>None means nothing exists at the path, so the path has NO form and one must be chosen explicitly.
/// It is a real member rather than a null return because "there is nothing here" is the case both data-loss
/// bugs in section 10 walked into.</summary>
public enum MapDocumentForm { None, Monolithic, Tiled }

/// <summary>Tiled for an existing directory, Monolithic for an existing file, None for a path that does not
/// exist. NEVER inspects the extension: Path.GetExtension("island.map") is ".map", not empty, so an
/// extension heuristic routes this spec's own example directory to a file write.</summary>
public static MapDocumentForm DetectForm(string path);   // no exception-driven branching for callers
```

Alternatives weighed:

- **Single file with an internal offset index.** Rejected. It does not fix the save side at all (still one
  serialize), JSON has no seekable framing, and any offset table is invalidated by a hand edit or a comment,
  which is exactly what a diffable dev-authored format invites.
- **Manifest plus per-sculpt-tile files.** Rejected. At Ruinborne's 64 m sculpt span, a 100 km world is up to
  2.4 M potential files and the measured realistic case is 24,398 occupied ones, each about 10 KB. That is a
  filesystem and git problem with no compensating benefit, since 10 KB is far below any parse cost worth
  streaming.
- **Manifest plus region files plus tiles inside a region.** Rejected. Two granularities means every consumer,
  every test and every residency decision has to say which one it means. One granularity that is both the file
  unit and the residency unit is the whole point.
- **Dropping the monolithic form.** Rejected. `KeDungeon` emits whole documents (`tools/KeDungeon/Program.cs:180`),
  every test fixture is a whole document, and a 4,921-placement island in one diffable file is genuinely better
  than the same content in four. Keeping it costs one branch in `Save` and one in `Load`.

## 5. Decision 2: what lives in the manifest, what lives in a tile

The rule: **globals and shapes stay in the manifest, point content buckets.**

`map.json` carries, in this order:

| Field | Why global |
|---|---|
| `$schema`, `formatVersion`, `id`, `displayName` | Document identity |
| `schemeVersion` | `MapDocumentHash.SchemeVersion` at the time the stored hashes were computed. Ships in release 1, see section 10 |
| `bounds` | One rect for the world |
| `tileSize`, `sculptCellSize` | Grid headers. A per-tile copy could disagree with its neighbours |
| `terrain` (seed, water level, biome bands, ordered features) | Analytic base, global by construction. `MapRuntime.BuildTerrainConfig` needs all of it at once and it is a few KB |
| `scatterLayers`, `companionLayers` | Layer definitions. `BuildScatterConfigs` builds every layer together and a companion layer resolves its host by name across the whole set |
| `exclusions`, `scatterOverrides`, `regions` | See below |
| `tiles` | The occupied-tile index: `[{ "x": i, "z": j, "hash": "..." }]`, ascending (z, then x) |

Each `tiles/s_*/t_*.<hash>.json` carries an optional `$schema` string plus exactly four lists, and nothing
else. `$schema` is a file-level annotation, not content: the writer emits it, the reader ignores it, and it
never enters the tile hash, exactly as `$schema` is excluded from the manifest hash in section 8. It is not a
member of `MapTileContent` for the same reason.

| Field | Bucketed by |
|---|---|
| `placements` | `MapTileGrid.CoordOf(p.X, p.Z, tileSize)` |
| `spawns` | `MapTileGrid.CoordOf(s.X, s.Z, tileSize)` |
| `playerSpawns` | `MapTileGrid.CoordOf(s.X, s.Z, tileSize)` |
| `sculpt` | the origin-corner rule from section 3. Same `{ tileX, tileZ, deltas[] }` shape as today's `terrainOverrides.tiles` entries |

Those four are exactly the lists that scale with authored content, which is what the measurement says: 1.88 M
placements and 24,398 sculpt tiles are the entire problem, and spawns and player spawns share the placements'
point-shaped nature so they cost nothing extra to bucket.

**Exclusions, scatter overrides and regions stay global**, decided rather than deferred. Three reasons, in
descending weight.

1. `MapScatterOverrideDoc` has first-match-wins-in-document-order semantics (`MapRuntime.cs:103-110`), which is
   a global ordering. Bucketing them either breaks that ordering or requires reassembling the global list on
   load, at which point the bucketing bought nothing.
2. `MapRuntime.BuildScatterConfig` needs the full exclusion and override set for a layer to build one
   `ScatterConfig` (`MapRuntime.cs:97-111`). A region-scoped scatter config is a different feature with its own
   determinism question and is not in scope here.
3. They are shapes with extent, so a region can legitimately span the world, and they are not the scaling
   variable. Ruinborne's island has zero of each. Even a heavily authored world has hundreds.

Rejected alternative: bucket shapes into every tile they overlap, with a dedup pass on load. Costs a
duplication rule, a dedup pass, and an ordering rule, and buys nothing measurable. If a world ever authors
enough exclusions to matter, that is a new issue with its own evidence.

The object model does not fork. `MapDocument` is unchanged in shape: a fully loaded tiled document is
indistinguishable from a monolithic one except that `MapDocument.Tiles` is non-null. Two serializations, one
model.

```csharp
public sealed class MapDocument
{
    // ... existing members unchanged ...

    /// <summary>Document tile edge in world meters. Added at format version 3. A v2 document migrates with
    /// MapDocumentFile.DefaultTileSize.</summary>
    public float TileSize { get; set; } = MapDocumentFile.DefaultTileSize;

    /// <summary>The tile index of a tiled document, null for a monolithic one. Present even when only part of
    /// the world is loaded, so a partial save can carry unloaded tiles through untouched.</summary>
    public MapTileIndex? Tiles { get; set; }
}

public readonly record struct MapTileEntry(MapTileCoord Coord, string Hash, bool Loaded);

public sealed class MapTileIndex
{
    public float TileSize { get; }
    public IReadOnlyList<MapTileEntry> Entries { get; }          // ascending (Z, then X)
    public int LoadedCount { get; }
    public bool TryGet(MapTileCoord coord, out MapTileEntry entry);
    public bool IsOccupied(MapTileCoord coord);

    /// <summary>MapDocumentHash.SchemeVersion the stored Hash values were computed under, read from the
    /// manifest. Ships in release 1 (section 10).</summary>
    public int SchemeVersion { get; }

    /// <summary>True when at least one indexed tile is NOT loaded, so this document is a WINDOW onto a larger
    /// world. Section 10's save guard is stated on this flag, and every save entry point checks it.</summary>
    public bool IsPartial { get; }

    /// <summary>The directory this index was read from, null for an index built in memory from a whole
    /// document. A partial document may only be written back here.</summary>
    public string? SourceDirectory { get; }
}
```

**`MapTileEntry` is a positional `readonly record struct`, so it is the wrong place to put anything that might
grow.** Adding a fourth positional member later changes the primary constructor AND the arity of the generated
`Deconstruct`, which breaks `var (coord, hash, loaded) = entry;` at the source level and breaks every compiled
caller at the binary level. `MapTileIndex` is a class, so it is where `SchemeVersion`, `IsPartial` and
`SourceDirectory` live and where anything else added later belongs. This is also why section 10 refuses to
defer `schemeVersion` to release 2.

## 6. Decision 3: spatial bucketing API

Two layers, because there are two questions. "Which content is near here in a document I already hold" is
answered in memory and works for both forms. "Which content is near here without holding the document" is
residency, section 9.

```csharp
namespace KhaozEngine.MapDoc;

/// <summary>Buckets a loaded document's point content by document tile, once. O(n) to build, O(k) per query.
/// Works on a monolithic and a tiled document alike, so the editor, the MCP tool and a small game keep a
/// whole-document workflow and still get region queries. The document-layer analogue of the internal
/// PlacementBuckets the chunk sink already uses.</summary>
public sealed class MapSpatialIndex
{
    public static MapSpatialIndex Build(MapDocument doc);

    public float TileSize { get; }

    /// <summary>The occupied tiles, ascending (Z, then X). ORDERED, and a list rather than a collection, because
    /// the monolithic OfWorld path composes the world hash straight off this: an insertion-ordered
    /// Dictionary.Keys would make the same world built in two authoring orders hash differently.</summary>
    public IReadOnlyList<MapTileCoord> OccupiedTiles { get; }

    public IReadOnlyList<MapPlacement> PlacementsIn(MapTileCoord tile);
    public IReadOnlyList<MapSpawn> SpawnsIn(MapTileCoord tile);
    public IReadOnlyList<MapPlayerSpawn> PlayerSpawnsIn(MapTileCoord tile);
    public IReadOnlyList<MapSculptTile> SculptTilesIn(MapTileCoord tile);

    // Rect forms append into a caller-owned list, so a per-frame query allocates nothing.
    public void PlacementsIn(RectArea area, List<MapPlacement> into);
    public void SpawnsIn(RectArea area, List<MapSpawn> into);
}
```

`MapRuntime.BuildPlacements` grows two region-scoped overloads beside the existing whole-document one, which is
untouched:

```csharp
public static class MapRuntime
{
    // Existing, byte-identical behaviour, still the whole document.
    public static IReadOnlyList<PropPlacement> BuildPlacements(MapDocument doc, TerrainField field);

    /// <summary>Authored placements whose (X, Z) falls in the half-open rect. A partition of rects covering the
    /// world reproduces the whole-document result exactly, including ground-snap, because snapping samples the
    /// deterministic field per placement and never depends on neighbours.</summary>
    public static IReadOnlyList<PropPlacement> BuildPlacements(MapDocument doc, TerrainField field, RectArea area);

    /// <summary>O(k) form over a prebuilt index.</summary>
    public static IReadOnlyList<PropPlacement> BuildPlacements(MapSpatialIndex index, TerrainField field, MapTileCoord tile);

    /// <summary>Allocation-free form: appends into a caller-owned list.</summary>
    public static void BuildPlacements(MapSpatialIndex index, TerrainField field, MapTileCoord tile, List<PropPlacement> into);
}
```

The rect is **half-open** on both axes, matching `ChunkGrid.AreaOf`'s streaming invariant. A placement exactly
on a rect's max edge belongs to the next rect. That is what makes a partition reproduce the whole, and it is the
property the determinism test asserts.

Rejected: a general quadtree or R-tree over the document. The content is point-shaped and the query is always
"a rect aligned to a grid we already defined", so a hash bucket on the grid key is O(1) with no balancing, no
rebuild policy and no tuning. A tree would be a strictly worse fit for a query shape we control.

## 7. Decision 4: a save path that never materializes one giant string

Three entry points, layered so the monolithic form gets the fix too.

```csharp
public static class MapDocumentFile
{
    public const int CurrentFormatVersion = 3;
    public const float DefaultTileSize = 512f;

    // Existing. Still one string. Kept because the editor tests and existing consumers use it for deep
    // comparison, now documented as "small documents only" and no longer the way to obtain bytes to hash.
    public static string SaveText(MapDocument doc, MapDocRegistry? registry = null);

    /// <summary>Serializes straight into the stream through a Utf8JsonWriter, so no intermediate string exists
    /// at any point. Peak managed memory is the writer's buffer, not the document's serialized size.</summary>
    public static void SaveTo(MapDocument doc, Stream stream, MapDocRegistry? registry = null);

    /// <summary>Unchanged signature, reimplemented over SaveTo. The monolithic write path stops building a
    /// multi-gigabyte string.</summary>
    public static void Save(MapDocument doc, string path, MapDocRegistry? registry = null);

    /// <summary>Writes the tiled form. Peak SERIALIZATION buffer is one tile.</summary>
    public static void SaveTiled(MapDocument doc, string directory, MapDocRegistry? registry = null,
                                 MapDocumentSaveOptions? save = null);

    /// <summary>Dispatches on DetectForm(path): Tiled to SaveTiled, Monolithic to Save, and None THROWS.
    /// What the editor and the MCP tool call, so a document saves back in the form it opened. It never
    /// invents a form for a path that does not exist, because there is no honest way to guess one.</summary>
    public static void SaveAuto(MapDocument doc, string path, MapDocRegistry? registry = null,
                                MapDocumentSaveOptions? save = null);

    /// <summary>Writes in an EXPLICITLY named form, whatever is or is not already at the path. This is what
    /// the two conversion verbs call. MapDocumentForm.None throws.</summary>
    public static void SaveAs(MapDocument doc, string path, MapDocumentForm form,
                              MapDocRegistry? registry = null, MapDocumentSaveOptions? save = null);
}

/// <summary>Write-side knobs. Separate from MapDocRegistry because it is policy, not content.</summary>
public sealed class MapDocumentSaveOptions
{
    /// <summary>Default Fast. See "Durability level promised" below.</summary>
    public MapSaveDurability Durability { get; set; } = MapSaveDurability.Fast;
}

public enum MapSaveDurability { Fast, PowerFail }
```

**No save entry point inspects a file extension, ever.** `Path.GetExtension("island.map")` is `".map"`, not
empty, so an "extension-less path means tiled" heuristic sends this spec's own `island.map/` example to a FILE
write named `island.map`. The form comes from what is on disk (`SaveAuto`) or from the caller
(`SaveAs`), and from nothing else.

**Every save entry point refuses a partial document.** `Save`, `SaveText`, `SaveTo`, `SaveAuto` and `SaveAs`
all throw `MapDocumentException` when `doc.Tiles is { IsPartial: true }`, and `SaveTiled` additionally throws
when `directory` is not `doc.Tiles.SourceDirectory`. The guard is stated on the DOCUMENT, not on one writer,
because the data-loss path is a windowed document reaching a whole-document writer: a monolithic write of a
window silently drops every unloaded tile and looks like a successful save. Section 10 has the editor-side
consequences.

`SaveTo` alone is the cheapest single fix in this spec and it lands first. It moves the monolithic ceiling off
the .NET per-object cap and onto disk, so the 64,000-tile case that OOM'd now writes a 2.5 GB file instead of
throwing. That is not a good outcome, it is just no longer a failure, and the tiled form is what makes it a good
outcome.

### `SaveTiled` write ordering

The first draft of this section was **wrong**, and wrong in the way a durability story usually is: it named
one ordering rule ("manifest last"), stated the crash outcome that rule would give, and did not check the
other steps against it. Three of them broke it. Step 3 moved new bytes onto a LIVE tile name, so a crash
before the manifest write left the old manifest indexing new content, and since load does not verify hashes
the document came back as a silent mix reporting the old world identity. Step 4 deleted files BEFORE the
manifest that still referenced them, so a crash there left a dangling index entry and the next load threw.
And the manifest write was itself a plain write, so a crash inside it truncated the one file that makes the
whole directory readable. "Manifest last" is necessary and nowhere near sufficient.

**The invariant.** At every instant, the bytes on disk describe exactly one document: either the complete
previous version or the complete new one, never a mixture. The manifest names tiles by coordinate, so if a
tile's file name is fixed per coordinate the manifest cannot tell old content from new, and any window in
which new bytes sit under an old manifest violates the invariant by construction. **Therefore the file name
must encode the version, and the canonical hash is already exactly that** (section 8 computes it during the
write and the manifest already stores it, so content-addressing costs no extra manifest field and no extra
work). Two files with the same name have the same bytes, so a tile write is idempotent and a rename can never
destroy anything a manifest needs.

1. **Validate.** `MapDocumentValidator.Validate` once over the whole document. Refusing to write an invalid
   document is existing behaviour (`MapDocumentFile.cs:133-135`) and does not change. Then the partial-document
   guards above, plus section 10's moved-content guard.
2. **Bucket and hash.** Bucket the four point-shaped lists into tiles and compute each loaded tile's canonical
   hash (section 8).
3. **Read the previous manifest.** This is the ONLY source of the previous occupied set and the previous
   per-tile hashes. Never a directory listing, never a parse of a file name. A missing or unparseable
   `map.json` means "no previous version": every tile is treated as new and the sweep in step 6 is SKIPPED,
   because a manifest that cannot be read cannot vouch for which files are garbage. A `map.json.tmp` found here
   is by definition a crashed write and is deleted unread.
4. **Write changed tiles, at names nothing points at yet.** For each loaded tile whose full hash differs from
   its previous-manifest entry, or that has no previous entry: open
   `tiles/s_<sx>_<sz>/t_<x>_<z>.<hash>.json.tmp`, stream the tile through a `Utf8JsonWriter` while a parallel
   compact canonical writer feeds an `IncrementalHash`, flush, close, then `File.Move(overwrite: true)` onto
   `t_<x>_<z>.<hash>.json`. Two serializations, zero buffering, one tile live at a time. `overwrite: true` is
   safe precisely because the name is the content: an existing target holds the identical bytes. **Tiles whose
   hash is unchanged are not written at all**, which is what makes a windowed save over a 38,416-tile world
   touch only what the author actually edited.
5. **Commit: rename the manifest.** Write the new `map.json.tmp` (freshly computed per-tile hashes, the new
   occupied set, `schemeVersion`), flush, close, then `File.Move("map.json.tmp", "map.json", overwrite: true)`.
   **That single rename is the document's commit point**, and a single-file rename over an existing file is
   atomic on APFS, ext4 and NTFS. Nothing before this step mutated anything live.
6. **Sweep, after the commit.** Delete every file under `tiles/` the NEW manifest does not name, and every
   stray `*.tmp`. That collects superseded generations, tiles that left the occupied set, and leftovers from an
   earlier crashed save, all in one pass with one rule. The sweep is best effort: a delete failure leaves inert
   garbage, never a broken document, so it is reported and does not throw.

Crash at any point, enumerated rather than asserted:

| Crash during | On-disk state | Loads as |
|---|---|---|
| 4, any tile | New files at names no manifest references | Previous version, intact |
| 5, before the rename | `map.json.tmp` truncated, `map.json` untouched | Previous version, intact |
| 5, the rename itself | Atomic: it happened or it did not | Either version, both complete |
| 6, mid-sweep | New manifest live and correct, some superseded files linger | New version, intact |

**Durability level promised: crash-consistent, not power-fail-consistent, by default.** `MapSaveDurability.Fast`
defends against a process kill, an unhandled exception, or an editor crash, which is what actually happens on a
dev box, and where the real durability story is the git commit that follows. It does NOT defend against a power
loss, because `rename` orders the directory entry and not the file contents, so a renamed-in tile can come back
with stale or zeroed blocks. `MapSaveDurability.PowerFail` opts in: every tile file and `map.json.tmp` gets
`FileStream.Flush(flushToDisk: true)` before its rename, and the containing directories are fsynced after the
renames via `RandomAccess.FlushToDisk` on a directory handle. That last part is honest about its platform
limits: Linux and macOS support a directory fsync, Windows has no equivalent primitive and NTFS orders metadata
through its own journal instead, so on Windows the level is per-file flush plus that caveat rather than a
stronger guarantee dressed up as one.

Rejected: **write to a sibling temp directory and rename the directory.** Directory rename over an existing
directory is not atomic on any of the three platforms, so it trades a real guarantee for the appearance of one.
Rejected: **a per-tile generation counter in the name.** Same crash properties as content addressing and the
same git churn, but it needs a new per-entry manifest field and the number carries no meaning a reader could
check. Rejected: **truncating the hash in the file name to 8 or 16 hex characters.** It reintroduces the
overwrite hazard at 2^-32 or 2^-64, and a low-probability data-loss path inside a save routine is a worse trade
than a long file name.

**The cost this accepts: git sees a rename, not a modify.** Editing one tile changes its file name, so
`git status` shows a delete plus an add unless rename detection fires. It does fire in practice (rename
detection is on by default in modern git and a one-placement edit leaves a ~99% similar file, far above the
50% threshold), and the founding promise in section 4 is that the CONTENT is readable indented JSON, which is
untouched. Stated here so nobody rediscovers it as a bug.

### Re-saving after a crashed save

Fully specified, because "the next successful save overwrites them" was hand-waving.

- **The previous occupied set and hashes** come from `map.json` (step 3), never from enumerating the directory
  and never from parsing coordinates out of file names. This is also what keeps the culture hazard of section 8
  strictly on the write side: names are generated from manifest values with `CultureInfo.InvariantCulture` and
  are never read back as numbers.
- **Orphan tile files** (files under `tiles/` the live manifest does not name) are inert. Load only ever opens
  what the manifest indexes. They are collected by step 6 of the next successful save, which is the only moment
  the live set is known. There is no separate repair verb.
- **Leftover `.tmp` files** are deleted on sight, never read: `map.json.tmp` in step 3, every stray `*.tmp` in
  step 6.
- **An unreadable `map.json`** is the one case that does not self-heal, deliberately. Every tile is rewritten
  and the sweep is skipped, so the directory ends up correct but carrying whatever orphans the unreadable
  manifest could not account for. Deleting files on the authority of a manifest that failed to parse is how a
  bad save turns into a lost world.
- `VerifyTiled` (section 8) reports orphans and stray `.tmp` files alongside hash mismatches. It reports and
  does not delete, for the same reason.

**Partial saves.** A document loaded through a window (section 10) has unloaded tiles in its index. `SaveTiled`
writes files only for loaded tiles and carries unloaded index entries through verbatim, which is safe because
an unloaded tile's file is content-addressed by the hash the manifest already carries, so carrying the entry
through carries the file through. Section 10 states the two guards that make it safe: the write must target the
directory the window came from, and no edited item may land in an indexed-but-unloaded tile.

## 8. Decision 5: per-tile canonical bytes and the world identity hash

**Algorithm: SHA-256, lower hex, full 32 bytes.** `System.Security.Cryptography.SHA256.HashData` is in the BCL,
is byte-identical on every platform and runtime the fleet targets, and is already what the fleet reaches for
(`UpdateManifest`, `SfxHasher`, `Pkce`, `RuinborneWorldIdentity`). Truncation is the consumer's call.
`RuinborneWorldIdentity.HashBytes` already truncates to 8 bytes for the wire and can keep doing so.

Rejected: xxHash or CRC32. Not in the BCL, and speed is not the constraint because hashing happens once per tile
at save and never on the load path. Rejected: `string.GetHashCode`, which is randomized per process.

**Canonical bytes** for a tile are a compact serialization, distinct from the indented bytes on disk:
`WriteIndented = false`, camelCase, nulls omitted, the standard invariant round-trippable float formatting
System.Text.Json already emits, and each of the four lists in a fixed order. Sculpt tiles keep today's ascending
(tileZ, tileX) order (`MapTerrainOverrides.Tiles` already sorts that way,
`MapTerrainOverrides.cs:46-54`). **Placements, spawns and player spawns are sorted by ordinal `Id` before
hashing.** Ids are already validated unique within a document (`MapDocumentValidator.cs:67-89`), so the sort is
total (no tie to break, no secondary key needed), and it makes the tile hash depend only on content. Two
authoring sessions that added the same placements in a different order produce the same world hash.

**Sorting is scoped to the four bucketed lists, and the identity claim is scoped to match.** `exclusions`,
`scatterOverrides`, `regions` and the various `Tags` lists are hashed in DOCUMENT ORDER, not sorted.
`scatterOverrides` has to be: `MapRuntime.BuildScatterConfig` walks it in document order and the resulting
`ScatterConfig` is order-sensitive (`MapRuntime.cs:103-110`), so reordering it changes the world and the hash
is right to notice. The others are unsorted only because they are not the scaling variable and sorting them
buys nothing. So the honest claim is **"the world hash depends only on content for the four bucketed lists,
and on content plus document order for the global shape lists"**, not the flat "depends only on content" the
first draft asserted.

**Every integer written into hash input or a file name goes through `CultureInfo.InvariantCulture`.** This is
not defensive boilerplate, it is a real divergence: under ICU, `sv-SE` and `fi-FI` format a negative integer
with U+2212 MINUS SIGN rather than U+002D HYPHEN-MINUS. Verified on this machine's .NET 10, where
`(-3).ToString(CultureInfo.GetCultureInfo("sv-SE"))` is `"−3"` (`nb-NO` too). So a world containing any
negative tile coordinate hashes differently on a Swedish workstation than on an American one, and writes
differently named files, and a golden-digest test running under the CI default culture cannot see any of it.
Every integer in the composition lines and in every generated file name is formatted invariantly (or through
`Utf8Formatter`, which has no culture at all). Section 15 adds an `sv-SE` variant of the golden-digest test so
the rule is enforced rather than remembered.

**A null sculpt block normalizes to "`DefaultCellSize`, zero tiles" for hashing.** `MapDocument.TerrainOverrides`
is nullable (`MapDocument.cs:34`) and `MigrateV1ToV2` deletes the key outright when it is null
(`MapDocumentFile.cs:39-47`), so "no sculpt" and "an empty sculpt block" are two distinct on-disk states for the
same world. Without a rule, a null-overrides monolithic document saved tiled and reloaded comes back with a
present-but-empty block at `MapTerrainOverrides.DefaultCellSize` (0.5), and
`WorldHash_MonolithicEqualsTiled` fails on a round trip that changed nothing. The rule: **a null block hashes
identically to an empty block at `MapTerrainOverrides.DefaultCellSize`**, and symmetrically, `convert_to_single`
omits the `terrainOverrides` key entirely when the block is empty, so a round trip through the tiled form is
byte-stable on the monolithic side too. `TerrainSculptStroke.Revert` already restores `null` rather than an
empty block for exactly this reason (`TerrainSculptStroke.cs:105`), so the rule matches behaviour that
already exists.

**Feature DTO member order is inherited, not controlled.** `MapFeatureConverter.Write` serializes a feature
through `JsonSerializer.SerializeToNode(value, value.GetType(), options)`
(`MapFeatureConverter.cs:32`), so the manifest hash inherits System.Text.Json's reflection member ordering for
those types. That is stable for a given assembly build, which is what the golden-digest test pins, but it is not
a contractual guarantee across runtimes or across a reordering of the DTO's own members. Changing the
declaration order of a feature DTO's properties is therefore a hash-affecting change and needs a
`SchemeVersion` bump, exactly like a canonicalization change.

```csharp
public static class MapDocumentHash
{
    /// <summary>Hash scheme version, folded into every digest. Bumping it invalidates every stored hash on
    /// purpose, which is what a canonicalization change must do.</summary>
    public const int SchemeVersion = 1;

    /// <summary>The canonical hash of one document tile's four content lists.</summary>
    public static string OfTile(MapSpatialIndex index, MapTileCoord tile, MapDocRegistry? registry = null);

    /// <summary>The canonical hash of the global half: bounds, terrain, scatter and companion layers,
    /// exclusions, scatter overrides, regions, tileSize, sculptCellSize, formatVersion, id.</summary>
    public static string OfManifest(MapDocument doc, MapDocRegistry? registry = null);

    /// <summary>Composes a world identity from the manifest hash and the per-tile hashes, ascending (Z, X).
    /// ASSERTS that the entries arrive strictly monotonic in (Z, X) and throws MapDocumentException if not,
    /// so an unordered caller fails loudly instead of minting a second identity for the same world.</summary>
    public static string Compose(string manifestHash, IEnumerable<MapTileEntry> tiles);

    /// <summary>The world identity of a document. On a tiled document this reads the hashes out of the manifest
    /// index and never opens a tile file.</summary>
    public static string OfWorld(MapDocument doc, MapDocRegistry? registry = null);
}
```

Composition input is `"kemap/" + SchemeVersion + "\n"`, then the manifest hash and a newline, then one
`"{x},{z}={hash}\n"` line per occupied tile in ascending (Z, X) order, every integer invariant-formatted.
Labelled and newline delimited for the same reason `RuinborneWorldIdentity` is: a value can never slide from
one field into the next.

**What is in the hash and what is not.** `displayName` and `$schema` are excluded. The hash exists to answer
"is the ground under this player the same ground", and renaming a zone must not desync a live server from its
clients. Everything that shapes terrain, scatter, or authored content is included. This is a deliberate
narrowing relative to Ruinborne's current behaviour, which hashes the entire `SaveText` output including the
display name.

**`tileSize` IS part of world identity, and that has a consequence worth saying out loud.** It is in the
manifest hash, so re-tiling a world (a pure storage decision that moves no content) changes the world hash and
therefore forces a coordinated client and server release. That is the correct trade, because the alternative
is a hash that cannot certify the bucketing the tile hashes were computed under. What follows from it is a
hard rule: **`convert_to_tiled` and `convert_to_single` must PRESERVE `tileSize`, never re-derive or default
it**, or a round trip through the monolithic form would silently change world identity. Changing it is a
separate, explicit `ke-mapedit retile <tileSize>` verb whose whole job is to say what it is doing, rewrite
every tile file, and warn that the world hash changes. `MapDocument.TileSize` keeps a public setter because
the model is a plain DTO everywhere else, so the rule lives in the converters and in `retile`, and section 15's
`WorldHash_ChangesOnRetile` test pins it.

**Cost.** The first draft's figure here conflated two grids and used the wrong line length, so: the honest
worst case is every document tile of a 100 km world occupied, which at the 512 m default is 196 tiles per axis,
38,416 tiles. A composition line is a 64-hex digest plus two coordinates and three delimiters, about 75 bytes,
not 40. That is roughly 2.9 MB of hash input, hashed once, milliseconds. (The 24,398 figure in section 1 is a
SCULPT tile count from the extent-100000m case, not a document tile count. Different grid, and the numbers are
not interchangeable.) Either way it replaces the measured 3.34 s and 6.14 GB, so the conclusion is unaffected.
`OfWorld` on a monolithic document buckets in memory and hashes each bucket with the same canonical writer, so
**the two forms of the same world produce the same identity**. That is the property that lets Ruinborne convert
to tiled without a coordinated client and server flag day.

**What invalidates a tile hash:** any byte of that tile's canonical content. **What invalidates the world hash:**
any tile hash, the manifest hash, or the set of occupied tiles.

**Trust and verification.** `SaveTiled` computes every hash as it writes, so the index is never stale relative to
the files it was written with. Load does **not** verify by default
(`MapDocumentLoadOptions.VerifyTileHashes = false`), because verification means re-serializing the parsed tile
canonically and that is exactly the per-load cost this design exists to remove. Verification is a content check,
not a hot path:

```csharp
/// <summary>Re-derives every tile hash from its file and reports mismatches, plus orphan files under tiles/
/// the manifest does not name and any stray *.tmp left by a crashed save. Empty means clean. Reports, never
/// deletes. For the editor's save-time check, a CI content gate, and the ke-mapedit validate verb.</summary>
public static IReadOnlyList<string> VerifyTiled(string directory, MapDocRegistry? registry = null);
```

Rejected: hashing the file bytes as written. Verification would then be free, but the hash would change on a
reindent, and a format that sells itself as human-diffable will get hand-edited.

## 9. Decision 8a: the streamer core extraction

The streamer core is already render-agnostic in code. `TerrainStreamer`, `StreamerConfig`, `IChunkSink`,
`IAsyncChunkSink`, `ChunkCoord`, `ChunkGrid`, `ChunkRing`, `ChunkBuildScheduler`, `IChunkBuildDispatcher`,
`TerrainLod`, `TerrainLodConfig` and `TerrainChunkRegion` all sit in `namespace KhaozEngine.Terrain`, deal in
opaque `object` handles, and reference no render or physics type. They ship in `KhaozEngine.Terrain.Render3D`,
which references `KhaozEngine.Render3D` and `KhaozEngine.Physics`, so a headless server cannot touch them.

**Decision: move all twelve types (thirteen with `TerrainLodTier`, `ChunkBuild<T>`, `ChunkBuildException` and
`TaskChunkBuildDispatcher` counted separately) into `KhaozEngine.Terrain`, in release 1.**

What stays in `KhaozEngine.Terrain.Render3D`: `Scene3DChunkSink`, `TerrainChunkBuilder`, `TerrainChunkBounds`
(references Render3D), `TerrainChunkMesh`, `TerrainChunkCollision`, `ChunkStatics`, `ChunkDynamics`,
`ChunkTerrainCollision` (all reference Physics), `PropLayer`, `PropRenderer`, `PropHlod`, `PlacementBuckets`,
`TerrainScene3D`, `TerrainLayeredMaterial`, `TerrainMaterialPresets`, `TerrainRamp`, `TerrainSplatPacking`,
`TerrainSplatWeights`. The package keeps its identity as the render arm and loses nothing a consumer names.

**Why in release 1 rather than release 2**, given section 2 established that residency does not strictly need
it. `MapTileGrid` delegates to `ChunkGrid.CoordOf` so the floor math has one implementation, and `ChunkGrid` is
in `Terrain.Render3D` today while `MapDoc` must never reference it. So release 1 needs the extraction or it
needs a second copy of the floor rule. Given that, doing the whole extraction at once is cheaper than doing two
of the types now and the rest later, and it lands the entire compat story in a single consumer adopt.

**SemVer: minor, with type forwarders.**

- **Source compatibility is total.** Every moved type keeps its namespace, and `Terrain.Render3D` still project
  references `Terrain`, so `using KhaozEngine.Terrain;` in a consumer that references either package resolves
  exactly as before. Nothing in Ruinborne's `RuinborneWorldView`, the `MapEditor`'s `ViewportWorld`, or
  `Showcase`'s `Room3D` needs a single edit.
- **Binary compatibility is not free and this spec does not pretend it is.** An assembly compiled against
  16.x's `Terrain.Render3D` resolves `TerrainStreamer` to that assembly and would fail with a `TypeLoadException`
  against the new package set. The fix is one file:
  `KhaozEngine.Terrain.Render3D/AssemblyForwarders.cs` carrying `[assembly: TypeForwardedTo(typeof(...))]` for
  every moved public type. With the forwarders in place the change is additive from both a source and a binary
  angle, so **minor**. The fleet recompiles on adopt and would have been fine either way, which makes the
  forwarders belt and braces, but they cost one file and remove an entire class of confusing failure for anyone
  vendoring nupkgs.

**One concrete build hazard.** Four XML doc comments in the moved files `cref` types that stay behind:
`TerrainStreamer.cs:14`, `ChunkGrid.cs:7`, `TerrainLod.cs:8` and `TerrainLodConfig.cs:38` all reference
`Scene3DChunkSink`. `Terrain` cannot reference `Terrain.Render3D` (that is the cycle the split exists to
prevent), so those crefs become CS1574, which with `TreatWarningsAsErrors` is a build failure. They are demoted
to `<c>Scene3DChunkSink</c>` plain text. `ChunkGrid.cs:8`'s `cref="PropScatter"` is fine, `PropScatter` is
already in `Terrain`.

**Tests.** `TerrainStreamerTests` and `TerrainAsyncStreamerTests` (`KhaozEngine.Render.Tests/Terrain/`) test the
moved types against fake sinks and reference nothing from Render3D. They stay in `Render.Tests`, which already
references `KhaozEngine.Terrain`. Moving them to a new `KhaozEngine.Terrain.Tests` would sharpen CI selection
by one project for two files, against the CI design's own principle of clusters rather than per-package test
projects. A `Terrain`-only change already drags `Render.Tests` today, so this is not a regression. An
architecture test asserts `KhaozEngine.Terrain` references neither `KhaozEngine.Render3D` nor
`KhaozEngine.Physics`, which is what locks the extraction against a future re-entangling.

**That architecture test goes in the `KhaozEngine.Tests` rump, and it costs CI selection nothing.** The
objection to putting it there (that it would drag a `Terrain` reference into the rump and blunt
`dotnet-affected`'s targeting) does not apply, because the rump has **no `ProjectReference` to any
`KhaozEngine.*` package at all** and its own csproj carries a comment saying so. `ArchitectureTests.cs` reads
the real `*.csproj` files as XML and has not one `using KhaozEngine.*` directive, which is precisely why every
dependency-graph invariant already lives there. A test asserting a reference does not exist must not itself
create that reference, so the rump is the only correct home for this one.

## 10. Decision 6 and 7: migration, versioning, and the editor

**Format version goes to 3.** The single model change is the root `tileSize`. `terrainOverrides` keeps its
current shape and its own `cellSize` in the monolithic form. The tiled form hoists `cellSize` to the manifest
as `sculptCellSize` because per-tile files must not each restate it, and the reader constructs
`MapTerrainOverrides(sculptCellSize)` and fills it from the tile files. One object model, two serializations.

Why bump at all, when the monolithic body barely changes: **per-tile hashing needs a `tileSize` even for a
monolithic document**, or a monolithic and a tiled copy of the same world hash differently. That is the whole
reason v3 exists, and it is worth the bump.

```csharp
// MapDocumentLoadOptions, extending the existing constructor
public MapDocumentLoadOptions()
{
    RegisterMigration(1, MigrateV1ToV2);
    RegisterMigration(2, MigrateV2ToV3);   // stamps tileSize = MapDocumentFile.DefaultTileSize
}
```

The v2 to v3 step stamps the default and nothing else. Any default is as arbitrary as any other for a document
that had no tile concept, so the rule is "deterministic and documented" rather than "derived". A v1 document
still loads through the full 1 to 2 to 3 chain, which the loader's contiguity check already enforces
(`MapDocumentFile.cs:96-104`).

Version and layout are independent axes. **A v3 monolithic file is legal and is what `Save` writes.** v3 means
"the model that can be tiled", not "tiled". Tangling the two is how a format version ends up meaning two things.

**Schemas.** The embedded `mapdoc.schema.json` has `additionalProperties: false` at the root, so `tileSize`
needs a schema edit. The tiled form needs two more. Three embedded resources, three accessors, so each JSON file
can point `$schema` at its own. The tile schema declares the optional `$schema` annotation from section 5, which
is what makes that claim true for tile files as well as for the manifest:

```csharp
public static class MapDocumentSchema
{
    public static string GetJson();            // monolithic document, existing
    public static string GetManifestJson();    // map.json
    public static string GetTileJson();        // tiles/s_*/t_*.<hash>.json
    public static void WriteTo(string path);   // existing
    public static void WriteAllTo(string directory);
}
```

**Schema validation of a v3 document by an OLDER engine reports the wrong error, and that is the accepted
pattern here, again.** Because `additionalProperties` is `false` at the root, a pre-v3 engine validating a v3
document reports "unknown property `tileSize`" rather than "this document is newer than I support". The loader's
own version gate gives the right message (`MapDocumentFile.cs:92-94`) and the schema is a secondary check, so
this is the same trade the v1-to-v2 bump already made and it is noted rather than fixed.

### The editor, and the two ways it could have destroyed a world

`MapEditorScene` loads with `MapDocumentFile.Load(_options.DocumentPath, ...)` and saves with
`MapDocumentFile.Save(...)` (`MapEditorScene.cs:1292`). Both become form-aware: `Load` dispatches on the path
and the save becomes `SaveAuto`. **The editor saves back in the form it opened**, never converting implicitly.
Deliberate conversion is two explicit `ke-mapedit` verbs, `convert_to_tiled` and `convert_to_single`, and both
call `SaveAs` with the form named rather than inferred.

Two data-loss paths were live in the first draft. Both are worth stating in full, because both are the kind
that look like a successful save.

**1. The open gate tests `File.Exists`, which is false for a directory.** `MapEditorScene.CreateDocument`
does `if (!string.IsNullOrWhiteSpace(_options.DocumentPath) && File.Exists(_options.DocumentPath))`
(`MapEditorScene.cs:400`), and falls through to a blank untitled document otherwise. Point the editor at
`island.map/` and it silently opens an empty world. Press Ctrl+S and `SaveAuto` sees an existing directory,
routes to `SaveTiled`, and the post-manifest sweep in section 7 deletes every tile file the blank document
does not name. Opening a tiled world and saving it destroys it. **Fix, both halves:** the gate becomes
`MapDocumentFile.DetectForm(path) != MapDocumentForm.None`, and `SaveTiled` independently refuses to write a
document whose `Tiles` index is null over a directory that already contains a `map.json`. The gate is the bug,
the writer's refusal is the belt.

**2. A windowed document could reach a whole-document writer.** The first draft scoped the partial-save guard
to `SaveTiled` and only to a different directory, so `MapEditSession.Save()` calling `MapDocumentFile.Save`
directly (`MapEditSession.cs:92`, and `Create` at `:75`) would happily write a window as a complete monolithic
file, dropping every unloaded tile. **Fix:** the guard is stated on the document (`MapTileIndex.IsPartial`,
section 5) and checked by `Save`, `SaveText`, `SaveTo`, `SaveAuto` and `SaveAs`, not by one writer. A new save
entry point added later inherits the guard by calling one of those, which is the point of putting it on the
document rather than on a code path.

**3. Content that moved into an unloaded tile: `SaveTiled` throws.** `set_placement_position` by id, a long
drag, or the undo of a delete can land an item in a tile the window never loaded. Writing that tile's file
would replace its real content with just the moved item. The precise rule: **after bucketing, throw when any
bucketed item lands in a tile the index marks occupied but NOT loaded**, naming the item id and the target tile
and pointing at `set_window`. An item landing in a tile that is not in the index at all is a brand new tile with
no existing content to destroy, so that case is allowed and creates the tile. Silent-clobber becomes a loud,
actionable refusal.

**4. `schemeVersion` ships in the manifest in RELEASE 1.** A windowed save carries unloaded tiles' stored
hashes through verbatim while recomputing the loaded ones. If `MapDocumentHash.SchemeVersion` has moved since
those stored hashes were written, the result is a manifest mixing two canonicalizations under one label, and
it is permanently wrong rather than detectably wrong. So the manifest records the scheme its hashes were
computed under, from the first release. The rules: a WHOLE load at a mismatched scheme is fine and the next
full save upgrades the document, because `SaveTiled` always recomputes every loaded tile under the current
scheme. A WINDOWED load at a mismatched scheme **refuses**, because a partial save cannot upgrade what it
cannot read. Deferring this to release 2 was not an option: it is a manifest field, and section 5 explains why
`MapTileEntry` in particular cannot grow one later.

**5. Moving the window is save-or-discard gated and clears history.** `MapEditSession` tracks `_dirty` but has
no concept of the loaded set changing under it, and an undo command holding a reference to a placement in a
tile that is no longer loaded cannot be reverted. So `set_window` takes an explicit `discard` flag, default
false: with a dirty document and `discard: false` it throws and names the two ways out (save first, or pass
`discard`). On success it **always clears undo and redo history**, whether or not anything was dirty, and
`window_status` reports the current `MapTileRect`, the loaded and occupied counts, and whether the last move
cleared history. No implicit save, no implicit discard, no undo stack that can only half apply.

For a large tiled world the editor opens a **window**, not the whole world:

```csharp
public static class MapDocumentFile
{
    /// <summary>Loads the manifest plus every occupied tile.</summary>
    public static MapDocument LoadTiled(string directory, MapDocumentLoadOptions? options = null);

    /// <summary>Loads the manifest plus the tiles in the window. Unloaded tiles keep index entries, so the
    /// document knows they exist and a later SaveTiled to the SAME directory carries them through untouched.</summary>
    public static MapDocument LoadTiled(string directory, MapTileRect window, MapDocumentLoadOptions? options = null);
}
```

`MapEditorOptions` gains `WholeWorldTileLimit` (default 512 occupied tiles) and `EditorWindowRadius` (default 2
tiles). Below the limit the editor whole-loads, exactly as today. Above it, the editor windows around the first
enabled player spawn or the restored camera bookmark and shows the window extent in the status strip. Every
world that opens today keeps opening: Ruinborne's island spans four 512 m tiles because the grid is origin
anchored and the bounds are -256 to 254, well under any limit.

Alternatives weighed. **Always whole-load in the editor**: simplest, but it hands the editor the same 6.4 s and
2.2 GB the spec exists to remove, and it makes region-scoped editing a rewrite later rather than a widening.
**Refuse to open a large world**: honest but useless, and it makes the tiled format unauthorable by the editor
that is supposed to author it. The window is the middle path and it is small because `SaveTiled` is already
per-file, so a partial save is a natural consequence rather than a new mechanism.

`MapEdit.Tool` (`ke-mapedit`, 78 verbs) follows the same rule through `MapEditSession.Open`
(`MapEditSession.cs:34`) and its two save paths (`:75`, `:92`), and gains `set_window`, `window_status` and
`retile`. Its `validate` verb currently does `MapDocumentFile.SaveText` plus schema validation
(`MapEditSession.cs:116`), which walks straight into the ceiling on a large document. It switches to
`MapDocumentValidator.Validate` plus per-tile schema validation over the loaded window, and delegates
whole-world checking to `VerifyTiled`.

## 11. Decision 8b and 9: document residency

Release 2. The residency layer itself is new public API in `KhaozEngine.MapDoc`, which is GPU-free and already
in both the `Foundation` and (through it) the `Server` umbrella, so **both heads get it with no new package and
no new umbrella row**. Two small additive seams live outside it, and both are additive-only: `IChunkBuildGate`,
`TerrainStreamer.BuildGate` and `IPlacementSource` go in `KhaozEngine.Terrain` (where `ChunkCoord`,
`PropPlacement` and `RectArea` already are), and `PropLayer` gains a source-backed factory overload in
`KhaozEngine.Terrain.Render3D`. `MapDoc` already project-references `Terrain` (`MapSculptTile` uses
`TerrainSculpt.TileSize`) and still never references `Terrain.Render3D`, so the layering is unchanged. Section
13 lists all three packages.

```csharp
namespace KhaozEngine.MapDoc;

/// <summary>Reads tiles on demand. Two sources, one interface, so residency works against a tiled directory
/// and against an in-memory whole document alike. The second is what lets a game adopt residency before it
/// converts its world to the tiled form, and what keeps dungeons on the monolithic form forever.</summary>
public sealed class MapDocumentSource : IDisposable
{
    public static MapDocumentSource OpenTiled(string directory, MapDocumentLoadOptions? options = null);
    public static MapDocumentSource FromDocument(MapDocument doc);

    /// <summary>The globals: bounds, terrain, scatter and companion layers, shapes. Fully populated, no tiles.</summary>
    public MapDocument Manifest { get; }
    public MapTileIndex Tiles { get; }

    /// <summary>Reads, parses and VALIDATES one tile. Pure, thread-safe, no shared mutable state, so residency
    /// runs it on a worker thread. Throws MapDocumentException for a tile the index does not mark occupied,
    /// and for a tile that fails per-tile validation.</summary>
    public MapTileContent ReadTile(MapTileCoord coord);
}

/// <summary>One tile's parsed content. Immutable once handed to a sink, INCLUDING the delta arrays inside
/// SculptTiles: the residency layer hands out the same arrays it parsed, and TerrainSculpt stores them by
/// reference. See "Array ownership" below.</summary>
public sealed class MapTileContent
{
    public MapTileCoord Coord { get; }
    public IReadOnlyList<MapPlacement> Placements { get; }
    public IReadOnlyList<MapSpawn> Spawns { get; }
    public IReadOnlyList<MapPlayerSpawn> PlayerSpawns { get; }
    public IReadOnlyList<MapSculptTile> SculptTiles { get; }
}

/// <summary>Residency tuning. Radii are in TILE units, CHEBYSHEV tile distance (a square ring), which is NOT
/// StreamerConfig's Euclidean metric. See "Ring geometry" below for why they differ on purpose. A distinct
/// type because StreamerConfig's ChunkSize and LodConfig are meaningless here, and a shared type would let a
/// chunk config be passed to document residency by accident.</summary>
public readonly record struct MapResidencyConfig(
    int LoadRadius, int UnloadRadius, int MaxLoadsPerUpdate, int DecorRadius = 0, bool Async = true)
{
    /// <summary>LoadRadius 2, UnloadRadius 3, 2 applies per update, no decor ring. At the 512 m default tile
    /// that is a 5x5 (2,560 m) gameplay square around the focus, 1,024 m of guaranteed coverage in every
    /// direction, and a 1-tile (512 m) hysteresis band.</summary>
    public static MapResidencyConfig Default { get; }

    public int OuterRadius { get; }
    public MapResidencyConfig Synchronous();

    /// <summary>Errors (empty means fine) when this config cannot cover a terrain streamer's chunk ring, so a
    /// chunk can never build or REBUILD against a non-resident tile. Needs sculptCellSize because a document
    /// tile's sculpt coverage is inset by one sculpt span (section 3). Checked by the consumer at wiring
    /// time.</summary>
    public IReadOnlyList<string> ValidateAgainst(StreamerConfig streamer, float tileSize, float sculptCellSize);
}

/// <summary>Residency notifications. The engine tells you a tile arrived or left. What you build from it,
/// including physics bodies, is yours.</summary>
public interface IMapTileSink
{
    void TileLoaded(MapTileCoord coord, MapTileContent content, ChunkRing ring);
    void TileRingChanged(MapTileCoord coord, MapTileContent content, ChunkRing ring);
    void TileUnloaded(MapTileCoord coord);
}

public sealed class MapTileResidency : IDisposable
{
    public MapTileResidency(MapDocumentSource source, MapResidencyConfig config, IMapTileSink sink,
                            IChunkBuildDispatcher? dispatcher = null);

    /// <summary>The resident set, UNORDERED and deliberately so: it changes every update, sorting it per
    /// update would be pure cost, and unlike MapSpatialIndex.OccupiedTiles it is never a hash input. A caller
    /// that needs a deterministic sequence sorts it itself.</summary>
    public IReadOnlyCollection<MapTileCoord> Resident { get; }
    public ChunkRing? RingOf(MapTileCoord coord);
    public bool TryGetContent(MapTileCoord coord, out MapTileContent content);

    /// <summary>Client form: one focus.</summary>
    public void Update(Vector3 focus);

    /// <summary>Server form: the union of the rings around every focus. A tile leaves residency only when no
    /// focus keeps it.</summary>
    public void Update(ReadOnlySpan<Vector3> foci);

    /// <summary>Deterministic blocking fill of the whole ring, for a loading moment. This is also half of the
    /// teleport contract below.</summary>
    public void PrimeAround(Vector3 focus);
    public void FlushPendingLoads();

    /// <summary>The build gate to hand TerrainStreamer.BuildGate, for a given chunk size and sculpt cell size.
    /// A chunk is buildable only when every document tile its (sculpt-expanded) footprint touches is either
    /// resident or unoccupied. See "The residency-gated chunk request".</summary>
    public IChunkBuildGate GateFor(float chunkSize, float sculptCellSize);

    /// <summary>Re-read one resident tile from the source (an editor wrote it, a tool regenerated it).</summary>
    public void Invalidate(MapTileCoord coord);

    public void UnloadAll();
    public void Dispose();
}
```

### Ring geometry: Chebyshev, not Euclidean

**The first draft declared residency radii Euclidean "mirroring `StreamerConfig`'s ring semantics exactly",
and that was a real geometric bug, not a wording slip.** `TerrainStreamer` genuinely is Euclidean
(`dx*dx + dz*dz <= r*r`, `TerrainStreamer.cs:175-176`), but copying that metric to document tiles breaks the
one property residency exists to provide: a guaranteed radius of loaded world around the focus.

Work it. The focus can sit anywhere in its own tile, including hard against a corner. Ask what the nearest
NON-resident tile is from there, computed rather than eyeballed:

| Metric, radius R | Resident tiles | Guaranteed covered radius |
|---|---|---|
| Euclidean, R=1 | 5 | **0** (tile (1,1) is excluded, and the focus can be arbitrarily close to it) |
| Euclidean, R=2 | 13 | 1 tile |
| Euclidean, R=3 | 29 | 2 tiles |
| Euclidean, R=4 | 49 | **2.83 tiles** |
| Euclidean, R=5 | 81 | 4 tiles |
| Chebyshev, R | (2R+1)^2 | **exactly R tiles, for every R** |

The Euclidean column is why the first draft's hard rule
(`LoadRadius * tileSize > OuterRadius * ChunkSize + ChunkSize`) certified a config that has a hole in it: at
`LoadRadius` 1 the resident set is a plus-shape of 5 tiles, tile (1,1) is not resident, and a focus near that
corner has zero guaranteed coverage while the chunk ring is happily requesting builds several hundred metres
out. Note also that the obvious repair, "keep Euclidean and use `(LoadRadius - 1) * tileSize`", is itself wrong:
at R=4 the true guarantee is 2.83 tiles, not 3, because the binding non-resident tile is the diagonal (3,3)
rather than the axial (5,0). An exact rule that only holds for some radii is worse than no rule.

**Decision: document residency rings are Chebyshev (a square ring, `max(|dx|,|dz|)`).** Load out to
`OuterRadius`, unload past `UnloadRadius` with `UnloadRadius > OuterRadius` enforced in the constructor,
nearest-first, `MaxLoadsPerUpdate` applies per update, immediate unloads. Chebyshev gives `LoadRadius * tileSize`
of guaranteed coverage exactly, for every radius, with no special cases, which makes the consumer contract below
a single line of arithmetic that is actually true. It also matches every piece of prose the first draft already
had ("a 1.5 km gameplay square", section 12's "at most 9 document tiles"): the geometry was Chebyshev
throughout and only the declared metric was Euclidean.

The two metrics differing is fine and deliberate. They are different units at different scales solving different
problems: a chunk is a render primitive where a round ring saves builds in the corners, and a document tile is a
data-availability unit where a square ring is the thing that can be reasoned about. `MapResidencyConfig` is a
distinct type from `StreamerConfig` precisely so the two cannot be confused, and its doc comment says which
metric it uses.

`ChunkRing.Gameplay` and `ChunkRing.Decor` are reused as-is: a gameplay tile is one the simulation touches, a
decor tile is one a client wants for far-field render only. That is the same distinction the chunk streamer
already draws and a second enum for it would be pure duplication. Note the ring governs what a CONSUMER builds
from a tile, never whether the tile's data is present: a Decor tile is fully loaded and `TryGetContent` returns
it, which is what lets the coverage rules below use `OuterRadius` for data and `LoadRadius` for colliders.

**Multi-focus ring resolution: strongest wins.** A tile that is Gameplay for one focus and Decor for another
resolves to **the numerically lowest `ChunkRing` any focus assigns it**, which is `Gameplay` (the enum is
`Gameplay = 0`, `Decor = 1`). Without that rule the result depends on the order the foci happen to be
enumerated in, so a tile would flap between rings from update to update and a consumer would shed and re-add its
colliders every frame. The minimum is order-independent by construction, so the resident set and every tile's
ring are a pure function of the focus SET, not its sequence. The refcount-free union stands: the resident set is
recomputed from the foci each update, so nothing is reference counted and a tile leaves only when no focus
keeps it.

**Threading mirrors `IAsyncChunkSink`'s split.** `MapDocumentSource.ReadTile` runs on the dispatcher's worker
thread (file read plus parse, no device, no shared state). `TileLoaded`, `TileRingChanged` and `TileUnloaded`
fire on the calling thread inside `Update`, before it returns, so a consumer registers and frees physics bodies
without a lock. Last-request-wins and cancel-on-departure are handled by a per-tile generation token, the same
invariant `ChunkBuildScheduler` maintains for chunks.

**Why not reuse `TerrainStreamer` directly**, given the ring logic is identical and a second `TerrainStreamer`
at `ChunkSize = tileSize` with a single-tier LOD config would work. Three reasons, one decisive.

1. **Decisive: absence is the common case and `TerrainStreamer` has no concept of it.** In a sparse 100 km
   world most tiles in the ring hold no authored content and have no file. `TerrainStreamer` assumes every chunk
   in the disk exists and gets a handle, so it would stat the filesystem for every absent tile every time the
   ring moved. Skipping absent tiles requires the manifest's occupied-tile index, which `TerrainStreamer` knows
   nothing about and should not.
2. Consumers need to query the resident set by document semantics (`TryGetContent`, `Resident`), not through an
   opaque sink handle.
3. LOD would be dead weight, and a degenerate single-tier `TerrainLodConfig` is a trap: with the default multi
   tier table a document tile would re-LOD, and therefore re-parse, as the player moves.

**Why not widen `ChunkBuildScheduler<T>` to `ChunkBuildScheduler<TCoord, T>`** and share the async bookkeeping.
It is a public break on a type `TerrainStreamer` uses, and the document side needs strictly less than it offers
(no LOD tier, only a ring), so the shared version would carry a dead type parameter for one of its two callers.
`MapTileResidency` reuses `IChunkBuildDispatcher` and `TaskChunkBuildDispatcher` (the seam that actually matters,
because it is what makes the async path testable with a manual dispatcher) and keeps about fifty lines of
generation bookkeeping of its own.

### Composing with `TerrainStreamer` without double-tracking

**Neither owns the other's unit.** `MapTileResidency` owns document tile lifetime. `TerrainStreamer` owns chunk
lifetime. The chunk sink reads resident document data. Nothing is tracked twice.

The first draft's consumer contract was wrong in three separate ways at once, so here it is derived rather than
asserted.

**What a chunk actually reaches.** A chunk at integer offset `(dx, dz)` from the focus chunk covers world out
to `(|dx| + 1) * ChunkSize` on each axis relative to a focus sitting at the far corner of its own chunk. So the
worst-case world distance a loaded chunk reaches is

```
maxChunkReach(R) = ChunkSize * max{ sqrt((|dx|+1)^2 + (|dz|+1)^2) : dx^2 + dz^2 <= R^2, dx and dz integers }
```

That maximum is NOT the axial `(R, 0)` case. Computed over the integer disk: at `R = 6` the worst offset is
`(5, 3)`, giving `sqrt(52) = 7.211` chunks, not the `sqrt((R+1)^2 + 1) = 7.071` an axial-only reading gives.

**Which R.** `UnloadRadius`, not `OuterRadius`. Chunks persist to `UnloadRadius`
(`TerrainStreamer.cs:149`, `:218`), and `TerrainStreamer.Invalidate(RectArea)` rebuilds every LOADED chunk the
rect touches at its current tier (`TerrainStreamer.cs:302-311` calling `InvalidateLoaded`), which the sculpt
handoff below calls on every tile arrival. So a chunk anywhere out to `UnloadRadius` can be rebuilt at any
moment and must find its document data resident when it is.

**Minus the sculpt inset.** Section 3: a resident tile's low-X and low-Z edges are covered by sculpt owned by
the neighbouring document tile, so subtract `sculptSpan = TerrainSculpt.TileSize * sculptCellSize`. Hysteresis
does not pay for this, because hysteresis is an unload-side allowance and the shortfall is on the load side.

The contract, in two rules, both checked by `ValidateAgainst`:

> **Data rule.** `OuterRadius * tileSize - sculptSpan >= maxChunkReach(streamer.UnloadRadius)`.
> No chunk can ever build or rebuild against missing document data.
>
> **Collider rule.** `LoadRadius * tileSize - sculptSpan >= maxChunkReach(streamer.LoadRadius)`.
> Every gameplay chunk (the ones that register scatter and colliders) sits over Gameplay document tiles, so a
> consumer that sheds colliders on a Decor tile never sheds them under a live gameplay chunk.
>
> **Ordering.** `MapTileResidency.Update` runs before `TerrainStreamer.Update` in the same frame.

At `StreamerConfig.Default` (LoadRadius 4, UnloadRadius 6, ChunkSize 60 m), `tileSize` 512 m and Ruinborne's
`sculptCellSize` 2 (a 64 m sculpt span):

| Quantity | Value |
|---|---|
| `maxChunkReach(6)` | 7.211 x 60 = **432.7 m** (worst offset (5,3)) |
| `maxChunkReach(4)` | 5.099 x 60 = **305.9 m** (worst offset (4,0)) |
| Data rule at `LoadRadius` 2 | 2 x 512 - 64 = 960 m >= 432.7 m, **527 m of margin** |
| Collider rule at `LoadRadius` 2 | 960 m >= 305.9 m, **654 m of margin** |
| Data rule at `LoadRadius` 1 | 512 - 64 = 448 m >= 432.7 m, **15 m of margin** |

**That last row is the whole argument for the default being 2 and not 1.** `LoadRadius` 1 does pass at
`StreamerConfig.Default`, by 3% of a tile, and fails the moment a game turns on a decor ring: at
`DecorRadius` 8 / `UnloadRadius` 10 the reach is `11.402 x 60 = 684 m`, which `LoadRadius` 1 cannot cover and
`LoadRadius` 2 covers with 276 m to spare. A default that only works for one streamer config is not a default.

So `MapResidencyConfig.Default` is **LoadRadius 2, UnloadRadius 3, MaxLoadsPerUpdate 2, no decor ring**: a 5x5
(2,560 m) gameplay square, 1,024 m of guaranteed coverage before the sculpt inset, and a 1-tile hysteresis band.
One tile of hysteresis is 512 m of focus travel, which absorbs any boundary oscillation completely (the chunk
streamer's own band is 120 m), and it caps the worst-case resident set at `(2*3+1)^2 = 49` tiles rather than the
81 a 2-tile band would allow. `MaxLoadsPerUpdate` 2 fills the 5-tile column a boundary crossing brings into
range in 3 updates, against the 85 s it takes to cross a 512 m tile at 6 m/s.

`ValidateAgainst` returns the error strings so a wiring mistake is loud at startup instead of a hole in the
world at 1 km.

### The teleport contract and the residency-gated chunk request

Ordering residency before the streamer is necessary and **not sufficient**, because residency is async by
default: at `MaxLoadsPerUpdate` 2 and roughly 40 ms per tile parse, a discontinuous focus move leaves the
streamer requesting chunk builds for tiles that will not arrive for many frames. Those chunks build against the
analytic field with no sculpt and no placements. The sculpt handoff does eventually invalidate them, so it
self-corrects, but for those frames the player sees flat ground with no props and, worse, stands on a terrain
collider built from the unsculpted field. That is a fall-through-the-world hazard, not a cosmetic pop. Two
things fix it, and both are needed because they cover different cases.

**1. The teleport contract, for a discontinuous focus move** (a teleport, a zone change, a camera jump):

> `residency.PrimeAround(newFocus)` then `streamer.UnloadAll()`, both before the next `streamer.Update`.

`PrimeAround` is the existing deterministic blocking fill: it is a loading moment, not a frame, so the whole new
ring is resident before anything asks for a chunk. `UnloadAll` on the streamer discards the pre-teleport ring so
no chunk built for the old location lingers or lands late. Order matters: residency first, so the streamer's
first ring at the new location builds against a complete document set.

**2. The residency-gated chunk request, for everything else.** Continuous motion can still outrun async
residency (a vehicle, a slow disk, a fat tile), and no ordering rule fixes that. So `TerrainStreamer` gains an
optional gate, additive and null by default:

```csharp
namespace KhaozEngine.Terrain;

/// <summary>Gates which chunks the streamer may build. Returning false DEFERS the chunk: it is not requested,
/// not marked loaded, and it is reconsidered on the next Update. A null gate (the default) means every chunk
/// in the ring is eligible, which is the pre-gate behaviour exactly.</summary>
public interface IChunkBuildGate
{
    bool CanBuild(ChunkCoord coord);
}

public sealed class TerrainStreamer
{
    /// <summary>Optional build gate. Null (the default) preserves today's behaviour byte for byte.</summary>
    public IChunkBuildGate? BuildGate { get; set; }
}
```

`MapTileResidency.GateFor(chunkSize, sculptCellSize)` returns the implementation: a chunk is buildable when
**no document tile touching its footprint is occupied-but-not-resident**. Two details carry the weight. First,
an ABSENT tile (not in the occupied index) is buildable, not blocked, because absence is the common case in a
sparse 100 km world and gating on it would deadlock the streamer over empty terrain. Second, the footprint is
expanded by one sculpt span on its -X and -Z sides before being mapped to document tiles, so a chunk whose
ground includes deltas owned by the neighbouring document tile waits for that neighbour too. That closes
section 3's ownership inset at the seam where it actually bites, with `ValidateAgainst` as the startup-time
backstop rather than the only defence.

A deferred chunk is not a failure mode, it is the streamer's ordinary "not yet": it arrives a few frames later
when its data does, which is exactly what the ring already does for every chunk.

### The render sink seam: how streamed placements reach the renderer

**This is the largest structural gap the first draft left, and it is decided here rather than deferred.**
`Scene3DChunkSink` buckets a placement layer's props **once, at construction**:
`_placementBuckets = PlacementBuckets.Build(snapshot, chunkSize)` (`Scene3DChunkSink.cs:120`) over a frozen
`IReadOnlyList<PropPlacement>` carried on a `readonly struct PropLayer` (`PropLayer.cs:32`). `ReLod`, and
therefore `Invalidate`, rebuilds from that same stale bucket map. So with the first draft as written, a
placement arriving with a streamed document tile would never render, no matter how correct the residency layer
was.

**Decision: a live placement source on `PropLayer`, queried per chunk build.**

```csharp
namespace KhaozEngine.Terrain;

/// <summary>A live source of a placement layer's props, queried at every chunk build instead of bucketed once
/// at sink construction. This is what lets streamed-in document tiles reach the render sink. Called on the
/// BUILD thread, so an implementation publishes an immutable snapshot and reads it once per query.</summary>
public interface IPlacementSource
{
    /// <summary>Appends the placements whose (X, Z) falls in the half-open rect into a caller-owned list, so a
    /// per-chunk query allocates nothing.</summary>
    void PlacementsIn(RectArea area, List<PropPlacement> into);
}
```

`PropLayer` gains `IPlacementSource? PlacementSource { get; }` and a `PlacementLayer` factory overload taking
one. `PlacementBuckets.Build` skips source-backed layers (they get `null` in the index-aligned bucket array),
and the sink's per-chunk placement fetch reads the source when one is set and the frozen bucket otherwise. A
frozen-list layer is byte-identical to today, so this is purely additive.

`MapTileResidency` implements `IPlacementSource` directly. `KhaozEngine.MapDoc` already references
`KhaozEngine.Terrain` (`MapSculptTile` uses `TerrainSculpt.TileSize`) and `IPlacementSource` lives there beside
`PropPlacement` and `RectArea`, so this needs no new package edge and, critically, no `MapDoc` reference to
`Terrain.Render3D`. A consumer wires `PropLayer.PlacementLayer(residency, meshes, drawRadius)` and streamed
placements flow with zero glue code.

Staleness is handled by the mechanism that is already there: the `TileLoaded` / `TileUnloaded` handler calls
`TerrainStreamer.Invalidate(MapTileGrid.AreaOf(coord, tileSize))` for the sculpt, and the same call rebuilds
those chunks' placements. Thread safety is the same copy-on-write discipline as the sculpt snapshot:
`MapTileResidency` publishes an immutable per-tile content map with `Volatile.Write` on every mutation and
`PlacementsIn` takes one `Volatile.Read`, so a build thread always sees one whole consistent generation.

Rejected: **an explicit `Scene3DChunkSink` reconstruction seam** (tear down and rebuild the sink when the
resident set changes). It throws away every built chunk in the ring for a change that affects a handful of
them, and it makes the sink's lifetime a consumer concern in a design whose whole point is that residency and
chunk lifetime are independent.

### The sculpt handoff

`TerrainField` holds its `TerrainSculpt` in a readonly field (`TerrainField.cs:18`) and
`MapRuntime.BuildField` constructs it once from the whole document. With residency the resident sculpt set
changes as the player moves, so the field has to change under the streamer.

**Decision: an atomic copy-on-write snapshot swap.** `TerrainSculpt` stays immutable and `TerrainField` gains:

```csharp
public sealed class TerrainField
{
    /// <summary>Replaces the sculpt layer with a new immutable snapshot, by an atomic reference exchange. A
    /// sampler running concurrently on a worker thread sees either the old snapshot or the new one, never a
    /// torn state, and both are valid terrain. Applies the SAME normalization the constructor does: a null or
    /// empty sculpt stores null, which is what keeps the analytic fast path and the 1 m normal epsilon.</summary>
    public void SetSculpt(TerrainSculpt? sculpt);
}

public sealed class TerrainSculpt
{
    /// <summary>This sculpt with tiles added and removed, sharing every unchanged tile's delta array by
    /// reference. O(tile count), not O(cell count). TAKES OWNERSHIP of every added tile's array, matching the
    /// constructor's existing contract.</summary>
    public TerrainSculpt With(IEnumerable<TerrainSculptTile>? add, IEnumerable<(int TileX, int TileZ)>? remove);
}
```

The consumer's `TileLoaded` handler rebuilds the snapshot with `With`, calls `SetSculpt`, then calls
`TerrainStreamer.Invalidate(MapTileGrid.AreaOf(coord, tileSize))`, which is exactly the partial invalidation
seam `Invalidate(RectArea)` was built for. Snapshot rebuild is O(resident tiles) with no delta array copied, so
at a few hundred resident tiles it is microseconds.

**Three things the "atomic reference exchange" phrase was quietly promising and did not deliver.**

**1. `SampleNormal` is not atomic under a swap, and the specced test would not have caught it.**
`TerrainField.SampleNormal` reads `_sculpt` **five** times per call: once for the epsilon
(`eps = _sculpt is null ? 1f : _sculpt.CellSize`, `TerrainField.cs:83`) and once inside each of its four
`SampleHeight` calls (`TerrainField.cs:73-74`). A swap landing mid-call therefore builds a normal out of two
different snapshots, and can pair an epsilon from one with heights from the other, which is not "either the old
or the new, both valid" but a third thing that is neither. The specced concurrency test hammers `SampleHeight`
only and would pass a broken implementation. **Fix:** every PUBLIC entry point reads the field exactly once
into a local and threads that local through a private overload
(`SampleHeight(x, z, TerrainSculpt? sculpt)`), so one call is one snapshot by construction. Section 15's test
grows a `SampleNormal` arm that asserts every sampled normal belongs to one of the two snapshots.

**2. Publication needs a memory barrier, not just a reference write.** A plain field write is atomic for a
reference but is not ordered against the writes that filled the new `TerrainSculpt`'s dictionary. On arm64 a
reader can observe the new reference before those writes are visible and read a half-built dictionary, which is
exactly the torn state the design claims cannot happen. **Fix, stated in the spec rather than left to the
implementer:** the field is `volatile`, `SetSculpt` publishes with `Volatile.Write` (or
`Interlocked.Exchange`), and every reader takes one `Volatile.Read` into the local from point 1.

**3. `SetSculpt` must reproduce the constructor's normalization.** The constructor stores `null` for a null or
empty sculpt (`_sculpt = sculpt is { IsEmpty: false } ? sculpt : null;`, `TerrainField.cs:31`), and that null is
load-bearing twice: it is what selects the analytic fast path in `SampleHeight`, and it is what makes
`SampleNormal`'s epsilon 1 m instead of the cell size. A `SetSculpt` that stores an empty sculpt as-is would
silently change the normal epsilon on a field that has no sculpt, so it applies the identical rule.

**Array ownership after `With`, made a rule instead of a convention.** `TerrainSculpt`'s constructor stores each
tile's `Deltas` **by reference** and already documents them as owned afterwards (`TerrainSculpt.cs:22-24`,
`:34`). `MapRuntime.BuildSculpt` passes `tile.Deltas` straight through (`MapRuntime.cs:77`), and
`MapSculptTile.Deltas` is a public array with a public indexer setter (`MapSculptTile.cs:19`), so today's safety
rests entirely on `TerrainSculptStroke` cloning before it hands anything over
(`TerrainSculptStroke.cs:89`, `:100`). Residency makes that convention load-bearing across a thread boundary,
because `MapTileContent` exposes `IReadOnlyList<MapSculptTile>` whose ELEMENTS are freely mutable and whose
arrays end up inside a snapshot that worker threads are sampling. **The rule: `MapTileContent` and everything
reachable from it is immutable to the consumer, and `TerrainSculpt.With` takes ownership of every array handed
to it.** A consumer that wants to edit a streamed tile's deltas clones first, exactly as
`TerrainSculptStroke` already does. `IReadOnlyList` of a mutable element type cannot express this, so it is
written on both types' doc comments and it is why `MapTileContent`'s summary says "immutable once handed to a
sink, INCLUDING the delta arrays".

Rejected: making `TerrainSculpt` mutable with `PutTile` and `RemoveTile`. `TerrainField.SampleHeight` is called
from chunk `BuildCpu` on worker threads, so in-place mutation is a data race on the dictionary, and the
contract that would make it safe ("mutate only on the frame thread, after `FlushPendingBuilds`") is a contract
consumers will get wrong. A chunk built across a swap carries the pre-swap terrain and is re-invalidated by the
same handler, which is a bounded, self-correcting outcome rather than a torn read.

### The physics seam, stated for the record

**The engine notifies. The consumer populates. Nothing here registers, owns, or frees a physics body.**

- `IMapTileSink.TileLoaded(coord, content, ring)` fires exactly once per tile entering residency, on the calling
  thread, before `Update` returns. `content.Placements` is the list a consumer walks to add static bodies.
- `IMapTileSink.TileUnloaded(coord)` fires exactly once per tile leaving residency. That is where a consumer
  removes the bodies it added.
- `IMapTileSink.TileRingChanged(coord, content, ring)` fires on a Gameplay to Decor transition or back, which is
  where a consumer sheds colliders for a far tile without dropping its data.
- **Per-tile add and remove of physics bodies is the intended use and nothing in this design forbids, batches,
  defers or wraps it.** A consumer may add and remove bodies from these callbacks freely.

Per-tile physics population itself is consumer work. Ruinborne's `RuinbornePhysics.Populate` reads game-global
statics today and Ruinborne fixes that on its side.

### The headless server

`MapTileResidency` is GPU-free and drives from a position, so a server runs it directly. Two server-specific
notes.

- Use the multi-focus `Update(ReadOnlySpan<Vector3>)`. The resident set is the union of the per-focus rings,
  recomputed each update, so no reference counting is needed, and each tile's ring is the strongest any focus
  assigns it (see "Multi-focus ring resolution" above), so the answer does not depend on focus order. Cost is
  O(foci * ring area): at 100 players and the default 5 by 5 ring that is 2,500 coordinate tests, trivial. Past
  a few hundred foci a shard server should drive **one residency per `CellSim`** rather than one global
  residency with a thousand foci. That guidance is in the package README, not enforced.
- **That per-`CellSim` guidance has two known holes, and they are DEFERRED rather than solved here**, filed as
  [#341](https://github.com/APKiwiOrg/KhaozEngine/issues/341). A region of an occupied cell that no player's
  ring covers never materializes its `spawns`, so authored server-side content becomes observation-gated in a
  way it is not today (the server holds the whole document). And a player crossing a cell boundary arrives in a
  cell whose residency has no focus there yet, so the first ticks after the crossing run with nothing resident.
  The client-side teleport contract does not transfer, because a shard handoff is a move between two residency
  instances rather than a discontinuous move within one. Closing this needs a cell-scoped minimum resident set,
  a handoff prime, and a stated line between observation-gated content and content that must exist regardless.
  All three want their own spec, which is why it is an issue and not a paragraph.
- A server that wants terrain chunks (for collision or a nav bake rather than a mesh) can now construct a
  `TerrainStreamer` too, because the extraction put it in `KhaozEngine.Terrain`. Its sink builds colliders
  instead of meshes. That is [#269](https://github.com/APKiwiOrg/KhaozEngine/issues/269)'s territory and this
  spec only unblocks it.

### Per-tile validation

`MapDocumentFile.LoadText` validates the WHOLE document and throws `MapDocumentException` naming the path
(`MapDocumentFile.cs:117-119`), which is the format's loud-fail stance: map documents are dev-authored content,
so a bad one fails a boot rather than being quarantined. **A tile read through `MapDocumentSource.ReadTile`
bypasses all of that**, because `MapDocumentValidator.Validate` needs globals a tile file does not carry
(bounds, layer names, the registry-backed kind ids).

So `ReadTile` runs a **per-tile validation subset** and keeps the loud-fail stance: throw
`MapDocumentException` naming the directory, the tile coordinate and the file. The subset is everything
checkable with the manifest's globals plus the tile's own content:

- every placement, spawn and player spawn has a non-empty id, and ids are unique **within the tile** (global
  uniqueness across tiles is a `VerifyTiled` and whole-load check, since one tile cannot see another),
- every kind or asset reference resolves against the `MapDocRegistry`,
- every sculpt tile has exactly `TerrainSculpt.TileSize` squared deltas (`MapSculptTile`'s constructor already
  enforces this and its `ArgumentException` is wrapped),
- every item actually falls inside the tile it was read from, under `MapTileGrid.CoordOf` and section 3's
  origin-corner rule for sculpt. This is the one check a whole-document load cannot make and a tiled load must,
  because it is what catches a hand-edited or tool-generated file whose content does not match its name.

Bounds and cross-tile reference checks stay with the whole-document validator, and `VerifyTiled` is where a
whole-world check lives.

## 12. Decision 10: does terrain-field sampling degrade as resident sculpt grows

The spike's open question (findings section 6): `BuildNavWorld` wall time grew 13x across the density sweep
while its own allocation stayed flat at about 252 MB. Two candidate mechanisms, unresolved by the spike:
sampling getting slower as the field holds more sculpt, or gen2 GC tracing over a 2.6 GB live heap.

**Reading the code makes both mechanisms the same underlying thing, and the design answer is the same either
way.** `TerrainSculpt.SampleDelta` performs **four** `CellDelta` calls per sample for the bilinear corners
(`TerrainSculpt.cs:54-55`), and each one is an independent `Dictionary<long, float[]>` probe
(`TerrainSculpt.cs:64`). At 64 stored tiles that dictionary lives in L1 or L2 and four probes are free. At
640,000 tiles the bucket and entry arrays are tens of megabytes and every probe is a likely cache miss, times
four, times the roughly 800,000 samples `BuildNavWorld` takes. A 13x wall-clock growth with zero change in
allocation is exactly the shape of that. The GC hypothesis has the same driver: a large live sculpt set.

**Decision: per-tile residency bounds the live set, and that is the entire fix. No spatial index is added.**
The arithmetic, done honestly: residency holds tiles out to `UnloadRadius`, not `LoadRadius`, so with the
corrected `MapResidencyConfig.Default` (Chebyshev, LoadRadius 2, UnloadRadius 3) the worst-case resident set is
`(2*3+1)^2 = 49` document tiles, not the 25 of the load ring and not the 9 an earlier draft claimed. At
Ruinborne's 2 m cells a fully authored 512 m tile holds 64 sculpt tiles, so the ceiling is **3,136 resident
sculpt tiles against 640,000 in the failing case**, a factor of 204.

What that does to the mechanism is the point, and it is the dictionary and not the delta arrays that matters.
At 3,136 entries a `Dictionary<long, float[]>` is roughly 90 KB of bucket and entry arrays, comfortably L2
resident, so the four probes per sample are near-free again. At 640,000 entries the same arrays are roughly 18
MB, past any L2, so every probe is a likely miss, times four, times the roughly 800,000 samples. (The delta
arrays themselves are 12.8 MB at the ceiling and are irrelevant to probe cost, since a probe touches one of
them and only after it has already found the entry.) The fix shrinks both candidate mechanisms at once without
needing to know which one it was.

Adding a spatial index would be the wrong move on its own terms: the lookup is already O(1) through a hash, so
an index would insert an indirection into a path that is one probe from its answer.

**What would change this answer**, named so it is falsifiable. If a re-measure after residency ships still shows
sampling cost growing with resident sculpt, the next step is not an index but a flat open-addressed table keyed
on the packed tile id with a power-of-two mask instead of `Dictionary<long, float[]>`, which removes the hash
and one indirection and is entirely internal to `TerrainSculpt`. A cheaper win sits beside it: `SampleDelta`'s
four corner lookups share the same tile for 30 of every 32 cells, so caching the last resolved tile would cut
probes by close to 4x. Both are filed rather than done here, because doing them now would be tuning against an
unisolated mechanism.

## 13. Release split

Two releases, drawn so that release 1 is adoptable on its own and release 2 is purely additive.

### Release 1: format, one minor bump

Packages touched: `KhaozEngine.Terrain`, `KhaozEngine.Terrain.Render3D`, `KhaozEngine.MapDoc`,
`KhaozEngine.MapEditor`, `KhaozEngine.MapEdit.Tool`.

- **Terrain** gains the extracted streamer core (`ChunkCoord`, `ChunkGrid`, `ChunkRing`, `ChunkBuild<T>`,
  `ChunkBuildException`, `ChunkBuildScheduler<T>`, `IChunkBuildDispatcher`, `TaskChunkBuildDispatcher`,
  `IChunkSink`, `IAsyncChunkSink`, `StreamerConfig`, `TerrainStreamer`, `TerrainLod`, `TerrainLodConfig`,
  `TerrainLodTier`, `TerrainChunkRegion`).
- **Terrain.Render3D** loses those types and gains `AssemblyForwarders.cs`.
- **MapDoc** gains `MapTileCoord`, `MapTileRect`, `MapTileGrid`, `MapTileEntry`, `MapTileIndex`,
  `MapSpatialIndex`, `MapDocumentForm`, `MapDocumentHash`, `MapDocumentSource`, `MapTileContent`,
  `MapDocumentSaveOptions`, `MapSaveDurability`, `MapDocument.TileSize`, `MapDocument.Tiles`, the three
  `MapRuntime.BuildPlacements` overloads, `MapDocumentFile.SaveTo` / `SaveTiled` / `SaveAuto` / `SaveAs` /
  `LoadTiled` (both forms) / `DetectForm` / `VerifyTiled` / `DefaultTileSize`, `CurrentFormatVersion = 3`, the
  2 to 3 migration, `MapDocumentLoadOptions.VerifyTileHashes`, and `MapDocumentSchema.GetManifestJson` /
  `GetTileJson` / `WriteAllTo`.
- **MapEditor** and **MapEdit.Tool** become form-aware and window-capable, and `MapEdit.Tool` gains
  `set_window`, `window_status` and `retile`.

`MapTileContent` and `MapDocumentSource` land in release 1 rather than release 2 because windowed loading needs
them and the editor needs windowed loading. **`MapDocumentSource.FromDocument` is the one member of that pair
with no release-1 caller**: windowed loading only ever opens a tiled directory, so wrapping an in-memory whole
document exists purely so a game can adopt residency in release 2 before converting its world. Acknowledged
rather than hidden, and it is why release 2's Ruinborne adoption can start with a repin and no format change.

**`schemeVersion` ships here, in release 1**, in the manifest and on `MapTileIndex`. Section 10 has the reason:
a windowed save mixes stored and recomputed hashes, so the scheme those stored hashes were written under has to
be recorded from the first release, and section 5 explains why `MapTileEntry` in particular cannot grow the
field later.

### Release 2: residency, one minor bump

Packages touched: `KhaozEngine.MapDoc`, `KhaozEngine.Terrain`, `KhaozEngine.Terrain.Render3D`.

- **MapDoc** gains `MapResidencyConfig`, `IMapTileSink`, `MapTileResidency`.
- **Terrain** gains `TerrainField.SetSculpt`, `TerrainSculpt.With`, `IChunkBuildGate`,
  `TerrainStreamer.BuildGate` and `IPlacementSource`.
- **Terrain.Render3D** gains `PropLayer.PlacementSource` plus the `PlacementLayer` factory overload that takes
  an `IPlacementSource`, and `Scene3DChunkSink` learns to read a source instead of a frozen bucket map.

**Nothing in release 2 changes a release 1 SIGNATURE, and that is a narrower claim than "changes nothing".**
Release 2 does change release 1 internals, in three places worth naming so an adopter is not surprised.
`TerrainField`'s sculpt field stops being `readonly` and becomes `volatile`. `SampleNormal` snapshots that
field once per call instead of re-reading it inside each `SampleHeight`. And `Scene3DChunkSink`'s placement
fetch gains a branch. With no `SetSculpt` call and no source-backed layer, all three are behaviourally
identical to release 1, so a consumer that never touches residency sees no change. That is a real guarantee,
just not the same guarantee as an untouched implementation.

## 14. Ruinborne adoption

### After release 1

1. Repin `<KhaozEngineVersion>` and refresh the vendored feed. No source edits are needed for the streamer
   extraction: `RuinborneWorldView`'s `TerrainStreamer` and `StreamerConfigFor` compile unchanged.
2. `RuinborneWorld.ComputeWorldHash` (`Ruinborne.Core/RuinborneWorld.cs:46-47`) stops calling
   `MapDocumentFile.SaveText`. It passes `MapDocumentHash.OfWorld(Document)` into
   `RuinborneWorldIdentity.ComputeHash` instead of the canonical text. That is a one-parameter change on the
   Ruinborne side and it kills the measured 3.34 s and 6.14 GB, plus the separate `SaveText` those numbers sat
   on top of. **The world hash value changes**, so client and server must ship together for that release, which
   they already do.
3. Convert `island.map.json` to `island.map/` with `ke-mapedit convert_to_tiled`, or leave it monolithic. Either
   works. The island spans four 512 m tiles, so converting is a genuine smoke test of the tiled path against
   real content at zero risk.
4. `Ruinborne.Editor/EditorMaps.cs:36` switches `MapDocumentFile.Save` to `SaveAuto`.
5. Re-run the `ScaleSpike` document sweep against the tiled writer. The two OOM cases are the regression target.

### After release 2

1. Replace `RuinborneWorld.Document`'s eager whole-document `Lazy<MapDocument>`
   (`Ruinborne.Core/RuinborneWorld.cs:60-71`) with a `MapDocumentSource`. `MapDocumentSource.FromDocument` keeps
   the current behaviour, so this step can land before the world is tiled.
2. Client: construct one `MapTileResidency` beside the existing `TerrainStreamer` in `RuinborneWorldView`, run
   `residency.Update` before `_streamer.Update` in the same frame, set
   `_streamer.BuildGate = residency.GateFor(_streamerConfig.ChunkSize, sculptCellSize)`, and call
   `MapResidencyConfig.ValidateAgainst(_streamerConfig, tileSize, sculptCellSize)` at wiring time. The
   validation must run against the WIDEST render-distance profile, not the active one, because the profile is a
   runtime setting and a config that only validates on Low is a hole in the world on Ultra.
3. Client: route every teleport, zone change and camera jump through the teleport contract
   (`residency.PrimeAround(newFocus)` then `_streamer.UnloadAll()` before the next `_streamer.Update`). This is
   the step most likely to be missed, because without it the world looks right within a few frames and the
   failure only shows as a brief fall-through on arrival.
4. Client: the authored-decor `PropLayer` switches from a frozen placement list to
   `PropLayer.PlacementLayer(residency, ...)`, so streamed placements reach the renderer at all.
5. Server: one `MapTileResidency` per `CellSim`, driven from the cell's own player positions, with
   [#341](https://github.com/APKiwiOrg/KhaozEngine/issues/341)'s two holes understood before this ships to a
   live shard.
6. Physics: `RuinbornePhysics.Populate` moves off game-global statics and onto `IMapTileSink.TileLoaded` /
   `TileUnloaded`, adding and removing static bodies per tile. That is the Ruinborne-side work this spec's seam
   exists to enable and it is tracked on the Ruinborne side.
7. Sculpt: the `TileLoaded` handler rebuilds the snapshot with `TerrainSculpt.With`, calls
   `TerrainField.SetSculpt`, and calls `TerrainStreamer.Invalidate` for the tile's rect.

## 15. Test plan

Every item is a headless test. MapDoc tests live in `KhaozEngine.MapEditor.Tests` (the CI selective-test design
assigns the `MapDoc` folder there). The extracted streamer's tests stay in `KhaozEngine.Render.Tests`.

**Determinism, the load-bearing group.** Modelled on `MapDocDeterminismTests.ChunkedEnumeration_EqualsWholeZone`,
which already compares key-sorted sets rather than sequences, so canonical reordering inside a tile is not a
false failure.

- `TiledLoad_EqualsWholeLoad`: build a synthetic document, `SaveTiled`, `LoadTiled` whole, and compare
  `MapRuntime.BuildPlacements` and a grid of `TerrainField.SampleHeight` probes against the original.
- `TiledPartialLoads_UnionEqualsWhole`: load each tile window separately, union the results, compare to whole.
- `RegionScopedPlacements_PartitionEqualsWhole`: `BuildPlacements(doc, field, rect)` over a covering rect grid
  equals the whole-document call. The grid deliberately does not divide the bounds evenly, the way the existing
  test uses 30 m against a 200 m zone.
- `PlacementOnTileBoundary_BelongsToExactlyOneTile`: a placement at exactly `tileSize` on each axis, and at a
  negative boundary, lands in one bucket and only one.
- `MapTileGrid_AgreesWithChunkGrid`: fuzz positive, negative and on-boundary positions.

**OOM regression, as a scaling proxy rather than an absolute.**

- `SaveTo_DoesNotBufferTheDocument`: write a document whose serialized form exceeds 200 MB into a counting
  stream and assert the `GC.GetTotalMemory(true)` delta across the write is under a small fixed bound. Sharp,
  fast, machine independent, and it targets the measured failure directly.
- `SaveTiled_SerializationBufferStaysFlatAsTileCountDoubles`: **the first draft of this test asserted something
  a correct implementation cannot satisfy**, because it measured retained memory across a `SaveTiled` whose
  step 1 validates and whose step 2 buckets the whole document, both of which hold everything. What is flat is
  the SERIALIZATION buffer, one tile at a time, not the process. So the test saves a 4,096-tile and then an
  8,192-tile synthetic world and asserts that the `GC.GetTotalMemory(false)` DELTA measured across the write
  itself (sampled after bucketing, before the first tile file, and again after the last) is within noise of
  each other, while allocated grows roughly linearly. A scaling assertion on the right quantity rather than a
  machine-specific byte budget on the wrong one. 8,192 tiles is comfortably past the 6,400 that succeeded
  monolithically and short of the 64,000 that needs 262 MB of deltas just to construct.

**Save durability.** Every one of these fakes the crash by aborting the writer at a named step, so they run
headless and deterministically with no process kill.

- `CrashBeforeFirstTileWrite_LeavesPreviousVersionIntact`, `CrashBeforeManifestRename_LeavesPreviousVersionIntact`,
  `CrashDuringSweep_LeavesNewVersionIntact`. Each asserts the document still LOADS and equals the expected
  whole version, which is the invariant, rather than asserting on files.
- `ResaveAfterCrash_SweepsOrphansAndTmpFiles`.
- `ResaveWithUnreadableManifest_RewritesEverythingAndSkipsTheSweep`: locks the deliberate refusal to delete on
  the authority of a manifest that did not parse.
- `UnchangedTile_IsNotRewritten`: touch one tile of a many-tile world, assert every other tile file's
  last-write time is unchanged. This is the test for the windowed-save efficiency claim.
- `PartialDocument_RefusedByEverySaveEntryPoint`: `Save`, `SaveText`, `SaveTo`, `SaveAuto`, `SaveAs`, each
  asserted to throw. Parameterised, so a save entry point added later without the guard fails here.
- `SaveTiled_ThrowsWhenAnItemMovedIntoAnUnloadedTile`.

**Hash.**

- `TileHash_IsOrderIndependent`: the same content added in two orders gives the same tile hash.
- `WorldHash_MonolithicEqualsTiled`: the same world in both forms gives the same `OfWorld`, run for BOTH a
  null and an empty `terrainOverrides` block so section 8's normalization rule is locked.
- `WorldHash_ChangesOnSculptDelta`, `_ChangesOnPlacementMove`, `_ChangesOnBoundsChange`, `_ChangesOnRetile`
  (the last one pins `tileSize` as part of world identity).
- `ConvertRoundTrip_PreservesTileSize`: monolithic to tiled to monolithic leaves `tileSize` and the world hash
  untouched, which is the converter rule from section 8.
- `WorldHash_UnchangedOnDisplayNameChange`: locks the deliberate exclusion.
- `WorldHash_ReadsFromManifestIndex`: `OfWorld` on a tiled source with every tile file deleted after the
  manifest was written still returns the right value, proving no tile file is opened.
- `Compose_ThrowsOnUnorderedEntries`: locks the monotonicity assert.
- `WorldHash_MatchesGoldenDigest`: a hard-coded digest for a fixed fixture whose tile coordinates include
  negatives. **Plus an `sv-SE` variant of the same test**, running under
  `CultureInfo.CurrentCulture = sv-SE` and asserting the identical digest and the identical generated file
  names. This is the one that catches the U+2212 divergence, and the plain variant cannot, because CI runs
  under the invariant or an en-* culture where the bug is invisible.
- `VerifyTiled_ReportsAHandEditedTile`, `VerifyTiled_ReportsOrphansAndTmpFiles`.

**Migration and schema.**

- `V2Document_LoadsAtV3WithDefaultTileSize`, `V1Document_StillLoadsThroughTheChain`,
  `V3Document_RejectedByAnOlderVersionCheck` (asserts the existing newer-than-supported message path).
- `WindowedLoad_RefusesOnSchemeVersionMismatch` and `WholeLoadThenSave_UpgradesSchemeVersion`.

**Editor.**

- `OpenTiledDirectory_DoesNotStartABlankDocument`: the regression test for the `File.Exists` gate. Points the
  editor at a tiled directory and asserts the document has the expected content, not that it is untitled.
- `SaveTiled_RefusesToWriteANullIndexOverATiledDirectory`: the belt, independent of the gate.
- `SaveAuto_ThrowsForANonExistentPath` and `SaveAs_WritesTheNamedFormRegardlessOfExtension`, the latter run
  against `island.map` specifically, since `Path.GetExtension` returns `".map"` for it and that is the exact
  input that routed the first draft wrong.
- `SetWindow_ThrowsWhenDirtyWithoutDiscard` and `SetWindow_ClearsUndoHistory`.

**Residency (release 2), mirroring `TerrainStreamerTests` and `TerrainAsyncStreamerTests`.**

- `PrimeAround_FillsTheRing`, `OscillatingFocus_DoesNotChurn` (hysteresis),
  `TileUnloaded_FiresExactlyOncePerDeparture`, `RingChange_FiresRingChangedNotLoadUnload`.
- `ResidentSetIsAChebyshevSquare`: at `LoadRadius` 1 the resident set is 9 tiles including the diagonals, not
  the 5-tile plus-shape a Euclidean ring would give. This is the regression test for the geometry bug.
- `AbsentTile_IsNeverRead`: a source that throws on `ReadTile` for an unindexed coord, asserting residency
  consults the index first. This is the test for the decisive reason in section 11.
- `MultiFocus_ResidentSetIsTheUnion`, `TileStaysResidentWhileAnyFocusKeepsIt`, and
  `MultiFocus_RingIsStrongestWinsRegardlessOfFocusOrder` (feed the same foci in both orders, assert identical
  rings, which is what stops the collider flap).
- `AsyncLoads_ApplyInNearestFirstOrder` and `CancelledLoadIsDiscarded`, driven by a manual
  `IChunkBuildDispatcher` for controlled completion order.
- `ValidateAgainst_RejectsAStreamerRingWiderThanResidency`, plus
  `ValidateAgainst_UsesUnloadRadiusNotOuterRadius` and `ValidateAgainst_SubtractsTheSculptSpan`, each pinning
  one of the two arithmetic corrections so a later simplification cannot quietly undo them.
- `BuildGate_DefersAChunkOverANonResidentTile` and `BuildGate_AllowsAChunkOverAnAbsentTile`. The second is the
  one that matters: gating on absence would deadlock the streamer over empty world.
- `BuildGate_WaitsForTheSculptOwningNeighbour`: a chunk on a document tile's low-X edge is deferred until the
  neighbouring tile that OWNS its sculpt arrives.
- `StreamedPlacements_ReachTheSink`: a placement arriving with a streamed tile renders after the tile's
  `Invalidate`, driven through a fake sink. Without the `IPlacementSource` seam this fails, which is the point.
- `SetSculpt_IsSafeAgainstAConcurrentSampler`: hammer `SampleHeight` **and `SampleNormal`** on worker threads
  while swapping snapshots, and assert every sampled height AND every sampled normal belongs to one of the two
  snapshots. The `SampleNormal` arm is the load-bearing half: a `SampleHeight`-only test passes against an
  implementation that reads the field five times per normal.
- `SetSculpt_NormalizesAnEmptySculptToNull`: assert the normal epsilon returns to the 1 m analytic value, since
  that is the observable consequence of the constructor's normalization rule.

**Architecture.**

- `Terrain_HasNoRender3DOrPhysicsReference`, in the `KhaozEngine.Tests` rump, locking the extraction. The rump
  is correct here and not a compromise: it has no `ProjectReference` to any engine package and asserts on
  csproj XML, so a test that a reference does not exist does not itself create one.

## 16. Deferred, with the reason

Each of these becomes a GitHub issue as this spec lands, per the discovered-work rule. A follow-up recorded only
in a design doc is invisible to the ledger.

- **Region-scoped scatter config.** `MapRuntime.BuildScatterConfig` still builds a whole-world `ScatterConfig`.
  It is cheap today (layer definitions plus shapes, all global), and scoping it needs its own determinism
  argument about override ordering. Not blocking.
- **`TerrainSculpt` corner-lookup caching and the flat open-addressed table.** Named in section 12 as the next
  step if a post-residency re-measure still shows sampling growth. Filed rather than done, because doing it now
  is tuning against an unisolated mechanism.
- **Re-measure the spike after release 2.** The trigger for the item above, and the only way to close the
  findings doc's section 6 question properly.
- **Region-scoped editing beyond a window.** The window in section 10 lets the editor open and save a large
  tiled world. Editing across a window boundary, moving a placement from one tile to another that is not
  loaded, and a whole-world search all need more. Section 10 makes the moved-into-an-unloaded-tile case throw
  rather than silently clobber, which is the correct interim behaviour and not the feature.
- **Per-`CellSim` server residency holes**
  ([#341](https://github.com/APKiwiOrg/KhaozEngine/issues/341), filed). Section 11's headless-server guidance
  leaves authored server-side content observation-gated in regions no player ring covers, and leaves a cell
  boundary crossing arriving in a cold cell. Needs a cell-scoped minimum resident set, a handoff prime, and a
  stated line between observation-gated and always-present content. Deferred deliberately: all three want their
  own spec.
- **Tiled nav bake** ([#269](https://github.com/APKiwiOrg/KhaozEngine/issues/269)) consumes the same tile
  granularity and the same arrival and departure events. Nothing here blocks it and nothing here does it.
- **Editor `StreamerConfig` surface** ([#282](https://github.com/APKiwiOrg/KhaozEngine/issues/282)) is no
  longer unaffected: 17.5.0 added `RenderDistanceProfile`/`RenderDistanceTier` (`KhaozEngine.Terrain`) and
  `MapEditorOptions.RenderDistance`, so `ViewportWorld` now builds its `StreamerConfig` from
  `_renderDistance.ToStreamerConfig().Synchronous()` instead of the hardcoded
  `StreamerConfig.Default.Synchronous()` this section originally described, and `MapEditorScene` applies
  the same profile's far clip to the fly camera. This does not retire the item on its own (no editor UI
  settings row exists yet), but the config surface it asked for now exists at the options level.
