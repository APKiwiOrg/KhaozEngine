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

## Slope sliding and wall slide (2026-08-02 addendum, 17.27.0, #442)

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

### Correction, 2026-08-02 (17.28.0): what adversarial review changed in the model above

Three of the rules as written above are wrong, and this note records what replaced them rather than
rewriting the history. Everything not named here still stands.

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
A cycle may transiently reach slightly higher than a bare jump would (a frictionless face converts
run speed into altitude, measured 2.22 m against a bare apex of 1.92 m at the shipped tuning), and
it gains nothing across cycles, because the whole rise is handed back on the way down. The exploit
invariants are unchanged and still pinned: never grounded on the face, and no net altitude across
cycles.

**A WEDGE is supported, and it is the one exception to point 2's "never grants support".** As
written, a character in a concave crease (a V-gully, the inside of a cleft) soft-locks: its column
reads steep so support is refused, the fall line of either wall points into the other so the wall
contact removes the whole horizontal, and the ground clamp then swallows the descent that horizontal
was meant to pay for. Measured 0 grounded ticks in 400, with a held jump that could never fire. So
the termination invariant gains a fourth end: a slide terminates on walkable ground, in water, in
open air, **or wedged between opposing faces**. The detector is stateless and tick-local (this
tick's start position, resolved position, resolved vertical, dt and the tuning), and it arms only on
an accumulated downward speed past `Gravity * max(CoyoteTime, dt)` whose demanded descent was
swallowed - which is precisely what a jump-apex graze lacks, so the retired #440 ratchet gains
nothing from it. Support is granted for that tick, so a character parked in a crease reports a
low-duty-cycle grounded pulse (measured about one tick in five) rather than steady footing. That is
enough to jump out, to refresh coyote, and to latch the arrested fall as a landing. **Consumer
consequence, deliberately not designed around:** every pulse is an airborne-to-grounded transition,
so `LandingImpactSpeed` latches on each one and a game reading it for a landing SOUND gets a rattle
while the character sits in a crease. Only the first latch carries the real fall, and the rest carry
the few ticks of gravity between pulses, so a consumer gating on impact speed for fall DAMAGE is
unaffected. The same is true of a jagged crest whose columns alternate steep and walkable, which is
pinned by its own fixture rather than left unknown.

Two smaller corrections in the same pass. The slide's horizontal carry is clamped to the wire's own
per-axis ceiling, because a slide's horizontal terminal is `MaxFallSpeed / tan(surface angle)` and a
gate below about 21 degrees puts that past what `MovementState` can replicate - the sim would
otherwise commit a velocity its own wire quantizes to a different one. And the terminal divide's
`h` is floored by the sine of a VALIDATED gate, so a degenerate `MaxSlopeRadians` of zero or less
(which calls level ground steep) cannot turn `MaxFallSpeed / h` into a division by the smallest
non-zero magnitude a float normal can express.
