# Tile combat contact presentation

Status: written design for owner review. No implementation or implementation plan is approved.

Consumer: [Grimhollow #371](https://github.com/APKiwiOrg/Grimhollow/issues/371). The owner approved developing
a smooth presentation-adjustment design. The earlier [spacing evidence](COMBAT-CONTACT-SPACING-2026-10-01.md)
remains the characterization and proposal history. Its four headless cases establish the existing 1.250 m
walk and 1.500 m run pursuit geometry.

## 1. Proposed ruling and acceptance

Add opt-in, client-only correction of displayed combat bodies. Raw tile glide, prediction, camera, input,
committed tiles, server reach, schedules, wire data and outcome delivery keep their existing owners. The
corrected body pose is a separate read. Preparation gives it time to approach contact continuously. The
confirmed blow and all feedback still occur once, on the result's receipt frame.

This proposes an explicit exception for the combat body to section 9 of the
[preparation design](TILE-COMBAT-PREPARATION-DESIGN-2026-09-28.md#9-grimhollow-adoption-contract).
The underlying movement timeline is not retimed. The displayed body gains a bounded adjustment. Approval
of this written design would authorize that exception, not a server movement or reach change.

Release acceptance requires the current 17 Grimhollow contact cases and all three existing pursuit blows
to pass their body and authored-contact checks on the published 4/2 preparation. Retain the consumer's
80 mm envelope and the raw movement characterization. These are mandatory measurements before adoption,
not a claim that the proposed constants already pass them.

Exact contact for every possible arrival is impossible under bounded continuous movement and receipt-frame
feedback. Insufficient earlier information produces a named measured miss, never an impact snap or delayed
feedback. Ordinary pursuit's alternating committed reach is not a late-arrival exception.

## 2. Alternatives and selection

Scores are design assessments from 1 to 10, not measured results. Ordinary contact has weight 3 because it is
this defect. The other criteria have weight 1.

| Approach | Ordinary contact x3 | Reciprocal fights | Receipt feedback | Continuous bodies | Weighted total | Disposition |
| --- | --- | --- | --- | --- | --- | --- |
| Zero remote delay | 4 | 4 | 10 | 4 | 30 / 60 | Local smoothing and intrinsic pursuit geometry remain |
| Independent attacker-to-target corrections | 7 | 1 | 10 | 7 | 39 / 60 | Competing goals in reciprocal fights |
| Filter every latest committed component layout | 1 | 8 | 10 | 8 | 29 / 60 | Refuted by pursuit's one-tile, two-tile, one-tile goal jumps |
| Stable legal component layout advected by a raw anchor | 8 | 9 | 10 | 8 | 51 / 60 | Selected, subject to the acceptance gates and fallback limits |

Delaying all effects onto a historical movement timeline would change receipt-frame feedback and local
responsiveness. Stretching a weapon or reach, or snapping a body at impact, is outside this selection.

## 3. Ownership and public API

All proposed types belong to `KhaozEngine.TileWorld.Netcode`, without renderer or game references.
`TileWorldClient.ContactPresentation.cs` contains integration only. `TileCombatContactPresenter` owns
attempts, graph layouts, anchors, controllers and diagnostics. `TileCombatContactSourceFrames` owns source
history. `TileCombatContactMotion` owns pure bounded motion, collision checks and release paths.
`TileCombatContactPresentationTypes` owns these public types:

```csharp
public sealed record TileCombatContactPresentationSettings
{
    public float ResponseSeconds { get; init; } = 0.02f;
    public float ReleaseSeconds { get; init; } = 0.15f;
    public float MaxAdjustmentSpeedTilesPerSecond { get; init; } = 4f;
    public float MaxGoalOffsetTiles { get; init; } = 3f;
    public float ContactToleranceTiles { get; init; } = 0.04f;
    public byte TerminalHoldTicks { get; init; } = 2;
    public ushort MaxParticipants { get; init; } = 256;
}

// Added to TileWorldClientConfig. Null preserves existing behavior.
public TileCombatContactPresentationSettings? CombatContactPresentation { get; init; }

public readonly record struct TileCombatBodyPresentation(
    TilePose Pose, Vector3 Velocity, Vector3 BaseVelocity, Vector3 Correction,
    long SourceServerTick, bool Discontinuity, TileCombatContactLimits Limits);

public readonly record struct TileCombatContactImpact(
    ulong AttackId, uint Revision, long AttackerNetId, long TargetNetId,
    long ImpactTick, long GeometryServerTick, Vector3 DesiredRelativePosition,
    float PlanarErrorMetres, bool WithinTolerance, TileCombatContactLimits Limits);

// Added to TileWorldClient.
public bool TryGetCombatBodyPresentation(long netId,
    out TileCombatBodyPresentation presentation);
public IReadOnlyList<TileCombatContactImpact> CombatContactImpacts { get; }
public long CombatContactMissCount { get; }
public bool TryTransferCombatBodyPresentation(long previousNetId, long successorNetId);

[Flags]
public enum TileCombatContactLimits
{
    None = 0, MissingGeometry = 1, LatePreparation = 2, LateOutcome = 4,
    ChangedGeometry = 8, ConflictingLayout = 16, GoalDistance = 32,
    ParticipantCapacity = 64, Collision = 128, ReleasePath = 256,
    TargetUnavailable = 512, PresentationCut = 1024
}
```

Enabling requires `CombatPreparationEnabled`. Finite scalar settings must be positive, tolerance less than
the goal-offset limit, terminal hold at least one tick, and capacity at least two. Invalid settings fail
construction. Settings use tiles and seconds, converted with `Presenter.TileSize`. Public poses, velocities,
corrections and errors use world metres. The constants are proposed starting values for verification and
look review, not permission to replace a failed acceptance case with tuning alone.

The body read handles local and remote bodies. Disabled or unaffected bodies return their exact raw pose
with zero correction. Unknown, removed or unjoined bodies return false. Reads never advance motion or gait.
The impact list is immutable and belongs to the most recent presentation frame, with one diagnostic per
real result observed since the previous advance. It is not a new combat event or feedback trigger.
Disabled mode reports zero velocities without allocating contact histories. Enabled body velocities come
from the last completed presentation advance. A read inside `Poll` observes that previous completed body
frame, not a half-applied new frame. `SourceServerTick` names the latest accepted movement frame used to
validate that body's identity and size. Each edge separately retains its admitted geometry tick.

## 4. Source frames, identity and lifecycle

Capture the last 32 accepted movement snapshot ticks immediately after apply, before delayed interpolation
overwrites the world. Include local authoritative state and every visible body's tile, step origin/progress,
footprint size and epoch. Do not assemble remote routes. Older, equal or rejected snapshots cannot replace
an accepted frame. Source history is bounded by 32 frames and the existing snapshot entity bounds.

Attempt keys are `(connectionGeneration, attackerNetId, AttackId)`, with monotonically accepted revisions.
Store target, admitted geometry tick, endpoint epochs and sizes, and lifecycle. Body-controller keys are
`(connectionGeneration, netId, teleportEpoch)`. They retain offset, previous raw/body poses, stationary
history and release path across revisions, graph changes and successive attempts. Opposite attempts share
geometric ownership, not feedback identities.

Observe preparations and results before their external callbacks, but advance body motion only once after
`Poll` and raw presentation. Results enter a measurement queue without changing their delivery. Retain a
resolved edge for `TerminalHoldTicks` of the bounded combat clock, unless an endpoint cuts or disappears.
A same-target successor continues its layout. Cancellation has no terminal hold. Bodies with no remaining
ownership release continuously. Superseded outgoing local attempts are fenced when local prediction clears
or replaces their target. Incoming attacks against a local runner are not fenced.

Disconnect/dispose clear all state and fence pending callbacks by generation. Epoch, plane, local hard snap
or an existing remote presentation cut use the existing raw cut and reset affected contact state. Ordinary
late preparation and impact never create a cut. Removed bodies have no pose. Surviving neighbours release.

## 5. Stable legal footprint layout

Prefer source geometry from the attempt's `PrepareTick`, when it was scheduled legally. If that frame is
already available and legal, use it. A successor can be announced in Hold before that future tick. It
inherits the same-target admitted layout when both epochs and sizes match, without release and re-entry.
For an initial record without that inheritance, use its first applied preparation-state tick if that source
frame is legal, otherwise the earliest subsequently available legal frame. Until one is admitted report
`MissingGeometry`, without creating a correction goal or holding feedback. At the prepare tick, the common
legal snapshot rule below can refresh inherited geometry. A future prepare tick never breaks a continuing
fight's admitted layout.
Use existing `TileReach.Contains` with the actual attacker size, target footprint, plane and client map.
A map difference limits presentation, not the server's result.

Map committed footprint centres through `Presenter.PoseAt(footprint, plane)`. Their world X/Z points are
layout points `F_i`. The directed desired vector is `F_target - F_attacker`. Do not normalize it to one metre.
A 1x1 cardinal pair has one-tile separation. A 1x1 player beside a 2x2 cow retains the actual legal centre
vector, including a possible 0.5-tile lateral and 1.5-tile normal difference. The game owns clips, chest
heights, sockets and attack surfaces, which this generic centre layout cannot infer.

Retain the admitted legal layout through out-of-reach ticks. Pursuit's temporary two-tile committed gap
must not widen its displayed contact goal. A pending strike still waits for the server. Displayed proximity
does not predict damage. Adopt changed legal sides or sizes for an existing component only from one new
snapshot in which every constrained edge is legal. That snapshot's actual centres form a consistent layout.
Mark differing vectors `ChangedGeometry` until aligned. Otherwise retain the last admitted layout.

## 6. Components, multiple targeters and cycles

Use an undirected graph for layout ownership while retaining directed attempt identities. Compute goals
from raw poses and layout points only. Never feed a corrected target back into another correction.

A first pair initializes from its legal source. A reciprocal edge with the negative vector is redundant
geometry. A new edge joining separate components translates the newer component's entire layout to satisfy
that edge, preserving all old relative vectors. The older layout stays fixed. Older means earlier creation
prepare tick, then smaller minimum net ID. Preserve current offsets while establishing the merged goals.

For an edge within a component, compare its vector with the existing point difference within
`0.0001 * TileSize`. If they disagree, try a common legal snapshot for every edge. If unavailable, keep the
older compatible layout and track the conflicting edge with `ConflictingLayout`, without imposing its
contradictory constraint. Retry on new source ticks. Its result still arrives and is measured against its
own desired vector. Do not average an impossible cycle into apparently successful contact.

A consistent cycle is exact at the goal because each vector is the difference of two points and its sum
around the cycle is zero. Splits preserve remaining points and controllers, choosing anchors anew. Merges,
new links and incompatible cycles can need fresh convergence time. A coincident impact can legitimately
miss and must report that limit. The ordinary acceptance fixtures cannot invoke this exception.

## 7. Anchor and bounded frame algorithm

Anchor priority is: an eligible previous stationary anchor, otherwise the smallest-ID stationary member,
otherwise the local raw body if present, otherwise the raw centroid. Stationary means raw planar speed no
greater than 0.001 tiles/s for one world tick and no authoritative step in flight. Correction motion never
decides stationarity. This keeps a stationary target from shuffling toward a run-up and back. It may move
the local rendered body relative to its raw camera anchor, an explicit look-review decision.

For a body anchor `k`, translation is `A = r_k - F_k`. For centroid anchoring it is `mean(r_i - F_i)`.
Here `r_i` is the current raw pose. Desired body position is `y_i = F_i + A`, with offset goal
`g_i = y_i - r_i`. At those goals every accepted edge has its admitted footprint vector. Centroid anchoring
minimizes summed squared offsets without picking an attacker as geometric owner.

If any goal exceeds `MaxGoalOffsetTiles`, multiply all component goals by one common scale
`min(1, limit / max_goal_length)` and report `GoalDistance`. This bounded transition goal does not pretend
to satisfy the full layout. Do not independently clamp bodies into different intended layouts.

Advance after the raw local and remote presentation samples. Let `h = min(valid_positive_dt, 1/30 second)`.
Invalid or zero dt moves no correction. Free-space motion uses `alpha = 1 - exp(-h / ResponseSeconds)` and
`delta = alpha * (goal - current_offset)`, capped to `MaxAdjustmentSpeedTilesPerSecond * TileSize * h`.
Apply that delta. Entry starts at zero. Revisions, goals and anchors keep the current offset. Release uses
the same rule toward zero with `ReleaseSeconds`, subject to section 8. Static goals cannot overshoot.
A stalled frame cannot spend a long accumulated correction in one image. A result gives no extra advance,
gain, zeroing or snap. Contact is measured after the one ordinary advance.

This guarantees position continuity and a free-space adjustment bound, not globally continuous acceleration.
Total body travel is bounded by raw travel plus the adjustment budget. Sort identities, build components,
establish layouts/anchors, compute goals, advance bodies, then measure queued results. There is no convergence
loop over edges. Capacity retains existing components first, then whole new components by age and minimum ID.
Never truncate a component. Excess components return raw poses with `ParticipantCapacity`. A previously
admitted component that loses capacity releases, preserving its current offset.

## 8. Turns, collision, ground and gait

Correct X/Z only. Resample existing presenter ground at the adjusted centre on the existing plane. Conversion
back to `PoseAt(Vector2, plane, facing)` removes its half-tile convention from that already-centred coordinate,
for any footprint size. No correction changes plane or epoch. Planar alignment does not promise vertical
weapon contact on a slope, which needs separate game measurements.

Before accepting adjustment, sweep the footprint along its proposed incremental segment with the existing
tile collision and corner rules. Also require a clear segment from the proposed body point to the current
raw point. Retain that segment as a release tether. A turn updates only legal goals, without resetting motion.
A blocked adjustment reports `Collision` and enters release, rather than cutting a corner to force contact.

For collision release, extend the known tether with continuous raw travel, consume it from the body end,
and collapse clear shortcuts. Each frame's travel budget is raw travel plus the adjustment budget. Keep at
most 64 waypoints. If that bound cannot be retained or raw travel becomes unknowable, report `ReleasePath`
and make at most one bounded existing `TilePathfinder` search per new source tick, within eight tiles, for
a complete collision-valid return path to the current raw footprint. This is a visual return, not an input
command or a predicted server route. Follow only a complete valid path. With no path, hold the last safe
body point and retry on a new source tick or topology change. Outcomes still confirm on receipt. A real cut
or removal ends the hold. Changed topology can therefore cause a visible hold and extra lag.

`MaxGoalOffsetTiles` limits new goals, not accumulated lag while a collision hold prevents return. Report
excess lag as `ReleasePath`. Finite movement, no wall crossing and a hard absolute offset bound cannot always
coexist after a topology cut. This design gives safety of the displayed path and continuity priority, and
reports the missed contact. It does not silently teleport the body back to its raw point.

Public `Velocity` is actual body finite-difference velocity, `BaseVelocity` raw velocity. Both start at zero
and are zero on a cut. Preserve gait phase and weights across attempts. Advance the game's distance-driven
gait once from actual corrected ground displacement, not the goal or raw travel. Grimhollow's existing
`WalkCycle.Advance` already derives phase from shown distance and blends run weight. Cuts do not count as
strides. Retain the existing attack phase clock. The engine sample retains raw yaw, while game facing reads
actual body travel and corrected targets through its existing action and turn-smoothing rules.

Rate bounds alone do not approve sideways corrections, release backsteps, sharp stride-rate changes or the
local body/camera difference. These need the look gate in section 11, with actual velocity and gait reported.

## 9. Contact measurements and deterministic fallbacks

For each real result compare the corrected planar relative vector with its admitted legal vector. Use the
impact tick's saved source frame to verify legal side, epochs and sizes. Missing impact geometry reports
`MissingGeometry`, giving the available admitted-frame error without claiming verified impact placement.
If the result changed legal side, measure against that new vector even if motion has not caught up.
`WithinTolerance` requires matching impact geometry, no excluded layout or goal-distance constraint, and
error within tolerance. Increment the miss count once for every failed or unverified result.

| Condition | Behavior |
| --- | --- |
| Ordinary preparation, stable contact side | Converge to the legal layout, retaining it through pursuit's illegal tick |
| Preparation first arrives during strike or after deadline | Bounded entry, `LatePreparation`, receipt feedback even if contact misses |
| Result with no admitted preparation | Raw or already-owned motion, `MissingGeometry`, no result-triggered repositioning |
| Reversal or new legal side shortly before impact | Continuous goal update, `ChangedGeometry` when the new contact is not reached |
| Epoch, plane or existing presentation cut | Existing raw cut, affected-state reset, `PresentationCut` |
| Target removal | No invented target pose, `TargetUnavailable`, edge retirement and neighbour release |
| Multiple targeters or graph merge | One layout, retained offsets and bounded convergence. Coincident contact is measured |
| Consistent cycle | One layout, no double correction |
| Incompatible cycle without a common legal frame | Older compatible layout wins, conflict is reported, no averaging or feedback delay |
| Burst, reorder or duplicate | Existing ledger filters identity/revision. One advance, ordered once-only feedback and per-result measurements |
| Blocked correction or failed release path | Safe return or reported hold, never unsafe contact |

Several outcomes in one poll cannot reenact several historical body placements in one frame. Preserve their
original delivery order, advance once and measure each. Old results do not rewind the layout or restart a
controller. A historical result is marked `LateOutcome`. No hitsplat alone is delayed to make it line up.
Historical means a result's impact tick precedes the newest applied movement tick when its callback is
delivered. `GeometryServerTick` is the matching impact source tick when available, otherwise the admitted
source tick, or -1 when neither exists. Terminal hold starts on receipt's combat presentation time, not a
possibly long-past impact tick. Late flags may coexist with successful contact when the measured geometry
does happen to align. They never manufacture that success or suppress the diagnostic.

## 10. Game adoption boundary

Opt in at the client composition root after released engine adoption. Camera, input, prediction and rule
overlays use the unchanged raw reads. Bodies, held items, overheads, hitsplats, body picking, shield/death
poses and positional feedback share the corrected body sample for that frame. Game water-height and rig
adjustments apply afterward. Body draw priority treats correction travel as displayed movement without
changing the authoritative tile used by gameplay.

Collect results during `Poll`, advance engine presentation, then place game feedback from that frame's body
samples on the same render frame. This does not defer feedback. Preserve authoritative health, retaliation
and once-only outcome ownership. The game owns authored contact, facing smoothing and look approval. The
engine owns graph geometry, motion, constraints and diagnostics. No parallel game contact solver is added.

### Death and presentation successor handoff

The transfer method queues a visual identity handoff for the next ordinary presentation advance. It changes no
entity position, ownership or feedback. Disabled, unjoined, unknown successor, incompatible plane or footprint,
and unavailable predecessor history return false. Duplicate requests for the same pair do nothing. A known
predecessor or its last completed visible sample retained for two presentation frames can supply the starting
world point. Retention is bounded by the participant capacity and connection generation.

On acceptance, initialize the successor's planar offset from that point minus the successor's current raw point,
then release through the same bounded motion and collision rules. Do not copy the predecessor's offset alone,
because the two raw bases may differ. This preserves the handoff point and converges toward the authoritative
successor pose. It does not preserve a permanent corpse offset. The game owns collapse motion and facing.

Grimhollow's `CarcassState.DeadActorNetId` provides an explicit predecessor link, and carcasses already have a
remote tile pose. Register the handoff before advancing presentation when that link matches a previously shown
cow. Seed the existing collapse from the shown living motion and transfer its hitsplats as today. First-sight
carcasses without history use their raw pose and completed collapse. Never infer a lineage from proximity.

A removed target with no impact geometry remains a reported unavailable contact. Historical visible samples
can preserve visual continuity but cannot establish a new authoritative epoch or impact tile. Keep that limit
visible in diagnostics. Verify killing swings, moving kills, matched carcass collapse and first-sight carcasses.
Ground loot keeps its authoritative placement. Include its placement beside the dying body in the look packet,
so contact correction cannot hide a misleading pickup position.

## 11. Tests and owner look gate

Headless engine tests belong to `KhaozEngine.TileWorld.Netcode.Tests`. Command-based tests phase the client
independently before join. Lockstep-only tests cannot establish the behavior.

| Family | Required proof |
| --- | --- |
| Disabled mode | Exact raw poses/callbacks and raw geometry characterization unchanged |
| Footprints | 1x1, both sides of 2x2, lateral offsets, reversed edges and unequal sizes use actual centres |
| Stable pursuit | Walk4/run2 legal 1, illegal 2, legal 1 sequence never widens the admitted goal on the illegal tick |
| Graph | Reciprocal pair, three targeters, square cycle, merge/split and conflicting mixed-time cycle. Layout sums, retained offsets and explicit exclusion |
| Motion/reset | Entry, revision, switch, release, invalid dt, stall, epoch, plane, snap, removal/re-entry. One advance and travel bounds |
| Delivery | Missing source/preparation/impact, late results, duplicates/reorders, burst and callback disconnect. No invented result, snap or duplicate diagnostic |
| Constraints | Reversal, legal-side rotation, wall/corner, 2x2 passage, tether overflow, missing path and changed topology. No unsafe segment or hidden contact success |
| Consumer | All 17 live-session contact cases and authored blade checks, retaining moving/stopped target pins |
| Pursuit | All three existing goblin-on-runner blows, including the deferred first blow, inside the consumer envelope |
| Clocks/jitter | Client phases 0.06 and 0.13 s, 60/50 Hz, independently phased observer, deterministic delayed and burst delivery |
| Gait/camera | Actual body travel drives continuous phase, no attempt reset, unchanged camera/prediction, no stride on a cut |
| Death handoff | Killing swing, moving kill, explicit predecessor/successor, differing raw bases, first-sight corpse, unavailable history, duplicate request and bounded collision release |

Normal cases report prepare/strike/scheduled/actual impact ticks, source/layout ticks, raw/body points, offsets,
anchor, travel, gait phase/weights, centre error, blade contact and limit flags. They fail if capacity, conflict,
goal distance or path fallback is invoked to excuse contact. Exception cases assert their named fallback,
uninterrupted receipt feedback and absence of an impact snap. Never loosen the consumer envelope.

The owner's look packet compares run-up, walk-up, walking-away target, observer, all chase blows, sword/shield
and 2x2 cow. Include the largest local body/camera offset, fastest adjustment, anchor change, corner release
killing and carcass handoffs, ground loot placement and deliberately late result. Inspect target stability, stride rate, sideways/backward movement, facing and
held-item contact. Distance checks do not approve appearance.

## 12. Written decisions requested

Approve or revise the separate smooth body adjustment, stable legal layout, stationary/local/centroid anchor
priority, constants and continuous fallback when contact is causally unavailable. Confirm receipt-frame
feedback despite a measured late or incompatible contact miss. Local body/camera separation, collision holds
and locomotion look need explicit review. No implementation plan, engine code or game adoption starts until
this written artifact is approved.
