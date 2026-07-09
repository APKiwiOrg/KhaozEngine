using System;
using System.Globalization;
using System.Text;

namespace KhaozEngine.Primitives;

/// <summary>Selects the shape <see cref="TimeFormatter"/> renders a duration in.</summary>
public enum DurationStyle
{
    /// <summary>
    /// Colon clock, highest non-zero unit down: "18s", "2:34", "1:02:34", "3d 4:02:34". Rounds up to the next
    /// whole second (a positive fraction shows the ceiling), so a countdown reads "1s" until it truly hits zero.
    /// </summary>
    Clock,

    /// <summary>
    /// Coarse unit letters from the most-significant non-zero unit: "18s", "45m 30s", "2h 15m", "3d 4h". Shows up
    /// to <c>coarseUnits</c> units (default 2) and truncates (drops the fractional second), for at-a-glance
    /// summaries rather than a ticking clock.
    /// </summary>
    Coarse,
}

/// <summary>
/// Game-agnostic duration formatting: turns a number of seconds into a compact human-readable string in one of two
/// styles (<see cref="DurationStyle"/>). <see cref="DurationStyle.Clock"/> is the ticking colon clock ("1:02:34")
/// for timers/countdowns; <see cref="DurationStyle.Coarse"/> is the two-unit summary ("2h 15m") for stats and
/// "played for" lines. This is the one place that logic lives so every screen reads identically.
/// <para>
/// Non-finite input (NaN / infinity) renders as "---" and non-positive input as "0s", so a formatter call is
/// always safe. Output is culture-invariant. Pure (BCL only), no dependency, so it lives in
/// <c>KhaozEngine.Primitives</c> and is usable from a renderer, a headless server, or a tool.
/// </para>
/// </summary>
public static class TimeFormatter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    const int SecondsPerMinute = 60;
    const int SecondsPerHour = 3600;
    const int SecondsPerDay = 86400;

    /// <summary>
    /// Formats <paramref name="totalSeconds"/> as a duration.
    /// </summary>
    /// <param name="totalSeconds">The duration in seconds. Non-finite renders "---"; non-positive renders "0s".</param>
    /// <param name="style">The output shape (default <see cref="DurationStyle.Clock"/>).</param>
    /// <param name="coarseUnits">For <see cref="DurationStyle.Coarse"/>, how many units to show from the top (default 2, minimum 1); ignored for <see cref="DurationStyle.Clock"/>.</param>
    public static string Format(double totalSeconds, DurationStyle style = DurationStyle.Clock, int coarseUnits = 2)
    {
        if (double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "---";
        if (totalSeconds <= 0) return "0s";

        return style == DurationStyle.Coarse
            ? FormatCoarse(totalSeconds, coarseUnits)
            : FormatClock(totalSeconds);
    }

    static string FormatClock(double totalSeconds)
    {
        // Round up to the next whole second so a countdown shows "1s" until it truly reaches zero.
        long total = (long)Math.Ceiling(totalSeconds);
        int days = (int)(total / SecondsPerDay);
        int hours = (int)(total % SecondsPerDay / SecondsPerHour);
        int minutes = (int)(total % SecondsPerHour / SecondsPerMinute);
        int seconds = (int)(total % SecondsPerMinute);

        if (days > 0) return string.Format(Inv, "{0}d {1}:{2:D2}:{3:D2}", days, hours, minutes, seconds);
        if (hours > 0) return string.Format(Inv, "{0}:{1:D2}:{2:D2}", hours, minutes, seconds);
        if (minutes > 0) return string.Format(Inv, "{0}:{1:D2}", minutes, seconds);
        return string.Format(Inv, "{0}s", seconds);
    }

    static string FormatCoarse(double totalSeconds, int coarseUnits)
    {
        long total = (long)totalSeconds;   // truncate: a coarse summary drops the fractional second
        int days = (int)(total / SecondsPerDay);
        int hours = (int)(total % SecondsPerDay / SecondsPerHour);
        int minutes = (int)(total % SecondsPerHour / SecondsPerMinute);
        int seconds = (int)(total % SecondsPerMinute);

        (int Value, char Unit)[] ladder = [(days, 'd'), (hours, 'h'), (minutes, 'm'), (seconds, 's')];

        // Start at the most-significant non-zero unit (or seconds if everything truncated to zero).
        int start = 0;
        while (start < ladder.Length - 1 && ladder[start].Value == 0) start++;

        if (coarseUnits < 1) coarseUnits = 1;
        var sb = new StringBuilder();
        for (int i = start, taken = 0; i < ladder.Length && taken < coarseUnits; i++, taken++)
        {
            if (taken > 0) sb.Append(' ');
            sb.Append(ladder[i].Value.ToString(Inv)).Append(ladder[i].Unit);
        }
        return sb.ToString();
    }
}
