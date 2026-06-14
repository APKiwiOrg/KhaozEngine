# KhaozEngine time-scale + pause (2.2.0)

## Goal

A first-class, engine-level pause / time-scale so games can freeze, slow, or
speed up the *simulation* while UI, overlays, screen transitions, and
notifications stay live on real time. Replaces the `PassUpdateThrough` modal
trick as the way to freeze gameplay, and generalizes to slow-mo / fast-forward
(Nullwake bullet-time, TD speed buttons).

Additive and opt-in. Default behavior is byte-for-byte unchanged for the three
pinned consumers (Hardpoint, Nullwake, SpaceGame). SpaceGame's deterministic
fixed-timestep lockstep must remain untouched.

## Package placement

New package **`KhaozEngine.Time`** (net10.0, no MonoGame-screen dependency; it
takes `Microsoft.Xna.Framework.GameTime` but knows nothing about screens). It
holds one public type, `GameClock`.

- `KhaozEngine.Screens` gains a reference on `KhaozEngine.Time` and wraps a
  `GameClock` on `ScreenManager`.
- `KhaozEngine.Ecs` is **not** modified and does **not** depend on `.Time`. A
  gameplay screen feeds the scaled dt into `world.Update(scaledDt)` itself, so
  ECS stays clock-agnostic.
- A screen-less / pure-ECS consumer can construct and drive a `GameClock`
  directly without taking `.Screens`.

All packages share the unified version line; this release is **2.2.0** (additive
= minor).

## `GameClock` (KhaozEngine.Time)

```csharp
public sealed class GameClock
{
    public float TimeScale { get; set; }     // clamped to >= 0 on set; default 1
    public bool IsPaused { get; }             // _paused || TimeScale == 0f
    public float RealDeltaSeconds { get; }    // last frame's unscaled dt
    public float ScaledDeltaSeconds { get; }  // RealDeltaSeconds * (IsPaused ? 0 : TimeScale)

    public void Pause();                      // sets _paused = true
    public void Resume();                     // sets _paused = false
    public void Update(GameTime gameTime);    // advance once per frame, before consumers read deltas

    public event Action? Paused;              // fired when IsPaused transitions false -> true
    public event Action? Resumed;             // fired when IsPaused transitions true -> false
}
```

### Semantics

- **Pause is orthogonal to `TimeScale`, with memory.** `_paused` is a separate
  flag; `TimeScale` holds the *intended* speed. Setting `TimeScale = 2` then
  `Pause()` then `Resume()` returns to 2x, not 1x.
- `IsPaused` is the *combined* state: true whenever `_paused` is set **or**
  `TimeScale == 0f`. `ScaledDeltaSeconds` is zero whenever `IsPaused`.
- `TimeScale` covers the full range with one knob: `0` = paused,
  `0 < x < 1` = slow-mo, `1` = normal, `> 1` = fast-forward. Negative values are
  clamped to `0` on set.
- `Update(gameTime)` reads `gameTime.ElapsedGameTime.TotalSeconds` into
  `RealDeltaSeconds`, recomputes `ScaledDeltaSeconds`, and fires `Paused` /
  `Resumed` exactly once on any transition of `IsPaused`. It does **not** fire
  per frame.
- Before the first `Update`, both deltas are `0`.

## ScreenManager integration (KhaozEngine.Screens)

```csharp
public ScreenManager(InputManager input);                  // creates its own GameClock
public ScreenManager(InputManager input, GameClock clock); // shares an external clock
public GameClock Clock { get; }

// ambient forwarders (read-only convenience over Clock):
public bool  IsPaused           { get; }
public float TimeScale          { get; set; }
public float RealDeltaSeconds   { get; }
public float ScaledDeltaSeconds { get; }
```

`Update` advances the clock **first**, then drives transitions on **real** dt so
they stay live while paused:

```csharp
public void Update(GameTime gameTime)
{
    Clock.Update(gameTime);
    float dt = Clock.RealDeltaSeconds;   // was: (float)gameTime.ElapsedGameTime.TotalSeconds
    GameScreen[] snapshot = _screens.ToArray();
    ...
}
```

`GameScreen.Update(GameTime, bool)` is **unchanged**. Gameplay screens reach the
scaled dt ambiently via `Manager.ScaledDeltaSeconds` instead of computing dt from
`gameTime`. UI, transitions, notifications, and the `TextInputHandler` caret keep
reading real time (they already do) and so stay live during pause.

### Lifecycle hooks

`GameScreen` gains:

```csharp
protected virtual void OnPause()  { }
protected virtual void OnResume() { }
```

`ScreenManager` subscribes to its `Clock.Paused` / `Clock.Resumed` in the
constructor and dispatches the corresponding virtual to every screen currently in
the stack (iterating a snapshot to tolerate add/remove). Screen-less consumers
wire the clock events directly. Hooks fire once per transition.

> Dispatch detail: `OnPause`/`OnResume` are `protected`, so `ScreenManager` calls
> them via an `internal` shim on `GameScreen` (e.g. `internal void RaisePause() =>
> OnPause();`) since the two types are in the same assembly.

## Consumer patterns (documented, not enforced)

Input gating is **not** enforced by the engine. The engine ships the clock; the
existing top-down input routing covers the overlay case for free.

- **Overlay pause:** push the pause menu with `PassUpdateThrough = true`.
  Gameplay below still `Update`s (animates on real dt) but receives
  `receivesInput = false` and `ScaledDeltaSeconds = 0` freezes its sim.
- **Overlay-less pause:** `Manager.Clock.Pause()`. The gameplay screen guards
  discrete actions with `if (Manager.IsPaused) return;` and integrates movement
  against `Manager.ScaledDeltaSeconds`.
- **ECS:** `world.Update(Manager.ScaledDeltaSeconds)`.
- **Slow-mo / fast-forward:** `Manager.Clock.TimeScale = 0.5f;` /
  `Manager.Clock.TimeScale = 2f;` and back to `1f` to clear.

## SpaceGame-safety story

Default `TimeScale == 1`, not paused, so
`ScaledDeltaSeconds == RealDeltaSeconds == gameTime.ElapsedGameTime.TotalSeconds`
- identical to today. Nothing reads the clock until a consumer opts in. No
`Update` signature change and no input-routing change. SpaceGame's lockstep uses
a fixed timestep and computes its own dt, never touching `ScaledDeltaSeconds`, so
determinism is preserved even after it adopts 2.2.0. The feature is purely
additive and inert by default.

## Testing

All headless, mirroring `ScreenManagerTests`. `GameTime` built as
`new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt))`.

**`GameClock` unit suite (new test file):**
- Default `TimeScale == 1`, `IsPaused == false`, deltas `0` before first `Update`.
- After `Update(dt)`: `RealDeltaSeconds == dt`, `ScaledDeltaSeconds == dt`.
- `TimeScale = 2` then `Update(dt)` → `ScaledDeltaSeconds == 2*dt`,
  `RealDeltaSeconds == dt`.
- `TimeScale = 0.5` → `ScaledDeltaSeconds == 0.5*dt` (slow-mo).
- Negative `TimeScale` clamps to `0`.
- `Pause()` → `IsPaused`, `ScaledDeltaSeconds == 0`, `RealDeltaSeconds == dt`.
- `Pause()` with `TimeScale = 2` then `Resume()` restores 2x (memory).
- `TimeScale = 0` reports `IsPaused == true` and fires `Paused`.
- `Paused`/`Resumed` fire exactly once per transition, not per frame.

**`ScreenManager` integration:**
- Transitions advance while paused (push a transitioning screen, `Pause()`, run
  frames, confirm `TransitionAlpha` still progresses on real dt).
- `OnPause` / `OnResume` dispatched to every stacked screen on transition; a
  test screen records the calls.
- Injected `GameClock` is the one exposed via `Manager.Clock`.
- `Manager.ScaledDeltaSeconds` reflects `TimeScale` after a frame.

Add `KhaozEngine.Time` to the test project references.

## Release ritual (single commit)

1. `Directory.Build.props` `<Version>` 2.1.0 → 2.2.0.
2. `CHANGELOG.md` newest-first entry (new `KhaozEngine.Time` package, `GameClock`,
   `ScreenManager` clock + `OnPause`/`OnResume`, additive/opt-in, default
   unchanged, SpaceGame-safe).
3. `docs/CONSUMERS.md` engine-version line → 2.2.0.
4. New `KhaozEngine.Time/KhaozEngine.Time.csproj` added to `KhaozEngine.slnx`.
5. `dotnet pack -c Release -o ./local-feed` (cumulative; keep old versions).
6. Commit, `git tag v2.2.0`, push `main` + tag.
7. Ping the user to bump Hardpoint.

## Out of scope / YAGNI

- No upper clamp on `TimeScale` (consumers choose their own max).
- No per-screen "freezes while paused" flag (input gating left to consumers).
- No `.Ecs` changes; ECS stays clock-agnostic.
- No automatic pause-overlay policy in the router.
