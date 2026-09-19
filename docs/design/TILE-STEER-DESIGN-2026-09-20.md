# Tile steering: held-direction movement on the tile world

Status: approved by the owner on 2026-09-20. Ships in 19.7.0. Consumer: Grimhollow keyboard movement
(`docs/design/KEYBOARD-MOVEMENT-DESIGN-2026-09-20.md` in that repository).

## Problem

`KhaozEngine.TileWorld.Netcode` has one way to move a player: a click that names a goal, which the simulator
paths to. A keyboard player holds a direction instead. Faking that with `WalkTo(adjacent tile)` every tick is
wrong for two reasons. `WalkTo` searches, and an unreachable goal walks to the nearest reachable tile, so a
key held into a fence sends the body on a detour. It also costs one search per tick per steering player on the
server.

A held direction is its own intent and gets its own command kind.

## Decision

Add `TileCommandKind.Steer = 5`. A steer command carries one `TileDirection` and the move mode. It never
searches. It resolves one step at a time against the live collision map, at step boundaries only, and hands
that step to the same commit path a routed step uses.

Stateless was chosen over a held direction stored in `TileMoveState`. The stored form survives a lost packet
better, but it adds a replicated and persisted field and needs an explicit stop command because
`TileCommandKind.None` could no longer mean that the keys were released. The stateless form changes no state
layout, no snapshot and no persistence record.

## Wire

The frame stays 24 bytes: `[tag:1][seq:4][kind:1][goalX:4][goalZ:4][plane:1][mode:1][target:8]`.

- `kind` is 5.
- `target` carries the direction as its `TileDirection` byte value. `Kind` already decides what `Target`
  means, so this is the existing rule rather than a new pun.
- `goal` is zero and ignored.
- The decoder's kind bound moves from `InteractEntity` to `Steer`. A steer frame whose `target` is not one of
  the eight defined directions is rejected whole, the same answer an unknown kind gets.

An older decoder rejects kind 5 as unknown. A consumer moves its own game protocol version when it adopts the
pin, which is what refuses a mixed pair at the door.

## API

```csharp
public static TileCommand Steer(TileDirection direction, TileMoveMode mode);
public TileDirection SteerDirection { get; }   // valid only when Kind == Steer
```

`TileWorldClient` gains a held level beside its click event:

```csharp
public void SetSteering(TileDirection? direction, TileMoveMode mode);
public TileDirection? Steering { get; }
```

`Queue` stays the latest-wins event door for clicks. Steering is a level the head sets every frame from its
input. On a command tick, a queued click is sent as it is today. When nothing was queued and a direction is
held, the client predicts and sends `Steer(direction, mode)` instead of `Continue(RunMode)`. A head therefore
cannot lose a steering tick to frame timing, and a click always wins the tick it lands on.

The steering mode is carried with the level and is NOT adopted as `RunMode`. A head that lets a modifier key
invert the pace while steering passes the inverted mode here, and the saved toggle is what the next
`Continue` carries once the keys are released. `SetSteering(null, ...)` clears the level.

A pure helper maps input to a direction so every tile game shares one quantization:

```csharp
public static class TileSteering
{
    // right is right minus left, forward is forward minus back, each in -1..1. cameraForward is the camera's
    // world-space look direction. Only its ground-plane part is read, and it need not be normalized.
    // Returns null for no input, for opposite keys that cancel, or for a camera looking straight down.
    public static TileDirection? FromAxes(int right, int forward, Vector3 cameraForward);
}
```

It takes the look vector rather than a yaw angle so no consumer has to match an angle convention. The vector
is WORLD space, exactly what a camera's `Forward` answers. The helper owns the mapping from world X and Z to
tile directions, which is the tile world's own fact and is not the identity: `TileWorldSpace` maps tile north
to world -Z, so the helper converts the vector through `TileWorldSpace` before it does anything else. A consumer
that handed in a tile-space vector would get a silently north-south mirrored result.

In tile space it builds the ground-plane forward and right vectors, combines them by the axis pair and snaps
to the nearest of eight directions. The engine's cameras are right handed in world space, so screen right of a
camera with ground forward world `(wx, wz)` is world `(-wz, wx)`. Through the tile mirror that is tile
`(fz, -fx)` for tile forward `(fx, fz)`. An exact octant boundary resolves to the counter-clockwise neighbor on
a north-up map, which is what rounding the angle up does, so the result is stable. The helper runs on the
client only. Its output is an integer direction, so no float reaches the simulation.

## Simulator rules

On a tick whose command is `Steer`:

1. Intent is replaced exactly as `WalkTo` replaces it. `Route`, `InteractTarget`, `InteractDomain` and
   `CombatTarget` clear. `Mode` takes the command's mode. Steering is a walk for every rule that asks.
2. The direction is resolved only on a boundary tick: the body is standing, or the step in flight lands this
   tick. On any other tick the command has done its work in rule 1.
3. Resolution runs against `TileMoveState.Tile` with `TileCollision.CanStep` and the body's footprint:
   - the direction itself when it is open
   - for a diagonal that is blocked, the open one of its two axis steps, testing the Z axis step before the X
     axis step so both heads agree when both are open
   - otherwise nothing, and the body stands. It still turns to face the held direction, so a key pressed
     into a wall reads as an answer rather than as a dropped input
4. A resolved step is committed through the same method a routed step uses. `Start` is split so that the tile
   flip, `StepFrom`, `Facing`, the progress reset and `StepTotal = StepTicks.For(s.Mode)` live in one shared
   commit body with two callers: the route door and the steer door. No route is allocated for a steered step.
5. The two doors into a step keep their existing asymmetry. A step started from standing counts its first tick
   immediately. A step started on a landing tick begins at zero progress. A key therefore never costs a tick of
   standing still, exactly as a click never does.

Releasing the keys means the next command is `Continue`. The step in flight lands and nothing starts, so the
body never takes an extra tile. A command lost on a boundary tick costs one tick of standing, after which the
next steer command starts the step from the standing door.

`Accepts` answers true for a steer command with a defined direction. A steer has no plane to disagree with.

## Pace is one seam

A steered step and a routed step are stamped by the same `StepTicks.For(s.Mode)` line in the shared commit
body. The run gate is unchanged: the server's `Admit` downgrades a run the game's `CanRun` refuses, whatever
the command kind. A consumer adds no speed value to adopt steering.

This is also where a future per-entity pace would land. When a game needs a haste or slow effect, that one
lookup becomes a per-entity answer and clicks, steering, actors and chases inherit it together. It is not
built here because nothing drives it yet.

## Server admission

`TileWorldServer.AdmitCommand` gains a `Steer` case. A steer abandons a pending action the way an applied walk
does, so it calls `actions.Clear(slot)` and passes the command through. The fight-lock watch in the tick treats
`Steer` like `WalkTo`: it is a deliberate disengage, so a lock broken by it is not reported as a lost target.

`TileWorldClient.Admit` passes a steer through untouched, matching the server.

## Tests

Headless, in the tile netcode test project:

- Codec: round trip for all eight directions, rejection of an undefined direction byte, the kind bound, and the
  existing truncation and fuzz suites extended to kind 5.
- Cadence: a steered straight line commits tiles on exactly the ticks a routed straight line does, at walk and
  at run, from standing and across landings.
- Blocked steps: a straight step into a wall stands. A blocked diagonal slides along the open axis. A diagonal
  with both axes open but the corner blocked takes the Z axis step. A fully enclosed body stands.
- Release: `Continue` after steering lands the step in flight and starts nothing.
- Intent: steering clears a route, a pending interaction, a combat target and the action queue entry. A click
  after steering replaces it.
- Mode: a mode change lands at the next step start. A gated run steers at walk and counts on `GatedRunCount`.
- Loopback: a steering client runs with zero corrections on clean ground, and a divergent blocker produces the
  same single correction a routed walk does.
- `TileSteering.FromAxes`: the eight directions with the camera looking north, a table of look vectors across
  every octant boundary, a camera looking straight down, cancelled opposite keys and no input.

## Documentation

The package README and `docs/USING-KHAOZENGINE.md` gain the steering door beside the click door. The tile
netcode design's row in `docs/INDEX.md` notes the new command kind, and this document gets its own row.
`CHANGELOG.md` gets the 19.7.0 entry in the version commit.
