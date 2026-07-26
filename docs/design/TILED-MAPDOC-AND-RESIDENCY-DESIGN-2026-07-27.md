# Tiled map document format and document residency (2026-07-27)

Status: design approved, implementation pending. Program issues:
[#334](https://github.com/APKiwiOrg/KhaozEngine/issues/334) (tiled format) and
[#335](https://github.com/APKiwiOrg/KhaozEngine/issues/335) (residency). Both are engine sub-projects of the
Ruinborne 100 km world program, [Ruinborne#242](https://github.com/APKiwiOrg/Ruinborne/issues/242).

Joint spec because the tile granularity decision is shared: the format's tile is the residency unit, and
deciding one without the other guarantees two grids.

Measurement evidence: `Ruinborne/docs/design/2026-07-26-world-scale-spike-findings.md` plus the CSV beside it,
measured on engine 16.3.1 against unmodified production code.

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
placements at roughly 250 bytes, about 1.2 GB. A 2.5 GB string is over the roughly 2 GB per-object cap.
At extent-100000m the same arithmetic gives roughly 970 MB, under the cap, which is exactly the case that
succeeded. The ceiling is structural in "one big string" and a bigger machine does not move it.

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
position and does not care which file a tile arrived in. The one consequence for residency is that sculpt
coverage lags the tile ring by up to one sculpt span at the edge, which sits well inside the hysteresis band.

Rejected: keying document tiles on sculpt tile coordinates directly. It ties the document layout to a per-world
authoring choice, so two worlds with different `cellSize` get different file granularity for the same physical
area, and changing `cellSize` would rewrite every file.

## 4. Decision 1: on-disk layout

**A document is either a single file or a directory. Both are first class and both are supported forever.**

```
island.map/                        the tiled form: a directory
  map.json                         root manifest
  tiles/
    s_0_0/                         shard dir, shard = tile >> 4 (arithmetic, floors for negatives)
      t_0_0.json
      t_3_-2.json
    s_-1_0/
      ...
```

- Shard directories cap a directory at 256 tile files. They are a filesystem and git nicety, never a load unit.
  Nothing ever reads a shard.
- File names carry the signed tile coord verbatim (`t_-3_12.json`).
- JSONC is tolerated on read for the manifest and every tile file, through the same `Jsonc.ParseNode` the
  monolithic loader uses. The engine never writes JSONC, matching `Jsonc`'s own stated read-time-only policy.
- Tile files are written indented, because human-diffable git-committed content is a founding promise of this
  format. The hash (section 8) is over a separate compact canonicalization, so indentation never affects
  identity.

**Which form a document is in is decided by the path, not by a flag.** `MapDocumentFile.Load(path)` on a file
loads monolithic. On a directory it loads tiled and fails loudly if there is no `map.json`. A flag inside the
manifest declaring the form would be redundant with the path and could disagree with it.

```csharp
public enum MapDocumentForm { Monolithic, Tiled }

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
| `bounds` | One rect for the world |
| `tileSize`, `sculptCellSize` | Grid headers. A per-tile copy could disagree with its neighbours |
| `terrain` (seed, water level, biome bands, ordered features) | Analytic base, global by construction. `MapRuntime.BuildTerrainConfig` needs all of it at once and it is a few KB |
| `scatterLayers`, `companionLayers` | Layer definitions. `BuildScatterConfigs` builds every layer together and a companion layer resolves its host by name across the whole set |
| `exclusions`, `scatterOverrides`, `regions` | See below |
| `tiles` | The occupied-tile index: `[{ "x": i, "z": j, "hash": "..." }]`, ascending (z, then x) |

Each `tiles/s_*/t_*.json` carries exactly four lists, and nothing else:

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
}
```

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
    public IReadOnlyCollection<MapTileCoord> OccupiedTiles { get; }

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

    /// <summary>Writes the tiled form. Peak memory is one tile.</summary>
    public static void SaveTiled(MapDocument doc, string directory, MapDocRegistry? registry = null);

    /// <summary>Dispatches on the path's form (existing directory or extension-less path to SaveTiled,
    /// otherwise Save). What the editor and the MCP tool call, so a document saves back in the form it opened.</summary>
    public static void SaveAuto(MapDocument doc, string path, MapDocRegistry? registry = null);
}
```

`SaveTo` alone is the cheapest single fix in this spec and it lands first. It moves the monolithic ceiling off
the .NET per-object cap and onto disk, so the 64,000-tile case that OOM'd now writes a 2.5 GB file instead of
throwing. That is not a good outcome, it is just no longer a failure, and the tiled form is what makes it a good
outcome.

`SaveTiled` order of operations, which is also its durability story:

1. `MapDocumentValidator.Validate` once over the whole document. Refusing to write an invalid document is
   existing behaviour (`MapDocumentFile.cs:133-135`) and it does not change.
2. Bucket the four point-shaped lists into tiles.
3. Per occupied tile: open `t_x_z.json.tmp`, stream the tile through a `Utf8JsonWriter` while a parallel
   compact canonical writer feeds an `IncrementalHash` (section 8), then `File.Move(overwrite: true)` onto the
   real name. Two serializations, zero buffering, one tile live at a time.
4. Delete tile files for tiles the index no longer marks occupied.
5. Write `map.json` **last**, including the freshly computed per-tile hashes.

Manifest-last means a crash mid-save leaves the previous manifest indexing a consistent older tile set, plus
some orphan newer files. The loader only ever opens tiles the manifest indexes, so orphans are inert and the
next successful save overwrites them. Rejected: write to a sibling temp directory and rename. Directory rename
over an existing directory is not atomic on any of the three platforms, so it trades a real guarantee for the
appearance of one.

**Partial saves.** A document loaded through a window (section 10) has unloaded tiles in its index. `SaveTiled`
writes files only for loaded tiles and carries unloaded index entries through verbatim. It throws when a
partially loaded document is saved to a directory other than the one it was loaded from, because the unloaded
tile files would not be there. Loud, specific, and impossible to get wrong silently.

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
(tileZ, tileX) order (`MapTerrainOverrides.Tiles`). **Placements, spawns and player spawns are sorted by ordinal
`Id` before hashing.** Ids are already validated unique within a document (`MapDocumentValidator.cs:67-89`), so
the sort is total, and it makes the hash depend only on content. Two authoring sessions that added the same
placements in a different order produce the same world hash.

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

    /// <summary>Composes a world identity from the manifest hash and the per-tile hashes, ascending (Z, X).</summary>
    public static string Compose(string manifestHash, IEnumerable<MapTileEntry> tiles);

    /// <summary>The world identity of a document. On a tiled document this reads the hashes out of the manifest
    /// index and never opens a tile file.</summary>
    public static string OfWorld(MapDocument doc, MapDocRegistry? registry = null);
}
```

Composition input is `"kemap/" + SchemeVersion + "\n"`, then the manifest hash and a newline, then one
`"{x},{z}={hash}\n"` line per occupied tile in ascending (Z, X) order. Labelled and newline delimited for the
same reason `RuinborneWorldIdentity` is: a value can never slide from one field into the next.

**What is in the hash and what is not.** `displayName` and `$schema` are excluded. The hash exists to answer
"is the ground under this player the same ground", and renaming a zone must not desync a live server from its
clients. Everything that shapes terrain, scatter, or authored content is included. This is a deliberate
narrowing relative to Ruinborne's current behaviour, which hashes the entire `SaveText` output including the
display name.

**Cost.** `OfWorld` on a tiled document composes 24,398 lines of about 40 bytes, roughly 1 MB of hash input,
milliseconds. That replaces the measured 3.34 s and 6.14 GB. `OfWorld` on a monolithic document buckets in
memory and hashes each bucket with the same canonical writer, so **the two forms of the same world produce the
same identity**. That is the property that lets Ruinborne convert to tiled without a coordinated client and
server flag day.

**What invalidates a tile hash:** any byte of that tile's canonical content. **What invalidates the world hash:**
any tile hash, the manifest hash, or the set of occupied tiles.

**Trust and verification.** `SaveTiled` computes every hash as it writes, so the index is never stale relative to
the files it was written with. Load does **not** verify by default
(`MapDocumentLoadOptions.VerifyTileHashes = false`), because verification means re-serializing the parsed tile
canonically and that is exactly the per-load cost this design exists to remove. Verification is a content check,
not a hot path:

```csharp
/// <summary>Re-derives every tile hash from its file and reports mismatches. Empty means clean. For the
/// editor's save-time check, a CI content gate, and the ke-mapedit validate verb.</summary>
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
can point `$schema` at its own:

```csharp
public static class MapDocumentSchema
{
    public static string GetJson();            // monolithic document, existing
    public static string GetManifestJson();    // map.json
    public static string GetTileJson();        // tiles/s_*/t_*.json
    public static void WriteTo(string path);   // existing
    public static void WriteAllTo(string directory);
}
```

**The editor.** `MapEditorScene` loads with `MapDocumentFile.Load(_options.DocumentPath, ...)`
(`MapEditorScene.cs:401`) and saves with `MapDocumentFile.Save(...)` (`MapEditorScene.cs:1292`). Both become
form-aware: `Load` already dispatches on the path, and the save becomes `SaveAuto`. **The editor saves back in
the form it opened**, never converting implicitly. Deliberate conversion is two explicit `ke-mapedit` verbs,
`convert_to_tiled` and `convert_to_single`.

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

`MapEdit.Tool` (`ke-mapedit`, 68 verbs) follows the same rule through `MapEditSession.Open`
(`MapEditSession.cs:34`) and its two save paths (`:75`, `:92`), and gains `set_window` and `window_status`. Its
`validate` verb currently does `MapDocumentFile.SaveText` plus schema validation (`MapEditSession.cs:116`),
which walks straight into the ceiling on a large document. It switches to `MapDocumentValidator.Validate` plus
per-tile schema validation over the loaded window, and delegates whole-world checking to `VerifyTiled`.

## 11. Decision 8b and 9: document residency

Release 2. Everything here is new public API in `KhaozEngine.MapDoc`, which is GPU-free and already in both the
`Foundation` and (through it) the `Server` umbrella, so **both heads get it with no new package and no new
umbrella row**.

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

    /// <summary>Reads and parses one tile. Pure, thread-safe, no shared mutable state, so residency runs it on
    /// a worker thread. Throws MapDocumentException for a tile the index does not mark occupied.</summary>
    public MapTileContent ReadTile(MapTileCoord coord);
}

/// <summary>One tile's parsed content. Immutable once handed to a sink.</summary>
public sealed class MapTileContent
{
    public MapTileCoord Coord { get; }
    public IReadOnlyList<MapPlacement> Placements { get; }
    public IReadOnlyList<MapSpawn> Spawns { get; }
    public IReadOnlyList<MapPlayerSpawn> PlayerSpawns { get; }
    public IReadOnlyList<MapSculptTile> SculptTiles { get; }
}

/// <summary>Residency tuning. Radii are in TILE units, Euclidean tile distance, mirroring StreamerConfig's
/// ring semantics exactly. A distinct type because StreamerConfig's ChunkSize and LodConfig are meaningless
/// here, and a shared type would let a chunk config be passed to document residency by accident.</summary>
public readonly record struct MapResidencyConfig(
    int LoadRadius, int UnloadRadius, int MaxLoadsPerUpdate, int DecorRadius = 0, bool Async = true)
{
    /// <summary>LoadRadius 1, UnloadRadius 3, 2 applies per update, no decor ring. At the 512 m default tile
    /// that is a 1.5 km gameplay square around the focus with a 2-tile hysteresis band.</summary>
    public static MapResidencyConfig Default { get; }

    public int OuterRadius { get; }
    public MapResidencyConfig Synchronous();

    /// <summary>Errors (empty means fine) when this config cannot cover a terrain streamer's chunk ring, so a
    /// chunk can never build against a non-resident tile. Checked by the consumer at wiring time.</summary>
    public IReadOnlyList<string> ValidateAgainst(StreamerConfig streamer, float tileSize);
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

    public IReadOnlyCollection<MapTileCoord> Resident { get; }
    public ChunkRing? RingOf(MapTileCoord coord);
    public bool TryGetContent(MapTileCoord coord, out MapTileContent content);

    /// <summary>Client form: one focus.</summary>
    public void Update(Vector3 focus);

    /// <summary>Server form: the union of the rings around every focus. A tile leaves residency only when no
    /// focus keeps it.</summary>
    public void Update(ReadOnlySpan<Vector3> foci);

    /// <summary>Deterministic blocking fill of the whole ring, for a loading moment.</summary>
    public void PrimeAround(Vector3 focus);
    public void FlushPendingLoads();

    /// <summary>Re-read one resident tile from the source (an editor wrote it, a tool regenerated it).</summary>
    public void Invalidate(MapTileCoord coord);

    public void UnloadAll();
    public void Dispose();
}
```

**Ring semantics are `TerrainStreamer`'s, deliberately.** Load out to `OuterRadius`, unload past
`UnloadRadius` with `UnloadRadius > OuterRadius` enforced in the constructor, nearest-first, `MaxLoadsPerUpdate`
applies per update, immediate unloads. `ChunkRing.Gameplay` and `ChunkRing.Decor` are reused as-is: a gameplay
tile is one the simulation touches, a decor tile is one a client wants for far-field render only. That is the
same distinction the chunk streamer already draws and a second enum for it would be pure duplication.

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

The consumer contract, stated as a hard rule and checkable:

> `MapResidencyConfig.LoadRadius * tileSize` must exceed `StreamerConfig.OuterRadius * ChunkSize + ChunkSize`,
> and `MapTileResidency.Update` must be called before `TerrainStreamer.Update` in the same frame.

Then a chunk can never build against a non-resident tile, and the residency hysteresis band absorbs the chunk
ring's own oscillation. `MapResidencyConfig.ValidateAgainst(StreamerConfig, float tileSize)` returns the error
strings so a wiring mistake is loud at startup instead of a hole in the world at 1.5 km.

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
    /// torn state, and both are valid terrain.</summary>
    public void SetSculpt(TerrainSculpt? sculpt);
}

public sealed class TerrainSculpt
{
    /// <summary>This sculpt with tiles added and removed, sharing every unchanged tile's delta array by
    /// reference. O(tile count), not O(cell count).</summary>
    public TerrainSculpt With(IEnumerable<TerrainSculptTile>? add, IEnumerable<(int TileX, int TileZ)>? remove);
}
```

The consumer's `TileLoaded` handler rebuilds the snapshot with `With`, calls `SetSculpt`, then calls
`TerrainStreamer.Invalidate(MapTileGrid.AreaOf(coord, tileSize))`, which is exactly the partial invalidation
seam `Invalidate(RectArea)` was built for. Snapshot rebuild is O(resident tiles) with no delta array copied, so
at a few hundred resident tiles it is microseconds.

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
  recomputed each update, so no reference counting is needed. Cost is O(foci * ring area): at 100 players and a
  5 by 5 ring that is 2,500 coordinate tests, trivial. Past a few hundred foci a shard server should drive **one
  residency per `CellSim`** rather than one global residency with a thousand foci. That guidance is in the
  package README, not enforced.
- A server that wants terrain chunks (for collision or a nav bake rather than a mesh) can now construct a
  `TerrainStreamer` too, because the extraction put it in `KhaozEngine.Terrain`. Its sink builds colliders
  instead of meshes. That is [#269](https://github.com/APKiwiOrg/KhaozEngine/issues/269)'s territory and this
  spec only unblocks it.

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
With `MapResidencyConfig.Default` at the 512 m tile, Ruinborne's 2 m cells give at most 9 document tiles times
64 sculpt tiles, about 576 resident sculpt tiles against 640,000 in the failing case. That is a few megabytes,
back in cache, and it shrinks both candidate mechanisms at once without needing to know which one it was.

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
  `MapDocument.TileSize`, `MapDocument.Tiles`, the three `MapRuntime.BuildPlacements` overloads,
  `MapDocumentFile.SaveTo` / `SaveTiled` / `SaveAuto` / `LoadTiled` (both forms) / `DetectForm` / `VerifyTiled` /
  `DefaultTileSize`, `CurrentFormatVersion = 3`, the 2 to 3 migration,
  `MapDocumentLoadOptions.VerifyTileHashes`, and `MapDocumentSchema.GetManifestJson` / `GetTileJson` /
  `WriteAllTo`.
- **MapEditor** and **MapEdit.Tool** become form-aware and window-capable.

`MapTileContent` and `MapDocumentSource` land in release 1 rather than release 2 because windowed loading needs
them and the editor needs windowed loading.

### Release 2: residency, one minor bump

Packages touched: `KhaozEngine.MapDoc`, `KhaozEngine.Terrain`.

- **MapDoc** gains `MapResidencyConfig`, `IMapTileSink`, `MapTileResidency`.
- **Terrain** gains `TerrainField.SetSculpt` and `TerrainSculpt.With`.

Nothing in release 2 changes a release 1 signature.

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
   `residency.Update` before `_streamer.Update` in the same frame, and call
   `MapResidencyConfig.ValidateAgainst(_streamerConfig, tileSize)` at wiring time. The existing render-distance
   profiles feed the streamer config exactly as they do now, and the residency radii are chosen to cover the
   widest profile.
3. Server: one `MapTileResidency` per `CellSim`, driven from the cell's own player positions.
4. Physics: `RuinbornePhysics.Populate` moves off game-global statics and onto `IMapTileSink.TileLoaded` /
   `TileUnloaded`, adding and removing static bodies per tile. That is the Ruinborne-side work this spec's seam
   exists to enable and it is tracked on the Ruinborne side.
5. Sculpt: the `TileLoaded` handler rebuilds the snapshot with `TerrainSculpt.With`, calls
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
- `SaveTiled_PeakStaysFlatAsTileCountDoubles`: save a 4,096-tile and then an 8,192-tile synthetic world and
  assert retained memory after each is within noise of the other while allocated grows roughly linearly. A
  scaling assertion rather than a machine-specific byte budget. 8,192 tiles is comfortably past the 6,400 that
  succeeded monolithically and short of the 64,000 that needs 262 MB of deltas just to construct.

**Hash.**

- `TileHash_IsOrderIndependent`: the same content added in two orders gives the same tile hash.
- `WorldHash_MonolithicEqualsTiled`: the same world in both forms gives the same `OfWorld`.
- `WorldHash_ChangesOnSculptDelta`, `_ChangesOnPlacementMove`, `_ChangesOnBoundsChange`.
- `WorldHash_UnchangedOnDisplayNameChange`: locks the deliberate exclusion.
- `WorldHash_ReadsFromManifestIndex`: `OfWorld` on a tiled source with every tile file deleted after the
  manifest was written still returns the right value, proving no tile file is opened.
- `WorldHash_MatchesGoldenDigest`: a hard-coded digest for a fixed fixture. This is the one that catches a
  culture, float-format or canonicalization regression across platforms.
- `VerifyTiled_ReportsAHandEditedTile`.

**Migration.**

- `V2Document_LoadsAtV3WithDefaultTileSize`, `V1Document_StillLoadsThroughTheChain`,
  `V3Document_RejectedByAnOlderVersionCheck` (asserts the existing newer-than-supported message path).

**Residency (release 2), mirroring `TerrainStreamerTests` and `TerrainAsyncStreamerTests`.**

- `PrimeAround_FillsTheRing`, `OscillatingFocus_DoesNotChurn` (hysteresis),
  `TileUnloaded_FiresExactlyOncePerDeparture`, `RingChange_FiresRingChangedNotLoadUnload`.
- `AbsentTile_IsNeverRead`: a source that throws on `ReadTile` for an unindexed coord, asserting residency
  consults the index first. This is the test for the decisive reason in section 11.
- `MultiFocus_ResidentSetIsTheUnion` and `TileStaysResidentWhileAnyFocusKeepsIt`.
- `AsyncLoads_ApplyInNearestFirstOrder` and `CancelledLoadIsDiscarded`, driven by a manual
  `IChunkBuildDispatcher` for controlled completion order.
- `ValidateAgainst_RejectsAStreamerRingWiderThanResidency`.
- `SetSculpt_IsSafeAgainstAConcurrentSampler`: hammer `SampleHeight` on worker threads while swapping
  snapshots and assert every sampled value belongs to one of the two snapshots.

**Architecture.**

- `Terrain_HasNoRender3DOrPhysicsReference`, in the `KhaozEngine.Tests` rump, locking the extraction.

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
  loaded, and a whole-world search all need more.
- **Tiled nav bake** ([#269](https://github.com/APKiwiOrg/KhaozEngine/issues/269)) consumes the same tile
  granularity and the same arrival and departure events. Nothing here blocks it and nothing here does it.
- **Editor `StreamerConfig` surface** ([#282](https://github.com/APKiwiOrg/KhaozEngine/issues/282)) is
  unaffected. It wants a `StreamerConfig` on `ViewportWorld` (`ViewportWorld.cs:328` hardcodes
  `StreamerConfig.Default.Synchronous()`), which stays exactly the type it is today.
