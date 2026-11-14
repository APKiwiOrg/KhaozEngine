# Forced movement and fine speed scale

Ground commitments provide server-authoritative ground travel with collision-safe client prediction. They extend
movement commitments so replication, reconciliation, pending-command replay, same-host handoff and movement
anomaly exemption share the existing physical state. Ballistic commitments retain their launch and landing rules.
Usage belongs in the [consumer contract](../USING-KHAOZENGINE.md) and the
[Movement](../../KhaozEngine.Movement/README.md#ground-pursuit-for-committed-travel),
[Locomotion](../../KhaozEngine.Locomotion/README.md#types) and
[NetWorld](../../KhaozEngine.NetWorld/README.md#server-authored-committed-movement) references.

## Authority and package boundaries

The game calls one `GroundPursuit` from its authoritative tick, supplies actor and target state, then queues begin,
steer or end through the server. Movement owns guarded planning and route following. Locomotion owns physical
travel, progress, stalls and recovery. NetWorld owns sequence-checked controls, replication and events. Its request
DTOs and stop reasons live in Netcode without a Locomotion dependency. NetWorld has no Navigation or Movement
edge. `PathFollower` and `MoveToRange` retain their independent contracts.

The game owns stable actor and target identities, ability admission, active ability bookkeeping, cooldowns,
resource costs, damage and other effects. There is no engine actor or ability manager, because that would require
actor lookup adapters and add a second lifecycle owner. Stable actor identity and commitment sequence keep the
same pursuit valid across a same-host cell handoff. Route caches are not persisted. Carried physical progress is.
Changing target identity, teleporting or cancelling discards the pursuit cache before another admission.

## Guarded pursuit

Each ability supplies independent maximum straight-line trigger range and maximum path length. Current exact
body reach is checked first. The trigger range applies only at admission. The path cap applies to the current
remaining route at admission and every replan, preventing excessive detours around cliffs. Timeout bounds total
travel even when replanning supplies another budget. Path lengths and distance budgets are horizontal XZ metres,
consistent with horizontal speed. Slope rise does not consume budget. Endpoint membership and arrival use exact
three-dimensional body reach, including capsule extent and target height.

A route must be Complete, nonempty, walk-only, inside the path cap and end in current reach. Partial, unreachable,
Hop, Drop and unknown transitions refuse. Pursuit asks the planner for its default-off walk-only policy on every
query, because a Drop or Hop can otherwise be cheaper than an available walking detour. It still checks each
waypoint kind, because another planner can ignore that option. Actual-to-route entry and shortcuts use the same
supported-walk admission. A server-side navigation profile must match the actor's capsule, tuning and resolved
movement context, so a route certified for another body cannot authorize this actor.

An ability may narrow that profile with `GroundNavigation.ExcludingAreas(uint excludedAreas)`. The immutable
view shares the baked graph and captured per-surface area bits, so an actor allowed to wade can pursue through a
dry bridge without a second whole-world bake. Exclusions accumulate and are checked in addition to the original
required and excluded masks. Zero keeps the original profile. Even an exclusion overlapping a required bit can
only refuse more routes. The existing bake format and runtime movement binding remain unchanged.
The view also shares current source terrain ownership, including later attachment and evidence rebinding.
Range movement retains the stored terrain Drop policy and checks exclusions across its complete certificate
shell before execution. Pursuit still refuses every Drop through its separate walk-only policy.

The query restriction trusts the graph's existing support and clearance proofs. Requiring a link's straight
chord to satisfy supported-walk height checks would reject a permitted tall dry Drop even when the excluded
bit is absent. Area filtering adds only its area checks there. Pursuit separately keeps its walk-only policy.

Search checks full captured footprints at nodes and along directed edges, including links. The same view guards
straightening, actual-to-route entry, kept segments and waypoint advancement at admission and every replan.
If a filtered route fails, a diagnostic query uses the original baked policy with the same walk-only budget.
Only a complete original route that passes supported-walk admission produces NoPath with
`GroundPursuitDetail.ExcludedArea`. Other failures keep the filtered query's detail. The extra bounded query
distinguishes an area restriction from an already unreachable target without ever steering along its route.

Target drift, corridor departure, route exhaustion and invalid cached endpoint membership make replanning due.
An endpoint outside current reach needs refresh even below the ordinary drift threshold. During replan cooldown,
pursuit returns its last valid direction with zero length to hold travel rather than continue toward a stale goal.
A previously nonterminal route reaching a stale terminal can refresh once immediately. A fresh query at its
terminal keeps the cooldown. Crossing a waypoint measures progress along the actor's actual steering leg, so a
stride longer than twice the acceptance radius cannot oscillate across the waypoint. Advancement still requires
a supported segment from current feet to the next waypoint. A valid final approach keeps a positive budget inside
the waypoint acceptance radius until exact body reach witnesses arrival.

## Physical travel and prediction

Ground travel requires a grounded actor that is neither swimming nor airborne from water. A refused server begin
leaves any active commitment untouched and emits one Ended for its own sequence with UnsupportedState, giving
ability bookkeeping a terminal signal. Ground preparation enters Travelling without a vertical launch. Both
controllers retain ordinary gravity, slopes, step handling, support and collide-and-slide. Travel faces its
committed direction and ignores player movement, jump, facing and `SpeedScale`. The game refuses admission while
rooted and decides whether a later root or stun cancels an active ability.

Every step caps intended horizontal travel to the remaining budget and subtracts achieved XZ displacement.
Three consecutive steps achieving less than 10 percent of intended nonzero travel end as Blocked. Adequate
progress resets the carried counter. Steering and zero-budget holds do not reset it. A residual budget too small
for the controller to resolve is spent rather than counted as a stall. Water entry, lost support, teleport,
disconnect, supersession and timeout release control without lingering travel.

Only direction and remaining budget replicate, rather than a full route. Clients replay those controls without
navigation queries. A zero budget stops travel and holds input suppression until server steering or end, because
an old endpoint cannot prove reach of a moving target. Older steering at a corner can produce a short straight
run followed by correction. This is the accepted bandwidth and layering tradeoff. The phase-offset loopback
proof measures the rendered avatar as well as correction, with bounds derived from speed, fixed tick, configured
downstream delay and physical clearance. Separate client and server accumulators expose mid-presentation
reconciliation that a phase-locked harness would hide.

Fixed-direction ground knockback can use the same physical primitive with caller-owned completion. Leaps use
the ballistic kind. Neither primitive implies a turnkey knockback or leap ability.

## Arrival, recovery and release

Authoritative pursuit confirms Arrived only against the current supplied target shape. Accepted arrival emits
Arrived immediately before optional recovery advances. The game validates target, reach and combat rules and
applies its strike there. Ended means control was released and removes active ability bookkeeping. Each event
fires once. With no recovery, arrival precedes end in the same frame. Ballistic Landed keeps its existing meaning.

Accepted arrival cannot be revoked, because it is the game's strike point. Every later Ended for that sequence
reports Arrived, including water, lost support, teleport, supersession, disconnect and abort. Those interruptions
still release control immediately. Travel timeout stops applying after accepted arrival. Recovery is bounded by
its own timer. Steering and repeated end calls cannot restart recovery. Target loss, route loss, over-cap routes,
sustained blocked progress and travel timeout cancel before arrival.

## Precision and compatibility

`MovementState.SpeedScaleQ` is a short with quantum 1/1024, offset from one. The maximum stays 8x, zero/default
still decodes to exactly 1, root to exactly 0, and the clamping and nonfinite policies remain unchanged. A requested
10 percent haste becomes 1.099609375 rather than 1.125. Ordinary movement and effect consumers use that fine
scale. Ground travel keeps its own speed. Bundling both changes avoids a second coordinated protocol migration.

Wire generation 15 requires matched client and server and a major engine repin. The movement payload is 64 bytes.
Speed widens inside the old layout and the appended ground state defaults to Ballistic and zero. This requires
an explicit historical rewrite rather than append-only normalization. Old signed speed q becomes Int16(q * 64),
preserving its decoded value, including negative corrupt values that floor to zero. Pre-speed layouts get the
unmodified zero. The rewrite composes with the owner-timer split, preserving framed positions, owner-frame order,
legacy Swimming, unrelated carried state and opaque extensions. Malformed or truncated payloads still refuse
and quarantine. Persisted movement payloads migrate automatically through normal blob normalization. See the
[wire and persistence contract](../../KhaozEngine.NetWorld/README.md#fine-speed-and-ground-commitment-transport-wire-generation-15).
