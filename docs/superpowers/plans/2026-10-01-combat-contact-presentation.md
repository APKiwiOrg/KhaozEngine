# Tile Combat Contact Presentation Implementation Plan

**Execution status:** Parked after the owner's continuous-movement pivot. [Grimhollow #371](https://github.com/APKiwiOrg/Grimhollow/issues/371)
is closed as superseded. Task 1's public-contract prototype is preserved at `5e4c3a35e` on
`fix/combat-presentation-spacing`, with 41 focused tests passing. Contact correction is not implemented.
The prototype has not received final solution verification, integration or release. Tasks 2 to 11 were not
started. [Engine #1216](https://github.com/APKiwiOrg/KhaozEngine/issues/1216) remains open as tile-world roadmap
work. The requirements below preserve the approved design history, not an active Grimhollow adoption commitment.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add opt-in, continuous, collision-safe combat body presentation with stable legal footprint layouts and receipt-frame outcome measurements, then hand a released engine capability to Grimhollow for its mandatory adoption gates.

**Architecture:** Keep raw prediction, remote interpolation and combat scheduling unchanged. `TileCombatContactPresenter` owns attempts, component layouts, anchors, persistent controllers and diagnostics, using bounded source frames and a pure motion helper. `TileWorldClient.ContactPresentation.cs` only captures inputs, observes accepted lifecycle dispatches, advances once after raw sampling and exposes completed body frames.

**Tech Stack:** C#, .NET, `System.Numerics`, existing TileWorld collision/pathfinding, existing Replication and Netcode, headless xUnit tests.

**Spec:** [TILE-COMBAT-CONTACT-PRESENTATION-DESIGN-2026-10-01.md](../../design/TILE-COMBAT-CONTACT-PRESENTATION-DESIGN-2026-10-01.md), explicitly owner-approved in the dispatch for this plan. Its approval-status prose predates that instruction. [COMBAT-CONTACT-SPACING-2026-10-01.md](../../design/COMBAT-CONTACT-SPACING-2026-10-01.md) remains measured history, including the four raw 1.250 m walk and 1.500 m run characterization cases. It does not replace the selected stable-layout design or its revised weighted table.

## Global Constraints

- Worktree: `/Users/antonio/KhaozEngine/.worktrees/combat-presentation-spacing`, branch `fix/combat-presentation-spacing`, planning baseline `1ab1d67105`. Inspect `pwd`, branch, HEAD and status before each implementation dispatch. Preserve concurrent edits and stage explicit paths.
- Owner approval covers the written design. The owner reviews this plan and chooses execution before implementation. This planning artifact changes no product code, package version or release state.
- All combat contact types belong to `KhaozEngine.TileWorld.Netcode`. No renderer, game, new dependency, server-reach, movement-schedule, wire, outcome, damage or feedback-timing change.
- Null `CombatContactPresentation` preserves exact existing raw reads and callbacks, reports zero contact velocities and allocates no contact histories or controllers. Enabling requires `CombatPreparationEnabled`.
- Defaults remain `ResponseSeconds = 0.02f`, `ReleaseSeconds = 0.15f`, `MaxAdjustmentSpeedTilesPerSecond = 4f`, `MaxGoalOffsetTiles = 3f`, `ContactToleranceTiles = 0.04f`, `TerminalHoldTicks = 2`, `MaxParticipants = 256`.
- Validate all five scalar settings as finite and positive, tolerance less than goal-offset limit, hold at least one tick and capacity at least two. Settings are tiles/seconds. Public positions, velocities, corrections and errors are world metres, converted with `Presenter.TileSize`.
- Source history holds the last 32 strictly newer successfully applied movement snapshot ticks, including local authoritative state and all visible movement bodies. Do not assemble remote routes or alter snapshot admission and its existing entity/payload bounds.
- Attempt identity is `(connectionGeneration, attackerNetId, AttackId)` with monotonically accepted revisions. Controller identity is `(connectionGeneration, netId, teleportEpoch)`. Controllers survive attempts, revisions, graph changes and ordinary releases.
- Use actual legal footprint centres, not normalized one-metre directions. Retain legal layouts during pursuit's temporary illegal two-tile committed gap. Only a common snapshot legal for every constrained edge can replace an existing component's geometry.
- Advance after `Poll` and raw presentation, once per presentation call. `h = min(valid_positive_dt, 1/30 second)`. Invalid, zero, negative, NaN and infinite dt spend no correction budget. No result-triggered advance, snap, gain, zeroing or feedback delay.
- Free motion uses `alpha = 1 - exp(-h / ResponseSeconds)`, capped to `MaxAdjustmentSpeedTilesPerSecond * TileSize * h`. Release uses `ReleaseSeconds`. Planar body travel is bounded by raw travel plus the adjustment budget, except a named real raw cut.
- X/Z correction only. Resample ground on the existing plane, retain raw yaw, and publish actual finite-difference body/raw velocities. Cuts start with zero velocities and never count as gait strides.
- Release tethers hold at most 64 waypoints. Fallback uses at most one complete `TilePathfinder` search per new source tick with `maxRadius: 8`. Hold safely without a path, report `ReleasePath`, and never teleport to satisfy an absolute offset cap.
- Impact diagnostics are immutable owned snapshots of the most recent completed presentation frame, one per actual accepted result in original delivery order. Reads and repeated reads never advance motion, gait, feedback or the miss count.
- Ordinary acceptance cannot use capacity, conflict, goal-distance or path fallbacks as excuses. Missing information and exceptional constraints have measured named misses with unchanged receipt feedback.
- Use one serialized build/test lane. Run focused red/green tests synchronously per task, record command exit codes, and run the full solution suite once at final verification. Do not repeat tests in loops, run stress proofs or launch consumer clients for engine work. A new edit or reconciliation that invalidates verification justifies the affected recheck.
- New behavior has headless tests in its owning area. No warnings, blanket suppressions, KESIZE baseline growth or arbitrary file splitting. No em/en dashes or prose semicolons in shipped text.
- The additive package version candidate is `20.17.0`, subject to current main, staged versions and tags at finish. Documentation-only planning does not bump it. All guarded version declarations and `CHANGELOG.md` change only with actual package-bearing work.
- The parent orchestrator owns full verification, integration, push, packing, release and game adoption. Workers stop at verified scoped commits. No worker creates tags. The engine's only automatic-release exception is an explicitly pinned and waiting consumer, and only the parent evaluates it through the repository release ritual.

## Review Focus

- A preparation successor arrives in Hold before its future PrepareTick. Preserve a same-target layout when endpoint epochs/sizes match, including across a result burst, without release/re-entry (Tasks 4, 7, 9).
- A result callback re-enters `Poll` or disconnects/disposes while other callbacks are pending. Observe before callbacks, keep the previous completed body frame readable, and fence the abandoned generation (Task 7).
- A fractional 2x2 footprint traverses a wall, corner, narrow passage or changed topology. Sweep the entire footprint, keep return motion safe, and never advertise contact merely because endpoint tiles look legal (Task 6).
- A cow disappears in the killing snapshot while a linked carcass uses a different raw base. Transfer the last shown world point, retain it for only two presentation frames, and preserve unavailable-impact diagnostics (Task 8 and downstream gate).
- Invalid dt, a stall, an anchor change or a target reversal coincides with impact. Respect the ordinary frame budget and never spend a special impact correction or fabricate velocity (Tasks 5, 7, 9).

---

## Existing seams and file ownership

Read the spec, `AGENTS.md`, and matching sections of `docs/CONTRIBUTOR-RULES.md` before execution. These are the actual existing APIs at the planning baseline:

| Owner | Existing seam | Use |
| --- | --- | --- |
| `TileWorldClient.Snapshots.cs` | `void OnSnapshot(long localNetId, int ackSeq, long serverTick, byte[] snapshot)` | Capture only after successful `View.TryApply`, before interpolation or callbacks can overwrite the authoritative world |
| `TileWorldClient.Snapshots.cs` | `ReconciliationResult result = Prediction.Reconcile((int)serverTick, basis, ackSeq)` | `HardSnapApplied` and `Teleported` identify local raw cuts. Do not infer them from contact error |
| `TileWorldClient.Preparation.cs` | `void DispatchPreparations(TilePreparationDispatch dispatch)` | Observe accepted states/terminals before external callbacks, with the existing `preparationGeneration` fence |
| `TileCombatPreparationLedger.cs` | `bool TryGet(long attacker, out TileCombatPreparation preparation)` | Add an internal bulk-copy seam, because no enumeration API exists today |
| `TileWorldClient.cs` | `void AdvancePresentation(float dt)` | Advance contact after `Prediction.AdvancePresentation`, `View.InterpolateAt` and `RefreshRemoteSamples` |
| `TileWorldClient.cs` / snapshots | `TilePose LocalPose`, `bool TryGetRemotePose(long netId, out TilePose pose)` | Preserve these raw reads. Collect both kinds of body into a single input frame |
| `TileCombatPresentationClock.cs` | `double Tick`, `void Observe(long serverTick)`, `void Advance(float dt, float tickSeconds)`, `void Clear()` | Reuse the bounded clock for hold expiry, without changing its one-tick ceiling |
| `TileReach.cs` | `bool Contains(TileCollisionMap map, TileRect footprint, int plane, TileCoord from, int agentSize)` | Validate each directed attempt with target footprint, target plane and actual attacker size |
| `TilePresenter.cs` | `TilePose PoseAt(TileRect footprint, int plane, TileDirection facing = TileDirection.S)` | Legal footprint layout centres |
| `TilePresenter.cs` | `TilePose PoseAt(Vector2 tilePlanar, float planeIndex, TileDirection facing)` | Ground-resample an already-centred world point after undoing its half-tile convention |
| `TileWorldSpace.cs` | `float TileX(float worldX, float tileSize)`, `float TileZ(float worldZ, float tileSize)` | Inverse X/Z conversion, including world-Z negation |
| `TileCollision.cs` | `bool CanStand(TileCollisionMap map, int x, int z, int plane, int agentSize = 1)`, `bool CanStep(TileCollisionMap map, int x, int z, int plane, TileDirection dir, int agentSize = 1)` | Existing occupancy, wall, internal-edge and diagonal-corner rules |
| `TileCollisionMap.cs` | `TileCollisionFlags Get(int x, int z, int plane)`, `EnsureRegion`, `RemoveRegion`, `Or`, `Clear` | Collision geometry. Add a minimal topology revision, because there is no existing topology-change signal |
| `TilePathfinder.cs` | `TilePath FindPath(TileCollisionMap map, int plane, TileCoord start, TileCoord goal, int agentSize = 1, int maxRadius = DefaultMaxRadius, TilePathfinderScratch? scratch = null)` | Visual release fallback only, requiring `path.Reached` and complete valid endpoint connectors |
| `ClientReplicationView.cs` | `void SnapInterpolationToNewest(long netId)` | Existing caller-owned remote cut. Add notification of an effective flush, because the tile client cannot observe external calls today |

`TileWorldClient` currently has no independent remote distance-cut classifier. Do not invent one. An observed remote epoch/plane change resets contact when the raw sample changes. Explicit `View.SnapInterpolationToNewest` calls use the new notification. Keep raw interpolation behavior identical.

New files separate public contracts, bounded authoritative history, presenter layout/lifecycle ownership, pure bounded motion and client integration. Presenter partials may separate layouts and lifecycle within the same owning type. No general movement or replication refactor is included.

## Task 1: Public contract, validation and disabled compatibility

**Files:**
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactPresentationTypes.cs`
- Create: `KhaozEngine.TileWorld.Netcode/TileWorldClient.ContactPresentation.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClientConfig.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.cs` constructor and dispose integration only
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactContractTests.cs`
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactAllocationTests.cs`

**Interfaces:**
- Consumes existing raw reads and `TileWorldClientConfig.CombatPreparationEnabled`.
- Produces the settings record with the seven exact properties/defaults in Global Constraints.
- Produces `TileWorldClientConfig.CombatContactPresentation { get; init; }` of type `TileCombatContactPresentationSettings?`.
- Produces the exact public records and flag values below, and client members `bool TryGetCombatBodyPresentation(long netId, out TileCombatBodyPresentation presentation)`, `IReadOnlyList<TileCombatContactImpact> CombatContactImpacts { get; }`, `long CombatContactMissCount { get; }`, `bool TryTransferCombatBodyPresentation(long previousNetId, long successorNetId)`.

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
public readonly record struct TileCombatBodyPresentation(
    TilePose Pose, Vector3 Velocity, Vector3 BaseVelocity, Vector3 Correction,
    long SourceServerTick, bool Discontinuity, TileCombatContactLimits Limits);
public readonly record struct TileCombatContactImpact(
    ulong AttackId, uint Revision, long AttackerNetId, long TargetNetId,
    long ImpactTick, long GeometryServerTick, Vector3 DesiredRelativePosition,
    float PlanarErrorMetres, bool WithinTolerance, TileCombatContactLimits Limits);
[Flags]
public enum TileCombatContactLimits
{
    None = 0, MissingGeometry = 1, LatePreparation = 2, LateOutcome = 4,
    ChangedGeometry = 8, ConflictingLayout = 16, GoalDistance = 32,
    ParticipantCapacity = 64, Collision = 128, ReleasePath = 256,
    TargetUnavailable = 512, PresentationCut = 1024
}
```

- [ ] Write `Defaults_match_the_approved_contract`, `Invalid_settings_fail_construction`, and `Contact_requires_preparation`. Parameterize each float with zero, negative, NaN and both infinities, plus equality/excess tolerance, hold zero and capacity zero/one. Assert construction throws `ArgumentOutOfRangeException` for invalid scalars/bounds and `ArgumentException` for the missing preparation prerequisite.
- [ ] Write `Disabled_body_reads_match_raw_exactly` and `Unjoined_unknown_and_removed_bodies_have_no_contact_pose`. Assert local/remote poses exactly equal their raw reads, zero vectors, empty impacts, zero misses, and transfer false. Disabled `SourceServerTick` is the last successfully applied movement tick, or -1 before one, not the potentially rejected `ServerTick` field.

```csharp
Assert.Equal(rawPose, shown.Pose);
Assert.Equal(Vector3.Zero, shown.Correction);
Assert.Equal(Vector3.Zero, shown.Velocity);
Assert.Empty(client.CombatContactImpacts);
Assert.Equal(0L, client.CombatContactMissCount);
```
- [ ] Run `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TileCombatContactContractTests`. Expected red exit 1 from missing API or failed assertions. Record exit code.
- [ ] Implement the public types and validation in their owners. Add nullable contact runtime storage that remains null in disabled mode, and an accepted movement-tick scalar updated after successful apply for the disabled metadata read. Enabled functionality can initially return raw samples until later tasks provide the controller.
- [ ] Add an `AllocSensitive` warmed read test using `GC.GetAllocatedBytesForCurrentThread()`. Measure a fixed bounded set of local/remote body reads, impacts and transfer rejection, asserting zero new allocations and a null contact runtime. Compare disabled snapshot/presentation allocations against the existing path rather than asserting the transport itself never allocates.
- [ ] Run the focused contract/allocation filter synchronously. Expected exit 0. Commit explicit task paths as `feat(tile-contact): add opt-in body presentation contract`.

## Task 2: Bounded authoritative source frames

**Files:**
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactSourceFrames.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.ContactPresentation.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.Snapshots.cs` successful apply seam only
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactSourceFrameTests.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/ContactPresentationScenario.cs`

**Interfaces:**
- Consumes `View.Entities`, `World.TryGet(entity, out TileMoveState state)` and Task 1 opt-in runtime. Capture only accepted replication membership, excluding game-created entities that merely carry a `NetId`.
- Produces internal `readonly record struct TileCombatContactSourceBody(long NetId, TileCoord Tile, TileCoord StepFrom, byte StepTicks, byte StepTotal, int FootprintSize, uint TeleportEpoch)`.
- Produces internal `sealed class TileCombatContactSourceFrames` with `const int FrameCapacity = 32`, `long LatestTick { get; }`, `int Count { get; }`, `bool Capture(long serverTick, World world, IReadOnlyDictionary<long, Entity> entities)`, `bool TryGet(long serverTick, long netId, out TileCombatContactSourceBody body)`, and `void Clear()`.
- Produces test fixture `ContactPresentationScenario(bool enabled = true, TileCombatContactPresentationSettings? settings = null, float interpolationDelayTicks = 2f)` with `TileWorldClient Client`, `TileCollisionMap Map`, `void Snapshot(long tick, params (long Id, TileMoveState State)[] bodies)`, `void Preparation(long tick, params TileCombatPreparation[] records)`, `void Resolved(long tick, params PreparedCombatEvent[] results)`, and `void Advance(float dt)`. Use existing protocol encoders and the real receive path, not reflection to invoke private client methods.

- [ ] Write `Capture_precedes_delayed_interpolation_and_keeps_local_authority`, `Older_equal_rejected_frames_do_not_replace_history`, `Thirty_third_accepted_tick_evicts_the_oldest`, and `Sources_include_every_visible_movement_body_without_remote_routes`. Assert source ticks/fields, local authoritative state differs from prediction when expected, retained ticks are exactly the newest 32, and source entries expose no `TileRoute`.

```csharp
Assert.Equal(32, sources.Count);
Assert.False(sources.TryGet(firstTick, targetId, out _));
Assert.True(sources.TryGet(firstTick + 32, targetId, out var newest));
Assert.Equal(authoritativeTarget.Tile, newest.Tile);
```
- [ ] Run `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TileCombatContactSourceFrameTests`. Expected red exit 1.
- [ ] Implement a sorted body capture into a 32-slot owned ring. Capture immediately after successful `View.TryApply` and before `ObservePreparationSnapshot`, `CaptureLatestTiles`, `RefreshObjectStates` or external callbacks. Capture every visible movement entity including local state. Gate stale/equal ticks inside this owner, without changing raw snapshot handling.
- [ ] Exercise a partially applied rejected snapshot, a game-created entity outside `View.Entities` and a later valid recovery. Assert neither replacement history nor a new authoritative identity can be established from the rejected/game-created data. Source storage follows accepted snapshot entity counts, independently of the 256-controller admission cap.
- [ ] Run the focused filter. Expected exit 0. Commit explicit paths as `feat(tile-contact): retain authoritative contact source frames`.

## Task 3: Observe existing cuts and topology changes in their owners

**Files:**
- Modify: `KhaozEngine.Replication/ClientReplicationView.cs` existing snap method only
- Create: `KhaozEngine.Replication/ClientReplicationView.InterpolationCuts.cs` if required by KESIZE, with a minimal partial declaration change
- Modify: `KhaozEngine.TileWorld/TileCollisionMap.cs` mutators and revision property only
- Test: `KhaozEngine.Server.Tests/Replication/ClientReplicationBufferTests.cs`
- Test: `KhaozEngine.TileWorld.Tests/TileWorld/TileCollisionMapRevisionTests.cs`

**Interfaces:**
- Consumes existing `void SnapInterpolationToNewest(long netId)` and all existing map mutators.
- Produces `ClientReplicationView.public event Action<long>? InterpolationSnappedToNewest`, raised once after an effective multi-sample flush completes. Unknown/single-sample no-ops raise nothing. No per-frame cut-history collection is added.
- Produces `TileCollisionMap.public ulong TopologyRevision { get; private set; }`, initially zero, incremented once per mutator call that actually changes region storage or collision flags. A no-op does not increment it. Compare revisions by inequality, including counter wrap.

- [ ] Extend existing replication snap tests to assert one post-flush notification, newest raw sample already selected, no notification for unknown/single-sample calls, and unsubscribed behavior identical to the current API.
- [ ] Add `Only_effective_topology_mutations_increment_revision` covering new/existing `EnsureRegion`, successful/absent `RemoveRegion`, changed/duplicate `Or`, effective/empty `Clear`, and invalid-plane failures. Assert each effective call increments once, not once per changed tile.

```csharp
Assert.Equal(new[] { remoteId }, notifiedIds);
Assert.Equal(newestPosition, rawAfterSnap);
Assert.Equal(beforeRevision + 1UL, map.TopologyRevision);
```
- [ ] Run the two owning-project filters synchronously, one command at a time. Expected red exit 1 for each affected contract.
- [ ] Implement only these minimal observables. Keep notification outside the history enumeration so a handler may safely inspect or re-enter the view. Preserve `CanStep`, map flags and interpolation semantics. Add no distance-based cut rule.
- [ ] Run `dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ClientReplicationBufferTests`, then `dotnet test KhaozEngine.TileWorld.Tests/KhaozEngine.TileWorld.Tests.csproj -c Release --filter FullyQualifiedName~TileCollisionMapRevisionTests`. Both must exit 0. Commit explicit paths as `feat(tile-contact): expose existing visual cut and topology changes`.

## Task 4: Stable legal component layouts and raw anchors

**Files:**
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactPresenter.cs`
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactPresenter.Layouts.cs`
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactRuntimeTypes.cs`
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactLayoutTests.cs`
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactAnchorTests.cs`

**Interfaces:**
- Consumes Task 1 settings/flags, Task 2 source frames and exact `TileReach.Contains` / `Presenter.PoseAt(TileRect, int, TileDirection)` signatures above.
- Produces internal `readonly record struct TileCombatContactRawBody(long NetId, TilePose Pose, TileCoord Tile, int FootprintSize, uint TeleportEpoch, bool AuthoritativeStepInFlight, bool Discontinuity)` and `readonly record struct TileCombatContactGoal(long NetId, Vector2 Offset, bool Releasing, TileCombatContactLimits Limits)`.
- Produces internal `sealed partial class TileCombatContactPresenter(TileCombatContactPresentationSettings settings, TileCollisionMap map, TileCombatContactSourceFrames sources)` with `void ObservePreparations(long appliedStateTick, ReadOnlySpan<TileCombatPreparation> preparations, double combatTick, TilePresenter presenter)`, `void ObserveSourceFrame(TilePresenter presenter)`, and `ReadOnlySpan<TileCombatContactGoal> BuildGoals(ReadOnlySpan<TileCombatContactRawBody> rawBodies, long localNetId, double combatTick, float dt, TilePresenter presenter)`.
- `BuildGoals` updates raw stationary history once per completed advance. All `Vector2` values here use world X/Z metres. Layout/anchor details are available to tests through internal read-only diagnostic snapshots, not new public game APIs.

- [ ] Write footprint tests for cardinal 1x1, both opposing sides of a 2x2, its 0.5-tile lateral/1.5-tile normal vector, reversed edges and unequal sizes. Assert `desired = F_target - F_attacker`, legal reach uses the attacker size, and no vector normalization occurs.
- [ ] Write `Legal_illegal_legal_pursuit_keeps_the_admitted_vector` for walk4/run2 and `Future_prepare_successor_inherits_same_target_layout`. Assert legal one-tile geometry, illegal two-tile snapshot, then legal one-tile geometry never widens the goal. A future Hold successor retains admitted tick/layout and current controller offsets when epochs/sizes match.
- [ ] Write graph tests for a reciprocal pair, three targeters, exact square cycle, merge/split and mixed-time conflicting cycle. Assert reciprocal vectors negate, accepted cycle sum is zero within `0.0001 * TileSize`, merging translates only the newer layout, split points persist, and a conflicting edge is excluded with `ConflictingLayout` until a common legal source resolves it. Its own desired vector remains available for result measurement.
- [ ] Write anchor/capacity tests. Preserve an eligible previous stationary anchor, otherwise choose smallest stationary ID after one world tick at raw speed <= `0.001 * TileSize` with no authoritative step, otherwise local, otherwise centroid. Assert a stationary target does not shuffle. Existing components precede whole new components ordered by creation prepare tick/minimum ID. Reject a whole oversized component and never truncate it.

```csharp
Assert.Equal(admittedVector, vectorDuringIllegalTick);
Assert.Equal(admittedVector, vectorAfterFutureHoldSuccessor);
Assert.True(cycleSum.Length() <= 0.0001f * tileSize);
Assert.Equal(stationaryTargetRaw, stationaryTargetGoal);
```
- [ ] Run the layout/anchor filters synchronously. Expected red exit 1.
- [ ] Implement directed attempt records plus undirected ownership. Admit PrepareTick geometry first, otherwise the initial applied preparation tick or earliest subsequent legal frame. Missing geometry produces no goal. Inherit a matching same-target successor and only refresh at a common legal source. Use older creation prepare tick, then minimum ID, to retain compatible geometry. Preserve controller offsets while goals change.
- [ ] Compute anchor translation from raw points only. Scale all component goals by one common `min(1, limit / max_goal_length)` when needed and mark `GoalDistance`. Retain displaced capacity losers as releasing controllers. Retry excluded geometry on new accepted source ticks, with no edge convergence loop.
- [ ] Run the focused filters. Expected exit 0. Commit explicit paths as `feat(tile-contact): build stable legal contact layouts`.

## Task 5: Pure bounded planar motion and ground sampling

**Files:**
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactMotion.cs`
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactMotionTests.cs`

**Interfaces:**
- Consumes Task 1 settings and existing `TilePresenter`, `TileWorldSpace` conversion methods.
- Produces internal `static Vector2 BoundedDelta(Vector2 currentOffset, Vector2 goalOffset, float dt, float responseSeconds, float maximumSpeedMetresPerSecond)` on `TileCombatContactMotion`.
- Produces internal `static TilePose GroundedPose(TilePresenter presenter, Vector2 worldCentre, int plane, float rawYaw)` and `static Vector3 FiniteDifference(Vector3 previous, Vector3 current, float dt, bool discontinuity)` on that type.

- [ ] Write parameterized motion tests at 60/50 Hz, zero, negative, NaN, both infinities and a 2-second stall. Assert zero correction for invalid dt and `delta.Length() <= 4f * tileSize * min(dt, 1f/30f) + epsilon` for valid dt. With an uncapped small error, assert the exact exponential formula for response 0.02 and release 0.15. A static goal never overshoots.
- [ ] Write revision/anchor/reversal tests that retain the current offset and prove every frame's correction budget. Results do not call any motion method independently. Test non-unit tile sizes 0.5 and 2 so tile/metre conversions cannot pass accidentally at one-metre tiles.
- [ ] Write ground/yaw/velocity tests for slope, bridge-height source and 2x2 footprint centre. `GroundedPose` calls `PoseAt(new Vector2(TileX(x,size)-0.5f, TileZ(z,size)-0.5f), plane, TileDirection.S)` and retains raw yaw. Assert no second footprint half-size shift. Finite difference uses the actual valid frame dt, not capped h, and is zero for initialization/cuts/invalid dt.

```csharp
Assert.True(delta.Length() <= 4f * tileSize * MathF.Min(dt, 1f / 30f) + epsilon);
Assert.Equal(Vector2.Zero, invalidDtDelta);
Assert.Equal(rawYaw, groundedPose.Yaw);
Assert.Equal(Vector3.Zero, velocityOnCut);
```
- [ ] Run `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TileCombatContactMotionTests`. Expected red exit 1.
- [ ] Implement the pure helpers. Correct only X/Z, reject non-finite inputs from entering motion and use no elapsed-time debt. Do not advance game gait or combat phase here.
- [ ] Run the focused filter. Expected exit 0. Commit explicit paths as `feat(tile-contact): bound smooth body correction motion`.

## Task 6: Footprint sweeps, release tether and bounded fallback

**Files:**
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactMotion.Collision.cs`
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactMotionState.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileCombatContactMotion.cs` partial declaration only if needed
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactCollisionTests.cs`
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactReleaseTests.cs`

**Interfaces:**
- Consumes Task 3 topology revision, Task 5 pure motion, existing collision/pathfinder signatures above.
- Produces internal `sealed class TileCombatContactMotionState` holding current offset/body point, bounded tether, release state, reusable radius-8 path scratch, last search source tick and observed topology revision.
- Produces internal `readonly record struct TileCombatContactMotionResult(TilePose Pose, Vector3 Correction, TileCombatContactLimits Limits)`.
- Produces `static bool IsClearSegment(TileCollisionMap map, int plane, int footprintSize, Vector2 startWorldCentre, Vector2 endWorldCentre, float tileSize)` and `static TileCombatContactMotionResult Advance(TileCombatContactMotionState state, in TileCombatContactRawBody raw, Vector2 goalOffset, bool releasing, float dt, TilePresenter presenter, TileCollisionMap map, TileCombatContactPresentationSettings settings, long sourceTick)` on `TileCombatContactMotion`.

- [ ] Write segment tests for both wall sides, diagonal corner flags, blocked/unloaded tiles, fractional centres, 2x2 internal walls and a passage too narrow for 2x2. Assert both the incremental body segment and proposed body-to-raw tether are clear. An endpoint-only or fixed sample-count sweep must fail a thin-wall crossing fixture.
- [ ] Write release tests for corner turns, continuous moving raw bases, shortcut collapse, blocked correction, 64-waypoint capacity, unknown raw travel, complete fallback path, unreachable/partial path and changed topology. Assert `Collision` enters release, every output segment stays valid, waypoint count <= 64, path search radius is exactly 8, and at most one search occurs per distinct newly accepted source tick.
- [ ] Write `No_path_holds_safe_body_without_hiding_excess_lag`. Assert body position remains the last safe point, `ReleasePath` is set, outcomes still deliver, and lag may exceed 3 tiles while the body cannot safely return. New goals remain bounded. A new topology revision invalidates stale return segments immediately and a fresh source tick permits one retry.

```csharp
Assert.False(TileCombatContactMotion.IsClearSegment(map, plane, 2, start, blockedEnd, tileSize));
Assert.True(bodyTravel <= rawTravel + 4f * tileSize * MathF.Min(dt, 1f / 30f) + epsilon);
Assert.Equal(lastSafePoint, heldBodyPoint);
Assert.True(limits.HasFlag(TileCombatContactLimits.ReleasePath));
Assert.InRange(searchesOnSourceTick, 0, 1);
```
- [ ] Run the collision/release filters synchronously. Expected red exit 1.
- [ ] Implement a continuous footprint sweep over every tile/edge touched by the translating NxN rectangle. Use `Get` for swept blocked/wall geometry and `CanStand`/`CanStep` for existing occupancy and corner semantics at lattice transitions. Enumerate footprint leading/trailing edge boundary crossings, including tied crossings, instead of rounding the centre and testing only destination anchors. Reject non-finite/out-of-range conversion safely without overflowing integer coordinates.
- [ ] Retain the proven body-to-raw segment as a tether. Extend only with known continuous raw travel, consume from the body end within raw travel plus correction budget, and collapse only clear shortcuts. On overflow or unknown travel, require a complete path plus clear fractional endpoint connectors. A partial `TilePath` never authorizes motion. No input command, predicted route or server path is written.
- [ ] Preserve the hard one-search-per-new-source-tick cap. Topology change marks the current path dirty and checks safety immediately. If the current source tick already spent its search, remain held until the next accepted tick. This conservative reading resolves the spec's topology-retry wording without adding same-tick pathfinding bursts.
- [ ] Run the focused filters. Expected exit 0. Commit explicit paths as `feat(tile-contact): keep correction and release paths collision safe`.

## Task 7: Client frame integration and immutable result diagnostics

**Files:**
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.ContactPresentation.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.cs` `AdvancePresentation` and command observation seams only
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.Preparation.cs` dispatch hooks only
- Modify: `KhaozEngine.TileWorld.Netcode/TileCombatPreparationLedger.cs` internal copy method only
- Modify: `KhaozEngine.TileWorld.Netcode/TileCombatContactPresenter.cs`
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactPresenter.Impacts.cs`
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactDeliveryTests.cs`
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactImpactTests.cs`

**Interfaces:**
- Consumes Tasks 2-6 and existing `TilePreparationDispatch.AppliedStateTick`, `.Ended`, `.Results`, `PreparedCombatEvent.Outcome` and the bounded combat clock.
- Produces `TileCombatPreparationLedger.internal void CopyActiveTo(List<TileCombatPreparation> into)` with clear/fill semantics and deterministic attacker order.
- Produces presenter `void ObserveResult(in PreparedCombatEvent result, double combatTick)`, `void ObserveEnded(in CombatPreparationEnded ended, double combatTick)`, `void Advance(ReadOnlySpan<TileCombatContactRawBody> rawBodies, long localNetId, long localCombatTarget, double combatTick, float dt, TilePresenter presenter)`, `bool TryGetBody(long netId, out TileCombatBodyPresentation presentation)`, `IReadOnlyList<TileCombatContactImpact> Impacts { get; }`, `long MissCount { get; }`, and `void Clear()`.
- Produces client integration hooks `InitializeContactPresentation()`, `CaptureContactSource(long serverTick)`, `ObserveContactPreparations(long serverTick)`, `AdvanceContactPresentation(float dt)`, and `ClearContactPresentation()` in the new partial. The main client calls these at the actual seams named above.

- [ ] Write `Poll_callbacks_read_previous_completed_body_frame`, `Results_advance_once_and_measure_in_delivery_order`, and `Saved_impact_lists_remain_immutable_after_future_frames`. Assert callbacks happen during receipt `Poll`, queued results do not change body offsets, one ordinary advance consumes them, an empty subsequent frame publishes an immutable empty list, and retained earlier lists/struct values never change.
- [ ] Write result tests for matching impact source, missing preparation/source/impact frame, side reversal, endpoint epoch/size mismatch, target removal, old impact tick and simultaneous multiple outcomes. Assert `GeometryServerTick` is matching impact tick, otherwise admitted tick, otherwise -1. Missing geometry measures available admitted error while `WithinTolerance` remains false. New legal impact side uses its new vector. Historical means impact tick < latest accepted movement tick at callback delivery, with `LateOutcome` even if alignment succeeds.
- [ ] Assert one miss increment per failed or unverified accepted result, zero extra increments on reads or duplicate/reordered terminals, no diagnostics for cancellation, and no created combat outcome. Late flags can coexist with success. `WithinTolerance` requires verified matching identity/size/plane/impact geometry, accepted compatible layout, no `GoalDistance`, and planar error <= tolerance * tile size.
- [ ] Pin unavailable numeric diagnostics: with neither impact nor admitted vector, publish `DesiredRelativePosition = Vector3.Zero` and `GeometryServerTick = -1`. If either current completed body point is unavailable, publish `PlanarErrorMetres = float.PositiveInfinity`, `WithinTolerance = false` and `TargetUnavailable`. A retained visible death sample is a transfer input, not an invented current target pose.

```csharp
Assert.Equal(receivedResults.Select(x => x.AttackId), impacts.Select(x => x.AttackId));
Assert.Equal(impactTick, verifiedImpact.GeometryServerTick);
Assert.False(unverifiedImpact.WithinTolerance);
Assert.Equal(missesBefore + failedOrUnverifiedCount, client.CombatContactMissCount);
Assert.Equal(savedValues, savedImmutableImpactList);
```
- [ ] Write nested-poll and disconnect/dispose callback tests. Assert observation precedes callback, the current generation is fenced before remaining outer results, all queues/state are cleared, and later advances cannot publish abandoned-session diagnostics. A callback that reads raw/body state never sees a half-applied body frame.
- [ ] Run the delivery/impact filters synchronously. Expected red exit 1.
- [ ] Observe a complete accepted state set before `CombatPreparationsChanged`, each cancellation before its callback and each result before `PreparedCombatEvent`. Consume these accepted ledger dispatches, not raw chunks. Classify first-entry `LatePreparation` from receipt combat time >= `StrikeTick`, including arrivals beyond the scheduled deadline. Preserve existing dispatch identity, ordering and generation behavior under nested `Poll`.
- [ ] Collect raw local/remote samples after raw presentation finishes. Require membership in the latest accepted source, use its identity/size/epoch and actual raw-step state, and do not establish new contact identities from a partially applied rejected snapshot. Feed local predicted target to fence superseded outgoing attempts when it clears/replaces the target, retaining incoming attacks against that local body. Sort identities, build components, establish anchors/goals, advance each controller once, then measure every queued result.
- [ ] Publish completed body samples and owned read-only impact arrays atomically. Keep `SourceServerTick` on the latest accepted movement frame used for that body, independently from each edge's admitted geometry tick. Publish `Correction = completedPose.Position - rawPose.Position`, including ground-resampling height, while control remains planar. Publish desired impact vectors as world X/Z with Y zero, and compare only their planar error. Enabled unaffected bodies use the completed raw sample and its measured raw velocity. Expose no mutable arrays. Do not use old results to rewind layout or restart controllers.
- [ ] Run the focused filters plus existing preparation delivery/clock tests in one synchronous filtered command. Expected exit 0. Commit explicit paths as `feat(tile-contact): integrate receipt-frame body diagnostics`.

## Task 8: Lifecycle reset, terminal hold and death successor transfer

**Files:**
- Create: `KhaozEngine.TileWorld.Netcode/TileCombatContactPresenter.Lifecycle.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.ContactPresentation.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.Snapshots.cs` local reconciliation/cut observations only
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldClient.Preparation.cs` shared lifecycle cleanup only
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactLifecycleTests.cs`
- Test: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactTransferTests.cs`

**Interfaces:**
- Consumes Task 3 `InterpolationSnappedToNewest`, Task 7 completed frames, Task 6 bounded motion/release and existing local `ReconciliationResult` signals.
- Produces presenter `void ObserveCut(long netId)`, `bool QueueTransfer(long previousNetId, long successorNetId)`, with `TryTransferCombatBodyPresentation` delegating only when joined/enabled.
- Completed visible predecessor samples live for two presentation advances, capacity-bounded and generation-fenced. A queued handoff stores predecessor world point and compatible plane/footprint, not an authoritative epoch or copied offset.

- [ ] Write hold tests asserting resolved ownership lasts exactly `TerminalHoldTicks` from receipt combat time, cancellation has no hold, paused bounded combat time cannot expire a hold by wall time, and same-target successors continue without a zero-offset re-entry. Retain controller state across revisions/merges/splits/attempts.
- [ ] Write reset tests for local hard snap/teleport, remote epoch/plane change, effective explicit remote interpolation cut, endpoint removal and re-entry, disconnect, rejection and dispose. Assert `PresentationCut`, zero correction/velocities on the completed cut frame, removed read false, surviving neighbours release, and normal late prep/results never create a cut. Also assert queued data or a continued outer poll cannot recreate contact state after disposal, even though the existing `Dispose` does not reset `IsJoined`. Contact subscribes to the optional remote cut notification only when enabled and unsubscribes on cleanup.
- [ ] Write transfer tests with predecessor body point P, successor raw point R and differing bases. Assert the seed offset is `P.XZ - R.XZ`, not the old predecessor offset. At zero dt the first handoff sample preserves P exactly. Subsequent release remains bounded/collision-safe and converges toward the successor's authoritative raw pose.
- [ ] Assert false for disabled/unjoined mode, unknown successor, missing/expired predecessor history, incompatible plane/footprint and wrong generation. Duplicate same-pair requests return true as an accepted no-op while the successor controller still records that handoff, leaving the first queued/applied handoff unchanged and never reseeding a running release. Removal, a real cut or generation cleanup clears this marker. History remains available at the next two completed advances, then expires, with total retained entries <= participant capacity.
- [ ] Write `Removed_predecessor_history_cannot_verify_missing_impact_geometry`, `Moving_kill_handoff_does_not_reset_neighbour_motion`, and blocked successor-release tests. Historical visual continuity does not invent an impact tile/epoch. First-sight successors use raw poses. No body transfer moves ground loot or entity components.

```csharp
Assert.True(client.TryTransferCombatBodyPresentation(previousId, successorId));
Assert.Equal(previousShownPosition, successorAtZeroDt.Pose.Position);
Assert.Equal(previousShownPlanar - successorRawPlanar, seedOffset);
Assert.False(client.TryTransferCombatBodyPresentation(expiredPreviousId, successorId));
Assert.False(unavailableKillingImpact.WithinTolerance);
```
- [ ] Run lifecycle/transfer filters synchronously. Expected red exit 1.
- [ ] Implement reset and transfer in the presenter owner. Queue transfer acceptance before the next ordinary advance, initialize from world point after the successor raw sample is available, then use the normal release controller. Never copy permanent corpse offsets or game collapse/facing. Mark real cuts once in the completed body frame, then resume normal finite differences.
- [ ] Run the focused filters and existing arrival-correction tests synchronously. Expected exit 0. Commit explicit paths as `feat(tile-contact): preserve body continuity across lifecycle handoffs`.

## Task 9: Independently phased contact, jitter and fallback acceptance

**Files:**
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/ContactPresentationLoopbackHarness.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactLoopbackTests.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactJitterTests.cs`
- Preserve: `KhaozEngine.TileWorld.Netcode.Tests/TileNetcode/TileCombatContactGeometryTests.cs`
- Modify only the responsible contact implementation files if these new acceptance tests find a defect

**Interfaces:**
- Consumes existing `TileCombatHarness` frame order and `PreparationDeliveryScenario.CaptureTransport` injection pattern. Do not use `PursuitScenario.Run`, which is a server-only scheduling proof, as evidence of displayed contact.
- Produces a headless harness with constructor `ContactPresentationLoopbackHarness(float clientPhase, float observerPhase, float frameSeconds, float tickSeconds = 0.25f, bool contactEnabled = true)`, `TileWorldClient Client`, `TileWorldClient Observer`, `TileWorldServer Server`, and `void Frames(int count)`.
- A transport wrapper implementing existing `INetTransport` queues ordered server packets according to a fixed deterministic delivery schedule. Join phases are applied via each client's `Tick(phase)` before join. No sleeps, random seeds, GPU or live client.

- [ ] Add the full cross-product of command phases 0.06/0.13 seconds and render rates 60/50 Hz, with an independently phased observer. Use walk4/run2, a 4/2 preparation profile, local run-up, local walk-up, moving-away target, stopped target, reciprocal pair and pursuit with all actual server results. Pin unchanged server outcomes, receipt-frame callback order and raw geometry.
- [ ] Assert normal impacts meet `0.04f * Presenter.TileSize`, maintain each admitted legal relative vector through illegal pursuit ticks and do not set `ParticipantCapacity`, `ConflictingLayout`, `GoalDistance`, `Collision` or `ReleasePath`. Retain the raw tests' exact 1.250/1.500 m characterization and stopped one-metre controls.
- [ ] Add deterministic delayed and burst delivery with first-preparation-during-strike, no preparation, old results and multiple terminal records in one poll. Assert named `LatePreparation`/`LateOutcome`/`MissingGeometry` flags, one ordinary advance, receipt feedback on every real result, immutable ordered diagnostics and no impact snap. Ordinary phased pursuit cannot be reclassified as late merely to pass.

```csharp
Assert.True(normalImpact.WithinTolerance);
Assert.True(normalImpact.PlanarErrorMetres <= 0.04f * tileSize);
Assert.Equal(TileCombatContactLimits.None, normalImpact.Limits & excludedNormalLimits);
Assert.Equal(receiptFrame, feedbackFrame);
Assert.Equal(1, contactAdvancesOnReceiptFrame);
```
- [ ] Report prepare, strike, scheduled/actual impact ticks, source/layout tick, raw/body positions, offsets, anchor and per-frame correction/body travel, planar error and limits through `ITestOutputHelper`. The engine reports actual velocity. The downstream game gate adds gait, sockets and blade contact.
- [ ] Run `dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter 'FullyQualifiedName~TileCombatContactLoopbackTests|FullyQualifiedName~TileCombatContactJitterTests|FullyQualifiedName~TileCombatContactGeometryTests'`. New defect-catching cases must fail first. Record red exit 1, or explicitly identify a green characterization assertion that already existed.
- [ ] Fix only demonstrated contact-owner defects, without tuning alone as a replacement for the selected geometry/controller design. Re-run this focused command once after the fix. Expected exit 0. Commit explicit paths as `test(tile-contact): prove phased receipt-frame contact and fallbacks`.

## Task 10: Living docs, package version and verified release handoff

**Files:**
- Modify: `KhaozEngine.TileWorld.Netcode/README.md`
- Modify: `docs/USING-KHAOZENGINE.md`
- Modify: `docs/INDEX.md`
- Modify: `docs/design/TILE-COMBAT-CONTACT-PRESENTATION-DESIGN-2026-10-01.md` status/history only after implementation
- Modify on actual package finish: `Directory.Build.props`, `CHANGELOG.md`, `README.md`, and every current-version declaration selected by `scripts/check-doc-versions.sh`
- Modify other Markdown references only if the required sweep finds stale descriptions of this changed API

**Interfaces:**
- Consumes completed public API from Task 1 and verified behavior from Tasks 2-9.
- Produces paste-ready client opt-in and frame-order examples, lifecycle/fallback contracts and a concrete release/adoption handoff. No new implementation API.

- [ ] Write docs for opt-in validation/defaults, raw versus body reads, units, previous-frame callback reads, one advance, immutable per-result diagnostics, hold/release, cut behavior, transfer retention and named limitations. Show `Poll` -> register queued visual handoffs -> `AdvancePresentation` -> body sample/receipt-frame feedback placement. Describe raw camera/prediction reads and game-owned gait/rig/facing.
- [ ] Sweep all Markdown for `TileCombatContact`, `CombatContactPresentation`, `LocalPose`, `TryGetRemotePose`, `CombatPresentationTick`, `SnapInterpolationToNewest`, topology changes and obsolete claims that interpolation delay is the client's only presentation setting. Correct living prose while retaining raw movement decisions and historical measurements as history.
- [ ] Parent re-reads current main, version and tags before selecting the additive release number. Candidate `20.17.0` is valid only if still free. Ride an appropriate staged unreleased batch or choose the next free additive version. Change `Directory.Build.props`, newest `CHANGELOG.md` entry and guarded declarations together only for actual package work.
- [ ] Parent reconciles the task branch with current main without disturbing other work, then owns the single full final verification lane: `mkdir -p local-feed`, `dotnet build KhaozEngine.slnx -c Release`, `dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"`, followed by `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `sh scripts/check-agent-instructions.sh --tree`, and `bash scripts/check-doc-versions.sh`. All must exit 0 and the build must have zero warnings. Record passed/failed/skipped counts and all exit codes. Do not run the full suite per worker or in loops.
- [ ] Prepare explicit-path implementation/docs and release commits using `area(scope): summary`, with the selected version as release scope. Workers return their verified commits to the parent. The parent owns merge/push and a private-feed proof if needed, never a bare shared-feed pack.
- [ ] After the validated commit is on `origin/main`, parent runs the guarded pack/check ritual from main. A durable engine release precedes game adoption. No worker tags. Parent evaluates any explicitly pinned waiting-consumer exception under `scripts/tag-release.sh` and repository rules. Attach the verified commit/package version to the Grimhollow #371 handoff.

## Task 11: Separate Grimhollow adoption prerequisite and acceptance gate

This is downstream work owned by the parent in a separate Grimhollow worktree after released engine adoption. No Grimhollow file is edited or tested by an engine worker. Re-read Grimhollow's required gameplay/architecture/development and engine-integration rules before its own scoped plan.

**Files in the separate consumer task:**
- Engine pin/tools/vendor feed: `Directory.Build.props`, `.config/dotnet-tools.json`, refreshed files selected by `scripts/refresh-engine.sh`, `docs/ENGINE-INTEGRATION.md`
- Client composition/body/feedback integration: existing `Grimhollow.Core/Client/HollowmereSession*.cs` owners and their Desktop composition call sites
- Death lineage: `Grimhollow.Shared/Presentation/CarcassState.cs` existing `DeadActorNetId` read, `Grimhollow.Core/Client/HollowmereSession.Carcasses.cs`, `CarcassPresentation.cs`
- Tests: `Grimhollow.Tests/Client/AttackPresentationSpacingTests.cs`, `CombatPreparationLoopbackTests.cs`, existing carcass/gait tests and the existing pursuit capture owner
- Evidence: `docs/verification/COMBAT-PREPARATION-2026-09-28.md` or a clearly linked follow-on report

**Interfaces:**
- Consumes the released Task 1 public API, existing `CarcassState.DeadActorNetId`, existing `WalkCycle.Advance` distance-based phase/weight behavior and existing game combat/facing/feedback owners.
- Produces a consumer opt-in and one corrected sample per body per frame, with no game contact solver and no engine implementation copied into the game.

- [ ] Adopt only the released engine version, matching tool versions and refreshed guarded vendored feed. Record the swept capabilities and disposition in `docs/ENGINE-INTEGRATION.md`, restore and prove the heads use the released package.
- [ ] Keep camera, input, prediction, authoritative tiles and rule overlays on unchanged raw reads. Route bodies, held items, overheads, hitsplats, body picking, shield/death poses and positional feedback through the same completed corrected sample. Apply game water/rig height afterward. Draw priority treats actual correction travel as display movement while retaining authoritative gameplay tile ownership.
- [ ] Collect real outcomes during `Poll`, register explicit known predecessor/carcass links before engine advance, advance once, then place feedback on that same render frame. Seed collapse from shown living motion, retain existing hitsplat transfer and facing owners, use raw/completed collapse for first-sight carcasses, and never infer lineage by proximity. Ground loot remains authoritative.
- [ ] Advance distance-driven gait once from actual corrected ground displacement, preserving phase/run weights through attempts. A cut contributes no stride. Keep attack phase timing unchanged. Measure local body/camera displacement and actual body velocity.
- [ ] Run the existing mandatory contact command after adoption: `GRIMHOLLOW_SPACING_PROBE=contact dotnet test Grimhollow.Tests/Grimhollow.Tests.csproj -c Release --filter FullyQualifiedName~AttackPresentationSpacingTests --logger 'console;verbosity=detailed'`. Require all 17 current contact cases and authored blade checks on published 4/2 preparation, keeping the 80 mm envelope and moving/stopped-target pins. Historical baseline remains separately characterized, with intentional opt-in skip accounting reported.
- [ ] Require all three existing goblin-on-running-player blows, including the deferred first blow, inside the same body/authored-contact envelope. Record every blow, not only a later settled strike. Normal cases fail if capacity, conflict, goal-distance or path fallback is invoked. The 17-case gate and three-blow gate are both mandatory before accepting adoption.
- [ ] Produce the owner's look packet: run/walk approach, walking-away target, independent observer, every chase blow, sword/shield, both sides of the 2x2 cow, largest local camera/body offset, fastest adjustment, anchor change, corner release, killing/moving kills, carcass handoff, authoritative ground loot and a deliberately late result. Report actual gait phase/weights, travel, raw/corrected target/facing, centre and blade error, limits and source ticks. Rate bounds and distance checks do not approve appearance.
- [ ] Parent runs Grimhollow's build/test/format commands once in its final serialized lane and records exits. Live pixels are only the single final proof through its allowed playtest bridge or a human handoff. Parent retains release/adoption responsibility and obtains look approval before declaring the consumer defect resolved.

## Inline self-review and remaining decisions

- Spec sections 1-3 map to Tasks 1, 9-11. The selected weighted layout assessment is rationale, not measured proof. Release/adoption requires the preserved consumer gates.
- Sections 4-7 map to Tasks 2-4, 7-8. Source admission, future Hold inheritance, directed result identity, reciprocal/cycle geometry, anchor priority, whole-component capacity and persistent controllers each have named tests.
- Sections 7-9 map to Tasks 5-7 and 9. Every invalid dt class, a valid stall, release rate, non-unit tile size, excluded layout, geometry mismatch, target removal, late result, burst and once-only diagnostics are exercised. Every result uses one ordinary frame advance.
- Sections 8-10 map to Tasks 6, 8 and 11. Collision continuity, safe holds with excess lag, explicit death world-point transfer, two-frame expiry, differing bases, first-sight corpses and retained unavailable-impact limits are explicit. Game gait, collapse, facing and loot ownership stay downstream.
- Sections 11-12 map to Tasks 9-11 and the pre-implementation owner review. Engine headless tests establish math/lifecycle behavior. Consumer authored-contact and look gates remain separate prerequisites.
- Added observability is explicit in Task 3, not presented as an existing API. Topology invalidates safety immediately, but the hard one-search-per-new-source-tick limit takes precedence over an eager same-tick retry. If the owner requires immediate same-tick searches on topology edits, section 8's search cap needs an explicit amendment before that change.
- The design's old approval-status wording is superseded by owner approval in the dispatch. This plan records that provenance without editing the historical artifact during planning. No other unresolved spec conflict was found.
