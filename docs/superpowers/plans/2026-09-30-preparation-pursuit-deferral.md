# Preparation Pursuit Deferral Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep a prepared tile attack whose impact tick lacks only legal reach, resolve it on the first later tick with legal reach within its `StrikeTicks`, and end it `IllegalReach` after that, so equal-speed pursuit lands at the pre-preparation rate.

**Architecture:** The pure scheduler gains one bounded transition and a late-completion window. The server's existing validity check defers instead of ending for lost reach only. The replicated schedule stays unchanged while deferred, and the active state record's validity rule relaxes so an overdue record below its `StrikeTicks` is legal. No layout, tag, reason or public signature changes.

**Tech Stack:** C#/.NET, `KhaozEngine.TileWorld.Netcode`, xUnit, in-memory transport.

**Spec:** [TILE-COMBAT-PREPARATION-PURSUIT-DEFERRAL-DESIGN-2026-09-30.md](../../design/TILE-COMBAT-PREPARATION-PURSUIT-DEFERRAL-DESIGN-2026-09-30.md), amending [TILE-COMBAT-PREPARATION-DESIGN-2026-09-28.md](../../design/TILE-COMBAT-PREPARATION-DESIGN-2026-09-28.md). The owner chose option 4 on 2026-09-30 and both were reviewed before Task 1.

**Execution status:** Tasks 1 to 5 complete on `feature/preparation-pursuit-deferral`, released as 20.15.1. Design base `800c132d9`, released 20.15.0.

## Global Constraints

- "Only legal reach defers. A plane mismatch is part of legal reach and defers the same way. Every other reason still ends the attempt immediately."
- "The bound is the attempt's captured `StrikeTicks`." The attempt may resolve on any tick from `H` through `H + S`, and ends `IllegalReach` on `H + S` if reach is still illegal.
- "The deferral is silent." Identity, revision, `PrepareTick` and `ImpactTick` do not change while deferred. No revision, terminal, roll, RNG draw, cooldown write or combat stamp on a deferred tick.
- "An active state record is valid when `serverTick - ImpactTick < StrikeTicks`." Wire layout, schema 1, tags 4 and 5, record sizes, reasons 1 to 9 and legacy tag 3 stay byte-for-byte.
- The next impact after a deferred resolution is that resolution tick plus the captured cadence.
- An overdue attempt at `t > H` stays live only if it was deferred on `t - 1`. Otherwise it ends `ParticipantUnavailable`, as today.
- The disabled path is unchanged. No new public type, member or parameter. XML doc text on existing public members may change.
- Stay inside option 4. No reach tolerance, no phase-aware scheduler, no immediate re-preparation, no pathing, step timing, lead or presentation features.
- Work in the worktree named by the dispatch brief on `feature/preparation-pursuit-deferral`. Stage explicit paths. Preserve unrelated changes.
- Tests live in `KhaozEngine.TileWorld.Netcode.Tests`, namespace `KhaozEngine.Tests.TileNetcode`, and run headless in Release. Zero warnings, no blanket suppression, no file-size baseline increase.
- No em or en dash glyphs and no Markdown prose semicolons. No release tag from an implementation worker. Never loop tests on the shared dev machine.

## Review Focus

1. The post-roll recheck of a tick must not end an attempt that the same tick deferred. Owned by Task 3, `A_deferral_survives_the_post_roll_recheck_of_its_own_tick`.
2. An overdue attempt whose owner missed a pass must still end `ParticipantUnavailable`, not roll late. `CompletePreparedSwing` announces a result even when the kernel's `TryComplete` refuses (`TileWorldServer.Preparation.cs:200-205`), so the server's continuity check is the real guard. Owned by Task 1, `Late_completion_requires_an_unbroken_deferral`, and Task 3, `An_overdue_attempt_that_was_not_deferred_ends_unavailable` and `A_deferral_survives_a_real_region_handoff_and_resolves_in_the_new_cell`.
3. The relaxed wire rule must refuse a record overdue by exactly `StrikeTicks`. Owned by Task 2, `An_overdue_state_record_is_valid_only_below_its_strike_ticks`.
4. A food delay during a deferral must revise from the current tick, never announce a past impact. Owned by Task 3, `Delay_during_a_deferral_revises_from_the_current_tick`.
5. The client must see one unchanged schedule through the deferral, then exactly one outcome with the same identity and the resolution tick. Owned by Task 4, `A_deferred_impact_keeps_one_client_schedule_until_its_result`.

---

## Execution and file boundaries

Run tasks in order. Wait for every verification command to exit before committing. A background test is
not a passed gate. Unqualified runtime filenames resolve under `KhaozEngine.TileWorld.Netcode/`. Unqualified
test filenames resolve under `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/`. Create the gitignored
`local-feed` before restore.

| Responsibility | Files |
| --- | --- |
| Deferral bound and liveness predicate | New `TileCombatPreparationDeferral.cs` |
| Migration-only state and pure transitions | `TileCombatPreparationState.cs`, `TileCombatPreparationScheduler.cs` |
| Migration codec | `TileProtocol.PreparationState.cs` |
| Wire validity and client ledger | `TileProtocol.Preparation.cs`, `TileCombatPreparationLedger.cs` |
| Server validity, deferral and recheck | `TileWorldServer.Preparation.cs` |
| Public XML docs of changed behavior | `TileCombatPreparation.cs`, `TileWorldServer.PreparationDelay.cs` (doc of `TryGetAttackReadyTick` only) |
| Kernel, wire, ledger, sampler and migration tests | `TilePreparationSchedulerTests.cs`, `TilePreparationWireTests.cs`, `TilePreparationAssemblyTests.cs`, `TilePreparationDeliveryTests.cs`, `TilePreparationSamplerTests.cs`, `TilePreparationMigrationTests.cs` |
| Server deferral tests | New `TilePreparationDeferralTests.cs`, `TilePreparationServerTests.cs`, `TilePreparationDelayTests.cs`, `PreparationScenario.cs` for a profile-before-first-step option and a component-write helper only |
| Pursuit reproductions | New `PursuitScenario.cs`, new `TilePreparationPursuitTests.cs` |
| Client sequence | New `TilePreparationDeferralDeliveryTests.cs`, `PreparationDeliveryScenario.cs` only if a profile parameter is needed. A failure found here is fixed in the runtime file its owning task lists, as named in Task 4 |
| Docs, version and release | `docs/USING-KHAOZENGINE.md`, `KhaozEngine.TileWorld.Netcode/README.md`, both design docs, `docs/INDEX.md`, `CHANGELOG.md`, `Directory.Build.props`, declarations guarded by `scripts/check-doc-versions.sh` |

Existing anchors at the design base: `PreparationIsDue` and `PreparationInvalidity` in
`TileWorldServer.Preparation.cs:77` and `:97`, `RecheckPreparation` at `:226`. `TryComplete` and `TryDelay`
in `TileCombatPreparationScheduler.cs:72` and `:89`. `ValidPreparationRecord` in
`TileProtocol.Preparation.cs:134`. The ledger's stale-sample keep at `TileCombatPreparationLedger.cs:49`.
The serve's per-record validation at `TileWorldServer.PreparationServe.cs:88` calls the shared validator
and needs no edit.

## Shared interfaces to implement

All internal. No public surface changes.

```csharp
// TileCombatPreparationState: one new field, default 0 meaning never deferred.
// Any real deferral tick is at least 1 because every impact is at least LeadTicks >= 1.
public long DeferredTick;

internal static class TileCombatPreparationDeferral
{
    // Ticks an attempt may wait past its impact for legal reach.
    internal static byte BoundTicks(in TileCombatPreparation attempt) => attempt.StrikeTicks;
    // True when a record served at serverTick is still active. Callers guarantee serverTick >= 0 and
    // ImpactTick > PrepareTick >= 0, so the subtraction cannot overflow.
    internal static bool IsLive(in TileCombatPreparation attempt, long serverTick) =>
        serverTick - attempt.ImpactTick < attempt.StrikeTicks;
}

// TileCombatPreparationScheduler
// True and sets DeferredTick = tick when active, tick >= ImpactTick, tick - ImpactTick < StrikeTicks, and
// either tick == ImpactTick or DeferredTick == tick - 1. False changes nothing.
public static bool TryDefer(ref TileCombatPreparationState state, long tick);
```

`TryComplete` keeps its signature and accepts `tick == ImpactTick`, or
`ImpactTick < tick <= ImpactTick + StrikeTicks` with `DeferredTick == tick - 1`. It keeps using `tick` for
the outcome's impact tick and for retained readiness `tick + CadenceTicks`. `TryDelay` keeps its signature
and clears `DeferredTick` when it revises an active attempt. `ClearActive` clears `DeferredTick`.

A late completion computes `checked(tick + CadenceTicks)` with `tick` up to `ImpactTick + StrikeTicks`. The
guards that today prove `impact + cadence` representable, in `TryCreate` at
`TileCombatPreparationScheduler.cs:41` and in `TryDelay` at `:110`, widen to `impact + StrikeTicks + cadence`,
so every accepted attempt can complete on its last legal tick. The completion arithmetic at `:81` then
cannot overflow.

`TileProtocol.ValidPreparationRecord` replaces `record.ImpactTick <= serverTick` with
`!TileCombatPreparationDeferral.IsLive(record, serverTick)`, evaluated after the existing nonnegative and
ordering checks.

### Task 1: Bounded deferral in the pure kernel

**Files:** Create `TileCombatPreparationDeferral.cs`. Modify `TileCombatPreparationState.cs`,
`TileCombatPreparationScheduler.cs`, `TilePreparationSchedulerTests.cs`.

- [ ] **Step 1: Write the failing kernel tests.** Add `An_overdue_attempt_defers_only_below_its_strike_ticks`,
  `A_deferred_attempt_completes_late_and_retains_cadence_from_that_tick`,
  `Late_completion_requires_an_unbroken_deferral`,
  `Delay_of_a_deferred_attempt_revises_from_the_current_tick`,
  `Cancelling_or_completing_clears_the_deferral_tick`, and
  `A_delay_that_leaves_no_room_for_a_late_completion_is_refused`. Create at tick 100 with cadence 14 and
  run each window test for profiles `(3, 1, 7)` and `(4, 2, 7)`. Every tick is relative to the created
  impact `H = 100 + LeadTicks`, which is 103 for 3/1 and 104 for 4/2. `S` is the profile's strike ticks
  and `lead` its lead. Core assertions:

```csharp
long H = state.Active.ImpactTick;
Assert.False(TileCombatPreparationScheduler.TryDefer(ref state, H - 1));          // before impact
for (long t = H; t < H + S; t++)
{
    Assert.True(TileCombatPreparationScheduler.TryDefer(ref state, t));
    Assert.Equal(t, state.DeferredTick);
    Assert.Equal(created.Active, state.Active);                                    // silent
}
Assert.False(TileCombatPreparationScheduler.TryDefer(ref state, H + S));          // bound reached
Assert.True(TileCombatPreparationScheduler.TryComplete(ref state, H + S, outcome, out var late));
Assert.Equal((H + S, 1UL, 1U), (late.ImpactTick, late.AttackId, late.Revision));
Assert.Equal(H + S + 14, state.ReadyNotBeforeTick);
// gap, 4/2 only: deferred on H, never on H + 1, completion tried on H + 2.
Assert.False(TileCombatPreparationScheduler.TryComplete(ref gap, H + 2, outcome, out _));
// deferred: deferred on H, then delayed by 2 on H + 1 with cooldown ready at H + 1.
Assert.True(TileCombatPreparationScheduler.TryDelay(ref deferred, H + 1, 2, H + 1, out byte accepted, out _));
Assert.Equal(((byte)2, 2U, H + 3, H + 3 - lead, 0L), (accepted, deferred.Active.Revision,
    deferred.Active.ImpactTick, deferred.Active.PrepareTick, deferred.DeferredTick));
```

  Overflow boundaries. Add the row `(long.MaxValue - 17, 0)` to
  `Unrepresentable_impact_or_following_readiness_is_refused_without_mutation`. With 3/1 and cadence 14 its
  impact plus cadence fits exactly, but impact plus strike plus cadence does not. That row refuses the
  creation used by `The_last_representable_completion_retains_its_full_cadence`, so replace that test with
  `The_last_representable_late_completion_retains_its_full_cadence`: create at `long.MaxValue - 18`, defer
  on the impact `long.MaxValue - 15`, complete on `long.MaxValue - 14`, and assert
  `ReadyNotBeforeTick == long.MaxValue`. In `A_delay_that_leaves_no_room_for_a_late_completion_is_refused`,
  delay an active attempt so the revised impact plus cadence fits but plus strike does not. Expect
  `TickOverflow`, zero accepted ticks and no mutation.

  Keep `Completion_rejects_the_wrong_tick_or_participants` unchanged. Its row at tick 104 must still fail
  because no deferral was recorded.
- [ ] **Step 2: Run red.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TilePreparationSchedulerTests`
  must fail on the missing `TryDefer` and `DeferredTick`.
- [ ] **Step 3: Implement.** Add the field, the deferral type and `TryDefer`. Widen `TryComplete` to the
  late window with the continuity rule. Clear the field in `TryDelay` revisions and `ClearActive`. Widen
  the `TryCreate` and `TryDelay` overflow guards to `impact + StrikeTicks + cadence`. Keep checked
  arithmetic. Do not touch the server, wire or migration codec in this task.
- [ ] **Step 4: Run the same command green.** Exit 0, zero failed tests.
- [ ] **Step 5: Commit explicit task paths.** Subject: `feat(combat): add a bounded impact deferral to the preparation kernel`.

### Task 2: Overdue schedules on the wire, ledger and migration

**Files:** Modify `TileProtocol.Preparation.cs`, `TileCombatPreparationLedger.cs`,
`TileProtocol.PreparationState.cs`, `TilePreparationWireTests.cs`, `TilePreparationAssemblyTests.cs`,
`TilePreparationDeliveryTests.cs`, `TilePreparationMigrationTests.cs`, `TilePreparationSamplerTests.cs`.

- [ ] **Step 1: Write the failing tests.**
  - `An_overdue_state_record_is_valid_only_below_its_strike_ticks` in the wire tests. For strike 1 and 2,
    shift both `PrepareTick` and `ImpactTick` so the lead stays valid. Overdue by `StrikeTicks - 1` encodes
    and decodes to the same record. Overdue by `StrikeTicks` throws `ArgumentException` from the encoder
    and fails to decode with an empty output.
  - In `Counts_lengths_and_deadlines_are_validated_atomically`, replace the `due now` and `past due`
    mutations, which now fail only through a reversed lead, with `overdue by strike`: header tick
    advanced to `ImpactTick + StrikeTicks` with a valid lead. The golden bytes test stays unchanged.
  - In `Encoders_refuse_invalid_local_arguments_and_noncanonical_empty_sets`, replace the row
    `State(1) with { ImpactTick = 100 }` at `TilePreparationWireTests.cs:364`, which now fails only through a
    reversed lead, with an `overdue by strike` row: `State(1, tick: 96)` under header tick 100, so impact 99
    is overdue by exactly its one strike tick and the encoder throws.
  - `An_overdue_record_assembles_below_its_strike_ticks` in the assembly tests, with the same boundary.
  - `A_stale_revision_cannot_retire_a_deferred_sample` in the delivery tests. Apply a revision 2 state whose
    impact equals the next frame's tick, then a frame carrying the stale revision 1. The ledger keeps
    revision 2.
  - `Deferral_tick_round_trips_through_the_migration_codec` in the migration tests: a state with a nonzero
    `DeferredTick` survives the Migrate-channel codec unchanged. Optionally also inject that state onto an
    entity that then crosses the region line and read it back. Add `DeferredTick` to the field coverage of
    `All_preparation_fields_migrate_but_never_replicate_or_persist`. The server cannot defer until Task 3,
    so the real handoff proof lives there.
  - `A_deferred_schedule_samples_awaiting_outcome_until_its_terminal` in the sampler tests. This pins
    existing sampler behavior at `ImpactTick` and at `ImpactTick + StrikeTicks - 0.5`. It may already pass.
- [ ] **Step 2: Run red.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparationWireTests|FullyQualifiedName~TilePreparationAssemblyTests|FullyQualifiedName~TilePreparationDeliveryTests|FullyQualifiedName~TilePreparationMigrationTests|FullyQualifiedName~TilePreparationSamplerTests'`.
  Expect the overdue-record, stale-sample and migration assertions to fail.
- [ ] **Step 3: Implement.** Switch `ValidPreparationRecord` to the shared predicate. Switch the ledger's
  stale-sample keep from `newer.ImpactTick > frame.ServerTick` to `TileCombatPreparationDeferral.IsLive`.
  Append `DeferredTick` to the migration writer and reader after `TargetTeleportEpoch`. Nothing else.
- [ ] **Step 4: Run the same command green.**
- [ ] **Step 5: Commit explicit task paths.** Subject: `feat(combat): accept an overdue deferred schedule on the preparation wire`.

### Task 3: Server deferral, with the pursuit lock reproduced first

**Files:** Create `PursuitScenario.cs`, `TilePreparationPursuitTests.cs`, `TilePreparationDeferralTests.cs`.
Modify `TileWorldServer.Preparation.cs`, `TileCombatPreparation.cs` (XML docs only),
`TileWorldServer.PreparationDelay.cs` (XML doc of `TryGetAttackReadyTick` only), `TilePreparationServerTests.cs`,
`TilePreparationDelayTests.cs`, `PreparationScenario.cs` (helpers only).

`PursuitScenario` builds a real `TileWorldServer` over `TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0))`
with the fixture's `FixedRules` and a settable preparation profile, or null preparation rules for the
legacy path. A region is 64 tiles wide (x 0 to 63, see the handoff test at
`TilePreparationMigrationTests.cs:109-116`). The whole course stays inside that one region: the attacker
actor spawns at `(2, 20, 0)`, the target actor at `(3, 20, 0)`, and the target's goal is `(62, 20, 0)`. The
window is the 110 ticks from `W0`, which at run is at most 55 tiles, ending by x 58. Crossing a region is
not part of this proof. Handoff during a deferral has its own test below. The attacker is commanded
`TileCommand.Attack(target, gait)`. On lock tick `L + phase` the target is commanded
`TileCommand.WalkTo(goal, gait)`. Re-latch the same goal when the target's planned route ends, as the
Grimhollow pursuit measurement did. `W0` is the first tick the target's committed tile changes. It returns:

```csharp
internal readonly record struct PursuitWindow(int Landed, int IllegalReachEnds, int EndsAfterFirstLanded,
    long[] ResolvedTicks, long WindowEnd, bool EveryResolutionWithinBound, bool TargetNeverIdle,
    bool StayedInOneRegion)
{
    // Resolutions in [first, first + cadences * cadence), counted from this build's own first resolution.
    public int SteadyCount(int cadences, byte cadence);
}
internal static PursuitWindow Run(bool prepared, TileMoveMode gait, byte cadence,
    byte lead, byte strike, int phase);
```

Landed counts resolved outcomes for the attacker, hit or miss, from the prepared buffer or from
`OnCombatEvent` on the legacy path. `EveryResolutionWithinBound` records the attacker's schedule after
every tick and checks that each prepared resolution resolved between its own attack ID's scheduled
`ImpactTick` and that tick plus `StrikeTicks`. A resolution without a matching schedule, or outside the
bound, fails it. It is true by definition on the legacy path. A roll-in-reach check is not used, because
the roll phase already refuses a roll out of reach (`TileWorldServer.Combat.cs:274`) and it could not fail.
`TargetNeverIdle` asserts the target committed a new tile on every step period of the window.
`StayedInOneRegion` asserts both owners stayed in cell `(0, 0)`.

- [ ] **Step 1: Write the pursuit reproductions.** `Walking_pursuit_keeps_the_legacy_hit_rate_in_every_phase`
  and `Running_pursuit_keeps_the_legacy_hit_rate_in_every_phase`, each a theory over
  `(lead, strike, cadence)` in `(3, 1, 14)`, `(3, 1, 16)`, `(4, 2, 14)`, `(4, 2, 16)`. The test class takes
  `ITestOutputHelper`. Run all eight windows first, write a per-phase table, then assert, so the red run
  shows every phase and not only the first failure:

```csharp
const int Cadences = 5;
var rows = new List<(int Phase, PursuitWindow Before, PursuitWindow After)>();
for (int phase = 0; phase < 4; phase++)
    rows.Add((phase, PursuitScenario.Run(false, gait, cadence, lead, strike, phase),
        PursuitScenario.Run(true, gait, cadence, lead, strike, phase)));
// One line per phase and build: landed, IllegalReach ends, ends after first landed, first resolved tick,
// steady count, and the resolved ticks relative to W0.
string table = PursuitScenario.Table(rows, cadence, Cadences);
output.WriteLine(table);
foreach (var (phase, before, after) in rows)
{
    Assert.True(before.TargetNeverIdle && after.TargetNeverIdle && before.StayedInOneRegion
        && after.StayedInOneRegion, table);
    Assert.True(after.EveryResolutionWithinBound, table);
    Assert.True(after.IllegalReachEnds <= 1, table);        // only a run start from a stand may end once
    Assert.True(after.EndsAfterFirstLanded == 0, table);
    for (int i = 1; i < after.ResolvedTicks.Length; i++)
    {
        long gap = after.ResolvedTicks[i] - after.ResolvedTicks[i - 1];
        Assert.True(gap >= cadence && gap <= cadence + strike, table);
    }
    Assert.True(after.ResolvedTicks.Length > 0
        && after.ResolvedTicks[0] + Cadences * cadence <= after.WindowEnd, table);
    Assert.True(after.SteadyCount(Cadences, cadence) >= before.SteadyCount(Cadences, cadence), table);
}
```

  The hard gate is `EndsAfterFirstLanded == 0` plus every interval inside `[C, C + S]`. The rate comparison
  counts each build's resolutions over exactly five cadences from that build's own first resolution, per
  phase. A windowed or summed count was rejected: the prepared build resolves its first swing at least one
  lead later than the legacy build, so a fixed window can drop a swing from a correct fix on its edge alone
  (a replay gave 23 against 23 summed at 3/1 cadence 16, with one run phase at 5 against 6). A slack such
  as `legacy - 4` was rejected because it would pass a build that loses a swing in every phase. Counting
  from each build's own first resolution removes the lead offset and the window edge. With intervals of at
  least `C` neither build can exceed five, so a correct fix scores five in both, while a locked build scores
  one. The steady count is also the check that fails a lock when only one outcome resolves and the interval
  loop has nothing to test.
- [ ] **Step 2: Run red and check fixture fidelity.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TilePreparationPursuitTests --logger "console;verbosity=detailed"`,
  so the tables print. Read them, not the first assertion. The 3/1 rows must show the lock: at least one
  walk phase at cadence 14 with 10 or more `IllegalReach` ends and at most one landed outcome, and at least
  one run phase at cadence 16 with zero landed. If no phase locks, stop and report the tables and the
  per-tick timeline. Do not change the geometry.
- [ ] **Step 3: Write the failing server deferral tests** in `TilePreparationDeferralTests.cs` over
  `PreparationScenario`, profiles `(3, 1, 7)` and `(4, 2, 7)`. Set the profile before the first step. The
  scenario locks at tick 100, so the first impact is `H = 100 + lead`, which is 103 for 3/1 and 104 for 4/2.
  Every tick below is relative to `H`, with `S` the strike ticks. Move the target in `OnAfterMovement` so
  reach is decided on committed tiles.
  - `Out_of_reach_impact_defers_and_resolves_on_the_next_legal_tick`: out at `H`, back at `H + 1`. One roll
    at `H + 1`. Result impact `H + 1`, attack ID 1, revision 1, no ended record. Next schedule impact
    `H + 15`, prepare `H + 15 - lead`.
  - `A_deferred_tick_makes_no_roll_revision_or_terminal`: on each deferred tick `H` to `H + S - 1`, no roll,
    no RNG call, no ended or prepared record, cooldown 0, `TryGetCombatPreparation` unchanged,
    `TryGetAttackReadyTick` equals the current tick.
  - `An_escaping_target_ends_the_attempt_after_its_strike_ticks`: target never returns. `IllegalReach` with
    `ServerTick` `H + S` and `ImpactTick` `H`, no roll, cooldown 0, no attempt and no replacement in that
    pass. Restoring reach prepares attack ID 2 with a full lead from the next tick.
  - `Non_reach_invalidity_ends_a_deferred_attempt_immediately`, profile 4/2, deferred at `H`, change applied
    for `H + 1`, over `disengage`, `retarget`, `profile`, `invalid`, `permission`, `target-dead`,
    `attacker-dead`, `teleport` and `rules-null`. Each ends at `H + 1` with its own reason and no roll.
    `retarget` prepares the new target on `H + 2`, not `H + 1`.
  - `Non_reach_invalidity_on_the_impact_tick_wins_over_deferral`: the same changes at `H` with reach also
    lost. The ended reason is the change's reason at `H`.
  - `A_plane_mismatch_at_impact_defers_as_reach`, 4/2: target moved to plane 1 at `H` and back at `H + 1`.
    One roll at `H + 1`.
  - `A_deferral_survives_the_post_roll_recheck_of_its_own_tick`: another attacker resolves a roll on `H`
    while the first attacker defers. The first attempt is still live after the tick and resolves on `H + 1`.
  - `A_target_killed_during_deferral_ends_it_in_that_tick`, 4/2: a second attacker kills the target on
    `H + 1` while the first is deferred. The first ends `ParticipantUnavailable` on `H + 1`, before the serve.
  - `A_deferral_survives_a_real_region_handoff_and_resolves_in_the_new_cell`, 3/1, following
    `Preparation_survives_an_actual_region_handoff_without_restarting`: attacker at `(63, 20, 0)`, target at
    `(63, 21, 0)`, then in `OnAfterMovement` of `H` the target moves to `(65, 20, 0)`, two tiles away, so the
    attempt defers on `H`. On `H + 1` the follow steps the attacker to `(64, 20, 0)` in cell `(1, 0)`. Assert
    the new owner cell, one roll on `H + 1` with attack ID 1 and revision 1, and no ended record. If the
    handoff tick misses the combat pass and ends the attempt, stop and report it as a finding.
  - `An_overdue_attempt_that_was_not_deferred_ends_unavailable`, for design check 10. A missed pass cannot be
    produced naturally from the fixture: every player slot is in `tickSlots`, which is rebuilt from all slots
    at tick start (`TileWorldServer.Tick.cs:143-144`), and every actor is in `actorNetIds`, so a real miss
    needs an owner lookup to fail mid handoff. Construct the same state instead: in `OnAfterMovement` of `H`,
    with the target in reach, write the attacker's `TileCombatState.CooldownRemaining = 2` through the host.
    Phase 0 decrements it to 1, so the attempt is valid but not due and not deferred on `H`. On `H + 1` it
    must end `ParticipantUnavailable` with no roll, no `PreparedCombatEvent` and no `OnCombatEvent`. This is
    the server guard that matters, because `CompletePreparedSwing` announces a rolled result even when the
    kernel's `TryComplete` refuses.
  - In `TilePreparationDelayTests.cs`, `Delay_during_a_deferral_revises_from_the_current_tick`: deferred at
    `H`, `DelayAttack(attacker, 2)` before the `H + 1` pass. Revision 2, impact `H + 3`, prepare
    `H + 3 - lead`, no roll on `H + 1`, and a roll on `H + 3` when in reach.
  - In `TilePreparationServerTests.cs`, update `Invalid_due_attempt_requires_a_fresh_lead`, which uses 3/1
    with `H` 103: no end on 103, `IllegalReach` on 104 with impact 103, then the target restored after tick
    104 prepares attack ID 2 on 105 with impact 108.
- [ ] **Step 4: Run red.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparationDeferralTests|FullyQualifiedName~TilePreparationServerTests|FullyQualifiedName~TilePreparationDelayTests|FullyQualifiedName~TilePreparationPursuitTests'`.
- [ ] **Step 5: Implement in `TileWorldServer.Preparation.cs`.**
  - In `PreparationInvalidity`, replace the `TickCount > ImpactTick` end with
    `TickCount > ImpactTick && state.DeferredTick < TickCount - 1`, still `ParticipantUnavailable`, and
    check reach at `TickCount >= ImpactTick` instead of `==`.
  - In `PreparationIsDue`, when the reason is `IllegalReach` and the kernel's `TryDefer` accepts, write the
    state and return false without creating an attempt. A valid attempt is due when
    `ImpactTick <= TickCount` and cooldown is zero. Every other reason keeps its current path.
  - In `RecheckPreparation`, keep the attempt only when the reason is `IllegalReach` and
    `state.DeferredTick == TickCount`, meaning phase 0 of this tick deferred it. Otherwise end it as today.
    Do not call `TryDefer` here, so the recheck can never start a deferral that phase 0 did not judge.
  - Update XML docs: `TileCombatPreparation.ImpactTick` is the scheduled impact, resolving later only
    while legal reach is missing, within `StrikeTicks`. `PreparedCombatEvent.ImpactTick` is the resolution
    tick. `TileCombatPreparationEndReason.IllegalReach` is reach still illegal when the deferral bound
    passes. `TryGetAttackReadyTick` reports the current tick while an attempt is deferred.
  - No change to the roll, apply or death phases, roll order, `AttackReadyTick` logic or the serve.
- [ ] **Step 6: Run the Step 4 command green,** then the whole area project:
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release`.
  Both exit 0. Keep the printed pursuit tables for the report.
- [ ] **Step 7: Commit explicit task paths.** Subject: `fix(combat): defer an out-of-reach prepared impact until legal reach`.

### Task 4: The client sequence of a deferred impact

**Files:** Create `TilePreparationDeferralDeliveryTests.cs`. Modify `PreparationDeliveryScenario.cs` only to
accept a profile. If a test fails, the fix may touch only the runtime file that owns the failing seam, from
this list: `TileProtocol.Preparation.cs`, `TileCombatPreparationLedger.cs`, `TileWorldServer.Preparation.cs`,
`TileWorldServer.PreparationServe.cs`, `TileWorldClient.Preparation.cs`. Any other runtime file means the
design is wrong. Stop and report.

- [ ] **Step 1: Write the sequence tests** over `PreparationDeliveryScenario` with a preparation-enabled client:
  Let `H` be the first attempt's scheduled impact in the scenario.
  - `A_deferred_impact_keeps_one_client_schedule_until_its_result`, profile 4/2, target moved out of reach
    for `H` and `H + 1` and back for `H + 2`. The client sees the same `TileCombatPreparation` value in every
    applied state from `H - 1` through `H + 1`, one `CombatPreparationsChanged` per tick, `AwaitingOutcome`
    from the sampler at a `CombatPresentationTick` of `H` or later, then exactly one `PreparedCombatEvent`
    with the first attack ID, revision 1 and impact tick `H + 2`. No `CombatPreparationEnded`, no observed
    revision above 1, and `RejectedCombatPreparationFrameCount` stays 0.
  - `An_expired_deferral_delivers_one_illegal_reach_terminal`: target never returns. One
    `CombatPreparationEnded` with `ServerTick` `H + 2`, `ImpactTick` `H` and `IllegalReach`, and no result.
  - `Burst_delivery_of_a_deferral_applies_each_tick_in_order`: step the server through `H + 2` with
    `poll: false`, then poll once. States and the one result arrive in tick order, once each.
  - `An_observer_entering_interest_during_deferral_samples_awaiting_outcome`: a second client whose interest
    first includes the attacker on `H + 1` receives the overdue schedule and samples `AwaitingOutcome`, then
    the same single result.
- [ ] **Step 2: Run them.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TilePreparationDeferralDeliveryTests`.
  These pin a contract that Tasks 2 and 3 should already satisfy, so they may pass at once. If one fails,
  record the failure, fix it in the listed owning runtime file, rerun this command and the whole area
  project, and commit that fix separately with subject `fix(combat): <what the client sequence exposed>`
  before the test commit. Do not weaken an earlier assertion.
- [ ] **Step 3: Commit explicit task paths.** Subject: `test(combat): pin the client sequence of a deferred impact`.

### Task 5: Documentation sweep, version and changelog

**Files:** `docs/USING-KHAOZENGINE.md`, `KhaozEngine.TileWorld.Netcode/README.md`, both design docs,
`docs/INDEX.md`, `CHANGELOG.md`, `Directory.Build.props`, and every declaration
`scripts/check-doc-versions.sh` guards. Keep existing headings so the `#authoritative-attack-preparation-20141`
anchors still resolve.

- [ ] **Step 1: Select the version.** `git fetch`, then read `origin/main`'s `<KhaozEngineVersion>` and
  `git tag --sort=-v:refname | head`. Ride a staged unreleased version if one exists. Otherwise take the
  next free patch, expected 20.15.1 above the released 20.15.0. Record the choice in the report.
- [ ] **Step 2: Update the consumer references.** In the USING preparation section, replace the paragraph
  that begins "Temporary range loss retains the schedule" and the reads and sampler paragraphs with the
  deferral rule, the `StrikeTicks` bound, the resolution tick on `PreparedCombatEvent`, the later
  `IllegalReach` terminal, readiness while deferred, delay during a deferral, and the relaxed wire rule
  with its protocol-bump consequence. Make the same corrections to the package README's preparation
  section, including its table rows for `PreparedCombatEvent` and `CombatPreparationEnded` and the
  paragraph that begins "A changed target or profile replaces the attempt".
- [ ] **Step 3: Sweep.** Search every Markdown file and package README with
  `git grep -n -i -e 'IllegalReach' -e 'legal reach' -e 'at impact' -e 'range loss' -e 'impact tick' -e 'ImpactTick' -e 'AwaitingOutcome' -e 'preserves pursuit' -- '*.md'`.
  Correct every stale statement of the old rule. Leave released CHANGELOG entries alone. Mark the
  deferral design and its INDEX row implemented for the selected version, and add that version to the base
  design's amendment line.
- [ ] **Step 4: Bump and write the changelog in one change.** Set `<KhaozEngineVersion>`, update the guarded
  declarations, and add the newest `CHANGELOG.md` entry covering every point in section 9 of the deferral
  design.
- [ ] **Step 5: Run the guards and the build.**

```sh
mkdir -p local-feed
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

  Every command must exit 0 with zero warnings and zero failed tests. GPU facts skip in plain
  `dotnet test`. Report that as outside this headless change.
- [ ] **Step 6: Commit explicit paths.** Subject: `release(20.15.1): prepared impacts wait for legal reach in pursuit`,
  using the version selected in Step 1. This follows the repository's release commit precedent, for example
  `release(20.15.0): row timestamps across every engine table`. Report every task SHA with `git branch --contains` output.

### Task 6: Integration and release, owned by the orchestrator

Workers stop after Task 5. The orchestrator runs these steps. Grimhollow is explicitly pinned and waiting
on this release, so the automatic tag exception in the contributor rules applies and the release completes
without another prompt.

- [ ] **Step 1: Reconcile.** Fetch, re-read `origin/main`, its version and the tags. If the selected
  version is taken, bump to the next free one and move the CHANGELOG heading in the same commit. Merge
  current `origin/main` into the branch and rerun the full Task 5 Step 5 block. Every command exits 0.
- [ ] **Step 2: Integrate.** Merge the verified branch into `main` and push `main`. No force push.
- [ ] **Step 3: Pack.** After `origin/main` contains the release commit, from a clean `main`:
  `scripts/pack-local-feed.sh`, then `scripts/check-local-feed.sh`. Never run a bare pack.
- [ ] **Step 4: Tag and publish.** From that `main`, `scripts/tag-release.sh`, then push only the new tag
  with `git push origin v<version>`. Wait for the tag's release workflow to finish and confirm the packages
  at that version exist in GitHub Packages.
- [ ] **Step 5: Hand off to Grimhollow.** Report the released version and its commit. The consumer's
  adoption moves `<KhaozEngineVersion>`, `ke-tileedit` and `ke-sfxbake` together, refreshes its vendored
  feed, records the swept range, and updates tests that expect `IllegalReach` on the impact tick or a result
  impact tick equal to the schedule's. Its unreleased adoption already bumps its connect protocol string,
  so no further bump is needed if both ship together. Re-running the Grimhollow pursuit measurement on the
  new pin is the consumer's acceptance check. No game code is pre-authorized by this plan.

## Self-review and coverage record

| Deferral design requirement | Owning tasks |
| --- | --- |
| Only reach defers, plane included, ordered checks, continuity | 1, 3 |
| `StrikeTicks` bound and expiry as `IllegalReach` | 1, 3 |
| Silent schedule, relaxed wire rule, ledger and migration | 2, 4 |
| Cadence from the resolution tick | 1, 3 |
| Delay, cancellation, retarget, movement, death, handoff, burst, interest, determinism | 2, 3, 4 |
| Pursuit reproductions at the legacy rate or better, per phase | 3 |
| Real region handoff during a deferral and the missed-pass guard | 3 |
| Overflow room for a late completion | 1 |
| Docs sweep, version, changelog | 5 |
| Merge, pack, tag and consumer handoff | 6 |

All five Review Focus items have named tests in their owning task. The pursuit fixture has an explicit
fidelity gate before any server change. The plan adds no public API and no wire layout change.
