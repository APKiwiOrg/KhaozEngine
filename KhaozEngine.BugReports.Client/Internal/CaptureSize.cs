using System;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>Fits a frame into a capture size whose longest edge is at most a limit, keeping the aspect ratio.</summary>
internal static class CaptureSize
{
    /// <summary>
    /// Scales <paramref name="width"/> by <paramref name="height"/> down so the longer edge equals
    /// <paramref name="longEdge"/>, rounding the shorter edge to the nearest pixel and never below 1. A frame
    /// that already fits is returned unchanged, never scaled up.
    /// </summary>
    internal static (int Width, int Height) Fit(int width, int height, int longEdge)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(longEdge);

        int longer = Math.Max(width, height);
        if (longer <= longEdge) return (width, height);

        int shorter = Math.Min(width, height);
        int scaled = (int)Math.Max(1, ((long)shorter * longEdge + longer / 2) / longer);
        return width >= height ? (longEdge, scaled) : (scaled, longEdge);
    }
}
