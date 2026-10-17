using System;

namespace KhaozEngine.BugReports;

/// <summary>One JPEG frame of a report clip. Every frame carries its own size, so a resize mid-clip keeps both.</summary>
/// <param name="Offset">Time since the clip's first frame.</param>
/// <param name="RenderMilliseconds">The frame loop's unclamped delta for the frame this image came from.</param>
/// <param name="Width">Pixel width of this frame.</param>
/// <param name="Height">Pixel height of this frame.</param>
/// <param name="Jpeg">The baseline JPEG bytes.</param>
public sealed record ReportClipFrame(TimeSpan Offset, double RenderMilliseconds, int Width, int Height, ReadOnlyMemory<byte> Jpeg);
