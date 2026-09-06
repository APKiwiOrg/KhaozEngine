# KhaozEngine.TileWorld.Editing

The editing kernel over a `KhaozEngine.TileWorld` document: every mutation is a reversible command, so undo
and redo behave the same way whoever issued the edit. GPU-free and render-free, in the `Foundation` umbrella,
referencing `KhaozEngine.TileWorld` and nothing else. Design rationale:
`docs/design/TILE-WORLD-DESIGN-2026-08-15.md` in the engine repo.

## Why it is its own package

A tile world is authored from two frontends: the `ke-tileedit` MCP tool (an AI client over stdio) and, in a
later round, an in-engine GUI editor. Both mutate the same document and both need the same undo stack. Putting
the command layer here rather than in either frontend means the two cannot drift apart on what a single edit
is, and it keeps the layer free of any Gui or Render3D dependency, which is what lets a headless test cover the
whole thing.

The `MapDoc` side of the engine learned this the other way round: its command stack lives inside
`KhaozEngine.MapEditor`, which drags in Gui, Render3D and Terrain.Render3D, so the `ke-mapedit` tool carries a
renderer it only needs for two verbs. This package is that shape corrected.

## Quick start

```csharp
using System.IO;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;

string dir = "assets/worlds/hollowmere";
TileWorldDocument doc = TileWorldFile.Load(dir);
// manifest catalog paths are relative to the WORLD directory, so resolve them against it
TileWorldCatalogs catalogs = TileWorldCatalogs.Load(
    doc.CatalogPaths.Select(p => Path.IsPathRooted(p) ? p : Path.Combine(dir, p)));
var editing = new TileEditingDocument(doc, catalogs);   // bakes the collision map once, up front

// one edit: paint a 16x16 patch of grass on the ground plane
editing.Execute(new SetTilesCommand(new TileRect(0, 0, 16, 16), plane: 0,
    underlay: 1, overlay: null, shape: null, rotation: null, settings: null));

// a factory reads the document and hands back the command that expresses the edit
editing.Execute(TileEditOps.Raise(doc, new TileRect(4, 4, 9, 9), plane: 0, deltaCm: 150, falloff: 1f));

editing.Undo();                       // the hill is gone, collision rebaked over the same rects
editing.Redo();                       // and back

foreach (TileDirtyRect d in editing.PendingRebuilds)
    view.MarkDirty(d.Rect, d.Plane);  // a TileWorldView, if this frontend has a renderer
editing.AcknowledgeRebuilds();

TileWorldFile.Save(doc, "assets/worlds/hollowmere");
editing.MarkSaved();                  // clears IsDirty at this history position
```

## The command contract

`ITileCommand` is one reversible edit: `Label` for the undo menu, `Apply(doc)`, `Revert(doc)`, `TryMerge(next)`
and `DirtyRects`. `TileCommandBase` is the shared base carrying the label, an accumulating `Dirty` list, a
no-merge default and `AbsorbDirty`, so a concrete command writes only its own mutation.

**Capture happens once, on the FIRST apply.** A command reads the state it will need to revert itself the first
time it runs and keeps it, so a redo replays the same edit rather than capturing the state its own previous
apply left behind, which would turn the next undo into a no-op. A command whose validation threw before it
wrote anything captured nothing, and its revert is a no-op.

**Dirty rects are the unit both collision rebaking and renderer invalidation work in.** `TileDirtyRect(Rect,
Plane)` is a rect of world tiles on one plane, far edges exclusive. Applying and reverting reach the same
tiles, so one set serves both directions. A rect must cover every tile whose layers, corners or collision
moved, INCLUDING the full footprint of an object the command removes, measured with `TileFootprint.Of` BEFORE
the removal, because a rebake cannot measure what is gone. For a corner height it must cover the tiles on both
sides of every corner that moved.

**A merge takes over the other command's rects.** `TryMerge` returning true means the newer command has been
absorbed and the older one is the only one left to revert, so it MUST first union the newer one's `DirtyRects`
into its own, which is what `AbsorbDirty` does. A move that drags an object A to B and then B to C while
keeping only its A and B rects leaves C reading blocked for good once the gesture is undone. Repeated rects are
kept rather than filtered: rebaking one rect twice costs time, while scanning for duplicates costs a pass over
the whole accumulated set on every step of a long drag.

## `TileEditHistory`

The undo/redo stack with gesture coalescing. `Execute(doc, command)` applies the command and pushes it,
clearing redo, unless the command on top absorbs it through `TryMerge`, in which case no second step is pushed.
`Undo(doc)` and `Redo(doc)` move one step and return false when the matching stack is empty. `CanUndo`,
`CanRedo`, `UndoLabel`, `RedoLabel`, `UndoDepth` and `RedoDepth` report the state, and a coalesced gesture
counts as one step in `UndoDepth`.

A merge barrier is raised after any undo or redo, so the next edit starts a fresh step instead of coalescing
into a reactivated one. `SealGesture()` raises the same barrier at an explicit boundary (a drag release, focus
loss, a save) and is idempotent.

## `TileEditingDocument`

The open world plus its editing state, and the one mutation path a frontend uses.

- `TileEditingDocument(doc, catalogs)` bakes the collision map once up front. `Collision` takes its plane count
  from `TileWorldDocument.PlaneCount` at that moment and keeps it, so a later change to the document's plane
  count needs a fresh editing document.
- `Document`, `Catalogs`, `History` and `Collision` are the parts. `Collision` is derived, never authored and
  never saved.
- `Execute(command)`, `Undo()` and `Redo()` are the mutation entry points. Each one rebakes collision over the
  command's dirty rects (one `TileCollisionBaker.Rebake` per non-empty rect) and then raises the matching
  event. A command reporting a rect on a plane the collision map does not have is rejected in one pass BEFORE
  anything applies, so a bad plane is a clean throw rather than a half-rebaked map.
- `PendingRebuilds` accumulates those same rects, in the order the edits touched them, for a renderer to
  consume. `AcknowledgeRebuilds()` clears them. Repeats are kept rather than merged, because a rebuild is
  idempotent and folding rects from unrelated edits into one bounding rect would cover the whole world after
  two edits at opposite corners. A renderer's own seam margin is its own business (`TileWorldView.MarkDirty`
  grows a rect by two tiles for exactly that reason).
- `IsDirty` is tracked by history POSITION against a saved marker, so undoing back to the saved point clears
  it. `MarkSaved()` seals the current gesture first, so a later same-gesture edit can never merge into the
  saved command and hide itself. If a fresh edit discards the history branch that held the saved point, that
  state is unreachable and `IsDirty` stays true until the next save.
- `SealGesture()` forwards to the history.
- `CommandApplied`, `CommandUndone` and `CommandRedone` fire after the collision map is up to date, carrying
  the command. `CommandApplied` fires on EVERY execute, including one that coalesced into the current undo
  step, because a merged command still applied its mutation.

## The commands

| Command | Constructor | Dirty rects |
|---|---|---|
| `SetTilesCommand` | `(rect, plane, underlay?, overlay?, shape?, rotation?, settings?)` | the rect on that plane, or none for an empty rect |
| `SetCornerHeightsCommand` | `(cornerRect, plane, newCm[])` | one tile wider and one taller than the corner rect, starting one tile west and south of it, so it covers the tiles on both sides of every corner |
| `PlaceObjectCommand` | `(catalogs, archetypeId, x, z, plane, rotation, tags?)` | the archetype's rotated footprint |
| `MoveObjectCommand` | `(catalogs, id, x, z, plane)` | the footprint it left AND the one it arrived on, plus any absorbed by a merge |
| `RotateObjectCommand` | `(catalogs, id, rotation)` | BOTH footprints, the old rotation's and the new one's |
| `RemoveObjectCommand` | `(catalogs, id)` | the object's full footprint, measured before the delete |
| `SetObjectTagsCommand` | `(id, tags?)` | none |
| `SetMarkerCommand` | `(name, x, z, plane, tags?)` | none |
| `RemoveMarkerCommand` | `(name)` | none |
| `SetFoliageLayerCommand` | `(doc, layer)` | the union of old and new raster extents, split by plane when needed |
| `RemoveFoliageLayerCommand` | `(doc, id)` | the removed raster extent |
| `CreateRegionCommand` | `(coord)` | the region's full tile rect on EVERY plane |
| `DeleteRegionCommand` | `(coord)` | the region's full tile rect on EVERY plane |
| `CompositeCommand` | `(label, commands)` | every child's rects, read live |
| `SnapshotRectCommand` | `(label, rect, planes, mutate)` | the rect on each listed plane |

**`SetTilesCommand`** fills any subset of a plane's authored layers over a rect. A layer whose argument is null
is not read, not captured and not touched, so a fill can repaint the ground without disturbing what was built
on it. Every region the rect spans is checked BEFORE a single write, so a fill running off the edge of the
authored world leaves the world exactly as it found it rather than painting the half that happened to be there.
The check walks region coordinates rather than tiles and reports the first gap in reading order.

**`SetCornerHeightsCommand`** writes a rect of CORNERS, one new height per corner in row-major order with z
outer and rising (south row first). The value array is copied, so a caller reusing a scratch buffer for the
next brush stroke cannot rewrite what a redo replays. Corners whose own region does not exist are SKIPPED
rather than thrown on, because the lattice edge-extends into unauthored space and a brush overlapping the edge
of the world is a normal thing to do. `CornerCount` is what the rect covers and `WrittenCount` what the last
apply actually landed, so a tool can report how much of a brush or an imported heightmap fell outside the
world. The written record is refreshed on every apply, so a redo after a region was deleted underneath writes
fewer corners and the revert follows that rather than writing into a region that is no longer there.

**Object commands.** `PlaceObjectCommand` allocates its id on the first apply and keeps it, so a redo re-adds
the SAME object rather than a new one wearing a new id, and anything referring to it by id survives an undo and
a redo. `ObjectId` is null until that first apply. The document's allocator is deliberately not rewound by the
revert, so an id this command released is never handed to something else while the redo still expects it back.
`MoveObjectCommand` coalesces with a later move of the same object, keeping its own ORIGIN and taking the newer
target, so one undo returns the object all the way home. `RotateObjectCommand` reports both footprints because
a non-square footprint covers different tiles at two rotations. `RemoveObjectCommand` captures id, archetype,
anchor, plane, rotation and tags, and puts the object back with the id it had. `SetObjectTagsCommand` reports
no rects at all: nothing derived reads tags, the collision baker takes the archetype, anchor, plane and
rotation, and the renderer takes the mesh.

**Marker commands** report no dirty rects either, for the same reason: a marker draws nothing and blocks
nothing. `SetMarkerCommand` captures whatever the name held, another marker's position and tags or the fact
that the name was free, and validates the destination plane and region BEFORE the capture so a failed
placement leaves both the document and the command untouched. `RemoveMarkerCommand` throws when the name is not
in the document, because a delete of nothing is a mistake worth reporting rather than a silent no-op in the
undo stack.

**Foliage commands** replace or remove one immutable cosmetic layer as one undo step. They capture the old
layer before applying and report its world-metre raster extent as tile dirty rects, including both planes when
a replacement moves between them. Collision rebaking receives those rects through the ordinary command path,
but foliage itself contributes no flags, so collision remains byte identical across apply, undo and redo.

**Region commands** report the region's full tile rect on every plane, because the collision map keeps its
storage per region and the rebake over those rects is what gives a new region storage and takes it away again.
Without them the map would keep stale storage for a region the document no longer has, which reads WALKABLE
rather than blocked. The plane count comes from the document, so the rects can only be built on the first
apply. `CreateRegionCommand.Created` reports whether that apply actually created the region, and an apply over
one that was already there is a no-op with a no-op revert. `DeleteRegionCommand` captures the region OBJECT
itself rather than a copy: `DeleteRegion` detaches the instance untouched, so what the command holds IS the
pre-delete state, with no clone that could fall out of step and no 64x64 array copy per layer per plane, and a
caller that took a reference before the delete still holds the live region after the undo. Delete and the undo
of create use the permanent path, so a source-backed world drops its known hash and marker rows instead of
turning the operation into a safe unload.

**`CompositeCommand`** lands a list of commands as ONE undo step: applied front to back, reverted back to
front, reporting every child's rects live rather than as a snapshot (a child can only work out what it touched
while it applies). A child that throws part way takes the whole composite with it, and the ones that already
applied are reverted back to front before the exception carries on, so an apply either lands whole or leaves
the document as it found it. A rollback that itself throws does not stop the remaining reverts and does not
replace the original exception: what comes out is an `AggregateException` whose FIRST inner exception is the
failure that started it. It never merges, because a composite is already a whole authored operation.

**`SnapshotRectCommand`** runs an arbitrary mutation over a rect and makes it undoable by capturing the
pre-image first: every authored layer and every corner height inside the rect on each listed plane, which
layers each touched region had not materialised, plus every object and marker ANCHORED inside it. It is the
general answer for an edit whose reach is a rect but whose shape is not one command's worth of writes, and it
is what a prefab stamp goes through. A mutation that throws part way is rolled back here before the exception
carries on.

## Limits worth knowing, stated rather than solved

- **`SnapshotRectCommand` owns what was inside the rect when it looked, and nothing else.** An object whose
  ANCHOR is outside the rect but whose footprint reaches into it is not captured, so a mutation that moves or
  deletes one is not undone. The same goes for an object or marker the mutation moves INTO the rect from
  outside: the revert sweeps the rect clean before re-adding what it captured, so that one is removed and not
  put back.
- **Regions are outside that ownership in both directions.** A region the mutation CREATES inside the rect is
  not removed by the revert, because the snapshot restores tiles rather than the existence of the region
  holding them. A region the mutation DELETES makes the revert THROW when it re-adds a captured object there,
  rather than dropping the object quietly, because a snapshot that cannot restore should say so instead of
  losing content.
- **A `SnapshotRectCommand` revert is exact, but its REDO is not necessarily identity-preserving**, because a
  redo re-runs the mutation rather than replaying its writes. A mutation that allocates object ids allocates
  fresh ones every time, which `TileEditOps.PlacePrefab` does: execute then undo leaves the world hashing
  exactly as it did, but execute, undo, redo leaves the same content wearing different ids with the document's
  allocator further along. Object ids are part of a region's canonical bytes, so that redone world does not
  hash the same as the first execute even though it is the same world to play.
- **A `SetCornerHeightsCommand` on a plane above 0 materialises that plane's derived lattice, and `Revert`
  cannot un-materialise it.** Above plane 0 a null height layer means "derive from plane 0 plus the plane
  lift", and the document's first corner write on such a plane fills the whole layer from that derivation
  first. The revert restores the corner VALUES it wrote, so the content is identical, but the layer is now
  authored rather than derived: different bytes on disk and a different world hash.
  `SnapshotRectCommand` does track and restore that null-ness for the rect it owns, over a corner span one
  region wider and taller than its tiles, which is why a prefab stamp undoes to the same bytes.

## `TileEditOps`

Static factories that READ the document and return the command expressing the edit. Nothing here mutates: the
returned command goes to `TileEditingDocument.Execute`, which is what keeps every path undoable and the
collision map in step.

- `Raise(doc, cornerRect, plane, deltaCm, falloff = 0)` moves every corner of the rect by the delta (negative
  lowers). Above a falloff of 0 the delta is scaled by `1 - falloff * (distance / halfExtent)` clamped into
  0..1, where distance is the Chebyshev distance from the rect centre in CORNER units and halfExtent is the
  distance out to the rect's outermost ring, half of the larger dimension less one. Falloff 1 therefore fades a
  square brush to exactly nothing on its edge ring, and 0.5 to half there. A rect one corner wide or tall has
  no extent to fade across, so every corner keeps the full delta.
- `Flatten(doc, cornerRect, plane, toCm?)` levels every corner to the given height, or to the rounded average
  of the corners as they stand when it is null (half a centimetre rounds away from zero).
- `Smooth(doc, cornerRect, plane, iterations)` runs an iterated 3 by 3 box blur, taking neighbours outside the
  rect from the document (read fresh, never written, so the patch blends into the terrain around it) and
  rounding to whole centimetres between passes. Double buffered, so a pass cannot feed its own half-blurred
  output to the corners it visits later. Takes 1 to 64 iterations: the result has long since converged by 64,
  and the ceiling stops a mistyped count from walking the rect billions of times inside a call nobody can
  cancel.
- `SetHeights(doc, cornerRect, plane, cm[])` is the plain write every other height factory ends up building.
- `Line(catalogs, archetypeId, from, to, plane, rotation = 0)` places one object per tile of the integer
  Bresenham line, both ends included, as one `CompositeCommand`. Every object takes the same archetype and
  rotation, which is what a run of fence or wall pieces wants.
- `Scatter(editing, archetypeId, rect, plane, spacing, jitter, seed)` is a deterministic scatter: a grid at
  `spacing`, each point pushed by up to `jitter` tiles on each axis from a hash of the grid point and the seed
  rather than a random source, so the same arguments always produce the same world (splitmix64's finaliser,
  written out in the package on purpose, because this IS the world's content). A point is skipped when the
  jitter carries it out of the rect, when its tile reads blocked (which covers a region that does not exist,
  since the collision map answers blocked for one it does not hold), when an object is already anchored there,
  or when an earlier point of the same scatter claimed it. An empty result is legitimate.
- `PlacePrefab(prefab, x, z, plane, rotation)` stamps a prefab as one undo step, as a `SnapshotRectCommand`
  over the exact rect `TilePrefabs.Place` touches (the tile rect grown one tile west and south and one row and
  column north and east, for the corner writes on its far edges) across the planes the prefab carries. The
  planes are `plane` through `plane + prefab.PlaneCount - 1`, UNCLIPPED: a prefab reaching above the world's
  planes is refused by `Execute` rather than clipped the way `TilePrefabs.Place` clips it, because a snapshot
  must not claim a rect it cannot capture or restore.
- `ImportHeights(PgmImage, ...)`, `ImportHeights(PngImage, ...)` and the path overload resample a
  greyscale heightmap onto the corner rect. The image is stretched over the rect with bilinear interpolation,
  its first and last columns landing on the west and east corners and its first and last rows on the north and
  south ones, and image row 0 is the NORTH edge, because an image is written top row first while tile z grows
  northward. An image exactly the size of the rect maps one sample to one corner with nothing interpolated.
  Each interpolated sample is then mapped linearly from `0..PgmImage.MaxValue` onto `minCm..maxCm` and rounded
  away from zero, the mapping running AFTER the interpolation so a heightmap rounds once at the end. A sample
  above maxval (which a malformed file can carry) is treated as white rather than allowed to push a corner past
  `maxCm`.

Every height factory returns a `SetCornerHeightsCommand`, so `WrittenCount` against `CornerCount` reports how
much of the edit landed inside the authored world.

## `PgmReader` and `PgmImage`

`PgmReader.Read(path)` and `Read(bytes, name?)` decode binary PGM (netpbm P5) greyscale images, 8 or 16 bit,
which remains the smallest heightmap format for generated tooling. The path overload also accepts `.png` through
`KhaozEngine.Imaging.PngReader`. Its PNG overload ignores alpha and reads the first color channel, preserving
ordinary greyscale RGB heightmaps while retaining all 16 bits of sample precision.

`PgmImage(Width, Height, MaxValue, Samples)` carries the decoded samples row-major with the TOP row first, the
order the file itself is written in, and `Sample(x, y)` reads one with per-axis bounds checks (a raw flat index
would quietly hand back the wrong sample for an out-of-range y on a wide image rather than throwing).

Rules the reader enforces, each refused with a `TileWorldException` naming the file and the problem rather than
decoded on a guess, because a heightmap that reads a byte out of step is a terrain nobody can tell is wrong
until it has been authored on top of:

- The first two bytes must be the magic `P5`, so a P2 ascii greymap or a P6 colour pixmap is refused rather
  than read as one.
- Width, height and maxval follow as ASCII decimals, with whitespace or a `#` comment (to the end of its line)
  allowed between any two of them. Both dimensions must be positive and maxval must be 1 to 65535.
- **Maxval is closed by EXACTLY ONE whitespace byte, and no comment may follow it**, because the byte after
  that one is already the first sample of the raster. Skipping every whitespace byte there would eat the first
  sample of any raster beginning with a byte worth 9 to 13 or 32.
- **That rule makes line endings matter: write PGMs with LF.** A header terminated with CRLF spends its CR as
  the delimiter and leaves the LF as sample 0, shifting the whole raster by a byte. The reader cannot paper
  over it, because a raster whose first sample is genuinely 10 looks identical to a stray LF, and guessing
  between them would corrupt one of the two files silently.
- The byte count is checked against the header BEFORE anything is allocated, so a header claiming a 100000 by
  100000 image is refused on the bytes it does not have rather than after asking for 20 GB of samples.

Trailing bytes after the raster are the one thing tolerated, because a file with a stray newline at the end is
still a whole image.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
