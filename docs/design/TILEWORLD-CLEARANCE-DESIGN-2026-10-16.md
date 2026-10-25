# Tile world clearance

Status: shipped in 21.2.0. The consumer requirement is tree and building clearance for Grimhollow.
Current use belongs in the [TileWorld package reference](../../KhaozEngine.TileWorld/README.md#clearance), the
[ke-tileedit reference](../../KhaozEngine.TileEdit.Tool/README.md#clearance) and the
[consumer guide](../USING-KHAOZENGINE.md#tile-world-khaozenginetileworld). This record keeps the reasoning and
the places where the shipped code differs from the spec.

## The problem

A tile world places every object by its anchor tile, but a mesh reaches well past its footprint. Grimhollow's
tree crowns spread 3 to 7 m from the trunk after its world rescale, and four of 588 trees drew through a roof or a
wall. Nothing caught it. `TileWorldValidator` checks footprints against regions, never one object against
another, and the game's own tree placer kept trees one tile from a building anchor. Any edit through
ke-tileedit could bring the clipping back silently.

## Owner rulings

- **TC1 Authoring check in the engine, not a shader.** Hiding tree pixels inside a building slices crowns along
  wall and roof planes, costs a volume test per pixel and never tells the author. The check is engine work,
  released and adopted like any engine capability.
- **TC2 Declared tag pairs.** A world declares rules of the form "objects tagged A keep clear of objects tagged
  B". The engine interprets no tag strings. A later rule for rocks or bushes needs no engine change.
- **TC3 Refuse the save, allow a waiver.** ke-tileedit refuses a save with an unwaived issue. A deliberate
  overlap carries a per-object waiver tag, and using it is an owner decision in the consuming game.

Calls approved with the design:

- The check is not part of `TileWorldValidator.Validate`. It reads mesh files, and a head or a server never
  needs them to open a world.
- The block is outside `TileWorldHash.OfWorld`. It has no runtime effect, so tuning a rule changes no world
  digest and forces no navigation rebake.
- No TileWorld format version change. An optional additive manifest block has precedent in `foliageLayers`.
- The capability ships as a minor, 21.2.0.

## The block

`world.json` carries an optional `clearance` block beside `foliageLayers`:

```json
"clearance": {
  "meshRoot": "../../models",
  "cellSize": 0.1,
  "waiverTag": "clearance-waived",
  "rules": [
    { "name": "trees-clear-buildings", "subjectTags": ["tree"], "obstacleTags": ["structure"], "tolerance": 0.05 }
  ]
}
```

`TileClearanceSettings` is the immutable, self-validating model and `TileWorldDocument.Clearance` holds it.
`meshRoot` is relative to the directory holding `world.json` and uses forward slashes only. A rooted path is
refused with `ArgumentException` by the settings and as a `TileWorldException` at load, because world files are
committed and shared across machines. The rule is decided by the characters alone, the same on every OS: a leading
`/` or `\`, a drive prefix (an ASCII letter then `:`) and any backslash are refused. `Path.IsPathRooted` was not
used because it reads drive letters and backslashes only on Windows, so `C:\kit` or `..\models` would save on
macOS and then fail to load or resolve differently on a Windows head. It may be empty only while there are no rules. `cellSize` runs from
0.02 to 1 with a default of 0.1. A null `waiverTag` waives nothing. Rule names are unique by ordinal comparison,
both tag lists must be non-empty and a tolerance must be finite and not negative. A save writes every default out,
so a stored block reads the same without knowing this engine's defaults. An absent block stays absent, so an
older world keeps its bytes.

`TileClearanceRule` is a record with value equality written by hand: the name ordinally, both tag lists
ordinally in order, and the tolerance. The synthesized equality would compare the tag lists by reference, and the
settings take private copies of both, so a validated rule would never equal the rule its caller built or a loaded
world produced.

## The geometry core

The core lives in `KhaozEngine.Primitives` under `Clearance`, so `KhaozEngine.TileWorld` and a later
`KhaozEngine.MapDoc` adapter can both reach it without a reference cycle. It knows meshes, transforms and pitches,
never a document type.

- `ClearanceMesh` copies and validates an indexed triangle mesh once and keeps its local bounds.
- **Triangle sampling.** Each triangle is sampled in rows parallel to its longest edge, no more than one pitch
  apart across its height, each row sampled at no more than one pitch along its length. The first row is the
  longest edge itself, the two other edges get their own samples at the same spacing, and the three vertices are
  always visited exactly. The spec asked for barycentric samples. Barycentric steps on a long thin sliver
  oversample one direction and undersample the other, so rows along the longest edge replaced them and the cost
  now scales with area plus perimeter for every triangle shape. A pitch below 0.01 or not finite is refused.
- `ClearanceSamples` is a subject's probe points at the block's cell size, built once per mesh in local space.
- `ClearanceSolid` is an obstacle built once per mesh in local space. The spec rasterized triangles. The shipped
  solid instead fills a planar grid from the same surface samples at half the cell size, each filled cell keeping
  its highest sample as its top, and every cell's base is the mesh's lowest point. A two-pass chamfer distance
  transform gives each filled cell its planar distance to the nearest empty cell, less half a cell. A grid above
  `ClearanceSolid.MaxCells` throws `ClearanceTooLargeException`.
- **Depth** is the nearest way out of the solid: up to the cell top, down to the base, or sideways to an empty
  cell. A thin obstacle such as a wall therefore caps reported depth near half its thickness, which is the honest
  answer to how far the subject must move to leave it.
- `ClearanceProbe.Deepest` carries each subject point into the obstacle's frame through the inverse of the
  obstacle's placement and returns the deepest point beyond the tolerance, with its world position.
  `PlanarBounds` is the broad phase.

### The filled-solid limit

An obstacle is solid from its lowest point up to its top surface everywhere it has a surface. A doorway, an arch,
the space under eaves or a bridge deck over water all read as filled. That bias suits an authoring check: it
leans toward reporting a contact a precise test would clear. A crown in a roof's attic counts as clipping, which
is correct for roofs. A hollow piece such as a bridge deck or a balcony must not carry an obstacle tag.

### The error band

The solid is a raster, so every reading carries an error that scales with the cell size c. The figures in
brackets are for the default c = 0.1.

- A point outside the surface but inside a boundary cell of a curved or off-grid edge reads up to c / 2 inside
  (0.05).
- A cell whose only empty neighbour is diagonal reads about (sqrt 2 - 1/2) c inside (0.091), from the chamfer
  distance less half a cell.
- A cell top is the highest sample in the cell, so a point just above a sloped top reads up to about c times the
  slope's tangent inside, less its height above the surface. A point 1 cm above a 45 degree slope reads about
  0.09, and about 0.16 at 60 degrees.
- The check can also under-report. A contact narrower than the subject's sample pitch (c) can fall between
  samples and be missed, and an obstacle wholly inside a subject is never seen, because only the subject's
  surface is sampled.

The guidance that follows is a tolerance of at least c / 2 on each rule to absorb the edge band, and a smaller c
in a world with steep roofs.

## The TileWorld adapter

- `ITileMeshGeometry.TryLoad(meshRef, out mesh, out error)` keeps mesh loading out of `KhaozEngine.TileWorld`.
- `TileWorldClearance.Check(doc, catalogs, meshes)` reads loaded regions only and returns `TileWorldIssue`s.
  Each object is placed with `TileObjectPlacement.LocalToWorld`, the transform the prop renderer draws with, so
  quarter turns, archetype yaw offsets and plane heights come out exactly as drawn, and pairs across planes are
  checked because a roof stands on the plane above the trees beside it.
- Matching reads archetype catalog tags. The waiver reads the object's own tags and removes that object from
  every pair. An object whose archetype the catalogs lack is skipped, because the validator already reports
  `archetype.missing`. An object never pairs with itself.
- Every mesh is loaded, rasterized and sampled once per call, shared by every archetype naming it. A 16 m planar
  bucket grid over placed bounds limits probing to pairs that can touch, so cost stays near linear in object
  count.
- Codes, all sharing `TileWorldIssue`:
  - `clearance.rule`: a rule's subject or obstacle tags match no archetype, so a typo cannot pass everything.
  - `clearance.mesh`: a matched archetype has no `meshRef`, or its mesh cannot be read or built. Reported once
    per archetype, and its objects are never silently passed.
  - `clearance.overlap`: one per penetrating pair at its deepest point, naming the rule, both objects with their
    archetypes, tiles and planes, and the depth in metres.
- Issues come back rule issues first, then mesh issues by archetype id, then overlaps by rule order, subject id
  and obstacle id.
- `TileWorldClearance.ResolveMeshRoot` resolves `meshRoot` against the world directory for a tool.

## The glb seam

`GltfTileMeshGeometry` in `KhaozEngine.TileWorld.Render3D` is the shipped `ITileMeshGeometry`. It maps a
`meshRef` to a path exactly as `GltfMeshResolver.PathFor` does and reads geometry through `GltfLoader.Load`,
which needs no GPU, so node transforms are baked and the mesh sits in the archetype's local metres as drawn. It
reads `meshRef`, never `lodMeshRef`. A blank reference, a rejected path, a missing file, a loader throw and a
mesh `ClearanceMesh` refuses each return false with a reason naming the reference. Each load is cached per
reference with the file's last write time and length, and every ask re-stats the file and reloads when either
differs. A missing file is probed again on every ask, and only a reference that names no usable path stays cached
as a failure. Only the named file is stamped, so a `.gltf` whose external buffers change on their own keeps its
old load. The spec keyed the cache per path. The shipped cache is keyed per mesh reference, which is what the
check asks by, and each entry records the path it resolved to.

## ke-tileedit

- `world_save` runs the clearance check after `ValidateOrThrow` whenever the world declares rules. Any issue
  refuses the save, nothing is written, and the refusal lists every issue as `[code] message`, one per line.
- `world_validate` appends the clearance issues to the validator's.
- A resolved mesh root that is not a directory is one `clearance.mesh` issue, so a typo in the root never reads
  as a world with nothing to check. The save refusal for the root covers "not a directory" only, which is
  narrower than the spec's wording. A root that exists but cannot be read surfaces as one `clearance.mesh` issue
  per archetype whose mesh cannot be read.
- Five verbs author and run the block: `clearance_get`, `clearance_set`, `clearance_rule_set`,
  `clearance_rule_remove` and `clearance_check`. Each mutation is one undo step through `SetClearanceCommand`
  in `KhaozEngine.TileWorld.Editing`, which reports no dirty rects because nothing draws or collides with the
  block. `clearance_check` reports without writing.
- `clearance_set` replaces all three header fields on every call. An omitted `cellSize` or `waiverTag` resets
  to its default rather than keeping the current value, so a caller passes the current values to keep them.
  Its rules are kept.
- The session keeps one `GltfTileMeshGeometry` per open world, created lazily for the resolved mesh root and
  reused across saves and checks, because each one parses every glb it is asked for and a kit runs to a hundred or
  more. It is replaced lazily at the next check after the resolved root changes, through `clearance_set` or an
  undo or redo of one, and on `world_open` or `world_create`. Because each glb is stamped, a glb written or
  re-exported while the world is open is seen at the next save or check with no `world_open`.

## Follow-ups

- [KhaozEngine #1433](https://github.com/APKiwiOrg/KhaozEngine/issues/1433): the MapDoc adapter over the same
  core. It is not built here.
- [KhaozEngine #1422](https://github.com/APKiwiOrg/KhaozEngine/issues/1422): the validator's doc comment claims it
  runs at load on the heads, but only tools and tests call it.
