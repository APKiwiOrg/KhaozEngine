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

## Verb surface (39 tools)

| Group | Verbs |
|---|---|
| Document | `map_open`, `map_create`, `map_save`, `map_validate`, `map_summary` |
| Query | `ground_height`, `is_walkable`, `placements_in_rect`, `scatter_preview_in_rect`, `find_flat_area` |
| Placements | `placement_add`, `placement_move`, `placement_rotate`, `placement_scale`, `placement_rename`, `placement_remove` |
| Spawns | `spawn_add`, `spawn_move`, `spawn_set_enabled`, `spawn_rename`, `spawn_remove` |
| Terrain | `terrain_edit`, `feature_add`, `feature_edit`, `feature_remove`, `feature_reorder` |
| Scatter | `exclusion_add`, `exclusion_edit`, `exclusion_remove`, `scatter_override_add`, `scatter_override_edit`, `scatter_override_remove`, `bake_region` |
| Regions | `region_add`, `region_edit_shape`, `region_rename`, `region_remove` |
| Renders | `render_topdown`, `render_view` |

`map_validate` runs the structural checks (`MapDocumentValidator`) first, then a JSON schema check when
the structural checks pass. `bake_region` freezes a scatter layer's procedural output over a world rect
into authored placements (`baked-<layer>-N`, an explicit ground Y, tagged `baked`) plus a covering
exclusion scoped to that layer, so a designer can hand-tune what was procedural. `render_topdown` and
`render_view` return a PNG `ImageContentBlock` directly, no files written, preceded by a text block
naming the framing so the client can map pixels back to world coordinates.

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
