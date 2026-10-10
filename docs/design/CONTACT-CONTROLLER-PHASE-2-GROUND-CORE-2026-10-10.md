# Contact controller phase 2: ground core

Date: 2026-10-10. Detailed spec for phase 2 of [#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438). The
program, body model, support rule and invariants are in
[CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md](CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md), and phase 1
is on main at `d8a9d45d6`. Status: implemented, staged for 20.30.0. Nothing consumes it yet, so movement is unchanged.

## Scope

Phase 2 resolves one grounded tick: given where the feet are and the horizontal move the tick asks for, it
returns where the feet end up and what supports them. It is an internal pure function in
`KhaozEngine.Locomotion.Contacts`. Nothing consumes it yet. Phase 3 supplies intent, gravity, jumps and air.
Phase 4 derives the climb and step signals from its results. Phase 5 adds the `MoveTuning` selector and fields,
the wire and the navigation binding.

Out of scope: airborne ticks, landing, sliding on steep ground, signals, wire, navigation and the certified
support gaps. Those gaps (#1329 concave creases, #1330 vertices, #1331 curved primitives, #1332 one-sided back
faces, #1340 one static proposed per probe at a shared edge, #1342 an unresolved feature query at a convex nosing)
close in a separate phase 2b that must land before any game adopts the controller (owner ruling,
2026-10-10).

## Interface

```csharp
namespace KhaozEngine.Locomotion.Contacts;

internal readonly record struct GroundCoreSettings(float FootRadiusFraction = 0.5f);

internal enum GroundFooting : byte { Walkable, Steep, None, Held }

internal readonly record struct GroundStepResult(Vector3 Feet, GroundFooting Footing, SupportSample Support,
    float Rise, Vector2 Achieved, bool Blocked);

internal static class GroundCore
{
    internal static GroundStepResult Step(Vector3 feet, Vector2 displacement, float dt, in MoveTuning tuning,
        in GroundCoreSettings settings, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease);
}
```

- `feet` is a grounded body's feet in the world's local frame, as `StepCore` receives positions.
- `displacement` is the horizontal move this tick asks for, in metres. Phase 3 computes it from intent.
- `Rise` is the change in feet height this tick. Positive climbs, negative descends.
- `Achieved` is the horizontal move that actually happened. `Blocked` says a wall, cliff, steep rise or refusal
  stopped part of it.
- `Held` means the support at the start could not be certified. The body holds its position (phase 2b removes
  the cases that cause it).
- The foot disc radius comes from `settings` until phase 5 moves it into `MoveTuning` together with the
  navigation bake identity, so bakes change once.
- Input is validated like `FootSupport`: non-finite values, a non-positive `dt` and an invalid shell
  (`ShellGeometry.Validate`) throw `ArgumentOutOfRangeException` or `ArgumentException`.

## The tick

1. **Recovery.** If the shell at the start overlaps a static, push it out along the true MTV, up to four passes.
   Only the shell is recovered. Geometry below the knee never pushes the body.
2. **Start support.** `FootSupport` at the start axis, band `[feet - StepHeight, feet + StepHeight]`.
   `Refused` there returns `Held` at the start position. Phase 2 never moves a body whose footing it cannot
   certify.
3. **Up pass.** Sweep the shell straight up by `StepHeight`. The lift is the clear distance, less the contact
   skin. The lift exists because the shell's front can meet a walkable slope rising ahead before the down pass
   reseats it. Steps up to `StepHeight` never touch the shell.
4. **Owed climb.** Feet certainly below a walkable start support, where pacing leaves them for a tick or two, owe
   that climb. The tick pays it first, before any horizontal motion, from the same `MaxStepClimbSpeed * dt` budget
   that paces the step part and within the lift the up pass cleared. A tick with zero displacement pays it too and
   reports no horizontal motion. Feet within the support's height error owe nothing. Paid only after a seat, the
   climb would never be paid once a refusal blocks the next substep, because the refusal repeats from the same
   feet.
5. **Side pass.** Sweep the lifted shell along `displacement` in substeps of at most half the capsule radius.
   Passes 5 and 6 run per substep, so a wall-like outcome of the down pass slides from where it was met. On a
   hit, advance to the contact less the skin. A wall, ceiling or rising-support contact removes the move's
   component along the contact's horizontal normal and the rest continues, up to four slides per substep. Free
   motion is never shortened: on open ground `Achieved` equals `displacement` exactly.
6. **Down pass.** `FootSupport` at the new axis, same band around the start feet. The new axis is the axis the
   tick reports. While the motion follows its plan that is the start axis plus the planned part of the
   displacement, so the reported feet are certified at their own axis and not at a summed axis a rounding step
   away.
   - **Walkable:** seat on it. The step part of the rise is the rise the start support's own plane does not
     explain: the seated height less that plane extrapolated to the new axis. When `MaxStepClimbSpeed` is
     positive, the tick's owed climb and step part together are capped at `MaxStepClimbSpeed * dt`, so a run up
     stairs paces its climb as the knob always meant, while a continuous slope is followed exactly and never
     throttled. A paced body stays `Walkable` with its feet below the tread for a tick or two. That is legs-band
     overlap, which the body model allows.
   - **Steep above the start feet:** a footed tick never climbs ground it cannot stand on (#486). The steep
     face acts as a wall: undo the substep and slide along its horizontal tangent.
   - **Steep at or below the start feet:** seat on it with `Steep` footing. Phase 3 slides from there.
   - **None:** `None` footing at the start height. A ledge whose drop between surfaces exceeds `StepHeight`
     falls, so the 0.41 m ledge falls for every capsule radius.
   - **Refused:** the move is blocked. The body stays at the last certified position of this tick.
   - **Analytic cliff:** terrain at the new axis more than `StepHeight` above the start feet acts as a wall,
     with the terrain normal when the delegate gives one, else the reverse of the move.
7. **Unlifted retry.** If a lifted substep ends on a refusal, repeat that substep once without the lift. A
   lifted shell can pass over an obstacle taller than `StepHeight` that the probe then refuses, and the
   unlifted shell meets it as a wall. If the retry also refuses, the substep is blocked.
8. **Seat clearance.** The shell at the seated feet must not overlap. If it does, recover once along the MTV.
   If it still overlaps, the move is blocked at the last clear position. A push that raises the body is a climb
   and is paid from the same budget. When the budget cannot pay it, the move is blocked at the last clear
   position, so a paced shell that meets the next nosing waits below it.

The contact skin is 1 mm. It backs off a sweep only when the sweep hits something, so it never shortens free
motion. That is the difference from the legacy pre-sweep clearance push measured in #1270.

## Invariants this phase owns

From the program list: determinism (1), clearance never moves a body along its support (2), rises and drops
between support heights (3), and no seating on unstandable ground (7). Phase 2 adds:

- Free motion on walkable support is exact. A body commanded `d` on flat ground or a walkable slope moves `d`
  horizontally.
- A slowing approach converges. A body commanded the remaining distance to a target on walkable support arrives
  within the 1 mm navigation ball, so the #1270 and #1265 stalls cannot recur.
- The legs never block. Only the shell, steep rises, analytic cliffs and refusals stop horizontal motion.

## Ground suite

First-principles expectations, box and mesh variants where geometry allows, all through `GroundCore.Step` in
`KhaozEngine.Game.Tests` under `KhaozEngine.Tests.Locomotion.Contacts`. Legacy suites are mined for scenarios
only.

| Group | Cases |
|---|---|
| Exact motion | Flat prop floor, analytic terrain, 5 and 20 degree slopes, up, down and across: achieved equals commanded. |
| Convergent approach | The #1270 resting-bank approach from x -0.375 to -0.125, and the #1265 slope edges, each arriving within 1 mm in a bounded number of ticks. |
| Step up | Lip 0.0425 m, crate 0.3 m, step exactly `StepHeight`. A step of `StepHeight + 0.01` is a wall. |
| Stairs | Deep (0.40 m) and shallow (0.35 m) treads, risers 0.25 and 0.30, at 2, 3, 4 and 6 m/s: every riser climbed, step part of the rise per tick at most `MaxStepClimbSpeed * dt`. |
| Slope pacing | A 40 degree walkable mesh slope climbed at 6 m/s is followed exactly, never paced, and stays `Walkable` for 60 ticks. |
| Step down | Drops 0.32, 0.40 seat in one tick. 0.41 and 0.60 give `None`. |
| Walls | Head-on stops at the skin. 30 and 60 degree approaches keep the tangential component exactly. An inner corner stops both components. |
| Ceilings | A ceiling lower than the shell top blocks entry. A ceiling within the lift limits the lift and still allows a flat move. |
| Steep | A 60 degree face rising ahead blocks and slides. Walking off onto a 60 degree face gives `Steep`. |
| Analytic cliff | Terrain rising 0.5 m within one substep blocks and slides along the cliff. |
| Refusal | A sphere prop under the target blocks the move. A body started on a sphere gives `Held`. Both are pinned until phase 2b. |
| Determinism | Two identical steps are bit-identical. A 256-tick sequence replayed from a copied state matches tick for tick. |
| Cost | Queries per tick on flat ground, stairs and walls, recorded for #1334. |

## Files

- `KhaozEngine.Locomotion/Contacts/GroundCore.cs`: `GroundCore` tick orchestration and the `GroundCoreSettings`,
  `GroundFooting` and `GroundStepResult` types.
- `KhaozEngine.Locomotion/Contacts/ShellMotion.cs`: recovery, the up pass and the side pass with slides.
- `KhaozEngine.Locomotion/Contacts/GroundSeat.cs`: the down pass, pacing, steep, none, refusal and cliff rules.
- Tests under `KhaozEngine.Game.Tests/Locomotion/Contacts/`, reusing `FootSupportScenes.cs`.

## Boundaries

No legacy file, `MoveTuning` field, wire field or navigation identity changes. No consumer. The legacy stepper
stays the default and stays byte-unchanged. Phase 2 rides the next engine version under the release ritual.

## Risks

- **Pacing and the shell.** A paced body lags its tread. The legs overlap it, which is allowed, but the shell
  must still clear the next riser. The stair rows at 6 m/s pin it.
- **Held bodies.** Until phase 2b, a body placed on a curved prop cannot move. The refusal rows pin the
  behaviour so phase 2b replaces it deliberately.
- **Query cost.** Each tick runs up to two `FootSupport` queries plus shell sweeps. The cost rows measure it
  before phase 3 builds on it.
