# Grimhollow P6 engine round: a server tick on the client, a disconnect linger, a route stall rule and foot-locked diagonals

Status: approved scope, design by orchestrator ruling. The owner approved the first three items on 2026-10-04,
engine-first, and later the same day added the fourth: the owner chose engine-first for the locomotion blend and asked
for the diagonal glide to be fixed. The API shapes below are technical calls, and root answered the open ones on
2026-10-04 (below). Implemented through the [plan](../superpowers/plans/2026-10-04-grimhollow-p6-engine-round.md),
full Release build and test green, staged for 20.24.0 with no release. The plan's Outcome records every ruling.

Consumers: Grimhollow P6 (`feature/p6-plan`, plan `2026-10-04-continuous-combat-p6.md`, questions Q3 and Q4, Tasks 9,
10 and 13), the routed walk-up (`feature/routed-walkup-plan`, plan `2026-10-04-routed-walkup.md`, Q1 and Task 5) and
Grimhollow's eight-way locomotion blend (`feature/engine-20-23-blend`, lane B3).
Base: engine `origin/main` at `356bf8928`, newest tag `v20.23.0`, `<KhaozEngineVersion>` 20.24.0 staged by the
tracked save outcomes. Line citations are at `356bf8928` unless marked Grimhollow.

## Root's answers (2026-10-04)

Root took every recommendation. The plan is written for these answers.

- **Q1. A reconnect inside the linger.** Option A. The same account's reconnect reclaims its held slot, the lingering
  body leaves through the ordinary path (one `PlayerLeaving`, one save, despawn) and a fresh body is seated on that
  slot from the saved record where the old one stood (D6). Reattaching the connection to the live body is not built.
  The cost stands: the new body has a new net id, so creatures and players targeting the old one lose it. P6 Task 13's
  `ARejoinInsideTheLingerTakesTheLingeringBody` becomes "a rejoin inside the linger ends it and seats the player where
  the body stood".
- **Q2. Format 2 and the server tick.** `WorldClientConfig.ReceiveServerTick` with
  `RequestUnreliableDeltaReplication` is a configuration error this round. Root files the follow-up issue that carries
  the tick in the format 2 header after the round. Grimhollow uses reliable deltas.
- **Q3. Ticks with a zero travel bound under `Stall`.** A zero travel bound (a rooted body) counts toward nothing, as
  in `DirectMoveToRange` (`DirectMoveToRange.cs:65-66`). Refused steps do count. A rooted body (O2.25) stays the game's
  rule, which Grimhollow ends with its own 45 tick window (P6 C10). The cost stands: a body rooted for good while
  routed never latches `Blocked` from the engine.
- **Q4. #1278.** No fix this round. With `Stall` set, the refused raw leg #1278 describes ends in `Blocked` within the
  window instead of holding `Following`, and the issue stays open as a lead with that note. A creature without
  `Stall` keeps the hold until #1278 is reproduced and fixed.

## Problem and measured facts

### The client has no server tick

- `WorldServer.Tick` (`WorldServer.cs:540`) and `ShardedWorldServer.Tick` (`ShardedWorldServer.cs:483`) keep no
  frame counter. The sharded server only sums its cells' independent counters to learn whether movement ran
  (`ShardedWorldServer.cs:619-624`).
- The legacy frame header is `[localNetId:long][ackSeq:int]` (`MoveProtocol.cs:526-555`). The full snapshot body has
  no sequence. The legacy delta body's `snapshotSeq` exists only with delta replication and is not exposed. The
  format 2 sequence wraps and is documented as not a simulation tick.
- `WorldClient.authoritativeTick` (`WorldClient.cs:26`) is an ingest count passed to `Reconcile`
  (`WorldClient.cs:704`). It is never reset and is not a server tick.
- Remote interpolation stamps each ingest with the client's `presentationClock` (`WorldClient.cs:674`), collapses
  ingests that share a stamp (`ClientReplicationView.cs:440`), and renders every remote at
  `presentationClock - interpolationDelaySeconds` (`WorldClient.cs:488`), clamped before the oldest sample and held
  at the newest with no extrapolation (`ClientReplicationView.cs:481-495`). The render time is not stored.
- Grimhollow's continuous host counts server frames in the engine's before-tick hook and runs a gameplay tick on every
  fifth (Grimhollow `ContinuousGrimhollowHost.Clock.cs:44-56`). Its combat ticks are therefore server frame counts
  divided by five, and the client has nothing to compare them with.
- `TileWorldClient.ServerTick` (`TileWorldClient.cs:213`) is the tile host's precedent: the tile wire carries an
  8-byte server tick in every frame.

### A disconnect despawns at once

- Every leave ends in `ShardedWorldServer.OnLeave` (`ShardedWorldServer.Sessions.cs:54-96`), which raises
  `PlayerLeaving(slot, account, final)` (the save, `StatePersistence.Save.cs:38-56`) and despawns in the same call.
- `NetServer` frees the slot before the host drains `Left` (`NetServer.cs:115-124`, `RemovePeer` at `:229-236`), so
  a join in the same poll can recycle it.
- Persistence and games key leave-time state by slot: `TryResolveKey(slot, account)` and
  `CaptureGameState(slot, key)` (`StatePersistence.Save.cs:33-40`). A body kept past its slot's release would be
  saved under a slot another player may already hold.
- A joined slot with no input steps as idle: an empty command queue yields the neutral command
  (`RemoteCommandQueue.cs:135-138`), written as `PendingMove` each tick (`ShardedWorldServer.cs:497-503`).
- Snapshot exclusion (`ShardedWorldServer.cs:176`), eviction pinning (`ShardHost.Eviction.cs:44-55`), the shutdown
  save (`SaveDirtyPass` over `JoinedSlots`, `StatePersistence.Save.cs:104-124`) and the admin roster all key on joined
  slots.
- Precedent: `TileWorldServerConfig.CombatLogoutTicks` (`TileWorldServerConfig.cs:155`) keeps a body for a fixed
  window, ends it when the same account rejoins and bypasses kicks and drain (`TileWorldServer.Sessions.cs:193-305`).
  Its linger stays keyed by a slot `NetServer` has already released, and a new connection recycling that slot ends
  the linger (`TileWorldServer.cs:392-393`).

### `MoveToRange` never ends a hold

`MoveToRange.Tick` holds `Following` with zero input when the guard refuses a step on a raw route
(`MoveToRange.cs:103-107`), returns `WaitingForPath` for as long as an exhausted partial route waits on cooldown and
replans into the same partial (`PathFollower.Region.cs:98`), and can stall short of a waypoint on sloped physics ground
([#1265](https://github.com/APKiwiOrg/KhaozEngine/issues/1265)). The Movement README says it never returns `Blocked`
(`KhaozEngine.Movement/README.md:505`). Two 20.23.0 paths add holds: a refused straightened step holds one tick and
replans raw (`MoveToRange.Straighten.cs:9-15`), and lead
[#1278](https://github.com/APKiwiOrg/KhaozEngine/issues/1278) says that raw replan can start with a refused leg and
then hold until the target moves. `DirectMoveToRange` already owns the stall rule Grimhollow needs
(`DirectMoveToRange.cs:77-83`, ring in `DirectMoveToRange.Progress.cs`).

### Diagonals still slide on the 20.23.0 blend

Grimhollow lane B3 (`feature/engine-20-23-blend` at Grimhollow `8f959e6d`) adopted `DirectionalLocomotionBlend` and
measured planted-foot slide through its drawn skinned body (`LocomotionFootSlipTests.PlantedFeetHoldTheGround`: 240 Hz,
3 s after 1 s settling, the furthest horizontal travel of an ankle inside a contact window, worst foot, centimetres).
Cardinals barely move from the old single-clip path, and diagonals improve but still slide.

| Case | Old sector pick | 20.23.0 blend |
| --- | ---: | ---: |
| strafe walk left or right, 2 m/s | 3.4 | 3.4 |
| backpedal, 2 m/s | 9.9 | 9.8 |
| walk forward-left / forward-right, 2 m/s | 48.5 / 48.5 | 23.7 / 21.3 |
| walk back-left / back-right, 2 m/s | 61.6 / 61.6 | 16.0 / 17.7 |
| run forward-left / forward-right, 5 m/s | 34.2 / 34.2 | 15.7 / 24.6 |

The diagonal residue of 16 to 25 cm per contact window is the share rule, not Grimhollow's inputs. At 45 degrees the
playtest 1 design's D7 gives each family half, and its D8 phase advances by `speed / sum(weight x stride)` on one
shared phase. A clip moves its planted foot along its own axis only, so the blended foot travels `weight x stride x
rate` per axis. With Grimhollow's walk strides that carries the planted foot about (0.64, 0.36) of the body's
(0.71, 0.71) travel along forward and strafe, about 0.69 m/s of sideways slip at 2 m/s, roughly 20 cm per contact,
which matches the measurement. The forward walk (16 cm) and strafe run (22.1 cm) also slide on cardinals, on both paths:
that is each clip's own stride against its contact timing, art and stride work outside this round.

## Goals

1. An opt-in server tick on `WorldClient`: the newest applied frame's server tick, and the fractional server tick
   remote bodies are drawn at, on the same bracket the positions use.
2. An opt-in disconnect linger on `ShardedWorldServer`: the game picks a length per slot when the link drops, the body
   stays simulated and attackable, then leaves and is saved through the ordinary path.
3. An opt-in stall window on `MoveToRange` that latches `Blocked` with `DirectMoveToRange`'s rule and wording.
4. Default off is identical: wire bytes, frames, events, despawn timing, routes, commands and statuses are unchanged
   when no option is set.
5. `DirectionalLocomotionBlend` keeps a planted foot locked to the ground on a steady diagonal, as it already does on a
   cardinal, with no API change (D9).

## Non-goals

A server tick in format 2 frames (Q2). Time sync, playback rate adjustment or extrapolation of the remote timeline. A
linger on `WorldServer` or `TileWorldServer` (the tile host has its own). Reattaching a connection to a live body (Q1).
A logout message (a client logout is a disconnect). Fixing #1278, #1265 or #1281. An approach window on
`MoveToRange` (a detour may raise reach distance). Foot lock during a direction or pace crossfade, and the cardinal
slide of Grimhollow's forward walk and strafe run clips. Grimhollow files, pin moves and tags.

## Decisions

Scores are design judgments from 1 to 10, higher is better.

### D1. What the server tick counts

`ServerTick` on `WorldServer` and `ShardedWorldServer` is a `long` counting `Tick` calls. It is 0 before the first
call, increments as the first statement of `Tick` (before `OnBeforeTick`), and every frame served by that call
carries it. One tick per call whether or not a cell's accumulator stepped, because that is the unit the server's hooks
fire on and the unit Grimhollow's clock already counts. It is always on. Only the wire use is opt-in.

### D2. How the tick reaches the client

| Option | Default wire unchanged | Version skew safe | Exactness | Cost | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| A. A hello `ServerTickCapable` and two ticked frame kinds with a widened header | 10 | 9 | 10 | 8 | 37 |
| B. The tick in every frame header behind a protocol version | 2 | 5 | 10 | 9 | 26 |
| C. A separate tick frame before each snapshot | 9 | 8 | 8 | 5 | 30 |
| D. A client-side ingest count | 10 | 10 | 2 | 10 | 32 |

Select A. `ClientControlKind.ServerTickCapable = 4` is sent once on join after `DeltaCapable`, the pattern
`DeltaCapable` set (`MoveProtocol.cs:356-361`): an older server ignores the unknown kind. A server that knows it serves
that slot `ServerFrameKind.TickedSnapshot = 7` and `TickedDelta = 8`, each framed as
`[serverTick:long][localNetId:long][ackSeq:int][body]`. An older client never asks. D is not a server tick: collapsed and dropped ingests and the join
offset break it. Cost: 8 bytes per frame for an opted-in client.

### D3. The client surface

`WorldClientConfig.ReceiveServerTick`, default false, sends the hello. `WorldClient.LatestServerTick` (`long`) is the
newest ticked frame's tick this session, `-1` before one arrives and after a new attempt starts.
`WorldClient.RemoteRenderTick` (`double`) is the fractional server tick remote bodies are drawn at, `-1` while unknown.
`ReceiveServerTick` with `RequestUnreliableDeltaReplication` throws `ArgumentException` from the constructor (Q2).

### D4. How the render tick is computed

| Option | Matches drawn bodies | Allocation | Surface | Total |
| --- | ---: | ---: | ---: | ---: |
| A. A tick timeline stamped and bracketed exactly as the remote samples are | 10 | 9 | 8 | 27 |
| B. `LatestServerTick - InterpolationDelayTicks` | 4 | 10 | 10 | 24 |
| C. Per-entity render ticks | 10 | 5 | 4 | 19 |

Select A. Each ticked ingest records `(presentationClock, serverTick)` beside `RecordInterpolationSample`, and a second
ingest at the same stamp overwrites the newest entry, as the view does. `AdvancePresentation` evaluates the timeline at
the same render time with the same rule: before the oldest entry it is the oldest tick, at or past the newest it holds
the newest, between two it lerps by the true stamps. B drifts by a frame under jitter and starvation. C answers a
question the consumer does not ask, since every moving remote is sampled on every ingest. With remote interpolation
off, remotes render at the newest sample, so `RemoteRenderTick` equals `LatestServerTick`. Storage is one ring of 600
entries allocated at construction when opted in, the view's `MaxHistorySamples`, pruned below the lower bracket.

### D5. Where a lingering body lives

| Option | Slot keyed state stays valid | Rejoin capacity | Change size | Total |
| --- | ---: | ---: | ---: | ---: |
| A. `NetServer` holds the slot while the body lingers | 10 | 9 | 7 | 26 |
| B. Detach the body from its slot into a net id keyed table | 3 | 8 | 5 | 16 |
| C. Keep it on the released slot, as the tile server does | 2 | 7 | 8 | 17 |

Select A. B makes the final save and `CaptureGameState` run under a slot that may belong to someone else, and needs
its own snapshot exclusion, eviction pin, shutdown save and admin lookup. C lets an unrelated join on the recycled slot
end the linger. With A the lingering slot stays joined in every server table, so the exclusion, pin, shutdown save,
roster and kick-by-account work unchanged, and `PlayerLeaving` at release names a slot nobody else holds. Cost: a
lingering body counts against `MaxPlayers`, the tile host's rule (`TileWorldServer.cs:209`).

Shape:

- `ShardedWorldServerConfig.DisconnectLingerTicks`, `Func<int, long, int>?` (slot, net id, return server ticks),
  default null. It mirrors `EntityVisibleToSlot`'s delegate style. A result of zero or less leaves at once.
- `NetServer.HoldSlotOnDisconnect`, `Func<int, bool>?`, and `NetServer.ReleaseHeldSlot(int slot)`. On a transport
  `Disconnected` for a slot the delegate holds, `NetServer` drops the connection and still enqueues `Left`, but keeps
  the slot allocated and remembers its subject. `ShardedWorldServer` sets the delegate only when the hook is set.
- The hook is asked only for a transport disconnect (the client closed or timed out) of a joined slot the server is
  not closing. `Disconnect(slot)`, kicks, bans, replication restarts and the rate limit kick never linger, and nothing
  lingers after `BeginDrain`. `NetServer` records every connection it closes itself (both `Disconnect` overloads and
  any server-initiated close) and never asks the hold for it, so the rate limit kick, whose leave arrives later through
  the transport, needs no mark from the host (ruling P6E-4).
- On `Left` for a held slot the server forgets the slot's command queue and replication streams and keeps the body.
  The body steps on the neutral command, is served to everyone whose interest holds it and is not served itself.
- Expiry: a body granted `n` ticks during the poll after tick `k` is stepped in ticks `k + 1` to `k + n`, and leaves
  at the start of tick `k + n + 1`, after `ServerTick` advances and before `OnBeforeTick`.
- Leaving is `OnLeave` unchanged plus `net.ReleaseHeldSlot(slot)`: `MovementCommitmentEnded`, `PlayerLeaving`, the
  save, the despawn and the slot release run once, in that order.
- `ShardedWorldServer.IsLingering(int slot)` answers for the game and for admin tools.

### D6. A reconnect inside the linger

| Option | One body per account | Fight continuity | Persistence safety | New semantics | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| A. Reclaim the held slot, end the body, seat a fresh one from the save | 10 | 5 | 10 | 9 | 34 |
| B. Reattach the connection to the live body | 10 | 9 | 5 | 3 | 27 |
| C. Refuse the rejoin until the linger ends | 10 | 3 | 9 | 7 | 29 |

Select A (Q1). `NetServer` hands a Hello whose subject holds a held slot that same slot instead of allocating, ahead of
the duplicate session check, so neither `KickOlder` nor `RefuseNewer` applies and a full server cannot refuse the
account its own seat. `ShardedWorldServer.OnJoin` first ends the lingering body on that slot (D5's leave), then joins
as today. The save from `PlayerLeaving` is published before `PlayerJoined`, so the load waits behind it (#662), and the
resume hint seats the new body where the old one stood. B needs a `PlayerResumed` event, a persistence path that
starts a session without a load, and every game's seat code to handle it. C locks the player out of the fight the
linger exists for. A tokenless guest has no subject, so its linger runs out.

### D7. Where the stall rule lives

| Option | Latch semantics right | Reuse | Consumer cost | Total |
| --- | ---: | ---: | ---: | ---: |
| A. In `MoveToRange`, opted in by `RouteApproachOptions.Stall` | 10 | 9 | 9 | 28 |
| B. An engine watchdog wrapped around any driver | 6 | 7 | 6 | 19 |
| C. Game side (`RoutedWalkUpStall`) | 6 | 3 | 5 | 14 |

Select A. Only the driver sees `InRange`, `Reset` and the shape change that must unlatch it. `DirectMoveToRange`'s
ring moves unchanged to an internal `RangeProgressRing` that both drivers use.

### D8. The stall rule

`RouteStallOptions(int windowTicks, float travelMetres)`, a sealed record with get-only `WindowTicks` and
`TravelMetres`, validated by `DirectApproachOptions`' helpers with the same messages (1 to 65,535 ticks, finite and
positive metres). `RouteApproachOptions.Stall` is `RouteStallOptions?`, default null.

- Counted ticks: every tick that returns `Following` or `WaitingForPath`, except the hold for a zero travel bound
  (Q3). That covers a refused step, a refused straightened step, a zero-offset waypoint and an exhausted partial route
  waiting on cooldown.
- `Suspended` and `InRange` count toward nothing. `Unreachable` and `UnsupportedTransition` count toward nothing and
  are returned as today.
- A window of `N` ticks spans `N` intervals between `N + 1` counted samples. Net horizontal feet displacement under
  `TravelMetres` across the last window latches `Blocked` on that tick (the 16th counted tick for 15).
- Latched `Blocked` holds zero input until `InRange`, `Reset` or a change of target shape, range or capsule. The check
  sits after the goal update (`MoveToRange.cs:80`), whose shape change already calls `Reset`, so a shape change
  unlatches on the tick it happens. `InRange` clears the window. A replan, a straightening fallback or target
  translation does not.
- With `StraightenRoutes`, a refused straightened step is one counted zero-travel tick and the raw replan moves, so the
  fallback alone never latches. A raw leg the guard refuses (#1278) now latches `Blocked` within the window (Q4).

### D9. Foot-locked diagonal shares

Notation: the body-frame velocity is `(x, y)`, X right and Y forward. A family's travel component `c_f` is `|y|` for
forward or backward (whichever the sign of `y` picks) and `|x|` for left or right. A family's stride `s_f` at the
current speed is its members' bracket mix, `(1 - t) x s_lower + t x s_upper` with today's bracket `t` on the total
speed, so a clamped family is its one member's `StrideMetres`.

Derivation. A clip carries its planted foot along its own axis only, by its stride per loop. With one shared phase
advancing at rate `r` loops per second and family `f` weighted `w_f`, the linearly blended planted foot moves
`w_f x s_f x r` along that family's axis. Locking it to the ground needs `w_f x s_f x r = c_f` for each family, with the
shares summing to one. The unique solution is

- `w_f = (c_f / s_f) / sum_g (c_g / s_g)`, and
- `r = sum_g (c_g / s_g)`.

The phase is written as one rule for steady and easing states: it advances by `(|x| + |y|) x dt / sum_i(weight_i x
StrideMetres_i)` over the eased, normalised slot weights. In steady state `sum_i(weight_i x StrideMetres_i) = sum_f(w_f
x s_f) = (|x| + |y|) / r`, so the rate is exactly `r`. On a cardinal one component is zero, `|x| + |y|` is the speed,
and both the share and the rate reduce to 20.23.0's single family rule. During a crossfade the same formula carries
on from the eased weights, as D8 of the playtest 1 design does today, so the feet lock once the weights settle.

Worked example on the engine test set's walk strides at 1.2 m/s and 45 degrees forward-right (forward walk stride 1.2,
right walk stride 0.8): `c = 0.8485` per axis, `c / s` is 0.7071 and 1.0607, so forward gets 0.4, right gets 0.6 and
the phase runs at 1.7678 loops per second. The planted foot moves `0.4 x 1.2 x 1.7678 = 0.8485` forward and
`0.6 x 0.8 x 1.7678 = 0.8485` right, the body's travel. 20.23.0 gives 0.5 and 0.5 at 1.2 loops per second, which moves
the foot 0.72 forward and 0.48 right, short by 0.13 and 0.37 m/s.

Unchanged: members within a family keep today's speed bracket and clamp, `SyncPhase` sampling, `blendSeconds` easing
and normalisation, `TravelWeight`, the moving threshold on the Euclidean speed, `Reset`, `BodyFrame`, the public API
and the allocation contract. The phase still holds at zero travel.

Replace or opt in:

| Option | Default locks feet | Consumer cost | API surface | Change honesty | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| A. Replace the playtest 1 D7 share and D8 rate outright | 10 | 8 | 10 | 7 | 35 |
| B. An opt-in flag, default the 20.23.0 rule | 2 | 9 | 5 | 10 | 26 |

Select A, root's lean. The playtest 1 design states feet in step and foot-locked travel as the goal of D7 and D8
("the feet skate" is the problem it fixes), and the 20.23.0 arithmetic misses it on every diagonal. The blend shipped
in 20.23.0 with one consumer, Grimhollow, adopting it now on an unmerged lane, so B keeps a known-wrong default and a
mode flag forever to protect nobody. Cost: a visible behaviour change inside a minor, recorded as a behaviour change
in the 20.24.0 entry, and Grimhollow's B3 facts that pin half and half shares or the 20.23.0 phase rate update when it
adopts 20.24.0.

Changelog line (20.24.0): "Behaviour change: `DirectionalLocomotionBlend` now keeps a planted foot locked on
diagonals. Each direction family's share is its body-frame travel component over its stride, normalised, and the
shared phase advances by `(|x| + |y|)` over the weighted stride, so each family's blended foot travels exactly its
component. A 45 degree walk with forward stride 1.2 m and strafe stride 0.8 m now weights forward 0.4 and strafe 0.6
instead of half and half. Cardinals, speed brackets, easing, `TravelWeight` and the API are unchanged."

## Consumer contract

- **P6 Task 9.** Set `ReceiveServerTick` on the continuous client config. Read `Server.ServerTick` on the host instead
  of counting frames, so the server's gameplay tick is `ServerTick / GameplayTickEvery` on both sides.
  `ContinuousCombatClock.CombatTick` is `RemoteRenderTick / GameplayTickEvery`. Read `RemoteRenderTick` directly and
  never compute the drawn tick from `LatestServerTick`. The general relation is `LatestServerTick -
  InterpolationDelayTicks + (clock - newest ingest stamp) / TickSeconds`, which is `+ 1` on a frame that polls then
  presents one tick (ruling P6E-7), for a remote with continuous history after the first `InterpolationDelayTicks` of
  a session.
- **P6 Task 13.** Set `DisconnectLingerTicks` to `CombatLogoutTicks x GameplayTickEvery` (300) when the slot's body is
  in combat at the moment the link drops, else 0. A lingering slot stays joined, so the combat service keeps every
  target by net id and the seat code needs no change. A death while lingering is the game's rule. The body leaves at
  expiry through `PlayerLeaving`. Rename the rejoin test per Q1.
- **Routed walk-up Task 5.** `HollowmereNavigation.RouteOptions(navigation) with { Stall = new(15, 0.1f) }`. The
  mapping `Blocked` to `Blocked` already exists in the plan. A rooted routed body stays the game's 45 tick rule.
- **Grimhollow locomotion blend (lane B3).** No call changes. On adopting 20.24.0, the facts that pin the 20.23.0
  arithmetic take D9's values: `AnExactDiagonalIsHalfItsTravelGaitAndHalfItsStrafe`,
  `AnAngleWithinASectorSharesByItsFraction`, `ASettledForwardRightWalkAlignsTheContactsOfBothClips` (its 0.5 and 0.5)
  and `ADiagonalPhaseAdvancesTheFullDistanceOverTheWeightedStride`. `PlantedFeetHoldTheGround` re-measures, and its
  30 cm limit can tighten to what the cardinals already hold. Grimhollow's 1e-3 residue snap (B3-b) still applies
  before the call.

## Risks

- **Slow pace false positives.** A body whose bound per tick is under `TravelMetres / WindowTicks` latches `Blocked`
  while walking, as with `DirectMoveToRange`. Documented beside the option.
- **A hook that runs inside `NetServer.Poll`.** The linger delegate runs during the transport drain, before the host
  sees that poll's events. It must be cheap and must not throw. A throw propagates out of `Poll` as the rest of the
  drain does. Documented with the hook.
- **Capacity.** Lingering bodies hold slots. A server full of lingering bodies refuses new accounts but never a
  returning one (D6).
- **Kick by account.** `Kick(PlayerRef)` resolves the lingering slot through `accountIdBySlot`, and `Disconnect(slot)`
  on it is a no-op on the transport followed by the immediate leave. A banned account's body therefore leaves at once.
- **Diagonal cadence.** Locking the foot raises the loop rate off the cardinals. A diagonal steps faster than its
  longer-stride cardinal at the same speed, since `sum(|c| / s)` is at least `(|x| + |y|) / s_max`, but not always
  faster than its shorter-stride one. With equal strides a 45 degree diagonal steps `sqrt(2)` times as often as a
  cardinal. On the test set at 1 m/s (forward stride 1.2, strafe stride 0.8) the 45 degree walk loops 1.47 times a
  second, where 20.23.0 ran 1.0, which is 1.77 times the forward walk and 1.18 times the strafe. That is what two
  axis-bound clips need to cover a diagonal without sliding. The owner's playtest judges the look, and a cadence complaint is clip work (a diagonal clip), not a share
  change.
- **Short strafe strides take most of the share.** A family with a short stride needs more weight to cover its
  component, so Grimhollow's 0.8 m strafe walk outweighs its 1.4 m forward walk on a 45 degree walk. This is the
  intended result and is pinned by the 45 degree facts.
- **Allocation.** `MoveToRange.Tick` with `Stall`, `WorldClient.Poll` and `AdvancePresentation` with ticks, and
  `ShardedWorldServer.Tick` with lingering bodies allocate nothing in steady state. Expiry uses a reused scratch list.

## Test strategy

| Proof | Scenarios | Home |
| --- | --- | --- |
| Stall options | Bounds and parameter names as `DirectApproachOptions`, 65,535 accepted | Movement.Tests |
| Stall rule | Default off identical, latch on the 16th counted tick, `WaitingForPath` counts, zero bound and `Suspended` do not, `InRange`, `Reset` and shape change unlatch, translation does not, detour not blocked, straightening fallback alone never latches, a held raw leg latches, allocation-free | Movement.Tests |
| Direct ring move | Every `DirectMoveToRangeTests` and `DirectMoveToRangeDropTests` fact unchanged | Movement.Tests |
| Server tick wire | Codec round trip, hello ignored by a server that predates it (plain frames served), plain frames without the hello, `ServerTick` per `Tick` call on both servers | Server.Tests NetWorld |
| Client tick | Default off sends no hello and reads `-1`, latest tick per ingest, steady render tick is latest minus the delay, render tick matches a moving remote's drawn position, monotonic, starvation hold, reset on a new attempt, format 2 combination refused, allocation-free | Server.Tests NetWorld |
| Slot hold | Held slot not reallocated, subject reclaims it before the duplicate check, release frees it, no delegate is today's path | Server.Tests Netcode |
| Linger | Default off identical, steps exactly `n` ticks then one `PlayerLeaving` and save, attackable and served to others, idle, kicks, rate limit and drain do not linger, kick by account ends it, roster and shutdown save include it, allocation-free | Server.Tests NetWorld |
| Rejoin | Same slot reclaimed with one body, save before load, seated where it stood, guest expires, `RefuseNewer` does not refuse it, full server still admits it | Server.Tests NetWorld |
| Foot-locked shares | 45 degree shares from `c / s` on four diagonals, phase rate `sum(c / s)`, planted foot travel equal to each component (the 20.23.0 rule fails it), bracketed families at 2.7 m/s, cardinals, continuity at the new bound, reversal, stop and allocation unchanged | Game.Tests |

No test needs Grimhollow. Focused runs per task and one full Release run at the finish. No local repetition.

## Version

Additive public API, a new opt-in wire capability and one presentation behaviour change (D9), riding the staged
20.24.0 (newest tag `v20.23.0`). No bump. The round appends to the 20.24.0 `CHANGELOG.md` entry, with D9's line marked
as a behaviour change. Grimhollow adopts a released pin.
