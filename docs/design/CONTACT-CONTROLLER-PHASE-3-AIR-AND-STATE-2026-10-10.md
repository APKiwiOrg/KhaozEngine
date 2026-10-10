# Contact controller phase 3: air and state

Date: 2026-10-10. Detailed spec for phase 3 of [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438). The
program, body model, support rule and invariants are in
[CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md](CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md). Phase 2 (ground
core) and phase 2b (support neighborhood) are on main at `39f93b6b9`. Status: written under the owner's
2026-10-10 instruction to continue the program to completion, decisions recorded for review.

## Scope

Phase 3 delivers the whole tick of the new controller as an internal pure function with the same inputs as the
legacy `StepCore`, so phase 5 can select it with a one-line switch. It owns intent, gravity, jump, coyote time,
jump buffer, air control and air momentum, the air pass, landing and `LandingImpactSpeed`, `SupportGranted`,
steep slides, traction hysteresis, movement commitments and facing. Grounded ticks run the phase 2 ground core.

Out of scope: water (the `medium` argument is accepted and must be null or report dry until phase 4 wires the
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
        float? faceYaw = null, GroundCoreSettings settings = default);
}
```

- The parameters mean exactly what they mean for `StepCore` (`KhaozEngine.Locomotion/CharacterMovement.cs`).
  `MoveState.Position` is the capsule centre, so the feet are `Position - CapsuleHalfHeight * UnitY`.
- A non-null `world` must be an `IPhysicsQueryLeaseSource` and an `IPhysicsSupportNeighborhood`. The tick acquires
  one read lease and runs every query under it, as the program requires.
- `settings` defaults to `new GroundCoreSettings()` (foot radius fraction 0.5) until phase 5 moves the fraction into
  `MoveTuning`.
- A non-finite result returns the input state with the event fields zeroed, as the legacy core does.

The pure legacy helpers this phase reuses rather than duplicates (`ResolveFacing`, `ResolveAirborneVelocity`,
`ClipCarryToAchieved`, `LandingImpact`, `SlideFrictionScale`, `SlideFallLineStep`, `PrepareCommitmentTick`,
`FinishCommitmentTick` and the intent helpers) change from `private` to `internal`. Their bodies and the legacy
behaviour stay unchanged.

## The tick

1. **Commitment.** `PrepareCommitmentTick` runs first, exactly as in the legacy core: an active commitment
   overrides input, jump and facing, and an airborne one supplies its own gravity and horizontal velocity.
2. **Traction gate (#475, invariant 8).** The tick's slope gate is `MaxSlopeRadians + TractionHysteresisRadians`
   when the tick starts grounded, else `MaxSlopeRadians`. It is computed once and used by every support query,
   seat and slide decision in the tick. `GroundCore.Step` gains a gate argument for it.
3. **Intent.** Desired horizontal velocity from `moveDir`, `speedFraction`, `run` and the speed scale, with the
   legacy rule that an airborne tick scales by `AirControl` unless `AirMomentum` is on, in which case the carried
   `HorizontalVelocity` is steered by `ResolveAirborneVelocity`. Grounded ticks carry the commanded velocity.
4. **Branch on footing at the start of the tick.**
   - **Grounded and walkable:** the ground core moves the body by `velocity * dt` horizontally. `VerticalVelocity`
     is zero. A `None` result leaves the ground: the body becomes airborne at the reached position with
     `VerticalVelocity` zero and starts falling next tick.
   - **Sliding:** the body is on steep support. See Slides.
   - **Airborne:** see The air pass.
5. **Jump (decided last, as legacy).** A jump fires when it is requested this tick or still buffered and the body
   is grounded at the end of the tick or within `CoyoteTime` of leaving the ground. It stamps
   `VerticalVelocity = JumpSpeed`, clears grounded, sets `TimeSinceGrounded` past the coyote window and clears the
   buffer. The rise starts on the next tick. `LandingImpactSpeed` and `SupportGranted` are latched before the
   jump, so a buffered relaunch on the landing tick still reports both. A sliding body cannot jump unless it is
   wedged.
6. **State.** `TimeSinceGrounded`, `JumpBufferRemaining`, `HorizontalVelocity` (through `ClipCarryToAchieved`),
   `CommandedVelocity`, `FacingYaw` (through `ResolveFacing`, frozen under a commitment), the commitment
   (`FinishCommitmentTick`) and `clampXz`, in the legacy order.

## The air pass

The body model holds in the air. Only the knee-height shell blocks walls and ceilings. The legs never push the
body, and support comes only from the footprint.

- **Gravity.** `VerticalVelocity = max(VerticalVelocity - Gravity * dt, -MaxFallSpeed)` before the move, as legacy.
- **Move.** The tick's displacement `(velocity.X * dt, VerticalVelocity * dt, velocity.Z * dt)` runs in substeps of
  at most half the capsule radius. Each substep sweeps the shell along the full displacement with slides. Every
  shell contact removes the velocity component into it, vertical included, so a flat ceiling stops a rise and a
  wall leaves the vertical speed alone. There is no separate ceiling rule.
- **Landing.** After each substep, while descending, `FootSupport` runs the ground core's query anchored at the
  substep's start feet (its highest): reach up `StepHeight`, reach down the substep's whole drop. Walkable support
  anywhere in that band lands: the feet seat on it through the ground core's seat clearance, with the rise paced by
  `MaxStepClimbSpeed` like a step-up and any unpaid climb carried for the ground core to pay. `Grounded` is set,
  `VerticalVelocity` becomes zero and the remaining horizontal displacement of the tick runs through the ground
  core. A seat that fails clearance holds the body at the substep start for the rest of the tick, so the feet never
  pass below support they reached. That is the step-up a standing
  body at the same feet would take, so a falling body whose knees pass a ledge top lands on it. Steep support
  starts a slide only when it lies within the substep's travel. Analytic terrain more than `StepHeight` above the
  feet at the new axis is a wall, as in the ground core.
- **Invariant 5 (#468).** A tick without footing never ends on steep ground above its own vertical motion, and it
  rises only onto walkable support within `StepHeight` above its feet at the substep's start while descending. A steep face therefore can
  never be climbed by jumping, and the #440 bound of ballistic reach plus `StepHeight` holds.
- **Invariant 7 (#486).** A tick never seats on ground it cannot stand on: steep support starts a slide and never
  grounds the body, and the ground core already treats steep ground above the start feet as a wall.
- **Faces at 60 degrees and steeper.** The shell meets such a face before the footprint does, so a body falling
  beside it slides down along it, airborne, and lands at its toe. It is never seated mid-face.

## Slides

A slide is a body on certified steep support (footing `Steep`). It works on analytic terrain and physics statics
alike, because both reach the controller through the same support primitive.

- **Dynamics.** The fall line and contour come from the steep support's plane. The fall-line speed advances by
  `SlideFallLineStep` with gravity along the plane and the `SlideFrictionScale` ramp, clamped to the terminal
  `MaxFallSpeed / max(sin(slope), sin(gate))`. Input steers along the contour only, never into the carry, as
  legacy.
- **Move.** The slide's horizontal displacement runs through the ground core's slide mode, which keeps steep seats
  steep and treats steep ground above the start feet as a wall. The slide's state is its fall-line speed. The seat
  sets the position, and `VerticalVelocity` is the fall-line speed's vertical part, as legacy. The fall-line speed is
  clipped only when the fall-line part of the move is blocked, so a contour steer never resets the fall.
- **Blocked steer.** When a steered move is blocked, the slide reruns the tick with its carry alone and keeps
  whichever made more progress along the fall line, so input never costs a slide its speed and a steer that frees
  a pinned body is kept.
- **Wedge.** A sliding tick whose seat ends no lower than it started while its support's fall lines oppose (a
  V-gully) is wedged: it counts as grounded for that tick, may jump, and reports `SupportGranted`.
- **Leaving.** A slide that reaches walkable support grounds the body and reports the landing impact of its
  vertical speed. A slide whose support turns `None` becomes airborne with its velocity.

## Invariants this phase owns

From the program list: 5 (#468), 6 (one surface for the resolve and the clamp, which holds by construction
because every decision reads `FootSupport`), 7 (#486 and #470) and 8 (#475). Determinism (1) holds for every
path, including commitments and slides.

## Suite

First-principles expectations, box and mesh variants where the geometry allows, all through
`ContactController.Step`, in `KhaozEngine.Game.Tests` under `KhaozEngine.Tests.Locomotion.Contacts`. Legacy suites
are mined for geometry only.

| Group | Cases |
|---|---|
| Vertical | Gravity accelerates to `MaxFallSpeed`. A fall lands on flat ground with the impact equal to the speed at contact. A jump stamps `JumpSpeed`, rises from the next tick and reaches the sampled apex. No double jump at the apex. A coyote jump inside the window, none after it. A buffered jump fires on landing and keeps the impact. Determinism over 256 ticks. |
| Air control | `AirControl` 0.5 halves air travel with momentum off. With momentum on, release keeps speed, `AirControl` 0 is ballistic, and a wall clips the carry. |
| Ceilings | A jump under a slab stops rising at the slab with its upward speed removed, then falls. A run-jump under an eave never wedges. A run-jump along a vertical wall keeps its vertical speed. |
| Walls in the air | A run-jump into a wall slides along it and never tunnels at 50 m/s falls. An inner corner stops both components. |
| Landing | Falling onto a crate top seats on it, not sunk. Jumping onto a ledge lands when descending with the knees passing the top. A 0.9 m drop onto a 30 degree slope lands on it. Landing on a 0.32 m lower shelf seats in one tick. |
| Slides | Walking off onto a 60 degree face slides to the toe and lands with one impact. A 46 degree face slides gently, a 75 degree face fast, each by the friction ramp. Input steers along the contour only. A V-gully wedges and allows a jump. A slide on a physics prop works like terrain. |
| Hysteresis | A bank at 46 degrees: a walker keeps footing inside the band, a lander does not. 49 degrees refuses both. Traction knobs at zero reproduce the bare gate. |
| #440 | A held jump against a 78.7 degree face never grounds on it and never ratchets (second half maximum within 20 mm of the first). |
| #470 | A 0.15 m lip onto a 63.4 degree face at 60 Hz never grounds or grants support on the crossing tick, and no launch from it. A 0.35 m drop onto a level shelf seats in one tick. |
| #468 | A held run-jump across a creased cliff never ratchets up it. |
| Commitment | Preparation roots then launches. A wall blocks and the commitment lands. A launch blocked at the start aborts. Timeout aborts. The authored 8 m arc with apex 2 lands within one step of its authored landing. |
| Ground parity | Every phase 2 ground row run through `ContactController.Step` with a grounded state gives the ground core's result. |

Every round runs on arm64 locally and on hosted x64. No expectation pins a contact position finer than half the
contact skin, and no committed bit digest passes through a sweep.

## Files

- `KhaozEngine.Locomotion/Contacts/ContactController.cs`: the tick.
- `KhaozEngine.Locomotion/Contacts/AirPass.cs`: gravity, the shell sweep with ceilings, and landing.
- `KhaozEngine.Locomotion/Contacts/SlideCore.cs`: steep slides and the wedge.
- `KhaozEngine.Locomotion/Contacts/GroundCore.cs`: the traction gate argument.
- `KhaozEngine.Locomotion/Contacts/ShellGeometry.cs`: validation against the banded gate, because a footed body may
  stand on `MaxSlope + TractionHysteresisRadians`.
- The legacy helpers named above, private to internal only.

## Boundaries

No `MoveTuning`, wire, navigation or consumer change. The legacy stepper's behaviour is unchanged. Phase 3 rides the
staged 20.31.0 under the release ritual.

## Risks

- **Feel.** Ceilings now stop a jump and the legs pass ledge edges in the air. Both follow the body model. The
  owner's playtest in phase 6 judges feel.
- **Slides on props.** Legacy never slid on props. Certified steep support makes them slide, which the program
  intends. Rows pin it.
- **Large falls.** At 50 m/s and 30 Hz a tick falls 1.67 m. Substeps bound the shell sweep, and the landing band
  covers the whole travel, so the feet cannot pass through a floor.
