# Grimhollow P6 Engine Round Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Grimhollow's P6 combat and routed walk-up three opt-ins on the staged 20.24.0 (a server tick on `WorldClient`, a disconnect linger on `ShardedWorldServer`, and a stall window on `MoveToRange`), and make `DirectionalLocomotionBlend` keep planted feet locked on diagonals.

**Architecture:** `DirectMoveToRange`'s progress ring moves to an internal `RangeProgressRing` that `MoveToRange` also records into under `RouteApproachOptions.Stall`. Both servers count `Tick` calls as `ServerTick` and, for a client that sent a `ServerTickCapable` hello, serve ticked frame kinds that carry it. `WorldClient` keeps a `ServerTickTimeline` stamped and bracketed exactly like its remote samples. `NetServer` can hold a disconnected slot, so `ShardedWorldServer` keeps a lingering body joined on it until expiry or until the same account reclaims the slot. `DirectionalLocomotionBlend` gives each direction family its travel component over its stride as a share and advances the shared phase by the body-frame `|x| + |y|` over the weighted stride.

**Tech Stack:** C# on .NET 10, xUnit, the in-memory transport hub.

**Spec:** `docs/design/GRIMHOLLOW-P6-ENGINE-ROUND-DESIGN-2026-10-04.md`. Read it whole before Task 1. Decisions are D1 to D9. Root answered Q1 to Q4 on 2026-10-04 with every recommendation (the design's "Root's answers"), and the plan follows them.

## Global Constraints

- Worktree `/Users/antonio/KhaozEngine/.worktrees/grimhollow-p6-engine-round`, branch `feature/grimhollow-p6-engine-round`, base `356bf8928`. Merge current `origin/main` into the branch before Task 1 and again in Task 9 Step 3.
- Version: ride the staged 20.24.0. No bump. Task 9 appends to the existing `## 20.24.0` entry in `CHANGELOG.md` after re-reading `<KhaozEngineVersion>` and `git tag --sort=-v:refname | head -3`. If 20.24.0 has been tagged by then, stop and report.
- Default off is identical. With `RouteApproachOptions.Stall` null, `WorldClientConfig.ReceiveServerTick` false and `ShardedWorldServerConfig.DisconnectLingerTicks` null, routes, commands, statuses, wire bytes, frame kinds, session events and despawn timing are unchanged. `ServerTick` is always counted and changes nothing on the wire. The one deliberate exception is D9: `DirectionalLocomotionBlend` changes its diagonal shares and phase rate for every caller, with no option, and is identical on cardinals.
- Additive public API only. No project reference change. Netcode keeps its single `KhaozEngine.Netcode.Abstractions` reference.
- Names: `RouteStallOptions` (`WindowTicks`, `TravelMetres`), `RouteApproachOptions.Stall`, `RangeProgressRing` (internal), `WorldServer.ServerTick`, `ShardedWorldServer.ServerTick`, `MoveProtocol.ClientControlKind.ServerTickCapable = 4`, `MoveProtocol.ServerFrameKind.TickedSnapshot = 7`, `.TickedDelta = 8`, `MoveProtocol.EncodeTickedSnapshotFrame`, `MoveProtocol.TryDecodeTickedSnapshotFrame`, `WorldClientConfig.ReceiveServerTick`, `WorldClient.LatestServerTick`, `WorldClient.RemoteRenderTick`, `ServerTickTimeline` (internal), `NetServer.HoldSlotOnDisconnect`, `NetServer.ReleaseHeldSlot`, `ShardedWorldServerConfig.DisconnectLingerTicks`, `ShardedWorldServer.IsLingering`. `DirectionalLocomotionBlend`, `DirectionalGaitSet` and `GaitClip` keep their public API exactly.
- Steady paths allocate nothing: `MoveToRange.Tick` with `Stall`, `ServerTickTimeline.Record` and `.At`, and `ShardedWorldServer.Tick` with only lingering bodies joined. Allocation facts join `[Collection("AllocSensitive")]` and use the project's `AllocAssert.NoPerCallAllocation`.
- Zero warnings. KESIZE cap 800 lines: `ShardedWorldServer.cs` (749), `WorldServer.cs` (754) and `WorldClient.cs` (769) take only call sites, and new behaviour goes in the new partials named per task. No `.filesize-baseline` growth.
- Test namespaces under `KhaozEngine.Tests.*`. No em dashes, en dashes or prose semicolons in Markdown or comments.
- Every dotnet command runs from the worktree root through the shared lock: `/tmp/grimhollow-orch/slot-run.sh "<label>" /tmp/grimhollow-orch/<log> -- <command>`. Run `mkdir -p local-feed` once before the first restore. Labels are `p6e:t<task>-<step>` and logs `p6e-t<task>-<step>.log`. Below, `MOVE` is `KhaozEngine.Movement.Tests/KhaozEngine.Movement.Tests.csproj`, `SERVER` is `KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj` and `GAME` is `KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj`, each run as `dotnet test <project> -c Release --filter "<filter>"`.
- Focused filters per task, one full Release run in Task 9. No local repetition, stress runs or client launches.
- Guards: `sh scripts/check-dashes.sh --tree`, `sh scripts/check-prose.sh --tree`, `sh scripts/check-file-size.sh --tree`, `sh scripts/check-agent-instructions.sh --tree`, `bash scripts/check-doc-versions.sh`.
- Each feature task sweeps every Markdown file for the names it adds and the behaviour it changes. On macOS use `git grep -w` or `git grep -P`, never `git grep -E` with `\b`.
- Commit subjects `area(scope): summary`. Stage explicit paths. Workers do not merge, push, pack, tag or file issues unless the task says so.

## Review Focus

1. A client leaves and the same account reconnects inside one server poll. `NetServer` enqueues `Left(slot)` then `Joined(slot)` for the held slot, and the host must begin and end the linger in that drain with one `PlayerLeaving` before `PlayerJoined`. Task 7 `LeaveAndRejoinInOnePollSavesOnceBeforeTheJoin`.
2. Commands the client queued before the link dropped keep driving the lingering body, so a player who disconnects while running keeps running for the whole linger. Task 6 `ALingeringBodyIgnoresInputQueuedBeforeTheDrop`.
3. A frame hitch ingests several ticked frames between two presentation frames. The collapsed stamp must keep the newest tick and `RemoteRenderTick` must never run backwards. Task 4 `CollapsedIngestsKeepTheNewestTickAndStayMonotonic`.
4. An opted-in client meets a server that predates the hello and serves plain frames. The client must read `-1` for both ticks and keep playing, never misparse a frame. Task 4 `PlainFramesLeaveTheTickUnknown`.
5. A walk-up whose target changes shape while `Blocked` is latched (a new target, a new range) must start again that tick, and a moving body target must not unlatch it. Task 2 `ShapeChangeUnlatchesAndTranslationDoesNot`.

---

### Task 1: `RouteStallOptions` and the shared progress ring (D7, D8)

**Files:**
- Create: `KhaozEngine.Movement/RouteStallOptions.cs`, `KhaozEngine.Movement/RangeProgressRing.cs`
- Delete: `KhaozEngine.Movement/DirectMoveToRange.Progress.cs` (its class moves to `RangeProgressRing.cs` unchanged except the name and top-level placement)
- Modify: `KhaozEngine.Movement/DirectMoveToRange.cs:27,37` (ring type), `KhaozEngine.Movement/DirectApproachOptions.cs:50-56` (`Ticks` and `Metres` become `internal static`)
- Test: `KhaozEngine.Movement.Tests/RouteStallOptionsTests.cs`

**Interfaces:**
- Produces: `public sealed record RouteStallOptions` with constructor `RouteStallOptions(int windowTicks, float travelMetres)` and get-only `int WindowTicks`, `float TravelMetres`. Validation calls `DirectApproachOptions.Ticks(windowTicks, nameof(windowTicks))` and `DirectApproachOptions.Metres(travelMetres, nameof(travelMetres))`, so bounds and messages match. Summary uses `DirectApproachOptions`' window wording ("A window of N ticks spans N intervals between N + 1 counted samples and is first eligible on the (N + 1)th counted tick").
- Produces: `internal sealed class RangeProgressRing` with `RangeProgressRing(int capacity)`, `Record(Vector2 feetXz, float distance)`, `ClearAll()`, `ClearApproach()`, `StallBreached(float travelMetres, int windowTicks)`, `ApproachBreached(float gainMetres, int windowTicks)`, bodies as today.

- [ ] **Step 1: Write the tests.**

```csharp
[Theory] public void OptionsRequireAWindowAndAPositiveFiniteTravel(int ticks, float travel, string parameter)
// (0, 0.1, "windowTicks"), (-1, 0.1, "windowTicks"), (65536, 0.1, "windowTicks"), (15, 0, "travelMetres"),
// (15, -0.1, "travelMetres"), (15, NaN, "travelMetres"), (15, +Infinity, "travelMetres")
Assert.Equal(parameter, Assert.Throws<ArgumentOutOfRangeException>(() => new RouteStallOptions(ticks, travel)).ParamName);
[Fact] public void TheWindowBoundIsAccepted()       // new RouteStallOptions(65535, 0.1f).WindowTicks == 65535
[Fact] public void EqualWindowsAreEqual()           // new(15, 0.1f) == new(15, 0.1f), != new(16, 0.1f)
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "p6e:t1-red" /tmp/grimhollow-orch/p6e-t1-red.log -- dotnet test MOVE -c Release --filter "FullyQualifiedName~RouteStallOptionsTests"`. Expected: compile FAIL, CS0246 on `RouteStallOptions`.
- [ ] **Step 3: Implement** per Interfaces.
- [ ] **Step 4: Run GREEN** with filter `FullyQualifiedName~RouteStallOptionsTests|FullyQualifiedName~DirectMoveToRange`. Expected: all pass, nonzero count, every `DirectMoveToRangeTests` and `DirectMoveToRangeDropTests` fact unchanged.
- [ ] **Step 5: Commit.** `feat(movement): route stall options and a shared progress ring`

---

### Task 2: `RouteApproachOptions.Stall` in `MoveToRange` (D7, D8, Q3, Q4)

**Files:**
- Modify: `KhaozEngine.Movement/RouteApproachOptions.cs` (property), `KhaozEngine.Movement/MoveToRange.cs:53` (ring built when `Stall` is set), `:78` (`InRange` clears), `:80-81` (latch check after `Goal`), `:84`, `:86`, `:94`, `:100`, `:106`, `:109` (counted returns), `:113-117` (`Reset` clears)
- Create: `KhaozEngine.Movement/MoveToRange.Stall.cs`
- Modify: `KhaozEngine.Movement.Tests/MoveToRangeStraightenTests.cs:179` (`FallBackDrive` becomes `internal static`, with its `Driver` and `SeenCells` types)
- Modify docs: `KhaozEngine.Movement/README.md` (the `RouteApproachOptions` block near line 483, the sentence "`MoveToRange` never returns `Blocked`" at line 505, a `Stall` paragraph after the `StraightenRoutes` one), the matching block in `docs/USING-KHAOZENGINE.md` near line 8001
- Test: `KhaozEngine.Movement.Tests/MoveToRangeStallTests.cs`, `[Collection("AllocSensitive")]`

**Interfaces:**
- Consumes: Task 1 `RouteStallOptions`, `RangeProgressRing`.
- Produces: `public RouteStallOptions? Stall { get; init; }` on `RouteApproachOptions`, default null, documented with D8's counting rule, the latch, the zero bound exclusion (Q3), the slow pace note from the design's risks and the straightening interplay.
- Produces in `MoveToRange.Stall.cs`: `private readonly RangeProgressRing? _stallRing` (capacity `WindowTicks + 1`), `private bool _stalled`, `private RangeSteering Counted(in MoveState body, RangeSteering steering)` which, when the ring exists, records `(body.Position.X, body.Position.Z)` with distance `0f`, latches and returns `Hold(Blocked)` on `StallBreached(TravelMetres, WindowTicks)`, else returns `steering`. `private void ClearStall()` clears the ring and the latch. The approach half of the ring is never read here.
- Order in `Tick`: validation, Suspended (no count), `InRange` (`ClearStall()` then hold), `Goal(...)` (its shape change already calls `Reset`), then `if (_stalled) return Hold(RangeMoveStatus.Blocked)`, then the follower. Every `Following` and `WaitingForPath` return goes through `Counted` except the zero bound hold at `:91`. `Unreachable` and `UnsupportedTransition` return as today.

- [ ] **Step 1: Write the tests.** Use `MoveToRangeTests.Tuning`, `.Body`, `.Space`, `.Flat`, `.ScriptPlanner`, `.Route` and `NpcGroundMovement.Step`. `Stall15 = new RouteApproachOptions { Stall = new(15, 0.1f) }`. `Far = ReachTarget.Point(new(6f, 0.75f, 0f))` on the 32 x 32 open `Space`.

```csharp
[Fact] public void DefaultRouteOptionsHaveNoStall()
[Fact] public void DefaultOptionsNeverBlock()
// Body pinned at x 0 (re-fed each tick, never stepped) for 40 ticks toward Far with the old constructor and with
// RouteApproachOptions.Default: identical WorldDirection bits and statuses, and no Blocked.
[Fact] public void StallLatchesOnTheSixteenthCountedTick()
// Stall15, body fed at x = 0.005 * tick: ticks 1..15 Following with X > 0, ticks 16..30 Blocked with zero direction.
// Reset, then Body(0.15f) ticks Following.
[Fact] public void WaitingForPathTicksCount()
// ScriptPlanner returning Route(Partial, (0,0)) for a far target, follow config ReplanCooldownSeconds 10: the body at
// (0,0) gets WaitingForPath until the 16th counted tick, then Blocked.
[Fact] public void ZeroTravelBoundAndSuspendedTicksCountTowardNothing()
// 10 counted held ticks, 20 ticks with SpeedScale 0 (Following, zero direction), 20 ticks Grounded false (Suspended),
// then held ticks: Blocked first appears on the 6th held tick after the interruption (the 16th counted).
[Fact] public void InRangeClearsTheWindow()
// 10 counted held ticks, one InRange tick (body moved into range), 15 counted held ticks: still Following, Blocked on
// the 16th after InRange.
[Fact] public void ShapeChangeUnlatchesAndTranslationDoesNot()                       // Review Focus 5
// Latch Blocked toward a box target. Translating the box by 1 m on Z: still Blocked. Changing range 0.5 -> 0.6, the
// target kind, or the capsule radius: Following on that tick, and Blocked again only on its 16th counted tick.
[Fact] public void ARoutedDetourIsNotBlocked()
// MoveToRangeCarryTests.Surfaces(-4, -4) with an unstandable wall x 8..9, z 0..14, start centre of (4, 8), point
// target centre of (14, 8). Stall15, drive with NpcGroundMovement.Step: reaches InRange, no Blocked tick, and the
// first 30 ticks increase reach distance at least once (the detour leads away).
[Fact] public void TheStraighteningFallbackAloneNeverLatches()
// MoveToRangeStraightenTests.FallBackDrive with StraightenRoutes and Stall15: InRange, no Blocked, exactly one
// counted zero-direction tick (the refused straight step).
[Fact] public void ARefusedRawLegLatchesBlocked()                                      // the #1278 hold, Q4
// RefusedRawStepKeepsTheRoute's fixture with Stall15: the fallback tick plus 15 held raw ticks, Blocked on the 16th
// counted tick, plan count still two.
[Fact] public void WarmedStallTicksAllocateNothing()
// Stall15, open Space, a body walked with Step for 20 ticks, then AllocAssert.NoPerCallAllocation over 200 Tick calls.
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "p6e:t2-red" /tmp/grimhollow-orch/p6e-t2-red.log -- dotnet test MOVE -c Release --filter "FullyQualifiedName~MoveToRangeStallTests"`. Expected: compile FAIL, CS0117 on `Stall`.
- [ ] **Step 3: Implement** per Interfaces, then the docs under Files and the Markdown sweep for `Stall`, `RouteStallOptions` and "never returns `Blocked`".
- [ ] **Step 4: Run GREEN.** The Step 2 filter, then `FullyQualifiedName~MoveToRange|FullyQualifiedName~RouteStraightener|FullyQualifiedName~RouteStallOptions|FullyQualifiedName~DirectMoveToRange|FullyQualifiedName~NpcRangeNavigation|FullyQualifiedName~PlayerPathMovement|FullyQualifiedName~NpcSwimNavigation`. Expected: all pass, nonzero counts, every carry, straightening and swim fact unchanged.
- [ ] **Step 5: Commit.** `feat(movement): opt-in stall window latches Blocked in MoveToRange`

---

### Task 3: `ServerTick` and ticked frames on both servers (D1, D2)

**Files:**
- Modify: `KhaozEngine.NetWorld/MoveProtocol.cs:11` (`static partial class`), `:350-368` (`ServerTickCapable = 4` with a summary in `DeltaCapable`'s skew wording), `:636-640` (`TickedSnapshot = 7`, `TickedDelta = 8`)
- Create: `KhaozEngine.NetWorld/MoveProtocol.ServerTick.cs`
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.cs:483` (first statement `ServerTick++`), `:419` (hello sets the slot ticked), `:594-595` (frame through the helper), `KhaozEngine.NetWorld/ShardedWorldServer.Sessions.cs` (join and leave forget the slot), the same three sites in `KhaozEngine.NetWorld/WorldServer.cs:540`, `:477`, `:617-618` and `WorldServer.Sessions.cs`
- Create: `KhaozEngine.NetWorld/ShardedWorldServer.ServerTick.cs`, `KhaozEngine.NetWorld/WorldServer.ServerTick.cs`
- Test: `KhaozEngine.Server.Tests/NetWorld/ServerTickWireTests.cs`

**Interfaces:**
- Produces: `public static byte[] EncodeTickedSnapshotFrame(long serverTick, long localNetId, int ackSeq, byte[] snapshot)` writing `[serverTick:long][localNetId:long][ackSeq:int][snapshot]`, and `public static bool TryDecodeTickedSnapshotFrame(ReadOnlySpan<byte> data, out long serverTick, out long localNetId, out int ackSeq, out byte[] snapshot)`, false below 20 bytes with `-1`, `-1`, `-1` and an empty array.
- Produces on both servers: `public long ServerTick { get; private set; }`, summary per D1. Private `HashSet<int> tickedSlots` and `private (MoveProtocol.ServerFrameKind, byte[]) ServedFrame(int slot, MoveProtocol.ServerFrameKind kind, long netId, int ack, byte[] body)` which returns the plain kind and `EncodeSnapshotFrame` for an unticked slot, and `TickedSnapshot` or `TickedDelta` with `EncodeTickedSnapshotFrame(ServerTick, ...)` for a ticked one.
- The hello is honoured for any joined slot. A format 2 slot's frames are written by the format 2 stream and stay unticked (Q2).

- [ ] **Step 1: Write the tests.** Rig: the `ShardedWorldServerSlotLookupTests` join over `InMemoryTransportHub`, a raw `NetClient` that sends `EncodeClientControl` frames and records every server frame kind and decoded tick. `[Theory]` over sharded and flat for the server facts.

```csharp
[Fact] public void TickedFramesRoundTrip()            // ticks 0, 1, long.MaxValue, net id 1L << 40, ack -1 and 7
[Fact] public void AShortTickedFrameIsRefused()       // 19 bytes -> false, -1, -1, -1, empty
[Theory] public void ServerTickCountsTickCalls(bool sharded)
// 0 before Tick. OnBeforeTick reads n on the nth call, OnAfterTick reads n. A Tick(dt / 3) that steps no cell still
// counts.
[Theory] public void FramesStayPlainWithoutTheHello(bool sharded, bool deltas)
// DeltaCapable hello only (or none): every frame kind is Snapshot or Delta, byte-identical to EncodeSnapshotFrame.
[Theory] public void TheHelloTurnsFramesTicked(bool sharded, bool deltas)
// DeltaCapable (when deltas) then ServerTickCapable: every later frame is TickedDelta (deltas) or TickedSnapshot,
// and the decoded tick equals the ServerTick read in OnAfterTick of the serving Tick.
[Theory] public void ASlotThatLeavesForgetsTheHello(bool sharded)
// Ticked slot leaves, a new client recycles the slot without the hello: plain frames.
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "p6e:t3-red" /tmp/grimhollow-orch/p6e-t3-red.log -- dotnet test SERVER -c Release --filter "FullyQualifiedName~ServerTickWireTests"`. Expected: compile FAIL, CS0117 on `ServerTickCapable`.
- [ ] **Step 3: Implement** per Interfaces.
- [ ] **Step 4: Run GREEN.** The Step 2 filter, then `FullyQualifiedName~MoveProtocol|FullyQualifiedName~ShardedWorldServer|FullyQualifiedName~WorldServer|FullyQualifiedName~WorldClientDelta|FullyQualifiedName~WorldClientRebuild|FullyQualifiedName~DeltaRemotePresentation`. Expected: all pass, nonzero counts.
- [ ] **Step 5: Commit.** `feat(networld): count server ticks and serve ticked frames to clients that ask`

---

### Task 4: `LatestServerTick` and `RemoteRenderTick` on `WorldClient` (D3, D4, Q2)

**Files:**
- Create: `KhaozEngine.NetWorld/ServerTickTimeline.cs`, `KhaozEngine.NetWorld/WorldClient.ServerTick.cs`
- Modify: `KhaozEngine.NetWorld/WorldClientConfig.cs` (property after `RequestUnreliableDeltaReplication`), `KhaozEngine.NetWorld/WorldClient.cs` call sites only: constructor (validation and timeline), `:301` (hello after `DeltaCapable`), `:593` (`OnServerFrame` routes kinds 7 and 8 through the existing snapshot and delta paths with the tick), `:653` (`IngestServerState` records the tick), `:488` (`AdvancePresentation` evaluates), `:380-398` (`StartAttempt` resets)
- Modify docs: `KhaozEngine.NetWorld/README.md` (the `WorldClient` intro bullet at lines 45-63 and "Client simulation state versus presentation state" at line 966), the `WorldClient` presentation paragraphs in `docs/USING-KHAOZENGINE.md` (find with `git grep -n InterpolationDelayTicks docs/USING-KHAOZENGINE.md`)
- Test: `KhaozEngine.Server.Tests/NetWorld/ServerTickTimelineTests.cs` (`[Collection("AllocSensitive")]`), `KhaozEngine.Server.Tests/NetWorld/WorldClientServerTickTests.cs`

**Interfaces:**
- Consumes: Task 3 frame kinds, codec and hello.
- Produces: `public bool ReceiveServerTick { get; init; }` on `WorldClientConfig`, default false. With `RequestUnreliableDeltaReplication` the `WorldClient` constructor throws `ArgumentException` with `ParamName` `"config"` and a message naming both options.
- Produces: `public long LatestServerTick { get; }` and `public double RemoteRenderTick { get; }` on `WorldClient`, both `-1` while unknown.
- Produces: `internal sealed class ServerTickTimeline` with `ServerTickTimeline(int capacity)`, `void Record(double stamp, long tick)` (a stamp at or below the newest overwrites the newest entry, keeping the larger tick), `double At(double renderTime)` (`-1` when empty, the oldest tick before the oldest stamp, the newest tick at or past the newest stamp, else the lerp by true stamps, pruning entries below the lower bracket), `void Clear()`, `int Count`. Ring storage, capacity 600 (the view's `MaxHistorySamples`), overwriting the oldest when full.
- `IngestServerState` takes the frame's tick (`-1` for a plain frame) and records `(presentationClock, tick)` only for a ticked frame, beside `RecordInterpolationSample`. `AdvancePresentation` sets `RemoteRenderTick = timeline.At(presentationClock - interpolationDelaySeconds)` when remotes interpolate, else `LatestServerTick`. `StartAttempt` sets both to `-1` and clears the timeline.

- [ ] **Step 1: Write the tests.**

```csharp
// ServerTickTimelineTests
[Fact] public void EmptyTimelineIsUnknown()                         // At(5) == -1
[Fact] public void BracketsLerpByTrueStamps()                       // (1.0, 10), (1.1, 13): At(1.05) == 11.5 within 1e-9
[Fact] public void BeforeTheOldestClampsAndPastTheNewestHolds()     // At(0.5) == 10, At(9) == 13
[Fact] public void ASharedStampKeepsTheNewestTick()                 // Record(1.0, 10), Record(1.0, 11): At(1.0) == 11, Count 1
[Fact] public void PruningKeepsTheAnswer()                          // 20 entries, At over a rising render time equals a fresh timeline's answer
[Fact] public void AFullRingKeepsTheNewest()                        // 700 records into 600: At(oldest kept stamp) == tick 100
[Fact] public void RecordAndAtAllocateNothing()                     // AllocAssert over 1,000 Record and At pairs

// WorldClientServerTickTests: ShardedWorldServer (TickSeconds 1/30, InterestRadius 500) with observer A and remote B
// walking +X, as RemoteInterpolationTests' Rig does. A opts in unless a fact says otherwise.
[Fact] public void DefaultOffSendsNoHelloAndReadsUnknown()          // no opt-in: server frames to A are plain, both ticks -1
[Fact] public void LatestServerTickFollowsEachIngest()              // after each Tick and A.Poll: LatestServerTick == server.ServerTick
[Fact] public void SteadyRenderTickTrailsByTheDelay()
// One Poll and AdvancePresentation(1/30) per tick after a 10 tick warm-up: RemoteRenderTick == LatestServerTick - 2
// within 1e-9 (InterpolationDelayTicks default 2).
[Fact] public void TheRenderTickNamesTheTickTheRemoteIsDrawnAt()
// Server records B's X per ServerTick in OnAfterTick. A presents at 1/45 s per frame for 60 frames: lerping that
// record at RemoteRenderTick gives A's drawn X for B within 1e-4 m on every frame.
[Fact] public void CollapsedIngestsKeepTheNewestTickAndStayMonotonic()  // Review Focus 3
// Frames of 0, 2 and 3 ticks between AdvancePresentation calls with dt pattern 1/60, 1/20, 1/30: RemoteRenderTick
// never decreases, and after a frame that ingested ticks n and n + 1 it brackets with n + 1.
[Fact] public void StarvationHoldsTheNewestTick()                   // server stops ticking: RemoteRenderTick rises to LatestServerTick and stays
[Fact] public void WithoutRemoteInterpolationTheRenderTickIsTheLatest()  // InterpolateRemotes false
[Fact] public void PlainFramesLeaveTheTickUnknown()                 // Review Focus 4
// A raw NetServer over the hub answers A's Hello and sends EncodeServerFrame(Snapshot, EncodeSnapshotFrame(...))
// frames, as an older server does: A's LocalNetId is set and both ticks stay -1 over 10 frames.
[Fact] public void ANewAttemptForgetsTheTick()
// AutoReconnect on, hub.DisconnectClient(A's transport): -1 once the attempt starts, then ticks resume from the
// server's ServerTick.
[Fact] public void UnreliableDeltasWithTheTickAreRefused()          // ArgumentException, ParamName "config"
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "p6e:t4-red" /tmp/grimhollow-orch/p6e-t4-red.log -- dotnet test SERVER -c Release --filter "FullyQualifiedName~ServerTickTimelineTests|FullyQualifiedName~WorldClientServerTickTests"`. Expected: compile FAIL, CS0246 on `ServerTickTimeline`.
- [ ] **Step 3: Implement** per Interfaces, then the docs under Files and the Markdown sweep for `ReceiveServerTick`, `LatestServerTick`, `RemoteRenderTick` and any claim that the client has no server tick.
- [ ] **Step 4: Run GREEN.** The Step 2 filter, then `FullyQualifiedName~RemoteInterpolation|FullyQualifiedName~PresentationJitter|FullyQualifiedName~PresentationTrace|FullyQualifiedName~DeltaRemotePresentation|FullyQualifiedName~LocalInterpolationBasis|FullyQualifiedName~WorldClientReconnect|FullyQualifiedName~WorldClientLiveReconnect|FullyQualifiedName~WorldClientDelta|FullyQualifiedName~WorldClientRebuild`. Expected: all pass, nonzero counts.
- [ ] **Step 5: Commit.** `feat(networld): the client reads the server tick and the tick remotes are drawn at`

---

### Task 5: `NetServer` slot hold (D5, D6)

**Files:**
- Modify: `KhaozEngine.Netcode/NetServer.cs:18` (`sealed partial class`), `:115-124` (Disconnected asks the hold), `:165-173` (a held subject reclaims ahead of the duplicate check), `:229-236` (`RemovePeer` takes a hold flag)
- Create: `KhaozEngine.Netcode/NetServer.SlotHold.cs`
- Modify docs: `KhaozEngine.Netcode/README.md` "One account, one live session" (line 363), a short "Held slots" paragraph
- Test: `KhaozEngine.Server.Tests/Netcode/NetServerSlotHoldTests.cs`

**Interfaces:**
- Produces: `public Func<int, bool>? HoldSlotOnDisconnect { get; set; }`, default null, asked once for a transport `Disconnected` of an established slot and never for `EndOlderSession`, a refused Hello or a pending connection. Documented: it runs inside `Poll`, must be cheap and must not throw.
- Produces: `public void ReleaseHeldSlot(int slot)`, which frees a held slot and forgets its subject. No-op for a slot that is not held.
- A held slot keeps its allocator bit and moves its subject to `heldSlotBySubject`. `Left(slot)` is still enqueued as a terminal event. A Hello whose non-empty subject is in `heldSlotBySubject` is seated on that slot (no allocation, no duplicate policy) and gets `Joined(slot, ...)` as today.

- [ ] **Step 1: Write the tests.** `NetServer` over `InMemoryTransportHub` with `TestHandshake` subjects, `maxPlayers` 2 unless stated.

```csharp
[Fact] public void WithoutAHoldTheSlotIsFreedAsToday()      // a disconnects from 0, b joins on 0
[Fact] public void AHeldSlotIsNotReallocated()              // hold true: Left(0) enqueued, b joins on 1
[Theory] public void TheHoldingSubjectReclaimsItsSlot(DuplicateSessionPolicy policy)
// KickOlder and RefuseNewer: a held on 0, a reconnects: Joined(0, "a"), no Reject frame, no extra Left.
[Fact] public void AFullServerStillSeatsTheReturningSubject()
// maxPlayers 1, a held on 0: b is rejected "server full", a reconnects onto 0.
[Fact] public void ReleaseFreesTheSlotAndForgetsTheSubject()
// Release(0): b joins on 0, then a reconnects onto 1 as a fresh session. ReleaseHeldSlot(1) on an unheld slot is a no-op.
[Fact] public void ATokenlessHoldIsNeverReclaimed()         // a guest held on 0, another guest joins on 1
[Fact] public void OnlyTransportDisconnectsAskTheHold()
// A KickOlder duplicate of a connected subject and a refused Hello never call the delegate (call count 0).
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "p6e:t5-red" /tmp/grimhollow-orch/p6e-t5-red.log -- dotnet test SERVER -c Release --filter "FullyQualifiedName~NetServerSlotHoldTests"`. Expected: compile FAIL, CS0117 on `HoldSlotOnDisconnect`.
- [ ] **Step 3: Implement** per Interfaces, then the README paragraph.
- [ ] **Step 4: Run GREEN.** The Step 2 filter, then `FullyQualifiedName~NetServer|FullyQualifiedName~SlotAllocator|FullyQualifiedName~DuplicateSession`. Expected: all pass, nonzero counts.
- [ ] **Step 5: Commit.** `feat(netcode): a server can hold a disconnected slot for its subject`

---

### Task 6: The disconnect linger on `ShardedWorldServer` (D5)

**Files:**
- Modify: `KhaozEngine.NetWorld/ShardedWorldServerConfig.cs` (hook), `KhaozEngine.NetWorld/ShardedWorldServer.cs` call sites only: constructor (sets `net.HoldSlotOnDisconnect` when the hook is set), `:387-389` (`Left` for a lingering slot begins the linger instead of `OnLeave`), `Tick` (expiry right after `ServerTick++`, before `OnBeforeTick`), `:570-597` (the serve loop skips lingering slots), `:404` (the rate limit marks its slot closing), `:290-294` (`BeginDrain` stops new lingers)
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.Sessions.cs` (`OnLeave` clears the linger and the closing mark and calls `net.ReleaseHeldSlot(slot)` for a lingering slot)
- Create: `KhaozEngine.NetWorld/ShardedWorldServer.Linger.cs`
- Modify docs: `KhaozEngine.NetWorld/README.md` (the `ShardedWorldServer` intro bullet at lines 30-44), `docs/USING-KHAOZENGINE.md` "Sharded authoritative server" (line 12089) and "Reconnect + server notices" (line 22693)
- Test: `KhaozEngine.Server.Tests/NetWorld/ShardedWorldServerLingerTests.cs`

**Interfaces:**
- Consumes: Task 3 `ServerTick`, Task 5 `HoldSlotOnDisconnect`, `ReleaseHeldSlot`.
- Produces: `public Func<int, long, int>? DisconnectLingerTicks { get; init; }` on `ShardedWorldServerConfig` (slot, net id, return server ticks), default null, documented per D5 with the risk notes (runs inside `Poll`, counts against `MaxPlayers`).
- Produces: `public bool IsLingering(int slot)` on `ShardedWorldServer`.
- In `ShardedWorldServer.Linger.cs`: `Dictionary<int, long> lingerUntilBySlot`, `HashSet<int> closingSlots`, `bool draining`, a reused `List<int> lingerScratch`, `private bool HoldOnDisconnect(int slot)` (false when not joined, closing or draining, else asks the hook, and on a positive `n` stores `ServerTick + n` and answers true), `private void BeginLinger(int slot)` (`commands.Forget`, `deltaReplicator?.Forget`, `deltaCapableSlots.Remove`, `replication.Left`, and Task 3's ticked flag), `private void ExpireLingers()` (every slot with `until < ServerTick` goes through `OnLeave`).
- Expiry contract: a body granted `n` during the poll after tick `k` is stepped in ticks `k + 1` to `k + n` and leaves at the start of tick `k + n + 1`, before `OnBeforeTick`.

- [ ] **Step 1: Write the tests.** Rig: `CreateServer` and `Join` from `ShardedWorldServerSlotLookupTests` with a config whose hook returns a per-test `n` and counts calls. A second client B joins and stays.

```csharp
[Fact] public void DefaultOffLeavesOnThePollThatSeesTheDrop()
// No hook: after DisconnectClient and Poll, the net id is gone and PlayerLeaving fired once.
[Fact] public void ALingeringBodyStepsExactlyItsTicksThenLeavesOnce()
// n = 3: after the drop and Poll, IsLingering true, TryGetPlayerNetId true, no PlayerLeaving. Ticks k+1..k+3 keep the
// body. On tick k+4 the event log reads "leaving" before "before:k+4", the entity is despawned, IsLingering false.
[Fact] public void ALingeringBodyIsServedToOthers()
// B's WorldClient renders A's net id on every linger tick, and stops on the tick A's body leaves.
[Fact] public void ALingeringBodyIgnoresInputQueuedBeforeTheDrop()                 // Review Focus 2
// A queues 10 Right commands, then drops before the server polls them: the body's X moves less than 0.05 m over the
// linger, while the same queue without a drop moves it at least 0.5 m.
[Theory] public void ANonPositiveLingerLeavesAtOnce(int n)                         // 0 and -5
[Fact] public void ServerClosesNeverLinger()
// Disconnect(slot), Disconnect(slot, reason), Kick(PlayerRef account, reason) applied on the next Tick, the rate limit
// kick (AntiCheat.DisconnectOnRateLimit) and a drop after BeginDrain: hook call count 0, today's leave each time.
[Fact] public void KickByAccountEndsALingeringBody()
// n = 300: Kick(account, "bye"), next Tick: one PlayerLeaving, despawned, slot released (a new client gets it).
[Fact] public void ALingeringSlotStaysOnTheRoster()                                // JoinedSlots and ListOnline include it
[Fact] public void TickWithOnlyALingeringBodyAllocatesNothing()
// In a second class in the same file, ShardedWorldServerLingerAllocationTests, [Collection("AllocSensitive")]. B absent, A lingering with n = 100,000, warm 10 ticks,
// then AllocAssert over 50 Ticks. If Tick allocates with no players at all, assert equal allocation to that baseline
// and report it.
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "p6e:t6-red" /tmp/grimhollow-orch/p6e-t6-red.log -- dotnet test SERVER -c Release --filter "FullyQualifiedName~ShardedWorldServerLingerTests"`. Expected: compile FAIL, CS0117 on `DisconnectLingerTicks`.
- [ ] **Step 3: Implement** per Interfaces, then the docs under Files and the sweep for "despawns a player on leave", `PlayerLeaving` timing and `MaxPlayers`.
- [ ] **Step 4: Run GREEN.** The Step 2 filter, then `FullyQualifiedName~ShardedWorld|FullyQualifiedName~DuplicateSession|FullyQualifiedName~ServerDrain|FullyQualifiedName~ShardedNoticeDrain|FullyQualifiedName~ServerTickWire`. Expected: all pass, nonzero counts.
- [ ] **Step 5: Commit.** `feat(networld): opt-in disconnect linger keeps a body on its held slot`

---

### Task 7: A reconnect inside the linger (D6, Q1)

**Files:**
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.Sessions.cs:12` (`OnJoin` first ends a lingering body on the slot through `OnLeave`, before the reserved subject and ban checks)
- Modify docs: `docs/USING-KHAOZENGINE.md` "One account, one live session (17.38.0)" (line 22726) and "Persisting players so the world survives a restart" (line 22190, the leave and rejoin paragraphs), the `WorldPersistence` bullet in `KhaozEngine.NetWorld/README.md` (lines 95-140)
- Test: `KhaozEngine.Server.Tests/NetWorld/ShardedWorldServerLingerRejoinTests.cs`

**Interfaces:**
- Consumes: Task 5 reclaim, Task 6 linger.
- Produces: no public API. The order on a reclaimed slot is `PlayerLeaving(slot, account, final)`, the save, the despawn, then the join as today. `OnLeave`'s `ReleaseHeldSlot(slot)` is a no-op there, because `NetServer` reseated the slot and it is no longer held (Task 5 contract), so the reclaimed slot is never freed under its new connection.

- [ ] **Step 1: Write the tests.** `ShardedWorldPersistenceTests`' rig: `InMemoryWorldStore`, `WorldPersistence` with `SaveIntervalSeconds` 999, the hook returning 300.

```csharp
[Fact] public void ARejoinReclaimsTheSlotAndEndsTheBodyFirst()
// a lingers on slot 0, a reconnects: slot 0, the event log reads leaving(0, a) then joined(0, a), the old net id is
// gone, one player entity carries a's account.
[Fact] public void TheRejoinIsSeatedFromWhatTheLingerSaved()
// During the linger the game moves the body with SetPlayerState to x 12. After the rejoin and persistence updates,
// the new body stands at x 12 within 1e-3 with no restore teleport (LocalTeleportEpoch unchanged on a's WorldClient).
[Fact] public void LeaveAndRejoinInOnePollSavesOnceBeforeTheJoin()                 // Review Focus 1
// Drop and reconnect before one server Poll: one PlayerLeaving, then PlayerJoined, one store write for a.
[Fact] public void RefuseNewerDoesNotRefuseTheReturningAccount()
[Fact] public void AFullServerAdmitsTheReturningAccount()                           // MaxPlayers 1
[Fact] public void AGuestLingerRunsOut()
// Tokenless a lingers on 0 with n = 3, another guest joins on 1, a's body leaves on tick k+4, one PlayerLeaving.
[Fact] public void TheShutdownSaveIncludesALingeringBody()
// a lingers, SetPlayerState to x 20, persistence.SaveDirtyPass then FlushAsync: the stored record is at x 20.
```

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "p6e:t7-red" /tmp/grimhollow-orch/p6e-t7-red.log -- dotnet test SERVER -c Release --filter "FullyQualifiedName~ShardedWorldServerLingerRejoinTests"`. Expected: FAIL in `ARejoinReclaimsTheSlotAndEndsTheBodyFirst` (two bodies, or the join refused by the bound persistence key).
- [ ] **Step 3: Implement** per Interfaces, then the docs under Files.
- [ ] **Step 4: Run GREEN.** The Step 2 filter, then `FullyQualifiedName~ShardedWorld|FullyQualifiedName~WorldPersistence|FullyQualifiedName~DuplicateSession|FullyQualifiedName~NetServerSlotHold`. Expected: all pass, nonzero counts.
- [ ] **Step 5: Commit.** `feat(networld): a reconnect inside the linger reclaims its seat after one save`

---

### Task 8: Foot-locked diagonal shares in `DirectionalLocomotionBlend` (D9)

**Files:**
- Modify: `KhaozEngine.Game.Render3D/DirectionalLocomotionBlend.cs` (`SetTargets` at lines 142-152, the phase line in `Advance` at line 121, the class summary at lines 6-21, and the `SectorFamily` table at lines 29-33, removed if unused)
- Modify: `KhaozEngine.Game.Tests/Game/DirectionalLocomotionBlendTests.cs`
- Modify docs: `KhaozEngine.Game.Render3D/README.md` "DirectionalLocomotionBlend" (lines 242-262, the angle and phase paragraph) and `docs/USING-KHAOZENGINE.md` "Directional locomotion blend (eight-way, feet in step)" (line 5725, "by angle (45 degrees is half and half)" and "by distance over the weighted stride")

**Interfaces:**
- Consumes: nothing from Tasks 1 to 7. This task is independent of them and may run at any point before Task 9.
- Produces: no public API change. The behaviour, from the design's D9, with body-frame velocity `(x, y)` and Euclidean `speed` as today:
  - Families: `y > 0` picks forward with `c = |y|`, `y < 0` picks backward, `x > 0` picks right with `c = |x|`, `x < 0` picks left. A zero component adds no family.
  - Family stride `s_f` at `speed`: the bracket mix of its members' `StrideMetres` with today's bracket and clamp (`AddFamily`'s rule), so one helper computes both the stride and the member split.
  - Target share of family `f`: `(c_f / s_f) / sum_g (c_g / s_g)`, then split between its members by today's speed bracket.
  - Phase: `Phase = Frac(Phase + (MathF.Abs(x) + MathF.Abs(y)) * dt / stride)`, where `stride` is today's `sum(weight / total x StrideMetres)` over the eased slot weights. Steady state gives the rate `sum(c / s)`. On a cardinal both rules equal 20.23.0's.
  - Unchanged: easing, normalisation, `TravelWeight`, the moving test on `speed`, sampling at `Phase + SyncPhase`, `Reset`, `BodyFrame`, argument checks, zero allocation.

Derived values on the test set (`Set()`: forward walk 1.4 m/s stride 1.2, forward run 4.0 stride 2.4, back walk 1.0 stride 0.9, left walk 1.2 stride 0.8, right walk 1.2 stride 0.8, right run 3.0 stride 1.8). `AtDegrees` is clockwise from forward.

| Velocity | Families and `c / s` | Shares | Phase rate (loops/s) | 20.23.0 |
| --- | --- | --- | ---: | --- |
| 45 degrees, 1.2 m/s | forward 0.8485 / 1.2, right 0.8485 / 0.8 | ForwardWalk 0.4, RightWalk 0.6 | 1.767767 | 0.5, 0.5 at 1.2 |
| 315 degrees, 1.2 m/s | forward, left as above | ForwardWalk 0.4, LeftWalk 0.6 | 1.767767 | 0.5, 0.5 |
| 135 or 225 degrees, 1.2 m/s | back 0.8485 / 0.9, side 0.8485 / 0.8 | BackWalk 8/17 = 0.4705882, side walk 9/17 = 0.5294118 | 2.003469 | 0.5, 0.5 |
| 45 degrees, 1.0 m/s | forward walk, right walk | 0.4, 0.6 | 1.473139 | 0.5, 0.5 at 1.0 |
| 30 degrees, 2.7 m/s | forward 2.338269 / 1.8 (walk and run half each), right 1.35 / 1.633333 (walk 1/6, run 5/6) | ForwardWalk 0.3055742, ForwardRun 0.3055742, RightWalk 0.0648086, RightRun 0.3240429 | 2.125569 | 1/3 right by angle |

- [ ] **Step 1: Write the tests.** In `DirectionalLocomotionBlendTests`, with `Strides = { 1.2f, 2.4f, 0.9f, 0.8f, 0.8f, 1.8f }` beside `SyncPhases`.

```csharp
// Replaces DiagonalsSplitHalfAndHalf. Speed 1.2, settled.
[Theory]
[InlineData(1f, 1f, ForwardWalk, RightWalk, 0.4f, 0.6f)]
[InlineData(-1f, 1f, ForwardWalk, LeftWalk, 0.4f, 0.6f)]
[InlineData(1f, -1f, BackWalk, RightWalk, 0.4705882f, 0.5294118f)]
[InlineData(-1f, -1f, BackWalk, LeftWalk, 0.4705882f, 0.5294118f)]
public void DiagonalsShareByComponentOverStride(float x, float y, int a, int b, float wa, float wb)
// n == 2, w[a] and w[b] within 1e-5 of wa and wb.

[Fact] public void ABracketedDiagonalSplitsEachFamilyShareBySpeed()
// AtDegrees(30, 2.7): n == 4, ForwardWalk 0.3055742, ForwardRun 0.3055742, RightWalk 0.0648086, RightRun 0.3240429,
// each within 1e-5.

// Replaces PhaseAdvancesByDistanceOverBlendedStride. 45 degrees at 1.0 m/s, settled, 15 frames of 1/60 s.
[Fact] public void ADiagonalPhaseRateIsTheSumOfComponentOverStride()
// Circular(blend.Phase, Frac(p0 + 0.25f * 1.473139f)) <= 1e-5f. The 20.23.0 rule gives p0 + 0.25.

[Theory]
[InlineData(45d, 1.2f)]
[InlineData(135d, 1.2f)]
[InlineData(315d, 1.2f)]
[InlineData(30d, 2.7f)]
public void PlantedFootTravelMatchesTheBodyOnEachAxis(double degrees, float speed)
// Settle at AtDegrees(degrees, speed), read p0, Advance one Frame, read the samples and p1.
// rate = Frac(p1 - p0) / Frame. For the forward or backward slots, sum(weight x Strides[slot]) x rate equals
// speed x |cos| within 1e-3 m/s, and for the left or right slots it equals speed x |sin| within 1e-3 m/s.
// The 20.23.0 rule fails the first case: 0.72 forward and 0.48 right against 0.8485 each.
```

Change one existing fact's bound: `WeightsAreContinuousAroundTheCircle` uses `limit = 1.5f * MathF.PI / 360f + 1e-6f` (0.013091), the largest adjacent stride ratio at 1.2 m/s (1.2 over 0.8) times the 0.5 degree step in radians. The share's slope peaks at a cardinal at that ratio, and the measured worst step is 0.012921. Leave every other fact as it is: `EachCardinalIsOneClip`, `SpeedBracketsAndClamps`, `WeightsSumToOneAndSteadyStateHasAtMostFour` (still four clips), `PureStrafeReproducesItsOwnStride`, `SyncPhasesKeepContactsAligned`, `ZeroTravelHoldsThePhase`, `SplitDtIsInvariantWhenSettled`, `ReversalCrossfadesForBlendSeconds`, `StopFadesOutOfTheLastGait`, `StandingStillIsNotMovingEvenWithAZeroMovingSpeed`, `BodyFrameUsesTheCameraYawConvention`, `InvalidSetsAndArgumentsAreRefused`, `DuplicateClipIdsAreSeparateSlots` (equal strides, still two slots summing to one) and `DirectionalLocomotionBlendAllocationTests.AdvanceAllocatesNothing`.

- [ ] **Step 2: Run RED.** `/tmp/grimhollow-orch/slot-run.sh "p6e:t8-red" /tmp/grimhollow-orch/p6e-t8-red.log -- dotnet test GAME -c Release --filter "FullyQualifiedName~DirectionalLocomotionBlend"`. Expected: it compiles, and `DiagonalsShareByComponentOverStride`, `ABracketedDiagonalSplitsEachFamilyShareBySpeed`, `ADiagonalPhaseRateIsTheSumOfComponentOverStride` and every `PlantedFootTravelMatchesTheBodyOnEachAxis` case FAIL (0.5 shares, phase p0 + 0.25). Every unchanged fact passes.
- [ ] **Step 3: Implement** per Interfaces, then the docs under Files. Rewrite the class summary's direction and phase paragraphs to D9's rule, and sweep Markdown with `git grep -n "half and half"` and `git grep -n "DirectionalLocomotionBlend"` for any other claim that a diagonal splits by angle or that the phase advances by `speed`. Leave the 20.23.0 `CHANGELOG.md` entry and the playtest 1 design as the record of what shipped.
- [ ] **Step 4: Run GREEN.** The Step 2 filter, then `FullyQualifiedName~AnimationSampler|FullyQualifiedName~Locomotion`. Expected: all pass, nonzero counts, the allocation fact included.
- [ ] **Step 5: Commit.** `feat(render3d): foot-locked diagonal shares in the directional blend`

---

### Task 9: Round docs, the 20.24.0 entry and full verification

**Files:**
- Modify: `docs/INDEX.md` (a design row above the playtest 1 row at line 47), the design's status line, `CHANGELOG.md` (the `## 20.24.0` entry), this plan's Outcome
- Root files the follow-up issues

**Interfaces:**
- Consumes: every public name in Global Constraints.

- [ ] **Step 1: Sweep.** `git grep -w` each new name and `MoveToRange`, `Blocked`, `PlayerLeaving`, `InterpolationDelayTicks`, `DuplicateSessions` and `DirectionalLocomotionBlend` across all Markdown, package READMEs and `AGENTS.md`. Correct any claim that `MoveToRange` never blocks, that the client has no server tick, that a leave always despawns at once, or that a blended diagonal is half and half.
- [ ] **Step 2: Follow-ups.** Root runs `scripts/ledger.sh search "server tick"` and `scripts/ledger.sh search "format 2"`, then files a `kind/backlog`, `confidence/authored` issue to carry the server tick in the format 2 header (Q2), and comments on [#1278](https://github.com/APKiwiOrg/KhaozEngine/issues/1278) that `Stall` now ends its hold in `Blocked` (Q4). Record numbers in Outcome.
- [ ] **Step 3: Changelog.** Fetch and merge current `origin/main`, re-read `<KhaozEngineVersion>`, the `CHANGELOG.md` head and the newest tags. If 20.24.0 is still staged and untagged, append four bullets to its entry: the stall window with its default and counting rule, the server tick with its hello, frame kinds, client properties and the format 2 limit, the linger with its hook, slot hold, reclaim and what never lingers, and D9's "Behaviour change" line for `DirectionalLocomotionBlend` as the design words it. Set the INDEX row and the design status to implemented and staged for 20.24.0, no release. Commit `docs(20.24.0): stall window, client server tick, disconnect linger and foot-locked diagonals`.
- [ ] **Step 4: Verify once.**

```sh
mkdir -p local-feed
/tmp/grimhollow-orch/slot-run.sh "p6e:t9-build" /tmp/grimhollow-orch/p6e-t9-build.log -- dotnet build KhaozEngine.slnx -c Release
/tmp/grimhollow-orch/slot-run.sh "p6e:t9-test" /tmp/grimhollow-orch/p6e-t9-test.log -- dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
```

Expected: zero warnings, zero failures, nonzero counts and every guard exit 0. Also run `dotnet format whitespace KhaozEngine.slnx --verify-no-changes --include` over the `.cs` files the branch changed. Record build time, assembly count, passed, failed, skipped and total in Outcome. A failure goes back to its owning task, not a rerun.
- [ ] **Step 5: Outcome and handoff.** Record per-task commits and counts, every departure with its reason and cost, and the follow-up issues. Root reviews the whole branch, merges and pushes main, then runs `scripts/pack-local-feed.sh` from main. Only the owner starts the tag. Grimhollow adopts the released pin on its P6 and routed walk-up branches per the design's consumer contract. This plan edits no Grimhollow file.

## Outcome

Not started.
