# KhaozEngine.Simulation

Headless simulation-host primitives for an authoritative server.

- **`FixedTickHost`** - a fixed-timestep accumulator. Feed it variable real-elapsed time; it invokes your
  tick callback a whole number of times at a fixed `dt`, decoupling the simulation rate from the render/frame
  rate. Deterministic (the same elapsed-time sequence always yields the same tick count) and dependency-free,
  with a spiral-of-death guard that sheds backlog when ticks fall behind. `SecondsUntilNextTick` plus the static
  `ComputeIdleWaitSeconds` pure helper let a host loop sleep only as long as it actually has before the next tick
  (instead of a fixed sleep that both oversleeps past OS timer granularity and loses track of the tick boundary) -
  see `MmoServerSample/Program.cs` for the reference wiring.

```csharp
var host = new FixedTickHost(tickSeconds: 1f / 30f);
// each frame / network pump:
host.Advance(elapsedSeconds, tick => world.Step(tick));

// idle pacing between polls: sleep only until the next tick is actually due
float wait = FixedTickHost.ComputeIdleWaitSeconds(host.SecondsUntilNextTick, safetyMarginSeconds: 0.0156f, minimumSeconds: 0.001f);
if (wait > 0f) Thread.Sleep(TimeSpan.FromSeconds(wait)); else Thread.Yield();
```

- **World clock stack** - `WorldClock` is the normalized time kernel, `WorldClockHost` owns authority on the
  server tick, and `WorldClockMirror` advances the last authoritative state on a client between messages.
  `WorldClockCodec` supplies strict transport-free state and command payloads. `WorldClockAnchor` is the
  versioned restart record. The game still owns message kinds, delivery reliability, authorization, and the
  storage adapter.

`WorldClockState(TimeOfDay, DayLengthSeconds, TimeScale)` is the immutable state contract. Time of day is
normalized to `[0, 1)`, day length is finite and greater than zero, and time scale is finite and non-negative.
`WorldClockCommand(WorldClockCommandKind Kind, float Value)` carries one mutation. Its kinds are
`SetTimeOfDay`, `SetTimeScale`, and `SetDayLength`. The command codec validates exact length, known kind, and
finiteness. The host is the single policy authority for typed and wire commands. It wraps set-time values,
clamps set-scale values, and clamps set-day-length values.

`WorldClockHostOptions` exposes `BroadcastIntervalSeconds` (default `5`, finite and greater than zero),
`MaxTimeScale` (default `1000`, finite and non-negative), and `MinDayLengthSeconds` (default `60`, finite and
greater than zero). The host constructor rejects invalid options. Applied scale commands clamp to
`[0, MaxTimeScale]`, while day-length commands clamp up to `MinDayLengthSeconds`.

```csharp
const ushort WorldClockStateMessage = 40;

var clockHost = new WorldClockHost(
    new WorldClock(dayLengthSeconds: 1_200f, startTimeOfDay: 0.25f),
    payload => SendClockToAll(WorldClockStateMessage, payload),
    (slot, payload) => SendClockTo(slot, WorldClockStateMessage, payload));

// Network or admin thread. Authenticate and authorize before this call.
void HandleClockCommand(Session session, ReadOnlySpan<byte> payload)
{
    if (session.CanControlWorldClock)
        clockHost.TryEnqueueCommand(payload);
}

// Join callback. The state is sent from the next authoritative tick.
void OnPlayerJoined(int slot) => clockHost.PushTo(slot);

// Fixed authoritative tick.
void Tick(float dt) => clockHost.Tick(dt);

var clockMirror = new WorldClockMirror();
void OnClockState(ReadOnlySpan<byte> payload) => clockMirror.Apply(payload);
void UpdateClient(float realDt) => clockMirror.Advance(realDt);
```

The send callbacks are game-owned. Wrap the bytes in the game's message envelope and use its reliable ordered
channel. Do not mutate `clockHost.Clock` from a network or admin thread. Those threads may enqueue authorized
commands and late-join pushes, while the simulation thread alone calls `Tick`. Cross-thread readers use
`clockHost.Snapshot`.

Persistence is also a game boundary. A game that uses `KhaozEngine.WorldStore.IWorldStore` owns a small adapter
that loads a stable key, passes the bytes to `WorldClockAnchor.Decode`, and constructs the boot clock from
`anchor.RecomputeTimeOfDay(nowUnixSeconds, configuredDayLengthSeconds)`. It periodically saves
`new WorldClockAnchor(nowUnixSeconds, clockHost.Snapshot.TimeOfDay).Encode()` and saves once more during shutdown
drain. `Simulation` owns the record format and downtime math, while the adapter owns the key, store calls, retry
policy, and cancellation. A restart uses the configured day length and resets time scale to 1.

- **`Hosting.DrainController`** - a deterministic elapsed-time grace countdown shared by authoritative server heads.
  `Begin` starts or restarts it, `Advance(dt)` moves it from the host's own clock, and `HasBegun`, `IsDraining`, and
  `IsComplete` expose its lifecycle. It owns no notice, socket, or persistence behavior. Each head keeps those
  terminal actions at its own boundary. The nested namespace keeps source that imports both package roots from
  becoming ambiguous. The old `KhaozEngine.NetWorld.DrainController` remains as a forwarding
  compatibility type.

- **`IJobScheduler`** - the engine's one worker-pool abstraction: `For(int count, Action<int> body)` runs
  `count` independent jobs and blocks until all finish. `SingleThreadedJobScheduler` runs them inline in index
  order (deterministic, allocation-free - the default everywhere). `ThreadPoolJobScheduler` fans them across the
  BCL thread pool via `Parallel.For`, with an optional `maxDegreeOfParallelism` cap (`-1` = unbounded, the
  default) exposed back as the read-only `MaxDegreeOfParallelism` property. `ShardHost.Tick` (per-cell sim
  steps, server-side) and `World.ParallelForEach`/`World.DefaultScheduler` (`KhaozEngine.Ecs`, client or
  server) both fan across this same seam - see `docs/USING-KHAOZENGINE.md` "Worker-pool seam
  (`IJobScheduler`) + parallel cell ticks" and "Parallel `ForEach` + access declarations".
  **`ThreadPoolJobScheduler` runs every worker body inside a `DeterministicFpScope`.** `DeterministicFp` pins the
  floating-point control register (rounding mode, FTZ/DAZ, trap masks) on the CALLING thread only, and the whole
  point of that scheduler is to run sim work on threads that are neither the caller's nor a dedicated sim thread,
  so without the scope a sim fanned across cores can silently diverge in the low bits between two machines or two
  runs. Applying it at the scheduling boundary means a consumer does not have to remember to. It is
  allocation-free (one save plus one restore of that register per slice) and nests harmlessly.

Part of the MMO netcode stack (sub-project 0B). Depends on `KhaozEngine.Determinism` and nothing else.
