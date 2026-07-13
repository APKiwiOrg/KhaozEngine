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
- **`placements`** - authored props/buildings: stable id, kind, position (Y optional, ground-snapped if
  absent), yaw, scale, tags.
- **`spawns`** - NPC spawn markers (archetype id, position, enabled flag, tags), interpreted by the game.
- **`playerSpawns`** - player start markers (stable id, position, yaw, enabled flag, tags). No archetype:
  which start a game uses at runtime is game code's concern. Games read `doc.PlayerSpawns` directly, the
  same way they read `spawns` (no `MapRuntime` builder).
- **`regions`** - named, tagged shapes for quest areas, safe zones, triggers, interpreted by the game.
- **`terrainOverrides`** - reserved for a future sculpt/delta layer. Must be absent or null in format
  version 1, the validator rejects anything else, so sculpting lands later as a version bump, not a break.

## Schema

The package embeds `mapdoc.schema.json` (JSON Schema draft 2020-12). `MapDocumentSchema.GetJson()` reads
it, and `MapDocumentSchema.WriteTo(path)` materializes it into a game's data directory so a document's
`$schema` reference resolves for editor/AI tooling and for `KhaozEngine.Content`'s build-time schema
validator.

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
  "formatVersion": 1,
  "id": "valley",
  "displayName": "The Valley",
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
yourself), `BuildScatterConfigs` (every scatter layer, keyed by name), and `BuildCompanionConfig`.
Placement Y is ground-snapped against the built field whenever the document leaves it null, so every head
that loads the same document agrees on where an unpositioned placement sits. An exclusion or override
applies to a scatter layer when its `layers` list is null (every layer) or names that layer, and
`ScatterConfig.ClearingRadius` is always zeroed for document-built layers, since documents author
clearings as exclusion shapes instead of the legacy single disc.

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

## Format versioning

`MapDocumentFile.CurrentFormatVersion` is the version this engine build reads and writes. Loading a
document with an older `formatVersion` runs registered migrations
(`MapDocumentLoadOptions.RegisterMigration`, each a pure `JsonObject -> JsonObject` step from N to N+1)
until it reaches the current version. A document newer than the engine, or an old one with no registered
migration path, fails to load. Saving always writes the current version.

```csharp
var options = new MapDocumentLoadOptions();
options.RegisterMigration(0, root =>
{
    root["displayName"] = root["name"]?.GetValue<string>();   // v0 called it "name"
    root.Remove("name");
    return root;
});
var doc = MapDocumentFile.Load(path, options);
```

## Loud failures

Map documents are dev-authored content, not runtime state: `MapDocumentFile.Load`/`Save` throw
`MapDocumentException` on a read error, invalid JSON, a missing or out-of-range `formatVersion`, a
deserialization error, or any semantic validation failure (`MapDocumentValidator`, for example a duplicate
id, an unknown scatter layer reference, or `terrainOverrides` present). A game boots against a bad
document and fails loudly with a precise error rather than quarantining it and limping on, the opposite of
the quarantine handling runtime cell blobs get.

Depends on `KhaozEngine.Primitives`, `KhaozEngine.Serialization`, `KhaozEngine.Content`, and
`KhaozEngine.Terrain`. GPU-free. In the `Foundation` umbrella. The GUI editor (`KhaozEngine.MapEditor`)
and the `ke-mapedit` MCP tool are later frontends over this model, see
[`docs/MAP-EDITOR-DESIGN.md`](../docs/MAP-EDITOR-DESIGN.md).

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
