# OSRS-style tile world: document, collision, renderer, editor kernel, tile editor and MCP tool (2026-08-15)

Status: R1 to R5 shipped (document, file form, catalogs, validator, collision, pathfinder, raycast, prefabs,
then the renderer: ground mesher, props, view, residency, snapshot, goldens, then the editing kernel
`KhaozEngine.TileWorld.Editing` and the `ke-tileedit` MCP tool, then the Grimhollow bootstrap under the fly
camera at Grimhollow 0.2.0, then R5: textured ground through a new `TileGround` pipeline and river water through
the engine's water pass, sections 7.5 and 7.6, engine 17.38.0), R6
pending (editor kernel extraction and the GUI tile editor). Section 13 carries the delivery-order
reasoning. Program issue: [#629](https://github.com/APKiwiOrg/KhaozEngine/issues/629). First adopter: Grimhollow, a new low-poly 3D
MMO on the Ruinborne shell (repo to be scaffolded, `APKiwiOrg/Grimhollow`).

This is sub-project 1 of the Grimhollow program. The full program, for orientation only, is: (1) this tile
world plus its tools and one authored starter world, (2) the game bootstrap on Ruinborne's client/server/auth
shell, (3) tick-based click-to-walk movement, pathing and combat on the tile grid, (4) skills, XP, inventory,
bank, equipment, (5) NPCs, drops, shops, quests, (6) the passive skill tree(s). Only (1) is specified here.
The others are named so that (1) is shaped to serve them, and each gets its own spec when it starts.

## 1. Problem

KhaozEngine games have exactly one authorable world model, and it is the wrong shape for this game.
`KhaozEngine.MapDoc` is a continuous heightmap terrain document: an analytic `TerrainConfig` plus a sculpt
delta layer, with placements at continuous float positions, streamed in 512 m document tiles. Its
`csproj` references `Terrain` directly and it has no document-kind seam (`MapDocRegistry` is a terrain
feature registry only). `KhaozEngine.MapEditor` is a monolith over that document (`MapEditorScene` 3276
lines, `EditorCommands` 2710 lines, a closed `EditorToolMode` enum cast from a toolbar index). `ke-mapedit`
holds one `MapDocument` per session.

An OSRS-style world is a different thing: a discrete tile grid with stacked planes, a height at every tile
corner, a ground underlay and an optional shaped overlay per tile, walls that block the EDGE between two
walkable tiles rather than a tile, and objects anchored to tiles with quarter-turn rotations. Nothing in the
engine models it, renders it, edits it, or lets an AI author it. `Navigation.NavGrid` is a per-cell
clearance grid with no per-edge blocking, so it cannot even represent the collision. `Dungeon` has a 3D tile
grid but its cell is one byte and it is procgen output, not an authored document.

The user's instinct that "map tools come first" is therefore right, and more specifically: a tile-world
document and its tools come first, and they are a sibling of the existing map stack, not an extension of it.

## 2. Decisions taken in the brainstorm, with rationale

1. **Presentation: low-poly 3D over the tile grid (OSRS proper).** Rejected 2D top-down sprites (Tibia look,
   cheapest art, but the engine's 3D investment sits idle and the named look is lost) and 2.5D (inherits both
   pipelines). The document core is presentation-agnostic (grid, planes, flags, objects), but corner heights,
   the editor viewport and the asset pipeline hinge on this.
2. **Authoring: AI-first, GUI-second.** The MCP tool is the primary authoring path, the in-engine GUI editor
   is for inspection, touch-up and fly-through. Both fronts execute one command set over one document, which
   is how the existing map editor was designed. Bulk procgen-then-tune was rejected: OSRS's charm is
   hand-placed density, and procgen fights it. A cheap heightmap import survives as one MCP verb.
3. **Sub-project 1 stops at tools + one authored region, fly-camera only.** Click-to-walk on the tick is
   the next sub-project's opener, because tick movement, run, pathing rules and edge blocking deserve their
   own spec. The collision model is designed and MCP-queryable now so that spec has nothing to redo.
4. **Sibling stack + extracted editor kernel** (section 3), scored 48/60 against folding a tile kind into
   the existing stack (29/60) and a tile overlay on the continuous terrain (35/60).
5. **The game is Grimhollow.**

## 3. Approach: sibling stack, scored

| Criterion | Sibling stack | Fold into MapDoc/MapEditor | Tile overlay on terrain |
|---|---|---|---|
| Fit to the OSRS model (planes, corner heights, edge walls, tile objects) | 9 | 6 | 3 |
| Reuse of what the engine has | 7 | 8 | 10 |
| Blast radius on Ruinborne and the existing editor | 8 | 3 | 6 |
| Time to first authored region | 6 | 5 | 8 |
| Extensibility (prefabs, dungeons emitting into it, a third doc kind) | 9 | 5 | 3 |
| God-file / KESIZE ratchet risk | 9 | 2 | 5 |
| **Total** | **48** | **29** | **35** |

Fold-in keeps the `Terrain` dependency in the doc, grows both god-files past the ratchet by construction, and
puts Ruinborne's live editor at risk on every change. The overlay keeps Ruinborne's visual identity (smooth
splatted LOD terrain, no corner lattice, no planes) and every OSRS feature fights the base representation
forever.

What the sibling stack reuses, verified against the code:

- Render3D's `ModelVertex` carries a vertex colour the lit model shader multiplies into albedo, `MeshBuilder`
  merges coloured parts, `SceneInstances` + `PropLayer`/`PropRenderer` (LOD, HLOD, distance dissolve,
  instancing) already draw many small objects. Only the ground mesher is new. No new material or shader.
- `MapEditor`'s kernel pieces are clean and generic: `EditorHistory` (undo/redo with gesture coalescing),
  `EditorDocument` (dirty tracking, `IEditorCommand`), `EditorSelection`, `EditorRecentFiles`,
  `EditorSettings` + settings dialog, `MapEditorLandingScene` + `MapEditorLandingOptions` (head hooks:
  `CreateMap`, `OpenEditor`, `DiscoverMaps`, `Recent`, `RequestQuit`), `GizmoGeometry`/`GizmoDrag`,
  `MapEditorEnvironment`.
- `ke-mapedit`'s bootstrap: stdio host, one session singleton, query/mutation/render services,
  attribute-registered verbs (`[McpServerToolType]`, `[McpServerTool]`) behind `ToolGuard`, headless PNG
  via `Render3DSnapshot`. `MapEdit.Tool` already references `MapEditor` for `ViewportWorld`, which is the
  precedent for `TileEdit.Tool` referencing `TileEditor` for the command set.
- Ruinborne's 208-line `Ruinborne.Editor` head is the template for `Grimhollow.Editor`, and Ruinborne loads
  its world at exactly three call sites (`RuinborneWorld`, `BootCoordinator`, server `Program`), which is
  what sub-project 2 re-points.

## 4. Package plan

Five engine packages on the shared version line, one game head.

| Package | Umbrella | Depends on | Holds |
|---|---|---|---|
| `KhaozEngine.TileWorld` | Foundation | Primitives, Serialization (Content only if the catalog loader reuses its manifest helpers, decided at plan time) | Document, file form, catalogs, validator, migrations, collision map + baker, pathfinder, raycast, prefab |
| `KhaozEngine.TileWorld.Render3D` | Game3D | TileWorld, Render3D, Terrain.Render3D | Ground mesher, `TileWorldView`, `TileRegionResidency`, prop bridge, roof rules |
| `KhaozEngine.TileWorld.Editing` | Foundation | TileWorld | The command layer: `ITileCommand`/`TileDirtyRect`/`TileCommandBase`, `TileEditHistory`, `TileEditingDocument`, the concrete commands, `TileEditOps`, `PgmReader` |
| `KhaozEngine.Editor` | none (dev tooling, like MapEditor) | Gui, Game.Render3D, Render3D | The extracted editor kernel |
| `KhaozEngine.TileEditor` | none | Editor, TileWorld, TileWorld.Editing, TileWorld.Render3D | `TileEditorScene`, tools, wrapping the command layer's commands |
| `KhaozEngine.TileEdit.Tool` | none (dotnet tool, `ke-tileedit`) | TileWorld.Editing, TileWorld.Render3D, Imaging, ModelContextProtocol | The MCP tool |

`Terrain.Render3D` on the renderer is for `PropLayer`/`PropRenderer`, which live there today. If that prop
path is ever lifted to `Render3D` proper the edge disappears, but lifting it is not this program's job.
`Editor`'s exact dependency set is fixed at plan time from what the lifted types actually reference (the
landing scene and settings dialog need `Gui`, the gizmos and environment need `Render3D`).

Whether `Editor` and the two `Tile*` editor packages join an umbrella: no. `MapEditor` is not in one either.
They are dev tooling referenced by editor heads explicitly.

**`TileWorld.Editing` is new against the original plan, and it is why the tool shipped before the GUI.** The
plan above had the command set inside `TileEditor`, which made `TileEdit.Tool` depend on a Gui and Render3D
package to reach it, and made the MCP tool undeliverable until the GUI editor existed. R3 moved the commands
into their own GPU-free package instead, referencing `TileWorld` alone, so the tool ships on the document plus
the renderer and nothing else. The design's load-bearing claim, ONE command set for both frontends (section 9),
is unchanged and in fact easier to keep: the R5 `TileEditor` wraps these same commands rather than owning them,
so neither frontend can quietly become the definition of what an edit is. Being GPU-free also puts the layer in
the `Foundation` umbrella and puts its whole test suite in a headless project.

## 5. The document model (`KhaozEngine.TileWorld`)

### 5.1 World

`TileWorldDocument`:

- `Schema` (a URI), `FormatVersion` (starts at 1, migrations registered on a `TileWorldLoadOptions` the same
  way `MapDocumentLoadOptions.RegisterMigration` works), `Id`, `DisplayName`.
- `TileSize` (metres per tile, default 1.0, so `Locomotion`, physics and the renderer stay in metres, and a
  player is about 1.8 tiles tall, close to OSRS's proportions).
- `PlaneCount` (default 4).
- `PlaneHeight` (metres, default 3.0): the derived lift of a plane that carries no authored heights.
- `CatalogPaths` (relative to `world.json`): the ground material and object archetype catalogs this world
  was authored against. The catalogs are game content and are never stored in the document (section 5.4),
  but the world is self-describing about which ones it needs, so `world_open(path)`, the editor head, the
  client and the server all open a world with one argument.
- `Regions`: sparse, keyed by `RegionCoord(rx, rz)`. A region is 64x64 tiles (`TileRegion.Size = 64`,
  OSRS's map-square granularity, and the streaming unit on every head).
- `NextObjectId` (long): the document-wide allocator for object ids.

World tile coordinates are `(x, z, plane)` with `x = rx * 64 + localX`, so an object or marker's world
position is derivable from its region and local coordinates without a lookup.

### 5.2 Per region, per plane: four dense 64x64 layers

Each `TileRegion` holds `TilePlaneData[PlaneCount]`, and each plane holds:

- `Heights` (`short[4096]`, centimetres): the height of each tile's SW corner. **One global lattice.** A
  region's north and east far edges are read from the neighbouring region, and edge-extended when that
  neighbour is absent. There is no duplicated seam row to drift. A plane whose `Heights` is null derives
  every corner as `plane0 + p * PlaneHeight`, so a region file only carries the planes that were sculpted.
  Centimetre `short` was chosen over `float`: no canonicalisation problem for the hash, ±327 m is more than
  an OSRS-scale world ever needs, and 1 cm is finer than any brush.
- `Underlay` (`ushort[4096]`): ground material id. `0` is void: no ground is drawn and the tile is blocked.
- `Overlay` (`ushort[4096]`) + `OverlayShape` (`byte[4096]`) + `OverlayRotation` (`byte[4096]`, 0..3):
  material id (`0` = none), shape from `TileOverlayShape { Full, DiagonalHalf, CornerQuarter,
  CornerThreeQuarter }` (extensible, the OSRS shape family is larger and can be added by value without a
  format break), and a quarter-turn rotation. This is how a road gets a diagonal edge and a rounded corner.
- `Settings` (`byte[4096]`, authored flags): `Blocked` (impassable ground, water and cliffs), `Indoors`
  (roof-hiding trigger), `Bridge` (reserved for the over/under plane trick, not implemented in this
  program), `NoDraw` (skip the ground quad but keep the tile walkable, for a plane whose floor is an
  object). Bits 4-7 free.

Layers null out when entirely default so an empty region file is small.

### 5.3 Per region, sparse

- `Objects`: `List<TileObject>` across all planes. `TileObject { Id (long, unique per document, allocated
  from NextObjectId, MCP-addressable), ArchetypeId (string), X, Z (WORLD tile coordinates, the anchor is the
  SW tile of the footprint, the owning region is `RegionCoord.Of(X, Z)`), Plane, Rotation (0..3 quarter
  turns), Tags (string list, optional) }`. The footprint comes from the archetype and is swapped by rotation.
  A footprint may cross into a neighbouring region: the anchor region owns the object, and the collision baker
  resolves the spill.
- `Markers`: `List<TileMarker>`, `{ Name, X, Z, Plane, Tags }`, X and Z world coordinates like an object's:
  spawn points, bank and respawn anchors, later NPC spawn sites. Gameplay positions are authored in the same
  tool as the world and never live in code.

World coordinates rather than region-local ones were decided at R1 plan time. Region-local buys nothing once
regions are 64-aligned (the local pair is one `FloorMod` away, and `TileCoord.LocalX`/`LocalZ` expose it), and
it would make every consumer carry the region alongside an object just to know where that object is.

### 5.4 Catalogs (game content, referenced by id)

- `GroundMaterialCatalog`: `GroundMaterial { Id (ushort), Name, Color (RGB), Texture (optional ref, unused
  by the v1 renderer), Kind (Ground | Water) }`.
- `TileObjectArchetypeCatalog`: `TileObjectArchetype { Id (string), Name, MeshRef (glTF path), SizeX, SizeZ
  (footprint in tiles), CollisionKind (None | Solid | Wall | WallCorner | Diagonal), IsRoof, Interactive,
  YawOffsetDegrees, Tags }`.

Both are JSON files loaded by `TileWorldCatalogs.Load(paths)` with their own schema. The engine ships the
schema, the loader and a tiny greybox catalog pair under `TestSupport` for tests. Grimhollow ships its own.
`TileWorldValidator.Validate(document, catalogs)` fails a dangling material or archetype id, a plane out of
range, or a footprint that would leave every loaded region, at save time in the tools and at load time on the
heads, so a bad id fails in the editor and not at boot.

### 5.5 File form

One form only, a directory:

```
<world>/
  world.json                 manifest: header fields, CatalogPaths, NextObjectId,
                             regions: [{ rx, rz, hash }]
  regions/r_<rx>_<rz>.json   one region: planes[] with dense layers as base64 little-endian,
                             objects[] and markers[] as JSON arrays
```

- Stable file names, so a git diff points at the region that changed. Content-addressed names were weighed
  (the tiled `MapDoc` form uses them for crash-atomic commit) and rejected here: they churn every rename in
  git for a hand-authored, version-controlled world, and the crash safety is had another way.
- Save: each region is written to `r_<rx>_<rz>.json.tmp` and renamed into place, the manifest is written to
  `world.json.tmp` and renamed LAST. Only regions whose in-memory dirty flag is set are rewritten.
- Load: each region's bytes are hashed and compared to the manifest's entry. A mismatch is a torn write or a
  hand edit that forgot the manifest, and the load refuses, naming the region.
- `WorldHash`: SHA-256 over the canonical manifest bytes, which compose the per-region hashes, so it is
  independent of region write order and computed without re-serialising the regions. This is the value
  Grimhollow's client and server will compare, the way Ruinborne's `MapDocumentDigest` compares
  `MapDocumentHash`.
- Canonical region bytes: dense layers are written as base64 of little-endian arrays (a `short[4096]` is
  8 KB raw, about 11 KB in base64), objects and markers sorted by `(Plane, Z, X, Id)` before writing.
- Size: a fully authored region-plane is about 36 KB of dense data raw (heights, underlay and overlay at
  2 bytes per tile, shape, rotation and settings at 1) and about 48 KB in base64, so a region with one
  authored plane and three sparse ones is well under 100 KB and a 3x3 starter world under 1 MB. Server-side,
  a 30x30-region world with every plane of every region authored would be about 130 MB of raw arrays in
  memory, and far less in practice since upper planes are mostly null. Fine either way.
- Loading has two entry points over one document type: `TileWorldFile.Load(path)` materialises every region
  (tools, server, tests), and `TileWorldSource.Open(path)` reads the manifest and materialises regions on
  demand as `TileRegionResidency` (section 7.3) asks for them, which is what a streaming client uses. A
  region loaded on demand is hash-checked exactly like an eager one.

Dense layers as base64 rather than JSON arrays: JSON arrays of 4096 ints per layer are 5x the bytes and the
one thing that would make hand-reading a region file feasible, and nobody hand-reads a region file (the AI
reads through `tiles_get_rect`, the human through the editor). The trade was made for git size and load
speed.

### 5.6 Mutation API

`TileWorldDocument` is a mutable model with `TileRegion` accessors (`GetRegion`, `GetOrCreateRegion`,
`TryGetTile`, `SetHeight`, `SetUnderlay`, ...). It has no undo of its own: undo is the command layer's job
(section 8), and both frontends mutate through the `KhaozEngine.TileWorld.Editing` commands R3 shipped, which
8.2 names in full. There is no `TileWorldCommands` type. The document raises no events. Instead
every mutation returns the dirty rect it touched, which the commands accumulate for the collision rebake and
the region-plane remesh.

## 6. Collision and pathing (`KhaozEngine.TileWorld`)

### 6.1 Derived, never authored

`TileCollisionMap` per region-plane: `ushort[4096]` of `TileCollisionFlags`:

| Bit | Flag | Set by |
|---|---|---|
| 0 | `Blocked` | void underlay, `Settings.Blocked`, a `Solid` footprint tile, a `Diagonal` archetype |
| 1..4 | `WallN`, `WallE`, `WallS`, `WallW` | a `Wall` on this tile facing that edge, or the mirrored edge of a wall on the neighbour |
| 5..8 | `CornerNE`, `CornerNW`, `CornerSE`, `CornerSW` | a `WallCorner` (with its two edges) |
| 9 | `ProjectileBlocked` | reserved for ranged line of sight in a later sub-project, never set in this program |
| 10 | `Decoration` | reserved, never set in this program |

`TileCollisionBaker.Bake(document, catalogs)` builds every region-plane at load. `Rebake(document, catalogs,
region, plane, dirtyRect)` rebuilds one rect after a mutation, expanded by one tile so a wall's mirrored
edge on a neighbour tile (including across a region boundary) is recomputed. Rules, in order:

1. `Underlay == 0` or `Settings.Blocked` -> `Blocked`.
2. `Solid` -> `Blocked` on every footprint tile (rotation swaps `SizeX`/`SizeZ`), spilling into the
   neighbour region when the footprint crosses it.
3. `Wall` -> the edge bit facing `Rotation` on the object's tile AND the mirrored bit on the tile across that
   edge, in the neighbouring region if need be. A wall is one edge shared by two tiles: this is the OSRS rule
   that keeps walls symmetric, so `CanStep` never has to look at objects.
4. `WallCorner` -> the two edges of the corner named by `Rotation` plus that corner bit, both mirrored.
5. `Diagonal` -> `Blocked` (OSRS's diagonal wall type blocks the whole tile for movement).
6. `Interactive` and `IsRoof` never touch collision. `CollisionKind.None` never touches collision.

**A spill into a region the DOCUMENT does not have is DROPPED, and that region stays `Blocked`.** Storage is
allocated by `TileCollisionMap.EnsureRegion` alone, and both entry points call it on the document's own regions
before any object is applied (`Bake` on every region the document has, `Rebake` on the ones the cleared rect
touches), so `Or` outside storage is a no-op, and `EnsureRegion` is the only allocator. Letting a footprint or a
mirrored wall edge allocate its own region instead would turn the whole 64x64 of a region nobody authored
walkable, since every tile the spill did not touch would then read as clear rather than as the edge of the
world. Reads outside storage answer `Blocked` for the same reason: an unloaded region is a wall, not a void.

### 6.2 Movement primitive

`TileCollision.CanStep(map, x, z, plane, dir, agentSize = 1)`: for a 1x1 agent, the leaving tile has no wall on
`dir`'s edge, the entering tile is not `Blocked` and has no wall on the opposite edge, and a diagonal step
additionally requires both orthogonal intermediate steps to be legal (no corner cutting, and the corner bits
block the diagonal). For an NxN agent the same check runs over the leading edge tiles of the footprint. This
is the one primitive the tick movement of the next sub-project and the pathfinder share.

### 6.3 Pathfinder

`TilePathfinder.FindPath(map, plane, start, goal, agentSize = 1, maxRadius = 64)`: integer BFS over the
collision map (8-connected through `CanStep`), a fixed neighbour order (W, E, S, N, SW, SE, NW, NE, the OSRS
order, so both heads replay identical paths for identical inputs), search bounded to a `(2 * maxRadius)`
square around the start, with `maxRadius` itself bounded to 1..`MaxSearchRadius` (4096) because the window's
two scratch arrays are `(2r + 1)^2` entries each and a radius past that is a caller passing the wrong unit
rather than a search anyone wants to run. OSRS's partial-path rule: an unreachable goal yields the path to the
nearest reachable tile, nearest by SQUARED EUCLIDEAN distance to the goal (OSRS's own tie-break, and Chebyshev
would tie a whole column at distance N), then by path length, then by scan order. Result
`TilePath { Tiles, Reached, End }`, and callers branch on `Reached`, never on `Tiles.Count`, because a partial
walk carries steps too. A start standing on a `Blocked` tile is treated like any other start, since `CanStep`
allows egress from a tile that was blocked under the agent. Stairs and ladders are gameplay teleports across
planes, not collision.

Not folded into `Navigation.NavGrid`: that is a clearance grid for continuous worlds with per-cell blocking
only, and its A* expands 8-connected neighbours implicitly, so per-edge walls cannot be expressed there
without changing its model. An `IPathPlanner` adapter over `TilePathfinder` (tile centres to `Vector3`) is
about 30 lines and is filed as a follow-up for when sub-project 5's NPC AI wants `Navigation`'s utilities.
It is not built now, because nothing in this program calls it.

### 6.4 Raycast and prefabs (also GPU-free)

- `TileRaycast.Pick(document, plane, origin, direction, maxDistance = 2000f) -> TileHit? { X, Z, Plane, Point,
  Distance }`: ray against the lattice triangles of the loaded regions, in the document package because
  the next sub-project's click-to-walk needs it server-free.
- `TilePrefab`: `Extract(document, catalogs, rect, planeFrom, planeCount, includeObjects, includeMarkers,
  name) -> TilePrefab` (dense layers per plane, objects and markers with rect-relative coordinates, catalog
  ids by value plus each object's UNROTATED footprint size so a later rotation needs no catalog) and
  `Place(document, prefab, x, z, plane, rotation)` (rotates layers, shapes, object rotations and footprints,
  and re-allocates object ids). Serialised to JSON in a game-owned prefabs directory. Prefabs are what make
  "build one house, stamp a village" a single verb for the AI.

**The prefab datum contract.** The SW corner of the extracted rect is the height datum, on EVERY plane: heights
come out relative to that one corner on `planeFrom`, so the offsets between planes survive extraction.
`Rotate` re-bases after the turn, because a quarter turn moves the SW corner to a different physical corner and
would otherwise leave the heights relative to a corner that is no longer the prefab's own (0, 0). It then
re-trims, so a rotated prefab is shaped exactly like a fresh `Extract` of the same content. `Place` therefore
lands the stamp on the existing ground at (x, z) whatever the rotation, and it validates the prefab's shape
(sizes, layer lengths, and every object's and marker's plane), rotates, and requires every region of the TILE
RECT BEFORE the first write, so a bad stamp cannot tear half way through. The far-edge CORNER writes at `x + w`
and `z + h` are the deliberate exception. Their region may not exist at the edge of the authored world, and
refusing a stamp there is worse than dropping them: a corner outside the tile rect's regions is edge-extended
from those regions on the way back out, so it never carries a value of its own to lose. Those writes are
therefore SKIPPED, which is why `Place` writes corners through `TrySetCornerHeightCm` rather than the throwing
form. A stamp is ADDITIVE per layer: a null layer is skipped rather than zeroed, so pre-existing overlays or
settings under the stamp survive it, and a caller wanting a replace clears the rect first.

## 7. Renderer (`KhaozEngine.TileWorld.Render3D`)

### 7.1 Ground

`TileGroundMesher.Build(document, catalogs, region, plane) -> GltfMesh`, one mesh per region-plane. 4096
tiles is about 16k vertices, cheap enough that an edit rebuilds the whole region-plane through a per-frame
coalescing scheduler. Sub-chunking is not built until measured.

- Each tile is two triangles, split along the diagonal whose two corners differ least in height (removes
  saddle artifacts, deterministic). A `DiagonalHalf` overlay forces the split along its diagonal.
- Vertices are the existing `ModelVertex` (position, normal, colour, uv, tangent) through the existing lit
  model shader. The OSRS look is vertex colour: no new material, no new shader.
- Corner colour = the average of the underlay colours of the up-to-4 tiles sharing that corner (void tiles
  excluded), plus a small deterministic per-tile hue jitter hashed from the world tile coordinate. This is
  the soft grass-to-dirt gradient.
- Overlays are flat per tile (no blend), cut by shape: `Full` replaces the tile's two triangles,
  `DiagonalHalf` colours one triangle, `CornerQuarter` and `CornerThreeQuarter` add sub-triangles. The
  underlay fills the remainder.
- Smooth normals from the lattice. Void tiles emit nothing. No skirts and no seams: the lattice is global,
  so neighbouring regions share corner heights exactly.
- v1 was colour-only, with UVs written tile-local (0..1) and `Kind = Water` rendering as flat colour. R5
  replaces both: the ground material in 7.5 and the water planes in 7.6. The mesher's geometry, split rule,
  corner blend and overlay cut do not change, only what a vertex carries and which pipeline draws it.

**Fold-backs from the R2 implementation.** Five things this sketch left open were decided while building it, and
the shipped behaviour is the authority:

- **The mesh is REGION LOCAL, drawn with a translation.** `Build` returns vertices in the region's own frame with
  absolute Y, and `TileGroundMesher.WorldMatrix(doc, region)` is the pure translation that places it. Absolute
  world positions in the vertex buffer would have cost float precision far from the origin for no gain, and a
  region-local mesh moves an unload to dropping one handle.
- **Vertices are PER TRIANGLE, never shared.** Two triangles of one tile can carry different colours (the overlay
  paints some and not others), so sharing corners would need the colour to be a per-face attribute the model path
  does not have. The 16k-vertex estimate above therefore roughly doubles, which is still cheap enough that an edit
  rebuilds the whole region-plane.
- **Normals are central differences over the GLOBAL lattice.** `CornerNormal` reads `CornerHeightCm` one corner
  out on each axis, which crosses region borders by construction, so two regions meeting at a corner compute a
  bit-identical normal. The world-z gradient takes `+hz` rather than `-hz`, because tile z and world z run
  opposite ways (7.4). A mid-edge point averages its two corners' normals and renormalises, so a cut edge lights
  as the surface does there. `SmoothNormals = false` is the flat-shaded alternative, one face normal per triangle,
  flipped up rather than the winding being reversed, because the renderer culls nothing.
- **A dangling material id renders MAGENTA** (`TileGroundMesher.MissingMaterialColor`), not void and not the
  default ground. An id the catalogs no longer define is a content bug, and the failure mode that costs the most
  is the one nobody sees: an invisible tile reads as authored void.
- **Overlay shapes are exact geometry through one shared triangulation.** The cut is
  `TileTriangulation.Triangulate` in `KhaozEngine.TileWorld` (`TileLatticePoint` lattice points, `TileLatticeTriangle` records,
  `MaxTriangles` 4), called with the same inputs by the mesher and by `TileRaycast`, so a click lands on the
  triangle that was drawn rather than on a plain pair that is the wrong height in the middle of a corner-cut tile.
  A shape whose overlay material is missing meshes as `Full`, and the raycast reads that identically. Triangles
  are normalised to one winding in TILE space, which the z flip mirrors in world space. That is inert here (the
  renderer culls nothing and a flat normal is flipped up), and it is normalised anyway so a future culling pass
  keeps or drops a tile's triangles together instead of half of them.

### 7.2 Objects

`TileObjectProps.Build(document, catalogs, region) -> PropPlacement[]`, fed to the existing
`PropLayer.PlacementLayer` and drawn by `PropRenderer` (LOD, HLOD, distance dissolve, instancing already
there). Position = the rotated footprint centre at the bilinear lattice height, yaw = `Rotation * 90 +
YawOffsetDegrees`, mesh from the archetype's glTF ref through the existing model loader. One prop batch per
region so an unload is a drop. A missing mesh ref draws a placeholder box and logs once per archetype: a
bad ref never faults the view.

The resolver that reads the archetype's glTF ref shipped in 17.39.0 as `GltfMeshResolver`, chained over
`GreyboxMeshResolver` as its fallback, with the log-once rule above applying to a missing file and a loader throw
alike.

### 7.3 Runtime

`TileWorldView` owns loaded region meshes and prop batches in a `Scene3D`, driven by `TileRegionResidency`
(a Chebyshev ring of regions around an anchor, the `MapTileResidency` shape, radius default 1 for the editor
and configurable for the client). Plane rules: all planes are drawn, and `IsRoof` objects on planes above the
observer are hidden while the observer's tile carries `Indoors`, which is OSRS's global roof-hide. (Superseded
in 18.10.0: the plane-wide form stripped every roof in view when the observer walked into one building, so the
shipped rule hides only the roofs over the observer's own interior and the plane-wide form became
`RoofVisibility.AlwaysHidden`. See `CHANGELOG.md` and the package README.) Headless
PNG for MCP goes through the same `Render3DSnapshot` path `ke-mapedit` uses, with `TileWorldView` in place
of `ViewportWorld`.

**Fold-backs from the R2 implementation.**

- **`ITileWorldScene` is the seam, not `Scene3D`.** The view talks to a small interface (six members in R2: `LoadMesh`,
  `UnloadMesh`, `DrawMesh`, `LoadPropMeshes`, `UnloadPropMeshes`, `DrawProps`, plus R5's `LoadTileGroundMaterial`,
  `UnloadTileGroundMaterial`, the material `LoadMesh` overload and `DrawWater` as default implementations, 7.5
  and 7.6) shaped exactly on
  what `Scene3D` and the prop renderer already offer. `Scene3DTileWorldScene` ships and forwards straight through, and the tests
  drive a recording fake, so every view and residency rule (dirty coalescing, the flush budget, the roof rule,
  ring hysteresis, neighbour marking) is covered headless with no device. This was not in the sketch and is the
  single change that made the round testable.
- **A per-flush rebuild budget, because streaming makes bursts real.** `TileWorldViewOptions.MaxRebuildsPerFlush`
  (16) caps how many queued region-planes one flush remeshes, oldest first, with `PendingRebuilds` reporting the
  remainder and `Flush(int)` overriding it. The burst is not theoretical: two region loads in one update against a
  four-plane world is 64 marks. The budget counts only rebuilds that PRODUCE a mesh, so an authored-plane-0 world
  does not burn three quarters of every flush on region-planes that mesh to null, and a mark on a region that is
  not loaded is dropped free.
- **Streaming a region marks its eight neighbours dirty, on load AND on unload.** A region mesh is not
  self-contained (7.1's fold-backs), so a region meshed while its neighbour was absent carries an edge-extended
  border, which on a ridge along the shared border is a full-height crack rather than a subtle seam. Marking wide
  is free because the flush drops what is not loaded, and the budget keeps the burst off one frame.
  `PrimeAround` therefore ends with an unbudgeted flush, so a teleport settles in one call.
- **The dirty margin is 2 tiles, unconditionally.** `MarkDirty(TileRect, plane)` expands by
  `TileWorldView.DirtyRegionMargin` before turning the rect into region marks: a corner is shared by four tiles,
  a central-difference normal reads one corner further still, and a corner colour averages the tiles meeting
  there. The rect does not say which of those three an edit touched, and marking wide costs nothing.
- **A dirty region is never streamed out.** `TileWorldSource.Unload` throws on unsaved edits, so the residency
  keeps that region resident past the unload radius and logs once instead. The resident set can then exceed the
  ring, which is the correct trade: an editor holds a handful of dirty regions, and the alternative is losing the
  edit.

### 7.4 Handedness: world z is minus tile z

Decided at R2 plan execution: the tile-to-world mapping negates z, and every conversion goes through one
`TileWorldSpace` helper in `KhaozEngine.TileWorld`. The document's convention is x east, z north, y up, but the
engine renders right-handed with y up, where a camera facing +z has east on its LEFT and a top-down view with
+z up on screen has east on the left. The first captures proved it rather than argued it: the top-down with east
right came out with north DOWN, and the perspective looking north-west put the road, which is west of the house,
on the RIGHT. So the naive `worldZ = +tileZ * TileSize` renders the world as its own mirror image against a
compass, and a north-up minimap would contradict what the player sees. Negating z fixes it at one seam: north
(+tile z) becomes -world z, which is also a right-handed camera's default forward, and (east, north, up) =
(+x, -z, +y) stays a right-handed triple, so the top-down gets north up and east right at once rather than
having to trade one for the other. Two consequences fall out and are pinned by tests. A region-local ground mesh
runs from 0 to MINUS 64 tiles on z, and its lattice normal takes +hz rather than -hz because the tile-z gradient
and the world-z gradient have opposite signs. And the yaw for an instance rotation is NEGATIVE per quarter turn
(section 7.2's `Rotation * 90 + YawOffsetDegrees`, negated), because a row-vector `CreateRotationY(t)` sends the
west point `(-0.5, 0, 0)` to `(-0.5 cos t, 0, +0.5 sin t)`, which only reaches the north point `(0, 0, -0.5)` at
t of -90 degrees.

### 7.5 Ground materials (R5): textures per catalog material, blended at the corners

The R4 fly-through (Grimhollow 0.2.0) showed what vertex colour alone gives: ground in flat soft blobs, a river
as a blue strip, roads as tan strips. Section 14 had deferred textured ground and water as "v1 is vertex colour
only, UVs and `Kind = Water` reserved". This is that item, designed against the renderer as it stands.

**What already exists, and why it is not simply reused.** The continuous terrain (`Terrain.Render3D`) draws
through a splat pipeline (`ShaderSources.Terrain`, `Scene3D.LoadSplatMaterial`, `LoadMesh(mesh,
SplatMaterialHandle)`) that is already a texture ARRAY, triplanar or planar world-space tiling, per-layer tint
and tiles-per-metre, lit and shadowed by the same `LightingCommonGlsl` block the model path uses. It takes any
`GltfMesh`. What it cannot do is N materials: its layer count is a `const 5`, the four leading weights ride in
`ModelVertex.Color` and the fifth is the remainder, the tint UBO is `vec4[5]`. A tile world's catalog is open
ended (Hollowmere's ground catalog has 13 materials and one region touches 11 of them), so five fixed slots per
mesh cannot hold it. Fold-in (widen the splat pipeline to N) was weighed and rejected: it is Ruinborne's live
terrain path with goldens of its own, and the tile ground's vertex needs differ (a palette per triangle, a
jitter scalar, no per-vertex splat weights over a fixed set). What IS reused is everything around the pipeline:
the texture-array creation (`TextureUploads.CreateSplatArrays` or its sibling), the lighting block, the MRT
layout, the mip policy, the PNG decode (`ImageRgba` in `Render2D`, which `Render3D` already references), and
the constraints the terrain pass already paid for (one UBO per pipeline on Metal, textures sampled in binding
order, fragment interpolants contiguous from location 0 for FXC).

**The pipeline: `TileGround`, one new shader pair in `Render3D`, same vertex layout as the model path.**
`ModelVertex` does not change, so the mesher keeps emitting `GltfMesh` and nothing in the upload path moves.
The fields are repurposed for this pipeline only:

- Every triangle of a tile carries the SAME four material slots: the material chosen at each of the tile's
  four corners (SW, SE, NW, NE, see the mesher rule below), as floats holding integers in `Uv.x`, `Uv.y`,
  `Tangent.x`, `Tangent.y`. They are constant across the triangle (the mesher emits per-triangle vertices,
  7.1), so interpolation cannot smear them, and the fragment reads them as `int(x + 0.5)` the way the splat
  pass reads its packed values.
- `Color` = that vertex's four weights over the tile's four corner slots, all four in `Color`, renormalised by
  their own sum in the fragment, no one-minus-sum remainder (the splat pass's fifth-layer idiom does not
  apply). A corner vertex is one-hot on its own slot, a mid-edge point (an overlay cut) is 0.5 and 0.5 on its
  two end corners, a tile-centre point 0.25 on each. An overlay triangle holds its overlay material in ALL FOUR
  slots with weights (1, 0, 0, 0), so the painted part reads that one material flat however the tile under it
  blends, and no lane can leak the ground's palette into it.
- Every material set keeps its LAST slot (`Layers.Count - 1`, which is 63 only for a full set or for the
  set-less `IdentitySlotMap`) as the missing-material layer, filled
  with the magenta of the dangling-id rule (7.1). `ITileGroundSlotMap.MissingSlot` names it and a dangling id maps
  there, which is what carries that rule through texturing: slot 0 stays an ordinary material, because a set built
  in catalog id order would otherwise burn its first layer in every set.
- `Tangent.z` = the per-vertex brightness jitter, the same sharing-tile average the colour path uses today, so
  it stays soft across corners rather than stepping per tile. `Tangent.w` = 0.
- `Normal` = the lattice normal as today. No normal maps in R5: the OSRS register is flat low-frequency
  texture under smooth lighting, and four more array samples per fragment buy little. The array is built
  albedo-only (a new albedo-only sibling of `TextureUploads.CreateSplatArrays`, which wants both maps), and a
  `NormalArray` is the documented next step if the look wants it (section 14).

Why four slots per tile rather than a palette per triangle: continuity. Today a lattice corner is one colour
shared by every triangle touching it, so the ground is C0 continuous by construction. A per-triangle palette
capped at four would make two triangles either side of a tile edge fold a busy corner differently and seam
along that edge. With the slots fixed per TILE and a corner vertex one-hot on its own corner's material, a
shared corner samples the same material at weight 1 from every triangle, and a shared edge interpolates the
same two materials with the same weights from both sides, so the surface is continuous everywhere at four
samples per fragment and there is no cap to fall off.

The fragment samples the albedo array four times (`textureGrad`, derivatives hoisted, same as the terrain
pass), `uv = worldXZ * tilesPerMetre[slot]` from the absolute world position (`RenderOrigin` added back, as the
terrain pass does), blends by the weights, multiplies by `tint[slot] * jitter`, then the shared lighting and the
three MRTs. One UBO holds everything per material set: the frame block the model path already shares, then
`vec4 TintTiling[MaxMaterials]` (tint rgb, tiles per metre) plus a misc vector, `MaxMaterials = 64` (1 KB, a
catalog larger than that is split across sets by the caller and is not a 2026 problem). The pipeline is built
beside the splat one in `ModelRenderer.BuildPipelines` (same vertex + instance layouts, a resource layout of
UBO, albedo array, sampler, then the shadow map and its sampler LAST because Metal samples in binding order),
every interpolant the fragment reads sits gap-free from location 0 (the FXC rule the terrain pass learned), and
it is one more entry in the memoised shader set, so the second `Scene3D` in a process compiles nothing new.
The ground CASTS shadows, as it does today through the model path (the splat terrain is the one that opts
out), so the shadow-caster loop treats a tile-ground mesh like a model mesh, and the rebaked goldens move only
for the texturing.

**The material set: `Scene3D.LoadTileGroundMaterial(...) -> TileGroundMaterialHandle`,
`LoadMesh(mesh, TileGroundMaterialHandle)`.** (As shipped the `Scene3D` call takes the size and the layer list
rather than a `TileGroundMaterialSet`, because that type lives in `TileWorld.Render3D`, which sits ABOVE
`Render3D`. The set-taking overload is the `ITileWorldScene` seam member instead, and `Scene3DTileWorldScene`
unpacks it.) A set is N layers of equal size (`width`, `height`, `AlbedoRgba`,
`Tint`, `TilesPerMetre` per layer) plus the sampler config. `TileWorld.Render3D` builds it from the catalog:
`TileGroundMaterials.Build(catalogs, load)` gives every catalog material a slot in id order of the
catalog. A material with `Texture` set is decoded through `ImageRgba` (path resolved RELATIVE TO THE CATALOG FILE
that declared it, through a new public `TileWorldCatalogs.MaterialSource(id)`, the same rule `world.json` uses
for its catalog paths, and a material whose catalog came from memory rather than a file cannot carry a relative
`Texture` at all, a `TileWorldException` names it), and must match the set's size, a mismatch is a
`TileWorldException` naming the material and both sizes, not a silent resample. A material with no
`Texture` gets a flat layer of its catalog `Color` (one fill, same size), so the colour-only world of R1 to R4
renders through the SAME pipeline and the same goldens path, just without texture detail. Textured materials
take a white tint (the texture IS the colour, the catalog `Color` stays what the headless readers and the
untextured flat-layer fallback use). `TilesPerMetre` defaults to 0.5 (a 2 m repeat, which at 1 u = 1 m
puts two tiles per texture repeat and reads as OSRS-scale grain) and is a per-material override in the catalog
(`tilesPerMetre`, optional, the one field the catalog schema gains, which matters because the schema is
`additionalProperties: false`). Loading happens once per view construction or
catalog change, never mid-frame (a mipped upload opens its own command list, #424).

**The mesher: one material per corner, four slots per tile.** `TileGroundMesher.CornerColor` already walks
the up-to-four tiles sharing a corner and reads their underlay ids. R5 keeps that walk and picks the corner's
MATERIAL: the id with the most sharing tiles (void excluded as today, and a `NoDraw` tile COUNTED as today: it
draws no ground of its own, but its underlay is what keeps the ground continuous across the hole it punches, and
a rule that dropped it here while the colour path kept it would step at the edge of every object floor), ties broken by the lower
material id. The tie-break must be the same from every tile that shares the corner or the corner seams, so it
is deterministic and tile-independent by construction, and a change to it moves the goldens, which is wanted. Each tile then carries its
four corner materials as its four slots, every vertex of every triangle in the tile gets its weights over those
slots as described above, and the jitter per vertex is the sharing-tile average. A grass tile next to dirt
therefore blends from grass at its inner corners to dirt at the boundary corners (whichever id wins the 2 vs 2
tie), which is the OSRS soft edge, one tile wide, while a shaped overlay cut stays exact. `TileColors.Blend`
stays for the headless colour readers (`TileGet`, the top-down overlay painter, the flat-layer fill) and for a
caller that opts out of the textured pipeline. The rule is pinned by a test that meshes two regions sharing a
corner and asserts the corner slot and weight agree from every touching triangle.

**The view.** `TileWorldView` asks `ITileWorldScene` for `LoadTileGroundMaterial` once and uploads each
region-plane mesh with the handle. `TileWorldViewOptions.GroundMaterials` is the hook (null = build from the
catalogs with no texture root, i.e. flat layers). The new `ITileWorldScene` members (`LoadTileGroundMaterial`, `LoadMesh` with a material handle,
`DrawWater`) ship as DEFAULT interface implementations (the no-material upload and a no-op), so the two
existing implementers outside the engine's control (Grimhollow's test fake, and any consumer's) keep compiling
and the change stays additive. The fake scene in the tests records the material handle like it records meshes,
so the view stays headless-testable, and the two existing goldens are rebaked on all three families because
the untextured world now draws through the new pipeline. Two new goldens, `tileworld_textured` (a two-material
checker set generated in the test, no image files in the engine repo) and `tileworld_river` (7.6), in test
methods whose names carry `Golden` (the CI filter selects on the name). `TileWorldSnapshot` IS the 3D path, so
its perspective and top-down captures, and therefore `ke-tileedit`'s renders, come back textured. What still
reads the catalog colour is the headless side: `tile_get`, the top-down overlay painter's tints, and the
flat-layer fill for an untextured material.

**Deliberately not done.** Normal maps (section 14). sRGB (the engine has no sRGB formats, everything is
sampled in gamma space and the goldens are baked that way, a colour-space change is fleet-wide and not this
program's). An overlay edge feather (a road blending into grass along its cut edge): the cut is exact by design
(7.1), the feather would need a second palette per overlay triangle, and the shaped edges already break the
hard line. Per-vertex texture rotation (the OSRS trick that hides tiling): noted, cheap later, not now.

### 7.6 Water (R5): the engine's water pass, one plane per water body

Water is the existing `Scene3D.DrawWater(in WaterPlane)` pass with a per-plane `WaterLook`, the pass Ruinborne
uses for its sea and its inland lake. Nothing new is drawn: the depth tint, the shore foam band and the
waterline feather all read the SCENE DEPTH BUFFER (the pass reconstructs the ground under each water pixel from
the resolved depth), so no bathymetry map, no scene-wide setting, no new shader. The tile world's part is
deciding WHERE the planes are:

- **Water tiles are ground.** A `Kind = Water` material meshes like any other underlay (its texture is the
  river BED, mud or stones, and the author sinks the bed by lowering the corner heights, which is the authoring
  model: water is carved, not placed). `Settings.Blocked` on water tiles stays a content decision, as today.
- **`TileWaterPlanes.Collect(doc, catalogs, region, plane, look?) -> IReadOnlyList<WaterPlane>`:** the region-plane's
  water tiles are grouped into 4-connected components, each component gets ONE surface height (the maximum
  corner height over the component's tiles, which is the rim it shares with the bank, minus 2 cm), and the
  component's tile mask is cut into a DISJOINT set of maximal axis-aligned rectangles (row runs merged across
  rows while their span is identical, the usual greedy decomposition, deterministic), one `WaterPlane` per
  rectangle, tile centres converted through `TileWorldSpace` (world z = minus tile z, 7.4). Not one plane per
  tile, because the pass draws a fixed 97x97 grid per plane (18,432 triangles and about 113 KB of vertices
  uploaded per plane per frame), so a straight 3-wide river must be one plane and a bend a few. And NOT one
  bounding box per component: a box over-covers at bends, and water over-cover is only hidden where the ground
  under it is ABOVE the surface (the pass is depth-test-less with depth-write off, and discards only once the
  ground is at or above the surface). A ditch beside a bend, a cave mouth, a sunk road cut, any non-water
  ground below the rim inside the box would render as water, which is the exact failure Ruinborne's inland
  lake was built to avoid (it covers a round basin with inscribed strips for this reason). The rectangles are
  the component's own tiles and nothing else, so nothing that is not water gets a surface. The collector
  asserts pairwise disjointness over every plane a region-plane emits, because two overlapping planes
  double-darken (the blend is depth-write off) and the boundary reads as a crisp step, and it logs once when a
  region-plane emits more than 16 planes, which is the signal that an author drew a river that wants fewer,
  longer runs.
- **A sloped river renders as steps.** One surface height per component. A river that descends is authored as
  separate bodies at each level (a weir or a rapid between them), which is also how OSRS does it. Noted in 14.
- **`TileWaterLooks.River`** is a `static readonly WaterLook` in `TileWorld.Render3D` (a per-plane look, not an
  `OceanPreset`, which mutates scene-wide water settings): procedural waves, zero swell, small normal strength,
  no surf, no foam beyond the shore band, a browner shallow colour and a shorter `ShallowDepth` (0.8 m) than
  the sea, so a 60 to 80 cm bed reads as deep enough to darken. Ruinborne's inland lake look is the reference
  values. `TileWorldViewOptions.WaterLook` overrides it for a world that wants something else. The grid mode
  stays the scene's (camera focused by default, the clipmap mode is scene-wide and camera centred, neither is
  chosen per plane).
- **The view submits every frame.** `TileWorldView.Draw` enqueues the planes of each loaded region-plane through
  a new `ITileWorldScene.DrawWater` seam member. As shipped the planes are collected lazily on the first draw
  after a change and cached against the region-plane's MESH handle rather than at mesh-build time: a remesh always
  returns a fresh handle generation, and every edit that can move a water tile or a corner height is exactly an
  edit that remeshes, so the cache invalidates without the water path being wired into the rebuild. Planes from neighbouring regions are disjoint because a component
  is clipped to its region and region rects are disjoint, and planes within a region are disjoint by the
  decomposition, so the depth-write-off blend never double-darkens. A body that crosses a region border is two
  planes meeting at the border, the same surface height if the banks agree, which the authoring pass keeps
  true (a bank that differs by N cm across the border shows as an N cm step in the surface there, visible on
  purpose rather than hidden).
- **Collision, pathing, raycast, the top-down painter: unchanged.** Water is a ground material with a pass on
  top. `TileRaycast.Pick` still lands on the bed, which is what an editor click wants.

**Deliberately not done.** Flowing water (a scrolling direction per body), river-width-aware foam, a water
level per region or per world (the component rim rule needs no authored number), and sloped surfaces. Each is
a `WaterLook` or collector extension, none moves the format.

## 8. The editor kernel (`KhaozEngine.Editor`) and the tile editor (`KhaozEngine.TileEditor`)

### 8.1 Kernel extraction

Lifted out of `MapEditor` into `KhaozEngine.Editor`, keeping behaviour and tests:

- `EditorHistory` (undo/redo with gesture coalescing), `EditorDocument<TDoc>` (dirty tracking and
  `IEditorCommand` execute/undo/redo, while the terrain-specific `WorldRebuildPending`/`PendingRebuildRegion`
  stay in `MapEditor`'s derived document and the tile editor tracks its own dirty rects), `EditorSelection`,
  `EditorRecentFiles` + `IRecentFilesStore`, `EditorSettings` + `IEditorSettingsStore` + the settings dialog,
  the landing scene and its options (renamed `EditorLandingScene` / `EditorLandingOptions`: the `Map` prefix
  would lie in a kernel), `GizmoGeometry`/`GizmoDrag`, `EditorEnvironment`, and the fly camera if it lifts
  cleanly out of `MapEditorScene` (decided at plan time by reading it).
- One genuinely new seam: `IEditorTool { Name, Icon, Activate, Deactivate, OnPointer(frame), DrawOverlay }`
  and an `EditorToolbar` built from a registered `IReadOnlyList<IEditorTool>`, so the tile editor does not
  repeat `MapEditor`'s closed `EditorToolMode` enum. `MapEditor` is NOT migrated onto `IEditorTool` in this
  program (that is a refactor of a 3276-line scene for no functional gain), the seam is for new editors.
- `MapEditor` keeps forwarding types under the old names (`MapEditorLandingScene : EditorLandingScene`,
  `MapEditorLandingOptions : EditorLandingOptions`, and any other lifted public type Ruinborne's head
  touches), doc-commented as source-compatibility aliases, so Ruinborne's repin is not forced. They are
  deliberately NOT `[Obsolete]`: warnings are errors fleet-wide, so an obsolete alias would fail
  `Ruinborne.Editor`'s build on repin exactly as the rename would, and the alias would buy nothing.
  Additive: a minor bump. The aliases drop at the next major, and Ruinborne's 208-line head moves to the
  kernel names then (or earlier, at its convenience).

### 8.2 The tile editor

**Shipped ahead of this section: the commands.** R3 landed the whole mutation half of what this section
describes as `KhaozEngine.TileWorld.Editing`, so `TileWorldCommands` below is not a type to write, it is
`SetTilesCommand`, `SetCornerHeightsCommand`, `PlaceObjectCommand`, `MoveObjectCommand`, `RotateObjectCommand`,
`RemoveObjectCommand`, `SetObjectTagsCommand`, `SetMarkerCommand`, `RemoveMarkerCommand`,
`CreateRegionCommand`, `DeleteRegionCommand`, `CompositeCommand` and `SnapshotRectCommand`, driven through
`TileEditingDocument` (which also owns the history, the saved marker, the derived collision rebake and the
dirty rects a remesh consumes) with `TileEditOps` as the brush and batch factories. What R5 still owes is the
frontend: `TileEditorScene`, the tools below, the overlays and the palettes, hosted over those commands. Brush
drags coalesce through `ITileCommand.TryMerge`, which the MCP tool deliberately does not use (it seals after
every verb), so gesture coalescing is a GUI concern that already has its mechanism waiting.

`TileEditorScene : GameScene` + `TileEditorOptions { WorldPath, Catalogs (resolved from the manifest by the
head, overridable), SettingsStore, RenderDistanceRegions }`.

Tools v1, each an `IEditorTool`:

- **Select**: click a tile or an object, properties panel (tile layers + decoded collision, or object
  archetype/rotation/tags with move/rotate/delete).
- **Paint Underlay**: brush radius, material palette.
- **Paint Overlay**: material + shape + rotation.
- **Height**: raise/lower/flatten/smooth on lattice corners, brush radius and strength.
- **Flags**: toggle `Settings` bits on tiles under the brush.
- **Object Place**: archetype palette, R rotates, footprint ghost, click places.
- **Prefab**: marquee a rect, save as prefab (name prompt), or pick a prefab and stamp with rotation.
- **Plane switch** (PgUp/PgDn) and **Region Create** (click an empty region slot in the grid overlay).

Overlays: the tile grid on the active plane, region borders, selection, footprint ghost, and a collision
debug toggle (edges red, blocked tinted, corners marked). Every mutation is an `IEditorCommand` from
`TileWorldCommands`, brush drags coalesce, each command's dirty rect triggers `Rebake` + region-plane
remesh, Ctrl+S saves. Deliberately absent (AI-first): auto-tiling road brushes, heightmap import (MCP verb
only), multi-object marquee. Filed as follow-ups if wanted, not built.

`Grimhollow.Editor` is Ruinborne's head cloned: `EditorApp : GameApp3D`, discovers worlds under
`assets/worlds/`, resolves catalogs from the manifest, opens `TileEditorScene`.

## 9. The MCP tool (`KhaozEngine.TileEdit.Tool`, `ke-tileedit`)

Same skeleton as `ke-mapedit`: stdio host, `TileEditSession` (one locked open world), `QueryService`,
`MutationService`, `RenderService`, attribute-registered verbs behind the guard, structured JSON
results, exceptions turned into structured errors. Only the session carries the `Tile` prefix, because only
the session is a name the engine has more than one of. Two seams:

- **One command set for both frontends.** Mutation verbs execute an `ITileCommand` through the session's
  `TileEditingDocument`, so `undo(steps)` and `redo(steps)` exist in MCP and an AI edit and a human edit are
  byte-identical mutations. Shipped as written, with the command layer in `TileWorld.Editing` rather than in
  `TileEditor` (section 4).
- **The world is self-describing** (`CatalogPaths`), so `world_open(path)` is one argument. Those catalog
  paths, and every other path argument any verb takes, resolve against the WORLD directory, never the process
  working directory: an MCP server is launched by a client whose working directory is its own business, so a
  world resolved against it would be a world that only loaded for one client. The two verbs that OPEN a world
  are the exception, having no world to be relative to yet, and want an absolute path.
- **One verb is one undo step.** The session seals the gesture after every command, so the command layer's
  drag coalescing never fires over MCP, where each call is a discrete instruction rather than one sample of a
  held mouse button. The R5 GUI drives `TileEditingDocument` directly and keeps the coalescing.
- **Rows on the wire run north first.** Every ASCII map and every height row set has row 0 as the HIGHEST z of
  the rect, each row west to east, so `height_get_rect` hands its rows straight to `height_set` without
  flipping the terrain.

Verb families:

| Family | Verbs |
|---|---|
| World | `world_open`, `world_create(path, id, displayName, catalogPaths, planeCount, tileSize)`, `world_save`, `world_summary`, `world_validate`, `catalog_list(kind)`, `region_create`, `region_delete`, `region_list`, `undo`, `redo` |
| Tiles | `tile_get(x, z, plane)` (all layers + decoded collision), `tile_set`, `tiles_fill(rect, plane, underlay?, overlay?, shape?, rotation?, settings?)`, `tiles_get_rect(rect, plane, layer)` (one char per tile) |
| Heights | `height_set`, `height_raise(rect, plane, delta, falloff?)`, `height_flatten(rect, plane, to?)`, `height_smooth(rect, plane, iterations)`, `height_get_rect`, `height_import(pgmPath, rect, plane, minCm, maxCm)` |
| Objects | `object_place`, `object_move`, `object_rotate`, `object_remove`, `object_set_tags`, `object_get`, `objects_in_rect`, `object_find(archetype?, tag?)`, `objects_line(archetype, from, to, plane)`, `objects_scatter(archetype, rect, plane, spacing, jitter, seed)` |
| Markers | `marker_set`, `marker_remove`, `marker_list` |
| Prefabs | `prefab_save(rect, planeFrom, planeCount, savePath, includeObjects?, includeMarkers?)`, `prefab_place(prefabPath, x, z, plane, rotation)`, `prefab_list(directory)` |
| Collision | `collision_at`, `is_walkable`, `path(from, to, plane, agentSize, maxRadius)`, `walkable_rect(rect, plane)` (ASCII map) |
| Render | `render_topdown(rect, plane, pxPerTile, overlays, savePath?)`, `render_view(eye, target, size, observer?, savePath?)` |

`tiles_get_rect` and `walkable_rect` exist so the AI can read an area for a few hundred tokens instead of
thousands. Grimhollow registers the tool in its `.mcp.json` the way Ruinborne registers `ke-mapedit`.

**What shipped against this table: its 43 verbs, with four argument shapes corrected.** The corrections, each
because the spec's shape did not survive contact:

- **`render_*` return the image INLINE, not a path.** Each hands back two content blocks, a text block naming
  the framing (rect, plane, scale and overlays for the top-down, eye, target, size and roof observer for the
  view) and then the PNG itself, with the text first so a client can map image pixels back to tiles before it
  looks at them. `savePath` became an OPTIONAL extra rather than the delivery mechanism: given, it also writes
  the file and joins the saved path to the framing line. `render_view` also took an `observer` pair, so a shot
  aimed inside a building can hide that building's roof.
- **`height_import` reads a binary PGM (netpbm P5, 8 or 16 bit), not a PNG.** A PGM is a header of ASCII
  decimals followed by raw big-endian samples, so the reader is `PgmReader` in `TileWorld.Editing`. A PNG needs
  a deflate decoder that no engine package ships, and this program was not the place to add one, so the verb
  refuses PNG by name and says to convert first. If a PNG path is ever wanted, that is an engine-wide image
  decode question rather than a tile-world one.
- **The prefab verbs take PATHS, not names.** There is no prefab registry, so `prefab_save` extracts a rect to
  a file (its name without the extension becoming the prefab's name) and `prefab_place` and `prefab_list` take
  a file and a directory. `prefab_save` is the one write verb here that changes nothing about the world, so it
  is NOT an undo step (deleting the file is the undo), while every other mutating verb is exactly one.
- **`tile_set` is exactly a 1x1 `tiles_fill`**, kept as its own verb because a single tile is the common case
  and not because it does anything else, and clearing a rect is a `tiles_fill` with underlay 0, overlay 0,
  shape `Full`, rotation 0 and settings `none` rather than a verb of its own.

## 10. Grimhollow bootstrap and starter content

Repo `APKiwiOrg/Grimhollow` at `~/Grimhollow` via the `scaffold-game-repo` skill. Sub-project 1 keeps the
game side minimal: `Grimhollow.Editor`, `Grimhollow.Tests`, `assets/`, `.mcp.json`, `tools/kitgen/`. Client,
server, auth and persistence come in sub-project 2 by cloning Ruinborne's shell onto the tile world.

- `assets/catalogs/ground.json`: three grasses, dirt, mud, sand, gravel, cobble, road, wood floor, stone
  floor, snow, water.
- `assets/catalogs/archetypes.json` + `assets/models/kit/*.glb`: about 25 greybox pieces: wall, wall corner,
  wall with window, doorway (a `Wall`-shaped piece with `CollisionKind.None`), fence, gate, three trees, bush,
  two rocks (1x1, 2x2), flat and gable roof pieces + ridge (`IsRoof`), stairs, well, crate, barrel, table,
  chair, bank booth, anvil, furnace, altar, signpost, lamp post, ladder.
- Meshes are generated, not modelled: `tools/kitgen/` is a Blender Python script (driven through the Blender
  MCP) that builds each piece from primitives at 1 u = 1 m with exact footprints and exports glb. Each piece is
  authored to the resolver's local-space contract: the ORIGIN sits at the footprint CENTRE on the piece's own
  floor (not at a corner), x east, minus z north, with a wall on the `-x` face at rotation 0. Palette colours
  ride either as one material per shade or as per-vertex `COLOR_0`, both of which `GltfLoader` reads, so a piece
  needs no textures. The kit is regenerable code, and the archetype indirection means the later art pass swaps
  meshes per id without touching a world file.
- The world: `assets/worlds/hollowmere/` (working name, the directory name is the world `Id`), about 3x3
  regions, authored through `ke-tileedit`: a starter town
  (bank, general store, chapel with altar, smithy, well, houses as stamped prefabs), roads with shaped
  edges, a river crossed by plain bridge tiles, farm fields, a forest edge via `objects_scatter`, a hill
  with real height, a cave entrance stub, spawn and bank markers.
- Proof: `Grimhollow.Tests` loads the shipped world, validates it against the catalogs, asserts a stable
  `WorldHash`, bakes collision, and finds a path from the spawn marker to the bank marker. Plus rendered PNGs
  and the user's fly-through in `Grimhollow.Editor`.

**How R4 actually authored Hollowmere, and the correction.** R4 shipped the world out of `Grimhollow.WorldGen`,
a C# program of hand-chosen placements (every building, road, marker at a coordinate a person picked) driven
through the `TileWorld.Editing` commands, plus `objects_scatter` for the forest edge and a hash function for the
grass tints. It was the fastest route to a first world from a session that could not reach the MCP tool, and it
is not the authoring model: the map is hand authored, like OSRS and like Ruinborne's, and AI-first means through
`ke-tileedit` verbs in a session with the tool registered, not through a generator. From R5's Grimhollow round
on, Hollowmere is edited by hand through `ke-tileedit` (the river bed and banks, road shoulders, crop rows, the
tints that follow slope and water rather than a hash), `Grimhollow.WorldGen` is retired once that pass lands
(its prefab builders survive as prefab FILES under `assets/prefabs`, which the tool stamps), and the test that
regenerates the world and compares hashes goes with it. What stays as proof: validate against the catalogs,
markers present, sealed building shells (the door-blocked negative walk), the spawn-to-bank path, and a pinned
hash of the committed world that a content change updates deliberately.

## 11. Failure handling

- Load: schema errors name region and JSON path, a region hash mismatch names the region and refuses, a
  missing catalog names the manifest line, a missing neighbour region is not an error (edge-extend).
- MCP: the guard turns exceptions into structured errors, a rect touching an uncreated region names it and
  hints `region_create`, an object whose footprint leaves every loaded region is refused.
- Editor: a failed save surfaces in the status bar and never loses the document (tmp + rename).
- Renderer: a missing mesh ref draws a placeholder box and logs once per archetype, never faults the view.

## 12. Test plan

Headless, in per-area test projects, per the repo rule.

- `KhaozEngine.TileWorld.Tests`: save/load round trip byte-identical, `WorldHash` independent of region write
  order and of the in-memory insertion order, migrations, validator (dangling ids, plane out of range,
  footprint off every region), lattice edge semantics (neighbour read vs edge-extend), the collision baker as
  a table (one row per rule in 6.1), cross-region wall mirroring, `CanStep` including no-corner-cutting and
  NxN agents, pathfinder determinism (same inputs, same tiles, both heads' order), nearest-reachable, raycast
  against a known lattice, prefab extract/place round trip under all four rotations, torn-write refusal.
- `KhaozEngine.TileWorld.Render3D`: mesher geometry (triangle counts per shape, the diagonal rule, corner blend,
  zero seams: adjacent regions produce identical shared-corner positions), the prop yaw and anchor conventions,
  view bookkeeping (dirty coalescing, the flush budget, the roof rule, handle lifetime), residency ring, and the
  `GpuFact` goldens, baked on all backends through the cross-platform bake before they can gate.
  **These live in `KhaozEngine.Render.Tests/TileWorld/` (namespace `KhaozEngine.Tests.TileWorld`), not in a
  separate `KhaozEngine.TileWorld.Render3D.Tests` project as written above.** That is the repo norm rather than an
  exception: `Terrain.Render3D`'s tests live there too, and `GoldenCompare`, the golden comparer every image
  regression goes through, is internal to that assembly. Splitting a project out would mean either duplicating the
  comparer or making it public, both worse than one extra `ProjectReference`. The CPU tests live there with the
  goldens rather than being split across two homes, so the package's whole suite runs from one filter
  (`--filter "FullyQualifiedName~KhaozEngine.Tests.TileWorld"`). The GPU half needs `KE_GPU_TESTS=1` and is
  silently skipped without it, so a plain run proving 0 failed is not evidence the goldens passed: 0 SKIPPED in
  that namespace is.
- `KhaozEngine.TileWorld.Tests/TileWorld/Editing/`: the command layer, shipped in R3. Every command's undo
  restores a byte-identical document (compared by `TileWorldHash`) APART FROM four limits, which
  `KhaozEngine.TileWorld.Editing/README.md` states in full as the canonical list and this suite pins as tests
  rather than as prose: an object whose ANCHOR falls outside a `SnapshotRectCommand`'s rect, a region created
  or deleted inside the mutate, `PlacePrefab`'s redo taking fresh object ids, and a `SetCornerHeightsCommand`
  above plane 0 materialising the derived lattice. Alongside that: capture-once semantics across
  execute/undo/redo, the dirty rects each command reports, history coalescing and the merge barrier, the
  editing document's collision upkeep and plane rejection, `IsDirty` against the saved marker, the
  `TileEditOps` brushes and batches (falloff rings, blur convergence, scatter determinism), and `PgmReader`'s
  header rules including the one-whitespace delimiter. It lives beside the document's own tests rather than in a
  `KhaozEngine.TileWorld.Editing.Tests` project, the same repo norm the renderer's tests follow.
- `KhaozEngine.TileEdit.Tests`: the tool, shipped in R3. Session lifecycle (open, create, save, no-world
  errors, path resolution against the world directory), each verb family through its service, the overlay
  painter, the render service, and the verbs at the WIRE through the same `McpBootstrap` composition the stdio
  host uses, so a registration the host would expose and the tests would not cannot exist. The render tests
  need a GPU and skip without one.
- `KhaozEngine.Editor.Tests`: history coalescing, document dirty/undo, tool registry. `MapEditor`'s existing
  tests stay untouched and pass through the shims. R5.
- `KhaozEngine.TileEditor.Tests`: the GUI half, R5. The command-undo proof it was written for has already
  landed in the command layer's own tests above, so what is left here is tool behaviour and brush coalescing.
- Grimhollow: the world proof in section 10.

## 13. Release split

Engine work in worktree `feature/tile-world`, rounds under the riding rules, each merged to `main`,
pushed and packed to `local-feed`, no tags unless the user says so:

- **R1**: `TileWorld` (document, file form, catalogs, validator, migrations, collision map + baker,
  `CanStep`, pathfinder, raycast, prefab) + tests. Shipped.
- **R2**: `TileWorld.Render3D` alone (mesher, props, scene seam, view, residency, snapshot, two goldens) + tests.
  Shipped, riding the in-flight version rather than taking one of its own. That version is the MINOR `17.37.0`,
  re-tiered on `main` while the round was in flight, not the patch it was when the round started.
- **R3**: `TileWorld.Editing` (the command layer, GPU-free, in `Foundation`) + `TileEdit.Tool` (`ke-tileedit`,
  43 verbs) + tests. Shipped, riding the same in-flight version as R2.
- **R4**: the Grimhollow bootstrap, section 10: the engine pin, `ke-tileedit` registered in the repo, the
  catalogs, the 3x3 starter world, and a client that opens that world through `TileWorld.Render3D` under the
  fly camera. Shipped as Grimhollow 0.2.0, with `GreyboxMeshResolver` boxes standing in for the Blender kit
  (the kit and a glb resolver are Grimhollow#4, their own small round). The round also found and fixed the
  greybox roof lift (engine 17.38.0) and filed two engine fit-failure pairs (#658 prefab extract and the
  derived plane lift, #659 no marker index in the manifest).
- **R5**: ground materials (7.5) and water (7.6) in `Render3D` + `TileWorld.Render3D`, two new goldens and the
  two existing ones rebaked. Shipped, riding the in-flight version, which was re-tiered from the patch it was to
  the MINOR `17.38.0` while the round was in flight, because the round is additive API (a new pipeline, new
  members with defaults, one new schema field). The same re-tier R2 took. Deferred minors are
  [#665](https://github.com/APKiwiOrg/KhaozEngine/issues/665). The Grimhollow side is its own round in that repo:
  CC0 textures per catalog material with credits, the hand-authored terrain pass through `ke-tileedit`, `WorldGen`
  retired (section 10).
- **R6**: the `Editor` kernel extraction with the forwarding aliases, then the `TileEditor` GUI over the
  commands R3 already shipped. Pending.

**Delivery-order change 1, decided at R2 plan time and confirmed by the round.** The `Editor` kernel extraction
(section 8.1) was planned for R2 and moved to R3. It has NO consumer until `TileEditor` exists, so doing it in R2
would have shipped forwarding aliases nothing calls and frozen a kernel shape before the editor that has to live
in it, which is exactly the ordering that produces an abstraction fitted to a guess. The renderer, by contrast,
has two consumers waiting the moment it lands (the goldens, and the editor viewport and MCP render verbs), so R2
is the renderer alone.

**Delivery-order change 2, decided at R3 plan time: the kernel and the GUI moved again, to R5, and R4 became
the Grimhollow bootstrap.** The same has-a-consumer test that moved the kernel out of R2 moves it out of R3. Once
the command layer is its own GPU-free package (section 4), the MCP tool needs neither the kernel nor the GUI to
ship, and the GUI editor has no consumer until a human wants to author a world BY HAND. The MCP tool is the
AI-first authoring path this program was specified around, so that moment is later than it looked when the
rounds were written. What does have a consumer now is the Grimhollow side of section 10: everything the client
needs to open and draw an authored world exists after R3, and a world nobody has flown through is a world nobody
can judge. So R4 takes the bootstrap and R5 takes the editor, in the order the consumers actually arrive.
Nothing about either piece of work changed, only when it happens. The design's own load-bearing claim survives
untouched, because the editor round wraps the commands R3 shipped rather than a second set.

**Delivery-order change 3, decided after the R4 fly-through: ground materials and water go before the editor.**
The fly-through is the first time a person judged the world, and the judgement was about the ground (flat
colour blobs, a blue strip for a river), not about tooling. The GUI editor still has no consumer, the textured
ground has one standing in front of it. So R5 is 7.5 and 7.6 and the editor moves to R6. Sub-project 1's scope
did not move either: R4 ends at the fly camera, and tick-based click-to-walk stays with the next sub-project
and its own spec (section 14).

## 14. Deferred, with the reason

Each of these is filed as an issue when its round lands, not carried here.

- Tick-based click-to-walk movement: not part of this sub-project at all. R4 (section 13) ends at the fly
  camera over the authored world, and walking lands in the next sub-project of Grimhollow with its own spec,
  alongside the server shell, auth and persistence. The prose here says "the next sub-project" rather than a
  number on purpose: the orientation list at the top of this doc splits the shell and the walking into (2) and
  (3), and the round notes elsewhere in the fleet treat them as one, so the number is the part nobody has
  settled and the ordering is the part everybody agrees on.
- Textured ground materials and water: deferred from v1, now R5 (7.5 and 7.6). What R5 in turn leaves out:
  normal maps for ground materials (a second array, four more samples), an overlay edge feather, per-vertex
  texture rotation against tiling, flowing water and sloped river surfaces (one surface height per water body
  in R5, a descending river is authored as bodies with a drop between them). R5 shipped, so all five are filed,
  alongside the round's own deferred minors, as
  [#665](https://github.com/APKiwiOrg/KhaozEngine/issues/665).
- The over/under bridge plane trick: `Settings.Bridge` is reserved, semantics undefined until a bridge is
  authored that needs it.
- Auto-tiling road brushes and multi-object marquee in the GUI: AI-first, the MCP verbs cover the need.
- `IPathPlanner` adapter over `TilePathfinder`: 30 lines when NPC AI wants it.
- Sub-chunking the region-plane mesh: not until an edit-rate measurement says the whole-region rebuild is
  too slow.
- Migrating `MapEditor` onto `IEditorTool`: a refactor of a 3276-line scene for no functional gain.
- Lifting `PropLayer`/`PropRenderer` from `Terrain.Render3D` to `Render3D`: would remove the renderer's
  `Terrain.Render3D` edge, not this program's job.
