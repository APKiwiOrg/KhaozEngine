# KhaozEngine.TileWorld.Render3D

The render arm of [KhaozEngine.TileWorld](../KhaozEngine.TileWorld): meshes a tile world's ground into
vertex-coloured `Render3D` meshes, places its objects through the `Terrain.Render3D` prop path, and owns the
per-region scene handles, region streaming and headless snapshot capture on top. Kept separate from the
render-free document so a server or tool never drags in `Render3D`. In the `Game3D` umbrella. Design:
[docs/design/TILE-WORLD-DESIGN-2026-08-15.md](../docs/design/TILE-WORLD-DESIGN-2026-08-15.md).

## World z is minus tile z

The document's convention is x east, z NORTH, y up. The engine renders right handed with y up, where a camera
facing +z has +x on its LEFT, so mapping north straight onto +world z draws the world mirrored against a compass.
`TileWorldSpace` (in `KhaozEngine.TileWorld`) is the one seam that negates it: **world z is minus tile z**, so
north is -z, which is also a right-handed camera's default forward, and (east, north, up) = (+x, -z, +y) stays a
right-handed triple. That is what lets one top-down view have north UP and east RIGHT at once instead of trading
one for the other. Every conversion here goes through `TileWorldSpace.WorldX/WorldZ/ToWorld`, and two consequences
fall out: a region-local ground mesh runs from 0 to MINUS 64 tiles on z, and an object's yaw is NEGATIVE per
quarter turn.

## Ground (`TileGroundMesher`, `TileColors`)

`TileGroundMesher.Build(doc, catalogs, region, plane, options)` returns the `GltfMesh` of one region-plane, or
null when the region-plane has no drawable tile (underlay 0, or `TileSettings.NoDraw`). The mesh is REGION LOCAL,
so draw it at `TileGroundMesher.WorldMatrix(doc, region)`, a pure translation to the region's lowest tile corner
with Y left at 0 because the vertices already carry absolute corner heights. Vertices are the existing
`ModelVertex` through the existing lit model path: no new material and no new shader.

- **Colour is the OSRS look.** A corner's underlay colour is the average of the up-to-four tiles sharing it
  (`TileGroundMesher.CornerColor`), each multiplied by a deterministic per-tile brightness jitter hashed from the
  world tile coordinate (`TileColors.Jitter`, plus or minus 4 percent by default, `JitterAmplitude` 0 disables
  it). Void tiles are excluded and all-void blends to `TileColors.Void`. A `NoDraw` tile draws no ground of its
  own but still CONTRIBUTES its underlay, so ground stays continuous across a hole punched for an object floor.
  `TileColors.Parse` reads `#rrggbb` or `#rrggbbaa`, and a material id the catalogs do not define renders as
  `TileGroundMesher.MissingMaterialColor` (magenta), so a dangling id is visible rather than invisible.
- **Overlays are exact geometry, not an approximation.** The tile is cut by the shared
  `TileTriangulation.Triangulate` (two triangles for a plain tile or a diagonal half, four for a corner cut) and
  each triangle is painted with the flat overlay colour or left to the blended underlay. The raycast in
  `KhaozEngine.TileWorld` calls the same function with the same inputs, so a click lands on the triangle that was
  drawn. A shape with no overlay material meshes as the plain pair. Overlay alpha is forced to 1, so an authored
  `#rrggbbaa` cannot make the ground translucent.
- **Seamless by construction.** Normals come from the GLOBAL height lattice by central differences
  (`TileGroundMesher.CornerNormal`), which reads ACROSS region borders, so two regions meeting at a corner compute
  the identical normal and a border has neither a crack nor a lighting step. Set `SmoothNormals = false` for one
  flat normal per triangle instead. Vertices are never shared between triangles, because two triangles of one tile
  can carry different colours, so the mesher emits per-triangle vertices.

## Objects (`TileObjectProps`, `ITileMeshResolver`, `GreyboxMeshResolver`)

`TileObjectProps.Build(doc, catalogs, region, plane)` turns a region-plane's `TileObject`s into
`TileRegionProps(Ground, Roofs)`, two `PropPlacement` lists for the existing prop path, split so the roof rule can
hide one and keep the other. Objects on another plane, and objects whose archetype the catalogs do not define, are
skipped rather than thrown on, because content outlives a catalog edit. `AnchorPosition` puts a placement at the
centre of the ROTATED footprint at the document's ground height there, so a mesh is authored centred on its own
footprint with its base at y 0. `YawRadians` is NEGATIVE per quarter turn
(`-(rotation * 90 + YawOffsetDegrees)` in radians). That sign is what makes `Matrix4x4.CreateRotationY` turn
CLOCKWISE seen from above with north up, the tile-world rotation convention (0 west, 1 north, 2 east, 3 south),
and the archetype's yaw offset folds in under the same sign.

`ITileMeshResolver.Resolve(archetype)` is where a game hands over its own meshes, keyed off the archetype's mesh
reference. Returning null means "no mesh for this archetype", which the view answers with a placeholder box and
one log line rather than a throw. `GreyboxMeshResolver` is the shipped stand-in: one procedural vertex-coloured
box per archetype, sized from the footprint and shaped by the collision kind (a wall hugs the west edge, a corner
wall adds the north edge, a diagonal stands a post, a roof slab hangs above wall height), coloured from a
deterministic grey/brown/green palette by archetype id. Only the footprint extent scales with the tile size: every
thickness and height is absolute metres. It caches per archetype in a plain dictionary, so resolve on one thread.

## The scene seam (`ITileWorldScene`, `Scene3DTileWorldScene`)

Everything the view does to a scene goes through `ITileWorldScene`: `LoadMesh`, `UnloadMesh`, `DrawMesh`,
`LoadPropMeshes`, `UnloadPropMeshes`, `DrawProps`. It is shaped exactly on what `Scene3D` and the prop renderer
already offer, because its job is to let the view's bookkeeping run without a device, not to add an abstraction of
its own. `Scene3DTileWorldScene` is the shipped implementation and forwards straight through, and a test drives a
recording fake, which is how every view and residency rule is covered headless.

## The view (`TileWorldView`, `TileWorldViewOptions`)

`new TileWorldView(scene, doc, catalogs, resolver, options)` uploads one mesh set per catalog archetype up front,
so a region load is placements alone.

- `LoadRegion` / `UnloadRegion` build and free every plane of one region. Loading a region that is already loaded
  rebuilds it, so it doubles as a whole-region refresh. `LoadedRegions` is a snapshot, safe to walk while loading
  or unloading, and `LoadedRegionCount` is the count.
- `MarkDirty(region, plane)` queues one region-plane. `MarkDirty(worldRect, plane)` is the edit-facing overload: it
  grows the rect by `DirtyRegionMargin` (2 tiles) before turning it into region marks, because a corner height is
  shared by four tiles, a lattice normal is a central difference reading one corner further, and a corner colour
  averages the tiles meeting there, so an edit one tile inside a border genuinely changes the NEIGHBOUR's mesh.
  Marks coalesce, so a stroke that touches the same tiles a hundred times remeshes each region-plane once.
- `Flush()` rebuilds queued region-planes oldest first, up to `MaxRebuildsPerFlush` (default 16) of them, and
  `PendingRebuilds` counts what the budget deferred to the next call. `Flush(int)` overrides the budget for one
  call, and `int.MaxValue` is the settle-now form a loading moment wants. The budget bounds UPLOADS, not mesher
  CPU: a rebuild that produces no mesh does not spend it. A rebuild that THROWS is dropped along with everything
  completed ahead of it, so that region-plane keeps its previous mesh instead of being retried every frame.
- `Draw(focus)` flushes, then queues every loaded region: each plane's ground mesh at its world transform, that
  plane's ground props, and its roofs. `focus` is the point the prop draw radius (`PropDrawRadius`, 96 m) is
  measured from, which is the camera subject rather than the observer tile.
- **The roof rule.** `Observer` is the tile the rule is judged from and `ObserverIndoors` reports whether that
  tile carries `TileSettings.Indoors`. Standing indoors hides the roofs of every plane ABOVE the observer's own,
  so the storey the observer is on keeps its props and the ceiling between them and the camera goes.
- `Dispose` frees every region and every archetype mesh set the view uploaded. The scene is not owned.

## Streaming (`TileRegionResidency`, `TileResidencyConfig`)

`TileRegionResidency(source, view, config)` keeps a square ring of REGIONS resident around one observer tile,
materialising each through a `TileWorldSource` and handing it to the view. Radii are in regions at Chebyshev
distance, so `LoadRadius` 1 is the 3x3 block around the observer's own region. `UnloadRadius` must exceed
`LoadRadius` by at least one region: that band is the hysteresis that stops an observer pacing a border from
loading and unloading the same column on alternate frames. Use `TileResidencyConfig.Default`, because a defaulted
struct is all zeroes and the constructor refuses it.

- `Update(observer)` drops what has fallen past `UnloadRadius`, then loads up to `MaxLoadsPerUpdate` (default 2)
  of what is missing, nearest first with a deterministic tie-break.
- `PrimeAround(observer)` fills the whole ring IGNORING the budget and finishes with an unbudgeted flush, so a
  teleport settles in one call instead of drawing cracked borders for several frames.
- **Streaming a region dirties its eight neighbours** on every plane, in both directions, because a region meshed
  while its neighbour was absent carries a border built from edge-extended data and would keep it forever.
- A DIRTY region is never dropped: unloading one with unsaved edits would throw, so it stays resident past the
  ring and says so once through `Log`. Regions the manifest does not list are skipped and do not spend the budget.
- A torn region file throws `TileWorldException` straight out of `Update`. A world whose files no longer match
  what wrote them is not something a streaming loop should paper over by drawing a hole.

## Headless capture (`TileWorldSnapshot`)

Two RGBA8 captures over `Render3DSnapshot`, both building a throwaway view, loading the regions the shot can see,
settling every queued rebuild before the first frame, and rendering `CaptureFrames` (2) frames so nothing that
warms up over a frame is read back cold. Needs a headless GPU device.

- `CaptureTopDown(doc, catalogs, resolver, rect, plane, pxPerTile, options, configureScene)` is the orthographic
  map shot: the image is exactly `rect.Width * pxPerTile` by `rect.Height * pxPerTile` and one tile is exactly
  that many pixels square, set outright rather than framed, because a fit margin turns an exact scale into an
  approximate one. North is UP and east is RIGHT (`TopDownAzimuth` 0). `plane` chooses whose corner heights size
  the clip band, not what is drawn: every plane of every loaded region is drawn, and the observer stands on the
  top plane so the roof rule shows every roof.
- `CapturePerspective(doc, catalogs, resolver, eye, target, width, height, observer, options, configureScene)`
  shoots from an eye toward a target in world metres at a 60 degree vertical field of view, loading every region
  within `PerspectiveRegionRadius` (3) of the target's region. `observer` defaults to the tile under the target on
  plane 0, so a shot aimed inside a house hides that house's roof.
- `configureScene` runs LAST inside the capture's setup, so a caller's lighting, post or camera changes win over
  everything the helper set.
- Both need the document's regions MATERIALISED first, through `TileWorldFile.Load` or
  `TileWorldSource.EnsureLoaded`. A region the document does not hold is skipped rather than loaded, so a lazily
  opened world captures only the regions resident at the time and the rest come out as void.

## Tests and goldens

CPU tests and the two `[GpuFact]` goldens both live in `KhaozEngine.Render.Tests/TileWorld/`, the repo norm
(`GoldenCompare` is internal to that assembly). `tileworld_greybox` is the perspective shot with the observer
INSIDE the house, so it locks the roofs-hidden half of the rule, and `tileworld_topdown` is the map shot with the
observer above the world, so it locks the roofs-drawn half. Both pass a flat background through `configureScene`
(no starfield, no outline), so the comparison grid spends its cells on the tile renderer rather than on a
procedural sky. Golden images live in `KhaozEngine.Render.Tests/Gpu/goldens/`, one file per backend, and a new or
rebaked golden must be baked on D3D11 and Vulkan through the `cross-platform-gpu.yml` `bake=true` dispatch before
it can gate, or a Metal-only bake turns `main` red on every other backend.
