# KhaozEngine.SegmentRig

Render-free pose core for **rigid-segment characters**: a body is a set of rigid pieces (forearm, shin,
torso) posed every frame by procedural code rather than by a skinned animation clip. This package answers
with transforms and angles and draws nothing at all.

It is the visual counterpart of `KhaozEngine.Locomotion`, which owns `MoveState`. Locomotion decides where a
body is. This decides what it looks like there.

## What it does not depend on

Nothing. No `Render3D`, no `TileWorld`, no `Netcode`, no `Gpu`, not even `Primitives`. Just `System.Numerics`.
In the `Foundation` umbrella beside `Locomotion`.

## No tick counts, ever

It has to serve a world that updates a handful of times a second and one that updates every frame, so every
number here is **seconds or a normalized 0 to 1 phase**. There is no tick count in any signature and no
assumption about update rate. A cadence is a `float` of seconds.

## API

- **`BodyPose(Vector3 Position, float Yaw)`** - where a body is drawn and which way it faces. World metres
  and radians. Hand over the position you are DRAWING at (the presented or interpolated one), not the
  committed authoritative one, so the cycle follows the glide instead of stepping once per update.
- **`BodyRig`** - one two-legged body's proportions: the four joint pivots, the hand socket, the neck base,
  the rest height and the stride. `BodyRig.Human` is the reference proportion, `Scaled(f)` derives another
  size, and `Body` / `Torso` / `Limb` / `Elbow` / `Knee` build the frames a composition hangs pieces off.
- **`QuadrupedRig`** - the four-legged sibling: two shoulder and two hip pivots, the carpus and hock hinges,
  the poll, plus `Body` / `Torso` / `Trunk` / `Strike` / `ForeHinge` / `HindHinge` / `Head`.
- **`WalkPose`** - the 18 positional float channels one body is drawn at. See the contract below.
- **`WalkCycle`** - the distance-driven walk. `Advance(position, dt)` accumulates phase off ground covered,
  `Pose` reads the two-legged shape, `Sample` reads the shape-free `GaitSample` a four-legged body takes.
- **`IdleBreath`** - the standing-still cycle: torso rise and tip, an arm sway a quarter cycle behind it, a
  held break at both elbows, and a solved rest stance that keeps each sole on the floor under its own hip.
- **`QuadrupedGait`** / **`QuadrupedPose`** - the four-beat lateral-sequence walk off the same phase.

## The pose contract

`WalkPose` is **positional**, so a new channel is APPENDED and never inserted. A game holds poses of its own
and builds them positionally, and inserting a channel would silently re-point every one of them at the wrong
number. The current tail is `RootPitch` and `RootRoll`, the pair a body with no ground under it tips about
its own root on.

Everything past the first four channels is defaulted, and each is written by a small number of cycles: a walk
writes the limbs, the bob and the lean, a breath writes the torso rise and tip and the rest stance, a stroke
writes the arm yaws and the wrists, and only air or water writes the two root angles.

## Adding a pose of your own

A pose is a **static class with `PoseAt(...)` and `Compose(in WalkPose, ..., float weight)`**, and the order
poses are laid in lives in the game's own chain, not here. A jump, a fall, a swim, a wade, a strafe, a
backpedal and a turn in place are all game-side poses written that way over these channels.

```csharp
public static class SwimStroke
{
    public static WalkPose PoseAt(float phase) => new WalkPose(
        LeftArm: ..., RightArm: ..., LeftLeg: ..., RightLeg: ...,
        RootPitch: 1.4f);   // lying out flat, which is what the root channels are for

    public static WalkPose Compose(in WalkPose pose, in WalkPose stroke, float weight) => ...;
}
```

## Driving a cycle with no ground under it

`WalkCycle.Advance(position, dt)` derives phase from HORIZONTAL ground distance and discards y, because a
metre of climb is not a metre of walk. A swim or a fall covers no ground, so it would stand frozen. The
explicit phase source is the second overload:

```csharp
// Swimming: two strokes a second, no ground covered at all.
cycle.Advance(phaseDelta: 2f * dt, dt, moving: true, running: false);

// Falling: hold the legs where they are, keep the blend alive.
cycle.Advance(phaseDelta: 0f, dt, moving: false);
```

It also FORGETS the last sampled position, exactly as `Teleport()` does, so the first ground-driven call
after a swim does not read the whole swim as one frame of sprinting. Alternating the two overloads is safe
with no extra call at the seam.

## Headless by construction

No scene, no clock, no renderer, no allocation per frame. Build a rig, advance a cycle, assert on the
transforms.
