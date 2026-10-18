using System;

namespace KhaozEngine.BugReports.Client;

/// <summary>How the rolling clip records: its length, its rate, its frame size and its memory cap.</summary>
public sealed record FrameHistoryOptions
{
    /// <summary>How far back the clip reaches.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Clip frames captured per second, independent of the display rate.</summary>
    public int FramesPerSecond { get; init; } = 12;

    /// <summary>The longest edge of a clip frame in pixels. A smaller window is never scaled up.</summary>
    public int LongEdge { get; init; } = 960;

    /// <summary>JPEG quality of each clip frame, 1 to 100.</summary>
    public int JpegQuality { get; init; } = 70;

    /// <summary>Hard cap on the encoded bytes held. The oldest frames go first when it is exceeded.</summary>
    public int MaxBytes { get; init; } = 8 * 1024 * 1024;
}
