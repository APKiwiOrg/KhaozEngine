# Combat contact spacing: evidence and owner decision

Status: characterization and proposal for review. No product behavior changes or selected implementation.
The headless characterization passed all four cases in Release. Its geometry is derived from the existing
reachable pursuit trace and measured through the real simulator and presenter.

Consumer: [Grimhollow #371](https://github.com/APKiwiOrg/Grimhollow/issues/371). The owner chose KhaozEngine
for the shared capability. That choice does not settle the contact invariant or amend the movement rules.

## The contract that needs a decision

Grimhollow's contact probe asks for displayed body separation within 80 mm of 1.000 m at the confirmed impact,
and an armed attack's blade middle within 80 mm of the shown target's contact point. This is stricter than
committed adjacency. The server still resolves legal reach from committed tiles.

Section 9 of the [preparation design](TILE-COMBAT-PREPARATION-DESIGN-2026-09-28.md#9-grimhollow-adoption-contract)
requires a separate reviewed design for remaining spacing corrections. It prohibits stretching reach,
teleporting the body or retiming movement to make an image pass. Section 7 keeps the confirmed impact and its
feedback together on the result's receipt frame. The [movement ruling](TILE-WORLD-NETCODE-DESIGN-2026-08-22.md#52-the-invariant-and-the-four-rounds-it-took-to-settle-the-drawn-body-1810)
keeps the full-step linear glide. This proposal changes none of those contracts.

## Two distinct sources of separation

The existing Grimhollow measurements on engine 20.15.1 show a moving-away target 0.400 m from its attacker,
a walking attacker 1.350 m from its target on one observer, and pursuit blows 2.240 to 2.500 m apart despite
committed adjacency. See the consumer's
[adoption evidence](https://github.com/APKiwiOrg/Grimhollow/blob/main/docs/verification/COMBAT-PREPARATION-2026-09-28.md).
These are existing measurements, not new executions in this proposal.

The engine adds a timeline mismatch to the intrinsic movement geometry:

- `TileWorldClient.CombatPresentationTick` follows the newest applied server tick, with at most one tick of
  fractional advance. Prepared results dispatch during `Poll`.
- Remote movement samples use client arrival timestamps and a two-tick interpolation delay by default.
  `TilePresenter` carries the sampled step progress forward.
- The local body uses `ClientPrediction.RenderedState`: interpolation between prediction targets on its
  independent command phase, plus a continuity-preserving reconciliation offset.

Zero remote interpolation delay removes the explicit delay term. It does not remove local interpolation,
reconciliation offsets, transport age or the bodies' different progress through their own steps. An instant
per-participant switch from delayed to current samples also advances its displayed source time, so continuity
must be measured rather than assumed.

## Equal-time geometry already exceeds the contact envelope

`TileCombatContactGeometryTests` reproduces the existing `TileCombatTargetTests` pursuit through the real
`TileMoveSimulator`, a target snapshot taken before either body steps, `TileReach` and `TilePresenter`.
There is no client prediction, transport, interpolation delay or preparation scheduler in this test.
It characterizes movement geometry, not an actual prepared impact.

The walk and run traces reach the following states at tick 4 and tick 2 respectively. The tick count starts
with the first command at tick 0. Both bodies have X 20 and plane 0:

| Body | StepFrom Z | Committed Z | StepTicks | StepTotal |
| --- | --- | --- | --- | --- |
| Attacker | 21 | 22 | 0 | 4 walking, 2 running |
| Target | 22 | 23 | 1 | 4 walking, 2 running |

`TileReach` accepts the committed tiles. With the same fractional carry `f` supplied to both poses, their
uncentred planar positions are `21 + f/N` and `22 + (1 + f)/N`. The centre offsets cancel in the distance.
The gap is therefore `1 + 1/N`: **1.250 m walking and 1.500 m running**, above the consumer's 1.080 m upper
bound. The test checks both `f = 0` and `f = 0.5`, so the conclusion is not confined to an integer tick.

The stopped-target control follows the same initial pursuit to a short goal, lets both bodies land, and
checks legal adjacency with a 1.000 m displayed gap. Test output reports the captured step states and poses.
The test asserts the existing discrepancy. Its passing result characterizes it and does not fix or close #371.

```sh
dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TileCombatContactGeometryTests --logger 'console;verbosity=detailed'
```

## Candidate directions and their limits

| Candidate | What it can establish | Decision or limitation |
| --- | --- | --- |
| Reduce delay for combat participants | Remove some extra remote lag while retaining receipt-frame feedback | Does not guarantee fixed contact. Needs phase-offset and continuity measurements before selecting a delay or transition policy |
| Sample all combat participants on one authoritative presentation timeline | Separate timeline disagreement from intrinsic movement geometry | Must decide how the local predicted body joins that timeline without a pop. A delayed timeline would also change the receipt-frame feedback contract if effects followed it |
| Require fixed one-metre visual contact for every legal result | Preserve the consumer's current contact envelope as the acceptance condition | Equal-time full-step pursuit alone cannot satisfy it. The owner must explicitly choose which current movement, contact or feedback contract may change before an approach can be selected |

No candidate here silently increases server reach, snaps a body or shortens its glide. No new API is selected.
A common clock can improve coherence, but does not by itself provide the fixed-distance contact guarantee.

The owner-facing choice is between preserving fixed visual contact with a reviewed smooth combat presentation
adjustment, or preserving the current glides and accepting their intrinsic separation while bounding added lag.
The first direction may change the displayed path, but must leave server reach and the confirmed feedback beat
unchanged. It still needs a concrete continuity and gait design before implementation.

The scores below are engineering judgment about these directions, not measured guarantees. Contact fidelity has
weight 3 because it is the reported defect. Each other criterion has weight 1. Scores are out of 10.

| Direction | Contact fidelity x3 | Preserve current glides | Preserve authority and feedback | Maintainability | Weighted total |
| --- | --- | --- | --- | --- | --- |
| Fixed contact with smooth combat presentation adjustments | 10 | 4 | 10 | 7 | 51 / 60 |
| Preserve glides and correct only added timeline disagreement | 6 | 10 | 10 | 8 | 46 / 60 |

Recommend the fixed-contact direction if the owner approves amending the displayed glide during combat.
Reducing added lag alone remains useful, but cannot close the present contact acceptance condition.

## Owner decision and the next evidence

First choose the acceptable contact invariant: fixed one-metre visual contact for every legal impact, or
contact evaluated against the existing continuous step geometry with a separately bounded timeline mismatch.
If fixed contact is required, identify the existing contract that may change. Merely choosing the engine as
owner does not authorize that amendment.

Before selecting a timeline policy, compare remote delays 0 and 2 through a headless client/server probe with
independent client phases, at 60 Hz and 50 Hz, and one deterministic delayed or burst delivery case. Include
stationary run-up, a moving-away target, a remote walking attacker and a remote chaser of the local runner.
Measure after `AdvancePresentation`, preserving the result's receipt frame. Record the result tick's saved
authoritative states separately from current server states, latest and delayed remote states, step progress,
local prediction and correction, displayed gap and per-frame displacement. Confirm once-only feedback and
unchanged authoritative outcomes alongside spacing. Existing server-only pursuit tests cannot prove this.

This is evidence and an open proposal. Implementation planning follows the reviewed contact decision and
those measurements.
