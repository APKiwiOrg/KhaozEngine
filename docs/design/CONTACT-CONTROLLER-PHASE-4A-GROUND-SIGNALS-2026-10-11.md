# Contact controller phase 4a: ground signals

Date: 2026-10-11. Detailed spec for the first half of phase 4 of
[#438](https://github.com/APKiwiOrg/KhaozEngine/issues/438). The program, body model, support rule and invariants are
in [CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md](CONTACT-CLASSIFICATION-CONTROLLER-2026-10-08.md). Phase 3 (air
and state) is on main at `de9d1ef19`. Status: implemented, staged for 20.31.1. Written under the owner's
2026-10-10 instruction to continue the program to completion, with every decision recorded as a ruling for review,
including the rulings made during implementation.

## Scope

Phase 4 is split in two. Phase 4a (this spec) fills the three ground signals the controller leaves at zero:
`ClimbRate`, `ClimbRateEwma` and `StepDeltaY`. Phase 4b integrates `feature/swimming-contract`, moves swimming's
explicit grounded ticks onto the ground core and carries the #1299 deep-water rule. It gets its own spec.

`SupportGranted` and `FacingYaw` already follow the legacy contracts since phase 3. Phase 4a only adds suite rows
that pin them (see Suite).

Out of scope: water (still `NotSupportedException` until 4b), the `MoveTuning` selector, wire, navigation and bake
identity (phase 5). No wire field is added. `ClimbRate` is carried state that already rides the wire as
`ClimbRateQ`, and every head now supplies it to the step decoded from that value. The one public addition is
`MoveState.ClimbRateQuantum`, the wire's resolution, which the codec now reads from Locomotion.

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

Each plane is the support's own plane only where its height field follows it. A certified height is the least of
the support's plane at the axis, its witness height and its joined polygons' planes. When the height lies below the
plane through the witness with the support's normal, evaluated at the support's axis, by more than the contact skin
plus the support's certified error, the height came from the witness: an edge or vertex the footprint overhangs,
such as the top edge of a ramp. The height field there is level, so the classifier extends that support as a level
plane through its height. Analytic terrain witnesses lie on the axis and never take this rule.

**Ruling 4a-9.** An overhung edge is level ground for the classifier. The final review showed that past a sloped
face's top edge the support keeps the face's tilted normal over a level edge height, so both residuals picked up the
face's slope with the same sign and reported a step against the feet's motion (-0.025 m at 14 degrees, -0.058 m at
30). Cost if wrong: none.

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
  `ClimbRate` at least half the wire quantum) and the lag exceeds the contact skin plus the support's certified error. Otherwise carried lag is
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

**Trigger.** Signals are computed at the end of a tick whose final mode is ground with walkable footing. The
look-ahead runs only when the tick's achieved horizontal ground travel is at least the contact skin, which gives it
a direction, and when at least one of these holds:

- the tick applied a nonzero discrete rise or drop
- the input state's `ClimbRate` shows a run in progress, its magnitude at least half the wire quantum
- the input state has a positive `ClimbRateEwma`

Bodies on terrain or ramps never trigger it, so its cost falls only on stairs. Travel below the contact skin is the
body model's contact jitter, for example a body pressed into a wall, so it counts as no travel. A tick without
travel has an empty window: it cannot start a run, but its applied steps still count and a run in progress still
continues on them. A paced climb pays its lag in ticks whose horizontal travel is zero, so these ticks keep their
signals.

**Samples.** Sample 0 is the tick's final certified support at the axis, passed in, so it costs no query. Along
the unit direction of the tick's achieved horizontal travel, at distances `k * CapsuleRadius` for k = 1, 2, 3, the
controller calls `FootSupport.Find` with the tick's foot radius, the final feet height as the reference,
`ReachUp = ReachDown = StepHeight + 2 * k * CapsuleRadius` and the tick's traction gate. Only walkable samples
count. The grade is the least-squares slope of height against distance over the walkable samples, and it needs
sample 0 plus at least two ahead. The window holds a rise (or a drop) when the discrete step of Discrete steps,
taken between consecutive walkable samples, is positive (or negative) for some pair.

**Run.** A run starts on a grade whose magnitude is at least 0.3, the legacy run grade, and only when the window
holds a step of the same sign. A smooth ramp has no discrete step between its samples, so it never starts a run
however steep it is. A run in progress continues without the grade test:

- an ascent (input `ClimbRate` at least half the wire quantum) continues while the tick applied a discrete rise, the window holds a rise, or
  the feet still owe climb below their walkable support by more than the contact skin
- a descent (input `ClimbRate` at most minus half the wire quantum) continues while the tick applied a discrete drop or the window holds a drop

Near the top or bottom of a staircase, the window sees the landing and its fitted grade flattens while the last
risers are still being climbed or dropped. Continuation carries the run through them and ends it once the steps
are done. The constants stay named internal constants beside the others.

**Ruling 4a-10.** Every head decides on the replicated climb rate. The final review showed that the sharded head
rebuilt its step input without `ClimbRate`, so runs never continued there and the two server heads exported
different `ClimbRateQ` for the same walk, while the client's reconcile basis decoded it from the wire. The sharded
head now seeds `ClimbRate` from `ClimbRateQ` as `PlayerMoveState.From` does, and the controller treats a carried
rate as a run in progress only when its magnitude is at least `MoveState.ClimbRateQuantum / 2`, so the single-World
head's full-precision rate decides as the decoded one. `StepDeltaY` is not replicated, so the step-in-progress test
from `StepDeltaY` only shapes the unreplicated `StepDeltaY` itself, and continuation stays robust through the
geometric owed-climb test. Cost if wrong: run tails under 0.025 m/s end one tick sooner on every head.

**Ruling 4a-8.** Travel below the contact skin is no travel, and no travel only empties the look-ahead window. It does
not silence the signals. The Task 3 implementer showed that paced climbs pay lag in ticks with exactly zero
horizontal travel, that a stationary body paying a step's lag must still report it, and that a body pressed into
a wall still reports about a micrometre of travel per tick. This replaces the earlier ruling that zero travel
zeroes the signals. Cost if wrong: a walker slower than the skin per tick (0.03 m/s at 30 Hz) never starts a run.

**Ruling 4a-7.** Runs need a discrete step, and runs in progress continue on step evidence rather than on the
fitted grade. The Task 3 implementer showed that a grade-only test turns a steep ramp into a run whenever a rate is
carried in, and that a three-sample horizon drops the run on the last risers. Cost if wrong: a staircase whose
risers are more than three capsule radii apart reads as isolated steps.

**Ruling 4a-6.** Carried lag counts as an owed step only while the input state shows a step in progress. The
controller carries no new state, and a reviewer probe showed that lag left by pacing a smooth hill otherwise surfaced
as a step on the next tick. A replay from a server state whose `StepDeltaY` was not replicated may classify one tick
of lag as continuous, which only changes presentation, and client prediction never adds replayed events. Cost if
wrong: a paced riser whose first tick paid nothing (its budget spent earlier in the tick) reports nothing.

**Ruling 4a-2.** Look-ahead uses the certified support primitive, gated by the trigger, rather than an uncertified
ray fan every tick. The signal stays on the engine's single support truth, and the cost stays off the common
path. Cost if wrong: three extra support queries per tick on stairs, measured in the #1334 cost gate.

**Ruling 4a-3.** The first riser of a staircase emits `ClimbRate`, not `StepDeltaY`, because the trigger fires on
that riser and finds the run ahead. Legacy emitted the first riser as `StepDeltaY` because its run test also
required the body to stand on a prop. The stair glide animator composes either way. Cost if wrong: none.

## Rates

- **Ascent** (an ascent run): the applied rate is the tick's summed `StepRise` plus plane following
  on the ground segments, divided by the whole tick `dt`. `ClimbRateEwma` follows the legacy formula: seed
  `0.7 * rate` when the carried value is exactly zero, otherwise `previous + (1 - exp(-5 * dt)) * (rate -
  previous)`. `ClimbRate = max(0, ClimbRateEwma)`.
- **Descent** (a descent run): `ClimbRate = -min(achieved / dt * clamp(-grade, 0.3, 2), 6)`, where `achieved` is
  the length of the tick's achieved horizontal ground travel (zero below the contact skin) and `grade` is the
  fitted grade, or -0.3 when fewer than two samples ahead are walkable. `ClimbRateEwma` is zero.
- **Otherwise** both are zero.

**Ruling 4a-4.** Descent uses achieved travel, not commanded travel, so a wall or bound that stops the body cannot
export a descent the feet did not make. Cost if wrong: none, the contract only promises a continuous descent rate.

When `ClimbRate` is nonzero, `StepDeltaY` is zero. Otherwise `StepDeltaY` is the tick's summed `StepRise`.

## Ticks that emit nothing

All three signals are zero, and the EWMA resets, on a tick that ends airborne, sliding, wedged, held on refused
support, swimming (4b) or through the non-finite fallback, and on a tick whose end the play-area backstop shifts. The
backstop is a correction, not motion, and its placement keeps no footing the signals could read. A jump decided at the end of a grounded tick does not
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
- The signal computation allocates nothing on a warm tick. The whole tick already allocates the backend's read lease
  object (208 bytes measured), which belongs to the #1334 cost gate, not to this phase.

## Suite

New rows under `KhaozEngine.Game.Tests/Locomotion/Contacts/`. Expected values come from authored geometry, not
legacy numbers. Box and mesh variants where statics are involved, 30 and 60 Hz, walk and run.

- **Classifier.** A riser between level treads gives the riser height. A convex and a concave analytic curve give
  zero. A planar ramp at 30 degrees gives exactly zero. A drop gives the negative height. A step onto a 10 degree
  tread gives the riser height within the curvature disagreement.
- **Isolated step.** A doorstep up and down: `ClimbRate` zero every tick, `StepDeltaY` sums to the step height
  within one skin, on at most `ceil(height / (MaxStepClimbSpeed * dt)) + 1` event ticks.
- **Stair ascent.** On a 16-step staircase, `ClimbRate` positive from the first riser's tick to the top, pacing
  pauses included, never negative, `StepDeltaY` zero throughout, never above `MaxStepClimbSpeed`, and converged:
  over the second half of the climb, after the EWMA's warm-up, the mean rate is within 0.25 m/s of the measured rise
  rate over the same ticks. The mean over the whole climb includes the 0.7 seed's warm-up, which on a short
  staircase alone exceeds 0.25 m/s, so it is not the convergence test.
- **Stair descent.** `ClimbRate` negative and at least -6 through the run, `StepDeltaY` zero throughout.
- **Smooth ground.** Flat ground, a static ramp, a planar analytic slope and a curved analytic hill: all three
  signals exactly zero every tick.
- **Air and slides.** A jump arc over stairs, a fall onto a step, a slide on a steep face: all zero, the EWMA
  zero.
- **Denied descent.** A body pressing into a wall at the top of a descending stair exports no descent.
- **Held and fallback.** A held tick and the non-finite fallback export zeros.
- **Backstop.** A body standing on a riser with owed climb and a step or a run in progress, pushed across the bound
  by recovery from a wall, pays its climb and is shifted back by the backstop, and every such tick exports zeros.
- **Pacing off.** With `MaxStepClimbSpeed` zero, the doorstep still reports its height in one tick.
- **Grant and facing.** The Held-on-refused-support policy keeps the grant (pinned as built), and a commitment
  freezes facing (already pinned, kept).
- **Replay and allocation.** Replay bits include the three floats. A replay whose basis carries the live rate
  through the wire quantum is bit-identical to the live run, including a rate under half a quantum on the last
  tread. Both server heads hand the step the same carried `ClimbRate` for the same wire state over a stair climb. A warm `GroundSignals.Resolve` call with
  look-ahead allocates nothing.
- **Slope edges.** Slab ramps at 14 and 30 degrees, walked up over the top edge onto a floor 0.1 m below it and
  down off the low end onto a floor 0.1 m below it. Uphill, `StepDeltaY` sums to -0.1 within one skin with no
  event whose sign differs from that tick's feet change. Downhill, every event's sign matches the feet change, no
  event is positive and the sum is at most 0.1 plus one skin in magnitude. A classifier row pins the overhung edge.
- **Steep ramps.** A 20 degree ramp and a planar analytic slope with a carried `ClimbRate` and EWMA report zero.
- **Scenario harness.** Every scenario tick asserts finite signals and never both `ClimbRate` and `StepDeltaY`
  nonzero.

## Files

- `GroundSeat.cs`: the backward residual, the overhung edge's level plane and `d` beside `StepPart`.
- `GroundCore.cs`: owed discrete rise and `GroundStepResult.StepRise`.
- New `GroundSignals.cs`: trigger, look-ahead, grade fit and rates, one responsibility.
- `ContactController.cs`: sums `StepRise`, calls `GroundSignals`, zeroes signals on the listed ticks, finite check.
- Tests: a new `ContactSignalTests` partial set, and the scenario harness assertion.

## Known limits

- Run detection reads three samples ahead, so a staircase shorter than about three capsule radii may not register
  as a run and emits isolated steps instead.
- A staircase whose steps are farther apart than the look-ahead (three capsule radii) reads as isolated steps.
- A riser on a strongly curved tread reports its height less the curvature disagreement (Ruling 4a-1).
- A drop off a sloped face's low end can read short, or as a kink with no event, by at most one substep's travel
  times the slope. The jump at an unknown switch point between two samples lies between `rb` and `rf`, so when they
  disagree in sign the samples prove no jump, and the classifier reports the smallest jump the samples prove.
  Locating the switch through the support neighbourhood's joins is
  [#1354](https://github.com/APKiwiOrg/KhaozEngine/issues/1354). The cost is a few centimetres of unsmoothed drop at
  ramp feet, presentation only.
