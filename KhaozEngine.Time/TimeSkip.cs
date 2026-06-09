using System;

namespace KhaozEngine.Time;

/// <summary>What a <see cref="TimeSkip.Advance"/> call actually applied.</summary>
public readonly struct TimeSkipResult
{
    /// <summary>The sim-seconds requested, before cap and multiplier.</summary>
    public double RequestedSimSeconds { get; }

    /// <summary>The sim-seconds handed to the step callback (post cap, post multiplier); 0 on a no-op.</summary>
    public double AppliedSimSeconds { get; }

    /// <summary>True when <see cref="RequestedSimSeconds"/> exceeded the cap and was clamped.</summary>
    public bool WasCapped { get; }

    /// <summary>True if the step callback ran; false when the request was a no-op (&lt;= 0 or below the minimum).</summary>
    public bool Ran { get; }

    /// <summary>Creates a result.</summary>
    public TimeSkipResult(double requestedSimSeconds, double appliedSimSeconds, bool wasCapped, bool ran)
    {
        RequestedSimSeconds = requestedSimSeconds;
        AppliedSimSeconds = appliedSimSeconds;
        WasCapped = wasCapped;
        Ran = ran;
    }
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
    /// <summary>Optional cap on requested sim-seconds. Null = uncapped.</summary>
    public double? MaxSimSeconds { get; set; }

    /// <summary>Scales the applied sim-seconds after the cap. Default 1.</summary>
    public double Multiplier { get; set; } = 1.0;

    /// <summary>Requests below this many sim-seconds are a no-op (callback not invoked). Default 0.</summary>
    public double MinSimSeconds { get; set; } = 0.0;

    /// <summary>Raised after every <see cref="Advance"/> (including no-ops), carrying the result.</summary>
    public event Action<TimeSkipResult>? Completed;

    /// <summary>
    /// Advance by <paramref name="simSeconds"/>: clamp to <see cref="MaxSimSeconds"/>, multiply by
    /// <see cref="Multiplier"/>, then invoke <paramref name="step"/> once with the applied seconds.
    /// A request of 0 or less, or below <see cref="MinSimSeconds"/>, is a no-op (step not called).
    /// Always raises <see cref="Completed"/> and returns the result.
    /// </summary>
    public TimeSkipResult Advance(double simSeconds, Action<double> step)
    {
        if (step is null) throw new ArgumentNullException(nameof(step));

        if (simSeconds <= 0.0 || simSeconds < MinSimSeconds)
        {
            var noop = new TimeSkipResult(simSeconds, 0.0, wasCapped: false, ran: false);
            Completed?.Invoke(noop);
            return noop;
        }

        double capped = simSeconds;
        bool wasCapped = false;
        if (MaxSimSeconds is double max && simSeconds > max)
        {
            capped = max;
            wasCapped = true;
        }

        double applied = capped * Multiplier;
        step(applied);

        var result = new TimeSkipResult(simSeconds, applied, wasCapped, ran: true);
        Completed?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Pure helper for offline catch-up: real wall-seconds between two stamps, clamped to &gt;= 0, then
    /// scaled by <paramref name="timeScale"/> (pass <c>GameClock.TimeScale</c> to honour sim speed, or
    /// 1.0 for raw wall time).
    /// </summary>
    public static double ElapsedSimSeconds(DateTimeOffset lastSave, DateTimeOffset now, double timeScale = 1.0)
        => Math.Max(0.0, (now - lastSave).TotalSeconds) * timeScale;
}
