# Tile combat preparation: deferring an out-of-reach impact in pursuit

Status: proposed on 2026-09-30 for owner review. The owner chose the fix direction, which is to defer the
impact instead of ending it. This document decides the details. Design base: engine `800c132d9`, released
20.15.0. Implementation plan:
[2026-09-30-preparation-pursuit-deferral.md](../superpowers/plans/2026-09-30-preparation-pursuit-deferral.md).

This amends the [tile combat preparation design](TILE-COMBAT-PREPARATION-DESIGN-2026-09-28.md). It replaces
these statements there:

- Section 2, the ruling "Recheck legal reach at impact. An invalid attempt ends without damage" and the
  paragraph that says keeping the attempt "preserves pursuit".
- Section 4, rule 4, for the legal reach check only.
- Section 5, the table row "Illegal reach or plane at impact".
- Section 6, the active state record rule "impact strictly after the header tick".
- Section 10, the "Reach and motion" row.

Everything else in that design stands.

## 1. The measured problem

The shipped rule schedules impact `LeadTicks` after the first tick where the attacker is ready and in
legal reach. It keeps the attempt through range loss during the lead, rechecks legal reach at impact,
and ends the attempt `IllegalReach` when reach is illegal on that one tick. A later legal tick prepares
afresh.

The Grimhollow pursuit measurement compared 20.15.0 with preparation against the same game without it,
through the real server and client loopback, over four deterministic phases per scenario.

- In equal-speed tile pursuit the target commits its next tile on the tick its step starts. On that tick
  the bodies are two tiles apart in line, which is illegal reach. The pursuer's follow commits its own
  step on the next tick and restores reach. The illegal gap is one tick, whatever the gait period. At walk
  (four ticks per step) one tick in four is illegal. At run (two ticks per step) every other tick is.
- When an impact falls on an illegal tick, the attempt ends. The next attempt prepares on the next legal
  tick, and its impact `LeadTicks` later falls on the next illegal tick. With a three-tick lead this is
  absorbing for as long as the target keeps moving: a legal tick plus three is the next commit tick at
  walk, and at run a legal tick plus any odd lead is illegal.
- Landed outcomes per 60 ticks, mean over four phases, before and after preparation:

| Scenario | Before | After | After, worst phase |
| --- | --- | --- | --- |
| Player walks after a walking goblin, cadence 14 | 3.91 | 2.50 | 0.62, one landed then locked |
| Goblin walks after a walking player, cadence 16 | 3.59 | 2.81 | 0 |
| Goblin runs after a running player, cadence 16 | 3.59 | 0.94 | 0 in three of four phases |

- Before preparation, an attack rolled on the first tick it was ready and in reach. When the ready tick
  was illegal it waited one tick. It never ended an attempt, and it cost at most one tick per swing.

The original design rejected cancelling on every out-of-range tick, and keeping the attempt during the lead
does work. The gap is that the loss that matters in pursuit is not in the middle of the lead. It sits on a
fixed tick of the target's step clock, and a fixed lead can land on exactly that tick forever.

## 2. Decision summary

- When an attempt's impact tick arrives and every validity check passes except legal reach, the attempt
  is kept. It resolves on the first later tick with legal reach, rolling exactly once.
- The deferral is bounded by the attempt's own `StrikeTicks`. An attempt that has waited that long and
  still lacks legal reach ends `IllegalReach`, as today, with no roll and no cooldown charge.
- Only legal reach defers. A plane mismatch is part of legal reach and defers the same way. Every other
  reason still ends the attempt immediately.
- The deferral is silent. The replicated schedule keeps its identity, revision, preparation tick and
  impact tick. The client keeps sampling `AwaitingOutcome` until the terminal arrives. No revision is
  emitted, so no consumer redraws the strike.
- The wire layout does not change. The active state record's validity rule relaxes so a schedule may be
  overdue by fewer than its `StrikeTicks`. Default consumers are unaffected.
- The next attempt's impact is the actual resolution tick plus the cadence, as before preparation.
- No new public API. This is a behavior fix, proposed as patch 20.15.1.

## 3. Deferral semantics

Let `H` be the attempt's scheduled impact tick, `S` its `StrikeTicks`, and `t` the current combat-pass tick.
Before `H` nothing changes: range loss during the lead is retained and reach is not checked.

At `t >= H` the server checks, in this order, and the first failing check decides:

| Order | Check | Result when it fails |
| --- | --- | --- |
| 1 | `CombatRules` present | End `RulesUnavailable` |
| 2 | Attacker alive | End `ParticipantUnavailable` |
| 3 | Lock names the same target | End `TargetChanged` |
| 4 | Target alive and present | End `ParticipantUnavailable` |
| 5 | Lock held | End `Disengaged` |
| 6 | Both teleport epochs unchanged | End `Teleport` |
| 7 | `CanAttack` permits it | End `PermissionRevoked` |
| 8 | Profile timing valid | End `InvalidProfile` |
| 9 | Profile unchanged | End `ProfileChanged` |
| 10 | Continuity: when `t > H`, the attempt was deferred on `t - 1` | End `ParticipantUnavailable` |
| 11 | Legal reach on committed tiles | Defer when `t - H < S`, otherwise end `IllegalReach` |

Checks 1 to 9 are the existing checks in their existing order, so every non-reach invalidity ends the
attempt on the tick it is observed, including during a deferral. A non-reach failure on the same tick as
lost reach reports the non-reach reason.

Check 10 preserves the existing rule that an attempt not present for its deadline cannot become a late
hit. Today any `t > H` ends `ParticipantUnavailable`. With deferral, a late tick is legitimate only when
the attempt was deferred on the previous pass. An owner that missed a pass, for example in transit,
still ends as today. The server records the tick of the most recent deferral in the migration-only
preparation state, so the rule survives an in-process region handoff.

A deferred tick makes no roll, no RNG draw, no cooldown write, no combat stamp, no award and no terminal.
It does not create a replacement attempt. A deferred attempt that reaches legal reach joins the ordinary
roll order by its lock's age and net id, and it survives damage from another roll on that tick exactly as
an on-time attempt does.

### Why a plane mismatch defers

`TileReach` owns the plane rule. Reach never crosses planes, and the engine has no plane-specific reason
code. The shipped table already folds "illegal reach or plane" into one `IllegalReach` outcome. A pursuit
over a walked plane transition, such as a stair, lags by the same single tick as any other step, so it
needs the same deferral. A target that really stays on another plane ends at the bound as `IllegalReach`.
A plane change that bumps a teleport epoch still ends immediately as `Teleport` under check 6. Splitting
plane out would add a reason code and a wire change for no measured case.

## 4. The bound

A bound is needed so a target that truly escapes does not leave the attacker frozen at the end of its
strike, and so a blow that has not been telegraphed recently cannot land long after the visible strike.
The before-preparation build needed no bound because it had no visible commitment to honour.

| Option | For | Against |
| --- | --- | --- |
| Engine constant, such as 1 or 2 ticks | Simplest. The wire can validate it | Ignores the game's tick rate and motion. The hold a game can tolerate depends on its strike, not on the engine |
| Derived from `LeadTicks` | Game-owned, already on the wire record | Holds the finished strike as long as the whole wind-up. With a four-tick lead that is 0.67 s frozen at contact at six ticks per second, which the original design's no-frozen-arm ruling for food rejects in spirit |
| Derived from `StrikeTicks` | Game-owned and already published content for the first consumer. On the wire record, so the decoder can validate an overdue record exactly. The hold is at most the strike's own length. Always at least one tick, which covers the structural one-tick pursuit gap | Not separately tunable from the strike duration |
| New profile field | Fully tunable | Public API growth, a source break for positional construction of the profile record, new game content, and a value the wire record does not carry, so the decoder could only validate a loose bound |

**Decision: the bound is the attempt's captured `StrikeTicks`.** The attempt may resolve on any tick
from `H` through `H + S`, and it spends at most `S` ticks deferred in `AwaitingOutcome`. It ends
`IllegalReach` on tick `H + S` if reach is still illegal.

The equal-speed pursuit gap is one tick at both gaits because the follow commits one tick after the
target, independent of the step period. Any `S >= 1` covers it. When both bodies start moving from a
stand at run, the measurement showed two consecutive illegal ticks once.

| Profile | Bound | Steady walk pursuit (period 4) | Steady run pursuit (period 2) | Run start from a stand |
| --- | --- | --- | --- | --- |
| Current published 3/1 | 1 tick | Covered | Covered | One attempt can end. The next prepares on a legal tick, its odd lead lands on an illegal tick, defers one and resolves |
| Moving to 4/2 | 2 ticks | Covered | Covered | Covered |

A future game needing a different bound can add an optional profile value in a later minor release,
defaulting to `StrikeTicks`. Nothing here forecloses that.

## 5. Server tick contract, as amended

Section 4 of the base design changes only as follows.

- Rule 4 becomes the ordered checks of section 3 above. At `t >= H` a valid attempt with cooldown zero is
  due. An attempt lacking only legal reach is deferred while `t - H < S`.
- A due attempt at `t > H` resolves through the same roll, apply and death phases. Its resolved outcome
  carries `t` as its impact tick.
- An ended attempt at `t >= H` still cannot be replaced in the same pass. Reconsider on a later tick.
- Readiness: while an attempt is deferred, `TryGetAttackReadyTick` returns the current tick, because the
  attempt resolves on the first tick with legal reach. The retained boundary after an expiry is the old
  `H`, which is already in the past and imposes no wait.

Worked example, the measured walk lock (player cadence 14, profile 3/1, phase 1 of the measurement,
ticks relative to the target's first commit). The target commits on relative ticks 0, 3, 7, 11, 15 and
onward, and those ticks are illegal.

| Tick | Shipped | Amended |
| --- | --- | --- |
| 1 | Lands. Attempt 2 prepared with impact 15 | Same |
| 15 | Illegal. Attempt 2 ends | Illegal. Attempt 2 deferred |
| 16 | Attempt 3 prepared with impact 19 | Attempt 2 lands. Attempt 3 prepared with impact 30 |
| 19 | Illegal. Attempt 3 ends, and so on every four ticks | Holding |
| 30, 44, 58, 72, 86 | Nothing lands | Lands each time, all legal |

That is seven landed outcomes in the 96-tick window instead of one, against six before preparation.

Worked example, the measured run lock (goblin cadence 16, profile 3/1, phase 1). Illegal ticks are 0, 1
and every odd tick after. Attempt 1 has impact 1 and is deferred. It lands on tick 2. Every following
impact is 2 plus a multiple of 16, which is even and legal, so the goblin lands six times in the window,
as it did before preparation.

## 6. Cadence after a deferred impact

The scheduler already bases the next attempt on the resolution tick. On a resolved roll the attacker's
cooldown is set to the captured cadence `C` on that tick, and completion retains readiness at
`tick + C`. The successor's impact is `max(t + LeadTicks, t + C, retained readiness)`, which is `t + C`
because `LeadTicks <= C`. With `t` the actual resolution tick, the next impact is `t + C`, matching the
before-preparation behavior, which reset the cooldown on the tick it actually rolled.

The kernel's completion transition currently accepts only `tick == ImpactTick`. It must accept a tick in
`[H, H + S]` when the attempt was deferred on the previous tick. It keeps using the actual tick for both
the outcome's impact tick and the retained readiness.

A deferral therefore costs at most one tick per swing in steady equal-speed pursuit, the same cost the
before-preparation build paid. After one deferral the impact settles on a legal phase for even cadences
at run and for cadences of 14 or 16 at walk, as the worked examples show.

## 7. Presentation

### Options considered

**Silent deferral.** The server keeps the attempt unchanged. Its record stays in every full state set with
its original identity, revision, `PrepareTick` and `ImpactTick`, now in the past. The client's
`TileCombatPreparationSampler` already returns `AwaitingOutcome` with progress one for any time at or
after `ImpactTick`, so the strike holds just short of contact. The resolved terminal arrives with the
same identity and revision.

**Explicit revision.** On each deferred tick the server revises the attempt to a new impact one tick later.
To keep the published lead it must shift `PrepareTick` too, so the sampler moves back into `Strike`. With a
3/1 profile the strike rewinds to its start. With 4/2 it rewinds half way. Every deferred tick also spends
a revision, and consumers already treat a revision as a new motion. The first consumer's presentation
controller returns to carry and draws again on a revision, which is right for a food delay and wrong here.
Keeping `PrepareTick` fixed and only extending the impact instead changes the lead, which the server
reads as a profile change, and breaks the lead-within-cadence rule when lead equals cadence.

**Explicit deferral flag or deferred impact field.** This needs a new byte in the 50-byte state record,
a schema change, a new public field and consumer code to read it. It buys nothing the silent option lacks.

**Omitting the record while deferred.** The client treats absence as retirement, so the first consumer's
controller would fade the strike back to carry before the outcome arrives. Absence without a terminal
also looks like interest loss.

**Decision: silent deferral.** It keeps an in-flight strike on screen without a restart, emits no revision
and adds no field.

### Wire validity

The one blocker for silent deferral is the active state record rule. Today a record is valid only when
`ImpactTick > serverTick`. The encoder refuses an overdue record and the decoder and assembler reject it.
The rule changes to:

```text
serverTick - ImpactTick < StrikeTicks
```

With the existing guarantees that `serverTick >= 0` and `ImpactTick > PrepareTick >= 0`, the subtraction
cannot overflow. Before `H` the left side is negative, so an on-time schedule is valid exactly as before.
At the serve of tick `t` a deferred schedule has `t - H` from 0 to `S - 1`. At `H + S` it has either
resolved or ended, so it is never served beyond the bound. The client ledger's defensive stale-sample rule
uses the same predicate. The pure sampler validates against the schedule's own `PrepareTick` and is
unaffected. The layout, schema 1, tags 4 and 5, record sizes and reason codes are unchanged. Resolved
terminals keep the rule that their impact tick equals the header tick, which is the resolution tick.
Cancelled terminals already accept a past intended impact.

### What a client observes

For a deferral that resolves at `H + k`, with `0 < k <= S`:

1. State sets through tick `H - 1` carry the schedule with impact `H`, as today.
2. State sets for ticks `H` to `H + k - 1` carry the same record unchanged. `CombatPreparationsChanged`
   fires for each. `TryGetCombatPreparation` returns the same value. Sampling at or after `H` gives
   `AwaitingOutcome`.
3. Tick `H + k` carries no record for that attempt, or the successor with impact `H + k + C`, then one
   resolved terminal with the same attack ID, revision and key and impact tick `H + k`.
   `PreparedCombatEvent` fires once.

For an expiry, tick `H + S` carries no record for the attempt, then one cancelled terminal with reason
`IllegalReach`, header tick `H + S` and intended impact `H`. `CombatPreparationEnded` fires once with
`ServerTick` `H + S` and `ImpactTick` `H`.

An observer entering interest during a deferral receives an already overdue schedule and samples
`AwaitingOutcome` immediately. The base design already allows late interest to skip the preparation.

### What a consumer must change

A consumer that follows the documented contract needs no code change. It must not assume that:

- an active schedule's `ImpactTick` is in the future,
- a `PreparedCombatEvent.ImpactTick` equals the schedule's `ImpactTick`, since it is the resolution tick
  and can be up to `StrikeTicks` later,
- an `IllegalReach` cancellation arrives on the impact tick, since it arrives `StrikeTicks` later.

The first consumer's controller was checked against these. It compares whole schedule values, so an
unchanged record causes no transition, and it matches outcomes by attack ID and presentation key, not by
impact tick. Its food reply fence matches state evidence by identity, revision and impact tick. A deferred
schedule is present in the state set with its original impact tick, and an accepted food delay during a
deferral revises it, so the fence is satisfied either way. Matching terminal evidence by identity and
revision alone would be a sturdier fence, and is recommended but not required. Its tests that expect
`IllegalReach` on the impact tick move to the impact tick plus `StrikeTicks`.

## 8. Interactions

- **`DelayAttack` and `TryDelay`.** A delay during a deferral revises the attempt from the current tick:
  the new impact is the current tick plus the accepted delay, all three boundaries shift together, the
  revision increments and the deferral record clears. The consumer sees an ordinary food revision, which
  is correct because food adds a real wait. If reach is illegal at the new impact, the bound restarts from
  that impact. The 255-tick outstanding-wait cap is unaffected because a deferred attempt has no remaining
  scheduled wait. A delay from an outcome callback still affects only the next attempt.
- **Cancellation.** Disengaging during a deferral ends `Disengaged` on that tick, with the intended impact
  `H` retained on the terminal.
- **Retargeting.** A new target ends `TargetChanged` on that tick. Because the attempt is already due, the
  existing rule applies and the replacement prepares on the next tick, not in the same pass.
- **The attacker moving.** Automatic pursuit keeps the lock, so the attempt is kept. That is the case this
  change exists for. A local walk, steering or interaction that clears the lock ends `Disengaged`.
- **The target dying.** A target killed during a deferral, including by another attacker's roll on the same
  tick, ends the attempt `ParticipantUnavailable` in the post-roll recheck, before the serve. A deferred
  attempt that became legal and was admitted to the roll on that tick keeps its roll, as today.
- **Region handoff.** The deferral tick migrates with the rest of the preparation state. A handoff that
  keeps the owner in every combat pass keeps the deferral. One that misses a pass ends it under check 10.
- **Burst delivery.** Reliable ordered delivery of full state sets then terminals is unchanged. Several ticks
  applied in one poll repeat the same record, then deliver the terminal once. The ledger's terminal
  deduplication and previous-serve visibility are unchanged, so an attacker leaving interest on the
  resolution tick still delivers its outcome.
- **Determinism.** The decision reads committed tiles, the attempt's captured fields and the tick. It draws
  no RNG and iterates no hash collection. Roll order is still oldest lock then net id. The eventual roll
  draws exactly once.

## 9. Protocol and version impact

This is a behavior change of the opt-in preparation mode, with one relaxed wire validity rule and no layout
change. The disabled path, legacy tag 3, public signatures and reason codes are unchanged.

A 20.14.1 to 20.15.0 decoder rejects a state set containing an overdue record. It would count a rejected
frame and keep its previous cache for that tick, freezing every visible schedule for that viewer. Mixing
those clients with a new server is therefore unsupported. The base design already requires matched
preparation modes behind the game's connect protocol string, and a game ships its client and server
together. A game that has already shipped preparation-enabled clients must bump its connect protocol
string when adopting this release. The first consumer's adoption is unreleased and already bumps its
protocol string, so adopting this release inside that change needs no further bump.

Under the repository's rule of additive minor, fix patch and breaking major, this is a fix with no API
addition, so it is proposed as patch 20.15.1 above the released 20.15.0. The implementer re-reads
`origin/main`, the version and the tags before choosing, per the release rules.

The CHANGELOG entry must say:

- A prepared attack whose target is out of legal reach on its impact tick now waits for legal reach,
  for up to its `StrikeTicks`, and resolves once on the first legal tick. It still ends `IllegalReach`
  with no roll or cooldown charge when the bound passes. This fixes equal-speed pursuit, where an impact
  could fall on the target's step tick every time, measured by a consumer at up to a 74 percent loss of
  landed attacks.
- Only legal reach, including the plane, defers. Every other invalidity still ends the attempt at once.
- The schedule keeps its identity, revision and past `ImpactTick` while deferred, and the sampler reports
  `AwaitingOutcome`. `PreparedCombatEvent.ImpactTick` is the resolution tick. An `IllegalReach`
  cancellation arrives `StrikeTicks` after the intended impact. The next impact is the resolution tick
  plus the cadence.
- `DelayAttack` during a deferral revises the attempt from the current tick. `TryGetAttackReadyTick`
  reports the current tick while deferred.
- An active state record may now be overdue by fewer than its `StrikeTicks`. An older decoder refuses
  such a set, so a game that shipped preparation-enabled clients must bump its connect protocol string.
  Default consumers are unaffected.

## 10. Verification

All engine tests are headless, in `KhaozEngine.TileWorld.Netcode.Tests`, Release. The plan names each test.

| Area | Required evidence |
| --- | --- |
| Pursuit lock reproductions | Engine-level fixtures of walk and run pursuit with deterministic phases 0 to 3, profiles 3/1 and 4/2, cadences 14 and 16. Before the fix the 3/1 rows must show the lock. After it, no `IllegalReach` ends while the target keeps moving, every interval between resolutions is from the cadence to the cadence plus `StrikeTicks`, every roll is in legal reach, and landed outcomes summed over the four phases equal or exceed the same fixture with preparation disabled |
| Escaping target | A target leaving reach for good ends `IllegalReach` exactly `StrikeTicks` after the impact, with no roll or cooldown charge, and a returning target needs a fresh lead |
| Non-reach invalidity | Disengage, retarget, profile change, invalid profile, permission, attacker or target death, teleport and rules unavailable each end a deferred attempt on the tick observed, with their own reason, and win over lost reach on the impact tick |
| No out-of-reach roll | No roll, RNG draw, cooldown change, terminal or revision on any deferred tick. Every roll in every deferral test is in legal reach |
| Plane | A target on another plane at impact defers and resolves if it returns within the bound |
| Kernel | Deferral inside the window only, late completion only after an unbroken deferral, readiness from the resolution tick, delay of a deferred attempt |
| Wire | Overdue records valid below `StrikeTicks` and invalid at it, in the encoder, decoder, assembler and serve, with unchanged golden bytes |
| Migration | The deferral tick migrates and survives a real handoff |
| Client sequence | The observed state, sample, callback and terminal sequence in section 7, including expiry, burst delivery and interest entry mid-deferral, with zero rejected frames |

## 11. Out of scope

No change to reach rules, pursuit pathing, step timing, the lead, the scheduler's first-impact rule,
presentation features or consumer poses. A reach tolerance at impact, a phase-aware impact scheduler and
immediate re-preparation, which the measurement also listed, were not chosen.
