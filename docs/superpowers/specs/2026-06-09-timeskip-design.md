# KhaozEngine TimeSkip (analytical catch-up, 2.3.0)

## Goal

A small, game-agnostic helper for "advance the simulation by N hours" - both
on-demand ("skip +2h for some credits") and offline catch-up ("you were away
3h"). It owns the bookkeeping (cap, multiplier, minimum threshold, elapsed-time
calc, a Completed event) and delegates the actual advancement to a
consumer-supplied **analytical** catch-up callback.

It deliberately does **not** replay per-frame ticks and has no per-frame budget /
progress machinery. Investigation of the driving consumer (Nullwake) showed its
catch-up is already analytical and instant (`MiningSystem.SimulateOffline` runs
an event-stepped loop that "skips months in milliseconds"), and that fixed-tick
replay does not scale to large skips (72h at a 1/30s tick = ~7.8M ticks). The
analytical approach is the one that scales, and it needs no budgeting, so this
spec builds only the bookkeeping + delegation around it.

Additive and opt-in. No consumer is forced to adopt it; SpaceGame (deterministic
lockstep) is unaffected.

## Package placement

New type in the existing **`KhaozEngine.Time`** package, next to `GameClock`. No
new package. `TimeSkip` is decoupled from `GameClock`: the wall-clock-to-sim
conversion takes a plain `double timeScale`, so `TimeSkip` is testable without a
`GameClock` or MonoGame `GameTime`.

Version: additive = minor, **2.3.0** (all packages bump together per the unified
version line).

## API (KhaozEngine.Time)

```csharp
namespace KhaozEngine.Time;

/// <summary>What a <see cref="TimeSkip.Advance"/> call actually applied.</summary>
public readonly struct TimeSkipResult
{
    /// <summary>The sim-seconds requested, before cap and multiplier.</summary>
    public double RequestedSimSeconds { get; }

    /// <summary>The sim-seconds handed to the step callback (post cap, post multiplier).</summary>
    public double AppliedSimSeconds { get; }

    /// <summary>True when <see cref="RequestedSimSeconds"/> exceeded the cap and was clamped.</summary>
    public bool WasCapped { get; }

    /// <summary>True if the step callback ran; false when the request was below the minimum threshold.</summary>
    public bool Ran { get; }

    public TimeSkipResult(double requestedSimSeconds, double appliedSimSeconds, bool wasCapped, bool ran);
}

/// <summary>
/// Advances a simulation by a span of sim-time in one shot, applying optional cap / multiplier /
/// minimum-threshold policy, then invoking the consumer's analytical catch-up callback. Used for
/// on-demand fast-forward ("skip +2h") and offline catch-up ("away 3h"). Synchronous and instant:
/// the callback is expected to be analytical (O(events), not O(ticks)), so there is no per-frame
/// budget or progress.
/// </summary>
public sealed class TimeSkip
{
    /// <summary>Optional cap on requested sim-seconds. Null = uncapped. (Nullwake offline cap = 7200.)</summary>
    public double? MaxSimSeconds { get; set; }

    /// <summary>Scales the applied sim-seconds after the cap. (Nullwake "earnings multiplier".) Default 1.</summary>
    public double Multiplier { get; set; } = 1.0;

    /// <summary>Requests below this many sim-seconds are a no-op (callback not invoked). Default 0.
    /// (Nullwake offline threshold = 60.)</summary>
    public double MinSimSeconds { get; set; } = 0.0;

    /// <summary>Raised after a successful or no-op <see cref="Advance"/>, carrying the result (for UI).</summary>
    public event Action<TimeSkipResult>? Completed;

    /// <summary>
    /// Advance by <paramref name="simSeconds"/>: clamp to <see cref="MaxSimSeconds"/>, multiply by
    /// <see cref="Multiplier"/>, then invoke <paramref name="step"/> once with the applied seconds.
    /// If the request is below <see cref="MinSimSeconds"/>, <paramref name="step"/> is not called.
    /// Always raises <see cref="Completed"/> and returns the result.
    /// </summary>
    public TimeSkipResult Advance(double simSeconds, Action<double> step);

    /// <summary>
    /// Pure helper for offline catch-up: real wall-seconds between two stamps, clamped to >= 0, then
    /// scaled by <paramref name="timeScale"/> (pass <see cref="GameClock.TimeScale"/> if you want
    /// offline time to honour the sim speed; pass 1.0 for raw wall time).
    /// </summary>
    public static double ElapsedSimSeconds(DateTimeOffset lastSave, DateTimeOffset now, double timeScale = 1.0);
}
```

### Semantics

- **Order of operations in `Advance`:**
  1. `requested = simSeconds`.
  2. No-op guard: if `requested <= 0` **or** `requested < MinSimSeconds`, return
     `new TimeSkipResult(requested, 0, false, ran: false)`, raise `Completed`, and do **not** call
     `step`. (So a zero/negative request is always a no-op, even with the default `MinSimSeconds == 0`.)
  3. `capped = MaxSimSeconds is double m ? Math.Min(requested, m) : requested`;
     `wasCapped = MaxSimSeconds is double mm && requested > mm`.
  4. `applied = capped * Multiplier`.
  5. Call `step(applied)`.
  6. Return `new TimeSkipResult(requested, applied, wasCapped, ran: true)`, raise `Completed`.
- `step` exceptions propagate (a consumer catch-up bug should not be swallowed); `Completed` does
  not fire if `step` throws.
- `ElapsedSimSeconds`: `Math.Max(0.0, (now - lastSave).TotalSeconds) * timeScale`. Negative spans
  (clock skew / future stamp) clamp to 0.
- `TimeSkip` holds no per-call state; the same instance can be reused for every skip.

## Consumer patterns

```csharp
// On-demand "+2h for credits": explicit, uncapped, instant.
var skip = new TimeSkip();
skip.Advance(2 * 3600, s => world.CatchUp(s));

// Offline catch-up: timestamp-driven, capped + multiplied, with UI hook.
var skip = new TimeSkip { MaxSimSeconds = 7200, MinSimSeconds = 60, Multiplier = 1.0 };
skip.Completed += r => hud.ShowOfflineEarnings(r);
double elapsed = TimeSkip.ElapsedSimSeconds(save.LastSaveTime, now, clock.TimeScale);
skip.Advance(elapsed, s => world.CatchUp(s));
```

`world.CatchUp(double simSeconds)` is the consumer's analytical advancement (for Nullwake, its
existing `MiningSystem.SimulateOffline`). The engine simulates nothing itself.

## Testing

Headless, pure (no `GameTime`, no `GameClock` needed). Mirror `GameClockTests` style.

**`TimeSkip` suite:**
- `Advance` passes the applied seconds (after multiplier) to `step` and returns `Ran == true`.
- Uncapped (`MaxSimSeconds == null`): `AppliedSimSeconds == requested * Multiplier`, `WasCapped == false`.
- Cap clamps: request above `MaxSimSeconds` -> `step` gets the capped*multiplier value,
  `WasCapped == true`, `RequestedSimSeconds` retains the original request.
- Request below `MinSimSeconds`: `step` is never called, `Ran == false`, `AppliedSimSeconds == 0`,
  `Completed` still fires.
- `Multiplier` scales applied seconds (e.g. cap 7200, multiplier 2 -> applied 14400).
- Zero or negative `simSeconds`: always a no-op (`Ran == false`, `step` not called) even with the
  default `MinSimSeconds == 0`.
- `Completed` fires once per `Advance` with the same result that was returned.

**`ElapsedSimSeconds` suite:**
- Positive span returns wall-seconds (`timeScale` default 1).
- `timeScale` scales the result (e.g. 1h elapsed at scale 2 -> 7200).
- `now` earlier than `lastSave` clamps to 0.

Add tests to the existing test project (already references `KhaozEngine.Time`).

## Release ritual (single commit)

1. `Directory.Build.props` `<Version>` 2.2.0 -> 2.3.0.
2. `CHANGELOG.md` newest-first entry (new `TimeSkip` / `TimeSkipResult` in `KhaozEngine.Time`;
   analytical one-shot catch-up; cap/multiplier/threshold; `ElapsedSimSeconds` helper; additive/opt-in).
3. `docs/CONSUMERS.md` engine-version line -> 2.3.0 (matrix Time column stays `-`; no consumer adopts yet).
4. `dotnet pack -c Release -o ./local-feed` (cumulative).
5. Commit, `git tag v2.3.0` (do NOT push without user go-ahead).

## Out of scope / YAGNI

- No per-frame budget, progress, or cancel (analytical = instant; not needed). If a future game has
  a heavy per-tick sim that cannot go analytical, a budgeted tick-pump can be designed then.
- No `IProjectable`/projection interface (the catch-up logic is 100% game-specific; the callback is
  the seam).
- No `DateTime.UtcNow` inside the engine - the consumer supplies `now` (keeps it headless-testable).
- No `GameClock` dependency in `TimeSkip` (scale passed as a plain double).
- No consumer migration in this change (Nullwake adopting `GameClock`/`TimeSkip` is separate work).
```
