# KhaozEngine.Time

A small, game-agnostic clock for pausing and time-scaling the simulation.

`GameClock` separates **real** delta time (UI, transitions, notifications) from
**scaled** delta time (gameplay, world). Set `TimeScale` for slow-mo (`< 1`),
normal (`1`), or fast-forward (`> 1`); `Pause()`/`Resume()` freeze the sim
without losing the intended speed. `Paused`/`Resumed` events fire on transitions.

```csharp
var clock = new GameClock();
clock.Update(gameTime);            // once per frame
world.Update(clock.ScaledDeltaSeconds);
clock.TimeScale = 0.5f;            // slow-mo
clock.Pause();                     // ScaledDeltaSeconds == 0; RealDeltaSeconds unchanged
```

Used standalone, or via `ScreenManager.Clock` in `KhaozEngine.Screens`.
