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

Part of the MMO netcode stack (sub-project 0B).
