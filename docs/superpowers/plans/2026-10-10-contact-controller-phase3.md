# Contact Controller Phase 3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The whole tick of the contact controller (intent, gravity, jump, coyote, buffer, air control and
momentum, the air pass, landing, slides, traction hysteresis, commitments, facing) as an internal pure function
with `StepCore`'s inputs.

**Architecture:** `ContactController.Step` orchestrates one tick in the legacy order. Grounded ticks run the phase 2
`GroundCore`, airborne ticks run a new `AirPass` (shell sweep with ceilings, footprint landing), and steep ticks run
a new `SlideCore` (certified fall-line dynamics through the ground core). Pure legacy rule helpers are reused, made
internal.

**Tech Stack:** C# (.NET 10), BepuPhysics 2.4.0, xUnit.

**Spec:** `docs/design/CONTACT-CONTROLLER-PHASE-3-AIR-AND-STATE-2026-10-10.md`. Read "Interface", "The tick", "The air
pass", "Slides" and "Suite". Legacy algorithms live in `KhaozEngine.Locomotion/CharacterMovement*.cs` and their
tests in `KhaozEngine.Game.Tests/Locomotion/` (mine geometry, never numbers).

## Global Constraints

- Every local build or test runs through build-slot: `build-slot --label p3 -- <command>`, and
  `build-slot --heavy --label p3 -- <command>` for the full build and suite. Exit 75 means nothing ran, so retry.
  Never loop tests. Longest timeout the shell allows.
- `TreatWarningsAsErrors` holds. Zero warnings. Hooks on. Explicit paths. Subjects `area(scope): summary`.
- No em or en dashes and no prose semicolons in shipped text, comments included.
- No `MoveTuning` field, wire, navigation or consumer change. Legacy behaviour unchanged. The only legacy edits are
  `private` to `internal` on the named helpers.
- Expectations are derived from geometry and closed forms, never from a run. No assertion finer than half the
  contact skin (`ShellMotion.ContactSkin / 2`). No committed bit digest that passes through a Bepu sweep.
- Box and mesh variants wherever the geometry allows.
- Version: ride the staged 20.31.0. Check `git tag --sort=-v:refname | head -1` before a changelog edit and stop if
  v20.31.0 is tagged.

## Review Focus

- **Fast falls.** A 50 m/s fall at 30 Hz onto a thin slab lands on it and never tunnels. Pinned in Task 2
  (`TerminalFallLandsOnAThinSlab`).
- **Ceiling at launch.** A jump with a ceiling 1 cm above the head neither rises nor sticks, and falls back to the
  floor. Pinned in Task 2 (`JumpUnderALowCeilingStaysDown`).
- **Landing mid-tick keeps horizontal motion.** A run that lands mid-tick on flat ground achieves the full
  horizontal displacement of the tick. Pinned in Task 4 (`LandingMidTickKeepsTheHorizontalMove`).
- **Slide onto a prop edge.** A slide that reaches a walkable prop top grounds on it with one impact. Pinned in
  Task 3 (`SlideOntoAPropTopGrounds`).
- **Replay.** 256 mixed ticks (ground, jump, air, slide, commitment) replayed from a copied `MoveState` are
  bit-identical. Pinned in Task 4 (`ReplayIsBitIdentical`).

---

### Task 1: Traction gate, banded validation and shared helpers

**Files:**
- Modify: `KhaozEngine.Locomotion/Contacts/GroundCore.cs`, `GroundSeat.cs`, `GroundPlacement.cs` (gate plumbing)
- Modify: `KhaozEngine.Locomotion/Contacts/ShellGeometry.cs`
- Modify: `KhaozEngine.Locomotion/CharacterMovement*.cs` (`private` to `internal` on `ResolveFacing`,
  `ResolveAirborneVelocity`, `ClipCarryToAchieved`, `LandingImpact`, `SlideFrictionScale`, `SlideFallLineStep`,
  `PrepareCommitmentTick`, `FinishCommitmentTick` and any intent helper Task 4 needs, nothing else)
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/GroundCoreTests.cs`, `ShellGeometryTests` wherever validation is
  tested today

**Interfaces:**
- Produces: `GroundCore.Step(..., float? tractionSlopeRadians = null)` as the last parameter. Null means
  `tuning.MaxSlopeRadians`. Every slope decision inside the tick (support query `CosMaxSlope`, steep seat, steep
  wall, analytic cliff normal) uses the gate.
- Produces: `ShellGeometry.Validate` checks the plane constraint against `MaxSlopeRadians +
  TractionHysteresisRadians`.

- [ ] **Step 1: Write the failing tests:** `GroundCoreTests.BandedGateKeepsFootingOnTheBank` (a 46 degree mesh and box
  slope: with gate `45 + 3` degrees the walk stays Walkable, with the bare gate it is Steep), and
  `ShellGeometryTests.ShellMustClearTheBandedPlane` (a tuning that passes at 45 degrees but fails at 48 throws).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~KhaozEngine.Tests.Locomotion.Contacts"`. Expected: the new rows
  fail, nothing else.
- [ ] **Step 3: Implement** the parameter and the validation, and widen the helpers.
- [ ] **Step 4: Run** the Contacts filter and `KhaozEngine.Tests.Locomotion` once. Expected: all pass, legacy rows
  unchanged.
- [ ] **Step 5: Commit** `feat(locomotion): plumb the traction gate through the ground core`.

### Task 2: The air pass

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/AirPass.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/AirPassTests.cs`

**Interfaces:**
- Consumes: `ShellMotion` (recovery, sweep, contact classes), `FootSupport`, `GroundSeat.Query` for bands,
  `SupportCertification.ContactBand`.
- Produces:

```csharp
internal enum AirOutcome : byte { Airborne, Landed, Sliding }

internal readonly record struct AirStepResult(Vector3 Feet, float VerticalVelocity, Vector2 Achieved,
    Vector2 Remaining, AirOutcome Outcome, SupportSample Support, float FallSpeedAtContact, bool Blocked);

internal static class AirPass
{
    // velocity is the tick's horizontal velocity, verticalVelocity the carried one before gravity.
    internal static AirStepResult Step(Vector3 feet, Vector2 velocity, float verticalVelocity, float dt,
        float gravity, in MoveTuning tuning, in GroundCoreSettings settings, float tractionSlopeRadians,
        Func<float, float, float>? groundHeight, Func<float, float, Vector3>? groundNormal,
        IPhysicsWorld? world, IPhysicsQueryLease? lease);
}
```

- Rules from the spec: gravity first, clamped at `-MaxFallSpeed`. substeps of at most half the capsule radius over
  the full 3D displacement. shell sweep with slides where a ceiling removes the upward component and zeroes positive
  vertical velocity. after each substep a `FootSupport` landing check over the substep's feet span raised by
  `max(0, verticalVelocity * dt)`. landing only while descending. walkable lands (feet seat, vertical velocity
  zero, `Remaining` is the unspent horizontal displacement), steep starts a slide. `FallSpeedAtContact` is the
  vertical speed at the landing substep, positive downward.

- [ ] **Step 1: Write the failing tests** (box and mesh where possible): `GravityReachesTerminalSpeed`,
  `FallLandsOnFlatWithTheContactSpeed`, `TerminalFallLandsOnAThinSlab` (0.05 m slab, 50 m/s, 30 Hz),
  `JumpUnderASlabStopsRising`, `JumpUnderALowCeilingStaysDown`, `RunJumpUnderAnEaveNeverWedges`,
  `RunJumpIntoAWallSlides`, `InnerCornerStopsBothComponents`, `FallOntoACrateTopIsNotSunk`,
  `JumpOntoALedgeLandsWhenDescending` (legs pass the edge while rising), `LandingNeverRisesAboveTheReach`
  (invariant 5: a descending body beside a ledge top above its feet never lands on it), `FallOntoSteepStartsASlide`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~AirPassTests"`. Expected: build failure, then failures.
- [ ] **Step 3: Implement** `AirPass` in its own file. Reuse `ShellMotion` for the sweep with slides by extending it
  with a 3D sweep entry if needed, keeping its horizontal behaviour identical.
- [ ] **Step 4: Run** the filter, then the Contacts namespace once. Expected: all pass.
- [ ] **Step 5: Commit** `feat(locomotion): move the body through the air with footprint landing`.

### Task 3: Slides

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/SlideCore.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/SlideCoreTests.cs`

**Interfaces:**
- Consumes: `GroundCore.Step` with the gate, `FootSupport`, `CharacterMovement.SlideFallLineStep`,
  `CharacterMovement.SlideFrictionScale`.
- Produces:

```csharp
internal enum SlideOutcome : byte { Sliding, Wedged, Landed, Airborne }

internal readonly record struct SlideStepResult(Vector3 Feet, Vector2 HorizontalVelocity, float VerticalVelocity,
    Vector2 Achieved, SlideOutcome Outcome, SupportSample Support, float ImpactSpeed);

internal static class SlideCore
{
    // A body is sliding when FootSupport at its feet, reach 1 mm up and down, is Steep.
    internal static bool InContact(Vector3 feet, in MoveTuning tuning, in GroundCoreSettings settings,
        float tractionSlopeRadians, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        out SupportSample support);

    internal static SlideStepResult Step(Vector3 feet, Vector2 carry, float verticalVelocity, Vector2 steer,
        float dt, in MoveTuning tuning, in GroundCoreSettings settings, float tractionSlopeRadians,
        in SupportSample support, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease);
}
```

- Rules from the spec: fall line and contour from the steep support plane. fall-line speed by `SlideFallLineStep`
  with `Gravity * sin(slope)` along the plane and the `SlideFrictionScale` ramp at the gate. terminal
  `MaxFallSpeed / max(sin(slope), sin(gate))`. steer along the contour only. the horizontal displacement runs
  through `GroundCore.Step` with the gate. `VerticalVelocity` is the seated rise over `dt`. wedged when the seat ends
  no lower than it started and the support's fall lines oppose. landed when the support turns walkable (impact is
  the downward vertical speed). airborne when it turns `None`.

- [ ] **Step 1: Write the failing tests:** `SixtyDegreeFaceSlidesToTheToeWithOneImpact`,
  `FrictionRampSetsTheSlideRate` (46, 49, 53 and 75 degrees give the ramp's 0.125, 0.5, 1 and 1 against the
  closed form), `SteerMovesAlongTheContourOnly`, `VGullyWedges`, `SlideOnAPropMatchesTerrain` (box prop and
  analytic terrain of the same slope give the same fall-line speed within float error),
  `SlideOntoAPropTopGrounds`, `SlideOffAnEdgeGoesAirborne`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SlideCoreTests"`. Expected: failures.
- [ ] **Step 3: Implement** `SlideCore`.
- [ ] **Step 4: Run** the filter, then the Contacts namespace once. Expected: all pass.
- [ ] **Step 5: Commit** `feat(locomotion): slide on certified steep support`.

### Task 4: The tick

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/ContactController.cs`
- Test: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactControllerTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3, the shared legacy helpers, `IPhysicsQueryLeaseSource.AcquireQueryReadLease`.
- Produces: `ContactController.Step` exactly as the spec's Interface section.
- Order, from the spec: commitment prepare, gate, intent, branch (grounded through `GroundCore`, sliding through
  `SlideCore` when not grounded and `SlideCore.InContact`, else `AirPass`), a landing mid-air pass hands its
  `Remaining` to `GroundCore` in the same tick, jump last with `LandingImpactSpeed` and `SupportGranted` latched
  before it, then `TimeSinceGrounded`, `JumpBufferRemaining`, `HorizontalVelocity` via `ClipCarryToAchieved`,
  `CommandedVelocity`, `FacingYaw` via `ResolveFacing`, `FinishCommitmentTick`, `clampXz`. Climb signals stay zero.
  A non-null `medium` that reports water throws `NotSupportedException` until phase 4. Non-finite results return
  the input state with event fields zeroed.

- [ ] **Step 1: Write the failing tests:** `JumpStampsJumpSpeedAndRisesNextTick`, `NoDoubleJumpAtTheApex`,
  `CoyoteJumpInsideTheWindowOnly`, `BufferedJumpFiresOnLandingAndKeepsTheImpact`, `AirControlHalvesAirTravel`,
  `MomentumKeepsSpeedOnRelease`, `MomentumWithAirControlZeroIsBallistic`, `WallClipsTheCarry`,
  `LandingMidTickKeepsTheHorizontalMove`, `GroundParityWithTheGroundCore` (every `GroundCoreTests` scene stepped
  once with a grounded state gives the ground core's feet), the commitment rows (`PreparationRootsThenLaunches`,
  `WallBlocksThenLands`, `BlockedLaunchAborts`, `TimeoutAborts`, `AuthoredArcLandsOnTime`), `ReplayIsBitIdentical`,
  `WaterMediumIsRejectedUntilPhase4`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ContactControllerTests"`. Expected: failures.
- [ ] **Step 3: Implement** `ContactController`.
- [ ] **Step 4: Run** the filter, then the Contacts namespace and `KhaozEngine.Tests.Locomotion` once. Expected: all
  pass.
- [ ] **Step 5: Commit** `feat(locomotion): run the whole contact controller tick`.

### Task 5: Mined scenarios

**Files:**
- Create: `KhaozEngine.Game.Tests/Locomotion/Contacts/ContactScenarioTests.cs`
- Modify: `FootSupportScenes.cs` for new builders (rising face, creased cliff patch, lip onto face, eave)

- [ ] **Step 1: Write the tests**, each through `ContactController.Step` at 30 and 60 Hz:
  - `HeldJumpNeverClimbsASheerFace` (#440): 78.7 degree face, start 0.05 m from its toe, held jump with walk and
    run, momentum on and off, 3000 ticks: never grounded on the face, feet never above the ballistic reach plus
    `StepHeight` plus 0.05 m, second-half maximum within 20 mm of the first.
  - `LipOntoSteepFaceNeverSeats` (#470): 0.15 m lip onto a 63.4 degree face at 60 Hz and 6 m/s: the crossing tick
    is neither grounded nor support-granted and nothing launches from it. Control: a 0.35 m drop onto a level shelf
    seats in one tick.
  - `CreasedCliffNeverRatchets` (#468): a held run-jump across a creased cliff patch of 68.6 to 77.1 degree planes,
    over 360 headings in 15 degree steps: peak feet never rise from one crossing to the next.
  - `BankHysteresis` (#475): a 47 degree bank keeps a walker's footing and refuses a lander, 49 degrees refuses both,
    knobs at zero reproduce the bare gate.
  - `CliffToeNeverFlipsFooting` (#486): walking along a 60 degree face toe never alternates footing.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ContactScenarioTests"`. A failure is traced to its cause and
  fixed at the root with a focused row in the owning task's test file. Never relax an expectation.
- [ ] **Step 3: Commit** `test(locomotion): mine the air and slide scenarios`.

### Task 6: Finish phase 3

**Files:** `CHANGELOG.md`, `docs/INDEX.md`, the phase 3 spec status, the program spec phases table,
`docs/verification/2026-10-10-p3-final.json`.

- [ ] **Step 1:** Append one line to the staged `## 20.31.0` entry: the contact controller gains its whole tick
  (air, jumps, slides, commitments) internally, with no consumer or movement change. Set the phase 3 spec and its
  INDEX row to implemented.
- [ ] **Step 2:** Full verification once, heavy lane: Release build, full tests excluding LiveSocket (the filter in
  `docs/verification/2026-10-10-p2-final.json`), `dotnet format KhaozEngine.slnx --verify-no-changes --no-restore`,
  the five repository guards and `scripts/check-doc-versions.sh`. Record totals. Commit
  `docs(release): record contact controller phase 3`, push.
- [ ] **Step 3 (controller):** hosted x64 CI on the branch, whole-branch review, merge `origin/main`, fast-forward
  main, push, `scripts/pack-local-feed.sh`, comment on #438. No tag.
