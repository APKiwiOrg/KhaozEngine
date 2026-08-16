# OSRS-style tile world: document, collision, renderer, editor kernel, tile editor and MCP tool (2026-08-15)

Status: R1 shipped (document, file form, catalogs, validator, collision, pathfinder, raycast, prefabs), R2 and
R3 pending. Program issue:
[#629](https://github.com/APKiwiOrg/KhaozEngine/issues/629). First adopter: Grimhollow, a new low-poly 3D
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
| `KhaozEngine.Editor` | none (dev tooling, like MapEditor) | Gui, Game.Render3D, Render3D | The extracted editor kernel |
| `KhaozEngine.TileEditor` | none | Editor, TileWorld, TileWorld.Render3D | `TileEditorScene`, tools, `TileWorldCommands` |
| `KhaozEngine.TileEdit.Tool` | none (dotnet tool, `ke-tileedit`) | TileEditor, ModelContextProtocol | The MCP tool |

`Terrain.Render3D` on the renderer is for `PropLayer`/`PropRenderer`, which live there today. If that prop
path is ever lifted to `Render3D` proper the edge disappears, but lifting it is not this program's job.
`Editor`'s exact dependency set is fixed at plan time from what the lifted types actually reference (the
landing scene and settings dialog need `Gui`, the gizmos and environment need `Render3D`).

Whether `Editor` and the two `Tile*` editor packages join an umbrella: no. `MapEditor` is not in one either.
They are dev tooling referenced by editor heads explicitly.

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
`TryGetTile`, `SetHeight`, `SetUnderlay`, ...). It has no undo of its own: undo is the editor kernel's job
(section 8), and both frontends mutate through `TileWorldCommands`. The document raises no events. Instead
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
| 9 | `ProjectileBlocked` | reserved for ranged line of sight (sub-project 3), never set in this program |
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
is the one primitive the tick movement (sub-project 3) and the pathfinder share.

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
  sub-project 3's click-to-walk needs it server-free.
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
- v1 is colour-only. UVs are written tile-local (0..1) so a texture path (per-material submesh with an atlas)
  can land later without touching the mesher's logic. `Kind = Water` renders as flat colour in v1 and is
  reserved for a shader pass.

### 7.2 Objects

`TileObjectProps.Build(document, catalogs, region) -> PropPlacement[]`, fed to the existing
`PropLayer.PlacementLayer` and drawn by `PropRenderer` (LOD, HLOD, distance dissolve, instancing already
there). Position = the rotated footprint centre at the bilinear lattice height, yaw = `Rotation * 90 +
YawOffsetDegrees`, mesh from the archetype's glTF ref through the existing model loader. One prop batch per
region so an unload is a drop. A missing mesh ref draws a placeholder box and logs once per archetype: a
bad ref never faults the view.

### 7.3 Runtime

`TileWorldView` owns loaded region meshes and prop batches in a `Scene3D`, driven by `TileRegionResidency`
(a Chebyshev ring of regions around an anchor, the `MapTileResidency` shape, radius default 1 for the editor
and configurable for the client). Plane rules: all planes are drawn, and `IsRoof` objects on planes above the
observer are hidden while the observer's tile carries `Indoors`, which is OSRS's global roof-hide. Headless
PNG for MCP goes through the same `Render3DSnapshot` path `ke-mapedit` uses, with `TileWorldView` in place
of `ViewportWorld`.

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

Same skeleton as `ke-mapedit`: stdio host, `TileEditSession` (one locked open world), `TileQueryService`,
`TileMutationService`, `TileRenderService`, attribute-registered verbs behind the guard, structured JSON
results, exceptions turned into structured errors. Two seams:

- **One command set for both frontends.** Mutation verbs execute `TileWorldCommands` through an
  `EditorDocument<TileWorldDocument>` in the session, so `undo(steps)` and `redo` exist in MCP and an AI edit
  and a human edit are byte-identical mutations.
- **The world is self-describing** (`CatalogPaths`), so `world_open(path)` is one argument.

Verb families:

| Family | Verbs |
|---|---|
| World | `world_open`, `world_create(path, id, name, planeCount, tileSize, catalogPaths)`, `world_save`, `world_summary`, `world_validate`, `catalog_list(kind)`, `region_create`, `region_delete`, `region_list`, `undo`, `redo` |
| Tiles | `tile_get(x, z, plane)` (all layers + decoded collision), `tile_set`, `tiles_fill(rect, plane, underlay?, overlay?, shape?, rotation?, settings?)`, `tiles_get_rect(rect, plane, layer)` (one char per tile) |
| Heights | `height_set`, `height_raise(rect, plane, delta, falloff?)`, `height_flatten(rect, plane, to?)`, `height_smooth(rect, plane, iterations)`, `height_get_rect`, `height_import(png, rect, plane, min, max)` |
| Objects | `object_place`, `object_move`, `object_rotate`, `object_remove`, `object_get`, `objects_in_rect`, `object_find(archetype?, tag?)`, `objects_line(archetype, from, to, plane)`, `objects_scatter(archetype, rect, plane, spacing, jitter, seed)` |
| Markers | `marker_set`, `marker_remove`, `marker_list` |
| Prefabs | `prefab_save(name, rect, planes)`, `prefab_place(name, x, z, plane, rotation)`, `prefab_list` |
| Collision | `collision_at`, `is_walkable`, `path(from, to, plane, agentSize)`, `walkable_rect(rect, plane)` (ASCII map) |
| Render | `render_topdown(rect, plane, png, pxPerTile, overlays)`, `render_view(eye, target, png, size)` |

`tiles_get_rect` and `walkable_rect` exist so the AI can read an area for a few hundred tokens instead of
thousands. `render_*` return the PNG path (read inline). Grimhollow registers the tool in its `.mcp.json`
the way Ruinborne registers `ke-mapedit`.

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
  MCP) that builds each piece from primitives with palette vertex colours at 1 u = 1 m with exact footprints
  and exports glb. The kit is regenerable code, and the archetype indirection means the later art pass swaps
  meshes per id without touching a world file.
- The world: `assets/worlds/hollowmere/` (working name, the directory name is the world `Id`), about 3x3
  regions, authored through `ke-tileedit`: a starter town
  (bank, general store, chapel with altar, smithy, well, houses as stamped prefabs), roads with shaped
  edges, a river crossed by plain bridge tiles, farm fields, a forest edge via `objects_scatter`, a hill
  with real height, a cave entrance stub, spawn and bank markers.
- Proof: `Grimhollow.Tests` loads the shipped world, validates it against the catalogs, asserts a stable
  `WorldHash`, bakes collision, and finds a path from the spawn marker to the bank marker. Plus rendered PNGs
  and the user's fly-through in `Grimhollow.Editor`.

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
- `KhaozEngine.TileWorld.Render3D.Tests`: mesher geometry (triangle counts per shape, the diagonal rule,
  corner blend, zero seams: adjacent regions produce identical shared-corner positions), residency ring, and
  one `GpuFact` golden of a tiny world, baked on all backends through the cross-platform bake before it can
  gate.
- `KhaozEngine.Editor.Tests`: history coalescing, document dirty/undo, tool registry. `MapEditor`'s existing
  tests stay untouched and pass through the shims.
- `KhaozEngine.TileEditor.Tests`: every command's undo restores a byte-identical document, brush coalescing.
- `KhaozEngine.TileEdit.Tool.Tests`: session lifecycle, each verb family through its service, structured
  errors for the failure cases in section 11.
- Grimhollow: the world proof in section 10.

## 13. Release split

Engine work in worktree `feature/tile-world`, three rounds under the riding rules, each merged to `main`,
pushed and packed to `local-feed`, no tags unless the user says so:

- **R1**: `TileWorld` (document, file form, catalogs, validator, migrations, collision map + baker,
  `CanStep`, pathfinder, raycast, prefab) + tests. Minor bump.
- **R2**: `Editor` kernel extraction with the forwarding aliases, `TileWorld.Render3D` (mesher, props,
  view, residency, golden). Minor bump.
- **R3**: `TileEditor` + `TileEdit.Tool` + tests. Minor bump.

Then Grimhollow: scaffold, editor head, kit generation, catalogs, the authored world.

## 14. Deferred, with the reason

Each of these is filed as an issue when its round lands, not carried here.

- Tick-based click-to-walk movement: sub-project 2 of Grimhollow, its own spec.
- Textured ground materials and a water shader: v1 is vertex colour only. UVs and `Kind = Water` are
  reserved so the format does not move.
- The over/under bridge plane trick: `Settings.Bridge` is reserved, semantics undefined until a bridge is
  authored that needs it.
- Auto-tiling road brushes and multi-object marquee in the GUI: AI-first, the MCP verbs cover the need.
- `IPathPlanner` adapter over `TilePathfinder`: 30 lines when NPC AI wants it.
- Sub-chunking the region-plane mesh: not until an edit-rate measurement says the whole-region rebuild is
  too slow.
- Migrating `MapEditor` onto `IEditorTool`: a refactor of a 3276-line scene for no functional gain.
- Lifting `PropLayer`/`PropRenderer` from `Terrain.Render3D` to `Render3D`: would remove the renderer's
  `Terrain.Render3D` edge, not this program's job.
