using System;

namespace KhaozEngine.UI;

/// <summary>
/// Formats durations into compact human-readable strings.
/// Shows only the highest meaningful unit downward:
/// "18s", "2:34", "1:02:34", "3d 4:02:34".
/// </summary>
public static class TimeFormatter
{
    /// <summary>
    /// Formats a duration in seconds into a compact string.
    /// Only includes units from the highest non-zero unit downward.
    /// Examples: "18s", "2:34", "1:02:34", "3d 4:02:34".
    /// </summary>
    public static string Format(double totalSeconds)
    {
        if (totalSeconds <= 0) return "0s";
        if (double.IsInfinity(totalSeconds) || double.IsNaN(totalSeconds)) return "---";

        int total = (int)Math.Ceiling(totalSeconds);
        int days = total / 86400;
        int hours = (total % 86400) / 3600;
        int minutes = (total % 3600) / 60;
        int seconds = total % 60;

        if (days > 0)
            return $"{days}d {hours}:{minutes:D2}:{seconds:D2}";
        if (hours > 0)
            return $"{hours}:{minutes:D2}:{seconds:D2}";
        if (minutes > 0)
            return $"{minutes}:{seconds:D2}";
        return $"{seconds}s";
    }
}
