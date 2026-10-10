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

Native opted-in documents (non-null `ResolverIdentity`) are validated completely at every lifecycle boundary by
`NativeDocumentService.ValidateComplete(document, source, options)`. It checks the document locally, reloads and
digest-verifies the whole asset closure, then resolves placements under the document's resolver identity.
Resolver identity `(1, 1)` keeps analytic support from `MapRuntime.BuildField` and the unchanged
`NativeDocumentService.SessionOptions` build identity (`khaozengine.mapedit.analytic-support`, builder
version 1, options hash `mapruntime-buildfield-default-registry-v1`). The resolver identity `(1, 2)`, new in 20.30.0,
uses `MapResolverV2` over `MapDocumentSurfaceSource.Capture(document)` with `SessionOptionsV2`
(`khaozengine.mapedit.authored-support`, builder version 1, options hash
`mapdoc-resolver-v2-authored-bindings-v1`, resolver version 2). Unknown identities and mismatched resolver
options refuse before resource reads. Authored bindings never fall back to analytic support.
The resource root is the monolithic file's directory or the tiled document's own directory, anchored as an
absolute path at open, so later working directory changes never move it.

- Open and SetWindow verify the candidate before it replaces anything, then bind its fresh closure, so editing
  works without a manual bind. A windowed (partial) native load refuses. Failed loads keep the previous
  document, binding, path and dirty state.
- Save, ConvertToSingle, ConvertToTiled and Retile verify before any write. Conversions verify against the
  destination's resource root and refuse missing or stale resources rather than copying them or rebasing
  references. A monolithic native write is staged in a sibling file and promoted with one rename. That rename
  replaces the destination entry, so it does not write through a symbolic link and does not keep the previous
  file's mode or ACL metadata. Tiled writes keep the tiled writer and its guards.
- Every check names the storage form explicitly and reads resources through `MapStorageGuardedAssetSource`.
  Any closure reference (roots, transitive and unused declarations included) inside the namespace that form's
  writer owns refuses before it is read: for tiled storage the `tiles` subtree, `map.json`, its temp file and
  the save lock, for monolithic storage the document file. Conversion to tiled checks the target namespace even
  for a monolithic source.
- `convert_to_tiled` target policy differs by document kind. An analytic document refuses any existing
  directory. A native document may target an existing directory prepared with its relative resources, and
  refuses only once that directory holds a map manifest (`MapDocumentStorage.HoldsTiledDocument`). The prepared
  resources must sit outside the target's reserved namespace, and the tiled writer's own guards still apply.
- Native writes use the form the document was opened or converted in, never one inferred from the path later.
  Save and Retile refuse when tiled storage has vanished or been replaced. Retile also refuses a missing
  monolithic file, while Save recreates it.
- Validate reports a stale or incomplete closure as `ClosureValid` false with `ClosureErrors`, and a false
  `Valid`, instead of throwing. Summary throws for a stale closure, and otherwise reports
  `MapSummary.Native` (`NativeDocumentSummary`) with the authored and closure hashes and the numeric-ID
  high-water mark as an exact decimal string. Analytic documents report `Native` null and `ClosureChecked`
  false.

`MapEditSession.BindNativeAssets(closure)` remains for an explicit rebind. It is validated without dirtying
the session. Native mutation callbacks edit a detached candidate and publish only after full bound validation,
preserving the public document instance. Native placement commands additionally preserve their retry state on
rejection. `PlacementRename` changes the native display label and returns the unchanged placement ID. The
explicit service APIs `PlacementLabel` and `PlacementRemapId` distinguish label edits from stable-ID
remapping. These are service APIs, not additional MCP registrations.

The terrain path, new in 20.30.0, is also a service API. `MutationService.TerrainApply(edit)` runs the GUI's
`TerrainEditCommand(MapTerrainEdit)` under the session lock, through the same native transaction seam. It
refuses an analytic document and returns `MapTerrainMutationResult`, holding the `MutationResult` and the
accepted `MapNativeEditEffects`. A GUI edit and a service edit of the same document produce identical
document bytes and effects text, which the GUI reports through `EditorDocument.LastNativeEffects`. Terrain
MCP verbs are deferred to R10.

## Native colliders

`NativeCollisionService(session)` measures and edits the colliders of the open native document. It is a service
API, not an MCP registration.

```csharp
var collisions = new NativeCollisionService(session);
NativeCollisionMeasurement m = collisions.Measure("wall-1");
NativeCollisionEditResult preview = collisions.SetHeights("wall-variant", bottom: 0.1f, top: 2.4f);
NativeCollisionEditResult applied = collisions.SetHeights("wall-variant", 0.1f, 2.4f, dryRun: false);
session.Save();
```

`Measure(placementId)` returns a `NativeCollisionMeasurement` in asset-local metres, with source units applied and
the placement transform not applied. `RawMeshMaxY` is the highest mesh vertex as `NativeMapAssetLoader` loads it,
with no height normalization. `EffectiveBottom` and `EffectiveTop` are the collider's vertical extent under the
physics seam's conventions (boxes centred, cylinders base-aligned). `ColliderSha256` is the verified collider
digest.

`SetHeights(assetId, bottom, top, dryRun = true)` resizes a box collider or a compound of upright boxes to span
`bottom` to `top` in asset-local metres. Every box keeps its X and Z, and its centre and half height map linearly
from the collider's current vertical extent onto the requested one. A lone box becomes a one-child compound. Any
other collider refuses with "compound boxes". The `NativeCollisionEditResult` carries the collider digests before
and after, every placement of the asset in ordinal order and `MapNativeEditEffects` with the placements' old and new
world collider bounds and `Physics`, `Nav` and `Residency` invalidation.

A dry run, the default, computes the new collider bytes, digest and effects over an in-memory overlay and touches no
file and no session state. Apply writes the new collider and a new root manifest through `MapAssetFileWriter`,
reloads and verifies the closure from disk, then swaps the root reference in one native transaction whose
`MapNativeWriteSet.NativeAssets` flag publishes the roots. It binds the new closure and marks the session dirty, and
`Save` persists the document. The mesh resource and every placement transform stay unchanged. The asset and its
collider must be declared in one root manifest, and no other asset may share the collider resource. An edit that
leaves the collider bytes unchanged reports `Applied` false with no invalidation.

`MapAssetFileWriter(assetRoot)` writes resources under an explicit absolute asset root, the root the document's
resource references resolve against. `WriteResource(bytes, extension)` writes `assets/<sha256>.<extension>` and
returns a `MapAssetRef` with that relative path, the lowercase digest as its ID and payload version 1. Equal bytes
share one file, a file is staged and renamed into place, and an existing file is never overwritten: one holding other
bytes refuses. `WriteManifest(manifest, id)` serializes a `MapAssetManifestDoc` as payload 1 JSON, proves it reads
back under the strict manifest reader, writes it the same way and returns a reference with `id`, ready to stand as a
document root. Files a refused edit already wrote stay as unreferenced content-addressed resources.

## Tiled documents, whole-load vs windowed

`map_open` and `map_save` are form-aware, matching the GUI editor: `map_open` dispatches on
`MapDocumentFile.DetectForm` (a directory loads tiled, a file loads monolithic), and a tiled document at
or under `MapEditSession.WholeWorldTileLimit` occupied tiles (default 512) loads WHOLE. Above it,
`map_open` windows instead: the manifest plus the tiles inside a square around a nearby enabled player spawn,
radius `EditorWindowRadius` tiles (default 2, `MapDocumentWindowing.DefaultEditorWindowRadius`). The shared
`MapDocumentWindowing` policy searches at most 32 occupied tiles nearest the bounds center, with deterministic
coordinate ordering, and falls back to the bounds center if that search finds no enabled spawn.
`map_save` always writes back in the form and directory the document came from
(`MapDocumentFile.SaveAuto`), never converting implicitly. `window_status` reports the loaded window's
tile and world rect plus the occupied/loaded tile counts, `Tiled` false for a monolithic or in-memory
document. `set_window(minX, minZ, maxX, maxZ, discard?)` moves the window (a world rect, the same
convention `sculpt_flatten_region` takes): it refuses with unsaved changes unless `discard` is passed, and
on success discards whatever this session held before (there is no undo stack across calls to replay) and
reloads fresh from the manifest. Writing content that moved into a tile the window never loaded throws a
precise `MapDocumentException` naming the item and the target tile instead of silently dropping it, the
same guard `SaveTiled` states on the document itself, inherited automatically. `convert_to_tiled(directory)`
/ `convert_to_single(path)` change the on-disk FORM explicitly (`MapDocumentFile.SaveAs`, no extension
heuristics: `Path.GetExtension("island.map")` is `".map"`, not empty, so guessing from the path would
route a directory-shaped name to the wrong writer) and always preserve `tileSize` and the world hash
exactly. `convert_to_tiled` refuses an existing directory for an analytic document, and for a native document
only one that already holds a map manifest (see Session model). `retile(tileSize)` changes `tileSize` itself and re-saves: `tileSize` IS part of world identity
(`MapDocumentHash.OfWorld`), so this deliberately changes the world hash, and the result's `Warning` states
the before/after digests plainly so a client and server ship the change together.

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
`Render3DSnapshot.Capture` (the engine's public headless scene-capture helper). Every other verb (document,
query, mutation) runs on a machine with no display or graphics device. A render on a machine with no
headless GPU device fails with a precise `McpException` naming the selected backend, instead of hanging
or crashing the process.

Both renders take a `textured` bool (default `true`), mirroring the editor's `MapEditorOptions.TexturedProps`
toggle: `true` renders a manifest entry's textured multi-material parts when it declares `"textured": true`,
`false` renders every prop flattened regardless of the manifest flag. `RenderService.ConfigureWorld` threads
it into the throwaway `ViewportWorld` the render call builds (`ViewportWorld.TexturedPropsEnabled`), so a
render call gets the same textured-vs-flat choice the GUI viewport does without a live editor session
open. Additive parameter, no new verb.

Authored placements and scatter draw through the world's streamed ring, so each render streams where it looks
(`RenderStreamPlan`). `render_view` streams around its eye with the editor's default ring, so it shows what the
live editor shows from that eye: placements and scatter within about 240 m. `render_topdown` streams around the
rect centre and widens the gameplay ring, prop cull and companion cull until they reach every corner of the
requested rect, so a whole-document render includes the far corners. The ring is capped at 24 chunks (a 1440 m
reach, so a square rect up to about 1.9 km on a side is fully covered). Past the cap a wider rect still renders
its terrain, but placements and scatter appear only within about 1350 m of the rect centre, and the render's
text block says so, naming the covered distance. Render the rect in pieces for a larger map. Measured on an Apple
GPU with a scatter-free document (flat terrain and a single marker placement), a covered 1200 m square took 0.2
to 1.4 s (the higher figure includes first device start-up) and a capped 2 km square about 0.9 s. A document with
dense scatter layers pays for every scattered prop inside the widened ring as well, so expect longer renders.

## Verb surface (78 tools)

| Group | Verbs |
|---|---|
| Document | `map_open`, `map_create`, `map_save`, `map_validate`, `map_summary`, `set_window`, `window_status`, `convert_to_tiled`, `convert_to_single`, `retile` |
| Query | `ground_height`, `is_walkable`, `placements_in_rect`, `scatter_preview_in_rect`, `find_flat_area`, `procedural_info`, `exclusions_info`, `scatter_overrides_info`, `sculpt_stats` |
| Placements | `placement_add`, `placement_move`, `placement_rotate`, `placement_scale`, `placement_rename`, `placement_remove` |
| Spawns | `spawn_add`, `spawn_move`, `spawn_set_enabled`, `spawn_rename`, `spawn_remove` |
| Player spawns | `player_spawn_add`, `player_spawn_move`, `player_spawn_set_yaw`, `player_spawn_set_enabled`, `player_spawn_rename`, `player_spawn_remove` |
| Terrain | `terrain_edit`, `feature_add`, `feature_edit`, `feature_remove`, `feature_reorder`, `feature_rename`, `biome_band_add`, `biome_band_edit`, `biome_band_remove` |
| Scatter | `exclusion_add`, `exclusion_edit`, `exclusion_remove`, `exclusion_rename`, `exclusion_set_layers`, `scatter_override_add`, `scatter_override_edit`, `scatter_override_remove`, `scatter_override_rename`, `scatter_override_reorder`, `bake_region`, `freeze_zone`, `scatter_layer_add`, `scatter_layer_edit`, `scatter_layer_remove`, `scatter_layer_rename`, `scatter_rule_add`, `scatter_rule_edit`, `scatter_rule_remove`, `companion_layer_add`, `companion_layer_edit`, `companion_layer_remove`, `companion_layer_rename` |
| Regions | `region_add`, `region_edit_shape`, `region_rename`, `region_remove` |
| Sculpt | `sculpt_apply`, `sculpt_flatten_region`, `sculpt_clear` |
| Duplicate | `element_duplicate` |
| Renders | `render_topdown`, `render_view` |

`scatter_override_rename(index, name?)` renames the scatter override at `index`, a null or empty name
clearing it back to unnamed. `scatter_override_reorder(fromIndex, toIndex)` moves a scatter override
between list positions: unlike an exclusion reorder, order is genuinely significant here (a scatter's
override lookup resolves the first matching override in list order), so this verb always affects the
streamed world. `scatter_override_add`/`edit`/`remove` route through the same `EditorCommand` types the
GUI uses (`AddScatterOverrideCommand`, `EditScatterOverrideShapeCommand`/`EditScatterOverrideValuesCommand`,
`RemoveScatterOverrideCommand`), so an MCP-driven scatter-override edit is undoable in the same editor
session, matching how exclusion and feature verbs already worked.

`element_duplicate(kind, id?, index?)` duplicates one document element by kind: `placement`, `spawn`,
`player_spawn`, `region`, `scatter_layer`, `companion_layer` are addressed by `id`, while `feature`,
`exclusion`, `scatter_override`, `biome_band` are addressed by `index` (exactly one of the two per call). It reuses the exact same clone and
unique-identity helpers `KhaozEngine.MapEditor`'s own Ctrl+D duplicate uses (`EditorToolController`'s shape
clone, `MapEditorScene.CloneScatterLayer`/`CloneCompanionLayer`, `FeatureGeometry.Translated`), so a GUI-driven
and an MCP-driven duplicate can never drift apart: same `+2/+2` world-unit offset on the kinds that carry a
position, same `<name>-copy`/`-copy-2` uniquifying for a named feature, exclusion, or scatter override, same generated-name
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

`map_validate(verifyWholeWorld?)` runs the structural checks (`MapDocumentValidator`) first. It then checks
a whole document against the document schema, or checks each loaded tile of a windowed document against
`MapDocumentSchema.GetTileJson()`. Tile errors name their tile coordinate. The result reports `SchemaScope`
as `document`, `loadedTiles`, or `none`, so a partial check is never presented as whole-world coverage.
Pass `verifyWholeWorld: true` for a tiled document to run `MapDocumentFile.VerifyTiled` against every tile and
the native surface storage closure without loading cold content into the session. Surface checks include
byte and semantic integrity, references and incident bookkeeping, not geometry or navigation certification.
`WholeWorldChecked`, `WholeWorldValid`, and
`WholeWorldErrors` report that separate pass. `Valid` includes whole-world validity only when the option was
requested. `bake_region` freezes a scatter layer's procedural output over a world rect
into authored placements (`baked-<layer>-N`, an explicit ground Y, tagged `baked`) plus a covering
exclusion scoped to that layer, so a designer can hand-tune what was procedural. `freeze_zone` is the
whole-document terminal form: it bakes every scatter layer and companion layer across the document bounds
into authored placements (`baked-<source>-N`, explicit Y, tagged `baked` plus the source layer name), then
removes all scatter layers, companion layers, exclusions, and scatter overrides, leaving a placements-only
document with no procedural generation left. A single undoable operation, mirroring the GUI's Ctrl+Shift+F
chord. `FreezeZoneResult` reports `PlacementCount`, `ScatterLayersRemoved`, `CompanionLayersRemoved`,
`ExclusionsRemoved`, `ScatterOverridesRemoved`, and `Applied`. Called on a document with no scatter or
companion layers, it is a safe no-op: `Applied` comes back false, every count zero, and the document (and
its dirty flag) are untouched. `render_topdown` and
`render_view` return a PNG `ImageContentBlock` directly, no files written, preceded by a text block
naming the framing so the client can map pixels back to world coordinates. `procedural_info` reads back
the full terrain/biome-band/scatter-layer/companion-layer setup at full field fidelity, the read
counterpart to `terrain_edit` and the biome band and scatter/companion layer verbs below.
`exclusions_info` and `scatter_overrides_info` are the same read counterpart for the exclusion and
scatter-override lists: each returns every element in document order (order matters for scatter
overrides, whose lookup is first-match-wins) with its index, optional name, shape kind
(disc/rect/polygon), a compact one-line shape summary (disc: `center (x, z), radius r`, rect:
`min (x1, z1), max (x2, z2)`, polygon: `N points`), and its layer targeting, plus (for overrides)
the density multiplier and the `Kinds` substitution in the same `"id"`/`"id:weight"` convention
`procedural_info` uses, so a read value round-trips straight into `scatter_override_add`/`edit`.

`sculpt_apply(brush, x, z, radius, strength, dt, targetHeight?)` applies one terrain-sculpt brush dab
(`raise`/`lower`/`smooth`/`flatten`/`set_height`) at a world point as a single `TerrainSculptStrokeCommand`,
so it lands as one undo step, exactly the brush core the GUI's sculpt tool mode uses. `strength` and `dt`
feed the brush math directly (meters per stroke-second for raise/lower, a per-second blend rate for
smooth/flatten/set_height, scaled by `dt`), so a call is deterministic regardless of wall-clock time.
`targetHeight` is required for `set_height` and ignored otherwise; `flatten` instead captures its target
live, from the current ground height at `(x, z)`. `sculpt_flatten_region(minX, minZ, maxX, maxZ,
targetHeight)` flattens every sculpt cell whose centre falls inside the rect to `targetHeight` in one
command: an exact delta computation over the region (no falloff, no repeated dabs), not an approximation
built from many `sculpt_apply` calls. `sculpt_clear(minX?, minZ?, maxX?, maxZ?)` removes sculpt tiles,
restoring analytic terrain there, in one undo step; with every rect argument null it clears the whole
sculpt layer, and with all four supplied it clears only the tiles whose world extent intersects the rect
(the four must be supplied together or not at all). All three are clean no-ops when nothing changes (a
non-positive radius/dt, an already-flat region, a document with no sculpt layer, or a region touching no
stored tile): `Applied` comes back false and the document (and its dirty flag) are untouched. `sculpt_stats`
is the read counterpart: whether the document has a sculpt layer at all, its cell size, how many tiles are
stored, how many cells across those tiles carry a nonzero delta, and the min/max delta among those touched
cells. `ground_height`/`is_walkable` already reflect sculpted terrain (composited into the field by
`TerrainField.SampleHeight`), so use those for a point sample; `sculpt_stats` reads the raw layer's shape
instead. Design contract: [`docs/design/TERRAIN-SCULPT-LAYER-DESIGN.md`](../docs/design/TERRAIN-SCULPT-LAYER-DESIGN.md).

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
[`docs/design/MAP-EDITOR-DESIGN.md`](../docs/design/MAP-EDITOR-DESIGN.md). The `ke-mapedit` section of
[`docs/USING-KHAOZENGINE.md`](../docs/USING-KHAOZENGINE.md) has the same wiring example plus more on the
session model.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
