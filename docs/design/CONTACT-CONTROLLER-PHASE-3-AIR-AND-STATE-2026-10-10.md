# Contact controller phase 3: air and state

Date: 2026-10-10. Detailed spec for phase 3 of [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438). The
program, body model, support rule and invariants are in
[CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md](CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md). Phase 2 (ground
core) and phase 2b (support neighborhood) are on main at `39f93b6b9`. Status: implemented, released in 20.32.0.
Phase 3 introduced no consumer or movement changes. The later
[phase 4b handoff](CONTACT-CONTROLLER-PHASE-4B-SWIMMING-HANDOFF-2026-10-11.md) now runs opt-in
explicit land ticks through this core. Native adoption remains gated. Written under the owner's 2026-10-10
instruction to continue the program to completion, with decisions recorded for review. This text describes what
was built, including every ruling made during implementation.

## Scope

Phase 3 delivers the whole tick of the new controller as an internal pure function with the same inputs as the
legacy `StepCore`, so phase 5 can select it with a one-line switch. It owns intent, gravity, jump, coyote time,
jump buffer, air control and air momentum, the air pass, landing and `LandingImpactSpeed`, `SupportGranted`,
steep slides, traction hysteresis, movement commitments and facing. Grounded ticks run the phase 2 ground core.

Out of scope: water (a `medium` that reports water throws `NotSupportedException` until phase 4 wires the
swimming handoff), the climb signals `ClimbRate`, `ClimbRateEwma` and `StepDeltaY` (phase 4, left at zero), the
`MoveTuning` selector, wire and navigation (phase 5).

## Interface

```csharp
namespace KhaozEngine.Locomotion.Contacts;

internal static class ContactController
{
    internal static MoveState Step(in MoveState state, Vector2 moveDir, float speedFraction, bool run, bool jump,
        float dt, Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world,
        Func<float, float, Vector2>? clampXz, Func<float, float, float, MovementMedium>? medium,
        float? faceYaw = null, GroundCoreSettings? settings = null);
}
```

- The parameters mean exactly what they mean for `StepCore` (`KhaozEngine.Locomotion/CharacterMovement.cs`).
  `MoveState.Position` is the capsule centre, so the feet are `Position - CapsuleHalfHeight * UnitY`.
- A non-null `world` must be an `IPhysicsQueryLeaseSource` and an `IPhysicsSupportNeighborhood`, else the tick
  throws `NotSupportedException`. The tick acquires one read lease and runs every query under it, as the program
  requires.
- A null `settings` means `new GroundCoreSettings()` (foot radius fraction 0.5) until phase 5 moves the fraction
  into `MoveTuning`. An explicit value is used as given, so an explicit zero foot radius is rejected rather than
  replaced.
- A `medium` that reports water at the feet throws `NotSupportedException` until phase 4.
- A non-finite clamp or result returns the input state with the per-tick events zeroed and the commitment the tick
  advanced, as the legacy core does.

The pure legacy helpers this phase reuses rather than duplicates (`TractionGate`, `ResolveFacing`,
`ResolveAirborneVelocity`, `ClipCarryToAchieved`, `LandingImpact`, `SlideFrictionScale`, `SlideFallLineStep`,
`PrepareCommitmentTick` and `FinishCommitmentTick`) change from `private` to `internal`. Their bodies and the legacy
behaviour stay unchanged. The intent arithmetic is short enough to restate, so no intent helper is widened.

## Ground core changes

`GroundCore.Step` gains two optional arguments and three reported values.

- **Traction gate.** `tractionSlopeRadians` is the tick's gate, and every slope decision in the ground tick (start
  support, seats, placement after a push) uses it. Null means `MaxSlopeRadians`, so phase 2 callers are unchanged.
- **Climb budget.** `climbBudget` is the climb the tick may still pay, in metres. Null means a whole tick's
  `MaxStepClimbSpeed * dt`. The result reports `ClimbBudget`, the budget left after the tick's paced climbing, so
  later modes in the same tick pay from what is left.
- **Remaining time.** `RemainingTime` is the time left after the footing changed to `Steep` or `None`, for the next
  mode to run. The substep that found the change counts as spent.
- **`ChangedAtStart`.** True when the start was already steep or unsupported, so the footing changed before any time
  was spent and `RemainingTime` is the whole tick.
- **Slide mode.** The internal `GroundCore.Slide` runs the same substep loop for a sliding body with these
  differences: a steep start moves, a steep seat at or below the feet continues the tick instead of ending it, and
  a steep seat must clear the shell without a push. A steep seat that fails clearance ends the tick with footing
  `None` at the last clear position, for the air pass. Steep ground above the feet stays a wall, walkable support
  grounds the body and the rest of the move walks on, and no support gives `None`. Each slide substep's drop along
  the start plane stays within half `StepHeight`, so the seat's reach down holds it. From a steep start the seat's
  step part is only the rise above the start feet, so a slide that drops onto walkable ground charges nothing
  against the climb budget. One loop serves both modes, so the slide never duplicates the ground core's shape.

## Shell validation

`ShellGeometry.Validate` checks the plane constraint `(StepHeight + CapsuleRadius) * cos(steepest) >= CapsuleRadius
+ ContactSkin` at `steepest = MaxSlopeRadians + TractionHysteresisRadians`, because a footed body may stand on the
band. A band that is not positive widens nothing, as in the traction gate, so the check then runs at the bare
`MaxSlopeRadians`. The engine default, the Grimhollow player and the Ruinborne tuning pass.

The air pass also checks that `StepHeight` is at least `FootRadiusFraction * CapsuleRadius * (1 / cos(steepest) -
1)` over the same banded gate. The foot probe meets the steepest walkable plane that far above the plane at its
axis, and the landing band must hold that contact.

## The tick

1. **Commitment.** `PrepareCommitmentTick` runs first, exactly as in the legacy core: an active commitment
   overrides input, jump and facing, and an airborne one supplies its own gravity and horizontal velocity.
2. **Water.** A medium that reports water at the feet throws until phase 4.
3. **Traction gate (#475, invariant 8).** The tick's slope gate is `TractionGate(Grounded, tuning)`, the legacy
   rule: `MaxSlopeRadians + TractionHysteresisRadians` when the tick starts grounded and the band is positive, else
   `MaxSlopeRadians`. It is computed once and used by every support query, seat and slide decision in the tick.
4. **Intent.** Desired horizontal velocity from `moveDir`, `speedFraction`, `run` and the speed scale, with the
   legacy rule that an airborne tick scales by `AirControl` unless `AirMomentum` is on, in which case the carried
   `HorizontalVelocity` is steered by `ResolveAirborneVelocity`. A committed flight flies its own carried velocity.
   Grounded ticks carry the commanded velocity.
5. **Clamp before the move.** `clampXz` clamps the planned move's end before the move, as legacy clamps its target, and
   the move runs at the velocity that reaches the clamped end. The commanded velocity is still the one carried and
   reported, so a clamp sheds carry as a denial, as legacy does. A slide plans its own move, so every slide segment
   clamps its planned move the same way, and a clamp that shortens it holds the fall-line speed as a block does. Every
   mode also clamps each substep's move before it runs: the ground core's substeps and wall tangents, and the air pass's
   substeps and terrain wall reruns, including the air remainder after a slide. The shell's own collide-and-slide
   (`ShellMotion.Sweep` and `Fly`) then cuts each slide along a contact to the bound from the shell's axis. When the
   clamp's answer would press into the contact, the slide keeps its own direction up to where it meets the bound, so
   the shell reaches the corner of the contact and the bound as it reaches any inner corner. A ground substep the
   clamp shortens counts as blocked, and so does a cut slide. The air pass leaves its velocity alone, because the
   bound is not a contact. Every substep's end is then the clamp's answer, a point on a straight move toward one, or
   the point where a slide meets the bound, so for a convex bound no substep that starts inside it ends outside. Three
   things can still cross it: recovery out of an overlap at the start of a mode, and the clearance push of a ground or
   landing placement. A body one of them leaves outside has its next substeps clamped back toward the bound. The legacy
   re-clamp of the final position after the move is kept as the backstop for them. An end it shifts is placed at the
   clamped axis through the ground core's placement, with the climb budget the tick has left and the shell's clearance.
   An end that stood on support takes walkable support within `StepHeight` above or below it, or steep support at or
   below it. An airborne end takes only walkable support above it, never steep support. A placement the shell clears
   only with a push leaves the feet where they were, because the push would move the axis again. Every end it shifts is
   then floored at analytic terrain at the clamped axis.
6. **Move.** An airborne tick whose feet start below steep analytic terrain at the axis, or below walkable terrain by
   more than `StepHeight`, is first seated on it. No path leaves the feet there, but a defect that did would otherwise
   fall under the world, because no band, wall or seat reaches the terrain again. Legacy held the same floor. Feet
   within `StepHeight` below walkable terrain are a paced seat's lag, or the jump from it, and the air pass's paced,
   clearance-checked landing owns them. Seating them would lift the body unpaced, and under a roof would put the shell
   into it, where recovery throws the body back off the bank. The tick then starts in the mode its footing calls for:
   the ground core when grounded, a slide when an airborne tick (not a committed flight) has steep support within 1 mm
   of its feet and its vertical speed after the tick's gravity, `VerticalVelocity - Gravity * dt`, does not rise, else
   the air pass. A jump always rises, so it flies whichever way the body ran. A slide's vertical speed is its fall-line
   speed's vertical part, and a slide ends every tick descending or at rest, because the ground core blocks a slide
   carried up a steep face to zero progress. Where a delegate normal disagrees with the height field, a slide can end a
   tick rising along the normal's fall line, and its next tick then flies until it descends. So a slide stays a slide
   whatever its carry, including over a convex break. A rising body pressed into a steep face flies, and the air pass's
   terrain wall and shell contacts take away the horizontal part, so it cannot climb. A slide that loses its support
   does so inside a tick, never between two: the tick that ended sliding seated its feet on the support the next tick's
   contact query reads at the same feet. A slide that leaves on its last substep is the mid-tick hand-off with no time
   left. One climb budget of `MaxStepClimbSpeed * dt` is shared by the whole tick, and every mode pays climbing from
   what the earlier modes left. A footing change part way through runs the rest of the tick in the next mode, so the
   tick's motion never depends on where a substep boundary fell:
   - a ground move that loses its footing flies the time it has left, or slides it on steep support,
   - a grounded start whose certified support is steep slides the whole tick with the carried velocity in the same
     tick, so a wedge reports `Wedged` and stays grounded instead of alternating,
   - a slide that loses its support, or whose seat fails clearance, flies the rest,
   - a flight that lands walks the rest at the velocity its contacts left, and a flight that meets steep support
     slides the rest.
   Each hand-off passes on the speed at the hand-off, so the tick counts gravity once: the air pass gives back the
   gravity it integrated for the time left, never rising past zero, and a slide hands on its fall-line speed
   integrated over the time it spent.
   A committed flight never starts sliding, but its air pass may meet steep support and slide mid-tick. A cap of
   eight mode segments per tick guards the loop, and every cycle of mode changes spends air time, so the time
   left shrinks.
7. **Jump (decided last, as legacy).** A jump fires when it is requested this tick or still buffered and the body
   is grounded at the end of the tick or within `CoyoteTime` of leaving the ground. It stamps
   `VerticalVelocity = JumpSpeed`, clears grounded, sets `TimeSinceGrounded` past the coyote window and clears the
   buffer. The rise starts on the next tick. `LandingImpactSpeed` and `SupportGranted` are latched before the
   jump, so a buffered relaunch on the landing tick still reports both. A slide has no footing, so a sliding body
   jumps only when wedged or within `CoyoteTime` of its last footing, as legacy. A jump rises after the next tick's
   gravity, so that tick flies on the closed form instead of sliding on the steep support the body left, whichever
   way it ran, and the air pass hands it back to a slide once it descends onto steep support. The jump has spent
   the coyote window, so it cannot relaunch from the face.
8. **State.** `TimeSinceGrounded`, `JumpBufferRemaining`, `HorizontalVelocity` (through `ClipCarryToAchieved`,
   against the last mode's own carry, drive, achieved move and time), `CommandedVelocity` (the first mode's drive),
   `FacingYaw` (through `ResolveFacing`, frozen under a commitment) and the commitment (`FinishCommitmentTick`), in
   the legacy order.

## The air pass

The body model holds in the air. Only the knee-height shell blocks walls and ceilings. The legs never push the
body, and support comes only from the footprint.

- **Gravity.** `VerticalVelocity = max(VerticalVelocity - Gravity * dt, -MaxFallSpeed)` before the move, as legacy.
  A shell that recovery cannot free does not move or fall, as a held ground tick does not move.
- **Move.** The tick's displacement `(velocity.X * dt, VerticalVelocity * dt, velocity.Z * dt)` runs in substeps of
  at most half the capsule radius. Free flight follows the planned prefix exactly. Each substep sweeps the shell
  (`ShellMotion.Fly`) with slides. A ceiling or a walkable face removes the velocity component into it whole,
  vertical included, so a flat ceiling stops a rise. A wall or a face steeper than the gate removes it with
  horizontal motion alone, so it never lifts the body or slows its fall (the no-lift guard). There is no separate
  ceiling rule.
- **Analytic walls.** Analytic terrain has no shell to meet. Terrain at the substep's new axis more than
  `StepHeight` above the substep's highest feet is a wall, and so is steep terrain above them, even within
  `StepHeight`, as in the ground core. The substep reruns without its component into the wall (the delegate's
  normal made horizontal, else the reverse of the move), then without its horizontal part if the wall still stops
  it. The along-wall speed is kept.
- **Landing.** After each substep that ends descending, `FootSupport` runs the ground core's query anchored at the
  substep's highest feet: reach up `StepHeight`, reach down the substep's whole drop. Walkable
  support anywhere in that band lands. The feet seat on it through the ground core's placement and seat clearance,
  the step-up a standing body at the same feet would take, so a falling body whose knees pass a ledge top lands on
  it. The rise above the substep's start feet is paced by `MaxStepClimbSpeed` like a step-up, and the ground core
  pays any unpaid climb. The paced feet never lag the support by `StepHeight` or more, so the next ground tick's band
  still holds it. `Grounded` is set, `VerticalVelocity` becomes zero and the rest of the tick runs through the ground
  core with the budget the landing left.
- **Failed seat.** A landing seat that fails clearance lands unraised at the substep's end feet while the support is
  still within `StepHeight` above them. The sweep left the shell clear there, and the ground core pays the climb as
  it pays any lag. Otherwise the fall continues. Falling on past a support within reach would let the legs pass it,
  and analytic terrain has no shell to stop them.
- **Steep support.** Steep support never lifts the body. It starts a slide only when it lies within the substep's
  travel, and the slide runs the rest of the tick from the vertical speed at the hand-off.
- **Invariant 5 (#468).** A tick without footing never ends on steep ground above its own vertical motion. It rises
  only onto walkable support within `StepHeight` above its feet at the substep's start while descending, or along
  the projection of a walkable face its shell runs into. A steep face therefore can never be climbed by jumping, and
  the #440 bound of ballistic reach plus `StepHeight` holds. The walkable-face lift is accepted because the body
  could stand on that face anyway, and the projection bounds it.
- **Invariant 7 (#486).** A tick never seats on ground it cannot stand on: steep support starts a slide and never
  grounds the body, and the ground core already treats steep ground above the start feet as a wall.
- **Faces at 60 degrees and steeper.** The shell meets such a face before the footprint does, so a body falling
  beside it slides down along it, airborne, and lands at its toe. It is never seated mid-face.

## Slides

A slide is a body on certified steep support (footing `Steep`). It works on analytic terrain and physics statics
alike, because both reach the controller through the same support primitive.

- **Dynamics.** The fall line and contour come from the steep support's plane. The slide's state is its fall-line
  speed, the carry and vertical speed projected on the fall line. It advances by `SlideFallLineStep` with gravity
  along the plane and the `SlideFrictionScale` ramp, clamped to the terminal
  `MaxFallSpeed / max(sin(slope), sin(gate))`. Input steers along the contour only, never into the carry, as legacy,
  and a tick that starts sliding slides its own carry.
- **Convex slopes.** Each tick projects the fall-line speed onto the plane under its feet, so a plane change keeps
  `cos(delta)` of it, and on a smooth slope that loss vanishes with the tick. Legacy instead sets the vertical speed
  from the seated drop, which on convex ground is steeper than the plane it resolved on, so it adds speed gravity did
  not pay for. From rest on `h = -(1.2x + 0.2x^2)` at 30 Hz, the controller reaches 20.06 m/s at tick 30 and an exact
  integration of the same arc with the same friction ramp reaches 20.08. Legacy reaches 25.05, with a kinetic energy
  1.28 times what its own 9.79 m drop could pay. The controller's lower speed is the intended model.
- **Move.** The slide's horizontal displacement runs through the ground core's slide mode. The seat sets only the
  position, and `VerticalVelocity` is the fall-line speed's vertical part, as legacy. The fall-line speed is clipped
  to the achieved progress only when the fall-line part of the move is blocked, so a contour steer never resets the
  fall. The carry's contour part keeps no more than its own share of the achieved contour move.
- **Blocked steer.** When a steered move is blocked, the slide reruns the tick with its carry alone and keeps
  whichever made more progress along the fall line, so input never costs a slide its speed and a steer that frees
  a pinned body is kept. The steer the move applied is reported, so the tick clips the carry against the real drive.
- **Wedge.** A sliding tick whose seat ends no lower than it started while its support's fall lines oppose (a
  V-gully) is wedged: it counts as grounded for that tick, may jump, and reports `SupportGranted`. As legacy, it
  latches the fall the slide integrated, before a block clipped it, as its landing impact, and holds no vertical
  speed. The jump rises, so it flies, leaves the crease on the closed form and falls back into it.
- **Held.** Support that cannot be certified holds the body where it is, still sliding on what it had.
- **Leaving.** A slide that reaches walkable support grounds the body as a drop, not a climb, and reports the landing
  impact of its vertical speed. A slide whose support turns `None`, or whose seat fails the shell clearance, becomes
  airborne with its fall-line speed integrated over the time it spent, and the air pass runs the rest of the tick.
- **Steep prop faces.** The shell's clearance to a slide face is `(StepHeight + CapsuleRadius) cos(slope)`, so at
  the default tuning the shell overlaps a prop face from about 60 degrees. There the slide seat fails clearance and
  the body falls along the face through the air pass. Analytic terrain has no shell to meet, so terrain slides at
  60 and 75 degrees stay slides. A run-jump into a prop face from about 47 to 58 degrees is fallen along too. The
  jump rises, so it flies, and the shell keeps it off the face while the legs sink into it, up to about 0.21 m on 47
  degrees and 0.18 on 50. At the apex the face is above the feet, so no descending substep finds steep support
  within the span it travelled, and no slide starts. The body falls along the face to its toe, where its feet dip up
  to about 0.21 below the floor for a tick (0.16 on 50 degrees) before it lands. It never rises past a free jump's
  apex and never ratchets. The legs-band overlap is inside the body model, and the feel is a phase 6 playtest note.
  A fall onto these faces from rest still starts a slide.

## Invariants this phase owns

From the program list: 5 (#468), 6 (one surface for the resolve and the clamp, which holds by construction
because every decision reads `FootSupport`), 7 (#486 and #470) and 8 (#475). Determinism (1) holds for every
path, including commitments and slides.

The terrain floor holds as well: no tick ends with its feet more than half a contact skin below analytic terrain at the
axis, except a body below walkable terrain by a paced seat's lag, within `StepHeight`. That body is grounded on the lag,
which the ground core pays from later budgets, or airborne from the jump that leaves it, until the air pass lands it or
it rises clear. The jump tick itself ends below the terrain, and a rise of less than the lag in one tick leaves later
ticks below it too (0.3 m below a bank at 30 Hz is still 1 mm below after the first air tick). Legacy held the floor
with `max(pos.Y, groundY)` and had no paced lag on terrain. Here each mode keeps it: the ground core and the slide seat
on the terrain or treat it as a wall, the air pass lands on it, starts a slide on it or treats it as a wall, a failed
air seat lands with the terrain within `StepHeight` above the feet, every substep's move and every shell slide is
clamped inside the play area, and the backstop after the move floors every end it shifts at the terrain. An airborne
tick that starts below steep terrain, or below walkable terrain by more than `StepHeight`, is seated on it. A grounded
start is not, and neither is an airborne start within `StepHeight` below walkable terrain, because both are the lag
above. The scenario suite asserts the floor on every tick it runs, and none of its rows climbs a terrain step.

## Suite

First-principles expectations, box and mesh variants where the geometry allows, all through
`ContactController.Step` or the mode it exercises, in `KhaozEngine.Game.Tests` under
`KhaozEngine.Tests.Locomotion.Contacts`. Legacy suites are mined for geometry only.

| Group | Cases |
|---|---|
| Vertical | Gravity accelerates to `MaxFallSpeed`. A fall lands on flat ground with the impact equal to the speed at contact, and a terminal fall lands on a thin slab. A jump stamps `JumpSpeed`, rises from the next tick and reaches the sampled apex. No double jump at the apex. A coyote jump inside the window, none after it. A coyote jump from a slide start rises on the closed form. A buffered jump fires on landing and keeps the impact. Replay is bit-identical. |
| Air control | `AirControl` 0.5 halves air travel with momentum off. With momentum on, release keeps speed, `AirControl` 0 is ballistic, and a wall clips the carry. |
| Ceilings | A jump under a slab stops rising at the slab with its upward speed removed, then falls. A jump under a low ceiling stays down. A run-jump under an eave never wedges. A run-jump along a vertical wall keeps its vertical speed. |
| Walls in the air | A run-jump into a wall slides along it. An inner corner stops both components. A run into a steep face never lifts. A jump into a steep terrain face never sinks into it, and a diagonal run into a terrain cliff keeps its along speed. A rise onto a walkable ramp keeps moving. |
| Landing | Falling onto a crate top seats on it, not sunk. Jumping onto a ledge lands when descending with the knees passing the top, and a jump peaking under a ledge top lands on it. Ledge landings are paced and stay within the next ground tick's band. Landing never rises above the reach. A 0.9 m drop onto a 30 degree slope lands on it. A fall onto a 50 degree face starts a slide, and a fall beside a 75 degree face lands at the toe. |
| Mode changes | A walk-off keeps the tick's speed. A landing tick climbs within one budget. A landing onto steep support keeps the rest of the tick on the closed form, counting gravity once, and so does a slide off an edge. A graze then a landing does not walk back into the wall. A landing mid-tick keeps the horizontal move. A wedge stays grounded every tick, latches the fall the slide integrated, and a held jump clears its crease without ratcheting. A clamp at a ledge edge never hovers or sinks. A 60 degree slide into a clamp bound stays on the face. A walk and a 600 tick run along a wall whose tangent points past a bound stay inside it, on the terrain, and climb no more than one budget a tick, at 30 and 60 Hz. A walk and a run along a box or mesh wall facing out of a bound stop in the corner, inside the bound and grounded on the level terrain, for 300 ticks at 30 and 60 Hz, and the ground core and air pass alone end inside it. Reached through recovery across the bound, the backstop pays a seat onto higher walkable support from the budget the tick has left, never raises an airborne end onto steep support, and floors a shift onto rising terrain. |
| Slides | A 60 degree face slides to the toe and lands with one impact. The friction ramp sets the slide rate. Where the normal disagrees with the height field, the vertical speed is the fall-line speed's vertical part. Input steers along the contour only. A V-gully wedges. A slide on a prop matches terrain, a slide onto a prop top grounds and a fast slide lands on a prop top unsunk. A slide off an edge goes airborne. A steep prop face hands over to the air pass. A held run-jump into a 50 degree box or mesh face falls along it, lands at the toe with its feet within `StepHeight` of the floor, never rises past a free jump's apex and never ratchets, at 30 and 60 Hz. A blocked slide keeps only its achieved speed, and a blocked steer never costs the slide its speed. A slide carries its own speed, not the input. A slide over a 50 to 60 degree convex break stays on the slope and keeps its speed within the break's projection loss, at 30 and 60 Hz. 60 and 75 degree rows run on analytic terrain, 46, 49 and 53 degree rows on terrain, box and mesh. |
| Hysteresis | A bank at 47 degrees: a walker keeps footing inside the band, a lander does not, and a jump from inside the band leaves the bank on the closed form, walking or running uphill. 49 degrees refuses the walker. Traction knobs at zero reproduce the bare gate. A cliff toe never flips footing. |
| #440 | A held jump against a sheer face never grounds on it and never ratchets. |
| #470 | A lip onto a 63.4 degree face never grounds or grants support on the crossing tick, and no launch from it. A drop onto a level shelf seats in one tick. |
| #468 | A held run-jump across a creased cliff never ratchets up it, including the variant whose delegate normal is smoothed over 5 m. |
| Commitment | Preparation roots then launches. A wall blocks and the commitment lands. A launch blocked at the start aborts. Timeout aborts. The authored 8 m arc with apex 2 lands within one step of its authored landing. |
| Inputs | Non-finite inputs name their parameter. A non-finite clamp returns the input state. A zero step height, an explicit zero foot radius and a footprint the step height cannot hold are rejected. A water medium is rejected until phase 4. |
| Ground parity | Every phase 2 ground row run through `ContactController.Step` with a grounded state gives the ground core's result. No parity row changes footing with time left, so each is the ground core's whole tick. |
| Terrain floor | Every scenario tick ends with its feet no more than half a skin below analytic terrain. A failed seat over a terrain bank under a static roof lands. Feet that start a metre below level or steep terrain are seated on it. A jump from a paced seat's lag below a terrain bank rises from the lag on the closed form, and a fall from it lands with a paced climb, at 30 and 60 Hz. |

Every round runs on arm64 locally and on hosted x64. No expectation pins a contact position finer than half the
contact skin, and no committed bit digest passes through a sweep.

## Files

- `KhaozEngine.Locomotion/Contacts/ContactController.cs`: the tick and the mode changes within it.
- `KhaozEngine.Locomotion/Contacts/AirPass.cs`: gravity, the shell flight, analytic walls, landing and the hand-off
  to a slide, with the footprint validation.
- `KhaozEngine.Locomotion/Contacts/SlideCore.cs`: slide contact, the fall-line dynamics, the clamp of its own
  planned move, the blocked-steer rerun and the wedge.
- `KhaozEngine.Locomotion/Contacts/GroundCore.cs`: the traction gate and climb budget arguments, the reported
  remaining time, budget and `ChangedAtStart`, and the internal slide mode.
- `KhaozEngine.Locomotion/Contacts/GroundSeat.cs`: the gate's cosine, and the step part from a steep start.
- `KhaozEngine.Locomotion/Contacts/GroundPlacement.cs`: the gate's cosine, and the clearance a slide's steep seat
  must pass without a push.
- `KhaozEngine.Locomotion/Contacts/ShellMotion.cs`: `Fly`, the shell's flight through the air with ceilings and the
  no-lift guard, and the play-area cut of each slide along a contact in `Sweep` and `Fly`.
- `KhaozEngine.Locomotion/Contacts/PlayArea.cs`: the clamp of a planned move, shared by every mode.
- `KhaozEngine.Locomotion/Contacts/ShellGeometry.cs`: validation against the banded gate, because a footed body may
  stand on `MaxSlope + TractionHysteresisRadians`.
- `KhaozEngine.Locomotion/CharacterMovement.*.cs`: `TractionGate` and the legacy helpers named above, private to
  internal only.

## Boundaries

No `MoveTuning`, wire, navigation or consumer change. The legacy stepper's behaviour is unchanged. Phase 3 shipped in
20.32.0.

## Risks

- **Feel.** Ceilings now stop a jump and the legs pass ledge edges in the air. Both follow the body model. The
  owner's playtest in phase 6 judges feel, including the forgiving ledge landings and the slightly higher arc a
  walkable bank allows.
- **Slides on props.** Legacy never slid on props. Certified steep support makes them slide, which the program
  intends. Prop faces from about 60 degrees are fallen along instead, and so is a run-jump into a prop face from
  about 47 degrees. Rows pin each.
- **Large falls.** At 50 m/s and 30 Hz a tick falls 1.67 m. Substeps bound the shell sweep, and the landing band
  covers each substep's whole drop, so the feet cannot pass through a floor.

## Known limits

- **Carry cap.** The legacy 127 m/s carry cap is not ported. Terminal speed stays below it at every shipped tuning,
  so it is unneeded.
- **Play-area bound.** Every substep's move and every slide of the shell along a contact is clamped, so only a
  recovery or a clearance push that crosses the bound reaches the backstop after the move. Three rows reach it, each
  through recovery. A body left outside the bound has its next substeps clamped back toward it, so where the contact
  that pushed it faces out of the bound the shell holds against that contact until the backstop shifts the end. Its
  seat is paced through the
  ground core's placement, but on analytic terrain the floor wins: an end it shifts onto higher terrain is lifted onto
  it unpaced, as legacy floored every end. A placement the shell clears only with a push leaves the feet where they
  were, which can hover over a static support for a tick or stand below it by up to `StepHeight`, and the next
  tick's ground core drops onto it or pays the climb.
- **Low roof.** Under a roof too low to stand beneath, a landed body keeps its legs sunk up to `StepHeight` into the
  ledge, because the shell cannot rise to pay the climb.
- **Failed seat.** A landing seat that fails clearance lands unraised, with its legs below the support by up to
  `StepHeight`, and the ground core pays the climb from its budget. Under a roof the shell cannot rise past, the legs
  stay sunk, as in the low roof limit.
- **Inconsistent terrain normals.** On terrain whose delegate normals disagree with the height field, a contour
  steer may be dropped for a tick by the blocked-steer rerun.
