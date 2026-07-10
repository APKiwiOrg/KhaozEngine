# Procedural Dungeon Generator (KhaozEngine.Dungeon)

Design approved 2026-07-10. Fills the "Procedural dungeon generator" roadmap item.

## Summary

A render-free, deterministic, multi-level dungeon generator shipped as a new Foundation-tier package
`KhaozEngine.Dungeon`. The generator is a pure function (config + seed in, dungeon-local layout out)
whose output is provably completable by construction and re-verified by an always-on solver. Two sinks
consume the layout: a MapDoc bake (static, editor-visible content) and a runtime stamp (placements plus
3D static collision for an authoritative server). A thin CLI exposes generate/preview/verify/bake for
collaborative human+AI dungeon building, with verbs designed to be absorbed by the planned `ke-mapedit`
MCP server.

## Decisions

1. **World model: far-corner same-grid for V1, true instances as the end state.** Dungeons are stamped
   at reserved far-away XZ regions of the existing world grid and entered via teleport, so
   sharding/AoI/persistence work unchanged. The recorded end state is first-class instanced world
   spaces (see the instancing item under "Netcode / MMO refinements" in ROADMAP.md). Everything the
   generator emits is dungeon-local with a single placement transform applied at sink time, so the same
   output drops into true instances later without rework.
2. **Geometry: multi-level from V1.** Stairs, stacked floors, vertical layouts. Collision rides the 3D
   physics static path (`PhysicsShape` via `IPhysicsWorld.AddStatic`). The 2D XZ collision layer cannot
   express stacked floors and is not used for dungeon interiors.
3. **Emit model: pure library, two sinks.** Generation is deterministic and side-effect free. Bake and
   stamp are separate consumers of the same `DungeonLayout`.
4. **Content scope: layout + gating + markers.** The generator owns rooms, corridors, stairs, doors,
   lock/key/boss gating, and typed markers (spawn/loot/objective) as pure tagged data. Gameplay balance
   (what spawns, what drops) stays game-side per the engine-first boundary.
5. **Algorithm: incremental grow-and-embed** (chosen over mission-graph-then-embed and BSP-plus-gating,
   trade-off table below).
6. **Co-op build surface is a first-class deliverable**: JSON round-trip for config and layout, a
   `ke-dungeon` CLI, and a 1:1 verb mapping reserved for the `ke-mapedit` MCP server.

### Approach trade-off (recorded)

| Criterion | Grow-and-embed | Graph-then-embed | BSP + gating |
|---|---|---|---|
| Completability guarantee | 10 | 9 | 8 |
| Determinism / bounded runtime | 9 | 6 | 9 |
| Macro-structure control | 7 | 9 | 5 |
| Implementation simplicity | 7 | 5 | 9 |
| Extensibility | 8 | 9 | 6 |
| **Total** | **41** | **38** | **37** |

Grow-and-embed wins on the two properties that matter most for an authoritative MMO server: structural
completability (an edge only exists if its geometry was committed with it) and a single bounded
deterministic pass. The macro-control gap versus a mission grammar is recovered with growth heuristics
(critical-path length target, loop budget, rooms-per-floor) and a grammar layer can be added on top
later without discarding the embedder.

## Package

- **`KhaozEngine.Dungeon`**: render-free, GPU-free, pure .NET. Foundation umbrella member.
- **Deps**: `Primitives` (DeterministicRng, hashing), `MapDoc` (bake sink), `Physics` (the
  dependency-free shape seam, for emitted static collision).
- No public type named `Dungeon` (same shadowing lesson as `Sharding` vs a `World` package).

## Core model

```csharp
DungeonLayout DungeonGenerator.Generate(DungeonConfig config, ulong seed)
```

Pure, deterministic, bounded. One `DeterministicRng` split via `CreateDerived("rooms")`,
`CreateDerived("gating")`, `CreateDerived("markers")` so tuning one subsystem does not reshuffle the
others.

**`DungeonConfig`** (validated at Generate entry): tile cell size in meters, floor height in meters,
room count target, room size range in tiles, max floors, plot bounds in tiles, critical-path length
target, loop-edge budget, lock/key pair count, boss room toggle, marker density ranges.

**`DungeonLayout`** (all coordinates dungeon-local, tile space, entrance at origin, floor 0):

- Rooms: id, floor index, tile rect, type tags (entrance, boss, key, treasure, normal).
- Edges: corridor tile paths or stair cell pairs, door positions, optional lock id.
- Resolved 3D tile grid: per (x, z, floor) cell one of floor/corridor/wall/stair/door.
- Gating: lock ids on edges, key placements in rooms.
- Markers: typed positions (spawn, loot, objective, entrance) with tags. Pure data, game interprets.
- `LayoutStats`: rooms requested vs placed, critical-path length, floor count, saturation info.

World coordinates never appear in the layout. Sinks apply a plot transform (origin XZ, base Y, yaw).
This is the instance-readiness property.

## Algorithm (grow-and-embed)

1. Entrance room at grid origin, floor 0.
2. **Grow**: pick a frontier room deterministically, attempt to place a new room adjacent in free grid
   space, connected same-floor by a corridor or cross-floor by a stair cell pair. The edge and its
   geometry are committed atomically, so the graph never contains an unrealized connection. Candidate
   positions enumerate deterministically with hard attempt caps. A tight plot yields fewer rooms
   (recorded in stats), never a hang and never a throw.
3. **Loop edges**: after the tree is complete, spend the loop budget on extra corridors between
   spatially adjacent rooms that are graph-distant.
4. **Gating**: boss room is the farthest room on the critical path. For each lock edge, compute the
   region reachable from the entrance with that lock closed and place its key strictly inside that
   region. Key-before-lock is guaranteed by construction.
5. **Markers**: per room, driven by room type tags, own RNG stream.
6. **Solver proof, always on**: `DungeonSolver.Verify(layout)` flood-fills entrance to boss respecting
   gating and checks every key precedes its lock. `Generate` runs it on every call and throws
   `InvalidOperationException` on failure (unreachable by construction, so failure means an engine bug
   and must be loud). The solver is public so tests, the editor, and games can re-verify any layout.

## Sinks

**MapDoc bake: `DungeonMapDocEmitter`.** Layout + `DungeonKitMap` + plot transform appended into a
`MapDocument`: kit-piece `MapPlacement`s with explicit frozen Y per floor (the `BakeRegionCommand`
precedent), rooms as tagged `MapRegion`s, spawn markers as `MapSpawn`s, non-spawn markers (loot,
objective, entrance) as small tagged disc `MapRegion`s, and a `FlattenFeature` under the plot
footprint. Output is inspectable and editable in the MapEditor.

**Runtime stamp: `DungeonStamp`.** Same inputs, world-ready output for a server or client at runtime:
the placement list (rendered via the existing instanced kit-prop path) and static collision as
`PhysicsShape`s (greedy-merged wall boxes per floor into `CompoundShape`s plus floor slabs) for
`IPhysicsWorld.AddStatic`.

**Kit contract.** The layout speaks an abstract piece vocabulary. V1 minimum set: Floor, Wall,
DoorFrame, StairUp, StairDown (additions are additive, each needs a kit mapping and an emitter rule).
`DungeonKitMap` maps each piece to a manifest kit id
sized to the tile cell, throwing on a missing mapping with the piece name. The engine ships a minimal
CC0 greybox dungeon kit in Showcase assets so the whole path is testable and demoable engine-side.
Games swap in real kits via the same asset-manifest contract.

## Co-op build surface (CLI now, MCP later)

1. `DungeonConfig` and `DungeonLayout` get JSON serialization with schema validation, MapDoc-style.
2. `ke-dungeon` CLI at `tools/KeDungeon` (repo tools convention, references `KhaozEngine.Imaging` for
   PNG previews) with verbs:
   - `generate`: config + seed to layout JSON plus stats.
   - `preview`: per-floor top-down PNGs of the tile grid via `KhaozEngine.Imaging.PngWriter` (no GPU).
   - `verify`: run `DungeonSolver` on a layout JSON.
   - `bake`: emit into a `MapDocument` at a plot transform.
3. The planned `ke-mapedit` MCP server absorbs these as verbs 1:1 when it lands. Nothing thrown away.

## Error handling

- Bad config: `ArgumentException` at `Generate` entry.
- Tight fits: graceful degradation, fewer rooms, honest `LayoutStats`, hard attempt caps, no throw.
- Unsolvable layout: `InvalidOperationException` from the always-on solver (engine bug, loud).
- Missing kit mapping: emitter throws naming the piece.
- JSON load: schema validation errors name the offending field.

## Testing (headless xUnit, no GPU goldens)

- Determinism: same seed + config twice produces structurally identical layouts (stable layout hash).
- Seed sweep: ~1000 seeds across several configs, every layout solver-verified (reachability,
  key-before-lock, tiles-in-bounds, stair-pair consistency).
- Edge cases: one room, zero locks, max floors, plot barely fitting the entrance.
- Degradation: oversized room budget on a small plot yields fewer rooms and honest stats.
- Bake round-trip: emit to `MapDocument`, save, load, placements identical.
- Stamp: merged wall boxes exactly cover wall tiles, floor slabs per level, shape counts sane.
- Preview: PNG output has expected dimensions and non-empty content.

## Docs and release

Standard ritual, one batch in a `feature/dungeon-gen` worktree: README catalog row + Foundation
umbrella table, package README (ships in the nupkg), USING-KHAOZENGINE section, DEPENDENCY-SEAMS for
the new package edges, CHANGELOG entry, single minor version bump at batch end, merge + tag + push +
pack per the release ritual.

## Out of scope / follow-ups

- **Instanced world spaces**: recorded end state, roadmap item under "Netcode / MMO refinements". The
  generator's dungeon-local output adopts it when it lands.
- **Editor integration**: an undoable `GenerateDungeonCommand` in MapEditor (the emitter makes this
  trivial).
- **Grammar/mission layer** on top of the embedder for declarative macro-structure control, if growth
  heuristics prove insufficient.
- **Ruinborne adoption** (game-side): engine pin bump, far-corner plot allocation, teleport hook, and
  verifying authoritative movement resolves against the 3D physics world inside dungeons.
- **Decoration passes** (WFC-style interior detailing, themed clutter): possible later layer, never the
  structural generator.
