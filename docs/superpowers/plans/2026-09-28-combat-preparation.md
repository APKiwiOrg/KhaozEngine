# Tile Combat Preparation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add opt-in authoritative preparation before tile combat impacts while preserving the default combat path.

**Architecture:** A pure scheduler owns attempt identity and integer deadlines in migration-only state. The existing server combat pass remains responsible for eligibility, roll order, damage and death, while new bounded frame codecs carry schedules and terminal outcomes. A client ledger and pure phase sampler expose presentation without predicting damage.

**Tech Stack:** C#/.NET, `KhaozEngine.TileWorld.Netcode`, ECS and replication, reliable ordered in-memory transport tests, xUnit.

**Spec:** [TILE-COMBAT-PREPARATION-DESIGN-2026-09-28.md](../../design/TILE-COMBAT-PREPARATION-DESIGN-2026-09-28.md), approved by the owner at commit `60ccb6314`. Its historical status banner has not yet been updated. This plan requires separate approval before execution.

## Global Constraints

- "The existing combat path remains the default."
- "The capability belongs in `KhaozEngine.TileWorld.Netcode`."
- "A valid enabled profile has `1 <= StrikeTicks <= LeadTicks <= resolved AttackTicks`."
- "It never reads a drawn pose, projected route tile or render interpolation."
- "Set `H = max(T + P, T + R, retained readiness boundary)`."
- "Preserve the existing maximum of 255 ticks of outstanding wait."
- "All new multi-byte integers are explicitly little-endian, with no native-layout serialization, floats, strings or padding."
- Wire schema is 1, frame tags are 4 and 5, header is 16 bytes, state records are 50 bytes and terminal records are 46 bytes. Reconfirm tags remain free before implementation.
- "Bound assembly to the declared count, at most 256 chunks and 65,280 records per frame set." Each chunk holds at most 255 records.
- Reasons 1 through 9 retain the spec's exact mapping, including 9 for rules unavailable. Legacy tag 3 is unchanged.
- "The server's existing `OnCombatEvent` still fires once for every resolved swing, preserving game awards, journals and retaliation hooks."
- No engine sword/style enums, game clips, new rendering dependency, movement retiming, damage prediction or default-consumer timing change.
- Work in `/Users/antonio/KhaozEngine/.worktrees/combat-preparation`, branch `feature/combat-preparation`. Preserve unrelated changes and stage explicit paths.
- Tests use `KhaozEngine.Tests.TileNetcode` and the existing area project. Release, zero warnings, no blanket suppression or file-size baseline increase.
- No em/en dash glyphs or Markdown prose semicolons. No release tag from an implementation worker.
- Grimhollow adoption is a separate plan against the released API. This plan ends with a verified engine commit and a release handoff.

## Review Focus

1. A prepared-event handler re-enters `Poll()`: every result must be delivered once without losing the outer batch. Owned by Task 5, `Prepared_callbacks_can_reenter_poll_without_loss_or_duplication`.
2. A final state chunk is malformed after valid earlier chunks: the previously visible schedule must remain intact. Owned by Task 4, `Malformed_final_chunk_preserves_the_previous_complete_frame`.
3. A food delay follows cancellation while ordinary cooldown is zero: the retained future boundary must still gain the delay. Owned by Task 3, `Delay_after_cancellation_charges_retained_readiness`.
4. A supplied custom registry lacks preparation migration state: enabled boot must fail clearly instead of losing the attempt on a cell crossing. Owned by Task 2, `Enabled_custom_registry_requires_preparation_state`.
5. An old result and a successor schedule arrive in the same server tick: retiring the old sample must not suppress its real result or erase its successor. Owned by Task 5, `Successor_snapshot_does_not_consume_the_previous_terminal`.

---

## Execution and file boundaries

Run tasks in order. Complete each red-green-refactor cycle and wait for every verification process to
exit before committing or starting the next task. A started background test is not a passed gate.
All paths below are relative to the worktree root. Unqualified runtime filenames resolve under
`KhaozEngine.TileWorld.Netcode/`. Unqualified test filenames resolve under
`KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/`. Create the gitignored `local-feed` before restore.

| Responsibility | Files |
| --- | --- |
| Public records and profile seam | `KhaozEngine.TileWorld.Netcode/TileCombatPreparation.cs`, `ITileCombatPreparationRules.cs` |
| Migration state and pure transitions | `TileCombatPreparationState.cs`, `TileCombatPreparationScheduler.cs` in the same package |
| Server eligibility and lifecycle adapter | `TileWorldServer.Preparation.cs`, small calls in existing `TileWorldServer.Combat.cs`, `.Actors.cs`, `.Sessions.cs`, `.Tick.cs` |
| Delay/readiness adapter | `TileWorldServer.PreparationDelay.cs`, existing `DelayAttack` entry point |
| Migration codec | `TileProtocol.PreparationState.cs`, one registration in `TileProtocol.Components.cs` |
| Network codecs and complete-frame assembly | `TileProtocol.Preparation.cs`, `TileCombatPreparationFrames.cs`, `TileCombatPreparationAssembler.cs` |
| Per-viewer serve and client receive ledger | `TileWorldServer.PreparationServe.cs`, `TileWorldClient.Preparation.cs`, `TileCombatPreparationLedger.cs` |
| Pure stage sampler and presentation clock | `TileCombatPreparationSampler.cs`, `TileCombatPresentationClock.cs` |
| Tests | New `TilePreparation*Tests.cs` and `PreparationScenario.cs` under `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/` |
| Consumer reference and release preparation | Package README, `docs/USING-KHAOZENGINE.md`, approved spec, `docs/INDEX.md`, version/changelog declarations when implementation is ready |

Existing anchors at the approved base: combat cooldown/roll/apply lives in
`TileWorldServer.Combat.cs:145`, `:181`, `:232` and `:273`. The serve boundary is
`TileWorldServer.Tick.cs:215`. Receive demultiplexing and reentrancy precedent are in
`TileWorldClient.Snapshots.cs:104` and `:121`. Presentation time is advanced at
`TileWorldClient.cs:470`. These are integration seams, not invitations to duplicate the combat pass.
All these files are below the current 800-line cap. Keep new responsibilities in the named types.

## Shared interfaces to implement

Public types use `KhaozEngine.TileWorld.Netcode`. Define the following in Task 1, keeping wire-only
details internal. These signatures are the interface between tasks, not sample alternative names.

```csharp
public readonly record struct TileCombatPreparationProfile(
    byte LeadTicks, byte StrikeTicks, uint PresentationKey);
public interface ITileCombatPreparationRules
{
    TileCombatPreparationProfile ProfileFor(long attackerNetId);
}
public readonly record struct TileCombatPreparation(
    long AttackerNetId, long TargetNetId, ulong AttackId, uint Revision,
    uint PresentationKey, long PrepareTick, long ImpactTick,
    byte StrikeTicks, byte CadenceTicks)
{
    public long StrikeTick => ImpactTick - StrikeTicks;
}
public enum TileCombatPreparationEndReason : byte
{
    None = 0, Disengaged = 1, TargetChanged = 2, ProfileChanged = 3,
    ParticipantUnavailable = 4, PermissionRevoked = 5, IllegalReach = 6,
    Teleport = 7, InvalidProfile = 8, RulesUnavailable = 9
}
public readonly record struct PreparedCombatEvent(
    long ImpactTick, ulong AttackId, uint Revision, uint PresentationKey,
    TileCombatEvent Outcome);
public readonly record struct CombatPreparationEnded(
    long ServerTick, long AttackerNetId, long TargetNetId, ulong AttackId,
    uint Revision, uint PresentationKey, long ImpactTick,
    TileCombatPreparationEndReason Reason);
```

Task 2 adds `ITileCombatPreparationRules? TileWorldServerConfig.CombatPreparationRules { get; init; }`
with default null. Task 5 adds `bool TileWorldClientConfig.CombatPreparationEnabled { get; init; }`
with default false. Task 2 implements the server read and Task 5 its client counterpart:
`bool TryGetCombatPreparation(long attackerNetId, out TileCombatPreparation preparation)`.
Task 3 implements server-only `bool TryGetAttackReadyTick(long attackerNetId, out long tick)`.

The client callbacks in Task 5 are `event Action<long>? CombatPreparationsChanged` carrying the applied
server tick, `event Action<PreparedCombatEvent>? PreparedCombatEvent` and
`event Action<CombatPreparationEnded>? CombatPreparationEnded`. Observers query the atomically replaced
state in the first callback. Existing server `OnCombatEvent` remains the award hook. Prepared outcomes
do not raise the legacy client `CombatEvent` callback.

### Task 1: Pure attempt state and transition kernel

**Files:** Create the four profile/state/scheduler files in the structure table. Create
`TilePreparationSchedulerTests.cs` in the test directory.

**Interfaces:** Consumes no server or graphics objects. Produces the shared public types and internal
`TileCombatPreparationState : IComponent` holding `HasActive`, `Active`, `LastAttackId`,
`ReadyNotBeforeTick`, `AttackerTeleportEpoch` and `TargetTeleportEpoch`. Epochs are `uint`, IDs are
`ulong`, readiness is `long`. State default means no attempt and last ID zero.

Produce these internal scheduler methods in `TileCombatPreparationScheduler`:

```csharp
bool TryCreate(ref TileCombatPreparationState state, long tick, long attacker, long target,
    in TileCombatPreparationProfile profile, byte cadence, long readyTick,
    uint attackerEpoch, uint targetEpoch, out TilePreparationFailure failure);
bool TryCancel(ref TileCombatPreparationState state, long tick,
    TileCombatPreparationEndReason reason, out CombatPreparationEnded ended);
bool TryComplete(ref TileCombatPreparationState state, long tick,
    in TileCombatEvent outcome, out PreparedCombatEvent completed);
```

Methods are static. Define internal `TilePreparationFailure` values `None`, `InvalidProfile`,
`IdentityExhausted`, `TickOverflow`. `TryCreate` never overwrites an active attempt. Invalid profile
or checked-arithmetic failure writes no new attempt. `TryComplete` accepts only its due tick and
matching attacker/target, consumes the attempt and retains `tick + CadenceTicks` as readiness.
`TryCancel` returns false when idle, otherwise retains at least the old impact boundary.

- [ ] **Step 1: Write the failing transition tests.** Use named tests
  `First_attempt_uses_full_lead`, `Existing_wait_overlaps_preparation`,
  `Cancellation_retains_readiness`, `Completion_consumes_the_identity`,
  `Invalid_profiles_and_exhaustion_never_create_an_attempt`. Their core assertions are:

```csharp
Assert.Equal((100L, 102L, 103L),
    (state.Active.PrepareTick, state.Active.StrikeTick, state.Active.ImpactTick));
Assert.Equal((102L, 105L), (waiting.Active.PrepareTick, waiting.Active.ImpactTick));
Assert.False(cancelled.HasActive);
Assert.Equal(103L, cancelled.ReadyNotBeforeTick);
Assert.Equal(117L, completedState.ReadyNotBeforeTick);
Assert.False(repeatedCompletion);
Assert.Equal(1UL, first.AttackId);
Assert.Equal(2UL, replacement.AttackId);
Assert.Equal(1U, replacement.Revision);
Assert.False(invalid.HasActive);
```

  Create at tick 100 with profile `(3, 1, 7)`, cadence 14 and ready ticks 100 or 105. Complete at
  103. Test lead/strike zero, strike greater than lead, lead greater than cadence, last ID
  `ulong.MaxValue`, and tick near `long.MaxValue`. Include lead equal to cadence and presentation key zero.
- [ ] **Step 2: Run red.** `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TilePreparationSchedulerTests`
  must fail on missing preparation types or methods before implementation.
- [ ] **Step 3: Implement the declared types and methods.** Use checked deadline arithmetic and one
  monotonically increasing ID per successful creation. Capture only timing, identity and epochs.
  Do not roll, change health, touch transport or create a second game cadence source.
- [ ] **Step 4: Run the same command green.** Require exit 0 and zero failed tests. Refactor only
  duplication exposed by these transitions, then rerun this focused suite if changed.
- [ ] **Step 5: Commit explicit task paths.** Subject: `feat(combat): add preparation transition kernel`.

### Task 2: Server scheduling, cancellation and migration

**Files:** Create `TileWorldServer.Preparation.cs`, `TileProtocol.PreparationState.cs`,
`PreparationScenario.cs`, `TilePreparationServerTests.cs`, `TilePreparationMigrationTests.cs`.
Modify `TileWorldServerConfig.cs`, `TileWorldServer.cs`, `TileWorldServer.Combat.cs`,
`TileWorldServer.Actors.cs`, `TileWorldServer.Sessions.cs`, `TileWorldServer.Tick.cs`,
`TileProtocol.Components.cs` only at their existing lifecycle seams.

**Interfaces:** Consumes Task 1 transitions. Produces the server config/read surface above and
internal `IReadOnlyList<PreparedCombatEvent> PreparedCombatEventsThisTick` and
`IReadOnlyList<CombatPreparationEnded> EndedCombatPreparationsThisTick` for Task 5's serving adapter.
Keep returned buffers immutable to callers and valid until the next tick.
Expose named diagnostic counts `InvalidCombatPreparationCount` and
`ExhaustedCombatPreparationIdentityCount` on the server, both `long` reads.

Register the migration state at `TileProtocol.TileCombatPreparationStateTypeId =
ReplicationRegistry.FirstExtensionTypeId + 9`, verified free at base. Keep `FirstGameTypeId` at `+16`.
`TileProtocol.RegisterPreparationState(ReplicationRegistry registry)` is internal and called from
`CreateRegistry`. Its explicit codec includes all Task 1 fields and uses `ReplicationChannels.Migrate`
alone. Enabled server construction requires that ID in a supplied registry and refuses a missing
registration with `ArgumentException`. Disabled mode does not create preparation components.

The test fixture `PreparationScenario` wraps real `TileWorldServer` and counted `FixedRules`. Its
`Create(long nextTick = 100, byte cadence = 14, bool enabled = true, float tickSeconds = 1f / 6f)`
advances an empty server to the requested next tick, then spawns adjacent actors and locks the attacker.
It exposes `Server`, `Rules`, `Attacker`, `Target`, `Step()`, `AdvanceTo(long nextTick)`,
`Preparation()`, `SetProfile(TileCombatPreparationProfile profile)` and
`SetTargetPosition(TileCoord tile)`. `AdvanceTo` leaves that tick unexecuted. Build it from existing
`TileCombatResolveTests` fixture patterns, not a mock combat loop. Direct fixture placement preserves
the target's epoch unless the test explicitly exercises teleport.

- [ ] **Step 1: Write the failing server and migration tests.** Name the headline tests
  `First_roll_is_on_the_scheduled_tick`, `Next_roll_keeps_the_existing_cadence`,
  `Temporary_range_loss_does_not_restart_the_attempt`, `Invalid_due_attempt_requires_a_fresh_lead`,
  `Null_rules_create_no_attempt_and_cancel_live_attempts`, and
  `Enabled_custom_registry_requires_preparation_state` from Review Focus 4.

```csharp
using var fight = PreparationScenario.Create();
fight.Step(); // Tick 100 announces impact 103.
Assert.Equal(103L, fight.Preparation().ImpactTick);
Assert.Empty(fight.Rules.Rolls);
fight.AdvanceTo(103);
Assert.Empty(fight.Rules.Rolls);
fight.Step();
Assert.Equal(103L, Assert.Single(fight.Rules.Rolls).Tick);
Assert.Equal(117L, fight.Preparation().ImpactTick);
fight.AdvanceTo(118);
Assert.Equal(new long[] { 103, 117 }, fight.Rules.Rolls.Select(x => x.Tick));
```

  Add separate tests with these assertions, using the same fixture setup:

```csharp
Assert.Equal(oldId, afterTemporaryRangeLoss.AttackId);
Assert.Equal(TileCombatPreparationEndReason.IllegalReach, cancelled.Reason);
Assert.Empty(noRules.Rules.Rolls);
Assert.False(noRules.Server.TryGetCombatPreparation(noRules.Attacker, out _));
Assert.Equal(TileCombatPreparationEndReason.RulesUnavailable, nullCancellation.Reason);
Assert.True(restored.ImpactTick >= restoredAt + 3);
Assert.True(restored.ImpactTick >= cancelled.ImpactTick);
Assert.Equal(beforeHandoff, afterHandoff);
Assert.Throws<ArgumentException>(BuildEnabledServerWithMissingPreparationRegistration);
```

  Parameterize cancellation over disengage, retarget, profile fields, permission, missing/dead body,
  epoch change and invalid profile. Same-target repeated commands keep identity. Invalidity on the
  impact tick cannot create a replacement in that pass. Cover disabled null-rules behavior, lead
  profiles at a different tick length, mutual kills, misses and zero-damage hits.
- [ ] **Step 2: Run red.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparationServerTests|FullyQualifiedName~TilePreparationMigrationTests'`.
  Expect failing first-hit timing, cancellation or missing API assertions.
- [ ] **Step 3: Integrate the preparation adapter.** Keep one existing roll/apply/death pipeline.
  After ordinary cooldown decrement, validate or create attempts from committed state. Add only due
  valid attempts to the existing roll order. Capture their profile/identity before rolling. Write the
  resolved cooldown and consume the attempt before its one `OnCombatEvent` call, so a callback delay
  sees completed state and changes only the next deadline. Create successors only after
  callbacks and deaths settle. A null `CombatRules` creates nothing and cancels live attempts with
  reason 9 at the next pass. Resolve rules availability before cadence/profile reads.
  Gather cancellations without requiring an entity to remain alive for the later terminal serve.
- [ ] **Step 4: Wire migration and lifecycle cleanup.** Registration survives handoffs, has no
  `Replicate` or `Persist` channel, and is created lazily for enabled attackers only. Despawn and
  disconnect retire attempts without leaking state. Profile changes before a due tick replace the
  attempt subject to retained readiness. Do not sample future route tiles for eligibility.
- [ ] **Step 5: Run green synchronously.** Run the red command, then the area project with
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TileCombat|FullyQualifiedName~TilePreparation|FullyQualifiedName~TileFootprintCombat'`.
  Both must exit 0. Fix warnings at source and rerun affected checks after refactoring.
- [ ] **Step 6: Commit explicit task paths.** Subject: `feat(combat): schedule authoritative prepared attacks`.

### Task 3: Additive delay and phase-correct readiness

**Files:** Create `TileWorldServer.PreparationDelay.cs`, `TilePreparationDelayTests.cs`.
Modify `TileWorldServer.Combat.cs` at `DelayAttack`, `TileWorldServer.Preparation.cs`, and
`TileCombatPreparationScheduler.cs` for one pure deadline-revision operation.

**Interfaces:** Consumes Tasks 1 and 2. Produces server
`bool TryGetAttackReadyTick(long attackerNetId, out long tick)` and internal static scheduler
`bool TryDelay(ref TileCombatPreparationState state, long tick, byte ticks, long cooldownReadyTick,
out byte acceptedTicks, out TilePreparationFailure failure)`.
The adapter owns whether this pass's decrement has run. Do not infer it from the presence of an active
attempt. Its readiness read is valid in before-tick, after-movement, outcome callback and between-tick calls.

- [ ] **Step 1: Write failing delay tests.** Name tests
  `Initial_preparation_delay_works_with_zero_ordinary_cooldown`,
  `Delay_after_cancellation_charges_retained_readiness`,
  `Idle_delay_overlaps_new_preparation`, `Readiness_agrees_across_the_decrement_seam`,
  `Outcome_callback_delay_applies_to_the_next_attempt`, `Delay_saturates_without_identity_wrap`.
  Assert the spec examples and each revision boundary:

```csharp
Assert.Equal((103L, 105L, 106L),
    (delayed.PrepareTick, delayed.StrikeTick, delayed.ImpactTick));
Assert.Equal(original.AttackId, delayed.AttackId);
Assert.Equal(original.Revision + 1, delayed.Revision);
Assert.Equal(106L, readinessAfterCancelAndDelay);
Assert.Equal(105L, impactWithReady105At100);
Assert.Equal(103L, impactWithReady101At100);
Assert.Equal(120L, nextImpactAfterDelay3InOutcomeAt103);
Assert.Equal(255L, saturatedReadyTick - referenceTick);
Assert.Equal(beforeZeroDelay, afterZeroDelay);
Assert.True(afterRevisionExhaustion.AttackId > beforeRevisionExhaustion.AttackId);
```

  A one-tick delay during the strike adds one tick, not a new full lead. Test repeated delay,
  retained readiness longer than ordinary cooldown, unknown attacker, live idle attacker, both sides
  of decrement and no active attempt. At revision `uint.MaxValue`, replace identity without wrapping
  or losing the effective delay. At final attack ID exhaustion, refuse new preparation safely.
- [ ] **Step 2: Run red.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TilePreparationDelayTests`.
  Require a deadline or missing-API failure.
- [ ] **Step 3: Implement delay and readiness.** Branch from the existing `DelayAttack` only for
  enabled preparation state. An active or retained future deadline gains the effective delay up to
  255 outstanding ticks. Shift all active boundaries together and revise identity. An idle attacker
  without retained readiness uses the existing byte cooldown path. Zero changes neither identity nor
  deadline. Keep one absolute readiness calculation used by scheduling and the public getter.
  Outcome callbacks cannot revise the completed hit, and their delay is included before announcing
  the next attempt. No additional three-tick tax after existing readiness.
- [ ] **Step 4: Run green and legacy delay regression.** Run the same command, then
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparation|FullyQualifiedName~TileCombatDelayTests'`.
  Wait for both exit 0. Refactor the duplicate deadline arithmetic, then rerun if changed.
- [ ] **Step 5: Commit explicit task paths.** Subject: `feat(combat): revise preparation deadlines for attack delays`.

### Task 4: Deterministic wire codecs and atomic chunk assembly

**Files:** Create `TileProtocol.Preparation.cs`, `TileCombatPreparationFrames.cs`,
`TileCombatPreparationAssembler.cs`, `TilePreparationWireTests.cs`, `TilePreparationAssemblyTests.cs`.
Modify `TileProtocol.Frames.cs` only for the two reserved tags. Do not change legacy combat codecs.

**Interfaces:** Consumes Task 1 records. Produces internal
`TilePreparationChunkHeader(long ServerTick, ushort ChunkIndex, ushort ChunkCount)` and
`TileCombatTerminal` with the exact terminal record fields from spec section 6. Keep its enum
`TileCombatTerminalKind : byte` to `Resolved=1`, `Cancelled=2`.
Use these internal frame records. The record count is encoded from the chosen slice, not a second
header field. Frame arrays are owned snapshots that are never reused as decode buffers.

```csharp
internal readonly record struct TileCombatTerminal(
    long AttackerNetId, long TargetNetId, ulong AttackId, uint Revision,
    uint PresentationKey, long ImpactTick, TileCombatTerminalKind Kind,
    TileCombatPreparationEndReason Reason, ushort Amount, byte HitKind, byte Flags);
internal sealed record TilePreparationStateFrame(
    long ServerTick, TileCombatPreparation[] Records);
internal sealed record TilePreparationTerminalFrame(
    long ServerTick, TileCombatTerminal[] Records);
```

```csharp
// Internal static methods on TileProtocol. Terminal overloads use TileCombatTerminal.
byte[] EncodePreparationChunk(in TilePreparationChunkHeader header,
    IReadOnlyList<TileCombatPreparation> records, int start, int count);
bool TryDecodePreparationChunk(ReadOnlySpan<byte> data,
    out TilePreparationChunkHeader header, List<TileCombatPreparation> into);
byte[] EncodePreparationTerminalChunk(in TilePreparationChunkHeader header,
    IReadOnlyList<TileCombatTerminal> records, int start, int count);
bool TryDecodePreparationTerminalChunk(ReadOnlySpan<byte> data,
    out TilePreparationChunkHeader header, List<TileCombatTerminal> into);
// Internal methods on TileCombatPreparationAssembler.
bool TryAddState(in TilePreparationChunkHeader header,
    IReadOnlyList<TileCombatPreparation> records, out TilePreparationStateFrame? complete);
bool TryAddTerminals(in TilePreparationChunkHeader header,
    IReadOnlyList<TileCombatTerminal> records, out TilePreparationTerminalFrame? complete);
void Clear();
```

`true` plus null complete means a valid pending chunk. `false` means reject and clear only pending
assembly. Already published client state is never an assembler buffer. Enforce expected next chunk,
matching tick/count/tag, no interleaving and bounded storage before allocation.

- [ ] **Step 1: Write failing wire and assembly tests.** Name tests
  `Preparation_frames_have_exact_little_endian_bytes`,
  `Terminal_frames_preserve_resolution_and_all_nine_reasons`,
  `Malformed_final_chunk_preserves_the_previous_complete_frame`,
  `Counts_lengths_and_deadlines_are_validated_atomically`.

```csharp
Assert.Equal(66, stateBytes.Length); // 16 + 50.
Assert.Equal(62, terminalBytes.Length); // 16 + 46.
Assert.Equal(16, emptyStateBytes.Length);
Assert.Equal(expectedLiteralBytes, stateBytes);
Assert.Equal(9, reasonByte);
Assert.Null(pendingComplete);
Assert.False(malformedAccepted);
Assert.Equal(previousPublishedFrame, publishedAfterFailure);
Assert.Equal(65280, maximumComplete.Records.Length);
```

  Use a literal byte fixture with distinct multibyte values. Cover 0, 1, 255 and 256 records, 256
  chunks, count/length mismatch, unknown schema/tag/kind/flags/reason, zero identities, bad tick
  relationships, duplicate attackers, wrong final count and allocation bounds. Presentation key zero
  is valid. Reject an active record due on or before the header tick. Cancellation fields must be zero
  where required. Legacy `TileCombatWireTests` golden bytes must remain unchanged.
- [ ] **Step 2: Run red.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparationWireTests|FullyQualifiedName~TilePreparationAssemblyTests'`.
  Require missing codecs or failing byte/atomicity assertions.
- [ ] **Step 3: Implement codecs with `BinaryPrimitives` explicit little-endian reads/writes.** Copy
  spec field order exactly. Reject malformed input without throwing, leaving output lists empty.
  Encoders reject invalid arguments rather than emit malformed frames. Assemble into private bounded
  storage and publish owned complete arrays only after all chunks validate. No legacy padding rule
  is needed because the new frame lengths cannot equal the 24-byte command frame.
- [ ] **Step 4: Run green and legacy wire checks.** Run the red command, then
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparationWireTests|FullyQualifiedName~TilePreparationAssemblyTests|FullyQualifiedName~TileCombatWireTests'`.
  Require exit 0 and inspect all golden-byte assertions. Refactor shared header validation only.
- [ ] **Step 5: Commit explicit task paths.** Subject: `feat(netcode): encode combat preparation frames`.

### Task 5: Authoritative serving and client lifecycle ledger

**Files:** Create `TileWorldServer.PreparationServe.cs`, `TileWorldClient.Preparation.cs`,
`TileCombatPreparationLedger.cs`, `TilePreparationDeliveryTests.cs`, `TilePreparationInterestTests.cs`.
Modify server `.Tick.cs` and `.Sessions.cs`, client `.Snapshots.cs` and `.cs`, client config and
`TileServerReason.cs` and `TileCombatHarness.cs`. Extend the harness with trailing optional
`bool combatPreparationEnabled = false`, preserving every existing caller's behavior.

**Interfaces:** Consumes Tasks 2-4. Produces the client config, read and callbacks declared above.
Internal ledger methods are `TilePreparationDispatch ApplyState(TilePreparationStateFrame frame)`,
`TilePreparationDispatch ApplyTerminals(TilePreparationTerminalFrame frame)`,
`bool TryGet(long attacker, out TileCombatPreparation preparation)`, `void Forget(long netId)` and `void Clear()`.
Both Apply methods return an immutable dispatch batch, `TilePreparationDispatch`, with applied state
tick when present and separate owned arrays of `PreparedCombatEvent` and `CombatPreparationEnded`.
No callback executes while mutable ledger or decoder buffers are being enumerated.

```csharp
internal sealed record TilePreparationDispatch(
    long? AppliedStateTick, PreparedCombatEvent[] Results, CombatPreparationEnded[] Ended);
```

- [ ] **Step 1: Write failing delivery and interest tests.** Use real server/client transport and
  byte capture, preserving the existing phase-offset harness. Include Review Focus 1 and 5 as
  `Prepared_callbacks_can_reenter_poll_without_loss_or_duplication` and
  `Successor_snapshot_does_not_consume_the_previous_terminal`, plus
  `Entering_interest_receives_the_current_schedule`,
  `Terminal_on_interest_exit_reaches_the_previous_viewer`,
  `A_border_ghost_does_not_delay_the_owner_schedule`, and
  `Disabled_mode_emits_only_legacy_combat_frames`. Add
  `Oversized_state_set_is_refused_atomically_for_only_its_viewer` and
  `Disconnect_clears_preparation_state` for the orchestrator's overflow ruling.

```csharp
Assert.Equal(new byte[] { 0, 4, 5 }, capturedTagOrderForImpactTick);
Assert.Equal(1, legacyServerAwardCalls);
Assert.Equal(1, preparedClientResults);
Assert.Equal(0, legacyClientResults);
Assert.Equal(nextAttackId, clientPreparation.AttackId);
Assert.Equal(oldAttackId, Assert.Single(deliveredResults).AttackId);
Assert.Equal(expectedDistinctIds, reentrantDeliveredIds);
Assert.Equal(beforeDuplicateCounts, afterDuplicateCounts);
Assert.Equal(serverPreparation, newlyInterestedClientPreparation);
Assert.DoesNotContain(capturedDisabledTags, tag => tag == 4 || tag == 5);
Assert.Empty(rejectedViewerPreparationFrames);
Assert.Equal(1L, server.RejectedCombatPreparationFrameSetCount);
Assert.True(otherViewer.IsJoined);
Assert.False(disconnectedViewer.TryGetCombatPreparation(attacker, out _));
```

  Add stale state/terminal tests, all chunks at one tick, cancellation followed by replacement, repeated
  complete snapshots, disconnect/reconnect, removal followed by delayed terminal and actor-only versus
  target-only interest. A terminal for an already-retired active sample still delivers once. A later
  revision cannot be replaced by an older one. Reentrant `Poll()` must not clobber either result batch.
- [ ] **Step 2: Run red.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparationDeliveryTests|FullyQualifiedName~TilePreparationInterestTests'`.
  Expect missing frames, callbacks or cache lifecycle assertions.
- [ ] **Step 3: Implement server serving.** Collect active schedules from authoritative owners,
  sorted by attacker. Include records if either participant is visible. Build and validate the whole
  state/terminal set before sending any preparation chunk. Send snapshot, full state set and terminal
  set on `ReliableOrdered`, with cancellations first and resolutions in existing roll order.
  Keep the prior viewer set for one serve, prune it on disconnect, and emit one empty state chunk.
  Suppress tag 3 only in enabled sessions, without suppressing server award hooks.
- [ ] **Step 4: Implement bounded failure handling and client ledger.** Validate nonnegative configured
  player/actor budgets and reject an enabled configuration whose `MaxPlayers + MaxActorsPerCell`
  already exceeds 65,280, using checked widened arithmetic. This is a minimum boot check, not a bound
  on all visible remote attackers. Count actual active and terminal sets before encoding. If either
  exceeds the cap, send neither preparation set, increment named diagnostic
  `RejectedCombatPreparationFrameSetCount`, and terminate that viewer session with a developer
  overflow reason so it cannot retain an indefinitely stale partial fight. Other viewers still serve.
  Add boundary tests with artificial record collections instead of spawning 65,281 real actors.
  Apply complete frames atomically, distinguish active retirement from terminal delivery, and enforce
  per-attacker high-water IDs/revisions with bounded storage. Clear lifecycle state before raising
  disconnect. A malformed preparation set clears pending assembly, preserves the last published
  frame and increments client diagnostic `RejectedCombatPreparationFrameCount`. Existing frame
  demultiplexing discards a refused frame. Do not invent a `NetClient.Disconnect` API that does not exist.
  For overflow use existing `TileWorldServer.Kick(int slot, string reasonToken)` with new
  `TileServerReason.CombatPreparationOverflow = "ke:combat-preparation-overflow"`. This is a developer
  protocol token. The ordinary in-memory hub does not perform a server-requested disconnect, so the
  viewer-isolation test must wrap its transport's `Disconnect` to call `hub.DisconnectClient` for the
  matching test client. Prove the disconnect came from the overflow refusal, not an unconditional
  test-side drop, and prove server combat and a second viewer keep progressing.
- [ ] **Step 5: Run green synchronously.** Run the red command, then the whole area project in Release.
  Require exit 0, zero failed tests and preserved legacy event reentrancy behavior. No live sockets
  or graphics are required. Refactor buffer ownership before committing if tests expose aliasing.
- [ ] **Step 6: Commit explicit task paths.** Subject: `feat(netcode): deliver preparation state and outcomes`.

### Task 6: Presentation clock and pure stage samples

**Files:** Create `TileCombatPreparationSampler.cs`, `TileCombatPresentationClock.cs`,
`TilePreparationSamplerTests.cs`, `TilePreparationClockTests.cs`. Modify client `.Preparation.cs`,
`.Snapshots.cs` and `.cs` only where complete snapshots, lifecycle reset and presentation time advance.

**Interfaces:** Consumes Task 1 records and Task 5 client state. Produces:

```csharp
public enum TileCombatPreparationStage { Hold, Prepare, Strike, AwaitingOutcome }
public readonly record struct TileCombatPreparationSample(
    TileCombatPreparationStage Stage, float Progress);
// Public static method on TileCombatPreparationSampler.
public static TileCombatPreparationSample Sample(
    in TileCombatPreparation preparation, double serverTick);
// Public read on TileWorldClient.
public double CombatPresentationTick { get; }
// Internal TileCombatPresentationClock methods.
void Observe(long serverTick);
void Advance(float dt, float tickSeconds);
void Clear();
double Tick { get; }
```

Before the first valid server snapshot, the client read is `-1`. Hold progress is zero, AwaitingOutcome
progress is one. A profile with lead equal to strike has no Prepare interval and starts at Strike.
Non-finite sample time yields Hold/zero. Invalid `dt` advances nothing, matching existing presentation
sanitization. Invalid schedule arguments to the public sampler are rejected with `ArgumentException`.

- [ ] **Step 1: Write failing stage and clock tests.** Name tests
  `Three_tick_schedule_has_two_ticks_of_prepare_and_one_of_strike`,
  `Revised_schedule_returns_to_hold_until_its_new_start`,
  `Late_samples_enter_the_current_stage_without_creating_an_outcome`,
  `Clock_is_monotonic_bounded_and_reset_on_disconnect`.

```csharp
Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Hold, 0f), At(99));
Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Prepare, 0.5f), At(101));
Assert.Equal(new TileCombatPreparationSample(TileCombatPreparationStage.Strike, 0.5f), At(102.5));
Assert.Equal(TileCombatPreparationStage.AwaitingOutcome, At(103).Stage);
Assert.Equal(TileCombatPreparationStage.Hold, DelayedAt(102).Stage);
Assert.Equal(TileCombatPreparationStage.Prepare, DelayedAt(103).Stage);
Assert.Equal(101d, clock.Tick); // Anchor 100, elapsed at least one tick.
Assert.Equal(beforeOldSnapshot, afterOldSnapshot);
Assert.Equal(-1d, disconnectedTick);
Assert.Empty(generatedOutcomes);
```

  `At` samples preparation 100/102/103 and `DelayedAt` 103/105/106. Test boundaries at exact starts,
  large positive elapsed time, NaN, infinities, negative `dt`, lead equal to strike and unequal game
  tick lengths. A same-poll burst of snapshots advances to the newest tick without replaying skipped
  preparation or duplicating results.
- [ ] **Step 2: Run red.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparationSamplerTests|FullyQualifiedName~TilePreparationClockTests'`.
  Expect absent sampler/clock or incorrect boundary assertions.
- [ ] **Step 3: Implement the sampler and clock.** Sample integer schedule boundaries using fractional
  time, never inferred cadence. Anchor only on a newer applied server snapshot, advance at most one
  tick beyond it and reuse sanitized presentation `dt`. A result still comes exclusively from Task 5.
  Do not modify movement interpolation, turn pose sampling into engine animation or buffer an outcome
  to manufacture preparation. Consumers own impact pose, recovery and blending under short notice.
- [ ] **Step 4: Run green.** Run the red command and then
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparation|FullyQualifiedName~TileWorldClient'`.
  Require both exit 0. Refactor common boundary calculations, then rerun affected tests if changed.
- [ ] **Step 5: Commit explicit task paths.** Subject: `feat(combat): expose preparation stage sampling`.

### Task 7: Cross-seam regression proof

**Files:** Create `TilePreparationLoopbackTests.cs`, `TilePreparationCompatibilityTests.cs`.
Modify only an owning preparation implementation/test file when a new regression fails.

**Interfaces:** Consumes the completed server/client API. Produces engine evidence for the consumer
handoff, not new public surface. Use `PreparationScenario`, the extended `TileCombatHarness`, and
existing connection-gate and handoff fixtures. No duplicate production scheduler in test code.

- [ ] **Step 1: Add the combined regression assertions.** Name tests
  `Independent_client_clock_observes_the_server_schedule_and_real_impact`,
  `Mutual_kill_and_award_callbacks_keep_one_outcome_per_roll`,
  `Food_revision_after_entry_survives_handoff_and_late_poll`,
  `Restored_rules_require_new_preparation_after_null_cancellation`,
  `Legacy_mode_retains_its_first_hit_and_protocol`, and
  `Consumer_protocol_version_refuses_mixed_preparation_modes`.

```csharp
Assert.Equal(3L, firstImpactTick - firstEligibleTick);
Assert.Equal(14L, secondImpactTick - firstImpactTick);
Assert.Equal(authoritativeResults, observedResultIdentities);
Assert.Equal(2, mutualKillResults.Count);
Assert.Equal(0, healthAfterMutualKillA);
Assert.Equal(0, healthAfterMutualKillB);
Assert.Equal(0, rollsWhileRulesUnavailable);
Assert.True(restoredImpactTick >= restoredEligibleTick + 3);
Assert.Equal(legacyFirstEligibleTick, legacyFirstImpactTick);
Assert.True(mixedConsumerVersionWasRefused);
```

  Run locals and observers with phase-offset clocks, approach while running/walking, changing target
  tiles, death/respawn, disconnect, different equipment profile keys and short/long idle delays.
  Verify preparation causes no damage, XP hook or combat-logout stamp, and every real result calls
  the server hook once. The connect test uses two deliberately different game protocol strings,
  not a claim that the engine can infer a consumer's incorrect matching string.
- [ ] **Step 2: Run the new tests before any correction.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TilePreparationLoopbackTests|FullyQualifiedName~TilePreparationCompatibilityTests'`.
  Existing behavior may already pass. If any assertion fails, record that failure and make a focused
  correction in its owning file, then rerun. Do not manufacture a red by weakening earlier correct code.
- [ ] **Step 3: Run the full headless area suite.**
  `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release`
  must exit 0. Confirm no GPU/window launch or external service was used. If it fails, stop and fix the
  owning seam before proceeding to release preparation.
- [ ] **Step 4: Commit explicit task paths.** Subject: `test(combat): verify preparation across tick and network seams`.

### Task 8: Reference docs, version selection and verified release handoff

**Files:** Modify `KhaozEngine.TileWorld.Netcode/README.md`, `docs/USING-KHAOZENGINE.md`,
`docs/design/TILE-COMBAT-PREPARATION-DESIGN-2026-09-28.md`, `docs/INDEX.md`, `CHANGELOG.md`,
`Directory.Build.props` and guarded version examples in `README.md`. Touch other Markdown only when
the required name/behavior sweep finds a stale statement. Do not edit Grimhollow in this task.

**Interfaces:** Consumes all implemented APIs and evidence. Produces documented opt-in examples,
a correctly selected staged engine version and a clean verified engine commit for the controller.
The controller retains integration, packing, tagging and published-release verification.

- [ ] **Step 1: Inspect current origin/main, version and release tags.** Fetch read-only remote state
  and record the selected version in the execution report. Ride a currently staged unreleased version,
  otherwise select the next free additive minor. Do not hard-code a number from this planning date.
- [ ] **Step 2: Update the consumer references.** Show matching server/client mode configuration,
  public profile implementation, schedule reads, callbacks, sampler and readiness API. State exact
  delay overlap, null-rules cancellation, no default timing change, callback distinction and game
  protocol bump requirement. Move shipped API knowledge out of the design-only surface. Mark the spec
  and index implemented for the chosen staged version while recording consumer adoption as pending.
- [ ] **Step 3: Update version/changelog together and perform the Markdown sweep.** Search
  `CombatPreparation`, `PreparedCombatEvent`, `CombatRules`, `DelayAttack`, first-hit descriptions and
  legacy event assumptions with `rg` over all Markdown. Review every affected reference. Docs/version
  edits get guards and an executable build of API use through the existing tests, not mirror tests of
  prose. Keep one batch version bump and its matching changelog entry in the same commit.
- [ ] **Step 4: Run final verification synchronously from the worktree.**

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

  Each command must finish with exit 0, with zero compiler warnings and zero failed selected tests.
  Report skipped GPU categories as outside this headless feature, without claiming a GPU proof.
  A failure blocks the commit. Follow hooks as written and never bypass verification.
- [ ] **Step 5: Commit the explicit documentation/version paths.** Subject:
  `docs(combat): document prepared attack consumer contract` if riding a staged version, or
  `feat(<selected-version>): stage combat preparation` when selecting a new version. Then inspect
  status and commit contents and give the controller all task SHAs and exact check exits.
- [ ] **Step 6: Hand off release and consumer dependencies.** The controller merges current main
  into the task branch, resolves and reruns required verification after reconciliation, integrates and
  pushes the verified engine result, then runs the repository's guarded pack/release flow under the
  owner's existing authorization. Workers stop at the verified commit. No automatic tag is part of
  a task's commit step. Confirm durable packages and the released version before any game pin changes.
  The separate Grimhollow adoption plan must use the actual released API, update the engine/tools pins
  and vendored feed, replace cadence prediction for all styles, revise food fences and protocol version,
  update no-windup rulings, and prove loopback plus visual timing and #371 spacing. No game code or
  animation authoring is pre-authorized by this engine implementation plan.

## Self-review and coverage record

| Approved spec requirements | Owning tasks |
| --- | --- |
| Generic opt-in profile, records, identity, durations | 1, 2 |
| First/continuing timing, committed reach, rule availability, death and movement | 2, 7 |
| Delay, retained readiness, saturation and callback boundary | 3, 7 |
| Exact byte layout, reasons 1-9, bounded atomic assembly | 4 |
| Interest, stale delivery, reentrancy, deduplication and callback separation | 5, 7 |
| Fractional clock, phase boundaries and no predicted outcome | 6, 7 |
| Disabled compatibility, version gate and lifecycle | 2, 5, 7 |
| Public docs, version selection and release dependency | 8 |
| Grimhollow poses, food reply fence, localization/rulings and #371 | Separate adoption plan after release, handed off by 8 |

All five Review Focus inputs have named tests in their owning task. Public signatures are defined once
above and reused consistently. New runtime methods are specified at their owning seam, with test
assertions rather than complete implementation bodies. The plan intentionally leaves pose curves and
consumer release verification to their owning workstream.

Orchestrator ruling for this plan: Task 5 refuses the entire oversized preparation frame set before
encoding, increments `RejectedCombatPreparationFrameSetCount`, and disconnects only its viewer with
a developer-visible overflow reason. Keeping that connection without fresh full state could retain
stale preparations. Other viewers and server combat continue. Boot-time budget validation remains
the normal prevention. This fills in the spec's connection disposition without changing its wire
or gameplay contract. There are no unresolved design-plan ambiguities after this ruling.
