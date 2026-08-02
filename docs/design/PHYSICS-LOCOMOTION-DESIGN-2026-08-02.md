# Physics-driven character locomotion

Date: 2026-08-02. Decides [#371](https://github.com/APKiwiOrg/KhaozEngine/issues/371), ships the fix
for [#369](https://github.com/APKiwiOrg/KhaozEngine/issues/369), and provides the engine seams for
Ruinborne fall damage ([Ruinborne#292](https://github.com/APKiwiOrg/Ruinborne/issues/292)) and
camera-locked player facing (previously unfiled anywhere).

## The ask

Three player-visible requirements, from the user, 2026-08-02:

1. Movement and locomotion driven from the physics engine, for players and enemies alike.
2. Jumping off and falling down cliffs, with fall damage.
3. Holding right click rotates the player to the camera direction, server-side replicated.

## What already exists (the surprise that shapes everything)

Movement is already physics-driven in every sense that matters. `KhaozEngine.Locomotion.CharacterMovement`
is a kinematic capsule character controller that integrates gravity (`CharacterMovement.cs:251-256`),
jumps with coyote time and input buffering (`:748-755`), carries opt-in airborne momentum
(`CharacterMovement.Momentum.cs`), swims with a buoyancy solver (`CharacterMovement.Fluid.cs`), and
resolves collision by substepped swept collide-and-slide against the Bepu physics world
(`CharacterMovement.Collision.cs:97`, 14 `IPhysicsWorld` query call sites: 7 `SweepCapsule`,
4 `Raycast`, 3 `ComputePenetration`). NPCs run the same core through `StepTowards` (`:128`), so
enemies inherit everything for free. The physics world is a statics query oracle: `IPhysicsWorld.Step`
is never called on the movement path, and that is a feature, not an accident (see the constraint below).

What is actually missing, mapped one-to-one to the ask:

1. The slope gate that fences characters onto clifftops (#369): a model gap, not missing physics.
2. A trustworthy landing-impact fact (`VerticalVelocity` is zeroed on the landing tick) and a
   post-movement server hook to read it from.
3. Authoritative facing. Character yaw exists nowhere in the command state or the replicated state.
   `MoveCommand.CameraYaw` crosses the wire every tick but is consumed internally and never stored.
   Servers today derive facing from position deltas, so a stationary player cannot turn at all.

## The load-bearing constraint

`ClientPrediction.Reconcile` (`KhaozEngine.Netcode/ClientPrediction.cs:215`) replays up to 256
unacknowledged commands through the movement step on every snapshot arrival, starting from an
unconditional overwrite of the basis state. Two consequences govern every design below:

- The movement model must remain a pure function of `(MoveState, MoveCommand, dt)` plus stateless
  world queries. Any hidden mutable state outside `MoveState` desyncs on the first replay.
- Every field that feeds the next tick must ride the wire in `MovementState`, or reconciliation
  resets it on every correction (`MovementState.cs:79-84` documents this for the momentum fields).

## The #371 decision

Three candidate ends, weighted. Criteria scored 1-10, higher is better.

| Criterion (weight) | (a) patch the stepper | (b) contact-classification controller, kinematic, over physics queries | (c) player as Bepu rigid body |
|---|---|---|---|
| Prediction/reconcile compatibility (x3) | 10 | 9 | 1 (no Bepu rewind API, hidden solver state) |
| Determinism posture (x2) | 10 | 9 | 3 (warm-started iterative solver) |
| Delivers the user-visible asks (x3) | 9 | 9 | 7 (delivers them late, after solving rollback) |
| Risk to Ruinborne's tuned feel (x2) | 9 | 5 | 2 |
| Long-term extensibility, ends the per-case accretion (x2) | 3 | 9 | 8 |
| Cost (x1) | 9 | 4 | 1 |
| **Weighted total (max 130)** | **111** | **102** | **44** |

(c) is ruled out, not merely deprioritized. Bepu 2.4 exposes no snapshot or rollback, so replaying
256 ticks means rewinding contact manifolds, warm-start impulses, and island state that the library
does not expose. Anyone reopening (c) must answer that first, not last.

(a) wins the near term and (b) wins the long term, and they are not in conflict: the phase 1 items
below are (a)-shaped fixes specified so that none of them is thrown away by (b). The decision is
therefore: **direction is (b), a principled contact-classification kinematic controller over
physics-world queries, reached by phases, with phase 1 shipping the user-visible asks inside the
current stepper now.** #371 closes with this spec as its answer. Phase 2 gets its own roadmap issue.

## Phase 1 (this release)

### 1. Direction-aware slope gate (closes #369)

`AdvanceSlopeGated` (`CharacterMovement.Momentum.cs:144`) currently rejects any XZ move whose
destination ground normal exceeds `MaxSlopeRadians`, regardless of direction. New rule:

> A too-steep destination blocks the move only when its ground height is above the character's feet
> by more than an ascent tolerance. Descent and level traversal fall through to gravity.

- The gate gains the `groundHeight` delegate alongside `groundNormal`. Blocked iff
  `steep(destNormal) && groundHeight(dest) > feetY + tolerance`. The tolerance is a small constant
  tied to the existing skin/step constants, chosen by the implementation with tests, not a new knob.
- Grounded walk toward a cliff edge now proceeds, the support-floor logic finds no walkable floor,
  the character goes airborne, and gravity does the rest. This is the same asymmetry the Bepu-backed
  collide-and-slide already applies (`CharacterMovement.Collision.cs:232-252`), extended to the
  analytic-terrain path that Ruinborne's cliffs actually use.
- The airborne check against feet height (not current ground height) keeps the anti-tunnel property:
  flying into a cliff face whose ground is above your feet is still blocked, so the XZ position can
  never end up under terrain waiting for a ground clamp to pop it up the cliff.
- Applies identically to the grounded path (`DesiredHorizontalCore`) and the airborne-momentum path.
- Pure scalar math, fixed operation order, same delegates on both heads: prediction-safe.

### 2. Landing-impact seam (unblocks Ruinborne#292)

- `MoveState.LandingImpactSpeed`: float, meters per second, set to the downward speed on exactly the
  tick the character transitions airborne to grounded (captured before `VerticalVelocity` is zeroed),
  zero on every other tick. Event-as-state, following the `StepDeltaY` precedent of exporting the
  fact the sim already computed (`MoveState.cs:100-105`). Inherently capped by `MaxFallSpeed`, which
  is terminal velocity and therefore physical, not a data loss.
- Mirrored onto `MovementState` as a sim-local field (the `ClimbRateEwma` precedent): readable
  server-side per slot, not replicated, no wire change. The server computes damage from its own
  authoritative step. Remote clients that want landing VFX already receive `Grounded` and
  `VerticalVelocity` and can derive the transition.
- `WorldServer.OnAfterTick(float dt)` and `ShardedWorldServer.OnAfterTick(float dt)`: a
  post-movement hook, the mirror of `OnBeforeTick`. This is a genuine missing seam (Ruinborne's
  `DeadPlayerMovementLock.cs:8-13` documents working around its absence) and is where a game reads
  landing impacts, applies fall damage, and generally observes post-step state without waiting a tick.
- Surfaced on `EntityRenderState` for the local player (predicted), so client presentation can react
  on the predicted landing tick.
- Teleports must not fabricate an impact: a teleport mid-fall resets the vertical bookkeeping, and a
  test pins that the landing after a teleport reports only the post-teleport fall.

### 3. Authoritative facing (the right-click ask)

- `MoveCommand` gains `FaceCamera: bool`. Wire encoding: the run byte becomes a flags byte (bit 0
  run, bit 1 faceCamera). `MoveSize` stays 18, so the length-based frame demux contract
  (`MoveProtocol.cs:356-372`) is untouched.
- `MoveState.FacingYaw`: carried float, radians, same convention as `MoveCommand.CameraYaw` (when
  `FaceCamera` is held with no movement input, `FacingYaw` converges to `CameraYaw` exactly).
  Consumers using an opposite gameplay basis convert at their own boundary, as Ruinborne's
  `FacingConventions` already does.
- Update rule, applied on every step path (grounded, airborne, swim, and the NPC `StepTowards`,
  where the target is the steering direction): the facing target is `CameraYaw` when `FaceCamera`
  is set, else the yaw of the commanded world-space move direction when input is nonzero, else the
  current facing (a stationary character holds its heading, and can now turn in place by holding
  the button). The facing turns toward the target shortest-arc at `MoveTuning.FacingTurnSpeed`
  radians per second, default positive infinity, which snaps and matches today's commanded-facing
  presentation feel. Facing affects no position output, so existing games are bit-identical.
- `FacingYaw` is carried state, so it rides the wire: `MovementState.FacingYawQ`, quantized to a
  16-bit turn fraction, following the established quantizer discipline and the
  `HorizontalVelocityXQ/ZQ` carry pattern end to end (`PlayerMoveState.From`, both heads'
  carry-in/carry-out including the sharded write-back, the codec). `WireProtocolVersion` bumps 9
  to 10.
- Surfaced on `EntityRenderState.FacingYaw`: predicted for the local player, replicated for remotes.
  Games that ignore it keep their current presentation-derived facing.

## Phase 2 (successor roadmap issue, not this release)

Rebuild `StepCore`'s resolution sequence as an explicit contact-classification controller: collect
contacts from the physics world, classify ground, wall, and ceiling by normal, resolve velocity
against the contact set. Descend-vs-climb, ledges, and step-up then fall out of classification
instead of needing a gate per case. Still kinematic, still a pure function, so the netcode model is
untouched. Lands opt-in behind a `MoveTuning` switch with parity fixtures against the legacy stepper,
and flips per game only after that game's own playtest. Acceptance bar is Ruinborne's tuned feel:
`WalkSpeed`, `RunSpeed`, `MaxSlopeRadians`, `JumpSpeed`, `AirMomentum` keep their meaning, and the
`ClimbRate`/`ClimbRateEwma`/`StepDeltaY`/`Swimming` signal exports survive unchanged.

## Phase 3 (with #31, not this release)

Character presence in the physics world: mirror each character as a kinematic Bepu body after the
authoritative step (a post-step write, never sim input, so purity is preserved), giving other systems
(AI queries, projectiles, future pickups) a physical player to query. Character-carrying platforms
(velocity inheritance from the support body) live here, alongside the existing notes on
[#31](https://github.com/APKiwiOrg/KhaozEngine/issues/31).

## Wire compatibility

One `WireProtocolVersion` bump, 9 to 10, covering the flags byte and `FacingYawQ` together. Both
heads ship together in every consumer (Ruinborne pins one engine version for client and server), so
generation bumps are the established, tested path (generations 3 through 7 all added `MovementState`
fields this way).

## Test plan

Headless, in the existing per-area projects, following the both-heads parity idiom
(`ClientCollisionTests` pattern: two identical worlds, loopback pair, hand-stepped comparison head).

- Slope gate: grounded walk off a steep edge proceeds and goes airborne. Grounded walk into a steep
  rise stays blocked. Airborne drift into a face is blocked (no tunnel, no pop-up). Step-up onto a
  legal riser is unaffected. Air-momentum path gets the same four.
- Landing: impact speed equals the pre-landing downward speed on exactly the landing tick and is
  zero before and after. Jump-and-land round trip. Teleport mid-fall reports only the post-teleport
  fall. `OnAfterTick` fires after movement with post-step state visible, on both heads.
- Facing: converges to camera yaw under `FaceCamera` while stationary and while strafing. Follows
  the move direction without it. Holds heading when idle. Shortest-arc wrap at the seam. Finite
  `FacingTurnSpeed` turns at the configured rate identically on both heads. Reconcile parity: a
  facing mid-turn survives a reconciliation replay bit-identically (the `StairGlideReconcileParityTests`
  shape). Wire round-trip through `FacingYawQ` quantization.
- Cliff integration: walk off, fall, land: slope gate releases, gravity integrates, landing impact
  reports, facing held throughout. One test chaining all three features.

## Consumer adoption sketch (Ruinborne, its own repo and release)

- Wire right-click hold to `FaceCamera` alongside the existing orbit (the button already orbits, the
  flag makes the character follow), one line at the `SendInput` site.
- Fall damage per its own #292 spec: read `LandingImpactSpeed` per slot in the new `OnAfterTick`,
  map through its damage formula, feed the existing `PlayerDamageService.Damage` sink. Suppress
  during the wolf lunge script or convert that lunge to a real jump now that #369 lets it clear rims.
- Replace the position-delta `_playerYaw` derivation feeding melee arcs and ability cones with the
  authoritative `FacingYaw` (convention conversion at `FacingConventions`).
- Reconsider `MaxSlopeRadians` 40, which was tightened specifically to act as the cliff guardrail
  this work retires.
