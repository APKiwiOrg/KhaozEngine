# Tile combat preparation: an authoritative schedule before impact

Status: approved by owner on 2026-09-28, implementation pending. Implementation awaits plan approval.
Based on engine `54404dea5`. First consumer: Grimhollow. The owner selected engine implementation,
an engine release, and consumer adoption, with three ticks of preparation. This document records the
approved behavior for implementation planning.

## 1. Outcome and ownership

A tile combatant can announce a future attack before the server resolves it. The client draws a
preparation and a fast strike toward that deadline. Damage, misses, shields, sounds and death still
follow a resolved server event. Preparation is an intention that can end without a roll.

The capability belongs in `KhaozEngine.TileWorld.Netcode`. It owns tick scheduling, cancellation,
identity, replication and a headless presentation sample. Games own preparation durations, presentation
keys, pose curves, clips and gameplay numbers. No sword, humanoid, cow, equipment slot or animation
asset identifier is built into the engine.

The existing combat path remains the default. An unconfigured server retains its current first-hit
timing, cooldown, event bytes and client callbacks. A consumer opts in explicitly at server creation
and uses a matching client protocol. This feature changes the first eligible hit of opted-in fights.
It does not merely move an animation in front of the previous instant hit.

References: [tile-world netcode](TILE-WORLD-NETCODE-DESIGN-2026-08-22.md),
[combat and actors](TILE-COMBAT-ACTORS-DESIGN-2026-08-27.md), and the
[post-movement seam](TILEWORLD-POST-MOVEMENT-HOOK-DESIGN-2026-09-12.md).

## 2. Rulings and alternatives

### Owner decisions already made

- Build the authoritative schedule in KhaozEngine and adopt a released engine pin in Grimhollow.
- Give Grimhollow three ticks before impact, with a slower two-tick draw and a fast final-tick strike.
- Improve swings in general, including the first swing after a run.
- Review the written design before implementation. This documentation change does not build, tag,
  release or adopt a package.

### Orchestrator rulings proposed for owner review

- Grimhollow enables the schedule for every combat attacker and style, including bare hands and
  creatures. Other engine consumers retain the default behavior until they opt in.
- An already-ready, in-range attacker first considered on tick `T` hits no earlier than `T + 3`.
  This adds 0.5 seconds at Grimhollow's six ticks per second. It changes retaliation and first-hit
  balance as well as appearance.
- Continuing hits retain their existing hit-to-hit cadence. Preparation occupies its final three ticks.
- A new attempt's preparation may occupy the final three ticks of an existing cooldown or accepted
  idle food delay. Its first impact is `max(T + 3, readyTick)`. There is no additional three-tick
  charge after that existing wait. A short idle food wait can therefore overlap the initial preparation.
- Keep an issued preparation through temporary range loss while the same combat lock remains.
  Recheck legal reach at impact. An invalid attempt ends without damage, a miss event or a cooldown charge.
  A later valid opportunity must prepare again.
- Target or presentation-profile changes replace the attempt. They cannot bring the old attack-ready
  boundary forward. Repeating the same attack command does not restart an unchanged attempt.
- Food adds its accepted delay to the pending deadline. It does not add another full preparation on
  top. Retire the old revision and start the new draw three ticks before the revised impact, with
  carry during any intervening wait. Do not stretch a held wind-up across that wait. A revised animation
  can have less visible preparation when the revision arrives late.
- Impact feedback is event-driven and presented together. The client never reports a hit solely because
  the preparation clock reached its deadline.

Client-only anticipation was considered. It cannot know the server-only cooldown or guarantee a
first preparation after an adjacent click. Delaying only visual feedback was also considered. It
would separate damage, death and sounds from the visible strike. The selected schedule instead makes
the future authoritative impact a real part of the opted-in combat rules.

Cancelling on every out-of-range tick was rejected. Equal-speed tile pursuit can alternate between
legal and illegal range, making a three-tick preparation impossible to finish. Keeping the attempt
and checking its final tick preserves pursuit while retaining authoritative reach.

## 3. Public seam and state

Names below are the proposed API contract. The implementation plan may refine internal names, but
must preserve these semantics and identify any public API changes for review.

- `TileWorldServerConfig.CombatPreparationRules`: optional `ITileCombatPreparationRules`, default null.
  Fixed for that server instance. Null selects the complete legacy path.
- The existing `TileWorldServer.CombatRules` remains nullable and mutable. In enabled mode, null
  prevents new attempts. A live attempt whose rules become null is cancelled at the next combat pass
  as `rules unavailable`, retaining its old impact as a readiness lower bound. Returning rules require
  a fresh preparation. Disabled mode preserves its existing null-rules behavior.
- `TileWorldClientConfig.CombatPreparationEnabled`: bool, default false. True enables the preparation
  callbacks and reads for that connection. The consumer's connect version enforces matching modes.
- `ITileCombatPreparationRules.ProfileFor(long attackerNetId)`: returns
  `TileCombatPreparationProfile(byte LeadTicks, byte StrikeTicks, uint PresentationKey)`.
  The provider reads the game's current admitted state and must be deterministic and side-effect free.
- A valid enabled profile has `1 <= StrikeTicks <= LeadTicks <= resolved AttackTicks`.
  Existing cadence fallback still resolves a zero rules cadence through spawn cadence, then one tick.
  An invalid profile cancels its pending attempt and prevents a new roll, with a developer diagnostic.
  It never falls through to an immediate legacy hit. No engine-specific minimum in seconds is imposed.
- `TileCombatPreparation`: immutable public sample carrying attacker, target, attack ID, revision,
  presentation key, preparation start tick, strike start tick, impact tick and sampled cadence.
- Server and client `TryGetCombatPreparation(attacker, out preparation)` expose the current attempt.
  Client notification reports an atomically applied schedule frame, not partially decoded chunks.
- `PreparedCombatEvent` pairs the existing outcome fields with authoritative impact tick, attack ID,
  revision and presentation key. `CombatPreparationEnded` reports a cancelled identity and reason.
  Prepared client outcomes use these new callbacks. They do not also raise the legacy client
  `CombatEvent` callback. The server's existing `OnCombatEvent` still fires once for every resolved
  swing, preserving game awards, journals and retaliation hooks.
- `TryGetAttackReadyTick(attacker, out tick)` exposes the authoritative earliest attack boundary.
  With a live schedule this is its impact tick. Without one it is the later of retained readiness and
  cooldown boundaries, which alone does not promise an immediate hit because acquiring a preparation
  can impose a later boundary. It returns false only for an unknown or removed attacker. A live idle
  attacker with no wait returns the current tick.

The preparation state lives in a new migration-only component beside `TileCombatState`. It contains
the active descriptor, the next per-attacker attack ID and the readiness lower bound needed across
cancellation. It survives in-process region migration. It is transient combat state, is not written
to durable player records, and is cleared on despawn or a new connection's combat lifecycle.

`AttackId` is a monotonically increasing unsigned 64-bit value per attacker lifetime, starting at one.
Each new attempt gets a new ID. `Revision` starts at one and increases for a deadline change on the same
attempt. Identity is `(connection session, attacker net ID, attack ID, revision)`. A replacement retires
the previous ID. Values never wrap into an older identity. Revision exhaustion replaces the attempt
with a new ID. Attack-ID exhaustion refuses new preparations with a developer diagnostic.

Only presentation identity and timing are captured. Damage and accuracy still read current authoritative
state at the impact roll. The game changes `PresentationKey` when its visible weapon or style changes.
The key must cover every game-owned distinction whose change should replace the motion. A change to
lead ticks, strike ticks or resolved cadence also replaces the profile even if its key is unchanged.

## 4. Server tick contract

Preparation runs in the existing combat pass after command admission, movement, handoffs, the
post-movement callback and action resolution. It reads committed tiles and existing `TileReach`.
It never reads a drawn pose, projected route tile or render interpolation.

Let `T` be the current combat-pass tick, `P` the profile's lead, `F` its strike duration, `C` the
resolved cadence and `R` the existing cooldown remaining after this tick's ordinary decrement.

1. Check `CombatRules` availability before resolving a profile or creating an attempt. If null, create
   no attempts and cancel every live preparation as `rules unavailable`, including one due this tick.
   Retain each cancelled impact as a readiness lower bound. Do no roll or new cooldown charge, while
   existing readiness and cooldown continue to run down. When rules return, require rule 2's fresh
   preparation, so Grimhollow's next eligible tick `T` schedules no earlier than `max(T + 3, readyTick)`.
   Apply other invalidations and delay requests before deciding whether an attempt is due. Read the current
   target, profile, life and legality from this tick's admitted state.
2. An attacker with no attempt may create one only when `CombatRules` is non-null, both bodies are
   alive, the lock is admitted and the target is in legal reach now.
   Set `H = max(T + P, T + R, retained readiness boundary)`.
   The attempt's preparation start is `H - P`, strike start is `H - F` and impact is `H`.
   It may be announced during the earlier cooldown hold. It cannot roll on its creation tick.
3. An unchanged live attempt keeps `H`. Repeated commands naming the same target do not restart it.
  Before `H`, no damage roll, attack RNG draw, experience award or attack combat-log stamp occurs.
   Preparation does not newly count as a landed attack for aggression or combat logout. Those existing
   rules continue to read resolved swings. The first-hit delay therefore also delays retaliation.
4. At `T == H`, recheck rules availability, life, current target, profile, permission, plane and reach.
   An invalid attempt ends with its reason. It does not roll, count as a miss, award experience or start a new cooldown.
   Do not create its replacement in that same pass. Reconsider on a subsequent tick.
5. All valid due attempts join the existing oldest-lock-then-net-ID roll order. Roll all, then apply
   all, then determine deaths. Preserve mutual kills and existing outcome semantics. An attempt
   already admitted to this tick's roll survives damage caused by another roll on that tick.
6. A resolved hit or miss consumes the attempt and sets the existing cooldown to `C`. After deaths
   and callbacks settle, a still-live attacker and target with the same valid lock, unchanged profile
   and legal reach may receive the next attempt at `H + C` if `CombatRules` remains non-null,
   with its final `P` ticks reserved for preparation. A delay
   accepted during an outcome callback also contributes before that next deadline is announced.
   If rules are null, reach is absent or a callback changed target/profile, leave no next attempt
   and let rule 2 handle a later opportunity while retaining the cooldown just charged.
7. Cancel future attempts for dead or despawned participants before serving. Serve movement and health,
   then complete preparation state, then terminal records. Reap dead actors after the serve as today.

Readiness runs down independently of targeting and range. Cancelling or replacing an attempt retains
the later of its previously scheduled impact boundary and its cooldown boundary. It cannot reset the
wait to zero. Once that boundary is in the past it imposes no extra delay. A cancelled attempt does
not repeatedly extend readiness while idle.

For Grimhollow's stone sword, if ready and adjacent at tick 100, announce impact 103. Draw during
`[100, 102)`, strike during `[102, 103)`, roll at 103. With cadence 14, announce the next impact at
117. Hold through 114, draw during `[114, 116)`, strike during `[116, 117)`, roll at 117. This is
14 ticks between impacts, never 17. The preparation is 0.5 seconds, comprising 0.333 seconds of draw
and 0.167 seconds of strike. Engine tests also use other tick durations and profiles.

## 5. Changes and interruptions

| Change before impact | Authoritative result | Presentation result |
| --- | --- | --- |
| `CombatRules` becomes null | Cancel live attempt as rules unavailable at the next combat pass, retain its impact boundary, do no roll or new cooldown charge. No new attempt until rules return, then require a fresh preparation | Recover the aborted motion without impact feedback |
| Same target and profile, repeated Attack | Keep identity and deadline | Continue current motion |
| Different admitted target | Cancel old ID, retain readiness, create a fresh attempt when legal | Ease out the old preparation, use the new target and ID |
| Weapon, style, lead, strike or cadence changes | Cancel and replace under rule 2, never accelerate old readiness | New profile key owns the next motion |
| Local WalkTo, steering, interaction or other command clears lock | Cancel immediately after movement | Stop the intended strike, preserve ordinary movement |
| Automatic pursuit retains target | Keep attempt even while moving or temporarily out of range | Blend preparation over locomotion and keep target facing |
| Illegal reach or plane at impact | Cancel without roll or cooldown charge | No impact feedback, recover the aborted motion |
| Death, target removal or permission revocation before due roll | Cancel | No further predicted strike |
| Death from another due roll on the same tick | Existing simultaneous-roll rule applies | Deliver both real outcomes, then end future motion |
| Teleport changes either participant's teleport epoch | Cancel, then require a fresh preparation | Discard pre-teleport motion |
| Food or another accepted attack delay | Revise deadline on same ID | Re-time toward the revised deadline, no early hit |
| Disconnect or interest loss | Server fight follows existing lifecycle, client forgets invisible state | Clear cached samples and transient poses |

Profile and target changes are observed from final admitted state at the combat seam. A change that is
accepted and undone before that seam has no separately observable preparation. Games requiring that
distinction must change their opaque profile generation, not infer it from rendering.

### Attack delays and food

`DelayAttack` remains the one writer. With preparation disabled its contract is unchanged. With an
active attempt it adds the accepted delay to the existing impact deadline, shifts preparation and
strike start by the same amount, and increments revision. The delay does not disappear merely because
the ordinary cooldown has reached zero during an initial preparation. An idle delay still creates
readiness state and constrains a later attack.

Preserve the existing maximum of 255 ticks of outstanding wait. For a live preparation, outstanding
wait includes the scheduled wait until impact. Add only the amount that fits that bound, and shift all
three boundaries by that effective amount. With a cancelled preparation whose retained readiness is
still in the future, add to the later of that retained boundary and the ordinary cooldown boundary.
Cancelling before eating must not make the food delay free. With neither a live attempt nor retained
future readiness, keep the existing byte cooldown arithmetic, including idle-before-fight behavior.
Zero is a no-op. Never wrap. The authoritative getter must account for whether this
tick's cooldown decrement has run, so callers never reproduce `TickCount + remaining - 1` themselves.
Calls before the combat pass affect that pass. Calls from resolved-outcome callbacks affect the next
attempt and cannot revoke an already resolved hit.

For example, an impact at 103 delayed by three before it resolves moves to 106, with draw start 103
and strike start 105. Retire the old revision's visual preparation, ease back to carry during any
time before 103, then start the new two-tick draw at 103. Never slow or freeze the raised arm across
the added wait. The active attempt keeps its ID and gains a revision, rather than emitting a terminal
cancellation and accidentally ending the fight.

If a one-tick delay arrives during the strike, the new deadline gains exactly
one tick, not three. There may be insufficient time to replay a full draw. The client blends to the
revised current stage when its new preparation start is already in the past, and never issues the old
impact. Repeated accepted delays add, subject to the same cap. A late message gets the same bounded
catch-up rule, never an extra delay to the authoritative impact.

An idle bite has no scheduled impact to shift. Its existing attack-ready boundary can overlap the
new preparation: if an attack begins at 100 and food readiness is 105, prepare at 102 and hit at 105.
If readiness is 101, prepare at 100 and hit at 103. The first case adds no three-tick tax after the
bite's wait, and the second case absorbs the short remaining wait inside initial preparation. This
overlap interpretation is an explicit orchestrator ruling for written-spec review.

Grimhollow keeps the pending-food-request visual hold. Its accepted reply must identify the request
generation and the authoritative schedule identity/revision, or explicitly report no active schedule,
alongside readiness. Release the hold only after applying the corresponding or newer schedule state.
A terminal for that identity or a later replacement also satisfies that fence, so a cancelled schedule
cannot leave the food hold waiting forever. A no-active reply uses its authoritative server tick as
the fence against preparation snapshots. Disconnect clears the request and fence.
A refusal releases only its own pending hold. `AttackReadyTick` can no longer suppress preparation
until impact, because the new preparation intentionally precedes that tick. A confirmed combat event
always wins over a still-pending visual hold.

## 6. Deterministic wire and interest

Reserve two new server frame tags, proposed values 4 and 5, verified free at the base commit. Confirm
availability when implementing. All new multi-byte integers are explicitly little-endian, with no
native-layout serialization, floats, strings or padding. Existing legacy tag 3 remains byte-for-byte.

Both frames use this 16-byte header:

```text
[tag:u8][schema:u8=1][serverTick:i64][chunkIndex:u16][chunkCount:u16][recordCount:u16]
```

Tag 4 is a full snapshot of active preparations visible to that viewer. Its 50-byte records are:

```text
[attacker:i64][target:i64][attackId:u64][revision:u32][presentationKey:u32]
[prepareTick:i64][impactTick:i64][strikeTicks:u8][cadenceTicks:u8]
```

The lead is `impactTick - prepareTick`. Strike start is `impactTick - strikeTicks`. Active records are
sorted by attacker net ID. A record is visible when either participant is in the served interest set.
It carries no health, equipment or private game data. The game chooses an opaque public presentation key.
Use authoritative owner state when collecting schedules, so the existing border-ghost sync point does
not add a tick to a schedule announced after movement.

Full state was chosen over start-only events because observers can enter interest during a preparation.
It also makes absence an authoritative end to cached state. The cost is 50 bytes per visible active
attempt per tick, plus headers, about 300 bytes per second at six ticks per second. Delta compression
is deferred until measured traffic justifies its extra baseline and recovery state. Disabled consumers
pay no preparation traffic.

Tag 5 contains this tick's terminal records, each 46 bytes:

```text
[attacker:i64][target:i64][attackId:u64][revision:u32][presentationKey:u32]
[impactTick:i64][terminalKind:u8][reason:u8][amount:u16][hitKind:u8][flags:u8]
```

The terminal kind is 1 for resolved and 2 for cancelled. Resolved records use reason 0, the event's
amount and kind, and landed/killed flag bits 0 and 1. Cancelled records zero amount, hit kind and flags.
Cancellation reasons are 1 disengaged, 2 target changed, 3 profile changed, 4 participant unavailable,
5 permission revoked, 6 illegal reach, 7 teleport, 8 invalid profile, 9 rules unavailable.
Reason 9 is a cancelled terminal with zero amount, hit kind and flags, using the same 46-byte record.
The header tick is the terminal
transition tick. `impactTick` retains the intended deadline for cancellations and equals the header
tick for a resolution. Terminal records for an attacker retain transition order, with attackers
ordered by net ID for cancellations and the existing roll order for resolved events. Send cancellations
before resolved records for that tick. A replacement and resolution cannot resolve the same attack ID.

Tag 4 uses one or more chunks of at most 255 records and sends one empty chunk when the visible set
is empty. Tag 5 uses the same cap and is omitted when empty. Frames use `ReliableOrdered`, following
the movement snapshot, then all tag 4 chunks, then tag 5 chunks. Chunks must not be interleaved.
Bound assembly to the declared count, at most 256 chunks and 65,280 records per frame set. Reject an
over-budget visible set with a named server diagnostic before encoding, rather than emit a partial
snapshot that would falsely cancel omitted actors. Validate configured actor/viewer budgets at boot.

Decoders reject unknown schemas, tags, flags, reasons, impossible tick relationships,
zero identities, invalid chunk indices/counts, duplicate attackers in a state set, and any length
mismatch. Validation is atomic. Never publish valid prefixes of malformed sets. Clear assembly on
disconnect or protocol failure. Oversized/malformed input must be bounded and non-throwing at the decoder.
Tick relationships include nonnegative server and preparation ticks, positive lead, valid strike span,
lead no greater than cadence, and impact strictly after the header tick for an active state record.
Snapshot construction occurs after resolution, so a due attempt cannot remain active in that snapshot.

The client atomically replaces the visible active set only after complete tag 4 assembly. Absence
retires its old active sample. Retiring an active sample is separate from marking its terminal record
delivered. Tag 5 still supplies the reason or actual outcome after that retirement.
Interest entry therefore gets a current schedule, not a dependency on having seen its start. Terminal
delivery is filtered by current participant interest or by a preparation visible to this viewer on the
previous serve, so leaving interest on the terminal tick cannot strand a cached attempt.

Track last applied server tick, active identities and terminal identities. Apply tag 4 before tag 5
at the same tick, and permit all declared tag 5 chunks at that tick. An older frame cannot
restore an ended attack. Duplicate terminal delivery never repeats a hit, sound or cancellation.
Keep per-attacker highest retired ID and bounded current revision state rather than an unbounded event
history. Clear on remote lifecycle removal and disconnect. A terminal received after interest was
lost does not recreate a drawable body. Reliable ordering remains required, with stale-message tests
covering delayed dispatch and defensive deduplication.

## 7. Client clock, phase and impact beat

Add a headless `CombatPresentationTick` read to `TileWorldClient`. Anchor it to the newest applied
server snapshot tick, then advance by elapsed presentation seconds divided by configured tick length,
capped at one tick beyond that anchor. A new snapshot advances the anchor. Older snapshots do not
rewind it. This is a presentation estimate, not a simulation clock or a new movement timeline.

A pure preparation sampler accepts a schedule and fractional tick and returns Hold, Prepare,
Strike or AwaitingOutcome plus progress within that stage. A sample before `prepareTick` is Hold.
It prepares up to strike start, strikes up to impact, then waits for the outcome. It emits no event
and mutates no gameplay state. Game pose mapping keeps the final contact pose for the authoritative
event, stopping just short of contact while awaiting an outcome.

The outcome's frame places the attack at impact and starts the hitsplat, health-bar trigger, shield
response, impact sound and death presentation together after the poll batch is drained. A late
result is presented once on receipt. Do not buffer it to manufacture a full wind-up. A cancellation
produces none of these impact effects. Recovery is game-owned and begins only after a real outcome
or as an explicit aborted-motion recovery. The client never loops a missing outcome into another attack.
An outcome's impact and recovery temporarily own the action over the next attempt's Hold sample, which
can already be present in that frame's schedule snapshot. Recovery duration is game-owned and uses the
outcome's presentation key. A subsequent preparation takes over smoothly if recovery overlaps it.

Preparation samples for local and remote attackers use this combat clock, preserving the existing
event feedback beat. Remote movement retains its separate delayed presentation timeline. A remote
pose and combat phase can therefore disagree spatially. This is measured during adoption, not hidden
by delaying only its hitsplat or by changing server reach to read its picture.

For a newly received schedule already in progress, enter its current stage with a short game-owned
blend from the displayed pose. For a revised schedule, blend from the displayed pose toward its new
sample, avoiding an instant rewind to carry. Never extend the server deadline for that blend. If
impact arrives before the blend finishes, the confirmed impact wins. Several ticks arriving in one
poll may skip preparation entirely. Network latency, jitter, stalls and late interest entry make a
full remote visual lead impossible to guarantee. Ordinary stable delivery should show the scheduled
lead, and bounded late-delivery behavior is tested separately.

## 8. Compatibility, implementation boundaries and release

This is additive API and opt-in wire behavior. The default path preserves existing consumers' timing
and tag 3 event delivery. The enabled server sends prepared results only as tag 5, without a duplicate
legacy event. Matching enabled clients subscribe to the new callbacks. New client parsers understand
both frame families, but a game session selects one mode at connection setup.

The game's connect protocol must distinguish preparation-enabled sessions from old clients. Grimhollow
must bump its `ProtocolVersion` during adoption and verify mixed pairs are refused at the door.
Do not rely on an old decoder silently ignoring new tags. The engine release notes and package README
must call out the opt-in requirement and the separate callback contract. Select the engine version from
current main and tags during implementation, using the repository's additive-minor rules. This spec
does not reserve a version or change the current package version.

Implement scheduling, wire assembly and sampling in separate cohesive types. The server combat partial
retains its roll/apply responsibilities and calls the scheduler at its existing seams. Do not grow a
monolithic combat partial or raise the file-size baseline as a convenience. No rendering dependency
is added to tile netcode. Update the package README and USING reference only when the API ships.

After approved implementation and verification, the controller reconciles engine main, completes the
required package checks, and performs the authorized release through `scripts/tag-release.sh` under
the current release rules. Grimhollow then adopts that released pin, updates both local tools, refreshes
its vendored packages, records the swept range and verifies the game. No game feature ships against
unreleased engine bytes. This design-writing task performs none of those actions.

## 9. Grimhollow adoption contract

Enable `LeadTicks=3` and `StrikeTicks=1` for all combatants. Each style defines its own slow preparation
and fast strike poses, including punch and headbutt. Map public presentation keys to the game-owned
style and visible equipment identity. Capture the scheduled profile so a late event is not drawn with
whatever weapon happens to be equipped when it arrives.

Replace event-seeded cadence prediction in `HollowmereSession.Actions` with the schedule sampler.
Carry its stages through `AvatarMotion` and `StrokeSamples`. Retime both `slash` and `slash-moving`,
and the procedural counterparts, to the same two-tick draw and final-tick strike. Preserve recovery,
shield layering, the gait-aware companion and additive standing footwork. Different weapon cadences
change the hold duration, not the three-tick preparation. Clip phase mapping must be explicit and
shared between the procedural and authored paths, with no independent `.88` prediction gate left.

The new owner request supersedes the prior no-windup rule in
[Grimhollow GAMEPLAY-RULINGS.md, combat section](https://github.com/APKiwiOrg/Grimhollow/blob/main/docs/design/GAMEPLAY-RULINGS.md#combat-actors-and-persistence)
(line 270 at the investigation base) and the no-windup remarks in
[AttackSwing.cs](https://github.com/APKiwiOrg/Grimhollow/blob/main/Grimhollow.Core/Client/AttackSwing.cs)
(lines 35 to 53). Update these, the architecture explanation, clip authoring docs and affected tests
in the same adoption change. The old remarks must not survive as an apparently binding counter-rule.

[Grimhollow #371](https://github.com/APKiwiOrg/Grimhollow/issues/371) is an acceptance case. A first
run-up hit previously appeared while the target was displayed two metres away despite authoritative
adjacency. The new first preparation gives a two-tick run glide time to finish in the ordinary local
case. It does not prove every contact correct. A four-tick walk, prediction correction, moving target
or remote interpolation can still leave a gap. Measure rendered separation and weapon contact at
impact for these cases. Do not close #371 on timing assertions alone, stretch reach, teleport the body
or retime movement to make an image pass. Any remaining spacing correction needs a separate reviewed
design, and the release report must name the measured limitation.

## 10. Verification matrix

All engine behavior tests live in `KhaozEngine.TileWorld.Netcode.Tests` and run headlessly in Release.
Use fixed rules and a counted RNG/roll seam. No test should require an engine or consumer window.

| Area | Required evidence |
| --- | --- |
| Default compatibility | Existing combat, delay, admission, interest and wire tests unchanged with null configuration. No new state/frame cost in the disabled path |
| Rules availability | Enabled with null `CombatRules` and no attempt creates no schedule or roll. Null during a live preparation cancels once at the next pass, including its due tick, with reason 9 and no roll or new cooldown charge. Restore rules before and after retained readiness expires, requiring a fresh lead and new ID. Disabled mode retains existing null behavior. Reason 9 round-trips without changing wire widths |
| First attempt | Ready/in-range at T, no roll at T, T+1 or T+2, exactly one at T+3. Stand, run approach, actor and player cases |
| Continuing cadence | Impacts H and H+C, prepare H+C-3, strike H+C-1. No extra three-tick tax. Miss and zero-damage hit consume cadence |
| Profile validation | Other tick rates and legal leads, lead equal to cadence, invalid durations and fallback cadence. Invalid profile cannot produce an instant hit |
| Target/profile identity | Same-target spam stable. Retarget and weapon/style/cadence changes retire old ID, respect readiness and start fresh. Revision and ID exhaustion never wrap |
| Reach and motion | Temporary lost range retains attempt. Illegal reach at impact cancels without roll. Later legal opportunity prepares afresh. Pursuit, plane, collision and footprint rules remain authoritative |
| Interruptions | Walk, steering, interaction, permission revocation, teleport, despawn and disconnect cancel at the documented seam. No cancelled outcome or RNG draw |
| Death and order | Mutual kills, several attackers, kill on earlier tick, kill on the due tick, post-roll callback delay, and region handoff retain deterministic outcomes |
| Delay | First preparation with ordinary cooldown zero, during hold/draw/strike, idle-before-fight with wait shorter/longer than lead, repeated/zero/saturated delay, both sides of decrement and callbacks. Pending deadline gains only effective accepted delay |
| Readiness API | Absolute deadline consistent before and after decrement, after cancellation, after a callback and with no active attempt. Consumer never repeats tick-offset arithmetic |
| Protocol | Golden little-endian bytes, both frame families, all malformed fields and lengths, chunk boundaries, empty state, bounded assembly, no partial application and byte-for-byte legacy frames |
| Interest and stale traffic | Join mid-hold/draw/strike, actor-only or target-only visibility, crossing cells, leave on terminal tick, cancelled/replaced schedules, duplicate and stale dispatch, reconnect and multiple catch-up ticks |
| Client sampler | Boundary values, fractional frames, monotonic bounded clock, late/revised schedule, outcome after deadline and no outcome. Never invent damage or a second swing |

Grimhollow's separate adoption tests must drive the real server/client loopback harness for both local
and observing clients, all weapon archetypes, unarmed players and creature styles. Assert exact server
ticks independently of visual phase. Cover food replies and requests crossing revisions, equipment
changes, killing recovery, shield combinations and held movement. Assert exactly one impact/audio/splat
set per real result and zero for cancellation.
For food revision, assert carry before the revised preparation start, a fresh two-tick draw and
one-tick strike when time permits, no arm-held wind-up through the added wait, and current-stage
catch-up when the revision arrives after its start. Cover both idle-delay overlap examples above.

Extend existing first-hit transition tests to include frames before the preparation, not only the
impact and recovery. Measure weapon-hand/blade continuity, body clearance, sole continuity, gait source
blends and contact at the event. Retain seven real run approaches, half-frame samples and the blend
sweep. Update stationary clip goldens and add phase samples for draw start, draw finish and strike.
Visual review compares normal-speed local and remote sequences with timestamps and true/drawn tiles.
The owner approves the two-tick draw and one-tick cut for each style. Use headless capture where it
proves the case and one final authorized bridge proof for pixels/window behavior under game rules.

## 11. Review and completion gates

Before planning, the owner reviews the gameplay cost and all proposed orchestrator rulings in section 2.
In particular, confirm all-attacker scope, unchanged continuing cadence, transient range retention,
idle-delay overlap, and exact additive food delay for an existing attempt even when a full revised
visual preparation is unavailable.

The implementation is complete only when the opted-in rules and default compatibility pass their
engine matrix, public documentation is current, the released package is adopted by Grimhollow, the
game's full required verification passes, and the owner has reviewed its changed swing timing. A
remaining #371 spacing limitation is reported explicitly with evidence and its reviewed disposition.

Self-review for this draft checks the following: no double cadence charge, no early first roll, no
client damage prediction, no guarantee of full lead under arbitrary latency, no food delay lost while
cooldown is zero, no stale identity resurrection, no default-consumer behavior change, and no hidden
movement or reach change. Written-spec approval is the next gate. Implementation planning follows it.
