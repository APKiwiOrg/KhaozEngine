# Contact controller phase 4a (ground signals) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The contact controller exports `StepDeltaY`, `ClimbRate` and `ClimbRateEwma` with the legacy public meanings, from certified support.

**Architecture:** The ground core classifies each accepted substep's discrete step by plane agreement and reports the applied discrete rise. A new `GroundSignals` unit turns the tick's ground facts into the three signals, with a gated look-ahead for runs. The controller sums ground segments and zeroes signals on every other tick.

**Tech Stack:** C# (.NET 10), xUnit, `KhaozEngine.Locomotion` internal contact controller, Bepu statics through `FootSupportScenes`.

**Spec:** `docs/design/CONTACT-CONTROLLER-PHASE-4A-GROUND-SIGNALS-2026-10-11.md`

## Global Constraints

- No public API, wire field or carried state is added. Everything new is `internal`.
- Run grade 0.3, ascent seed 0.7, smoothing rate 5 per second, descent cap 6 m/s, descent grade clamp 0.3 to 2, look-ahead at k = 1, 2, 3 capsule radii with `ReachUp = ReachDown = StepHeight + 2 * k * CapsuleRadius`.
- The step classifier's only floor is `ShellMotion.ContactSkin` plus the two supports' certified height errors (spec Ruling 4a-1). The same floor decides whether carried lag counts as owed discrete rise.
- Signals never feed movement. Replay is bit-identical, signals included. A warm tick allocates nothing.
- Every local build and test goes through build-slot: `build-slot --label p4a -- <command>`, and `--heavy` for the full build and suite. Exit 75 means nothing ran, so retry. Never loop tests.
- Focused tests: `dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj -c Release --filter FullyQualifiedName~Locomotion.Contacts`.
- Docs: no em dashes, en dashes or prose semicolons. Commit subjects `area(scope): summary`. Zero warnings.
- Test expectations come from authored geometry and closed forms, never legacy numbers. Geometric bounds keep half the contact skin (`ShellMotion.ContactSkin / 2`).
- Version: ride the staged 20.31.1. Check `git tag --sort=-v:refname | head -1` before the changelog edit and stop if `v20.31.1` exists.

## Review Focus

- A body climbing a staircase diagonally (30 degrees off the run) still reads the run, because the grade is taken along achieved travel. Task 3 row.
- Stairs that end at a wall: achieved travel is zero, so no look-ahead and no NaN direction. Task 3 row.
- Look-ahead samples landing on steep or refused support (a stair beside a steep wall) are skipped, never counted. Task 3 row.
- A replayed tick whose input carries a quantized `ClimbRate` (a multiple of 0.05) and a seeded EWMA gives the same outputs on every run. Task 3 row.
- Stairs on top of curved analytic terrain: the terrain between statics contributes zero discrete step. Task 1 row.

---

### Task 1: Discrete steps and applied step rise in the ground core

**Files:**
- Modify: `KhaozEngine.Locomotion/Contacts/GroundSeat.cs`
- Modify: `KhaozEngine.Locomotion/Contacts/GroundPlacement.cs`
- Modify: `KhaozEngine.Locomotion/Contacts/GroundCore.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundSeatTests.cs`, new `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundCoreTests.Steps.cs`
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/FootSupportScenes.cs` (scenes below)

**Interfaces:**
- Produces: `internal static float GroundSeat.DiscreteStep(in SupportSample start, Vector2 startAxis, in SupportSample next, Vector2 axis)`.
- Produces: `GroundSeatResult` gains a trailing positional `float DiscreteStep` (zero for every outcome but `Seated`).
- Produces: `GroundPlacement` gains a trailing positional `double Paid` (the budget `Paced` spent, zero when it spent nothing).
- Produces: `GroundStepResult.StepRise { get; init; }` (float, applied discrete rise, signed) and `GroundStepResult.MoveRise { get; init; }` (float, feet change from accepted placements and lag payments, recovery excluded).
- Produces scenes: `FootSupportScenes.Staircase(SceneVariant variant, int steps, float riser, float tread)` ascending +X from x = 0 with a floor before and a landing after, and `FootSupportScenes.Doorstep(SceneVariant variant, float height)` a single step at x = 0.

- [ ] **Step 1: Write the failing classifier rows in `GroundSeatTests.cs`**

`DiscreteStep` with hand-built `SupportSample` values (no physics):
- `RiserBetweenLevelTreadsIsItsHeight`: start (0, 0) level at 0, next (0.1, 0) level at 0.18 → `0.18f` exactly.
- `ConvexAndConcaveCurvesAreNotSteps`: samples on `h = 0.5x²` and `h = -0.5x²` at x 0.3 and 0.4 with their true normals → `0f`.
- `PlanarRampIsExactlyZero`: two samples on a 30 degree plane → `0f`.
- `DropIsTheNegativeHeight`: level 0.18 to level 0 → `-0.18f`.
- `StepOntoASlopedTreadIsTheRiser`: level 0 to a 10 degree tread whose plane passes 0.18 at the new axis → within `|rf - rb|` of 0.18, positive.
- `NonWalkableEitherSideIsZero`: steep or none on either side → `0f`.

- [ ] **Step 2: Run them, expect a compile failure (no `DiscreteStep`)**

- [ ] **Step 3: Implement `DiscreteStep`**

`rf = h1 - plane(S0 at a1)` (the existing `StepPart` formula without the `NotBelow` rounding), `rb = plane(S1 at a0) - h0`. Return `sign(rf) * min(|rf|, |rb|)` when the signs agree and the smaller magnitude exceeds `ShellMotion.ContactSkin + start.HeightError + next.HeightError`, else 0. Compute in double. Fill `GroundSeatResult.DiscreteStep` for `Seated`.

- [ ] **Step 4: Write the failing ground core rows in `GroundCoreTests.Steps.cs`**

Through `GroundCore.Step` over successive ticks, box and mesh, 30 and 60 Hz:
- `DoorstepRiseSumsToItsHeight`: walking +X over `Doorstep(0.18)`, the sum of `StepRise` over the ticks is 0.18 within one skin, every tick's `StepRise` is at least zero, and at most `ceil(0.18 / (MaxStepClimbSpeed * dt)) + 1` ticks are nonzero.
- `DoorstepDropIsItsNegativeHeight`: walking -X off the doorstep, one tick with `StepRise` -0.18 within one skin.
- `SmoothGroundHasNoStepRise`: flat floor, a 20 degree static ramp, a planar analytic slope and the analytic hill `h = 0.2 sin(x) cos(z)`: `StepRise` is exactly 0 every tick for 60 ticks.
- `StairsOnAnAnalyticHillCountOnlyTheRisers` (Review Focus): `Staircase(4, 0.18, 0.3)` placed over the analytic hill, the summed `StepRise` equals 4 × 0.18 within one skin.
- `UnlimitedBudgetAppliesTheStepWhereItIsFound`: `MaxStepClimbSpeed` 0, the doorstep's 0.18 lands in one tick.
- `DropWhileLaggingReportsTheFeetChange`: a 0.3 riser then an immediate 0.3 drop within the lag, `StepRise` on the drop tick equals that tick's feet change from the drop within half a skin.
- `PartialDropKeepsTheRestOwed`: analytic level support 0 before x -0.15, 0.3 to x 0, 0.2 after, default tuning at 60 Hz, from (-0.3, 0, 0) two +0.2 m ticks: the second tick's `StepRise` equals its feet change within half a skin, and later ticks report the remaining rise to 0.2 as it is paid, so the run's total `StepRise` equals the final feet height within one skin.
- `PacedHillLagStaysContinuous`: the hill `0.2 sin(x) cos(z)` with true normals, `MaxStepClimbSpeed` 0.001, 60 Hz, +0.1 m per tick from (-1, z 0.4) for 30 ticks with `stepInProgress` false: `StepRise` exactly 0 every tick.
- `MoveRiseExcludesRecovery`: a start overlapping a box edge, `MoveRise` excludes the recovery push (compare to `Rise`).

- [ ] **Step 5: Run them, expect failure**

- [ ] **Step 6: Implement owed discrete rise in `GroundCore`**

`Paced` reports `paid`. `GroundCore.Step` takes a new optional `bool stepInProgress = false` (the controller passes input `StepDeltaY > 0 || ClimbRate > 0`). Per segment, start `O` at the carried lag below walkable start support only when `stepInProgress` and the lag exceeds `ContactSkin` plus the support's certified error, else 0. Each accepted substep and each `PayLag` payment accounts as one move: `H = O + DiscreteStep` (0 for `PayLag`), `L` = lag below the new walkable support after the move, `O' = clamp(L, 0, max(H, 0))`, applied `= H - O'`. `MoveRise` sums accepted placements' feet changes and lag payments.

- [ ] **Step 7: Run all of `Locomotion.Contacts`, expect PASS with no existing row changed**

- [ ] **Step 8: Commit**

`fix(locomotion): classify discrete steps by plane agreement in the ground core`

### Task 2: StepDeltaY from the controller

**Files:**
- Modify: `KhaozEngine.Locomotion/Contacts/ContactController.cs`
- Test: new `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactControllerTests.Signals.cs` (partial of `ContactControllerTests`)

**Interfaces:**
- Consumes: `GroundStepResult.StepRise`, `GroundStepResult.MoveRise` (Task 1).
- Produces: `Tick` gains `float StepRise, float MoveRise, Vector2 GroundTravel` (sums over the tick's ground segments, travel as the summed achieved horizontal ground displacement) and keeps final footing. Task 3 reads them.

- [ ] **Step 1: Write the failing rows**

Through `Ground.Step` (the existing test helper), box and mesh, 30 and 60 Hz:
- `DoorstepReportsItsHeightAsAStep`: up and down the doorstep, `StepDeltaY` sums to ±0.18 within one skin, `ClimbRate` and `ClimbRateEwma` stay 0.
- `SmoothGroundReportsNoSignals`: the four smooth surfaces of Task 1, all three signals exactly 0 every tick.
- `AirAndSlidesReportNoSignals`: a jump arc over `Staircase(4, 0.18, 0.3)`, a fall from 1 m onto the doorstep's top, a slide down a 60 degree analytic face. All three signals 0 every tick, landing ticks included.
- `AJumpTickKeepsItsGroundStep`: walking onto the doorstep with jump held, the tick that mounts the step and jumps reports the step.
- `HeldAndFallbackReportZeros`: a held tick (refused start) and the non-finite clamp fallback export 0 for all three.
- `PacingOffReportsTheDoorstepInOneTick`: `MaxStepClimbSpeed` 0, one tick with `StepDeltaY` 0.18 within one skin.
- `HeldKeepsTheGrant`: a held tick keeps `SupportGranted` true (pins the phase 3 policy).

- [ ] **Step 2: Run, expect failure**

- [ ] **Step 3: Implement**

Pass `stepInProgress: state.StepDeltaY > 0 || state.ClimbRate > 0` to the tick's walking `GroundCore.Step` calls (slides pass false). Accumulate the three sums in `Tick` from ground segments only. Set `StepDeltaY = StepRise` on ticks whose final mode is ground with walkable footing, 0 otherwise. Air, slide, wedge, held and fallback ticks export 0 for all three signals and reset the EWMA. Extend `Finite` to `ClimbRate`, `ClimbRateEwma` and `StepDeltaY`. Leave `ClimbRate` and `ClimbRateEwma` at 0 (Task 3).

- [ ] **Step 4: Run `Locomotion.Contacts`, expect PASS**

- [ ] **Step 5: Commit**

`fix(locomotion): export the contact controller's step events`

### Task 3: Runs, ClimbRate and the EWMA

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/GroundSignals.cs`
- Modify: `KhaozEngine.Locomotion/Contacts/ContactController.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactControllerTests.Signals.cs`

**Interfaces:**
- Consumes: Task 2's `Tick` sums, `FootSupport.Find`, the tick's lease, world, terrain callbacks, foot radius and traction gate.
- Produces: `internal static class GroundSignals` with constants `RunGrade = 0.3f`, `SeedFraction = 0.7f`, `SmoothingRate = 5f`, `MaxDescentRate = 6f`, `MaxDescentGrade = 2f`, `Samples = 3`, and `internal static (float ClimbRate, float Ewma, float StepDeltaY) Resolve(in MoveState input, float stepRise, float moveRise, Vector2 groundTravel, Vector3 feet, in SupportSample axisSupport, bool walkableGround, float dt, in MoveTuning tuning, float footRadius, float cosMaxSlope, Func<float, float, float>? groundHeight, Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease)` returning the three outputs. `walkableGround` is true only when the tick's final mode is ground with walkable footing.

- [ ] **Step 1: Write the failing rows**

Scene `Staircase(8, 0.18, 0.28)`, box and mesh, 30 and 60 Hz, walk and run:
- `StairAscentExportsAContinuousClimbRate`: from the first riser's tick to the last, `ClimbRate > 0` every tick, never negative, `StepDeltaY` 0 every tick it is nonzero, the mean `ClimbRate` over the run within 0.25 m/s of (8 × 0.18) / run duration, and no tick above `MaxStepClimbSpeed`.
- `StairDescentExportsANegativeClimbRate`: walking -X from the landing, `ClimbRate < 0` and at least -6 through the run, `StepDeltaY` 0 throughout, `ClimbRateEwma` 0.
- `DiagonalAscentStillReadsTheRun` (Review Focus): moving at 30 degrees off +X, the run registers.
- `StairsEndingAtAWallExportNothingOnceStopped` (Review Focus): a wall on the landing, once achieved travel is 0 all three signals are 0 and finite.
- `SamplesOnSteepSupportAreSkipped` (Review Focus): a staircase whose look-ahead samples fall on a 70 degree face beside it, the run grade uses only walkable samples, and with fewer than two walkable ahead no run is reported.
- `DeniedDescentExportsNothing`: pressing into a wall at the top of the descending stair, `ClimbRate` 0.
- `ReplayFromAQuantizedClimbRateIsIdentical` (Review Focus): an input with `ClimbRate = 35 * 0.05f` and `ClimbRateEwma = 1.75f` mid-stair, two runs give bit-identical outputs.
- Extend `ReplayIsBitIdentical` so a stair run is in its sequence.
- `WarmSignalResolveAllocatesNothing`: under one acquired lease, after two warm-up calls, `GC.GetAllocatedBytesForCurrentThread()` is unchanged across a `GroundSignals.Resolve` call that runs the look-ahead on the staircase. (A whole tick allocates the backend read lease, which is #1334's.)
- `SmoothGroundReportsNoSignals` (existing, Task 2) must keep passing unchanged: carried `ClimbRate` 1 and EWMA 2 on a 20 degree ramp and a planar analytic slope report zero.

- [ ] **Step 2: Run, expect failure**

- [ ] **Step 3: Implement `GroundSignals` and wire it**

Trigger, samples (sample 0 is `axisSupport`, no query), least-squares grade, window steps by `GroundSeat.DiscreteStep` between consecutive walkable samples, run start and continuation, and rates exactly as the spec's Runs and Rates sections (Ruling 4a-7). The ascent rate is `MoveRise / dt` (step rise plus plane following). Use stack storage for the four samples. Call it once at the end of the tick, before the jump, inside the existing lease.

- [ ] **Step 4: Run `Locomotion.Contacts`, expect PASS**

- [ ] **Step 5: Commit**

`fix(locomotion): export continuous stair runs from the contact controller`

### Task 4: Scenario invariants, docs and finish

**Files:**
- Modify: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactScenarioTests.cs`
- Modify: `docs/design/CONTACT-CONTROLLER-PHASE-4A-GROUND-SIGNALS-2026-10-11.md` (status, rulings made in implementation), `docs/design/CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md` (phase 4 row and status), `docs/INDEX.md` (new row), `CHANGELOG.md` (one line in `## 20.31.1`)
- Create: `docs/verification/2026-10-11-p4a-final.json`

- [ ] **Step 1: Add the harness assertion**

Every scenario tick asserts the three signals are finite and that `ClimbRate` and `StepDeltaY` are never both nonzero. Run the scenario suite, expect PASS.

- [ ] **Step 2: Docs**

Spec status: implemented, staged for 20.31.1, with any implementation ruling. Program phases table: phase 4 split into 4a (implemented, staged for 20.31.1) and 4b (to be specified). INDEX row for the 4a spec. CHANGELOG line: the contact controller exports step events and stair climb rates from certified support, still with no consumer or movement change.

- [ ] **Step 3: Full verification once, heavy lane**

`dotnet build KhaozEngine.slnx -c Release`, `dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`, `dotnet format KhaozEngine.slnx --verify-no-changes --no-restore`, the five repository guards and `bash scripts/check-doc-versions.sh`. Record totals and exit codes in the JSON.

- [ ] **Step 4: Commit**

`docs(release): record contact controller phase 4a`
