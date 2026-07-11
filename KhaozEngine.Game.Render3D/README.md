# KhaozEngine.Game.Render3D (5.x)

The 3D integration for `KhaozEngine.Game`, split out so a 2D game pulls **no 3D renderer**. Three pieces:

- **`GameApp3D : GameApp`** - a `GameApp` that builds a `Render3DSurface` and drives the 3D pass in the
  `OnRenderWorld` seam (before the 2D HUD). Subclass it and override `OnDraw3D(Scene3D)`.
- **`IGameScene3D`** - a `GameScene` implements this (in addition to deriving `GameScene`) to submit a 3D world
  pass. Keeps 3D out of the base `GameScene`.
- **`SceneManager.Draw3D(scene)`** extension - draws the visible scenes that implement `IGameScene3D`, the same
  visible set as `Draw2D`.

```csharp
sealed class MatchScene : GameScene, IGameScene3D
{
    public void OnDraw3D(Scene3D scene) { /* submit the board + entities */ }
    public override void OnDraw2D(SpriteBatch batch) { /* HUD */ }
}

// in the frame loop, between scene.Begin() and the 2D pass:
scenes.Draw3D(scene);
```

## CharacterAvatar - the turnkey third-person character

`CharacterAvatar` is the one object a game builds to get a moving, climbing, facing, animated, drawn character with
no per-game glue. It composes the three pieces that already exist - `CharacterController3D` (the body: walking,
slopes, smooth stair climbing, collision), `AnimatedCharacter` (the brain: idle/walk/run/jump/fall/swim clip
selection), and `CharacterFacing` (which way to face) - and wires them the way every game was re-wiring them by hand.

```csharp
// Build once (TryLoadGltf does the rig load: skinned-ingest, map clip names to states, scale to the capsule):
var controller = new CharacterController3D { CapsuleHalfHeight = 0.9f, CapsuleRadius = 0.4f };
controller.SetXZ(spawnX, spawnZ);
CharacterAvatar? avatar = CharacterAvatar.TryLoadGltf(scene, "assets/character/Player.glb", controller,
    onFailure: reason => Console.WriteLine($"rig load failed ({reason}); using a capsule"));

// Per frame - one call replaces the whole hand-rolled glue:
avatar?.Update(input, dt, cameraYaw, terrain.GroundHeight, terrain.GroundNormal, physics);
// ...then in the 3D pass:
avatar?.Draw(scene);
```

`Update` mirrors `CharacterController3D.Update` exactly, then internally: faces the INTENDED move direction (input +
camera, never the collision-slid velocity, so a scraped wall cannot spin the model), advances the animation off the
REAL collision-clamped horizontal speed plus the controller's grounded/vertical/swim state, and eases the facing at
`MaxTurnRate`. `Draw` renders the skinned mesh at the capsule's feet with that facing and the capsule-match scale.
`TryLoadGltf` returns `null` (never throws) on a missing/unreadable/skeleton-less/clip-less asset, so a game keeps a
greybox fallback. The composed pieces stay usable on their own - a movement-only game uses `CharacterController3D`
directly, a remote player uses `ReplicatedCharacterAnimators`, the facing math is the static `CharacterFacing` - the
bundle is the convenient default, never a requirement. Client-cosmetic: pose and facing never feed sim or netcode.

Discrete stair geometry snaps the physics height a whole riser per tick (most visible descending). The avatar eases
its DRAW height toward the physics height at a bounded rate (`RenderHeightSmoothRate`, default 6 m/s) so the model
glides instead of jolting, and exposes that as **`RenderPosition`** (physics X/Z + the smoothed height) - point the
follow camera at `RenderPosition` (not `Position`) so the camera glides on stairs too. Only grounded height eases; a
jump/fall snaps (crisp arc), horizontal is never smoothed (no input lag), and a teleport-sized jump snaps
(`RenderHeightSnapDistance`). Use `Position` (crisp physics) for gameplay/streaming/queries.

### CharacterFacing

The canonical facing math, so games stop re-deriving it (and stop hitting the wall-spin bug that velocity-steered
facing produces in tight spaces): `MoveAxis(input)` (the WASD axis, the single source `CharacterController3D` also
reads so move and facing never diverge), `IntendedMoveDirection(input, cameraYaw)` (that axis rotated into the camera
basis), `YawOf(direction)`, `TurnTowards(currentYaw, intendedDirection, maxTurnRate, dt)` (a bounded-rate,
shortest-path turn that holds facing when no key is held), and `WrapAngle`.

## ReplicatedCharacterAnimators

A render-free bridge that drives one skinned-character brain per networked entity from a per-frame list of
`CharacterSample`. By default it derives planar speed / facing / air state from the position stream (windowed, so a
plateauing position does not strobe the state). Richer constructors let the local player (or any entity whose
replicated `MovementState` is available) pass exact signals it already knows: `(id, position, isLocal, grounded,
verticalVelocity)` for the exact grounded flag + vertical velocity, and `(id, position, isLocal, grounded,
verticalVelocity, planarSpeed)` to ALSO drive the idle/walk/run state and clip-speed sync off the exact planar speed
(`WorldClient.LocalHorizontalSpeed`) instead of the finite-differenced render position - so the local avatar's
animation does not flicker walk&lt;-&gt;idle when it decelerates to a stop. A trailing `swimming` argument on the
exact-movement constructors (from the replicated `MovementState.Swimming` bit, surfaced on `EntityRenderState.Swimming`)
plays the forward `Swim` / tread `SwimIdle` clips; swim is exact-only, never derived (a swimmer glides horizontally
like a walker, so position cannot tell them apart). Facing still takes its DIRECTION from the derived heading but
gates on the exact speed too (when supplied), so the model holds its yaw through the post-stop render settle instead
of spinning to chase it. See `docs/USING-KHAOZENGINE.md`.

### Locomotion states + clips

`LocomotionState` = `Idle`/`Walk`/`Run` (ground, by speed), `Jump`/`Fall` (air, by vertical sign), and
`SwimIdle`/`Swim` (water: tread below `LocomotionThresholds.SwimForwardThreshold`, forward stroke above). Swim wins over both
ground and air when the swim flag is set. The enum names match the clip names a consumer bakes (name-based mapping),
so the two water clips are named `Swim` and `SwimIdle`; a rig without them degrades to `Idle` while swimming rather
than crashing. The forward `Swim` clip speed-syncs (pass its authored move speed as `LocomotionSpeedSync` `swimClipSpeed`
/ `CharacterAnimatorTuning.SwimClipSpeed`); the tread always plays at 1x. Swim/tread transitions commit immediately
(exempt from the ground-state debounce, like air states) because the enter/exit is hysteresis-debounced in the
movement sim.

## One-shot and held actions over locomotion

`AnimatedCharacter.PlayAction(clip, mask, fadeIn, fadeOut, speed, mode, hold)` -> `ActionHandle` plays a masked
action (an attack, a cast) over the locomotion base. Default (`hold: false`) is a one-shot: fade in, play through,
fade out overlapping the clip tail, then auto-retire. `hold: true` holds it indefinitely at full weight, looping the
clip as a persistent masked pose (e.g. a drawn-weapon arm idle held over locomotion) until `CancelAction(handle)`
fades it out with no pose pop. A held action played first sits below later one-shot actions, which composite over it
during a swing and fall back to it as they retire. The locomotion state machine keeps driving
the base layer exactly as before; while no action is live the pose is byte-identical to plain `AnimatedCharacter`,
so this is opt-in with zero effect on existing rendering. Slots are pooled, so firing action after action allocates
nothing in steady state. To play an action on a REPLICATED remote: reach its brain via
`ReplicatedCharacterAnimators.BrainFor(id)` and call `PlayAction` on it (the animator holds no ownership state).
Replicating the action TRIGGER is a game-message concern, out of scope here. Client-cosmetic: never feed the pose
back into simulation or netcode. See `docs/USING-KHAOZENGINE.md`.
