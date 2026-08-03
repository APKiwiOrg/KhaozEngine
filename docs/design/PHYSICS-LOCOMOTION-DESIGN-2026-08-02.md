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

> A too-steep destination blocks the move only when this tick climbs onto it faster than the gate's
> own gradient. Descent and level traversal fall through to gravity.

- The gate gains the `groundHeight` delegate alongside `groundNormal`. Blocked iff
  `steep(destNormal) && rise > max(noise, travel * tan(MaxSlopeRadians))`, where `rise` is
  `groundHeight(dest) - feetY` and `travel` is the tick's intended horizontal distance. The
  allowance is a GRADIENT, not a height: it asks whether the tick rises faster than the steepest
  walkable ramp would over the same ground, so a face past the gate blocks at every speed and every
  tick rate. The first cut used a fixed height (the skin width) and review found the hole that
  leaves: any mover whose per-tick rise stayed under it (a slowed character, a short steering
  vector, a high tick rate) climbed an arbitrarily steep face a fraction of a centimetre at a time.
  The absolute term survives only as a noise floor for a near-level traverse across a steep face.
  Neither term is a knob: `tan(MaxSlopeRadians)` is the existing gate read as a gradient.
- Grounded walk toward a cliff edge now proceeds, the support-floor logic finds no walkable floor,
  the character goes airborne, and gravity does the rest. This is the same asymmetry the Bepu-backed
  collide-and-slide already applies (`CharacterMovement.Collision.cs:232-252`), extended to the
  analytic-terrain path that Ruinborne's cliffs actually use.
- The airborne check against feet height (not current ground height) keeps the anti-tunnel property:
  flying into a cliff face whose ground is above your feet is still blocked, so the XZ position can
  never end up under terrain waiting for a ground clamp to pop it up the cliff.
- Applies identically to the grounded path (`DesiredHorizontalCore`) and the airborne-momentum path.
- Pure scalar math, fixed operation order, same delegates on both heads: prediction-safe.

**Addendum, 17.26.1** ([#440](https://github.com/APKiwiOrg/KhaozEngine/issues/440)). The feet turned out to be
the wrong reference on their own, because vertical motion inflates them. A Ruinborne playtest climbed a 78
degree sea cliff by jumping at it repeatedly: near the apex the face's local ground sits level with the raised
feet, so the rise reads about zero, the drift onto the face is admitted, the ground clamp seats the character
on the face, and the next jump repeats. Nor was it jump-specific, since any airtime discounted the face the
same way and seated a character merely FALLING past one while steering into it. The rise reference is now the
LOWER of the feet and the ground under the current column, which airtime cannot raise, and which leaves
grounded motion, genuine descents, and the anti-tunnel property exactly as specified above (the gate only ever
became more conservative). The rest of the rule, the gradient allowance included, is unchanged.

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
  One semantic on both heads: it fires after frames in which authoritative movement RAN. That is
  every `Tick` on the flat head, which steps unconditionally, and only the frames that produce a
  sub-tick on the sharded head, whose cells run off a fixed-tick accumulator. Without the qualifier
  a sharded frame shorter than `TickSeconds` re-delivers the previous landing, once per short frame,
  which is a duplicate application of fall damage rather than a missed one.
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
  legal riser is unaffected. Air-momentum path gets the same four. Scale-freeness gets its own set:
  a steep face fences a full-speed walk, a crawling steering vector, a heavy slow, and a 1000 Hz
  tick alike, and one fixture at one rise gives opposite answers at two speeds.
- Landing: impact speed equals the pre-landing downward speed on exactly the landing tick and is
  zero before and after. Jump-and-land round trip. Teleport mid-fall reports only the post-teleport
  fall. `OnAfterTick` fires after movement with post-step state visible, on both heads, and a
  sharded frame too short to produce a sub-tick fires nothing rather than re-reporting the landing.
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

## Slope sliding and wall slide (2026-08-02 addendum, 17.28.0, #442)

Playtest verdict on the shipped gate, two rounds in: 17.26.0's gate let a jump ratchet up a sheer
face (#440), and the 17.26.1 fence that closed it blocks sideways movement into a face while
jumping, which reads as an invisible wall eating lateral air control. Both are the same root cause:
a GATE refuses movement, and refusal is not how terrain behaves. The user's ask is the standard
model (WoW-style terrain): steep ground is a surface you slide on, not a wall you are denied at.
The reconcile constraint never forbade this. Sliding is carried velocity plus pure surface math,
both of which the stepper already has. Phase 1 avoided it to protect tuned feel, and the playtest
has now voted against that caution, so the gate is replaced rather than patched a third time.

The model, replacing `AdvanceSlopeGated` entirely on the analytic-terrain path:

1. **Wall slide instead of refusal.** When a tick's horizontal move reaches a destination whose
   ground rises beyond what the step can reach (more than `StepHeight` above the feet), that is a
   wall contact. Project the horizontal movement onto the face's horizontal tangent: the into-face
   component dies, the along-face component survives. Applies grounded and airborne, on both the
   command path and the momentum path. This alone fixes the reported feel bug: strafing along a
   cliff mid-jump keeps its lateral motion. The face's horizontal normal comes from the ground
   normal's XZ projection (fall back to the movement direction when the normal is vertical-only),
   pure scalar math, fixed order.
2. **No traction on steep ground.** A surface steeper than `MaxSlopeRadians` never grants support:
   `Grounded` stays false, no jump, no coyote refresh, no landing latch (a slide onto walkable
   ground at the bottom is the landing, and `LandingImpactSpeed` reports there from the accumulated
   fall). The character seats on the surface (the ground clamp still forbids penetration) but
   slides: gravity is decomposed against the surface normal, the tangential component integrates
   into the carried velocities (`VerticalVelocity` plus `HorizontalVelocity`), and the character
   accelerates down-slope until walkable ground, a free fall past the surface, or water (the
   existing medium transition handles the swim hand-off). Input while sliding steers weakly via the
   existing `AirControl` semantics rather than granting a new knob.
3. **The ascent gate is deleted, subsumed.** Climbing self-defeats because there is no footing on
   the face. The jump ratchet stays dead because landing on the face lands in a slide. Anti-tunnel
   is carried by the wall-contact projection plus the ground clamp instead of a refusal. The
   `min(feet, current ground)` rise rule from 17.26.1 goes with the gate, and #441
   (prop-to-steep-face refusal) is subsumed: stepping from a prop toward a face wall-slides like
   everything else.
4. **No wire change expected.** Slide motion rides the already-replicated carried fields, so
   `Grounded` false plus surface contact is derivable per head and the wire generation stays at 10.
   If implementation finds a carried value that must survive reconcile and does not fit the
   existing fields, that is a finding to surface, not to improvise around.

Compatibility call: unconditional, not a knob. The gate semantics this replaces were themselves
17.26.0 behavior changes, the Locomotion blast radius is Ruinborne-only, and the requesting
playtest is Ruinborne. `MaxSlopeRadians` keeps its exact meaning as the traction threshold. The
#369 and #440 test suites' refusal assertions are rewritten to slide semantics with their intent
preserved: no net ascent by any input pattern, no tunnel, descent free, and the new invariants
(lateral air control along a face survives, a slide always terminates on walkable ground, water,
or open air).

Relation to the phases: this is a slice of phase 2's semantics (surfaces as contacts, not gates)
delivered early on the analytic path where the pain is. #438 still owns the full
contact-classification rebuild over the physics-query path (props, buildings, Bepu geometry).

### Correction: what adversarial review changed in the model above

Three of the rules as written above are wrong. Adversarial review caught all three before the release
that carries them, and this note records what replaced them rather than rewriting the history. Everything not named here still stands.

**The contact deletes the into-surface component and NOTHING else.** Point 2 said the tangential
component integrates into the carried velocities, and the first implementation read that as the
FALL LINE alone: it resolved the carried velocity onto the down-slope tangent and rebuilt from that
one scalar. That silently deleted the CONTOUR component, the horizontal in-plane direction
perpendicular to the fall line, which costs no drop to follow at all. A 14 m/s fall running parallel
to a wall was therefore stopped dead on the tick it merely brushed the wall. The resolve is now a
full in-plane decomposition onto a fall-line tangent and a contour axis: only the normal component
is dropped, both survivors are kept in full, and gravity accumulates on the fall line alone (it has
no contour component to give, because the contour is level by construction).

**The fall-line speed is SIGNED.** The first implementation clamped it non-negative and the code
called that "the whole no-ascent property". It is not: it deleted a jump's upward along-face motion
the instant the jump grazed a face. What actually carries the no-ascent property is having no
FOOTING on steep ground, which point 3 already said. So the speed is signed, gravity accumulates
downward along it whatever the sign, and a rising slide decelerates, reverses, and comes back down.
It gains nothing across cycles, because the whole rise is handed back on the way down, and the
exploit invariants are unchanged and still pinned: never grounded on the face, and no net altitude
across cycles.

What the transient rise is WORTH was understated here at first (this note originally said "slightly
higher than a bare jump", measured 2.22 m against 1.92 m on a 78.7 degree face at walk speed). That
was the mildest case, not the property. The contact keeps the run INTO the face as well, so the
reach up a face is the launch's whole kinetic energy: gravity decelerates the fall-line speed at
`Gravity * h` and each metre along the fall line is `h` metres of height, the two cancel, and the
rise is `v^2 / (2 * Gravity)` at any face angle. At the shipped tuning a RUNNING JUMP launches at
`sqrt(JumpSpeed^2 + RunSpeed^2)` = 15.5 m/s and buys 4.8 m of reach against a bare vertical apex of
1.92 m, which is **2.4x** - measured 4.91 m on a near-gate 46 degree face, the best converter there
is, and 2.46 m on the 78.7 degree one once its input runs instead of walking. That is intended
behaviour and correct physics for a frictionless surface: players briefly ride a face upward on jump
energy and keep none of it. The anti-ratchet fixtures bound the reach by that ENERGY now rather than
by a bare apex, which the run rows breached honestly (2.456 m against a 2.370 m ceiling).

**A SWALLOWED DESCENT is supported, and it is the one exception to point 2's "never grants
support".** As written, a character in a concave crease (a V-gully, the inside of a cleft)
soft-locks: its column reads steep so support is refused, the fall line of either wall points into
the other so the wall contact removes the whole horizontal, and the ground clamp then swallows the
descent that horizontal was meant to pay for. Measured 0 grounded ticks in 400, with a held jump
that could never fire. So the termination invariant gains a fourth end: a slide terminates on
walkable ground, in water, in open air, **or where the world is absorbing its descent**. The
detector is stateless and tick-local (this tick's start position, resolved position, resolved
vertical, dt and the tuning), and it arms only on an accumulated downward speed past
`Gravity * max(CoyoteTime, dt)` whose demanded descent was swallowed - which is precisely what a
jump-apex graze lacks, so the retired #440 ratchet gains nothing from it.

The crease is the MOTIVATING case and the source of the `SlideWedged` name, not the condition: the
detector looks at one number, the shortfall, and never at a shape. Any concave curvature under one
tick's travel produces a real shortfall, because the resolve commits the drop the tangent plane at
the START of the tick needs while the ground clamp seats the capsule on the actual surface at the
end of it. So an OPEN creaseless face can grant a transient supported tick, and a held jump can fire
from it. Known and harmless, and left documented rather than fenced: it can only arm on the way
down, the gap is a fraction of one tick's travel and shrinks quadratically with the tick rate, and a
slide down a bowl briefly finding its feet is the honest physical answer anyway. Measured on a
parabolic bowl wall over 4000 ticks of sliding into it: nothing at gentle curvature, one supported
tick 1.29 m up as the curvature sharpens, one at 2.79 m, two at 5.74 m, each firing a launch with
the jump held, and **zero net altitude gain in every case** (the peak never passed the height the
slide started from).

Support is granted for that tick, so a character parked in a crease reports a low-duty-cycle
grounded pulse (measured about one tick in five) rather than steady footing. That is enough to jump
out, to refresh coyote, and to latch the swallowed fall as a landing. **Consumer consequence,
deliberately not designed around:** every pulse is an airborne-to-grounded transition, so
`LandingImpactSpeed` latches on each one and a game reading it for a landing SOUND gets a rattle
while the character sits in a crease. Only the first latch carries the real fall (11.2 m/s on the
fixture), and the rest carry the few ticks of gravity between pulses (4.0 m/s), so a consumer gating
on impact speed for fall DAMAGE is unaffected. The same is true of a jagged crest whose columns
alternate steep and walkable, which is pinned by its own fixture rather than left unknown.

Two smaller corrections in the same pass. The slide's horizontal carry is clamped to the wire's own
per-axis ceiling, because a slide's horizontal terminal is `MaxFallSpeed / tan(surface angle)` and a
gate below about 21 degrees puts that past what `MovementState` can replicate - the sim would
otherwise commit a velocity its own wire quantizes to a different one. And the terminal divide's
`h` is floored by the sine of a VALIDATED gate, so a degenerate `MaxSlopeRadians` of zero or less
(which calls level ground steep) cannot turn `MaxFallSpeed / h` into a division by the smallest
non-zero magnitude a float normal can express. Both clamps are per-axis and horizontal-only, which
mirrors the wire and is theoretical at any shipped tuning: the fastest fall-line speed a slide can
be handed on its up phase is the 15.5 m/s launch that arrived, and the down phase saturates at
50 m/s at the 45 degree gate, both far under the 127 ceiling on either axis.

### Correction, round two: the steer must not erode the carry

The rule above ("the steer is a per-tick term, not folded into the carry") was implemented one-way
only. A sliding tick ADVANCES by the commanded velocity, carry plus steer, but the carry was clipped
against the CARRY ALONE, so the collision clip measured a displacement the steer had helped produce
against a vector that did not contain the steer, read the difference as a denial, and rescaled the
whole carry by it - fall line included, which input is supposed to have no authority over in either
direction. Measured: a held steer whose contour component opposed a 14 m/s carried contour took it
to 8.0 m/s on the first tick and 0.0 by the tenth, where ten ticks of idle input kept all 14, and one
tick of opposing run-speed strafe removed 85% of a mixed carry's fall-line component.

The fix is to let the clip see the vector that actually drove the tick. The denial VERDICT is read
from the commanded velocity, so an unobstructed steered tick returns the carry untouched and exact
rather than re-deriving it from a position the steer moved. The denial AMOUNT is read from the
carry's own share of the committed displacement, the travel with the steer's contribution handed
back, so a genuine contact sheds exactly what it would have shed with no steer held whenever the
steer lies along the contact face - which the slide's steer does by construction against any face
whose outward direction is the fall line, since the steer is confined to the contour axis. The
clamp into `[0, |carry|]` is unchanged, so collision may still only ever clip the carry. Every path
but a steered slide drives with the carry itself, so this is the original measurement, byte for
byte, everywhere else. The rule is symmetric now, which is what it always claimed to be: input adds
nothing to the carry and takes nothing from it, and only geometry sheds it.

### Correction, round three (2026-08-02, 17.29.0, #468): altitude comes from velocity, never from the clamp

The first round-two corrections were reasoned from fixtures. This one is the first correction in the chain
driven by a MEASURED repro, and it says the model above was still missing an invariant rather than a
constant. A Ruinborne playtest climbed the authored sea cliff on the shipped 17.28.0 slide, and a
climber-bot sweep against the real terrain (Ruinborne `feature/climb-repro`, 401 of 2040 input patterns
climbing past 3 m, best 20.6 m of a 21 m face) found the hypothesis this issue was filed on - transient
walkable pawls at column resolution, banked by drifting laterally - wrong in the specific.

**What was actually happening.** Point 1 above says a move whose destination ground rises "more than
`StepHeight` above the feet" is a wall contact. Read the other way round, that admits every move whose
destination is WITHIN a step, on a 74 degree face exactly as on a doorstep - and the ground clamp then seats
the capsule on that column. Two ticks make a limit cycle: the slide tick commits its drop, the tick after it
is out of slide contact and has its whole up-slope command admitted, and the clamp lifts it 0.29 to 0.39 m.
Measured net climb 2.3 to 2.7 m/s while `VerticalVelocity` read -5 to -7. No footing was involved anywhere,
which is why the "climbing self-defeats for want of footing" argument in point 3 did not catch it: the
argument is sound and the climb was not coming from footing. It was coming from the clamp.

**The invariant the model was missing, and now states: A TICK WITHOUT FOOTING MUST NEVER END HIGHER THAN ITS
OWN RESOLVED VERTICAL MOTION ALLOWS.** Altitude on steep ground comes only from real velocity. The
`StepHeight` in point 1 is not a distance the world owes a body, it is what FOOTING buys - the height a
character standing on the ground can lift a foot onto - so a tick that has no footing has bought nothing and
its allowance is its own resolved upward motion, which is zero while it falls.

**It is enforced at the admission, not at the clamp**, and that is not an implementation detail. The clamp
cannot be capped: forbidding penetration is its whole job, and a clamp that refuses to raise a capsule leaves
it inside the terrain, which trades a climb exploit for a tunnel. So the rule lands on the horizontal that
would have needed the raise - which is what a wall contact already is. The move keeps its along-face
component and loses only the part that was buying altitude, so a character sliding down a face, strafing
across one or falling past one keeps everything it had.

**A sliding tick takes the same rule with a 1 mm float slack.** Its advance lies in the surface plane by
construction, so its rise EQUALS its resolved vertical and the comparison is otherwise settled by rounding -
and a false wall verdict there would shed a rising graze's up-slope component, deleting the signed fall
line's ride that the round-one correction restored. The slack cannot be aimed at, because a slide's per-tick
travel is the fall line's own speed and input has no authority over it in either direction. The slide needing
the rule at all is itself a finding: the model above assumes the resolve keeps the capsule ON the surface,
which is true of a PLANAR face and of nothing else. Nothing makes a consumer's normal delegate agree with its
own height field, and a smoothed normal field over a heightmap - what a real terrain sampler hands back -
disagrees everywhere. Measured on the repro's cliff patch at 120 Hz: 31 consecutive sliding ticks each seated
0.085 m higher than the last while `VerticalVelocity` read -2 to -8 m/s.

**The open-face wedge transient was not harmless.** The round-one correction documented `SlideWedged` firing
on an open creaseless face, measured it on a parabolic bowl at a few ticks per 4000 and worth zero altitude,
and left it documented rather than fenced. That was the right call for the geometry it was measured on and
the wrong one for a cliff: where the normal is smoothed the shortfall is structural rather than occasional,
and the open face granted support steadily - five grants inside one measured climb, each a full launch for a
player holding jump, with the probe ring reading 0 of 8 samples walkable and its fall lines spread by 1 to 3
degrees. So support gains a SHAPE test alongside the shortfall: some pair of fall lines sampled over the
established footprint ring must oppose by more than 120 degrees, where two unit fall lines sum to a vector no
longer than either alone. Below that the ring still agrees on a direction to leave by, which is a face. Past
it there is no downhill left to take, which is the wedge the rule exists for. The measured cases sit 40x and
60 degrees clear of it on either side.

**And the sim now says when it granted footing.** `MoveState.SupportGranted` reports support as RESOLVED,
before step 5's jump consumes it. `Grounded` is the state a tick ended in, and a held jump makes those
different on every tick of a hop cycle - which is why the climber-bot's first sweep read zero footing grants
over a 21 m climb that was in fact taking a wedge grant every few seconds. A signal a jump can hide is not a
signal an anti-cheat check can use.

### Correction, round four (2026-08-03, 17.29.0, #468): the resolve and the clamp must read the SAME surface

Round three above closed every heading it had been shown. A 360-heading sweep of its own fixture found 44 of 360
still climbing at 120 Hz, the worst gaining 389 m in 20 seconds with no jump, no footing grant, and
`VerticalVelocity` reading +21 to +24 m/s. So this correction is not a constant and not another bound. Round three
was measuring a correct rule against the wrong surface.

**The root cause is that there are TWO surfaces and the model never said which one was real.** A consumer hands
the step a ground-NORMAL delegate and a ground-HEIGHT field. Round three's own text already noted, as a caveat on
the slide's float slack, that "nothing makes a consumer's normal delegate agree with its own height field". That
was not a caveat. It was the bug. The slide resolved its fall line, its contour and its wall contacts against the
plane the NORMAL reported, while the ground clamp seated the capsule on the HEIGHT FIELD - and the
resolve-then-clamp cycle pumps energy wherever those two disagree. The resolve commits the drop its plane needs,
the clamp puts the capsule somewhere else, and the difference is altitude that no velocity paid for. Reaching for
the admission again could not have closed it, because the admission was computed from the same disagreeing plane.
That is why round three's reach rule, which is correct and is retained in full, still left a third of the circle
climbing.

**The resolution is to give each delegate exactly one job.** The HEIGHT FIELD is the geometry: on a tick with no
footing, the fall-line tangent, the contour, the wall-contact face direction and the reach admission all come from
a plane sampled off the heights themselves, a central difference at `CapsuleRadius` either side of the point in a
fixed order. The NORMAL delegate CLASSIFIES: too steep to stand on, and folds back on itself. Smoothing is a
stability feature in a classification and a liability in a geometry, which is the whole of the distinction.

**The invariant this buys, and it is the one worth carrying forward: the plane the slide resolves against IS the
surface the clamp seats to.** So the clamp can only ever be correcting float noise, and it can never hand back
energy the resolve did not already account for. Stated that way it is checkable rather than argued, and the sweep
checks it: 360 headings, four tick rates, walk and run, with and without a held jump, 4.9 million ticks, no
heading ending above its start and no tick rising past its own resolved vertical motion.

**Three consequences worth recording, because each one was a decision.**

- *The stencil is a fixed tuning value and a CENTRAL difference.* It has to span movement scale rather than sample
  scale, or the plane is as noisy as the height field and the slide resolves against detail the capsule cannot
  stand on. It must not depend on tick rate, speed or heading, because anything a player controls is a dial an
  exploiter can turn - that is exactly how the retired `StepHeight` admission was played. A forward stencil would
  be one delegate read cheaper and is asymmetric, so on a creased face there are headings whose stencil straddles
  a crease on one side only. The symmetric one has no preferred direction to aim at.
- *A sampled plane carries the height field's float noise, and it accumulates.* The clamp covers a capsule that
  ends BELOW the terrain and nothing covered one that ends above it, so the error is re-committed every tick and
  drifts in one direction until the capsule leaves slide contact in mid-face and drops its carry. A slide now
  re-seats DOWN onto the surface within the contact skin, never up, which makes the model's own claim ("a slide
  holds the capsule on the surface") true rather than approximate. Down-only is what makes it safe by
  construction rather than by argument: every bound here is stated as "no higher than".
- *The wedge needed a body-scale reading, and it is the better statement of the rule anyway.* The shortfall test
  asks whether a fall the tick DEMANDED went undelivered. A slide resolving against the body-scale plane correctly
  demands no fall in a crease bottom, because a capsule spanning both walls is on level ground - so the symptom
  the rule was watching for stopped occurring and the soft-lock returned. Support now also arms when the plane the
  heights describe across the footprint is standable, which is the wedge's actual question (is the world holding
  this body up) asked of geometry instead of inferred from a symptom. The fold test is required either way, and
  that is load-bearing: a capsule a centimetre past the TOE of a cliff also spans mostly flat ground, and granting
  footing there would put footing on the face.

**What the round says about the method, since this is the fourth correction to the same model.** Every prior round
was verified against the headings someone had named, and every prior round shipped with a hole somewhere else on
the circle. The two measurements that found this one are both cheap and neither is case-based: a swept parameter
(all 360 headings, not the interesting ones) and a per-tick invariant asserted on every tick of every fixture (not
a total at the end of a run, which cannot see a tick stealing 20 cm inside a ride that nets -700 m). Both are now
permanent. An aggregate that passes is not evidence that the ticks under it did.

### Correction, round five (2026-08-03, 17.30.0, #475): the gate boundary needs a memory and a ramp

The four rounds above are all about the same question asked from different angles: what may a body TAKE from a face
it has no business standing on. This one is about a different question the model never asked, and it comes from a
playtest rather than an exploit sweep. What does the model do to ground sitting right ON the threshold?

**Two failures, one boundary, and both were measured before they were designed for.** `MaxSlopeRadians` was a bare
per-tick binary, re-decided from scratch every tick with no reference to what the previous tick decided. So terrain
whose columns straddle it does not read as "marginally too steep", it reads as an alternating sequence of walk ticks
and full-gravity slide ticks. On a Ruinborne beach-to-plateau bank whose columns run 40.0 to 41.8 degrees against a
40 degree gate, a walking bot flipped footing 43 times in 330 ticks and ended 2.73 m up a 7.6 m bank having
repeatedly gained and lost the same ground. Re-tuning the gate past that bank fixed that bank (330 of 330 ticks with
footing, zero flips) and moved the identical failure onto the next feature: at a 46 degree gate, banks peaking at
46.4 and 48.8 degrees chattered the same way, 24 and 29 flips over 300 ticks. Any threshold lands inside some
terrain's slope distribution, so this is not a value anyone downstream can pick correctly. And separately,
`ResolveSlide` committed the full fall-line projection of gravity the instant a surface crossed the gate, so a
surface one degree too steep to stand on threw a character down at the same strength an eighty degree one did.

**Traction hysteresis: the decision reads the state it is deciding about.** A character that HAS footing keeps it up
to `MaxSlopeRadians + MoveTuning.TractionHysteresisRadians`. A character that has NONE regains it only at or under
`MaxSlopeRadians`. That is the whole rule. The band is a ceiling on what footing may KEEP and never a route to
footing, so landing on, sliding onto, or grazing ground past the gate still grants nothing, and the steepest ground a
body can end up standing on is exactly gate plus band by any route it takes. The memory is `MoveState.Grounded`,
which the sim already carries and the wire has replicated since the movement state's first generation (it predates
every "added in generation N" annotation on `MovementState`), so this adds no state anywhere and survives a
reconcile replay for free. The default band is 3 degrees, which covers every straddle in the measurements
above (the worst excursion above a gate was 2.8 degrees) with margin, and stays inside terrain a player still reads
as a steep hillside.

**The consequence that IS the mechanism, stated plainly so nobody reads it as a leak.** A uniform face at gate plus
two is now walkable, indefinitely, by a character that walks onto it from adjacent walkable ground. That is not a
side effect of hysteresis, it is hysteresis. The same face refuses a body that arrives falling. Two characters on the
same column can disagree about whether it holds them, and the one that is already standing is right. The play
consequence that surprises, and is correct: on band-held ground a JUMP converts a stable stand into a slide, because
the launch tick ends un-grounded and the landing is judged at the bare gate, so the column that was holding the
character refuses it on the way back down.

**Slide friction: the fall-line acceleration ramps in.** The scale is
`clamp((surface slope - MaxSlopeRadians) / MoveTuning.SlideFrictionRampRadians, 0, 1)`, default ramp 8 degrees, and
it multiplies gravity's fall-line term alone. At the shipped 45 degree gate a 46 degree face accelerates at 2.25
m/s^2 along the fall line against 17.99 unscaled, a 49 degree one at 9.43 against 18.87, and anything at 53 degrees
or steeper is untouched. Measured as one second of sliding from rest on a 46 degree face: 0.836 m of drop with the
ramp, 6.684 m without. The ramp reads the slope of the HEIGHT-derived plane, not the classification normal, for the
reason round four settled for every other number that decides where a capsule ends up.

**THE ONE RULE THAT KEEPS FRICTION FROM BEING ROUND FIVE'S OWN EXPLOIT, and it was nearly written the other way.** A
scale on the whole of gravity would scale the DECELERATION of a rising graze too. The reach of a launch up a face is
`v^2 / (2 * Gravity)`, so a body decelerating at an eighth of gravity rides eight times as high, and as the scale
approaches zero at the gate itself the reach is unbounded. A player who then steps off the top onto walkable ground
has climbed on nothing, which is #440 again by a new route. So gravity decelerates a RISING slide at full strength,
always, and only the accelerating half is scaled. The crossing tick is split exactly (full strength for the
`-fall / accel` seconds the body is still rising, scaled for the remainder) rather than taking one strength for the
whole of it, so the rule has no tick-rate-dependent kink. This is also what friction physically is: a force that
opposes motion, never one that helps a body up. Every "no higher than" bound in rounds one to four is therefore
untouched, because the up phase they bound is byte-for-byte the same arithmetic.

**One traction truth per tick.** The gate is now a function of state, so it can be read at two moments and give two
answers, and a support decision that disagrees with the wall contact driving it is exactly the chatter this round
removes. `StepCore` resolves it once from the footing the tick STARTED with and hands the same number to the slide
contact, the wall contact on all three horizontal paths, the slide resolve, the support decision, the wedge and the
step-down hold. Nothing re-derives it. The wall contact needing the widened gate is not a formality either: without
it a run up a bank the band is holding footing on would meet a fence built out of the ground under its own feet,
since a fast run on a legal ramp rises more than a `StepHeight` in one tick.

**The step-down hold was the last route to footing on steep ground, and it is closed here (#470).** The routes that
can grant footing on terrain are the support decision, the swallowed-descent wedge and step 4a-down's step-down hold.
The first two have run the traction test since it existed. The third never did, and not by omission: it read the
support decision's `noTraction`, which is written as `onGround && ...`, and `onGround` only reaches drops within
`GroundedEpsilon` (0.30). The step-down hold covers exactly the band ABOVE that, drops up to `StepHeight` (0.40), so
across the whole of its own range the guard was vacuously false and no traction test ran on the surface it was
seating onto. Measured: a 0.15 m lip onto a 63.4 degree face, walked east at 6 m/s and 60 Hz, put the crossing tick's
drop at 0.35 m, and that tick reported `Grounded` and `SupportGranted` seated on the face with a jump pressed there
launching at the full `JumpSpeed`. The hold now runs the same test against the same tick-resolved gate, which is the
widened one on this path by construction (the character held footing at tick start, or `s.Grounded` would not have
armed the hold at all). Ground past that gate refuses the seat and the body goes over the edge as any walk-off does.
The test itself is one function now rather than two expressions of the same question, which is the actual fix: the
band's legitimate work is untouched, because a walkable tread is under the gate whichever way the character is
travelling over it.

**Compatibility, and what it cost the fixtures.** Both mechanisms are default-ON and both change behaviour near the
gate for any game on steep terrain, which is the precedent this program set when the slide model itself shipped
unconditional. Setting either knob to 0 restores the previous model bit for bit, which is also what a bare
`default(MoveTuning)` reads, and that direction is pinned by its own case. Three fixtures pinned binary-threshold
behaviour at the gate boundary and were recalibrated rather than relaxed: two 46 and 47 degree faces moved to 49,
one degree past gate plus band, because the boundary they are about moved by the width of the band, and the wire
horizontal-ceiling fixture turns friction off so its 2%-past-the-gate face still reaches terminal inside its window
(friction can only reduce the speed a slide reaches, so a clamp that holds at full strength holds under any ramp).
Everything else stayed green unmodified, including the 360-heading sweep at four tick rates, which is
BIT-IDENTICAL rather than merely passing: its face runs 68.6 to 77.1 degrees, more than 15 degrees past gate plus
ramp so the friction scale is 1 and short-circuits, and it never grants footing so the gate is never widened.
Measured with both mechanisms active, 5760 rides and 4.9 million ticks: 0 climbers, 0 footing grants, 0 jumps, worst
peak 0.000 m at every rate.

That last sentence is also the instrument's own gap, and review folded a SECOND sweep in beside it: same circle,
same four rates, same bound, on a face whose planes run 46.0 to 47.1 degrees under the same smoothing stencil, so
the friction scale is 0.12 to 0.26 and the arithmetic this round added is what the sweep is measuring. A weak pull
is the adversarial direction for round three rather than a gentler one, because the ratchet was a race between a
command tick's rise and a slide tick's drop and this face shrinks the drop. Another 5760 rides: 0 climbers, 0
grants, 0 jumps, worst peak 0.000 m, every ride ending 226 to 579 m below its start.

## Directional speed under `FaceCamera` (2026-08-03 addendum, 17.30.0, #479)

Phase 1 above gave the character an authoritative facing and stated, correctly for what it shipped, that facing
"affects no position output, so existing games are bit-identical". This addendum is the consequence that statement
did not anticipate: once `FaceCamera` pins the body to the camera, the character HAS a front that does not turn
with its travel, and "which way is it moving relative to where it is looking" becomes a question the sim can answer.
Every third-person game answers it by charging for the answer. The Ruinborne playtest of the 0.16.16 facing wiring
raised it immediately: a player who strafes and backpedals at full run speed does not read as a character, it reads
as a hovering camera rig.

**It has to be sim-side.** A scale the client applies to its own input is two separate bugs. It is a speed hack,
because a client can simply not apply it, and it is a misprediction, because the server would not apply it either.
So it lives in the stepper, on the authoritative path both heads run.

**The classification reads the COMMAND, not the world.** `MoveCommand.Move` is already camera-relative (X = right,
Y = forward), which is exactly the frame the question is asked in, so the sector falls out of the raw axis with no
reference to the camera yaw, the world direction, or the carried heading. With `a = |Move.X|`: `Move.Y >= a` is
forward, `Move.Y <= -a` is reverse, everything else is strafe. One absolute value and two comparisons. No `atan2`,
no normalize, no division, nothing whose last bit can differ between a server and a client, and scale-invariant, so
a half-deflected stick classifies exactly as a fully deflected one does.

**The boundary rays are a decision, not a rounding accident.** Forward and reverse are CLOSED wedges, so exactly 45
degrees is forward and exactly 135 is reverse, with strafe owning the two open wedges between. The reason is that a
keyboard lands precisely on those rays rather than near them: `CharacterFacing.MoveAxis` builds the axis from whole
+/-1 components, so W+D is the vector `(1, 1)` and S+D is `(1, -1)`. Giving 45 to strafe would run the most common
forward diagonal in the game at the strafe scale. Giving 135 to strafe is worse than merely surprising: it would
hand a player who wants to flee quickly a strictly better key combination (S+D at the strafe scale with sprint
honoured) than the one that means flee (S at the reverse scale with sprint refused), which is an exploit the
tuning cannot close. Each boundary belongs to the wedge nearer the axis it straddles, and the cheapest predicate
form happens to be exactly that, which is a good sign rather than a coincidence.

**Where it composes, and why that single site is the whole design.** The player entry points already resolve a
command into `(unit direction, speed fraction)` before handing off to the shared core. The scale multiplies THE
FRACTION, in one new resolver (`ResolveCameraCommand`) that wraps the existing one. That is the only edit to the
movement path. Every consumer of a resolved command reads the fraction and therefore gets the scale exactly once,
by multiplication, with nothing to keep in sync: the grounded and airborne `DesiredHorizontalCore`, the
airborne-momentum steer target, the slide's in-plane input steer, and the swim step. It composes with
`MoveState.SpeedScale`, the wade ramp, the medium's zone scale and `AirControl` in whatever combination the tick
happens to be in.

Two consequences fall out of that placement rather than needing their own mechanism. The anti-cheat is one:
`MovementAnomaly.CorrectionDistance` builds the server's intended target from `MoveState.CommandedVelocity`, which
is built from the same product, so the target shrinks with the scale and a backpedalling player reads as denied
nothing. Had the scale been applied anywhere the export could not see, this feature would have flagged every
retreating player as a speed hacker inside a third of a second, which is exactly the swimmer bug that made the
check read the export in the first place. Momentum is the other: a committed `AirMomentum` arc flies the carried
`HorizontalVelocity`, which is not a command, so the scale steers the arc (with whatever authority `AirControl`
grants) and never shrinks it. No special case was written for either.

**The run rule is a separate knob because a fraction cannot carry it.** Refusing a sprint changes the BASE speed
the fraction multiplies, not the fraction, so `MoveTuning.BackpedalAllowsRun` rides back from the resolver
alongside the fraction as an effective run bit. It is a knob rather than an implication of the scale because the
two answer different questions: how fast a retreat is, and whether a retreat can be a sprint. A game can want a
slow backpedal a player may still sprint into, or a full-speed one they may not.

**All three defaults are neutral (1, 1, true), and that is load-bearing rather than polite.** The traction pair in
round five deliberately shipped default-ON because the behaviour they replace is a measured bug. These are the
opposite case: a character that moves as fast backwards as forwards is a FEEL choice, and it is the feel every game
on the stack was tuned against. Multiplying by exactly 1 is the identity for every float, so the neutral path is
bit-identical rather than nearly so, and a test pins that in every sector with and without the flag.

`MoveSector` and `CharacterMovement.Sector` are public for the same reason `CameraRelativeDir` is: a consumer
needs the same answer for presentation (which locomotion animation to play, whether to show a retreat stance), and
a hand-copied predicate downstream is how the two drift apart.
