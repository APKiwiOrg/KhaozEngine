# KhaozEngine.MapEdit.Tool

The `ke-mapedit` dotnet tool: an MCP (Model Context Protocol) server that opens, queries, mutates,
validates, renders, and saves KhaozEngine map documents (`.map.json`) over stdio, so an AI client can
edit a zone the same way the in-engine GUI map editor does. Hosts the same `KhaozEngine.MapDoc` model
`KhaozEngine.MapEditor` uses, so a git diff of the `.map.json` is the human review loop for either
frontend. Author-time dev tool, not a runtime package.

## Install

```bash
dotnet tool install --global KhaozEngine.MapEdit.Tool
```

Installs the `ke-mapedit` command. This README ships inside the tool's nupkg (`PackageReadmeFile`).

## Wiring into an MCP client

Register it as an MCP server. Repo-local, for development against the tool's own source:

```bash
claude mcp add ke-mapedit -- dotnet run --project /path/to/KhaozEngine.MapEdit.Tool -c Debug
```

Against the packaged tool, once installed globally:

```bash
claude mcp add ke-mapedit -- ke-mapedit
```

Or run it ephemerally with `dnx`, no install required:

```bash
claude mcp add ke-mapedit -- dnx KhaozEngine.MapEdit.Tool
```

Equivalent `.mcp.json` entry (repo-local form):

```json
{
  "mcpServers": {
    "ke-mapedit": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/KhaozEngine.MapEdit.Tool", "-c", "Debug"]
    }
  }
}
```

Swap `command`/`args` for `"ke-mapedit"` with no args once it is installed as a global tool.

## Session model

One document open at a time (`MapEditSession`): the current `MapDocument`, its path, the manifest
paths it was opened with, a dirty flag, and a cached `TerrainField` invalidated by any world-affecting
mutation (terrain features, terrain globals, exclusions, bake). `map_open` (or `map_create`) replaces
whatever was open, with no dirty guard, since the git diff is the safety net and `map_summary` reports
the dirty flag. Every mutation validates the document before it lands (`MapDocumentValidator`, then a
JSON schema check on save) and reverts with the validation errors folded into the thrown message on
failure, so the in-session document is never left invalid.

## Manifest paths

`map_open` and `map_create` take optional asset manifest paths (props and buildings). They enable
kind-aware rendering: `render_topdown` and `render_view` resolve placement kinds against them the same
way the game does. Without manifests every document, query, and mutation verb still works, renders just
come back terrain-only, with no prop or building meshes.

## Features and shapes cross the wire as JSON

Terrain features (`featureJson`) and exclusion/region/override shapes (`shapeJson`) are registry-open
or polymorphic unions (a game can register its own feature type, and a shape is `disc`/`rect`/
`polygon`), so they cross the MCP boundary as raw JSON strings parsed with the open document's own
serializer options, instead of exploding into typed parameters. A lake feature:
`{"type": "lake", "centerX": 34, "centerZ": -14, "radius": 22, "depth": 6}`. A disc shape:
`{"type": "disc", "centerX": 0, "centerZ": 0, "radius": 26}`.

## Renders need a GPU

`render_topdown` and `render_view` are the only two verbs that touch a GPU, via
`Render3DSnapshot.Capture` (the engine's one public headless render entry). Every other verb (document,
query, mutation) runs on a machine with no display or graphics device. A render on a machine with no
headless GPU device fails with a precise `McpException` naming the selected backend, instead of hanging
or crashing the process.

Both renders take a `textured` bool (default `true`), mirroring the editor's `MapEditorOptions.TexturedProps`
toggle: `true` renders a manifest entry's textured multi-material parts when it declares `"textured": true`,
`false` renders every prop flattened regardless of the manifest flag. `RenderService.ConfigureWorld` threads
it into the throwaway `ViewportWorld` the render call builds (`ViewportWorld.TexturedPropsEnabled`), so a
render call gets the same textured-vs-flat choice the GUI viewport does without a live editor session
open. Additive parameter, no new verb - the verb count stays 64.

## Verb surface (64 tools)

| Group | Verbs |
|---|---|
| Document | `map_open`, `map_create`, `map_save`, `map_validate`, `map_summary` |
| Query | `ground_height`, `is_walkable`, `placements_in_rect`, `scatter_preview_in_rect`, `find_flat_area`, `procedural_info` |
| Placements | `placement_add`, `placement_move`, `placement_rotate`, `placement_scale`, `placement_rename`, `placement_remove` |
| Spawns | `spawn_add`, `spawn_move`, `spawn_set_enabled`, `spawn_rename`, `spawn_remove` |
| Player spawns | `player_spawn_add`, `player_spawn_move`, `player_spawn_set_yaw`, `player_spawn_set_enabled`, `player_spawn_rename`, `player_spawn_remove` |
| Terrain | `terrain_edit`, `feature_add`, `feature_edit`, `feature_remove`, `feature_reorder`, `feature_rename`, `biome_band_add`, `biome_band_edit`, `biome_band_remove` |
| Scatter | `exclusion_add`, `exclusion_edit`, `exclusion_remove`, `exclusion_rename`, `exclusion_set_layers`, `scatter_override_add`, `scatter_override_edit`, `scatter_override_remove`, `bake_region`, `scatter_layer_add`, `scatter_layer_edit`, `scatter_layer_remove`, `scatter_layer_rename`, `scatter_rule_add`, `scatter_rule_edit`, `scatter_rule_remove`, `companion_layer_add`, `companion_layer_edit`, `companion_layer_remove`, `companion_layer_rename` |
| Regions | `region_add`, `region_edit_shape`, `region_rename`, `region_remove` |
| Duplicate | `element_duplicate` |
| Renders | `render_topdown`, `render_view` |

`element_duplicate(kind, id?, index?)` duplicates one document element by kind: `placement`, `spawn`,
`player_spawn`, `region`, `scatter_layer`, `companion_layer` are addressed by `id`, while `feature`,
`exclusion`, `biome_band` are addressed by `index` (exactly one of the two per call). It reuses the exact same clone and
unique-identity helpers `KhaozEngine.MapEditor`'s own Ctrl+D duplicate uses (`EditorToolController`'s shape
clone, `MapEditorScene.CloneScatterLayer`/`CloneCompanionLayer`, `FeatureGeometry.Translated`), so a GUI-driven
and an MCP-driven duplicate can never drift apart: same `+2/+2` world-unit offset on the kinds that carry a
position, same `<name>-copy`/`-copy-2` uniquifying for a named feature or exclusion, same generated-name
scheme for a fresh placement/spawn/player-spawn/region id. Terrain has no duplicate verb, since it is a
document singleton. Every failure throws a precise error instead of silently no-opping: an unknown kind, a
missing or wrong-addressed ref (id where an index is required or vice versa), an id or index that does not
resolve, or a feature type the clone cannot offset. Camera bookmarks (the GUI's Shift+1..9/1..9 fly-camera
pose store/recall) have no MCP verb: they are interactive viewport state with nothing for a stateless,
one-shot render call to persist between requests.

A player spawn (`player_spawn_add(x, z, yaw?, enabled?, id?, tags?)`) is a stable-id, position-plus-yaw
start marker with no archetype: which spawn a game actually uses at runtime is game code's own concern, so
the tool only authors the marker. A null `id` auto-generates `player-N`. `player_spawn_set_yaw` is its own
verb (not folded into `player_spawn_move`) since yaw and XZ position are independently undoable edits on the
GUI side (`SetPlayerSpawnYawCommand`/`MovePlayerSpawnCommand`) and MCP parity requires the same granularity.
Player spawn ids are unique only within the `playerSpawns` section, so an NPC spawn and a player spawn may
share the same id string with no collision.

`map_validate` runs the structural checks (`MapDocumentValidator`) first, then a JSON schema check when
the structural checks pass. `bake_region` freezes a scatter layer's procedural output over a world rect
into authored placements (`baked-<layer>-N`, an explicit ground Y, tagged `baked`) plus a covering
exclusion scoped to that layer, so a designer can hand-tune what was procedural. `render_topdown` and
`render_view` return a PNG `ImageContentBlock` directly, no files written, preceded by a text block
naming the framing so the client can map pixels back to world coordinates. `procedural_info` reads back
the full terrain/biome-band/scatter-layer/companion-layer setup at full field fidelity, the read
counterpart to `terrain_edit` and the biome band and scatter/companion layer verbs below.

Biome bands and scatter/companion layers are closed-shape types (not open unions like features and
shapes), so they cross the wire as typed flat parameters instead of json: `biome_band_add`/
`biome_band_edit` take `start`/`end`/`biome`/`baseHeight`/`hillAmplitude` directly, and
`scatter_layer_add`/`scatter_layer_edit`/`companion_layer_add`/`companion_layer_edit` take their scalars
directly with `kinds`/`hostKinds` as string lists in the `"id"` (weight 1) / `"id:weight"` convention.
`scatter_layer_rename` cascades the rename through every companion layer's HostLayer and explicit
exclusion/scatter-override layer filter that names it, so nothing is left pointing at a stale name, and
its result detail reports how many references were cascaded when that count is greater than zero.
Scatter layer rules (per-biome density and kinds) are editable through the `scatter_rule_add`/
`scatter_rule_edit`/`scatter_rule_remove` triad, index-addressed against the named layer's Rules list
the same way the biome band and terrain feature verbs address their own lists. `procedural_info` reports
rules at full fidelity regardless of whether they were set through MCP or the GUI.

`map_summary` also reports `PlayerSpawnCount` and `PlayerSpawnIds` alongside the existing placement/spawn
counts and region names, and `placements_in_rect`'s result carries a `PlayerSpawns` entry list (id, x,
groundY, z, yaw, enabled, tags) alongside its `Placements` and `Spawns` lists, the same way NPC spawns
already ride along with placements for that query. `procedural_info`'s `CompanionLayerInfo` gains a computed
`HostKindsMatchHost` bool: true when `HostKinds` is empty (matches every host by the empty-means-all rule) or
when a populated `HostKinds` intersects the host layer's placeable kit ids, false only for the silent-no-op
mismatch case. `companion_layer_add` and `companion_layer_edit` detect that same mismatch on the layer they
just wrote and append ", host kinds match no kind in the host layer" to the result's `Detail` when it applies
(mirroring the GUI editor's read-only warning row wording), so an MCP client sees the same warning a human
operator would see in the inspector without a separate `procedural_info` round trip.

Every mutation returns what changed. An exception from a lower layer (`MapDocumentException`,
`InvalidOperationException`, `ArgumentException`) reaches the client with its original, precise message
rather than a generic one.

Full document format, the runtime builders, and the GUI editor sharing this same model:
[`KhaozEngine.MapDoc`](../KhaozEngine.MapDoc/README.md),
[`KhaozEngine.MapEditor`](../KhaozEngine.MapEditor/README.md), and
[`docs/MAP-EDITOR-DESIGN.md`](../docs/MAP-EDITOR-DESIGN.md). The `ke-mapedit` section of
[`docs/USING-KHAOZENGINE.md`](../docs/USING-KHAOZENGINE.md) has the same wiring example plus more on the
session model.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
