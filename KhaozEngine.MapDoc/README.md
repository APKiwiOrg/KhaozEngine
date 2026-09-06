# KhaozEngine.MapDoc

The KhaozEngine zone/map document format: one JSON file per zone capturing what used to be world code
(terrain, procedural scatter, authored placements, spawns, regions), versioned and schema-validated, with
runtime builders that hand games back the exact objects they already consume. Human-diffable,
git-committed in the game repo. GPU-free.

## Sections

A map document (`MapDocument`) has:

- **`terrain`** - seed, water level, biome blend, gentle/detail noise frequency and amplitude, biome
  bands, and an ordered list of parametric features (`lake`, `flatten`, `ridge`, `rim` built in),
  resolved through an extensible `MapDocRegistry` so a game can add its own without an engine change.
  A feature carries an optional `Name` (`MapFeature.Name`, default null (null or empty means unnamed), unique when non-empty within
  the features list): the base feature type is open in the schema (only `type` is required), so no
  schema change was needed to add it.
- **`scatterLayers`** - named procedural scatter layers (cell size, jitter, per-biome density and weighted
  kind mix), one per prop type (trees, rocks, ...).
- **`companionLayers`** - named layers that ring hosts from a scatter layer with small foliage (ferns
  around trees). `HostKinds` filters which host placements grow companions: an empty or absent list
  matches every host kind in the host layer, a populated list keeps the old exact ordinal filter. This is
  a behavior-visible semantics change from earlier versions, where an empty list meant no companions at
  all: a document authored against the old behavior with an accidentally-empty `HostKinds` now grows
  companions on every host, so re-check any existing companion layer that left `HostKinds` empty.
- **`exclusions`** (`MapExclusion`) - shapes (disc/rect/polygon) kept free of scatter, optionally scoped to
  specific layers via `Layers` (null means every layer, an explicit list names only those). Builds into
  `ScatterConfig.Exclusions`. Carries an optional `Name` too (default null (null or empty means unnamed), unique when non-empty),
  added to the closed exclusions item schema as `"name": {"type": ["string", "null"]}` since exclusions,
  unlike features, are a closed structure.
- **`scatterOverrides`** (`MapScatterOverrideDoc`) - shapes that tweak scatter density and/or kind mix
  inside a region, first matching override (document order) wins. Builds into `ScatterConfig.Overrides`.
  Carries an optional `Name` too (default null, null or empty means unnamed, unique when non-empty among
  scatter overrides), added to the closed scatter-override item schema as `"name": {"type": ["string",
  "null"]}` since scatter overrides, like exclusions, are a closed structure. This is a format
  forward-compat break: a document with a named scatter override fails schema validation on engines built
  before this version, since the item schema is closed (`additionalProperties: false`). The exclusions
  `Name` field above set the precedent for accepting that break when adding a name to a closed item.
- **`placements`** - authored props/buildings: stable id, kind, position (Y optional, ground-snapped if
  absent), yaw, scale, tags.
- **`spawns`** - NPC spawn markers (archetype id, position, enabled flag, tags), interpreted by the game.
- **`playerSpawns`** - player start markers (stable id, position, yaw, enabled flag, tags). No archetype:
  which start a game uses at runtime is game code's concern. Games read `doc.PlayerSpawns` directly, the
  same way they read `spawns` (no `MapRuntime` builder).
- **`regions`** - named, tagged shapes for quest areas, safe zones, triggers, interpreted by the game.
  `MapRuntime.BuildRegions` resolves them into a point-testable `MapRegionSet` (see "Load and build").
- **`terrainOverrides`** (`MapTerrainOverrides`, format v2) - the terrain sculpt/delta layer: a sparse map
  of 32x32 delta tiles (`MapSculptTile`) at a document-chosen sculpt cell size (the block header, default
  0.5 m), only touched tiles stored. Deltas are float meters added to the analytic height, bilinearly
  sampled between cell centers at runtime. `MapRuntime.BuildField` folds them into the `TerrainField`, so
  every terrain consumer inherits authored terrain with no signature change. Absent or null means no
  sculpting (byte-identical to the analytic field). The type has a code-level authoring API (`SetDelta` /
  `AddDelta` / `GetDelta` at a global cell, `TileCount`, `IsEmpty`, `TryGetTile`, ordered `Tiles`), and
  the validating writer refuses a tile whose extent leaves the document bounds. See the sculpt section
  below.

## Schema

The package embeds `mapdoc.schema.json` (JSON Schema draft 2020-12). `MapDocumentSchema.GetJson()` reads
it, and `MapDocumentSchema.WriteTo(path)` materializes it into a game's data directory so a document's
`$schema` reference resolves for editor/AI tooling and for `KhaozEngine.Content`'s build-time schema
validator.

The tiled form needs two more, and they are DERIVED from the authored one at runtime rather than
hand-maintained: `GetManifestJson()` is the document schema without the four bucketed content lists plus
`schemeVersion`, `sculptCellSize` and the tile index, and `GetTileJson()` references the same `$defs` item
shapes the document schema owns. `WriteAllTo(directory)` materializes all three under
`MapDocumentSchema.DocumentFileName` / `ManifestFileName` / `TileFileName`. Three schemas describing
overlapping content is exactly the shape that rots, so there is only one place to edit.

Every closed structure (the document root, `bounds`, `terrain`, scatter layers, companion layers,
placements, spawns, player spawns, regions, exclusions, overrides, and each concrete shape) sets
`additionalProperties: false`, so an unknown field anywhere on them fails validation. The one exception
is deliberate: a `terrain.features` item only requires its `type` discriminator, because the feature
union is registry-open (`MapDocRegistry.RegisterFeature`), and locking its fields to the built-in set
would defeat a game's own feature type.

## Example document

A small, complete `valley.map.json`:

```json
{
  "$schema": "./mapdoc.schema.json",
  "formatVersion": 3,
  "id": "valley",
  "displayName": "The Valley",
  "tileSize": 512,
  "bounds": { "minX": -120, "minZ": -120, "maxX": 120, "maxZ": 120 },
  "terrain": {
    "seed": 7345,
    "waterLevel": -0.5,
    "biomes": [
      { "biome": "Meadow", "baseHeight": 0, "hillAmplitude": 1.5 }
    ],
    "features": [
      { "type": "lake", "centerX": 34, "centerZ": -14, "radius": 22, "depth": 6 }
    ]
  },
  "scatterLayers": [
    {
      "name": "trees",
      "seed": 1337,
      "cellSize": 4.5,
      "rules": [
        { "biome": "Meadow", "density": 0.55, "kinds": [ { "id": "pine_a", "weight": 1 } ] }
      ]
    }
  ],
  "exclusions": [
    { "shape": { "type": "disc", "centerX": 0, "centerZ": 0, "radius": 26 } }
  ],
  "scatterOverrides": [
    {
      "shape": { "type": "rect", "minX": -10, "minZ": -10, "maxX": 10, "maxZ": 10 },
      "densityMultiplier": 0,
      "layers": ["trees"]
    }
  ],
  "placements": [
    { "id": "inn", "kind": "building_inn", "x": -30, "z": 20, "yaw": 1.2 }
  ],
  "spawns": [
    { "id": "wolf-1", "archetypeId": "wolf", "x": 20, "z": 20 }
  ],
  "regions": [
    { "name": "town", "shape": { "type": "disc", "centerX": -30, "centerZ": 20, "radius": 34 }, "tags": ["safe"] }
  ]
}
```

The scatter override above zeroes tree density in a 20x20 clearing around the origin (a plaza), on top of
the disc exclusion around `(0, 0)` that keeps the same area entirely free of trees regardless of density.

## Load and build

```csharp
var doc = MapDocumentFile.Load("assets/maps/valley.map.json");
var registry = MapDocRegistry.CreateDefault();
var field = MapRuntime.BuildField(doc, registry);
var trees = MapRuntime.BuildScatterConfig(doc, "trees");
var placements = MapRuntime.BuildPlacements(doc, field);
// Both heads run exactly this, so client and server agree by construction.
```

`MapRuntime` also has `BuildTerrainConfig` (the raw `TerrainConfig`, if you want to build the field
yourself), `BuildScatterConfigs` (every scatter layer, keyed by name), `BuildCompanionConfig`, and
`BuildRegions` (the authored regions, below).
Placement Y is ground-snapped against the built field whenever the document leaves it null, so every head
that loads the same document agrees on where an unpositioned placement sits. An exclusion or override
applies to a scatter layer when its `layers` list is null (every layer) or names that layer, and
`ScatterConfig.ClearingRadius` is always zeroed for document-built layers, since documents author
clearings as exclusion shapes instead of the legacy single disc.

**Authored regions at runtime.** `MapRuntime.BuildRegions(doc)` resolves the `regions` section into a
`MapRegionSet`, so a game can ask which named area a position is in without writing its own shape tests:

```csharp
MapRegionSet regions = MapRuntime.BuildRegions(doc);
string? area = regions.RegionAt(player.X, player.Z)?.Name;   // null outside every authored region
```

Shapes are converted to their `IArea2D` once at build time, `Regions` keeps document order, and an entry
with no shape is skipped the same way the scatter builder skips one. Where regions overlap, `RegionAt`
returns the containing region whose shape center is nearest the point, and a shape with no derivable center
(`MapShapeGeometry.TryCenter` returning false) scores distance zero. The optional
`RegionAt(x, z, filter)` predicate skips regions it rejects. The editor's overlay picking runs on this same
resolver, so an authored region resolves identically at edit time and at run time.

## Custom terrain features

A game registers its own feature type on a `MapDocRegistry` instead of changing the engine:

```csharp
var registry = MapDocRegistry.CreateDefault();
registry.RegisterFeature("crater", typeof(CraterFeatureDoc), f => ((CraterFeatureDoc)f).Build());
var doc = MapDocumentFile.Load(path, new MapDocumentLoadOptions { Registry = registry });
```

`CraterFeatureDoc` derives from `MapFeature`, returns `"crater"` from `Type` (must match the registration),
and exposes a `Build()` that constructs the game's `ITerrainFeature`. `MapDocumentValidator` rejects any
feature `type` the registry does not know, so a document referencing an unregistered feature fails to load
instead of silently dropping it.

`MapDocRegistry.FeatureTypes` enumerates the registered discriminators in registration order (the default
registry yields `lake`, `flatten`, `ridge`, `rim`), so a tool can list the feature types it can place.

## Terrain sculpt layer (`terrainOverrides`)

`MapTerrainOverrides` is the authored sculpt/delta layer (format v2): a sparse map of
`TerrainSculpt.TileSize` (32) square delta tiles at a chosen sculpt cell size, folded into the analytic
terrain by `MapRuntime.BuildField`. It is the direct code authoring surface; the editor's sculpt brushes
(`KhaozEngine.MapEditor`) and the `ke-mapedit` `sculpt_apply`/`sculpt_flatten_region`/`sculpt_clear` MCP
verbs write through it too, both riding `PutTile`/`RemoveTile`. Author cells by global cell coordinate,
save/load round-trips deterministic tile order:

```csharp
var doc = MapDocumentFile.Load("assets/maps/valley.map.json");
doc.TerrainOverrides ??= new MapTerrainOverrides(cellSize: 0.5f);   // header cell size, default 0.5 m
doc.TerrainOverrides.SetDelta(cellX: 12, cellZ: -4, delta: 2.5f);   // raise cell (12,-4) by 2.5 m
doc.TerrainOverrides.AddDelta(cellX: 13, cellZ: -4, delta: -1f);    // add a lower delta next to it
float d = doc.TerrainOverrides.GetDelta(12, -4);                    // 2.5, or 0 where no tile covers
MapDocumentFile.Save(doc, "assets/maps/valley.map.json");           // refuses a tile that leaves bounds

var field = MapRuntime.BuildField(doc, MapDocRegistry.CreateDefault());
float h = field.SampleHeight(6f, -2f);   // analytic height + the bilinear sculpt delta
```

A global cell `(cellX, cellZ)` has its center at world `(cellX * cellSize, cellZ * cellSize)`; deltas are
meters, bilinearly interpolated between cell centers so sculpts stay smooth at any query resolution. Only
touched tiles are stored, and an absent or empty block leaves terrain byte-identical to the analytic field
(the field keeps its pure-analytic fast path). `TileCount`, `IsEmpty`, `TryGetTile`, and the ordered
`Tiles` snapshot round out the read side. The composition itself lives in `KhaozEngine.Terrain`
(`TerrainSculpt`); see `docs/design/TERRAIN-SCULPT-LAYER-DESIGN.md`.

## Format versioning

`MapDocumentFile.CurrentFormatVersion` is the version this engine build reads and writes (currently 3,
which added the root `tileSize`). Loading a document with an older `formatVersion` runs migrations
(`MapDocumentLoadOptions.RegisterMigration`, each a pure `JsonObject -> JsonObject` step from N to N+1)
until it reaches the current version. The engine's own steps are pre-registered by the
`MapDocumentLoadOptions` constructor: v1 -> v2 loads a v1 document (which had no sculpt layer) with an
empty layer and byte-identical terrain, and v2 -> v3 stamps `MapDocumentFile.DefaultTileSize` (512 m).
Any default is as arbitrary as any other for a document that had no tile concept, so the rule is
"deterministic and documented" rather than "derived". A document newer than the engine, or an old one with
no migration path, fails to load. Saving always writes the current version.

**Version and layout are independent axes.** v3 means "the model that can be tiled", not "tiled": a v3
monolithic file is legal and is what `Save` writes. The version bumped because per-tile hashing needs a
`tileSize` even for a monolithic document, or a monolithic and a tiled copy of the same world would hash
differently.

A game can register additional steps for its own synthetic older versions:

```csharp
var options = new MapDocumentLoadOptions();   // the built-in chain is already registered
options.RegisterMigration(0, root =>
{
    root["displayName"] = root["name"]?.GetValue<string>();   // v0 called it "name"
    root.Remove("name");
    return root;
});
var doc = MapDocumentFile.Load(path, options);
```

## On-disk forms: one file or one directory

A document is **either a single file or a directory**, and both are first class and supported forever.
The tiled form is what a world too big to serialize as one string uses:

```
island.map/                        a directory, not a file
  map.json                         the root manifest, and the ONLY file a save ever mutates
  tiles/
    s_0_0/                         shard dir, shard = tile >> 4, a filesystem nicety and never a load unit
      t_0_0.<64 hex>.json          content-addressed: the suffix IS that tile's canonical hash
      t_3_-2.<64 hex>.json
```

The manifest carries the globals plus the occupied-tile index (`[{ "x", "z", "hash" }]`, ascending Z then
X) and the `tileSize` / `sculptCellSize` grid headers. Each tile file carries an optional `$schema`
annotation plus exactly four lists: `placements`, `spawns`, `playerSpawns`, `sculpt`. Those four are the
only content that scales with authoring. Exclusions, scatter overrides and regions stay global, decided
rather than deferred: `MapScatterOverrideDoc` is first-match-wins in document order, which is a global
ordering that bucketing would either break or have to reassemble.

The object model does not fork. A fully loaded tiled document is indistinguishable from a monolithic one
except that `MapDocument.Tiles` is non-null. Two serializations, one model.

**Which form a path holds comes from the path, and no entry point ever inspects an extension.**
`Path.GetExtension("island.map")` is `".map"`, not empty, so an extension heuristic sends a directory to a
file write.

```csharp
MapDocumentForm form = MapDocumentFile.DetectForm(path);   // Tiled | Monolithic | None
MapDocument doc = MapDocumentFile.Load(path);              // dispatches on the form
MapDocument window = MapDocumentFile.LoadTiled(dir, new MapTileRect(min, max));

MapDocumentFile.SaveAuto(doc, path);                       // back in the form it opened, None throws
MapDocumentFile.SaveAs(doc, path, MapDocumentForm.Tiled);  // the named form, whatever is there
IReadOnlyList<string> report = MapDocumentFile.VerifyTiled(dir);   // empty means clean
```

### The tile grid and region queries

`MapTileCoord` is a square of world XZ with edge `MapDocument.TileSize`, a DISTINCT type from `ChunkCoord`
so a 60 m chunk coord cannot be passed where a 512 m document tile coord is meant. `MapTileGrid.CoordOf`
delegates to `ChunkGrid.CoordOf`, so the floor rule has one implementation, and `AreaOf` is half-open on
both axes: a point exactly on a tile's max edge belongs to the next tile, which is what makes a partition
of rects reproduce the whole document exactly. A sculpt tile is owned by the document tile containing its
**origin corner** (`MapTileGrid.OwnerOfSculptTile`), single-owner for every sculpt cell size, including
the ones where the tile size is not an integer multiple of the sculpt span.

`MapSpatialIndex.Build(doc)` buckets a loaded document's point content by tile once, O(n) to build and
O(k) per query, and works on both forms, so a whole-document workflow keeps region queries.
`MapRuntime.BuildPlacements` grows a rect overload and two index overloads beside the untouched
whole-document one.

### Saving without materializing the document

`SaveTo(doc, stream)` serializes straight through a `Utf8JsonWriter`, and `Save` is reimplemented over it,
so peak managed memory is the writer's buffer rather than the document's serialized size. `SaveTiled`
writes one tile at a time and **does not rewrite a tile whose canonical hash is unchanged**, which is what
makes a windowed save over a huge world touch only what the author actually edited.

The write ordering is crash-consistent: changed tiles go to content-addressed names nothing points at yet,
a single `map.json` rename commits, and a best-effort sweep afterwards collects whatever the new manifest
does not name. Crash at any instant and the directory loads as entirely the old version or entirely the
new one, never a mixture. That is why the file name encodes the content hash: with a fixed name per
coordinate the manifest could not tell old content from new.

`SaveTiled` holds exclusive write access to the directory until its sweep and in-memory index refresh finish.
An overlapping save from another thread or process fails with `MapDocumentException` before changing tiles or
the manifest. Retry after the first save finishes. The empty `.mapdoc-save.lock` file remains in the directory
between saves, but the operating system releases its ownership when the writer closes or exits. Do not remove
that file while a writer is active. Writers targeting different directories remain independent.

`MapDocumentSaveOptions.Durability` defaults to `MapSaveDurability.Fast`, which defends against a process
kill, an unhandled exception or an editor crash, which is what actually happens on a dev box.
`PowerFail` opts into per-file flushes plus a directory fsync on the platforms that have one (Linux and
macOS do, Windows has no equivalent primitive and orders metadata through the NTFS journal instead).

### Windowed loading and the partial-save guard

`LoadTiled(directory, window)` loads the manifest plus the tiles in a `MapTileRect`. Unloaded tiles keep
their index entries, so the document knows they exist and a later `SaveTiled` back to the SAME directory
carries them through untouched.

**Every save entry point refuses a partial document** (`Save`, `SaveText`, `SaveTo`, `SaveAuto`,
`SaveAs`), because the data-loss path is a windowed document reaching a whole-document writer: that write
silently drops every unloaded tile and looks like a successful save. The guard is stated on the DOCUMENT
(`MapTileIndex.IsPartial`), not on one writer, so a save path added later inherits it. `SaveTiled` also
throws when a bucketed item lands in a tile the index marks occupied but not loaded, naming the item and
the target tile, rather than replacing that tile's real content with just the moved item.

### World identity (`MapDocumentHash`)

```csharp
string world = MapDocumentHash.OfWorld(doc);        // SHA-256, lower hex, both forms agree
string tile  = MapDocumentHash.OfTile(index, coord);
```

Canonical bytes are a compact serialization distinct from the indented bytes on disk, so indentation and
hand reindenting never affect identity. The four bucketed lists are sorted (placements, spawns and player
spawns by ordinal id, sculpt tiles ascending) before hashing, so their half depends only on content: two
authoring sessions that added the same placements in a different order produce the same world hash. The
global shape lists are hashed in DOCUMENT ORDER, `scatterOverrides` necessarily so, since reordering it
changes the world.

`displayName` and `$schema` are excluded, because the hash answers "is the ground under this player the
same ground" and renaming a zone must not desync a live server from its clients. `tileSize` IS included,
so re-tiling a world changes its identity, and a conversion between the two forms must PRESERVE `tileSize`
rather than re-derive it. A null sculpt block hashes identically to an empty block at
`MapTerrainOverrides.DefaultCellSize`, and the monolithic writer collapses that empty block back to no
block, so a round trip through the tiled form never invents a `terrainOverrides` key.

Every integer in hash input and in every generated file name goes through `CultureInfo.InvariantCulture`.
Under ICU, `sv-SE` and `fi-FI` format a negative integer with U+2212 MINUS SIGN rather than U+002D, so a
world with any negative tile coordinate would otherwise hash differently and write differently named files
on a Swedish workstation. No code path ever parses an integer back OUT of a file name: the manifest is the
sole authority on which tiles exist and what each is called.

`MapDocumentHash.SchemeVersion` is folded into every composed digest and is recorded in the manifest.
Loading a whole document at a mismatched scheme is fine and the next save upgrades it. A WINDOWED load
refuses, because a partial save carries stored hashes through verbatim and cannot upgrade what it cannot
read.

### On-demand tile reads (`MapDocumentSource`)

```csharp
using var source = MapDocumentSource.OpenTiled("island.map");   // reads map.json and nothing else
MapTileContent tile = source.ReadTile(new MapTileCoord(3, -2)); // parses and validates one tile
```

`ReadTile` is free of shared mutable state, so a caller may run it on a worker thread.
`MapDocumentSource.FromDocument(doc)` wraps an in-memory whole document behind the same API.
`MapTileContent` is immutable once handed out, INCLUDING the delta arrays inside `SculptTiles`, because a
reader hands out the arrays it parsed and `TerrainSculpt` stores them by reference: clone before editing.
For `OpenTiled`, the occupied-tile index is a snapshot taken at open time, since a re-saved tile is
content-addressed and gets a new filename on every edit. Call `source.Refresh()` after an external save (an
editor, a generation tool) re-reads `map.json` and atomically swaps in the fresh index, which
`MapTileResidency.Invalidate` already does before it re-reads a tile. `Refresh` is a no-op for a
`FromDocument` source, since there is no `map.json` to re-read. Rebuild it over the updated document instead.

A tile read runs a per-tile validation subset, keeping the loud-fail stance for a read that cannot see the
whole document: ids non-empty and unique within the tile, placement kinds non-empty, delta counts exact,
and every item actually falling inside the tile it was read from. That last one is the check a
whole-document load can never make and a tiled load must, since it is what catches a hand-edited or
tool-generated file whose content does not match its name. Bounds and cross-tile checks stay with
`MapDocumentValidator` and `VerifyTiled`.

### Document residency (`MapTileResidency`)

Keeps a square ring of document tiles resident around one or more foci, reading each on demand through a
`MapDocumentSource` and handing arrivals and departures to an `IMapTileSink`. GPU-free and driven from a
position, so a client and a headless server run the same type.

```csharp
var config = MapResidencyConfig.Default;                       // LoadRadius 2, UnloadRadius 3, 2 per update
IReadOnlyList<string> errors = config.ValidateAgainst(streamerConfig, doc.TileSize, sculptCellSize);
if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));

using var residency = new MapTileResidency(source, config, mySink, dispatcher: null, field);
streamer.BuildGate = residency.GateFor(streamerConfig.ChunkSize, sculptCellSize);

// Every frame, in this order.
residency.Update(playerPos);
streamer.Update(playerPos, dt);

// Every teleport, zone change or camera jump, before the next streamer.Update.
residency.PrimeAround(newFocus);
streamer.UnloadAll();
```

Radii are in TILE units at CHEBYSHEV distance (a square ring), which is deliberately NOT `StreamerConfig`'s
Euclidean metric. The focus can sit anywhere in its own tile, including hard against a corner, and a square
ring guarantees exactly `LoadRadius * tileSize` of loaded world in every direction for every radius, where a
Euclidean ring guarantees 0 at radius 1 and an awkward 2.83 tiles at radius 4. `MapResidencyConfig` is a
distinct type from `StreamerConfig` so the two cannot be confused.

`ValidateAgainst` is the wiring-time check that a chunk can never build or REBUILD against a non-resident
tile: the data rule measures to the streamer's UNLOAD radius (chunks persist that far and `Invalidate`
rebuilds any of them), the collider rule keeps every gameplay chunk over Gameplay tiles, and both subtract
one sculpt span because a tile's low-X and low-Z edges are covered by sculpt owned by its neighbour. Run it
against the WIDEST render-distance profile, not the active one: the profile is a runtime setting, and a
config that only validates on Low is a hole in the world on Ultra.

`Update(ReadOnlySpan<Vector3>)` is the server form: the resident set is the union of the per-focus rings,
recomputed each update with nothing reference counted, and a tile contested between rings takes the
strongest any focus assigns it, so the answer does not depend on focus order. Cost is O(foci * ring area).
Past a few hundred foci a shard server should drive one residency per `CellSim` rather than one global
residency with a thousand foci. That guidance has two known holes (authored content in a region no player
ring covers, and a cell crossing arriving cold), tracked as
[#341](https://github.com/APKiwiOrg/KhaozEngine/issues/341) and not solved here.

The two seams a consumer wires:

- **`GateFor(chunkSize, sculptCellSize)`** returns an `IChunkBuildGate` for `TerrainStreamer.BuildGate`. A
  chunk builds only when no document tile touching its (sculpt-expanded) footprint is occupied-but-not-
  resident. An ABSENT tile is buildable, never blocking, because absence is the common case in a sparse
  world and gating on it would deadlock the streamer over empty terrain.
- **`MapTileResidency` implements `IPlacementSource`**, so `PropLayer.PlacementLayer(residency, meshes,
  drawRadius)` streams authored placements to the renderer with no glue. Pass a `TerrainField` to the
  constructor when any authored placement omits Y, since that is what ground-snaps it.

The sink is a notification seam and nothing more. `TileLoaded` / `TileRingChanged` / `TileUnloaded` fire on
the calling thread inside `Update` before it returns, so a consumer adds and frees per-tile physics bodies
with no lock, and the engine never registers, owns, or frees one. The file read and parse behind an arrival
ran on a worker thread. The handoff did not.

## Loud failures

Map documents are dev-authored content, not runtime state: `MapDocumentFile.Load`/`Save` throw
`MapDocumentException` on a read error, invalid JSON, a missing or out-of-range `formatVersion`, a
deserialization error, or any semantic validation failure (`MapDocumentValidator`, for example a duplicate
id, an unknown scatter layer reference, or a `terrainOverrides` tile that leaves the document bounds). A
game boots against a bad document and fails loudly with a precise error rather than quarantining it and
limping on, the opposite of the quarantine handling runtime cell blobs get. The tiled reads are the same:
a directory with no `map.json` has no form and says so, and a tile that fails the per-tile subset names the
directory, the tile coordinate and the file.

Depends on `KhaozEngine.Primitives`, `KhaozEngine.Serialization`, `KhaozEngine.Content`, and
`KhaozEngine.Terrain`. GPU-free. In the `Foundation` umbrella. The GUI editor (`KhaozEngine.MapEditor`)
and the `ke-mapedit` MCP tool are later frontends over this model, see
[`docs/design/MAP-EDITOR-DESIGN.md`](../docs/design/MAP-EDITOR-DESIGN.md).

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
