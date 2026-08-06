# KhaozEngine.Locomotion

Render-free character locomotion core. `CharacterMovement.Step` is the single movement function shared by the
local controller, the authoritative server sim, and client-side prediction - so local and networked movement
run identical code.

## API

Two overloads share one horizontal core (camera-relative WASD axis, normalised diagonals, walk/run speed,
optional steep-terrain wall slide via a ground-normal delegate):

- **`Step(Vector3, in MoveCommand, float, groundHeight, in MoveTuning, groundNormal?, medium?) -> Vector3`**
  Horizontal-only step. Y is clamped to `groundHeight(x, z) + halfHeight` every tick. No air, no vertical
  physics. Use for top-down or no-jump scenarios.

- **`Step(in MoveState, in MoveCommand, float, groundHeight, in MoveTuning, groundNormal?, IPhysicsWorld?, clampXz?, medium?) -> MoveState`**
  Vertical-physics step. Gravity, jump (coyote-time + jump-buffer), air control, land/clamp, and 3D collision
  resolution against an `IPhysicsWorld` (8.0.0+). The collision path uses a **substepped swept
  collide-and-slide** over `IPhysicsWorld.SweepCapsule` (8.4.0): a fast move can no longer tunnel through a
  thin one-sided wall, and the capsule never gets trapped inside a closed mesh. Walkable contacts (slope at or
  below the gate) are followed; steep contacts block and redirect tangentially. A **step-up probe** wires
  `MoveTuning.StepHeight`: stair treads and curbs below `StepHeight` are mounted without a jump. Step-up climbing
  rises at a **bounded vertical rate** (`MoveTuning.MaxStepClimbSpeed`, default 3.5 m/s), so a stair run ascends at
  a steady walking pace instead of snapping up a whole riser per tick. The probe climbs **perpendicular to the riser
  edge** (straight up the stairs, not along the raw angled move), so an off-axis approach ascends square to the treads
  and merely shaves the lateral against a shaft wall like a normal flat-ground slide instead of leaking sideways and
  wedging; and the step-up's forward advance is **capped to the walk step** (no fore-aft lurch) and then validated
  against the underfoot support fan (10.66.0): a stair run's capped pose stays supported on the treads it spans, so
  the cap holds and the climb stays smooth, while a deep single riser's capped pose has no genuine floor below the
  feet, so the mount instead commits the step-up probe's already-proven landed seat, letting depenetration lift the
  capsule up onto the step rather than push it back off. The fan counts only a support that sits genuinely below the
  feet (a hit closer than the skin depth is the feet embedded in a solid riser box, not resting on it, and is
  rejected), so a solid building or doorway riser reads unsupported and mounts too, not just a one-sided tread gap
  (10.67.0). At a staircase **base**, where the terrain sits within a step of the tread, a footprint that straddles a
  tread shallower than the capsule diameter can make the downward support sweep miss the tread entirely (it grazes the
  vertical riser front); when that happens the same radius-less ray fan finds the tread the sweep cannot and seats the
  support on it, so the bottom step mounts cleanly at any approach speed and arrival phase instead of collapsing a
  whole riser back to the ground below and bobbing there. Monotone either way, so it starts cleanly from the flat floor at any
  angle/speed instead of vibrating on the first riser - a stair climbs like a ramp. A per-riser backward shove during
  a paced climb is also caught by a monotone-forward hold (grounded climb ticks only, a jump takeoff is never held).
  A low curb whose rise fits one tick's budget
  still mounts in one tick, and `MaxStepClimbSpeed <= 0` restores the instant snap. Depenetration
  via `ComputePenetration` is retained as a residual settle pass. Pass `world: null` for terrain-only
  (byte-identical to pre-8.4.0).

- **`StepTowards(in MoveState, Vector2 worldDir, bool run, float, groundHeight, in MoveTuning, groundNormal?, IPhysicsWorld?, clampXz?, medium?) -> MoveState`** (10.64.0)
  The world-space kinematic step for **server-authoritative, non-player agents (enemy NPCs)**. It drives the SAME
  collision resolution the player gets - swept collide-and-slide + `StepHeight` step-up against the `IPhysicsWorld`,
  the analytic terrain support floor, the `groundNormal` wall slide, and the `clampXz` bounds - but from a
  **world-space steering direction** instead of a camera yaw. `worldDir` is an XZ direction whose length scales speed
  in `[0,1]` (unit = full speed, shorter = a slower saunter, longer clamped to full; near-zero = idle). Per-agent
  capsule radius / half-height / walk-run speed come from `MoveTuning`, so different creatures get different sizes and
  speeds with no extra plumbing. **No jump bit** (NPCs do not jump in v1) and **no client prediction** (AI is
  server-only). Both the camera-relative player `Step` and this world-space `StepTowards` resolve their input to one
  shape (a unit direction + a speed fraction) and share a single collision core, so player and AI can never drift
  apart - the player path stays byte-for-byte identical to before. Since 17.26.0 it also turns
  `MoveState.FacingYaw` toward the steering direction, so an NPC's heading is authoritative with no camera and no
  extra plumbing (the AI path never has a `FaceCamera` target: there is no camera on it).

- **`CameraRelativeDir(in MoveCommand) -> Vector2`** (14.9.0)
  The **commanded** camera-relative travel direction as a unit XZ vector (`Vector2.Zero` when idle, inside the
  1e-6 length-squared dead-zone), the exact direction the authoritative/prediction `Step` resolves the command to
  before it moves. For a consumer driving **explicit model facing** (facing the model toward where it is COMMANDED
  to travel, distinct from the direction the measured render position drifts): a commanded-facing yaw is just
  `MathF.Atan2(dir.X, dir.Y)` (world radians about +Y, 0 = +Z), gated on the vector being non-zero. Shares the ONE
  camera basis the step uses (`forward = (-sinYaw, -cosYaw)`, `right = (cosYaw, -sinYaw)`), so the public facing and
  the resolved movement can never drift apart.

- **`WrapYaw(float) -> float`** and **`FacingYawOf(Vector2 dirXz) -> float`** (17.26.0)
  The two public conversions for `MoveState.FacingYaw`. `WrapYaw` reduces any angle to the canonical
  `[-pi, pi)` (low end inclusive), returning an already-canonical angle **bit-identically**, which is what makes
  the "the heading converges to `CameraYaw` exactly" contract true rather than approximate. `FacingYawOf` is the
  heading of a world-space XZ direction (`X` = world +X, `Y` = world +Z, matching `CameraRelativeDir`'s output),
  and it is the exact inverse of the camera basis the step resolves a command in. A non-finite angle and a zero
  vector both yield 0, which is a legal heading (facing -Z) rather than a sentinel.

- **`Sector(in MoveCommand) -> MoveSector`** (17.30.0)
  Which directional sector the command's camera-relative axis falls in relative to the character's facing:
  `Forward` (within 45 degrees, inclusive), `Strafe`, or `Reverse` (135 degrees or more). The rule the directional
  speed scales are charged by while `MoveCommand.FaceCamera` is held, exposed so a consumer choosing a locomotion
  animation reads the same answer the movement did. It reports the SECTOR alone and does not look at `FaceCamera`,
  since presentation wants it whether or not the sim is charging for it. See "Directional speed under `FaceCamera`"
  below for the predicates and the boundary rule.

### Steep terrain: wall slide and no traction (17.28.0)

Supply a `groundNormal` delegate and ground steeper than the tick's TRACTION GATE stops being walkable. That gate
is `MoveTuning.MaxSlopeRadians`, widened by a hysteresis band while the character already has footing (17.30.0,
see the next section). Until
17.26.1 that was a GATE which REFUSED a move outright, and refusal produced a bug at each setting of its own
tightness: loose enough and a repeated jump ratcheted up a sheer face
([#440](https://github.com/APKiwiOrg/KhaozEngine/issues/440)), tight enough to stop that and sideways movement
into a face while jumping read as an invisible wall eating lateral air control. Terrain does not refuse, so
[#442](https://github.com/APKiwiOrg/KhaozEngine/issues/442) replaced the gate with two rules. Both are
unconditional, and `MaxSlopeRadians` keeps its exact meaning as the traction threshold (17.30.0 gave that
threshold a memory and the slide a friction ramp, both with their own knob, without changing what it means).

**1. Wall slide.** A horizontal move whose destination is BOTH steeper than the tick's traction gate AND above what this
tick can REACH is a wall contact:

    the into-face component of the move dies, the along-face component survives

The REACH is the tick's OWN RESOLVED UPWARD MOTION and nothing else - `max(0, vVel * dt)`, so zero while it falls
and zero while it walks on the flat (17.29.0, [#468](https://github.com/APKiwiOrg/KhaozEngine/issues/468), then
17.31.0, [#486](https://github.com/APKiwiOrg/KhaozEngine/issues/486)). A step is what footing buys, and no tick has bought a
step onto ground past its own traction ceiling: a tick may be seated at or below the height it began at, and a
rising one exactly as far up as its own velocity carries it. **Altitude on steep ground comes only from real
velocity, never from the ground clamp.** Read as a `StepHeight` this admission was a climb and a bounce, both times
because the clamp seats the capsule on whatever column the admission reaches. It let walking at a 74 degree cliff
gain 2.3 to 2.7 m/s while `VerticalVelocity` reported falling at 5 to 7 (and pay MORE at a higher tick rate, the
ceiling being a `StepHeight` per tick against gravity's `g*dt` takeback), and while it was still granted to GROUNDED
ticks it seated a walking character onto a cliff toe that the same tick's support decision then refused, flickering
the falling pose at every steep-face base (112 footing flips in 600 ticks at 30 Hz on a 60 degree face). A SLIDING
tick takes the same allowance read off the slide's own resolved vertical, plus a 1 mm float slack it alone needs,
because its advance lies in the surface plane and its rise therefore EQUALS its resolved vertical to the last float.

**Walkable ground and band ground never read the reach at all**, because the steepness test returns first, so every
real step-up, step-down and stair glide is exactly what it was. The reach decides one thing only: what a tick may do
about a destination it has already been told it cannot stand on. A DESCENDING destination is admitted at any reach
(it rises by a negative amount), which is how cresting onto a steep face from above still enters a slide.

The face's horizontal direction is the XZ projection of the HEIGHT-DERIVED plane at the destination (see "Which
delegate answers what" below), with the movement direction standing in when that projection is degenerate, which
meets the face head-on and kills the whole move - the conservative direction. Both conditions are load-bearing.

**What survives is then made LEVEL on the column the walker is standing on** (17.32.0,
[#502](https://github.com/APKiwiOrg/KhaozEngine/issues/502)). The face above says which component the wall eats. It
does not say what altitude the survivor takes, and on a face that BENDS in plan it quietly buys some: the contour it
hands back is level on the plane at the DESTINATION, so a step along that line taken from the walker's own position
cuts inside the contour by the sagitta of a chord and climbs by `G * L^2 / (2R)` whatever its length. A walker
leaning on a bank is resting exactly ON its traction ceiling, so the only endpoints that climb at all are past it,
and the ride became a slow oscillation across that ceiling: the step is committed, the support decision at the end
of the same tick takes the footing, the character slides back down and walks in again. That is what a playtest
reports as getting stuck sprinting alongside a bank and as the falling pose flickering on and off while walking one.
So the surviving travel has its component along the WALKER's own column's outward removed, which makes it level on
the surface the character is actually standing on and, on a bend, descending by that same sagitta instead of
climbing it. Measured over the fixture bank sweep: 543 footing flips to 2, 5102 airborne ticks to 57, a peak
along-face speed of 158 percent of commanded to 105, a climb creep of 2.9e-4 m per tick to 1.4e-5, and the one ride
past the shortening ladder's coverage ceiling from a dead park to full travel.

Three gates, and the third is a SIGN. It is taken only when the walker's own column stands at most two degrees
short of the tick's traction gate (so it is resting on the face rather than on a walkable floor with a wall ahead
of it), only when it costs under 1.2 percent of the travel (so the two reads describe one surface rather than
two), and only when it LOWERS what the step asks the surface for. That last one is what makes the rule right on
both kinds of bend rather than one: on a CONCAVE face - a cove, a bowl, the inside of a curve - the destination
anchor is the one that already descends by the sagitta and the walker's own column is the one that climbs, so the
same arithmetic would add exactly the rise it removes on a convex face. Measured on the exact mirror of the bank
sweep, without the sign gate that costs 471 footing flips and 13225 airborne ticks of 12600 against a pre-#502
engine that spends 6 and 171, with one row sliding a walker 2.04 times as far as the stick asked. With it, all
sixty mirror rides reproduce the pre-#502 ride to every digit while the convex sixty keep every number above.
Neither the ladder below nor the wide-face read changes: this decides where along the surface the character is
pointed and never what altitude it may take.
The steepness test is what leaves walkable ground untouched, since a fast run up a legal ramp can rise more than a
`StepHeight` in one tick. The height test is what keeps this a CONTACT rather than the old gate for the ticks that
have no footing: ground at or below the feet is admitted, so a fall, a graze and a slide all still meet the surface
and ride it, and rule 2 is what makes doing so worthless. The projected move is re-tested and refused outright only
if it still lands in a wall, which happens in
a concave corner and is what keeps an XZ from ever being committed under terrain. There is no outward-move
exemption: a move that the heights say rises past the reach is a wall contact whichever way it points.

**2. No traction.** A surface steeper than the tick's TRACTION GATE grants no support. `Grounded` stays false, so there
is no jump, no coyote refresh and no landing latch on the face. The character is still SEATED on it (the ground
clamp forbids penetration) but it slides: gravity is decomposed against the surface normal and the tangential
component integrates into the carried `MoveState.VerticalVelocity` and `MoveState.HorizontalVelocity`,
accelerating it down the fall line until it reaches walkable ground (that landing is where `LandingImpactSpeed`
fires, from the fall the slide accumulated), open air, or water.

The surface frame is two unit vectors from the normal's Y and its horizontal direction: a down-slope tangent
`T = (ny*hx, -h, ny*hz)` and a level contour `C = (-hz, 0, hx)`. Gravity along `T` is exactly `Gravity * h`, and
along `C` exactly zero. **A contact deletes the into-surface component and nothing else**: the resolve
projects the carried velocity onto `T` and `C` and rebuilds from those two, so the normal component is gone by
construction and both survivors are kept in full.

- The **contour** speed is what a fast run ACROSS a face carries, and following it costs no drop at all. Carrying
  the fall line alone (the first cut of this model did) stopped a 14 m/s fall running parallel to a wall dead on the tick it merely
  brushed the wall.
- The **fall-line** speed is SIGNED. A jump grazing a face arrives with up-slope motion, and clamping it to zero
  deleted the launch outright. Gravity accumulates downward along it whatever the sign, so a rising slide
  decelerates, reverses, and comes back down. What carries the no-ascent property is having no FOOTING, not a
  clamp: there is nothing to re-launch from up there, so a cycle hands the whole rise back.
  **The payout is large, and intended.** The contact keeps the run INTO the face too, so the reach up a face is
  the launch's whole kinetic energy, `v^2 / (2 * Gravity)`, whatever the angle. A running jump at the shipped
  tuning launches at `sqrt(JumpSpeed^2 + RunSpeed^2)` = 15.5 m/s and is worth 4.8 m of reach against a bare
  vertical apex of 1.92 m, so 2.4x (measured 4.91 m on a near-gate 46 degree face, the best converter there is).
  Players can briefly ride a face upward on jump energy. They cannot keep any of it.

Because the rebuilt velocity lies entirely in the surface plane, the committed drop is precisely the drop the
committed horizontal travel needs, so the character stays glued to the face instead of bouncing off it. Terminal
is `MaxFallSpeed`, read through the surface so the vertical component lands exactly on it, and the horizontal
carry is clamped to the wire's own per-axis ceiling so the sim can never commit a velocity `MovementState` would
replicate as a different number (only reachable on a gate below about 21 degrees).

Input while sliding steers along the CONTOUR at the usual `AirControl`-scaled speed and has no authority along the
fall line in either direction - no new knob. Up-slope authority would be traction by another name, and down-slope
authority buys a hop off the surface on every tick it is held, which is a visible bounce on any moderate slope.
The steer is a per-tick term on the commanded velocity and is **not** folded into the carry, so contour speed
evolves only by contact and never by held input: a player steers across a slide at a fixed rate on top of whatever
contour momentum the fall gave them, and cannot pump the two into each other. That rule is symmetric, and the
collision clip is what makes it so. The advance is driven by the COMMANDED velocity, so the clip reads its denial
verdict from that same vector and sheds only the carry's own share of the committed displacement. Measuring the
carry alone against a displacement the steer helped produce reads the steer's travel as a collision denial and
rescales the whole carry, fall line included. **Input adds nothing to the carry and takes nothing from it. Only
geometry may shed it.**

**A BODY THE WORLD IS HOLDING UP is supported**, the one exception to rule 2, and there are two ways to read that
off a tick. Either the plane the HEIGHT FIELD describes across the capsule's own footprint is standable (the
body-scale reading, 17.29.0), or the tick carried a real fall (a downward speed past `Gravity * max(CoyoteTime,
dt)`) and committed measurably less descent than that fall demanded (the dynamic reading). Support means `Grounded`
true, jump enabled, coyote refreshed, and the swallowed fall latched as a landing. The test is stateless and
tick-local, and the dynamic half cannot arm at a jump apex, where the vertical speed is near zero by definition,
which is what keeps the #440 ratchet dead.

**Either reading also requires the ground to FOLD BACK ON ITSELF** (17.29.0). The normal is sampled over the
capsule's footprint ring and some pair of horizontal fall lines must oppose by more than 120 degrees, where two
unit fall lines sum to a vector no longer than either alone. Below that the ring still agrees on a direction to
leave by, which is a face however folded. Past it there is no downhill left to take, which is the wedge. Without
it, a shortfall alone could not tell a gully from a face - and on a face whose normal is smoothed over a wider
stencil than its height field's detail (what a real terrain sampler hands back) the shortfall is structural rather
than occasional, so an open cliff granted support steadily: five grants inside one measured climb, each a full
launch for a player holding jump, with the ring reading 0 of 8 samples walkable and its fall lines spread over 1
to 3 degrees. The fold test is equally what keeps footing off the TOE of a cliff, where a capsule a centimetre
past the knee spans mostly flat ground (so the body-scale reading alone would grant) while every fall line there
still points the same way.

The motivating case is a **concave crease** (a V-gully, the inside of a cleft), which is where the rule's
`SlideWedged` name comes from: a capsule there can neither be granted support by its own steep column nor slide
out, because the fall line of either wall points into the other and the wall contact removes the whole horizontal,
so the first cut of this model soft-locked, with a held jump that could never fire.

Support is per-tick, so a character parked in a crease reports a grounded PULSE rather than steady footing: about
one tick in two, since a grant ends the tick grounded, a grounded tick is not a slide contact, and the tick after
it is refused again by the point-sampled normal. Measured at the shipped tuning: 194 grounded ticks in 400 and
1994 in 4000. **Consumer note:** the pulse does not rattle `LandingImpactSpeed`, because one tick of gravity
between grants is not a fall worth latching - measured, 8 latches over 400 ticks and the same 8 over 4000, all of
them during the arrival, the first at 7.4 m/s and the loudest after it at 1.3. Gate a landing sound on the impact
SPEED anyway, which is what makes it robust to the tuning as well.

**Which delegate answers what (17.29.0).** You supply two descriptions of the same surface and they do not have to
agree, because a smoothed or lower-resolution normal field over a heightmap disagrees with its own heights
everywhere. So each has one job:

| Question | Answered by |
| --- | --- |
| Is this ground too steep to stand on? | the `groundNormal` delegate |
| Does the ground under the footprint fold back on itself? | the `groundNormal` delegate |
| Which way is the fall line, the contour, the face of this wall? | the `groundHeight` field |
| How far above my feet is the ground I am moving onto? | the `groundHeight` field |

The geometry answers come from a plane sampled off the heights by a central difference at `CapsuleRadius` either
side of the point, in a fixed order, so a reconcile replay derives the same plane bit for bit. **The invariant that
buys: the plane the slide resolves against IS the surface the ground clamp seats to**, so the clamp can never hand
back altitude the resolve did not account for. Before it, the two disagreeing pumped energy every tick and 44 of
360 headings still climbed a creased 74 degree face at 120 Hz, the worst by 389 m in 20 seconds with no jump and
no footing grant. Nothing to configure: an analytic surface whose normal IS its own gradient behaves exactly as
before, and where the two differ the heights now win. A slide also re-seats DOWN onto the surface within the
contact skin (never up), so the sampled plane's float noise cannot drift the capsule off the face over a long
fast slide.

Consequences worth knowing. **Climbing self-defeats** rather than being fenced, so the #440 jump ratchet is dead:
landing on a face lands in a slide. **Prop support always wins** - only the analytic terrain is traction-less, so
a plank over a ravine or a stair against a mountain still carries a character exactly as before. Descent, walk-offs
and jump-offs are unchanged. And it needs no new wiring, no carried state and no wire change: the slide rides
`HorizontalVelocity` and `VerticalVelocity`, both of which already replicate, so it replays bit-identically
through `ClientPrediction.Reconcile`.

Everything applies identically to the grounded path, the airborne-momentum path, `StepTowards` (the NPC path) and
the horizontal-only overload. **A game that used the gate as a cliff guardrail now gets real falls, and a steep
face now slides** - that is the fix, and it is a behaviour change rather than an opt-in.

### Traction hysteresis and slide friction (17.30.0)

`MaxSlopeRadians` was a bare per-tick binary and the slide had no friction, so ground sitting ON the threshold
behaved like a cliff edge in the model where it is a hillside in the world. Measured on a Ruinborne bank running
40.0 to 41.8 degrees against a 40 degree gate: 43 footing flips in 330 ticks, stalled 2.73 m up a 7.6 m climb. Two
composable mechanisms fix it ([#475](https://github.com/APKiwiOrg/KhaozEngine/issues/475)), both **default-on**.

**Traction hysteresis (`MoveTuning.TractionHysteresisRadians`, default 3 degrees).** The decision is
state-dependent:

    footing is GRANTED at MaxSlopeRadians, and KEPT to MaxSlopeRadians + TractionHysteresisRadians

So a walk across a bank that straddles the gate holds ONE continuous footing decision instead of flipping every
tick, while a body arriving WITHOUT footing (a landing, a slide, an apex graze) is judged at the bare gate and
slides. The band is a ceiling on what footing may keep and never a route to footing, so **the steepest ground a
character can stand on is exactly gate plus band, by any route**. The memory is `MoveState.Grounded`, which the sim
already carries and the wire already replicates, so there is no new state and a reconcile replay reaches the same
answer. The consequence that IS the mechanism: a uniform face at gate plus two is walkable, indefinitely, by a
character that walked onto it, and refuses one that fell onto it.

**Slide friction (`MoveTuning.SlideFrictionRampRadians`, default 8 degrees).** The fall-line acceleration ramps in
over a band past the gate instead of arriving at full strength:

    accel scale = clamp((surface slope - MaxSlopeRadians) / SlideFrictionRampRadians, 0, 1)

At the shipped 45 degree gate, a 46 degree face accelerates at 2.25 m/s^2 along the fall line against 17.99
unscaled, a 49 degree one at 9.43 against 18.87, and 53 degrees and steeper is untouched. One second of sliding from
rest on a 46 degree face drops 0.836 m rather than 6.684 m. The slope is read off the same HEIGHT-derived plane the
resolve is built from, not the classification normal. It scales the ACCELERATION and not the speed, so it is not a
terminal: a long enough marginal face still reaches `MaxFallSpeed`, over a much greater distance.

**Friction NEVER applies to a rising slide.** Scaling gravity's deceleration would multiply the reach of a launch up
a face by `1 / scale` (unbounded as the scale approaches zero at the gate), which is the #440 ratchet by another
route. A rising slide decelerates at full gravity, always, so the reach of a running jump onto a marginal face is
exactly what it was before friction existed and every "no higher than" bound above is untouched.

**One traction truth per tick.** The gate is resolved ONCE from the footing the tick started with and handed to the
slide contact, the wall contact on all three horizontal paths, the slide resolve, the support decision, the wedge
and the step-down hold. The wall contact reading the same widened gate is load-bearing: otherwise a run up a bank
the band is holding footing on would meet a fence built from the ground under its own feet.

**The step-down hold runs the traction test too, from 17.30.0.** A drop within `StepHeight` normally seats a
character grounded in one tick so a doorstep reads as a step rather than a fall. That hold used to skip the traction
test across its whole band, because the test it read is only computed for the smaller drops the `GroundedEpsilon`
ground stick reaches, so a step-down onto a face far past the gate seated grounded and handed out a jump. It now
asks the same question against the same tick-resolved gate: past the gate the seat is refused and the character goes
over the edge as any walk-off does. Walkable treads are under the gate, so stair descent is untouched.

**Compatibility.** Both knobs at 0 (which is what a bare `default(MoveTuning)` reads, and what a negative or NaN
value reads too) restore the 17.29.0 model bit for bit. Ground well under the gate is untouched either way. The
horizontal-only `Step(Vector3, ...)` overload always uses the bare gate, because it takes no support decision and so
has no footing to remember.

### Movement medium (wading)

Both overloads take an **optional medium provider** `medium: (x, z, feetY) -> MovementMedium`. It is a **pure,
deterministic read of the game's world** (a lake plane, a river, a swamp) that the game supplies **on BOTH heads**
(the authoritative server tick and the client's prediction replay), exactly like the `groundHeight` delegate: the
engine never computes water itself. When a sample reports `InWater`, horizontal speed is scaled by a **depth wade
ramp** - full speed at ankle depth (`MoveTuning.WadeStartDepthFraction`) down to a floor
(`MoveTuning.WadeMinSpeedScale`) at chest depth (`MoveTuning.WadeEndDepthFraction`), where depth is
`WaterSurfaceY - feetY` as a fraction of body height (`2 * CapsuleHalfHeight`). The medium's own `WadeSpeedScale`
composes as a further per-sample multiplier (a swamp zone dial). **A null provider (or a dry sample) is
bit-identical to the pre-medium behaviour.** `CharacterMovement.WadeSpeedScale(x, z, feetY, tuning, medium)`
exposes the same scale for callers that predict or echo the wade factor (floored at 0, **uncapped above** so a
zone scale > 1 lifts the result past 1).

### Surface swim v1

Past the wade band the same seam drives **surface swim** (vertical-physics `Step` only). Submersion reaching
`MoveTuning.SwimEnterDepthFraction` (default 0.65, chest, where the wade ramp bottoms out) flips the character into
swimming; it exits below the LOWER `MoveTuning.SwimExitDepthFraction` (default 0.55) or on leaving the water - the
enter/exit gap is a **hysteresis band** (no flicker at the boundary), carried on `MoveState.Swimming`. While
swimming, gravity and ground-snap are suspended, the capsule **settles to a buoyancy waterline**
(`MoveTuning.SwimSurfaceSubmersionFraction`, default 0.6) via an exact analytic **critically-damped** approach
(`MoveTuning.SwimBuoyancyStiffness`, default 8 - unconditionally stable, no oscillation), horizontal travel is
`MoveTuning.SwimSpeed` (default 2.5, the zone `WadeSpeedScale` still composing), and jump is a **hop-out in
near-shore shallows only** (fires the ordinary jump + drops swim when submersion is within the exit band; ignored
in deep water). `CharacterMovement.ResolveSwimming(wasSwimming, medium, feetY, tuning)` exposes the pure enter/exit
decision. **A null provider never engages swim.** The swim flag replicates via NetWorld's `MovementState.Swimming`
(a breaking wire change: `MoveProtocol.WireProtocolVersion` -> 3).

## Types

- **`MoveCommand`** - movement intent: camera-relative XZ axis, run flag, camera yaw, jump bit, and (17.26.0) the
  `FaceCamera` flag, which asks the character to face `CameraYaw` instead of its travel direction. `FaceCamera`
  changes `MoveState.FacingYaw`, so a strafing character keeps its body pointed at the
  camera and - the case that is impossible without it - a character with NO movement input can turn on the spot.
  `false` (the default, and what every pre-facing construction site produces) is the pre-facing behaviour exactly.
  Since 17.30.0 the flag can also change the SPEED, since a character with a fixed front can be charged for moving
  sideways or backwards relative to it (see "Directional speed under `FaceCamera`"), which is opt-in and neutral by
  default.
- **`MoveState`** - carried kinematic state: position, `VerticalVelocity`, `Grounded`, coyote/buffer timers,
  `Swimming` (surface-swim flag, carried tick-to-tick for the enter/exit hysteresis), and `SpeedScale` (see below).
  Also the stair-glide
  step-climb signals: `ClimbRate` (signed step-climb rate in m/s, replicated quantized as
  `MovementState.ClimbRateQ`, positive on a paced stair-climb run and negative on a stepped-down descent, 0
  when not on a step climb) and `StepDeltaY` (sim-local, not replicated: the signed vertical delta a single
  discrete step commits, read once by the local owner at the client-prediction boundary). Both are what a
  presentation layer consumes to glide/ease the drawn feet on stairs, see the "Animated characters" section
  of `docs/USING-KHAOZENGINE.md` for the full `CharacterController3D.ClimbRate` / `.StepDeltaY` consumer
  contract. `ClimbRateEwma` is a sim-local (not replicated) smoothing average `ClimbRate` is stamped from on
  a run tick, with no consumer of its own.
- **`MoveState.SpeedScale`** (14.26.0) - per-entity HORIZONTAL speed multiplier: haste (`> 1`), slow (`< 1`),
  root (`0`), unmodified (`1`, the default). A movement INPUT the step reads and carries through unchanged, and nothing
  in the sim derives or decays it. It multiplies INTO the existing speed product rather than replacing any of it, so
  it composes with the grounded/`AirControl` term and the medium's `WadeSpeedScale`, applies while swimming, and
  scales a jump's horizontal reach (jump HEIGHT is untouched). Under `MoveTuning.AirMomentum` the airborne
  composition drops the `AirControl` term (air control becomes a steering authority there, not a speed scale) and
  the multiplier scales the commanded speed the arc steers toward. Assignment clamps to `>= 0`: a negative
  multiplier would reverse travel against the command, which is never what a modifier means.
  It is a **property, not a field like the rest of this struct**, deliberately: a struct field cannot have a
  non-zero default, so a raw field would make `default(MoveState)` (and every pre-existing initializer) a character
  frozen at 0 m/s. The backing store is the offset from 1, which makes the zero default mean "unmodified", exactly.
  On a networked player the server is the sole author (`WorldServer.SetSpeedScale` / `ShardedWorldServer.SetSpeedScale`,
  replicated as `MovementState.SpeedScaleQ`). A server-only NPC or a single-player controller just sets it.
- **`MoveState.CommandedVelocity`** (16.0.0, was the `float CommandedSpeed` field in 14.27.0) - sim-local step
  OUTPUT (not replicated): the unconstrained horizontal VELOCITY in m/s the step actually commanded this tick,
  before the wall slide, collision, or the play-area clamp denied any of it. Its magnitude is the whole speed
  product (walk/run or `SwimSpeed`, times air control, the wade ramp, the zone scale, and `SpeedScale`), so with
  nothing denying the move the step travels exactly `CommandedVelocity * dt`. `(0,0)` on an idle tick.
  `CommandedSpeed` survives as a computed readonly property (`CommandedVelocity.Length()`), so reads still compile
  and writes do not. It exists so the server-side movement-anomaly check can measure ONLY the denial: rebuilding
  that product downstream is what made a swimming or wading player read as a speed hacker. It is a vector and not
  a scalar because the check needs the direction of travel too, and under `MoveTuning.AirMomentum` that is the
  conserved `HorizontalVelocity` rather than the input. Same "the sim exports what it knows" pattern as
  `ClimbRate`.
- **`MoveState.HorizontalVelocity`** (16.0.0) - `Vector2` (XZ, m/s), the CARRIED airborne inertia and new
  replicated state (`MovementState.HorizontalVelocityXQ` / `HorizontalVelocityZQ`). Maintained on EVERY tick
  regardless of `MoveTuning.AirMomentum` and consumed only when it is on, so with momentum off the field is
  written and never read. What is stored is the step's intended velocity CLIPPED to what the collision resolve
  delivered, projected along its own direction and clamped into `[0, |intended|]`: free flight leaves it exactly
  untouched, a head-on wall clips it to ~0, a glancing wall sheds magnitude and keeps direction, and no
  depenetration nudge or play-area clamp can ever inject speed into it. `SwimStep` replaces it with the swim's own
  commanded velocity, so a flight into water drops its arc at the waterline. `default` (zero) is "carrying
  nothing".
- **`CharacterMovement.IntendedHorizontalTargetAtSpeed(position, cmd, dt, speed)`** (14.27.0) - the unconstrained
  target a command reaches in one step at an EXPLICIT speed. The existing `IntendedHorizontalTarget(..., tuning,
  speedScale)` now delegates to it, so the two share one camera basis. Correct for any caller whose travel
  direction is its input direction, which is every grounded step and every airborne one without momentum.
- **`CharacterMovement.IntendedHorizontalTargetAtVelocity(position, velocity, dt)`** (16.0.0) - the vector form,
  `position.XZ + velocity * dt`. No command and no camera basis, because the direction comes from the velocity.
  Pair it with `CommandedVelocity`. This is the form the movement-anomaly check uses.
- **`MovementMedium`** - the fluid medium at one world sample the medium provider returns: `WaterSurfaceY`,
  `InWater`, `WadeSpeedScale` (a per-sample zone multiplier, default 1). `default` / `MovementMedium.Dry` is dry land.
- **`MoveState.LandingImpactSpeed`** (17.26.0) - sim-local step OUTPUT (not replicated): the DOWNWARD speed in m/s,
  non-negative, that this tick's landing erased, captured before the ground contact zeroes `VerticalVelocity`. Set
  on exactly the tick a character transitions airborne to grounded and 0 on every other tick, so it is a per-tick
  EVENT rather than carried state. It is the authoritative fall-damage input, following the `StepDeltaY` precedent
  of exporting the fact the sim already computed: the speed is gone by the time anything downstream can read the
  state, so without it a game has to finite-difference a position across the very tick the ground clamp moved it.
  Inherently capped by `MaxFallSpeed`, which is terminal velocity and therefore physical. A landing that a
  BUFFERED JUMP re-launches on the same tick still reports its impact (so `Grounded` may be false while this is
  nonzero), because suppressing it would let a bunny-hop cancel fall damage. Swimming never fabricates one (a swim
  tick is never grounded). A spawn or teleport reports about `Gravity * dt` on its first tick, honestly, since
  `default(MoveState).Grounded` is false, so **consumers should threshold** rather than treat any nonzero value as
  a fall. The server-side read path is NetWorld's `WorldServer.OnAfterTick` / `ShardedWorldServer.OnAfterTick`.
- **`MoveState.SupportGranted`** (17.29.0) - sim-local step OUTPUT (not replicated): `true` on a tick that RESOLVED
  support, read BEFORE the jump consumes it. `Grounded` is the state the tick ENDED in, and those are not the same
  fact: the jump step launches off the support the tick just found and sets `Grounded` false on the way out, so a
  player holding the button reports false on every tick of a hop cycle and any support granted underneath is
  invisible. That gap is what let a 21 m cliff climb read as ZERO footing grants while it was taking a wedge grant
  every few seconds ([#468](https://github.com/APKiwiOrg/KhaozEngine/issues/468)), so this is the signal a
  server-side anomaly check or a telemetry recorder needs to see a character finding footing where the terrain
  grants none. Set on EVERY supported tick rather than only on transitions (a transition is still derivable by
  comparing it with the previous tick's), `false` on a swim tick, and mirrored server-side onto
  `MovementState.SupportGranted` as a sim-local field exactly as `LandingImpactSpeed` is.
- **`MoveState.FacingYaw`** (17.26.0) - the CARRIED heading in radians, in the same convention as
  `MoveCommand.CameraYaw`: 0 faces world -Z, a positive angle swings toward -X, canonical range `[-pi, pi)` with
  the low end inclusive. See "Authoritative facing" below. It affects NO position output, which is what makes
  every existing game bit-identical across the feature. Replicated as `MovementState.FacingYawQ`.
- **`MoveTuning`** - all speed and feel constants:
  `WalkSpeed` / `RunSpeed` / `CapsuleHalfHeight` / `MaxSlopeRadians` / `CapsuleRadius` (default 0.4) /
  `StepHeight` (default 0.4) /
  `Gravity` / `JumpSpeed` / `MaxFallSpeed` / `CoyoteTime` / `JumpBuffer` / `AirControl` / `GroundedEpsilon` /
  `WadeStartDepthFraction` (default 0.15) / `WadeEndDepthFraction` (default 0.65) / `WadeMinSpeedScale` (default 0.45) /
  `SwimEnterDepthFraction` (default 0.65) / `SwimExitDepthFraction` (default 0.55) / `SwimSpeed` (default 2.5) /
  `SwimSurfaceSubmersionFraction` (default 0.6) / `SwimBuoyancyStiffness` (default 8) /
  `MaxStepClimbSpeed` (default 3.5) / `AirMomentum` (default false) / `AirBrakeAccel` (default 0) /
  `FacingTurnSpeed` (default `float.PositiveInfinity`, which snaps) /
  `TractionHysteresisRadians` (default 3 degrees) / `SlideFrictionRampRadians` (default 8 degrees) /
  `StrafeSpeedScale` (default 1) / `BackpedalSpeedScale` (default 1) / `BackpedalAllowsRun` (default true).
- **`MoveSector`** (17.30.0) - which directional sector a command's axis falls in relative to the character's
  facing: `Forward` / `Strafe` / `Reverse`. Returned by `CharacterMovement.Sector(cmd)`, and what the directional
  speed scales are charged by while `MoveCommand.FaceCamera` is held. Public so presentation (which locomotion
  animation to play) reads the same answer the movement did rather than a hand-copied predicate.

## Airborne momentum (16.0.0, opt-in)

Off by default. With `MoveTuning.AirMomentum = false` a character in free flight has no inertia: the horizontal is
recomputed from the command every tick, so a mid-air `SpeedScale` change collapses a committed arc and releasing
input stops horizontal travel dead. That is the pre-16.0.0 model and it is what every game gets until it opts in.

With `AirMomentum = true` the AIRBORNE step flies the carried `MoveState.HorizontalVelocity` instead. A jump at
speed S travels its whole arc at S whatever the command does afterwards, releasing input holds both speed and
direction, and pressing into the arc can ACCELERATE it but never brake it. **Grounded motion is untouched either
way** - it stays instant-to-target with no acceleration and no friction. `AirControl` becomes the STEERING
authority over the direction of travel rather than a speed scale, which gives it a reading it never had: `1` is
still full control (an instant 180 mid-flight, still at the carried speed), and `0` is now a true ballistic arc
rather than "frozen horizontally in mid-air".

`MoveTuning.AirBrakeAccel` (m/s^2, default 0) bleeds a conserved speed down toward a STRICTLY SLOWER commanded
speed, stopping there and never going below it. `0` is pure conservation. It is there for a root or a snare
landing mid-flight, and it is the one knob that dials back toward the old feel without turning momentum off. It
never accelerates: braking is its job alone and steering is the blend's, and the two deliberately do not overlap.

Networked play needs nothing extra - the carried velocity replicates and survives a reconcile - but it is a wire
break, see `KhaozEngine.NetWorld/README.md`. Rationale and the full per-tick resolve are in
`docs/design/AIRBORNE-MOMENTUM-DESIGN-2026-07-26.md`.

## Authoritative facing (17.26.0)

Which way a character POINTS is now part of the movement model rather than something each presentation layer
re-derived from a position delta. That derivation cannot turn a stationary character at all, and it reads a fast
diagonal or a slope walk as a turn that never happened.

`MoveCommand.FaceCamera` selects the target: with it set the character turns toward `CameraYaw` whatever the move
axis is doing, and without it toward the yaw of the commanded world-space move direction while there is input, or
the current heading when there is none (an idle character holds its heading rather than snapping to a default).
The result is `MoveState.FacingYaw`, carried tick to tick, radians, **0 faces world -Z and a positive angle swings
toward -X** - the basis the step already resolves a camera-relative command in (forward is
`(-sin yaw, 0, -cos yaw)`), so a character walking straight forward under camera yaw `y` faces exactly `y`.
Canonical range `[-pi, pi)`, low end inclusive. Convert with `CharacterMovement.FacingYawOf` and
`CharacterMovement.WrapYaw`. A consumer whose gameplay basis is the opposite converts at its own boundary, once.

`MoveTuning.FacingTurnSpeed` (rad/s) rate-limits the turn, always along the SHORTEST ARC, and lands exactly on the
target on the tick the remaining gap fits inside one step's budget - so a rate changes how long a turn takes and
never where it ends. The default `float.PositiveInfinity` SNAPS, deliberately rather than some plausible finite
rate: before facing became authoritative state a consumer pointed its model straight at `CameraRelativeDir` with
no smoothing, so infinite is the feel every existing game already has. A finite value (2 to 10 rad/s is the usual
range) leans the body into its turns and does so identically on the server, in client prediction and on every
remote, because the turn is part of the authoritative step rather than a smoother each end runs its own version
of. A value of 0, which is what a bare `default(MoveTuning)` reads, FREEZES the heading rather than meaning "no
limit": treating 0 as unlimited would make the un-configured case the most aggressive setting there is.

The HEADING is an OUTPUT and nothing else (the `FaceCamera` FLAG is a different matter since 17.30.0: see
"Directional speed under `FaceCamera`" below). No position, velocity or grounded value is derived from it, so
every existing game is bit-identical on position across the feature. Networked play needs the heading on the wire
(it is carried state that the next tick turns FROM), which is a wire break: see `KhaozEngine.NetWorld/README.md`.
Rationale and the phase plan are in `docs/design/PHYSICS-LOCOMOTION-DESIGN-2026-08-02.md`.

## Directional speed under `FaceCamera` (17.30.0)

A character that turns to face wherever it walks has no reverse. One pinned to the camera does, and the usual
third-person feel charges for it. Three knobs, consulted ONLY while `MoveCommand.FaceCamera` is held:

| Knob | Default | What it does |
| --- | --- | --- |
| `MoveTuning.StrafeSpeedScale` | `1` | Speed multiplier in the strafe sector. Run honoured. |
| `MoveTuning.BackpedalSpeedScale` | `1` | Speed multiplier in the reverse sector. |
| `MoveTuning.BackpedalAllowsRun` | `true` | Whether the run bit is honoured while backing up. `false` puts the reverse scale on `WalkSpeed`. |

All three are neutral by default, so a game that never sets them is bit-identical to every release before 17.30.0.
Without `FaceCamera` nothing is consulted at all.

**The sector rule.** `CharacterMovement.Sector(cmd)` classifies the command's own camera-relative axis (never a
world vector) into `MoveSector.Forward` / `Strafe` / `Reverse`. With `a = |Move.X|`: `Move.Y >= a` is forward,
`Move.Y <= -a` is reverse, everything else is strafe. An absolute value and two comparisons, so it is exact on
every head, needs no `atan2`, and does not care about the axis's LENGTH (half a stick deflection classifies as a
full one does). Forward and reverse are CLOSED wedges, which is what decides the boundary rays: exactly 45 degrees
is forward and exactly 135 is reverse. That matters because a keyboard lands on them - the WASD axis is built from
whole +/-1 components, so W+D IS the vector `(1, 1)`, and reading the most common forward diagonal in the game as
a strafe would be wrong. Mirrored, S+D is a retreat with a lean rather than a sidestep. An idle command reads as
`Forward`, the sector that scales nothing. `Sector` is public so a consumer picking a locomotion animation reads
the same answer the movement did.

**Where it applies.** The scale multiplies the resolved speed FRACTION at the player entry point, so it composes
once with `MoveState.SpeedScale`, the wade ramp, the medium's zone scale and `AirControl` in whatever combination
the tick is in, and it is inside `MoveState.CommandedVelocity` - which is what NetWorld's anti-cheat measures its
correction against, so a backpedalling player is denied nothing rather than reading as a full-speed correction on
every tick. Airborne command speed scales exactly as grounded does. A committed `AirMomentum` arc does NOT (the
carried velocity is not a command, so the scale steers it and never shrinks it). The world-space
`StepTowards` agent path has no camera and no sectors, so an NPC is untouched. A negative or NaN scale reads as 0
rather than reversing travel, and 0 itself is legitimate, with its ticks treated as idle downstream.

## Usage

```csharp
using KhaozEngine.Locomotion;

// Horizontal-only (top-down / no air):
Vector3 next = CharacterMovement.Step(pos, new MoveCommand(move, run, cameraYaw), dt,
                                      terrain.GroundHeight, MoveTuning.Default);

// Vertical physics (gravity + jump + swept 3D collision):
MoveState s = CharacterMovement.Step(s, new MoveCommand(move, run, cameraYaw, jump), dt,
                                     terrain.GroundHeight, MoveTuning.Default,
                                     groundNormal: terrain.GroundNormal, world: physicsWorld);
// s.Position, s.VerticalVelocity, s.Grounded

// Server-authoritative enemy NPC: same collision as the player, steered by a world heading.
Vector2 toTarget = Vector2.Normalize(new Vector2(target.X - a.Position.X, target.Z - a.Position.Z));
a = CharacterMovement.StepTowards(a, toTarget, run: true, dt,
                                  terrain.GroundHeight, enemyTuning,
                                  groundNormal: terrain.GroundNormal, world: physicsWorld,
                                  clampXz: bounds.Clamp);
```

Depends on `KhaozEngine.Primitives` and `KhaozEngine.Physics` (the `IPhysicsWorld` seam). No input, render,
or netcode dependency. Part of the `Foundation` umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
