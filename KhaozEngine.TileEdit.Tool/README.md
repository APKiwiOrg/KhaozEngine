# KhaozEngine.TileEdit.Tool

The `ke-tileedit` dotnet tool: an MCP (Model Context Protocol) server that opens, queries, mutates, validates,
renders and saves KhaozEngine tile worlds over stdio, so an AI client can author a world before any GUI editor
exists. Every mutation runs through `KhaozEngine.TileWorld.Editing`, the same command layer the later GUI
editor uses, so an MCP edit and a GUI edit are the same undoable operation. Author-time dev tool, not a runtime
package.

## Install

```bash
dotnet tool install --global KhaozEngine.TileEdit.Tool
```

Installs the `ke-tileedit` command. This README ships inside the tool's nupkg (`PackageReadmeFile`).

## Wiring into an MCP client

Register it as an MCP server. Repo-local, for development against the tool's own source:

```bash
claude mcp add ke-tileedit -- dotnet run --project /path/to/KhaozEngine.TileEdit.Tool -c Debug
```

Against the packaged tool, once installed globally:

```bash
claude mcp add ke-tileedit -- ke-tileedit
```

Or run it ephemerally with `dnx`, no install required:

```bash
claude mcp add ke-tileedit -- dnx KhaozEngine.TileEdit.Tool
```

Equivalent `.mcp.json` entry (repo-local form):

```json
{
  "mcpServers": {
    "ke-tileedit": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/KhaozEngine.TileEdit.Tool", "-c", "Debug"]
    }
  }
}
```

Swap `command`/`args` for `"ke-tileedit"` with no args once it is installed as a global tool.

## Session model

One world open at a time (`TileEditSession`): the `TileEditingDocument` being edited, the directory it came
from, and the catalog paths it resolved. All members lock internally, so a query sees one consistent world even
while another call is mid-edit.

**The world is self-describing.** `world_open(path)` takes a directory and reads its `world.json`, which names
the catalogs, so opening is one argument. `world_create` stores the catalog paths in the manifest EXACTLY as
given, so a relative entry keeps the world portable. `world_open` replaces whatever was open with no dirty
guard: the client's git diff is the safety net, and `world_summary` reports the dirty flag.

**Catalog paths, and every other path, resolve against the WORLD directory.** An MCP server is started by a
client whose working directory is its own business, so a world that only loaded from one directory would be a
world that only loaded for one client. The two verbs that OPEN a world (`world_open`, `world_create`) name a
directory before there is a world to be relative to, so a relative path THERE resolves against the process
working directory: prefer an absolute path for both. Every other path-taking verb (the heightmap import, the
three prefab verbs, both renders' save paths) resolves a relative path against the open world's directory.

**One verb is one undo step.** The session seals the gesture after every command, so the drag coalescing the
command layer offers never fires over MCP, where each call is a discrete instruction rather than one sample of
a held mouse button. Two `object_move` calls that quietly became one undo step would leave a client unable to
step back through its own edits. `undo(steps)` and `redo(steps)` move up to that many steps and report how many
actually moved. The GUI editor of a later round drives `TileEditingDocument` directly and keeps the coalescing
for its drag tools.

**Collision is kept in step automatically.** Each command rebakes the derived collision map over its own dirty
rects, so `collision_at`, `is_walkable`, `path` and `walkable_rect` answer correctly on the call right after an
edit, with no explicit rebake verb and no full-world rebake.

**Errors keep their message.** A `TileWorldException`, an `ArgumentException` or an out-of-range plane reaches
the client with the lower layer's own precise text rather than a generic failure, through one guard every verb
goes through.

## Coordinate conventions

- **Tile space throughout the query and mutation surface**: x runs east, z runs north, and a plane is a storey
  index from 0.
- **A rect is four ints, `x, z, width, height`, with the far edges EXCLUSIVE**, so `(0, 0, 64, 64)` is tiles
  0..63 on both axes. `x, z` is the rect's south-west corner.
- **Heights live on tile CORNERS, not tiles**, so every rect in the height family is a rect of corners: the
  corners of tiles (0, 0) to (3, 3) are the rect `(0, 0, 5, 5)`, one wider and one deeper than the tiles they
  carry.
- **Rotation is quarter turns clockwise**, 0 west, 1 north, 2 east, 3 south, on objects, overlay shapes and
  prefab stamps. Outside 0..3 is refused rather than masked.
- **Regions are 64x64 tiles.** Region `(rx, rz)` covers tiles x `rx*64 .. rx*64+63` and z `rz*64 .. rz*64+63`.
- **`render_view` is the one exception: it speaks WORLD metres**, where y is up and world z is MINUS tile z
  (north is minus world z). To look at tile (10, 20) from the south-east, put the target at world
  (10.5, 0, -20.5) and the eye at (18, 8, -12). `render_topdown` takes a tile rect like everything else.

**Rows on the wire run NORTH FIRST.** Every ASCII map and every height row set has row 0 as the HIGHEST z of
the rect, each row running west to east, which is the way round a top-down render reads. `height_get_rect`
hands its rows straight to `height_set` without flipping the terrain.

## Verb surface (48 tools)

### World, catalogs, regions, history

| Verb | What it does |
|---|---|
| `world_open(path)` | Opens the world in a directory (its `world.json` plus the regions it names), replacing any open world. Returns the path, identity and a full summary. |
| `world_create(path, id, displayName, catalogPaths, planeCount = 4, tileSize = 1)` | Creates an empty world with one region at (0, 0), validates and saves it, and keeps it open. Refuses a directory that already holds a world. |
| `world_save()` | Validates, then writes the world back to its own directory and clears the dirty flag. An invalid world throws and nothing is written. Returns the directory and the world hash of what landed. |
| `world_summary()` | Id, display name, directory, plane count, tile size, region/object/marker counts, world hash, dirty flag, undo and redo depths with their labels, and the manifest's catalog paths. |
| `world_validate()` | Every issue as `[code] message` rather than a failed call. Returns a valid flag and the list. |
| `catalog_list(kind)` | The loaded catalogs' `materials` or `archetypes`, so a client knows the legal ids before it paints or places. |
| `region_create(rx, rz)` | Materialises an empty 64x64 region, void ground until something paints it. |
| `region_delete(rx, rz)` | Deletes a whole region, tile layers, objects and markers included. The undo puts all of it back. |
| `region_list()` | Every region the world holds, south row first and west to east, each with its tile rect and its object and marker counts. The map of where the world actually exists. |
| `undo(steps = 1)` | Steps back, stopping early when the stack runs out. Reports how many moved, the depths and labels, and the hash after the move. |
| `redo(steps = 1)` | The same forwards. |

### Tiles

| Verb | What it does |
|---|---|
| `tile_get(x, z, plane)` | One tile in full: both material ids with their catalog names, overlay shape and rotation, authored settings, DERIVED collision flags and blocked state, the four corner heights in centimetres, and which region holds it. |
| `tile_set(x, z, plane, underlay?, overlay?, shape?, rotation?, settings?)` | Paints one tile, exactly a 1x1 `tiles_fill`. |
| `tiles_fill(x, z, width, height, plane, underlay?, overlay?, shape?, rotation?, settings?)` | Paints every tile of a rect. A layer left null is not touched, so a fill can repaint the ground without disturbing what was built on it. Underlay 0, overlay 0, shape `Full`, rotation 0 and settings `none` CLEARS the rect back to void ground. |
| `tiles_get_rect(x, z, width, height, plane, layer)` | One layer of a rect as an ASCII map with the legend that decodes it. |

`shape` is a NAME (`Full`, `DiagonalHalf`, `CornerQuarter`, `CornerThreeQuarter`, case-insensitive) and
`settings` a comma list of NAMES (`None`, `Blocked`, `Indoors`, `Bridge`, `NoDraw`), with `none` or an empty
string clearing every flag. A number is refused in both, so a wrong enum value cannot land silently.

### Cosmetic foliage

| Verb | What it does |
|---|---|
| `foliage_layer_set(layer)` | Adds or replaces a complete validated layer. `density` is base64 row-major bytes and must contain `width * height` samples. |
| `foliage_get(id?)` | Reads one detached layer, or lists all layers in authoring order when id is omitted. |
| `foliage_density_set(id, width, height, rows)` | Replaces the complete density raster from numeric 0 to 255 rows. Dimensions must match the configured layer. |
| `foliage_paint(id, worldX, worldZ, radius, density, hardness)` | Paints a circular brush in world metres. Hardness 0 is fully soft and 1 is a hard circle. |
| `foliage_remove(id)` | Removes a layer. |

Every foliage mutation is one undo step and changes no gameplay object or collision. In the layer object and
`foliage_density_set`, X advances within each row. Row 0 starts at `originZ` and later rows advance along
positive world Z. This is a world-space raster convention, so it differs from the north-first tile maps and
height rows above. Tile north maps to negative world Z. `allowedUnderlays` applies to the visible material at
the sample, including the painted part of a shaped overlay. A door clearance uses objects tagged `door` on the
layer plane.

`tiles_get_rect` legends, one character per tile:

| Layer | Legend |
|---|---|
| `underlay` / `overlay` | one base36 digit of the material id modulo 36, `.` for id 0 (void, or no overlay). Ids above 35 wrap, `tile_get` names the exact id |
| `shape` | `.` full tile, `d` diagonal half, `q` corner quarter, `t` corner three quarter |
| `settings` | `.` none, `b` blocked, `i` indoors, `r` bridge, `x` nodraw, `B` blocked and nodraw, `+` another mix |
| `collision` | `#` blocked, `\|` a west or east wall, `-` a north or south wall, `+` both, `.` open, `v` no region |

The collision map's `v` matters: the collision map answers blocked for a region the world does not hold, and an
author reading a map needs to tell "there is a wall here" from "there is no world here".

### Heights (corner rects, rows north first)

| Verb | What it does |
|---|---|
| `height_set(x, z, width, height, plane, rows)` | Writes explicit corner heights in centimetres. `rows[0]` is the highest z, each row west to east and `width` long, `height` rows in total. |
| `height_raise(x, z, width, height, plane, deltaCm, falloff = 0)` | Raises (negative lowers) the corners, optionally fading the delta out toward the rect's edge ring so the patch blends in. Falloff 1 fades to nothing on the edge ring. |
| `height_flatten(x, z, width, height, plane, toCm?)` | Levels every corner to one height, or to the rect's own rounded average when none is given. This is how a building gets flat ground. |
| `height_smooth(x, z, width, height, plane, iterations = 1)` | An iterated box blur over the corners, blending into the terrain around them. 1 to 64 passes. |
| `height_get_rect(x, z, width, height, plane)` | Reads the corner heights, north first, in the exact shape `height_set` takes. |
| `height_import(pgmPath, x, z, width, height, plane, minCm, maxCm)` | Resamples a binary PGM or noninterlaced PNG heightmap onto the corner rect, black to `minCm` and white to `maxCm`. |

Every height verb also reports how many corners the rect COVERED against how many actually LANDED. They differ
where the rect reached space no region holds, which the lattice edge-extends into rather than refusing, so a
brush overlapping the edge of the world is normal and the two counts are how a client sees how much of it took.

**`height_import` reads PGM and PNG.** Binary PGM (netpbm P5) and noninterlaced PNG may carry 8-bit or 16-bit
samples. PNG greyscale is read directly, alpha is ignored, and RGB or RGBA input uses the red channel. Write PGM
headers with LF line endings: a header terminated with CRLF spends its CR as the single delimiter byte and leaves
the LF as sample 0, shifting the whole raster. The image's own row 0 is treated as the NORTH edge of the rect.

### Objects

| Verb | What it does |
|---|---|
| `object_place(archetypeId, x, z, plane, rotation = 0, tags?)` | Places one object from a catalog archetype and reports the id the document allocated. The archetype's collision kind is what makes the tile a wall or a solid block. |
| `object_move(id, x, z, plane)` | Moves the anchor, and the plane with it. The dirty rects cover both footprints. |
| `object_rotate(id, rotation)` | Turns it in place. A non-square archetype covers different tiles afterwards, so both footprints are reported. |
| `object_remove(id)` | Deletes it. The undo puts it back with the id it had, so every reference still resolves. |
| `object_set_tags(id, tags?)` | Replaces the authoring tags outright. Null or empty removes every tag. |
| `object_get(id)` | Archetype, anchor, plane, rotation, tags and the tile rect its rotated footprint covers. |
| `objects_in_rect(x, z, width, height, plane?)` | Every object whose ANCHOR falls inside the rect, in id order. An object anchored just outside but overhanging in is NOT listed, so widen the rect by the largest archetype when that matters. |
| `object_find(archetypeId?, tag?)` | Every object in the world matching an archetype, a tag, or both, in id order. Both null lists everything. |
| `objects_line(archetypeId, fromX, fromZ, toX, toZ, plane, rotation = 0)` | One object per tile of the straight line between two tiles, both ends included, as a SINGLE undo step. The fence and wall-run verb. |
| `objects_scatter(archetypeId, x, z, width, height, plane, spacing, jitter, seed)` | A deterministic scatter over a rect as a SINGLE undo step: a grid at `spacing`, each point jittered from a hash of that point and the seed, skipping blocked and already-occupied tiles. |

Both batch verbs report how many landed and their ids, so the batch can be tagged or removed as a unit. The
same scatter arguments always produce the same world, and an empty scatter is a legitimate answer for a crowded
rect.

### Markers

| Verb | What it does |
|---|---|
| `marker_set(name, x, z, plane, tags?)` | Places the named marker, or moves it when the name is taken (names are unique world-wide). |
| `marker_remove(name)` | Deletes it. The undo puts it back with its tags. |
| `marker_list()` | Every marker in name order, each with its tile, plane and tags. |

A marker is a uniquely named point on a tile carrying tags and nothing else. It is how a world names the places
a game looks up later (a spawn, a shop door, a quest step) without inventing an object for them, so markers
draw nothing and block nothing.

### Prefabs

| Verb | What it does |
|---|---|
| `prefab_save(x, z, width, height, planeFrom, planeCount, savePath, includeObjects = true, includeMarkers = true)` | Extracts a rect of the world into a prefab FILE. The file name without its extension becomes the prefab's name. |
| `prefab_place(prefabPath, x, z, plane, rotation = 0)` | Stamps a prefab with its south-west corner at the tile, turned by the given quarter turns. Everything it carries lands as a SINGLE undo step. |
| `prefab_list(directory)` | The prefab json files in a directory, by name, each with its full path and size. Fails when the directory does not exist. |

`prefab_save` is the one write verb here that changes NOTHING about the world, so it has no undo step (deleting
the file is the undo). `prefab_place` has one caveat worth knowing: a REDO re-runs the stamp rather than
restoring what it made, so the objects come back with FRESH ids and the world hash after a place, undo and redo
differs from the hash after the place alone. Same world to play, different object ids.

### Derived collision

| Verb | What it does |
|---|---|
| `collision_at(x, z, plane)` | The flag names, whether the tile is blocked outright, and whether a one-tile agent standing there could step north, east, south or west. Use it to work out why a path refuses to go somewhere. |
| `is_walkable(x, z, plane, agentSize = 1)` | Whether an agent that many tiles square, anchored here and extending north and east, stands clear. The same footprint rule the pathfinder walks with. |
| `path(fromX, fromZ, toX, toZ, plane, agentSize = 1, maxRadius = 64)` | The walk between two tiles on one plane, and the tiles it steps through. |
| `walkable_rect(x, z, width, height, plane)` | What a one-tile agent could stand on over a rect, as an ASCII map: `#` blocked, `.` open. North first. |

Collision is DERIVED, never authored: it is baked from the tiles' own `Blocked` setting plus the collision kind
of every object standing on them. The way to change what these verbs report is to paint a setting or place an
object and ask again.

`path` never changes plane. An unreachable goal still returns the steps to the NEAREST reachable tile with
`reached` false, and so does a goal simply further away than `maxRadius`, which is a search-window limit rather
than a verdict on the world. Widen `maxRadius` before concluding a place is cut off.

### Renders

| Verb | What it does |
|---|---|
| `render_topdown(x, z, width, height, plane, pxPerTile = 4, overlays = "", savePath?)` | An orthographic top-down PNG of one plane over a tile rect, north up and west left, exactly `pxPerTile` pixels per tile, so the image is `width * pxPerTile` across. |
| `render_view(eyeX, eyeY, eyeZ, targetX, targetY, targetZ, width = 640, height = 480, observerX?, observerZ?, savePath?)` | A perspective PNG from an eye toward a target, both in WORLD metres. The eye and the target must not coincide. |

**Both return the image INLINE**, as two content blocks: a text block naming the framing (rect, plane, scale,
overlays for the top-down, or eye, target, size and roof observer for the view), then the PNG itself. The text
comes first so a client can map image pixels back to tiles before it looks at them. `savePath` is optional and
additive: when given, the PNG is ALSO written there (the directory is created, a relative path resolves against
the world directory) and the saved path joins the framing line, so a client reading only the text still learns
where the file went.

`overlays` is a comma list painted into the captured pixels afterwards: `grid` (one line per tile edge),
`collision` (blocked tiles tinted, each wall drawn on the edge it blocks), `objects` (a dot at each anchor,
coloured by archetype, for the QUERIED plane only, since one dot per plane stacked on a tile would say nothing
about either) and `regions` (the borders between regions). They paint in a fixed order whatever order they were
asked in, so the anchors stay on top and two renders of the same overlays look the same. Empty paints none, and
an unknown name fails before any GPU work.

`observerX`/`observerZ` pin the tile the roof rule is judged from, so a shot aimed inside a building hides that
building's roof. Give both or neither: null for both uses the tile under the target, and one alone is refused
rather than silently ignored.

Meshes are greybox boxes sized from each archetype's footprint and collision kind, so a render reads as
structure rather than as art. A resolver that loads a game's real glb by mesh reference is later work.

**These two are the only verbs that need a GPU** (a headless Metal, D3D11 or Vulkan device, through
`TileWorldSnapshot` over `Render3DSnapshot`, the same code path the render goldens take). Every other verb runs
on a machine with no display or graphics device, and a render on a machine without a headless device fails with
a precise error rather than hanging.

## An authoring walkthrough

```text
world_create("/abs/path/assets/worlds/hollowmere", "hollowmere", "Hollowmere",
             ["../../catalogs/ground.json", "../../catalogs/archetypes.json"])
catalog_list("materials")                      # learn the legal ids before painting
tiles_fill(0, 0, 64, 64, 0, underlay: 2)       # grass over the whole starter region
height_raise(20, 20, 13, 13, 0, 220, 0.8)      # a hill, faded out at its edge ring
height_smooth(18, 18, 17, 17, 0, 2)            # blend it into the flat ground around it
height_flatten(8, 8, 7, 7, 0)                  # level the ground the house will sit on
prefab_place("prefabs/cottage.json", 8, 8, 0)  # or object_place, one piece at a time
objects_line("fence", 4, 4, 4, 20, 0)          # a run of fence posts, one undo step
objects_scatter("tree_oak", 40, 0, 24, 24, 0, spacing: 3, jitter: 1, seed: 7)
marker_set("spawn", 10, 10, 0)
walkable_rect(0, 0, 32, 32, 0)                 # cheap ASCII check before rendering
render_topdown(0, 0, 64, 64, 0, pxPerTile: 6, overlays: "grid,collision,objects")
world_validate()                               # what world_save would refuse, without failing
world_save()
```

Read an area with `tiles_get_rect` or `walkable_rect` for a few hundred tokens rather than thousands of
`tile_get` calls, then drill into the specific tile a map raised a question about. `world_summary` after a
batch of edits shows where the history and the dirty flag stand.

## Composition

`Tools/McpBootstrap.cs` is the one place the server's services and verb classes are registered, shared by the
stdio host in `Program.cs` and by the in-process wire-level tests, so the two can never disagree on which verbs
exist. Logging routes to stderr, so it never corrupts the JSON-RPC stream on stdout, and the host shuts down
cleanly when stdin closes.

Full document format, the command layer every mutation goes through, and the render arm sharing this same
model: [`KhaozEngine.TileWorld`](../KhaozEngine.TileWorld/README.md),
[`KhaozEngine.TileWorld.Editing`](../KhaozEngine.TileWorld.Editing/README.md), and
[`docs/design/TILE-WORLD-DESIGN-2026-08-15.md`](../docs/design/TILE-WORLD-DESIGN-2026-08-15.md). The
`ke-tileedit` section of [`docs/USING-KHAOZENGINE.md`](../docs/USING-KHAOZENGINE.md) has the same wiring
example plus more on the session model.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
