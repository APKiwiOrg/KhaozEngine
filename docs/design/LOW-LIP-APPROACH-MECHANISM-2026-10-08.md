# Low-lip resting approach mechanism

The approved support fallback seats every non-flat Hold row and passes the 39 Game low-prop, wall,
dome, slope and stair controls. One directed proof still fails. From the clear bank column at x -0.375,
`TryEdge` cannot reach the resting bank anchor at x -0.125 within the 1 mm arrival ball. It converges
to a fixed point 6.75 mm short and spends the remaining budget there.

## Measurement

`LowLipTraversalTests.RestingBankApproachStallsOnTheMovingCornerDepenetration` replays the unchanged
`TryEdge` call order. Its 64 step outputs match the predicate inputs bit for bit. At the fixed point a
fresh grounded state and the actual carried state produce the identical next position. Carried velocity,
climb signal and grounded state therefore do not contribute. A shadow of one core step through the same
public world queries reproduces the core output within 1.5e-8 m. Evidence is in
`docs/verification/2026-10-08-low-lip-approach-mechanism.json`.

Each tick at the fixed point:

1. `SlideSubstep` depenetrates the skin-inflated capsule before sweeping. The body is moving, so
   `restHold` is false and the walkable corner MTV is kept whole. It moves the body 4.39 mm away from
   the lip edge.
2. The sweep meets the walkable corner. The pass-through advances the remainder less one skin along the
   sweep direction. That direction is mostly gravity, 27.8 mm per tick at 25 m/s^2 and 30 Hz, so only
   `d(1 - skin/|delta|)` of the horizontal command survives.
3. The 8.1 mm residual overlap is inside the settle slop. Support then lifts the body vertically onto
   the corner.

The horizontal pushback is `skin * offset / radius` and the gain is `d(1 - skin/|delta|)`. They balance
at `d = 6.75 mm`. The command slows toward the target, so every approach reaches this point and no
step budget can cross it. From the deck side the same normal points along travel, which is why the
other three directed edges pass.

Support, footprint, step budget, pacing and carried state were each measured and excluded. The
remaining cause is moving collision response, which the approved support and anchor scope excludes.
The spec requires a separate owner decision for it.

## Repair options

| Criterion | A. Seated low-band vertical push | B. Feature-certified vertical push | C. No collision change |
| --- | ---: | ---: | ---: |
| Removes the measured pushback | 9 | 9 | 1 |
| Limits regression exposure | 6 | 8 | 10 |
| Bounded implementation | 9 | 4 | 10 |
| Fits existing movement rules | 8 | 7 | 6 |
| Total | 32 | 28 | 27 |

A applies the existing rest rule to one more state. In `SlideSubstep`'s pre-sweep depenetration only,
a grounded body whose tick-start feet sit above the analytic terrain but within `OnPropSkin` takes the
vertical part of a walkable MTV. That is the state `LowPropSupport` already owns. Settle, steep contacts,
terrain-level first contact and elevated stair runs stay unchanged. The #1265 change verticalized every
grounded body and broke seven stair cases. A excludes that elevated regime. The tradeoff is that A
changes collision response for every low walkable prop contact in that band, so the full controls and a
fresh review must run again.

B adds a certified-feature requirement to A. `ComputePenetration` returns no body, so B needs an extra
sweep and feature query under a lease in each depenetration iteration. It narrows exposure at a real
cost in complexity and per-tick queries.

C leaves the forward bank-to-deck edge unproved. The bridge route in Grimhollow #416 needs that
direction, so C does not meet the goal. Arrival tolerance, step budget, pace and forced goals stay
excluded by the owner's boundaries.

Recommendation: A, followed by the Task 6 controls, the seven-stair slice and the discriminator converted
to a passing approach control.
