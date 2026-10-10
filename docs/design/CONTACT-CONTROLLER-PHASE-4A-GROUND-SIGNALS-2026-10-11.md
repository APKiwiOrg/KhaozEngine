# Contact controller phase 4a: ground signals

Date: 2026-10-11. Detailed spec for the first half of phase 4 of
[#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438). The program, body model, support rule and invariants are
in [CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md](CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md). Phase 3 (air
and state) is on main at `de9d1ef19`. Status: written under the owner's 2026-10-10 instruction to continue the
program to completion, with every decision recorded as a ruling for review.

## Scope

Phase 4 is split in two. Phase 4a (this spec) fills the three ground signals the controller leaves at zero:
`ClimbRate`, `ClimbRateEwma` and `StepDeltaY`. Phase 4b integrates `feature/swimming-contract`, moves swimming's
explicit grounded ticks onto the ground core and carries the #1299 deep-water rule. It gets its own spec.

`SupportGranted` and `FacingYaw` already follow the legacy contracts since phase 3. Phase 4a only adds suite rows
that pin them (see Suite).

Out of scope: water (still `NotSupportedException` until 4b), the `MoveTuning` selector, wire, navigation and bake
identity (phase 5). No public API, wire field or carried state is added.

## Contracts kept

The public meanings stay as `MoveState` documents them, because the wire codec, client prediction, the stair glide
animator and the step offset smoother read them.

- `ClimbRate`, signed m/s. Positive is a continuous stair ascent, negative a continuous stair descent, exact zero
  otherwise. Descent is never below -6 m/s. Replicated as `ClimbRateQ`.
- `ClimbRateEwma`, m/s, sim-local ascent smoothing. Reset to zero on any tick that is not an ascent run. Seeded from
  a positive decoded `ClimbRate` by the existing reconciliation and shard-handoff code, which stays unchanged.
- `StepDeltaY`, signed metres, a per-tick event for an isolated step. Zero on any tick with a nonzero `ClimbRate`.
  Never replicated. Client prediction adds it exactly once per predicted tick.

## Discrete steps

A step is a jump in support height that the supports on both sides agree on.

For one accepted ground substep from a walkable start support S0 (height h0, normal n0, axis a0) to a walkable new
support S1 (height h1, normal n1, axis a1):

- the forward residual `rf = h1 - plane(S0 at a1)` is the existing `StepPart`
- the backward residual `rb = plane(S1 at a0) - h0`, the new plane carried back to the start axis

On a riser between two treads both residuals equal the riser height. On a continuous curved surface they have
opposite signs, because each plane misses the other point on its own side of the curve. The discrete step is
therefore:

- `d = sign(rf) * min(|rf|, |rb|)` when `rf` and `rb` have the same sign
- `d = 0` when the signs differ, or when the smaller magnitude is not above `ShellMotion.ContactSkin` plus the two
  supports' certified height errors

The contact skin (1 mm) is the body model's own resolution, the same distance as the arrival rule and the contact
gap, so a support jump finer than it is ground the body cannot tell from continuous. It is not a tuned threshold.
A planar ramp gives `d = 0`, because analytic terrain certifies no height error and leaves float residuals of a few
nanometres. A smooth curve gives `d = 0` even across an inflection, where both residuals share a sign but are
cubic in the substep length (about 15 micrometres on the suite's hill). A step onto a sloped tread gives the riser
height less the curvature disagreement, which is the part a viewer reads as a step.

A start or new support that is not walkable (steep, none, refused) gives `d = 0`. Steep supports belong to slides,
which never emit steps.

**Ruling 4a-1.** Steps are classified by plane agreement above the contact skin, not by the legacy
`GroundedEpsilon` dead zone or by `StepPart` alone. `StepPart` alone reports millimetre events on every tick over
curved terrain, and the step offset smoother freezes the drawn mesh on each event, which would stutter on
Grimhollow's hills. The skin floor was added in implementation, after the Task 1 implementer showed float residuals
on analytic planes and same-sign residuals across smooth inflections. Cost if wrong: steps and seams under 1 mm
emit nothing, and a riser on a strongly curved tread reports a few millimetres less than its height.

## Applied step rise

Pacing pays rises from the climb budget, so the feet can lag below a tread for a tick or more. The signal reports
the discrete rise that actually happened this tick.

- The tick keeps an owed discrete rise `O`. At the start of a ground segment, `O` is the lag the feet already carry
  below walkable support, but only when the input state shows a step in progress (`StepDeltaY > 0` or
  `ClimbRate > 0`) and the lag exceeds the contact skin plus the support's certified error. Otherwise carried lag is
  continuous: pacing also defers curvature residuals, and without a step in progress the lag cannot be a riser.
- Each accepted substep (and each `PayLag` payment) accounts as one move. With `H = O + d` before the move and
  `L` the lag below the new walkable support after it, the owed rise becomes `O' = clamp(L, 0, max(H, 0))` and the
  applied discrete rise is `H - O'`. A rise settles `O` as the budget pays it. A drop erases only the owed rise its
  new support no longer stands above, so a partial drop keeps the rest owed, and the applied amount always equals the
  discrete part of the feet's actual change.
- Plane following and curvature residuals are never discrete, even when the budget pays them, because they never
  enter `H`.

`StepRise` (new on `GroundStepResult`) is the tick's applied discrete rise for that ground segment. The controller
sums `StepRise` over the tick's ground segments. Air segments, landings, slides, recovery pushes and the play-area
backstop contribute nothing.

## Runs

`ClimbRate` reports a continuous run of steps. It needs to know what lies ahead, so the controller samples the
support ahead of the axis, as legacy samples its grade fan.

**Trigger.** The look-ahead runs only at the end of a tick whose final mode is ground with walkable footing and
nonzero achieved horizontal travel, and when at least one of these holds:

- the tick applied a nonzero discrete rise or drop
- the input state has a nonzero `ClimbRate` (a run in progress)
- the input state has a positive `ClimbRateEwma`

Bodies on terrain or ramps never trigger it, so its cost falls only on stairs.

**Samples.** Along the unit direction of the tick's achieved horizontal travel, at distances `k * CapsuleRadius`
for k = 1, 2, 3, the controller calls `FootSupport.Find` with the tick's foot radius, the final feet height as the
reference, `ReachUp = ReachDown = StepHeight + 2 * k * CapsuleRadius` and the tick's traction gate. The axis
support is sample 0. Only walkable samples count. The grade is the least-squares slope of height against distance
over the walkable samples, and it needs sample 0 plus at least two ahead.

**Run.** A run is a grade whose magnitude is at least 0.3, the legacy run grade. The constant stays a named
internal constant beside the others.

**Ruling 4a-6.** Carried lag counts as an owed step only while the input state shows a step in progress. The
controller carries no new state, and a reviewer probe showed that lag left by pacing a smooth hill otherwise surfaced
as a step on the next tick. A replay from a server state whose `StepDeltaY` was not replicated may classify one tick
of lag as continuous, which only changes presentation, and client prediction never adds replayed events. Cost if
wrong: a paced riser whose first tick paid nothing (its budget spent earlier in the tick) reports nothing.

**Ruling 4a-2.** Look-ahead uses the certified support primitive, gated by the trigger, rather than an uncertified
ray fan every tick. The signal stays on the engine's single support truth, and the cost stays off the common
path. Cost if wrong: up to three extra support queries per tick on stairs, measured in the #1334 cost gate.

**Ruling 4a-3.** The first riser of a staircase emits `ClimbRate`, not `StepDeltaY`, because the trigger fires on
that riser and finds the run ahead. Legacy emitted the first riser as `StepDeltaY` because its run test also
required the body to stand on a prop. The stair glide animator composes either way. Cost if wrong: none.

## Rates

- **Ascent** (run with grade at least 0.3): the applied rate is the tick's summed `StepRise` plus plane following
  on the ground segments, divided by the whole tick `dt`. `ClimbRateEwma` follows the legacy formula: seed
  `0.7 * rate` when the carried value is exactly zero, otherwise `previous + (1 - exp(-5 * dt)) * (rate -
  previous)`. `ClimbRate = max(0, ClimbRateEwma)`.
- **Descent** (run with grade at most -0.3): `ClimbRate = -min(achieved / dt * clamp(-grade, 0.3, 2), 6)`, where
  `achieved` is the length of the tick's achieved horizontal ground travel. `ClimbRateEwma` is zero.
- **Otherwise** both are zero.

**Ruling 4a-4.** Descent uses achieved travel, not commanded travel, so a wall or bound that stops the body cannot
export a descent the feet did not make. Cost if wrong: none, the contract only promises a continuous descent rate.

When `ClimbRate` is nonzero, `StepDeltaY` is zero. Otherwise `StepDeltaY` is the tick's summed `StepRise`.

## Ticks that emit nothing

All three signals are zero, and the EWMA resets, on a tick that ends airborne, sliding, wedged, held on refused
support, swimming (4b) or through the non-finite fallback. A jump decided at the end of a grounded tick does not
clear the ground segment's signals, as in legacy, where jump consumption follows the signal stamp.

**Ruling 4a-5.** Signals do not depend on `MaxStepClimbSpeed`. Legacy computed its up event and run only inside
the pacing call, so disabling pacing silenced them. That was an implementation coupling, not a contract. With an
unlimited budget the discrete rise is applied in full on the substep that finds it. Cost if wrong: a game that
disables pacing now sees step events it did not see before.

## Invariants

- Every output is finite. The non-finite check covers the three floats.
- The signals never feed movement. Only the signal computation reads the input `ClimbRate`, `ClimbRateEwma` and
  `StepDeltaY`: the trigger, the EWMA update and the carried-lag test.
- Replay is bit-identical (invariant 1), signals included.
- A warm tick allocates nothing, look-ahead included.

## Suite

New rows under `KhaozEngine.Game.Tests/Locomotion/Contacts/`. Expected values come from authored geometry, not
legacy numbers. Box and mesh variants where statics are involved, 30 and 60 Hz, walk and run.

- **Classifier.** A riser between level treads gives the riser height. A convex and a concave analytic curve give
  zero. A planar ramp at 30 degrees gives exactly zero. A drop gives the negative height. A step onto a 10 degree
  tread gives the riser height within the curvature disagreement.
- **Isolated step.** A doorstep up and down: `ClimbRate` zero every tick, `StepDeltaY` sums to the step height
  within one skin, on at most `ceil(height / (MaxStepClimbSpeed * dt)) + 1` event ticks.
- **Stair ascent.** `ClimbRate` positive from the first riser's tick to the top, never negative, no zero tick
  between the first and last riser, `StepDeltaY` zero throughout, the mean rate within 0.25 m/s of the measured
  rise rate and never above `MaxStepClimbSpeed`.
- **Stair descent.** `ClimbRate` negative and at least -6 through the run, `StepDeltaY` zero throughout.
- **Smooth ground.** Flat ground, a static ramp, a planar analytic slope and a curved analytic hill: all three
  signals exactly zero every tick.
- **Air and slides.** A jump arc over stairs, a fall onto a step, a slide on a steep face: all zero, the EWMA
  zero.
- **Denied descent.** A body pressing into a wall at the top of a descending stair exports no descent.
- **Held and fallback.** A held tick and the non-finite fallback export zeros.
- **Pacing off.** With `MaxStepClimbSpeed` zero, the doorstep still reports its height in one tick.
- **Grant and facing.** The Held-on-refused-support policy keeps the grant (pinned as built), and a commitment
  freezes facing (already pinned, kept).
- **Replay and allocation.** Replay bits include the three floats. A warm stair tick allocates nothing.
- **Scenario harness.** Every scenario tick asserts finite signals and never both `ClimbRate` and `StepDeltaY`
  nonzero.

## Files

- `GroundSeat.cs`: the backward residual and `d` beside `StepPart`.
- `GroundPlacement.cs`: `Paced` reports what it paid.
- `GroundCore.cs`: owed discrete rise and `GroundStepResult.StepRise`.
- New `GroundSignals.cs`: trigger, look-ahead, grade fit and rates, one responsibility.
- `ContactController.cs`: sums `StepRise`, calls `GroundSignals`, zeroes signals on the listed ticks, finite check.
- Tests: a new `ContactSignalTests` partial set, and the scenario harness assertion.

## Known limits

- Run detection reads three samples ahead, so a staircase shorter than about three capsule radii may not register
  as a run and emits isolated steps instead.
- A staircase that turns into a ramp keeps `ClimbRate` while the grade ahead stays above 0.3.
- A riser on a strongly curved tread reports its height less the curvature disagreement (Ruling 4a-1).
