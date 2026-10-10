# Contact Controller Phase 2 (Ground Core) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Resolve one grounded tick of the #438 controller as an internal pure function, `GroundCore.Step`, on
top of phase 1's certified foot support and knee-height shell.

**Architecture:** Three cohesive internal types in `KhaozEngine.Locomotion.Contacts`. `ShellMotion` moves the shell
(recovery, lift, horizontal sweep with slides). `GroundSeat` applies the down-pass rules to one candidate axis.
`GroundCore` orchestrates substeps, the unlifted retry, step-part pacing and seat clearance. Nothing consumes it.

**Tech Stack:** C# (.NET 10), System.Numerics, Bepu through `KhaozEngine.Physics.Bepu`, xUnit.

**Spec:** `docs/design/CONTACT-CONTROLLER-PHASE-2-GROUND-CORE-2026-10-10.md` at `312331c39` (owner-approved
2026-10-10). Program spec: `docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md`.

## Global Constraints

- No change to legacy files (`KhaozEngine.Locomotion/CharacterMovement*.cs`), `MoveTuning`, `MoveState`, the wire,
  `KhaozEngine.Movement` or navigation identity. All new types are internal. No consumer.
- Contact skin 1 mm (`ShellMotion.ContactSkin = 0.001f`), applied only when a sweep hits. Substeps at most
  `CapsuleRadius / 2`. At most 4 slides per substep. At most 4 recovery passes.
- Support band for every `FootSupport` query is `[startFeetY - StepHeight, startFeetY + StepHeight]` and the
  footprint radius is `settings.FootRadiusFraction * CapsuleRadius` (default 0.5).
- A comparison of a certified height uses its `HeightError` on the conservative side. No tolerance is chosen to make
  a case pass.
- Refusal policy until phase 2b: a refused start returns `Held`, a refused target blocks the substep.
- Every build, test or format through `build-slot`, one focused run at a time. Build and test through
  `KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj`. The full suite runs once, in Task 5, heavy lane.
- Warnings are errors. KESIZE 800-line cap. Test namespaces under `KhaozEngine.Tests.Locomotion.Contacts`. No em or
  en dashes, no prose semicolons in Markdown or comments. Explicit staging, hooks on, `area(scope): summary`.
- Ride the staged, untagged 20.30.0 at the finish: append a changelog line, no new version.

## Review Focus

- A zero displacement holds the body exactly, with no drift on flat ground or a slope. Task 3 pins it.
- A large single-tick move cannot tunnel through a thin wall. Task 4 pins it.
- A body starting at skin distance from a wall and moving parallel to it keeps full speed. Task 1 pins it.
- A terrain-only world (`world` null) resolves slopes and cliffs from the analytic delegates alone. Task 3 pins it.
- A start overlap deeper than four recovery passes can clear reports `Blocked` and never returns a non-finite
  position. Task 1 pins it.

---

### Task 1: Shell motion

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/ShellMotion.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/ShellMotionTests.cs` (scenes from `FootSupportScenes.cs` and its
  `FootSupportScene.Flat`/`Slab` builders, extended there only if a box wall or ceiling builder is missing)

**Interfaces:**
- Consumes: `ShellGeometry.Shape/Centre/Validate`, `ContactClassifier.ClassifyShell`.
- Produces:

```csharp
namespace KhaozEngine.Locomotion.Contacts;
internal readonly record struct ShellSweep(Vector2 Achieved, bool Blocked);
internal static class ShellMotion
{
    internal const float ContactSkin = 0.001f;
    internal static Vector3 Recover(IPhysicsWorld? world, Vector3 feet, in MoveTuning tuning, out bool cleared);
    internal static float Lift(IPhysicsWorld? world, Vector3 feet, in MoveTuning tuning);
    internal static ShellSweep Sweep(IPhysicsWorld? world, Vector3 feet, float lift, Vector2 move,
        in MoveTuning tuning, float cosMaxSlope);
}
```

`Recover` pushes the shell out along the true MTV from `ComputePenetration`, at most 4 passes, and sets `cleared`.
`Lift` sweeps the shell up by `StepHeight` and returns the clear distance less the skin, never negative. `Sweep`
moves the shell, raised by `lift`, horizontally by `move` (one substep, the caller splits), advancing to a hit less
the skin and removing the move's component along the contact's horizontal normal for a Wall, Ceiling or
RisingSupport contact, at most 4 slides. A null world moves freely, lifts the full `StepHeight` and recovers nothing.

- [ ] **Step 1: Write the failing tests** (`MoveTuning.Default`, `cosMaxSlope = cos(45 degrees)`, box variant):
  - `FreeMoveIsExact`: flat floor, move (0.15, 0.05) gives `Achieved` exactly equal and `Blocked` false.
  - `HeadOnWallStopsAtTheSkin`: wall face at x 1, feet x 0, move (0.2, 0) from x 0.39: achieved x within 1e-5 of
    `1 - 0.4 - 0.001 - 0.39`, `Blocked` true.
  - `AngledWallKeepsTheTangent`: approaches at 30 and 60 degrees into a wall along z keep the z component of the
    move exactly once in contact.
  - `InnerCornerStopsBoth`: two walls meeting at a right angle, a diagonal move into the corner ends with both
    components blocked.
  - `MovingParallelAlongAWallKeepsFullSpeed`: start at skin distance from a wall, move parallel: achieved equals move.
  - `LowCrateNeverTouchesTheShell`: a 0.3 m crate in the path is not hit (legs territory).
  - `CeilingLimitsTheLift`: ceiling underside at feet + 1.8 + 0.1: lift within 1e-5 of 0.099.
  - `RecoveryPushesTheShellOut` and `DeepOverlapReportsNotCleared`: a box overlapping the shell by 0.05 clears in at
    most 4 passes, a box enclosing the shell centre reports `cleared` false with a finite position.
  - `NullWorldMovesFreely`: world null gives exact achieved, lift `StepHeight`, no recovery.
- [ ] **Step 2: Run, expect build failure (`CS0246` or `CS0103`)**
  `build-slot --label p2-t1 -- dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release -m:1 --filter "FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts.ShellMotionTests"`
- [ ] **Step 3: Implement `ShellMotion`** as specified above.
- [ ] **Step 4: Run to green** with the Step 2 command.
- [ ] **Step 5: Format and commit** `feat(locomotion): move the contact controller shell`, then push the branch.

### Task 2: Ground seat rules

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/GroundSeat.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundSeatTests.cs`

**Interfaces:**
- Consumes: `FootSupport.Find`, `FootSupportQuery`, `SupportSample`, `SupportStatus`.
- Produces:

```csharp
internal enum SeatOutcome : byte { Seated, SteepSeated, Airborne, Wall, Refused }
internal readonly record struct GroundSeatResult(SeatOutcome Outcome, float FeetY, SupportSample Support,
    float StepPart, Vector3 WallNormal);
internal static class GroundSeat
{
    internal static GroundSeatResult Resolve(Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        in SupportSample start, Vector2 startAxis, float startFeetY, Vector2 axis, Vector2 moveDirection,
        float footRadius, in MoveTuning tuning);
}
```

Rules, in order: analytic cliff (terrain at `axis` above `startFeetY + StepHeight`) gives `Wall` with the terrain
normal made horizontal, or `-moveDirection` without a normal delegate. Otherwise query `FootSupport` with the band
around `startFeetY`. `Walkable` gives `Seated` at its height with `StepPart` = height less the start support's plane
extrapolated to `axis` (`h + (n.x (sx - ax) + n.z (sz - az)) / n.y`). `Steep` whose `Height - HeightError` exceeds
`startFeetY` gives `Wall` with the steep normal made horizontal, otherwise `SteepSeated`. `None` gives `Airborne` at
`startFeetY`. `Refused` gives `Refused`.

- [ ] **Step 1: Write the failing tests**, box and mesh where the scene allows:
  - `FlatToLipSeatsWithTheWholeRiseAsStepPart`: start on the floor, axis 0.1 before the lip edge: `Seated`, height
    0.0425, `StepPart` within `HeightError` of 0.0425.
  - `SlopeContinuationHasNoStepPart`: 20 degree slope, move 0.1 up it: `Seated`, `StepPart` magnitude at most
    `HeightError + 1e-6`.
  - `SteepRiseIsAWall`: 60 degree face rising ahead: `Wall`, normal horizontal and pointing back at the body.
  - `SteepBelowSeatsSteep`: walk off onto a 60 degree face below: `SteepSeated`.
  - `LedgeBeyondStepHeightIsAirborne`: drop 0.41: `Airborne` at the start height. Drop 0.40: `Seated`.
  - `CurvedPropRefuses`: a sphere under the axis: `Refused`.
  - `AnalyticCliffIsAWall`: world null, terrain rising 0.5 within the move: `Wall` with the terrain normal made
    horizontal, and with no normal delegate `Wall` with `-moveDirection`.
- [ ] **Step 2: Run, expect build failure**, filter `FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts.GroundSeatTests`.
- [ ] **Step 3: Implement `GroundSeat.Resolve`.**
- [ ] **Step 4: Run to green.**
- [ ] **Step 5: Format and commit** `feat(locomotion): seat the contact controller on certified support`, push.

### Task 3: Ground core step

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/GroundCore.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundCoreTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces the spec's interface verbatim: `GroundCoreSettings`, `GroundFooting { Walkable, Steep, None, Held }`,
  `GroundStepResult(Feet, Footing, Support, Rise, Achieved, Blocked)`, and `GroundCore.Step(feet, displacement, dt,
  tuning, settings, groundHeight, groundNormal, world, lease)`.

Orchestration per the spec's tick: validate, recover, start support (`Refused` gives `Held` at the start), lift,
then per substep of at most `CapsuleRadius / 2`: `ShellMotion.Sweep` then `GroundSeat.Resolve`. A `Wall` outcome
undoes that substep's advance and slides the rest of it along the wall's horizontal tangent once. A `Refused`
outcome retries the substep once with lift 0, then blocks. `Airborne` and `SteepSeated` end the tick there with
`None` or `Steep` footing. The tick's total step part is capped at `MaxStepClimbSpeed * dt` when positive. The
seated shell must not overlap, else recover once, else block at the last clear position.

- [ ] **Step 1: Write the failing tests**:
  - `FreeMotionIsExact`: flat prop floor, terrain only, 5 and 20 degree slopes up, down and across: `Achieved`
    equals `displacement` exactly, footing `Walkable`.
  - `StandingStillHoldsExactly`: zero displacement on flat and on a 20 degree slope: `Feet` bit-identical to input.
  - `StepUpWithinStepHeight`: lip 0.0425 and crate 0.3: footing `Walkable`, final height the top. A step of
    `StepHeight + 0.01` blocks.
  - `StepDownSeatsOrFalls`: drops 0.32 and 0.40 seat in one tick, 0.41 and 0.60 give `None`.
  - `SteepRiseBlocksAndSlides` and `WalkingOffOntoSteepGivesSteep`.
  - `AnalyticCliffBlocksAndSlides`: world null.
  - `RefusedTargetBlocks` and `RefusedStartHolds`: sphere prop.
  - `LowCeilingBlocksEntry`: a ceiling ahead lower than the shell top (feet + 1.8) blocks the move. A ceiling
    within the lift above the start still allows a flat move.
  - `InvalidInputsThrow`: non-finite feet, `dt` 0, a tuning whose shell fails validation.
- [ ] **Step 2: Run, expect build failure**, filter `FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts.GroundCoreTests`.
- [ ] **Step 3: Implement `GroundCore.Step`.**
- [ ] **Step 4: Run to green.**
- [ ] **Step 5: Format and commit** `feat(locomotion): resolve a grounded contact controller tick`, push.

### Task 4: Ground scenarios, determinism and cost

**Files:**
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundScenarioTests.cs`, and a test-only
  `KhaozEngine.Game.Tests/Locomotion/Contacts/CountingQueryView.cs`
- Create: `docs/verification/2026-10-10-p2-query-cost.json`
- Modify: `KhaozEngine.Locomotion/Contacts/*.cs` only for a real defect a scenario exposes, reported as such

**Interfaces:**
- Consumes: `GroundCore.Step`. `CountingQueryView` implements `IPhysicsWorldQueryView` and `IPhysicsCapsuleFeatures`
  over a real selected view, forwards every call (leases come from the inner view, `SourceWorld` is the inner
  view's), and counts `SweepCapsule`, `ComputePenetration`, `Raycast` and `QueryCapsuleFeature` calls.

Run each scenario as a loop of `GroundCore.Step` at 30 Hz, feeding the result's feet into the next tick.

- [ ] **Step 1: Write the tests**:
  - `RestingBankApproachConverges` (#1270): the 4.25 cm lip with the #1270 capsule (radius 0.3, half-height 0.75),
    feet from x -0.375 on the bank toward x -0.125, at 1 m/s, commanding `min(remaining, 1 * dt)` each tick: within
    1 mm (3D) of the feet `FootSupport` reports at the target axis, in at most 30 ticks. Under the footprint model
    that target may be the lip top, since the disc reaches the lip edge.
  - `SlopeEdgesConverge` (#1265): 0.08 and 0.25 grade mesh slopes, uphill, downhill and sideways edges of 0.25 m,
    same command rule: within 1 mm (3D) of the target axis support in at most 30 ticks.
  - `StairsClimbEveryRiser`: treads 0.40 and 0.35, risers 0.25 and 0.30, speeds 2, 3, 4 and 6 m/s: the top tread is
    reached, footing never `None`, and each tick's step part at most `MaxStepClimbSpeed * dt` plus `HeightError`.
  - `SteepWalkableSlopeIsNeverPaced`: 40 degree mesh slope at 6 m/s for 60 ticks: `Walkable` every tick and the feet
    on the slope plane within `HeightError`.
  - `LargeMoveCannotTunnelAThinWall`: a 0.05 m wall, one 5 m displacement: blocked before the wall.
  - `ReplayIsBitIdentical`: a 256-tick stair-and-wall sequence run twice from copies gives bit-identical results.
  - `QueryCostPerTick`: through `CountingQueryView`, record calls per tick on flat ground, stairs and a wall slide,
    assert each is at most the spec's budget (2 support queries per substep plus shell sweeps, recovery and lift),
    and write the measured counts into the verification JSON.
- [ ] **Step 2: Run** with filter `FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts.GroundScenarioTests`.
  A failing scenario is investigated to its root cause first. Never relax an expectation or a skin to pass.
- [ ] **Step 3: Commit** `test(locomotion): prove the ground core scenarios`, push, and comment the measured cost
  on #1334.

### Task 5: Finish phase 2

**Files:** `CHANGELOG.md`, `docs/INDEX.md`, the phase 2 spec status line, `docs/verification/2026-10-10-p2-final.json`.

- [ ] **Step 1:** Append one line to the staged `## 20.30.0` entry: the ground core landed internally, nothing
  consumes it, movement unchanged. Set the phase 2 spec and its INDEX row to implemented. Sweep Markdown for the new
  type names.
- [ ] **Step 2:** Full verification once, heavy lane: build, tests excluding `LiveSocket`, format, then the five
  repository guards. Record totals in the verification JSON. Commit `docs(release): record contact controller
  phase 2`, push.
- [ ] **Step 3 (controller):** fetch and merge `origin/main`, whole-branch review, fast-forward main, push, run
  `scripts/pack-local-feed.sh` from main, comment on #438. No tag.
