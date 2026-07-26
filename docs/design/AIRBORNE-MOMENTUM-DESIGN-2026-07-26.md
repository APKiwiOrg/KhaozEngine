# Airborne horizontal momentum in CharacterMovement

Date: 2026-07-26. Status: complete, shipped 16.0.0.
Issue: https://github.com/APKiwiOrg/KhaozEngine/issues/320.
Consumer origin: https://github.com/APKiwiOrg/Ruinborne/issues/203 and
https://github.com/APKiwiOrg/Ruinborne/issues/225, scoped in Ruinborne's
`docs/design/2026-07-26-airborne-momentum-engine-scope.md`.

## The gap

`MoveState` carries `VerticalVelocity` and no horizontal velocity, so `CharacterMovement.StepCore`
recomputes horizontal motion from scratch every tick as a desired position:

```csharp
float speedScale = (s.Grounded ? 1f : t.AirControl) * wade * s.SpeedScale;
(float dx, float dz, float commandedSpeed) = DesiredHorizontalCore(...);
```

A character in free flight therefore has no inertia. Two consequences: a mid-air `SpeedScale` change
collapses a committed arc (Ruinborne's Quicken buff expiring at 30 m/s drops the flight to 6 m/s on
that tick), and releasing input mid-air stops horizontal motion dead.

## The model

Airborne only. Grounded motion is untouched: it stays instant-to-target, with no acceleration or
friction. Grounded momentum would change the feel of every game on the stack and is a separate,
later, opt-in decision.

Momentum sits behind a new `MoveTuning` knob that is **off by default**, so `MoveTuning.Default` and
every existing game are bit-identical to today until they opt in. The fleet has been burned once by
an engine bump silently retuning inherited `MoveTuning` defaults with a green build, so the default
is the load-bearing part of this design.

### Carried state

`MoveState.HorizontalVelocity` (`Vector2`, XZ, m/s) is new replicated simulation state. It is
maintained on **every** tick regardless of the knob, and **consumed** only when the knob is on. That
split is deliberate: it means the "unchanged at the default" claim is structural rather than
behavioural, because with the knob off the field is written and never read.

### Per-tick resolve, airborne, momentum on

```
carried    = s.HorizontalVelocity
ac         = clamp01(t.AirControl)
targetSpeed = (run ? RunSpeed : WalkSpeed) * wade * SpeedScale * speedFraction   // NO AirControl term
target     = moveDir * targetSpeed                                              // (0,0) with no input

steered    = carried + (target - carried) * ac        // ac=1 => full input authority over direction
                                                      // ac=0 => pure ballistic, input ignored
conserved  = |carried|
if AirBrakeAccel > 0 and targetSpeed < conserved:     // ONE-DIRECTIONAL: bleeds a fast arc down toward the
    conserved = max(targetSpeed, conserved - AirBrakeAccel * dt)   // command, never raises a slow one

speed      = max(|steered|, conserved)                // input may ACCELERATE, never brake below conserved
dir        = |steered| > eps ? steered/|steered|
           : |carried| > eps ? carried/|carried|
           : moveDir                                   // all-zero degenerates to no motion, speed is 0 there
v          = dir * speed
```

`AirControl` keeps its contract that `1` is full control, and gains a meaning it never had: `0` is now
a true ballistic arc rather than "frozen horizontally mid-air". Both are strictly behind the opt-in.

The brake's gate is load-bearing rather than cosmetic. Ungated, `max(targetSpeed, ...)` RAISES the
conserved speed whenever the command is faster than the arc, so a game that set `AirBrakeAccel`
alongside a low `AirControl` would find a snare accelerating a ballistic arc it is not even steering.
Acceleration is the steer blend's job alone, and the two must not overlap.

Behaviour this produces, which is the acceptance list:

| Situation | Result |
|---|---|
| Jump at 30 (hasted), buff expires mid-air, input held | stays 30, arc preserved |
| Jump at 6, haste lands mid-air | accelerates to 30 |
| Release input mid-air | speed and direction both hold |
| Jump straight up from rest, then press forward | accelerates from 0 to walk speed |
| Turn around mid-air at 30 with `AirControl` 1 | instant 180 at 30 |
| Turn mid-air with `AirControl` 0.5 | velocity bends toward the input over several ticks at 30 |

### Seeding, landing, walls, water

**Takeoff needs no special case.** The jump fires at the END of `StepCore` (step 5), so the tick that
leaves the ground computed its horizontal as *grounded*, at full speed with no `AirControl` term. Its
`HorizontalVelocity` is therefore the grounded speed, and the first airborne tick carries it. A jump
at speed S starts its arc at S by construction, with no takeoff branch to get wrong.

**Landing** needs no special case either. Momentum is not consumed while grounded, so the first
grounded tick recomputes horizontal from the command exactly as today.

**Collision clips, never injects.** After the position resolve, the stored velocity is the intended
velocity clipped to what actually survived, projected along its own direction:

```
achieved = (posXZ - startXZ) / dt
along    = dot(achieved, dirIntended)
if along >= |vIntended| * (1 - 1e-3):  stored = vIntended      // undenied: carry the intent through
else:                                  stored = dirIntended * clamp(along, 0, |vIntended|)
```

Free flight leaves it EXACTLY untouched, and the `1e-3` undenied tolerance is what buys the "exactly".
Without it, an undenied tick still round-trips through the measurement, where float rounding makes
`along` straddle `|vIntended|` while the upper clamp discards every high reading and keeps every low
one, so a carried arc can only ever ratchet DOWN. The bias is invisible near the origin and reaches
about 0.02 m/s over a ten-second arc at 12 km out, because a single float step on a large coordinate
is a measurable fraction of one tick's travel. The tolerance is a fraction of the intended SPEED, so
it suppresses that whenever one float step of the coordinate stays under 0.1% of a tick's travel,
which covers overworld range at run speed and does NOT cover a slow drift at extreme range (see
Deferred, below). It is far below any denial worth clipping for: a wall, a step hold, or a play-area
clamp removes whole percentages of the move, never a thousandth of it.

A head-on wall clips it to ~0. A glancing wall reduces the magnitude and keeps the direction. A
depenetration nudge or a play-area clamp can only ever reduce it, because of the upper clamp. This is
what makes the field safe: nothing in the collision resolve can inject speed into carried state.

**Water kills momentum.** `SwimStep` returns early and sets `HorizontalVelocity` from its own
commanded swim velocity, so entering water from a flight drops the arc rather than carrying it.

## The anti-cheat consequence, and why `CommandedSpeed` becomes a vector

`MovementAnomaly.CorrectionDistance` measures denial by rebuilding the intended target from the
command DIRECTION plus the step's exported `CommandedSpeed`. Under momentum the travel direction is
the conserved velocity, not the input direction, so a player who releases input mid-air at 30 m/s
would measure as a full-speed denial on every airborne tick and be reported as a speed hacker. The
scalar export is no longer sufficient.

`MoveState.CommandedSpeed` (a `float` field) is therefore replaced by
`MoveState.CommandedVelocity` (a `Vector2` field): the unconstrained horizontal VELOCITY the step
commanded this tick, momentum included. `CommandedSpeed` survives as a computed readonly property
(`CommandedVelocity.Length()`), so every existing read site still compiles. `MovementAnomaly` builds
its intended target as `prev.XZ + CommandedVelocity * dt`, which is exact under both models: with the
knob off, `CommandedVelocity == moveDir * commandedSpeed` and the check is arithmetically identical
to today.

This is the same lesson the `CommandedSpeed` doc comment already records, applied one level up:
export the fact the sim computed, do not reconstruct it downstream. Reusing the carried
`HorizontalVelocity` as a direction source instead would have worked today and would have coupled the
anti-cheat to the invariant that collision clipping preserves direction, which is exactly the kind of
silent coupling that broke it the first time.

Removing a public field is a breaking change, so this ships as a **major** bump.

## Wire

`MovementState` gains `HorizontalVelocityXQ` / `HorizontalVelocityZQ`, both `short`, quantum
`1f/256f` (an exact power of two, following `SpeedScaleQuantum`'s reasoning rather than
`ClimbRateQuantum`'s decimal, so both heads multiply by bit-identical values). Range +/-127.99 m/s,
which covers `RunSpeed * MaxSpeedScale` with room. Resolution 0.0039 m/s, which over a four-tick
reconcile window at 60 Hz is 0.00026 m of position drift.

`MoveProtocol.WireProtocolVersion` goes 6 to 7. `MovementState` is a built-in, unframed component, so
this is a hard break gated at the handshake by `WireGenerationAuthenticator`, not an additive one.

`MovementState.CommandedSpeed` (the sim-local, non-wire persistence slot the sharded head reuses)
becomes `CommandedVelocity`, a `Vector2`. It stays off the codec.

`PlayerMoveState.From` must seed `HorizontalVelocity` from the decoded wire value. `Reconcile` does a
full unconditional overwrite of the predicted state from the two replicated components, so a field
missing from that seed silently resets to the struct default on every correction and diverges mid-air
the moment a correction lands. That is the failure `SpeedScaleQ` was added to fix.

`CommandedVelocity` is NOT seeded from the wire on a client (it rides no codec, so it reads 0, which
measures as "no denial" rather than a spurious one, the safe direction and the existing behaviour).

## Tuning

Two new `MoveTuning` knobs, both appended so positional construction is unaffected:

- `bool AirMomentum = false`. The master opt-in. Off is today's model exactly.
- `float AirBrakeAccel = 0f`. m/s^2 at which conserved airborne speed decays toward a STRICTLY SLOWER
  commanded speed. `0` is pure conservation. It exists because a root or a snare landing mid-air is a
  real case a game may want to bleed the arc for, and because it is the knob that lets a game dial back
  toward the old feel without turning momentum off. It never accelerates: see the gate above.

`CharacterController3D` mirrors both, per the existing rule that its field defaults match
`MoveTuning`'s literal for literal.

## Acceptance

1. A character that jumps at speed S travels the arc at S regardless of a mid-air `SpeedScale` change
   or an input release.
2. Prediction and authority agree across the change on real geometry, not a flat fixture.
3. Every existing game's jump is unchanged with `AirMomentum` at its default. Proven by asserting the
   airborne advance equals the old closed form `moveDir * speed * AirControl * dt` under a mid-air
   `SpeedScale` change and an input release, not by a green build.
4. Reconcile-replay corrects a client mid-air and converges.
5. The anomaly check reports no denial for a legitimate momentum flight with released input.

## Deferred

**The undenied tolerance does not cover a SLOW arc at extreme range.** The clip's tolerance is a
fraction of the intended speed while the rounding error it defends against is a fraction of the
COORDINATE, so the two stop lining up once one float step of the position exceeds 0.1% of a tick's
travel: roughly `|coordinate| > speed * dt * 16800`, i.e. about 8 km at 30 m/s and 60 Hz, or about
1.7 km at walk speed. Beyond that some ticks fall back to the measured branch and the ratchet resumes,
though at a reduced rate. Measured, with an off-axis arc over a ten-second flight: a 6 m/s arc at 5 km
sheds 0.0096 m/s both before and after the fix, unchanged, while a 30 m/s arc at 12 km goes from
0.0218 m/s to exactly 0. Left alone because the magnitudes are small and the fix that removes the
limit entirely is a different shape: carry the achieved DELTA out of the collision resolve rather than
re-deriving it by differencing two absolute positions, which means threading a value through
`StepCore` rather than tuning a constant. Worth filing if a game ever runs slow airborne movement far
from the origin.
