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
- **`HumanoidSkeleton`** / **`QuadrupedSkeleton`** - the composers: ordered piece names, one transform per
  piece into a caller's span, and the rest offsets those transforms land on at a zero pose. See below.
- **`SegmentSockets`** - the wrist and off-hand turns a held piece rides, the hand chain that carries it, and
  a containment check for a socket against a measured box. See below.

## The strokes

A stroke is a one-shot or looping action pose laid over whatever the cycles left the body at. Every one of
them is a static class with `PoseAt(...)` and `Compose(...)`, and the order they are laid in is the game's.

- **`AttackTrajectory`** - the clock every fighting stroke shares, and the only one: recover off the blow,
  stand still, strike late and fast. `PoseAt(phase, impact, rest)` blends two end poses over it, the four-key
  overload `PoseAt(phase, impact, rest, key, keyFraction)` splits the strike alone for a stroke that passes
  through a key on the way, and `AmountAt(phase, out landing)` reads how much of the blow is carried for a
  stroke whose channels are not an arm's.
- **`AttackSwing`** / **`AttackPose`** - the EMPTY-hand punch, on the weapon arm alone. The three phase
  boundaries (`ImpactPhase`, `RestPhase`, `StrikePhase`) are fractions of a cadence and every stroke below
  reads them. `HoldSecondsFor(cadenceSeconds)` and `RecoverySecondsFor(cadenceSeconds)` turn a cadence into
  an envelope.
- **`SlashSwing`** - the ARMED cut, the same clock and a different shape: carried at the side, then out to
  the weapon side and across, both inside the strike.
- **`AttackStyle`** / **`AttackStyles.PoseAt(style, phase)`** - the dispatch between those two. Which style a
  body throws is the GAME's to decide off whatever is in the hand.
- **`ChopSwing`** / **`ChopPose`** - the tool stroke: wound back slowly over the shoulder and brought down
  hard and sideways into the target, `HoldPhase` and `LiftPhase` its own two boundaries.
- **`ProcessingSwing`** - the restrained two-handed working pose, one `Compose(pose, phase, atStation,
  weight)` and no pose type of its own.
- **`BlockRaise`** / **`BlockPose`** - the flinch a plate in the off hand answers a blow with. An AGE in
  seconds rather than a phase, because it is a reaction and does not wrap: `WeightAt(ageSeconds)` is the
  envelope, `StanceAt(weight, rig)` solves the braced legs so both soles stay planted, and `PoseAt` carries
  the envelope itself.
- **`Headbutt`** / **`HeadbuttPose`** / **`QuadrupedStrike`** - the four-legged strike, on the punch's clock,
  and the only thing that writes `QuadrupedPose.Surge` and `Pitch`. `PoseAt(phase, rig)` solves every leg so
  the hooves stay where they stood.

## Cadence is always a caller's number

A cadence is a `float` of SECONDS and it is always a parameter. The package has no cadence of its own, no
default, and no idea how often a game updates: `AttackSwing.HoldSecondsFor(2.67f)` is the whole interface. A
game that derives a cadence from its own update rate does that arithmetic on its own side and hands over the
seconds.

Everything else is a normalized phase, 0 to 1, with 0 and 1 both the blow. Values outside it are WRAPPED and
a value that is not a number is the blow, so a caller may hand over a raw accumulator.

## Composing a stroke

`PoseAt` first, then `Compose(in WalkPose, ..., weight)`, and the chain's ORDER is the game's:

```csharp
WalkPose frame = cycle.Pose;
frame = IdleBreath.Compose(frame, IdleBreath.PoseAt(clock, bodyId, rig), idleWeight);
if (swinging) frame = AttackSwing.Compose(frame, AttackStyles.PoseAt(style, swingPhase), swingWeight);
if (chopping) frame = ChopSwing.Compose(frame, ChopSwing.PoseAt(chopPhase), chopWeight);
frame = BlockRaise.Compose(frame, BlockRaise.PoseAt(secondsSinceBlow, rig));   // adds, costs nothing at zero
```

A stroke on the WEAPON arm REPLACES what that arm was doing, because it owns the arm for as long as it runs.
`BlockRaise` and `Headbutt` ADD instead, because a flinch lasts under half a second on an arm that is still
walking and replacing it would stop the cycle underneath dead. Both kinds cost exactly nothing at zero.

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

## The skeletons: a rig plus a pose to one transform per piece

`HumanoidSkeleton` and `QuadrupedSkeleton` are the composers. Each answers `PieceCount` transforms into a
caller-supplied span, in the order `PieceNames` declares, with a parent always ahead of its children. Nothing
is drawn, nothing is allocated, and the rest frame is available on its own so a game can measure a body's
bounds without composing one.

```csharp
Span<Matrix4x4> at = stackalloc Matrix4x4[HumanoidSkeleton.PieceCount];
HumanoidSkeleton.Compose(rig, pose, walk, at);
scene.Draw(myMeshes[HumanoidSkeleton.ForearmRight], at[HumanoidSkeleton.ForearmRight]);

Vector3 rest = HumanoidSkeleton.RestOffset(rig, HumanoidSkeleton.ShinLeft);   // where that piece sits at zero
```

- **`HumanoidSkeleton`** - ten pieces: `Torso`, the two upper arms, the two forearms, the two thighs, the two
  shins, and a `Head` piece riding the neck base. TWO parent frames: the thighs compose inside `BodyRig.Body`
  and everything above the hips inside `BodyRig.Torso`, so a breathing body moves its chest and its arms and
  leaves its feet planted. A forearm composes against its own upper arm and a shin against its own thigh,
  which is what makes a flex a hinge rather than a second swing from the shoulder.
- **`QuadrupedSkeleton`** - ten pieces: the `Trunk`, the `Poll`, four upper legs and four cannons, the legs in
  `QuadrupedGait.Leg` order from each base. The trunk and the poll ride `QuadrupedRig.Torso` through the trunk
  yaw, and the four legs ride the PLAIN `QuadrupedRig.Body` through the same yaw, so the hooves turn with the
  trunk and stay on the ground while the ribcage lifts.

**Two composers rather than one, deliberately.** The two bodies do not share a shape: the two-legged one has
two root frames, a shoulder yaw on each arm and a piece on the neck base, and the four-legged one has a trunk
yaw its legs take off a different frame, a poll, and a second pose type. Folding them into one data-driven
walk would need a parent table plus a per-piece selector over two unrelated rig types and two pose types,
which is more machinery than either method is. They share a vocabulary (`PieceCount`, `PieceNames`,
`Compose`, `RestOffset`) and nothing else.

A span shorter than `PieceCount` is refused up front rather than throwing part way through, and a longer one
is filled with the tail left alone.

## The sockets: what the two wrist channels mean

`SegmentSockets` is the held-piece half, and it is the whole of what the `RightWrist` and `LeftWrist` channels
mean.

- **`Wrist(radians)`** - the WEAPON hand's tip, about the hand's own x, applied after the piece's own
  orientation so it adds to whatever lean that orientation left it at. Positive carries the piece's head the
  way the body faces.
- **`OffTurn(radians, fist)`** - the OFF hand's turn, about the BODY's up axis through the fist, applied at
  the very end of the chain. That is what makes a plate's face independent of how far the elbow is folded: a
  pitch about x leaves the hand's own x alone.
- **`Held(rig, grip, forearm, tip, turn)`** - the whole five-factor product, so a game does not hand write it:
  `grip * Wrist(tip) * translate(HandFromElbow) * forearm * OffTurn(turn, fist)`.
- **`IsInside(socket, bounds)`** - whether a socket offset lands in a box, the sanity check a consumer makes
  against the fist its own mesh actually has.

```csharp
Matrix4x4 blade = SegmentSockets.Held(rig, myBladeGrip, at[HumanoidSkeleton.ForearmRight], tip: walk.RightWrist);
Matrix4x4 plate = SegmentSockets.Held(rig, myPlateGrip, at[HumanoidSkeleton.ForearmLeft], turn: walk.LeftWrist);
```

A GRIP ORIENTATION is not here and will not be. How far a blade leans out of a fist, which way a plate's face
starts, how a short haft sits: those are properties of a MESH, so they are content and stay with the mesh. The
two rotations above are properties of the RIG.

## Headless by construction

No scene, no clock, no renderer, no allocation per frame. Build a rig, advance a cycle, assert on the
transforms.
