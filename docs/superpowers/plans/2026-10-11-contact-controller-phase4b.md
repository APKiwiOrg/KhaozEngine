# Contact controller phase 4b (swimming handoff) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Swimming's explicit movement runs the contact controller for every land tick, keeps swimming's medium ownership, passes both StepFootprint tests unchanged and enforces #1299's deep-water rule at runtime.

**Architecture:** A leased internal entry (`ContactController.StepLeased`) shares the dry controller's tick machinery through a guarded `ContactQueryContext`. Support owners bind through a producer capability under the environment pin. Every accepted segment earns swimming's medium and boundary proof through an attempt-local `ContactMotionRoute`. Water ticks stay with swimming's handlers, and one continuation carries time, budget and signals across a mid-tick handoff.

**Tech Stack:** C# (.NET 10), xUnit, `KhaozEngine.Locomotion` (contacts and explicit movement), `KhaozEngine.Movement`, `KhaozEngine.NetWorld`, Bepu statics.

**Spec:** `docs/design/CONTACT-CONTROLLER-PHASE-4B-SWIMMING-HANDOFF-2026-10-11.md` (Rulings 4b-1 to 4b-20, Suite rows B1 to I1).

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/swimming-contract`, branch `feature/swimming-contract`. Use `git -C` and absolute paths.
- `AThirtyHertzColumnProducerApproachReachesAWithinStepHeightBank` and `AnOffColumnPointAloneIsNotAnAxisSupportPlaneCertificate` stay byte-for-byte unchanged and must pass (B1, B2).
- One owner of the ground and air algorithms: no second step or support search on the explicit path (4b-1).
- Every query runs inside the caller's one `MovementQueryLease`, scope-checked before the backend or producer call (4b-10). No repin mid-tick.
- Placement skin `s = 0.001 m`, fixed, vertical, never charged to the climb budget or counted as a step (4b-13). The dry controller keeps `s = 0`.
- Solid proofs use `ContactShell`. Water queries use the full body (4b-12).
- Generation 14 stays. No new wire field, carried detector, owner handle or lease token (4b-17). Phase 5 owns the `MoveTuning` selector and bake identity.
- `ContactController.Step` (dry) keeps its behaviour and arithmetic order. Existing Contacts rows keep their expectations.
- Expectations come from geometry and certified enclosures, normally no finer than half the contact skin.
- Every build and test through build-slot (`build-slot --label p4b -- <command>`, `--heavy` for full runs). Exit 75 means retry. Never loop tests.
- Docs: no em dashes, en dashes or prose semicolons. Commit subjects `area(scope): summary`. Zero warnings.
- Version: no version or changelog edit until the final integration task.

## Review Focus

- A lifted retry whose endpoints are dry but whose side path crosses forbidden water must not commit (P1, Task 3).
- A query failure late in a tick after earlier accepted substeps holds the whole input pose, with no partial prefix, step event, landing or grant (P4, Task 3).
- The 1 mm skin under a roof with less than 1 mm of free space cannot push the head through (B5, Task 2).
- A mixed ground, water, ground tick prepares and finishes its commitment once, turns once and resolves signals once (H4, Task 5).
- A thin 0.05 m deep band cannot be crossed in one long tick by a WadeOnly walker (W3, Task 7).

---

### Task 1: Producer support facts and same-pin guards

**Files:**
- Create: `KhaozEngine.Locomotion/MovementContactSupportFacts.cs`, `KhaozEngine.Locomotion/IMovementContactSupportPin.cs`, `KhaozEngine.Locomotion/MovementQueryLease.ContactSupport.cs`
- Modify: the Game and Server test environment producers (retain installed static handles, register floor and bank owners)
- Test: new `KhaozEngine.Game.Tests/Locomotion/ContactSupportBindingTests.cs`, affected `MovementEnvironmentContractTests`

**Interfaces:**
- Produces: `IMovementContactSupportPin` with `MovementAvailability SampleAnalyticGround(in MovementSupportRequest request, out MovementAnalyticGround? ground)` and `MovementAvailability BindContactSupport(in MovementContactSupportRequest request, out MovementContactSupportBinding binding)` (spec 4b-8, fields as the spec lists).
- Produces: internal guarded lease methods that call them under scope and currentness checks.

- [ ] **Step 1:** Write rows B3 and Q1 to Q4, plus analytic absence and room separation, failing first.
- [ ] **Step 2:** Implement the capability, facts and guarded wrappers. No movement change.
- [ ] **Step 3:** Run the new tests and `MovementEnvironmentContractTests`, expect PASS.
- [ ] **Step 4:** Commit `feat(locomotion): bind contact support to canonical owners under the environment pin`.

### Task 2: Contact query seam and skin adapter

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/ContactQueryContext.cs` and its leased adapter
- Modify: `Contacts/GroundCore.cs`, `GroundPlacement.cs`, `GroundSeat.cs`, `AirPass.cs`, `SlideCore.cs`, `ShellMotion.cs`, `GroundSignals.cs` (query plumbing only)
- Test: new `ContactLeasedQueryTests.cs`, existing `ContactControllerTests*`

**Interfaces:**
- Consumes: Task 1's guarded binding and analytic reads.
- Produces: internal `ContactQueryContext` (support find with binding, shell sweep, contacts and recovery, segment acceptance hook, medium classification) with a dry implementation forwarding today's calls and a leased one applying the `s` mapping (`f = C - UnitY * (H + s)`).

- [ ] **Step 1:** Write the zero-offset dry parity rows (bit-identical MoveState against `ContactController.Step`) and B4, B5, Q2, failing first where new.
- [ ] **Step 2:** Route every core query through the context without changing selection, certification or arithmetic.
- [ ] **Step 3:** Run `FullyQualifiedName~Locomotion.Contacts` and the new tests, expect PASS with no existing row changed.
- [ ] **Step 4:** Commit `refactor(locomotion): route contact queries through a query context`.

### Task 3: Selected-route proof and medium constraints

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/ContactMotionRoute.cs`
- Modify: the shared acceptance sites in `GroundCore`, `GroundPlacement`, `AirPass`, `SlideCore`, `ShellMotion`, and `MovementCapsuleResolver.cs` (factor its trace, projection and rounding proof)
- Test: new `ContactMediumRouteTests.cs`, `MovementCapsuleResolverTests`, affected non-swimmer retrace rows

**Interfaces:**
- Consumes: Task 2's context.
- Produces: an attempt-local ordered route (lift, side vertices with real tangents, paced seat, pushes, recovery and lag paths, flight segments) proved through full-body TraceWater, water policy constraints and `MovementBoundary.ProveSegment` before acceptance (4b-11).

- [ ] **Step 1:** Write P1 to P4, tied-boundary, push, lag-path and rollback rows, failing first.
- [ ] **Step 2:** Implement routes and acceptance proofs. Forbidden media contribute active normals through `MovementWaterBoundary` and `MovementConstraintProjection`, with retrace. No new step search.
- [ ] **Step 3:** Run the new tests, `MovementCapsuleResolverTests`, and `FullyQualifiedName~Locomotion.Contacts`, expect PASS.
- [ ] **Step 4:** Commit `feat(locomotion): prove every accepted contact segment against the medium`.

### Task 4: The whole land tick through StepLeased

**Files:**
- Create: `KhaozEngine.Locomotion/Contacts/ContactController.Leased.cs` (and a shared tick composition file if needed)
- Modify: `ContactController.cs` (factor prelude, mode runner and finalization), `ExplicitCharacterMovement.cs`, `.Steps.cs`, `.Queries.cs`
- Test: B1, B2 by full method name, new `ExplicitContactLandTests.cs`

**Interfaces:**
- Consumes: Tasks 2 and 3.
- Produces: `internal static MovementStepResult StepLeased(in FramedMovementState state, Vector2 moveDir, float speedFraction, bool run, bool jump, float dt, in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries, MovementBoundary? boundary = null, float? faceYaw = null, GroundCoreSettings? settings = null)` (4b-2). Explicit land ticks call it. The old `Settle` and `TryStepUp` land motion is removed.

- [ ] **Step 1:** Write the dry parity rows through both entries (flat, steps, stairs, ramps, ledge to air, landing, coyote and buffered jump, air momentum, commitments), L1 to L3, S1, S2, failing first where new.
- [ ] **Step 2:** Implement the shared tick and the thin explicit land entry.
- [ ] **Step 3:** Run B1 and B2 by full name, the new tests, `FullyQualifiedName~Locomotion.Contacts` and the explicit ground and commitment fixtures, expect PASS. B1 is the phase's headline: it must now pass unchanged.
- [ ] **Step 4:** Commit `feat(locomotion): run explicit land ticks on the contact controller`.

### Task 5: Swimming ownership and within-tick continuation

**Files:**
- Modify: `ExplicitCharacterMovement.Fluid.cs` (handlers), `Contacts/ContactController.Leased.cs` (continuation protocol), `GroundCore`, `SlideCore`, `AirPass` (accepted-substep medium handoff result)
- Test: new `ExplicitContactWaterHandoffTests.cs`, explicit swimming, surface jump and commitment fixtures

**Interfaces:**
- Produces: internal `ContactTickContinuation` (4b-6 fields) and the mode table of 4b-5.

- [ ] **Step 1:** Write H1 to H4, the one gravity, time and climb budget rows, guard exhaustion and landing before a buffered jump, failing first.
- [ ] **Step 2:** Implement the handlers and continuation. `SurfaceWaterMotion` keeps its dynamics and launch speed.
- [ ] **Step 3:** Run the new tests and the swimming fixtures, expect PASS.
- [ ] **Step 4:** Commit `feat(locomotion): hand explicit ticks between land and water within one tick`.

### Task 6: Placement and continuation doors

**Files:**
- Modify: `ExplicitCharacterMovement.Placement.cs`, `.Continuation.cs`, `MovementSupportResolver.cs`
- Test: contact placement, cold and correction, and cell-continuation fixtures

- [ ] **Step 1:** Write A1, Q3, Q4, below-knee placement, forbidden placement, retained land buffer and no-prefix rows, failing first.
- [ ] **Step 2:** Route `ValidatePlacement`, `SettlePlacement`, `ReclassifyContinuation` and the public `Select` door's land placement through the shared contact placement (4b-9). Keep `Select`'s candidate guards. No zero-dt tick.
- [ ] **Step 3:** Run the fixtures, B1 and B2, expect PASS.
- [ ] **Step 4:** Commit `feat(locomotion): place and continue explicit bodies through the contact controller`.

### Task 7: Deep-water acceptance on the integrated core

**Files:**
- Test: new `ExplicitContactDeepWaterTests.cs`, `ExplicitCharacterMovementTests.NonSwimmer`, default legacy swim fixtures
- Modify: only a medium-constraint repair the rows prove necessary

- [ ] **Step 1:** Write W1 to W7 at both body heights, failing first where the integrated path differs.
- [ ] **Step 2:** Repair only what the rows require, inside the medium seam (4b-15, 4b-16). No `MoveTuning` flag, no nav bake.
- [ ] **Step 3:** Run the new tests and the non-swimmer fixtures, expect PASS.
- [ ] **Step 4:** Commit `feat(locomotion): enforce the deep-water line for explicit walkers`.

### Task 8: Profile identity, heads and integration

**Files:**
- Modify: `KhaozEngine.Movement/ExplicitMovementProfile.cs` (identity `contact-shell-footprint-swim-v1` with footprint fraction, skin and recipe versions), the spec status, `docs/INDEX.md`, the program phases table, `CHANGELOG.md` (one line in the staged version)
- Test: I1, R1, Movement explicit-profile tests, Server explicit movement, admission and replication tests

- [ ] **Step 1:** Write I1 and R1, failing first.
- [ ] **Step 2:** Change the profile identity and inputs only after Tasks 1 to 7 pass.
- [ ] **Step 3:** Full verification once, heavy lane: Release build, full suite `--filter "Category!=LiveSocket"`, format, the five guards and `bash scripts/check-doc-versions.sh`. Record totals in `docs/verification/2026-10-11-p4b-final.json`.
- [ ] **Step 4:** Commit `docs(release): record contact controller phase 4b`.
