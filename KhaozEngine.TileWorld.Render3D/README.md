# KhaozEngine.TileWorld.Render3D

The render arm of [KhaozEngine.TileWorld](../KhaozEngine.TileWorld): meshes a tile world's ground into
`Render3D` meshes for the tile-ground pipeline, places its objects through the `Terrain.Render3D` prop path, and owns the
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

## Ground (`TileGroundMesher`, `ITileGroundSlotMap`, `TileColors`)

`TileGroundMesher.Build(doc, catalogs, region, plane, options)` returns the `GltfMesh` of one region-plane, or
null when the region-plane has no drawable tile (underlay 0, or `TileSettings.NoDraw`). The mesh is REGION LOCAL,
so draw it at `TileGroundMesher.WorldMatrix(doc, region)`, a pure translation to the region's lowest tile corner
with Y left at 0 because the vertices already carry absolute corner heights. Vertices are the existing
`ModelVertex`, with its fields repurposed for the tile-ground pipeline (`Scene3D.LoadTileGroundMaterial`), so
nothing in the upload path moves.

- **Four material slots per TILE, weights per vertex.** Every triangle of a tile carries the same four slots, one
  per corner in SW, SE, NW, NE order, as floats in `Uv.x`, `Uv.y`, `Tangent.x` and `Tangent.y`. `Color` is that
  vertex's four weights over them: one-hot at a corner, 0.5 and 0.5 at a mid-edge point of an overlay cut.
  `Tangent.z` is the brightness jitter and `Tangent.w` is 0. Slots per tile rather than per triangle is what keeps
  the ground continuous: a corner shared by four tiles is one-hot on the same material from all of them, and a
  shared edge interpolates the same pair from either side.
- **The corner material is the most-shared underlay.** `TileGroundMesher.CornerMaterial(doc, x, z, plane)` counts
  the up-to-four tiles sharing a lattice corner and takes the id most of them carry, ties broken by the LOWER id
  so every tile touching the corner picks the same one. Void tiles are the only exclusion: a `NoDraw` tile draws
  no ground of its own but still contributes its underlay, so the ground does not step at the edge of a hole
  punched for an object floor. `TileGroundMesherOptions.Slots` (an `ITileGroundSlotMap`) turns the id into the
  material set's layer slot, and an id the set does not carry lands on its reserved `MissingSlot`, whose layer is
  the magenta `TileGroundMesher.MissingMaterialColor`, so a dangling id is visible rather than invisible. The
  default `IdentitySlotMap` maps every id to itself, for a caller that has not built a set yet.
- **Jitter is per vertex, averaged at the corner.** `TileGroundMesher.CornerJitter` is the mean of
  `TileColors.Jitter` (a deterministic multiplier hashed from the world tile coordinate, plus or minus 4 percent
  by default, `JitterAmplitude` 0 disables it) over the same tiles, so the OSRS brightness variation stays soft
  across a corner instead of stepping per tile. It is a MULTIPLIER: no jitter is 1, and a vertex carrying 0
  renders black.
- **The colour path stays, and nothing in the mesh path reads it.** `TileGroundMesher.CornerColor` still blends
  the up-to-four sharing tiles' jittered material colours (`TileColors.Blend`, all-void blends to
  `TileColors.Void`), and `TileColors.Parse` still reads `#rrggbb` or `#rrggbbaa`. That pair is the GPU-free
  colour surface, for a caller that wants a tile's colour without the textured pipeline behind it: a minimap, a
  2D painter, a tool query. The vertices do not carry it any more, and the mesher itself only calls
  `TileColors.Jitter`.
- **Overlays are exact geometry, not an approximation.** The tile is cut by the shared
  `TileTriangulation.Triangulate` (two triangles for a plain tile or a diagonal half, four for a corner cut) and
  each triangle either names the overlay's slot in all four lanes at weight (1, 0, 0, 0) or is left to the tile's
  own corner materials. An overlay keeps the per-corner jitter, so a paved tile still varies softly rather than
  reading as one flat patch. The raycast in `KhaozEngine.TileWorld` calls the same function with the same inputs,
  so a click lands on the triangle that was drawn. A shape with no overlay material meshes as the plain pair.
- **Seamless by construction.** Normals come from the GLOBAL height lattice by central differences
  (`TileGroundMesher.CornerNormal`), which reads ACROSS region borders, so two regions meeting at a corner compute
  the identical normal and a border has neither a crack nor a lighting step. Set `SmoothNormals = false` for one
  flat normal per triangle instead. Vertices are never shared between triangles, because two triangles of one tile
  can carry different slots and weights (an overlay paints some of them and not others), so the mesher emits
  per-triangle vertices.

## Ground materials (`TileGroundMaterials`, `TileGroundMaterialSet`)

`TileGroundMaterials.Build(catalogs, load?)` turns a ground catalog into the `TileGroundMaterialSet` the meshes
are drawn with: one albedo layer per material in ASCENDING id order, then one reserved layer LAST filled with
`TileGroundMesher.MissingMaterialColor`. The set IS the `ITileGroundSlotMap` the mesher wants, so `SlotOf` answers
a catalog id with its layer and `MissingSlot` is that trailing layer, which is where a dangling id goes.

- **Textured or flat, one pipeline either way.** A material with a `Texture` is decoded (`load`, defaulting to
  `ImageRgba.Load`) and takes a WHITE tint, because the texture is the colour. A material with none becomes a flat
  layer of its catalog `Color` at the same size, so a colour-only world renders through the same textured pipeline
  and the same goldens, just without texture detail. The catalog colour stays what the headless readers use.
- **Paths resolve against the catalog FILE.** A relative `Texture` is combined with the directory of the catalog
  the material was declared in (`TileWorldCatalogs.MaterialSource`), the same rule `world.json` uses for its
  catalog paths. An absolute path is taken as written. A catalog built in memory has no directory to resolve
  against, so a relative texture on one throws a `TileWorldException` naming the material.
- **One size for the whole set.** Every layer is one slice of ONE texture array, so the first textured material
  decides the size and any other textured material of a different size throws, naming the material and both sizes,
  rather than being resampled behind your back. With nothing textured the flat fills are 1x1.
- `TilesPerMetre` is the material's own value or `TileGroundMaterials.DefaultTilesPerMetre` (0.5, a 2 m repeat).
  A catalog larger than `TileGroundMaterialConfig.MaxMaterials - 1` materials throws: the set is one uniform
  buffer, and splitting a catalog across several sets is not built.

Hand-build a `TileGroundMaterialSet` directly when the layers come from somewhere other than a catalog. The
constructor takes the size, the material id of each leading slot, and one layer per id PLUS the trailing reserved
one, and it refuses a layer that is not the size the set declares.

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
wall adds the north edge, a diagonal stands a post, a roof is a slab over the whole footprint), coloured from a
deterministic grey/brown/green palette by archetype id. Only the footprint extent scales with the tile size: every
thickness is absolute metres. It caches per archetype in a plain dictionary, so resolve on one thread.

Every greybox shape is PLANE-LOCAL: y 0 is the floor of the plane the object stands on, because `AnchorPosition`
already lifts a placement to that plane. A roof is placed on the plane ABOVE the walls it covers (that is what the
roof rule keys on), so its slab starts at y 0 too and the PLANE supplies the height, which is why
`GreyboxMeshResolver(float tileSize = TileWorldDocument.DefaultTileSize, float wallHeight = TileWorldDocument.DefaultPlaneHeight)`
makes a wall exactly one plane tall. Pass `doc.PlaneHeight` for `wallHeight` whenever a document is at hand, so a
world with a non-default plane height still has its walls meet its roofs.

## The scene seam (`ITileWorldScene`, `Scene3DTileWorldScene`)

Everything the view does to a scene goes through `ITileWorldScene`: `LoadMesh`, `UnloadMesh`, `DrawMesh`,
`LoadPropMeshes`, `UnloadPropMeshes`, `DrawProps`, plus the ground-material trio `LoadTileGroundMaterial`,
`UnloadTileGroundMaterial` and the `LoadMesh(mesh, material)` overload that binds a mesh to the tile-ground
pipeline. The trio ships as DEFAULT interface implementations (an invalid handle, a no-op, and a fall-through to
the material-free upload), so an implementation written before textured ground existed keeps compiling. It is shaped exactly on what `Scene3D` and the prop renderer
already offer, because its job is to let the view's bookkeeping run without a device, not to add an abstraction of
its own. `Scene3DTileWorldScene` is the shipped implementation and forwards straight through, and a test drives a
recording fake, which is how every view and residency rule is covered headless.

## The view (`TileWorldView`, `TileWorldViewOptions`)

`new TileWorldView(scene, doc, catalogs, resolver, options)` uploads the ground material set and one mesh set per
catalog archetype up front, so a region load is placements alone.

- **The ground material set is uploaded once and shared.** `TileWorldViewOptions.GroundMaterials` is the hook, and
  null builds one from the catalogs with no texture loader, which is every material as a flat colour layer. The
  view points `Options.Mesher.Slots` at whichever set it ends up with, because the slots a vertex names only mean
  anything against the set the mesh is drawn with, and it reads back as `GroundMaterials`. Every region-plane mesh
  goes up bound to it, and `Dispose` frees it.

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
