# Forced movement design

Approved direction for the unbuilt [forced movement program](forced-movement.md). Nothing below
describes shipped ground pursuit. The tracker owns current state, owner gates and FM-D decision ids.

## Responsibility and composition

Locomotion owns immutable ground commitment state, physical progress, stall accounting and recovery.
Movement owns a reusable pursuit object and guarded region planning. Netcode retains dependency-free
requests under their shipped NetWorld namespace. NetWorld queues authoritative control, publishes
events and carries movement through wire, reconciliation and persistence. The game owns actor and
target lookup, active ability lifecycle, costs, cooldowns, root/stun policy and combat effects.

The game tick calls the engine pursuit object and forwards results to the server controls (FM-D12).
A turnkey engine charge manager was rejected because it adds actor lookup adapters and another
lifecycle owner. Both float server heads already invoke OnBeforeTick before draining admin commands.
NetWorld takes no Movement/Navigation edge and Netcode takes no Locomotion/Navigation edge.
PathFollower and MoveToRange are not edited for this capability (FM-D6).

## Ground commitment

Add a MovementCommitmentKind with Ballistic at zero and Ground next. Append Travelling to the phase
enum and new end reasons without changing existing ordinals. Carry Kind, DistanceRemaining and
ConsecutiveBlockedTicks. Preserve ballistic constructors and behavior. Add ground factories to the
Locomotion state and MovementCommitmentRequest, keeping the request below Locomotion without naming
a Locomotion enum. Default requests remain invalid and direction normalization handles extreme finite
nonzero inputs safely.

After preparation, ground travel has no vertical launch. It commands Direction times HorizontalSpeed,
ignores player travel/jump/facing input and SpeedScale, faces its direction, and retains ordinary actor
gravity, slope/step following and collide-and-slide. It must obey support and blocking rules.

Cap each intended horizontal step to the remaining budget. Subtract achieved horizontal progress,
never below zero. Three consecutive nonzero steps achieving less than 10 percent of intended travel
abort as Blocked. Adequate progress resets the counter, steering does not. Zero-budget holds do not
count as stalls. The counter is carried for deterministic replay. Water, invalid support, timeout,
teleport, disconnect and supersession have explicit terminal handling.

At zero budget, horizontal travel stops and the commitment holds active for authoritative steering or
end. Timeout continues and user input stays suppressed. A consumed old route is not evidence that a
moving target is still in range. This refines the old unbuilt budget-completes-arrival direction.

Authoritative Arrived is based on the current target shape. Emit an arrival event immediately, then
enter optional recovery or complete. Recovery retains input suppression and preserves Arrived when
Ended fires. This gives the game its strike point before recovery ends. Prediction may stop at its
budget and replay carried recovery, but never authors arrival or damage (FM-D5, FM-D13).

## Pursuit and admission

GroundPursuit lives in Movement, one instance per pursuit. It offers a GroundNavigation convenience
constructor and a lower-level equivalent with IRegionPathPlanner, NavSpace and segment admission.
Validate navigation against the actual capsule and GroundMoveContext, as guarded range movement does.

Each ability supplies TriggerRange, MaximumPathLength and StopDistance (FM-D10). Trigger and path caps
are finite and positive, stop distance finite and nonnegative. Planner budget and replan/corridor
policy are explicit options using established path-following defaults. Speed and movement timings
belong to the commitment request, not duplicate pursuit settings.

Inputs are authoritative MoveState, MoveTuning, the current ReachTarget or explicit missing-target
indication, elapsed time and GroundMoveContext. Positions are absolute at this boundary. Identities
remain game-owned. Target replacement, teleport and manual cancellation require Reset or a new pursuit.

Admission first checks current reach and returns AlreadyInRange for game policy instead of issuing a
zero-direction move. Trigger range uses ReachGeometry's actor/target shape distance. A route must be
Complete, nonempty, end in the current reach region and contain only Walk waypoints. Refuse partial,
unreachable, hop, drop, unknown-transition and over-cap routes. Guard actual-to-route entry and any
shortcut with the same segment admission. Ready returns direction and admitted remaining path length.
Other results are AlreadyInRange, OutOfRange, NoPath, TooLong and UnsupportedState, with typed detail.

Path budget and speed use horizontal XZ metres. Endpoint membership and arrival use exact 3D reach,
including capsule height and extent. Vertical slope rise does not consume horizontal travel budget.

Per tick, check current reach before route following. Replan for target drift, corridor exit, consumed
route or invalid cached endpoint. An endpoint outside the current reach region forces replanning even
below the ordinary drift threshold. Reapply full walk-only and path-cap admission. Trigger range gates
the initial use, while MaximumPathLength caps the remaining route at each admission/replan. Timeout
bounds total pursuit duration, steering must not refresh it.

Tick returns Travelling with direction and remaining length, or Arrived, TargetLost, NoPath, TooLong
or UnsupportedState. It does not step physics or write player positions. The game forwards these
results and stops pursuit updates on arrival/cancellation. It supplies target state across same-host
cell handoffs, keyed by stable actor identity and commitment sequence. Route caches are not persisted.

Client replay uses carried direction/budget rather than a navigation query. An older steering basis
at a corner can produce a short straight run and correction, the accepted FM-D5 tradeoff. Quantify
consumer-visible correction against speed, tick, delay and clearance. Do not hide it with a loose
absolute tolerance or silently add a replicated full route.

## Server control and events

Retain BeginMovementCommitment and AbortMovementCommitment. Add sequence-checked steering and end
calls to both float heads and IAdminControllable, with unsupported defaults on other heads. The proposed
forms are SteerMovementCommitment(target, sequence, direction, distanceRemaining) and
EndMovementCommitment(target, sequence, reason). The end request uses a dependency-free Netcode enum
mapped into Locomotion reasons.

Validate values before queueing, apply on the host thread. Calls from OnBeforeTick affect that frame's
movement. Ignore stale sequences, absent actors, wrong kinds, terminal state and repeated ends without
altering a replacement or duplicating events. Ignore steering during recovery.

Add MovementCommitmentArrived using MovementCommitmentResult. Accepted Arrived emits it once before
optional recovery. Ended emits once at control release. Preserve ballistic Landed/Ended meanings.
Distinguish target loss, route loss/over-cap and unsupported ground state in terminal reasons.

On ability use, the game checks admission and begins a ground commitment. Each server before-tick
hook updates pursuit and queues steering or end. The game applies impact from authoritative Arrived,
with normal target, reach and combat validation. Ended cleans ability bookkeeping. The game refuses
a rooted start and owns any later root/stun cancellation policy. Recovery needs no pursuit steering.

## Fine speed scale

Bundle #1652 (FM-D11). MovementState.SpeedScaleQ becomes short, along with quantizer return and decoder
parameter types. Use offset-from-one quantum 1/1024, preserving the 8x maximum, exact default/root
behavior, nonfinite handling and existing clamp policy. Legitimate q spans -1024 to 7168. Decode still
floors negative results at zero. A requested 10 percent haste becomes 1.099609375 instead of 1.125.
All representable scales remain exact binary floats.

Ordinary movement and effect consumers gain precision. Ground commitment travel still ignores
SpeedScale. This changes a public field type, so the combined delivery is a major engine release.
No game effect definitions or catalog edits belong here.

## Wire and persisted movement

Current baseline is generation 14 and a 57-byte movement payload. Intended generation 15 widens the
existing speed byte in place to Int16, then appends Kind byte, DistanceRemaining float and blocked byte
after the current payload, producing 64 bytes. Recheck the generation after FM-D8 lands. Preserve enum
ordinals, zero Ballistic and zero ground defaults, and update every basis/frame/handoff conversion.

Widening an interior field is not append-only (FM-D14). Explicit historical rewriting sign-extends old
speed q and writes Int16(q * 64), preserving its old decoded value. Layouts before speed scale get
zero/unmodified. Preserve all other fields and compose with the generation-12 owner-timer split and
owner-frame ordering. Never skip widening through the split's early return. New ground state defaults
to Ballistic/zero. Bad or truncated payloads continue to refuse/quarantine.

Verify all source generations 1 through 14 field-by-field against the live codec, with owner timers,
framed position, extension frames and terminators intact. Mixed live generations remain refused at
connect. Consumers need matched client/server protocol and the major engine repin.

## Verification and delivery

- Locomotion tests use real Legacy and Contact wiring, slopes, steps and blockers. Cover exact last
  travel cap, one graze, three-step stall, re-aiming, zero hold, recovery, water/support loss, timeout,
  teleport/supersession and unchanged ballistic behavior.
- Pursuit tests use guarded real navigation. Cover independent limits, cliff detour, already-in-range,
  partial/non-walk routes, moving target, sub-threshold invalid endpoint, exhausted route, corridor
  departure, missing target, cancellation and mismatched body/profile.
- Test both heads for same-hook commands, stale sequences, exactly-once arrival before recovery,
  disconnect, handoff and a sharded frame without a physical step.
- Pin live codec and every historical migration, fine-scale endpoints/ties/nonfinite inputs, old-q
  preservation and server/client ordinary movement parity.
- A phase-offset loopback uses real corner geometry and a moving target. Assert the owner-rendered
  position, authority stop, correction bound, no final overshoot and inability of inputs to fight or
  initiate the commitment. Client prediction and server/reconcile clocks must not be phase-locked.

Implementation and its living documentation land together after FM-D8. Select the next free major
from current main/tags, update all guarded declarations and changelog, then review, reconcile, push
and pack through the normal wrapper. Never increase KESIZE baselines or weaken warnings/tests.

The exact release tag needs separate owner approval. Required hosted full CI and native Metal, D3D11
and Vulkan gates precede tagging. Verify publication and the complete durable feed, announce the
engine-release outcome, then post capability/adoption guidance on the consumer issue. No consumer
repin or release is performed by this engine program. Move surviving decisions into living docs,
delete the completed tracker/design and close #1658/#1652 after verified release.

## Later consumers

A fixed-direction knockback can use the ground primitive with caller-owned completion. A leap uses
ballistic commitment. Neither later ability is implemented in this milestone (FM-D9).
