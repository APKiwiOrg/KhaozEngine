# KhaozEngine.Dungeon

Deterministic procedural dungeon generator. `DungeonGenerator.Generate(config, seed)` grows a multi-level
room graph (rooms, corridors, and stairs) on a 3D tile grid, committing each room together with its corridor,
doors, and edge atomically, then applies a wall pass and plans gating (an optional boss room, locks on
critical-path bridge edges, and reachability-proven key placement) and gameplay markers (spawn/loot/objective/
entrance). The result is completable by construction, and `DungeonGenerator.Generate` re-proves that via the
always-on `DungeonSolver.Verify` before it ever returns a layout: a generator bug can produce an unsolvable
dungeon, but it can never leave this package. Identical config and seed always produce an identical layout
(`DungeonLayout.LayoutHash`). Render-free and GPU-free throughout.

A generated `DungeonLayout` is a raw tile raster plus a room graph. It carries no kit content or world
position: two sinks (`DungeonMapDocEmitter` and `DungeonStamp`) turn it into concrete content, resolving an
abstract `DungeonPiece` vocabulary through a `DungeonKitMap` and a world placement through a
`DungeonPlotTransform`.

## Generate + solve

```csharp
using KhaozEngine.Dungeon;

var config = new DungeonConfig { RoomCountTarget = 12, MaxFloors = 2, LockCount = 1 };
DungeonLayout layout = DungeonGenerator.Generate(config, seed: 2026UL);

// DungeonGenerator.Generate already ran DungeonSolver.Verify and throws if it failed, so a layout in hand
// is always solvable. Call Verify again yourself only if you hand-edited a layout after generation.
DungeonSolveReport report = DungeonSolver.Verify(layout);
Console.WriteLine($"solvable: {report.IsSolvable}, critical path: {layout.Stats.CriticalPathLength} edges");
```

`DungeonConfig` covers plot and room sizing, floor count, the loop-edge target, lock/key count, marker
density caps, corridor width (`CorridorMinWidth`/`CorridorMaxWidth`, see below), the hall knobs
(`HallChancePercent`, `HallMinLengthTiles`/`HallMaxLengthTiles`, see below), and the roofed-interior toggle
(`CeilingMode`, see below). `Validate()` throws
`ArgumentException` naming the first offending property (`Generate` calls it for you). `DungeonLayout` exposes the raster read-only via `GetCell(x, z, floor)` plus
`Rooms`/`Edges`/`Keys`/`Markers` and a `Stats` summary (`RoomsPlaced`, `CriticalPathLength`, `LocksPlaced`,
`Saturated` when the plot or room budget ran out before hitting the target). `CriticalPathTarget` is
validated but advisory and reserved: the boss room is derived as the farthest room from the entrance by BFS,
not steered toward this length, so treat `Stats.CriticalPathLength` as the realized value. The knob is
reserved for a future growth-heuristics/grammar layer.

`DungeonJson.SaveConfig`/`LoadConfig` and `SaveLayout`/`LoadLayout` round-trip both types to JSON (JSONC-
tolerant reads, deterministic byte-identical writes). The JSON matches the embedded schema
(`DungeonSchema.GetJson()`, a `oneOf` over a `config` and a `layout` `$def`), which the package's tests
validate against and which is available for editor/AI tooling. The load path itself enforces its own semantic
checks, throwing `DungeonJsonException` naming the offending field in its own camelCase spelling.

## Baking into a `MapDocument` (`DungeonMapDocEmitter`)

```csharp
using KhaozEngine.Dungeon;
using KhaozEngine.MapDoc;

DungeonKitMap kit = DungeonKitMap.Greybox();               // or your own kit ids, see below
var plot = new DungeonPlotTransform(originX: 120f, originZ: 0f, baseY: 0f, yawRadians: 0f);

var target = new MapDocument { Id = "dungeon-01", DisplayName = "dungeon-01" };
DungeonMapDocEmitter.Emit(layout, kit, plot, target);      // spawnArchetypeId defaults to "dungeon-spawn"
MapDocumentFile.Save(target, "dungeon-01.map.json");
```

`Emit` appends: never clears or replaces anything already in `target`, so a document can accumulate several
dungeon bakes (different layouts, or the same layout at different plots) or dungeon content alongside
hand-authored content. Placements, spawns, and marker regions carry the "dungeon" tag, spawns and marker
regions add a `floor:<n>` tag, and room regions additionally carry "room" and the room's type (lowercased). Every id is salted per `(layout, plot)` pair, so repeated bakes
into the same document never collide.

## Runtime stamping (`DungeonStamp`)

```csharp
using KhaozEngine.Dungeon;

DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);
// stamp.Props: one DungeonPropInstance (KitId, X, Y, Z, Yaw, Scale) per piece - hand these to your scene loader.
// stamp.Statics: merged (PhysicsShape, Pose) pairs - one per wall run, one per floor-slab run, a run of solid
// box steps per stair, plus one per ceiling run when Roofed - register each with IPhysicsWorld.AddStatic(shape, pose).
```

`DungeonStamp.Build` shares its cell-to-piece mapping with `DungeonMapDocEmitter` (both go through the internal
`PieceMapper`), so `stamp.Props` is identical to what a bake of the same layout/kit/plot would place. Statics
are greedy axis-run merges, not one shape per tile: a contiguous wall run along a row becomes one `BoxShape`,
same for a contiguous walkable floor run (stair-tread cells are excluded, they are covered by their stair's step
boxes instead). Each stair run climbs one floor over a three-cell run as a row of solid upright box steps (riser
under the default step-up height, so the character mounts every tread; a single pitched ramp box is not walkable
from a flush floor), matching the greybox stair mesh. See `KhaozEngine.Showcase`'s "Dungeon (walk)" room
for a wiring example (generate once, stamp, load the kit meshes, spawn the player at the `Entrance`
marker). Note the demo wires rendering and the walk camera only, it does not register the physics statics.

## Kit contract (`DungeonKitMap`)

The generator never references kit content directly. A sink resolves each `DungeonPiece` to a kit id through
a `DungeonKitMap`:

```csharp
var kit = new DungeonKitMap();
kit.Map(DungeonPiece.Floor, "my_floor_tile");
kit.Map(DungeonPiece.Wall, "my_wall_tile");
kit.Map(DungeonPiece.DoorFrame, "my_doorframe");
kit.Map(DungeonPiece.StairUp, "my_stair");
kit.Map(DungeonPiece.StairDown, "my_landing");
kit.Map(DungeonPiece.Ceiling, "my_ceiling");   // only emitted when CeilingMode is Roofed
```

`Require(piece)` throws `InvalidOperationException` naming the missing piece if a mapping is absent, so a
partially-configured kit fails loudly at bake/stamp time rather than silently dropping content.
`DungeonKitMap.Greybox()` maps all six pieces to the placeholder ids `dungeon_floor`/`dungeon_wall`/
`dungeon_doorframe`/`dungeon_stair`/`dungeon_landing`/`dungeon_ceiling`, matching the committed greybox kit under
`KhaozEngine.Showcase/assets/dungeon/` (glTF pieces plus `dungeon.manifest.json`, loaded through
`KhaozEngine.Render3D`'s `AssetManifest`/`PropLoader`) - useful for tests and early integration before real
content exists.

## Wide corridors and halls

Corridors default to 1-tile single-file connectors. Open the width range to carve grand, multi-tile halls,
and turn on halls for elongated grand-connector rooms:

```csharp
var config = new DungeonConfig
{
    CorridorMinWidth = 2,        // default 1/1 (single-file, exact back-compat)
    CorridorMaxWidth = 5,
    HallChancePercent = 30,      // default 0 (no halls)
    HallMinLengthTiles = 10,     // hall long-axis span (only used when the chance is positive)
    HallMaxLengthTiles = 18,
};
```

When the width range is open, growth corridors and loop corridors carve a straight rectangular tube (a
constant perpendicular band from one room edge to the other) and their door frames become multi-cell
openings. The drawn width is capped to the narrower of the two room edges the corridor spans, so a wide
draw still places against a smaller room by narrowing, and a loop corridor falls back to a 1-wide edge when
a wide band would not fit (so a loop still forms). A hall is a `DungeonRoomType.Hall` room whose long axis
runs along the corridor that reached it and whose girth is a normal room span, so it is provably longer than
any `RoomMaxTiles` room. When halls are enabled the plot must fit `HallMaxLengthTiles + 2` on both axes.

Both are deterministic: the width and hall decisions draw from the `rooms` RNG stream only when their range
is open, so a default config (`CorridorMinWidth == CorridorMaxWidth == 1`, `HallChancePercent == 0`) consumes
no extra randomness and reproduces every existing seed byte-for-byte (`DungeonLayout.LayoutHash`). The
per-cell sinks and `DungeonSolver` handle wide corridors and halls with no extra work.

## Roofed interiors (`CeilingMode`)

By default a dungeon is open-top (roofless, as seen from above). Set `DungeonConfig.CeilingMode = Roofed`
to have both sinks roof it so it reads as an enclosed cave:

```csharp
var config = new DungeonConfig
{
    MaxFloors = 2,
    CeilingMode = DungeonCeilingMode.Roofed,   // default is Open
    CeilingHeightMeters = null,                 // null -> FloorHeightMeters (flush with the floor above)
};
```

A ceiling (`DungeonPiece.Ceiling`) is placed over every walkable cell at `floorY + CeilingHeightMeters`,
EXCEPT where a walkable cell OR a `StairVoid` headroom cutout sits directly above at the same XZ - so the whole
stair shaft stays open (a `StairVoid` sits above every tread, the headroom the steps climb through), and where the
floor above already has its own slab that slab is the roof (no double geometry).
`DungeonStamp` additionally emits greedy-merged ceiling collision slabs (same pattern as the floor slabs,
lifted a ceiling height and facing down). This is a pure sink-time geometry choice: it never changes the
generated layout structure, so `Open` and `Roofed` layouts from the same seed share a `LayoutHash` and
`Open` output is byte-for-byte the pre-ceiling output.

## CLI (`ke-dungeon`, `tools/KeDungeon`)

A dev CLI over this package. Four verbs, exit codes 0 (success), 1 (a failed `verify`), 2 (unknown verb or a
missing/invalid option), 3 (malformed input JSON):

```bash
dotnet run --project tools/KeDungeon -- generate --seed 2026 --out layout.json
dotnet run --project tools/KeDungeon -- generate --seed 2026 --config dungeon.config.json --out layout.json
dotnet run --project tools/KeDungeon -- preview --layout layout.json --out-dir preview/
dotnet run --project tools/KeDungeon -- verify --layout layout.json
dotnet run --project tools/KeDungeon -- bake --layout layout.json --map zone.map.json \
    --origin-x 120 --origin-z 0 --base-y 0 --yaw 0
```

`generate` prints the `LayoutStats` summary and writes the layout JSON. `preview` renders one 8px-per-cell PNG
per floor for quick visual inspection (a fixed debug palette, not game content). `verify` re-runs
`DungeonSolver.Verify` and prints every error to stderr on failure. `bake` always uses `DungeonKitMap.Greybox()`
and loads the target map document if it already exists (so repeated bakes accumulate), otherwise creates a
fresh one.

## Determinism and completability

- **Deterministic.** Every random draw goes through `DeterministicRng` streams derived from the seed by name
  (`"rooms"`, `"gating"`, `"markers"`), each only consumed when that phase actually needs randomness (a
  `LockCount = 0` config, for example, draws nothing from the gating stream). The same config and seed always
  produce a byte-identical layout, verified by `DungeonLayout.LayoutHash` (a cross-platform-stable FNV-1a 64
  fold over the raster, rooms, edges, keys, and markers).
- **Completable by construction.** Every room is carved together with its connecting corridor or stair and
  that connection's doors in one atomic step, so a partially-built layout is never returned mid-carve.
- **Always re-proven.** `DungeonSolver.Verify` runs a cell-level flood fill from the entrance (collecting keys
  and unlocking their doors as it goes) plus four structural checks (edge cell kinds match the edge's kind,
  every key matches exactly one locked edge, room ids are unique, and no key sits behind its own lock).
  `DungeonGenerator.Generate` calls it on every generated layout and throws `InvalidOperationException` (with
  every reported error) if it fails, so an un-completable dungeon can never leave the generator. `Verify` is
  pure and read-only, so a hand-edited or hand-authored layout can be checked the same way.

Depends on `KhaozEngine.Primitives`, `KhaozEngine.MapDoc`, and `KhaozEngine.Physics`. In the `Foundation`
umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
