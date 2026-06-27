# Bounded play area design (`RimFeature` + `WorldBounds`)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Area: engine (Terrain + NetWorld) — the bounded-zone mechanism every designed area needs

## Context

The overworld engine track is complete (terrain → walkable → forest → networked → streaming →
sharding) and persistent. The terrain authoring model is feature composition:
`TerrainConfig { Seed, WaterLevel, BiomeBand[], ITerrainFeature[] }` with `LakeFeature`,
`RidgeFeature`, `FlattenFeature`. Ruinborne's first playable is a **bounded starting zone** — a town
on a plateau, a lake, the whole play area ringed by impassable mountains with one pass (the road out).
A greybox of exactly this was built live in Blender (this session) as the design target.

The town (`FlattenFeature` + placed buildings) and lake (`LakeFeature`) already map onto existing
features. The **one missing capability is a bounded play area** — there is no border/bounds/rim concept
anywhere in `Terrain`/`NetWorld`/`Sharding` today. This spec adds it, engine-first, because every
designed zone (start towns, future hubs, arenas, the handcrafted underground areas) wants borders.

A good border is **diegetic** (you see why you can't pass — mountains, cliffs, water) *and* **enforced**
(the authoritative server won't let you). So two complementary pieces.

## Components

### 1. `RimFeature : ITerrainFeature` — `KhaozEngine.Terrain`

Raises terrain into an enclosing wall around a bounded region (the visual/soft border). Packaged from
the Claudecraft "raise the world rim so the player stays in bounds" pattern and the greybox's rim logic.

```csharp
public sealed class RimFeature : ITerrainFeature
{
    // circular play area: flat inside InnerRadius, ramps to WallHeight by OuterRadius
    public Vector2 Center; public float InnerRadius, OuterRadius, WallHeight, Ruggedness;
    public RimPass[] Passes;            // gaps in the wall (roads out): each a direction + width
    public float Apply(float x, float z, float h); // adds rim height by distance past InnerRadius, reduced at passes
}
public readonly struct RimPass { public float AngleRadians, HalfWidth, Falloff; }
```

- Inside `InnerRadius`: unchanged. Between inner and outer: smoothstep ramp up to `WallHeight`
  (× a coordinate-hash `Ruggedness` for a jagged crest, not a smooth berm — reuse `TerrainNoise`).
- `Passes` cut corridors through the wall (lower the rim along a heading) so a road can leave.
- MVP is a **circular** rim; a rectangular variant (`RimRectFeature`) and arbitrary polygons are the
  obvious extensions — design `Apply` around a "distance to the play-area boundary" so the shape can
  generalize later.

### 1b. Slope-gate wiring — the rim only works as a border if it can't be climbed

`CharacterMovement.Step` (Locomotion) ALREADY has a slope gate
(`MoveTuning.MaxSlopeRadians`, default 50°: `if (acos(groundNormal.Y) > MaxSlopeRadians) blocked`), but
it only runs when a `groundNormal` delegate is passed, and **nothing passes it today** — `TerrainWalkSample`
and the controllers call `Update(... , GroundHeight)` with no normal, so `groundNormal` is null and the
player walks straight up cliffs. This is NOT a missing physics system; the gate is unwired.

Fix: expose `TerrainCollision.GroundNormal(x,z)` (it already has `SampleNormal`/`IsWalkable`), and pass it
as the `groundNormal` delegate everywhere movement runs: `CharacterController3D` (local + prediction) AND
the server sim (`PlayerMoveSimulator` → `WorldServer`/`ShardedWorldServer`), so the gate is **authoritative**
(a hacked client still can't climb the rim). Tune `MaxSlopeRadians` down (50° is steep) so the rim mountains
exceed it and block. `blocked` currently stops the XZ move entirely — keep that, but consider slide-along
(project the move onto the walkable direction) as a feel follow-up. With this wired, the rim is a real
diegetic border; `WorldBounds` is the belt-and-braces hard stop behind it.

### 2. `WorldBounds` — `KhaozEngine.NetWorld`

The hard, authoritative border: a play-area shape the movement **clamps** to each tick, so the rim can't
be climbed/glitched past (the server is authority).

```csharp
public abstract class WorldBounds { public abstract bool Contains(float x, float z); public abstract Vector2 Clamp(float x, float z); }
public sealed class CircleBounds : WorldBounds { public Vector2 Center; public float Radius; }
public sealed class RectBounds   : WorldBounds { public float MinX, MinZ, MaxX, MaxZ; }
```

Wired into the movement step (`PlayerMoveSimulator` / `WorldServer` / `ShardedWorldServer`): after
`CharacterMovement.Step`, clamp the new XZ into `WorldBounds` (slide along the edge, don't hard-stop, so
movement stays smooth). `Passes` in the rim correspond to openings in the bounds (or the bounds is
simply larger than the walled region until the pass content exists). Optional/nullable — no bounds =
today's unbounded behaviour.

The two compose: `RimFeature` makes the edge *look* enclosed; `WorldBounds` sits at/just inside the wall
and *guarantees* it.

## Testing (headless)

- **RimFeature**: height is unchanged inside `InnerRadius`, ramps up to ~`WallHeight` by `OuterRadius`,
  and a `RimPass` corridor stays low (a gap); deterministic; composes with `Lake`/`Flatten`.
- **WorldBounds**: `Contains`/`Clamp` correct for `Circle` + `Rect`; a point outside clamps onto the
  boundary; clamping is idempotent inside.
- **Slope gate**: with the `groundNormal` delegate wired, a player driving at a slope steeper than
  `MaxSlopeRadians` is blocked (cannot climb the rim); a gentle slope is walkable; the server sim gates
  identically to the client (authoritative) so prediction stays consistent.
- **Movement integration**: a player driving into the wall is held inside (slope gate + `WorldBounds`);
  driving through a pass region is allowed; neither breaks prediction/reconciliation.

## Scope

### In scope

- **Slope-gate wiring**: `TerrainCollision.GroundNormal(x,z)`, passed as the `groundNormal` delegate
  through `CharacterController3D` (local + prediction) and `PlayerMoveSimulator` →
  `WorldServer`/`ShardedWorldServer` (authoritative); tune `MoveTuning.MaxSlopeRadians` so the rim blocks.
- `RimFeature` (+ `RimPass`) in `KhaozEngine.Terrain`; circular MVP, `Apply` shaped for later
  rect/polygon generalization.
- `WorldBounds` (`CircleBounds` + `RectBounds`) in `KhaozEngine.NetWorld`, clamped in the movement step
  of `WorldServer` and `ShardedWorldServer` (nullable; off = current behaviour).
- A demo/preset showing a bounded zone (e.g. a `TerrainPresets.BoundedClearing()` or a bounds-on flag in
  `TerrainWalkSample`) so the walk sample can't leave the play area.
- Headless tests; additive **minor** version bump; docs (CLAUDE.md package map note, USING usage
  section, CHANGELOG/CHANGENOTES, guard declarations).

### Out of scope (named)

- **Town / building content, the specific RuinborneStartZone config, NPC/spawn data** — game content
  (Ruinborne), composed from these features.
- **Rectangular/polygon rim** beyond the circular MVP (extension; `WorldBounds` ships circle + rect, the
  rim ships circular).
- **Water shader, PBR rock on the rim** — visual polish, separate.
- **Navmesh, gates/doors at passes, zone-transition on reaching a pass** — later.
- **Static prop / building collision** (capsule-vs-prop colliders so you can't walk through trees or
  the inn), **gravity / falling / jump**, and **step-height over small ledges** — these are the NEXT
  character-physics features (a separate sub-project, needed once the CC0 buildings are placed in the
  town). This spec only wires the slope gate that stops cliff-climbing; it is not a general physics
  system. Named here so it is not forgotten.

## Engine-first

`RimFeature` (Terrain) + `WorldBounds` (NetWorld) are the reusable engine mechanism; Ruinborne composes
them into its `RuinborneStartZone` `TerrainConfig` + building list + bounds. Independent of the in-flight
perspective-outline and sharding work (different files), so it can run as a concurrent engine chat.

## Open items to confirm during implementation

- Circular-only rim for the MVP vs ship a rect variant too (the bounds ships circle + rect regardless).
- Clamp-and-slide vs reject-the-move at the bounds (prefer clamp-and-slide for feel).
- How a rim `Pass` and a `WorldBounds` opening stay consistent (one source of the play-area shape, or
  the bounds is authored to match the rim).
- Where the bounded demo lives (a preset + a `TerrainWalkSample` flag, or a small new sample).
