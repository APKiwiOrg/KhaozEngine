# KhaozEngine.Simulation

Headless simulation-host primitives for an authoritative server.

- **`FixedTickHost`** - a fixed-timestep accumulator. Feed it variable real-elapsed time; it invokes your
  tick callback a whole number of times at a fixed `dt`, decoupling the simulation rate from the render/frame
  rate. Deterministic (the same elapsed-time sequence always yields the same tick count) and dependency-free,
  with a spiral-of-death guard that sheds backlog when ticks fall behind.

```csharp
var host = new FixedTickHost(tickSeconds: 1f / 30f);
// each frame / network pump:
host.Advance(elapsedSeconds, tick => world.Step(tick));
```

Part of the MMO netcode stack (sub-project 0B).
