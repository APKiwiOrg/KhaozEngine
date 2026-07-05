# KhaozEngine.Progression

Wall-clock progression primitives for KhaozEngine. GPU-free, zero third-party dependencies, part of the
`KhaozEngine.Foundation` umbrella.

## `WallClockRewardSchedule`

A generic "every N of **real-world** time a reward becomes available and stays available until claimed"
scheduler, as a pure `readonly struct` over `DateTimeOffset`.

It is built for daily-login / periodic-reward features that must track **real** wall-clock time, so it is:

- **Immune to game speed and offline caps.** Availability is measured against the UTC instant you pass in,
  never a sim clock, so game `TimeScale` and offline / time-skip catch-up caps can't distort it.
- **Non-stacking.** At most one reward is available at a time no matter how long the player was away, and
  claiming schedules the next one exactly one interval later.
- **Clock-step safe.** A backward wall-clock step (NTP correction, user changing the clock) or an
  implausibly far-future timestamp can neither brick the schedule nor spam rewards - it's a persistent
  boolean threshold, not an event, and the arithmetic saturates instead of overflowing.
- **Persistence-agnostic.** It holds two plain serializable values (`Interval` + `NextAvailableUtc`); the
  consumer stores them (typically just `NextAvailableUtc`, with the interval from config) in its own save
  and reconstructs the struct on load. Timestamps are normalised to UTC (a local offset is *converted*,
  never relabelled), so a schedule round-trips cleanly.

### API

```csharp
public readonly struct WallClockRewardSchedule
{
    TimeSpan       Interval          { get; init; }
    DateTimeOffset NextAvailableUtc  { get; init; }   // the one value to persist

    bool     IsAvailable(DateTimeOffset nowUtc);
    TimeSpan TimeUntilAvailable(DateTimeOffset nowUtc);   // clamped to zero once available (HUD countdown)
    WallClockRewardSchedule Claim(DateTimeOffset nowUtc); // consume, schedule the next one interval out

    static WallClockRewardSchedule Start(TimeSpan interval, DateTimeOffset nowUtc, TimeSpan initialDelay);
    static WallClockRewardSchedule Start(TimeSpan interval, DateTimeOffset nowUtc, bool availableImmediately = false);
}
```

### Usage

```csharp
using KhaozEngine.Progression;

// First run: seed a "1 per 24h real-world" reward. The initialDelay overload is the first-run knob -
// TimeSpan.Zero for an immediate welcome, `interval` for a full period, or a random 0..interval offset
// so the first reward does not always land on the boundary.
var interval = TimeSpan.FromHours(24);
var offset   = TimeSpan.FromSeconds(rng.NextDouble() * interval.TotalSeconds);
var schedule = WallClockRewardSchedule.Start(interval, DateTimeOffset.UtcNow, initialDelay: offset);
Save(schedule.NextAvailableUtc); // persist the instant in your own save

// Each frame / on the reward screen:
var now = DateTimeOffset.UtcNow;
if (schedule.IsAvailable(now))
    ShowTappableReward();                          // presentation stays in the game
else
    ShowCountdown(schedule.TimeUntilAvailable(now));

// When the player taps to collect:
schedule = schedule.Claim(now);                    // non-stacking: next is due one interval after `now`
Save(schedule.NextAvailableUtc);
GrantReward();                                     // the payload (which reward) stays in the game
```

Support N independent rewards by keeping one `WallClockRewardSchedule` per reward id, each with its own
interval.

## Testing

Because the current instant is a plain method parameter (no ambient clock), the type is trivially
unit-testable with fixed `DateTimeOffset` values - no clock injection needed. See
`WallClockRewardScheduleTests` in `KhaozEngine.Tests`.
