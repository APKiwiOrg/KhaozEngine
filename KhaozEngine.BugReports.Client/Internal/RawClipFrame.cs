using System;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>One mapped clip frame waiting to be encoded.</summary>
/// <param name="Rgba">
/// Pixels in RGBA order, rented from the encode queue's pool. It may be longer than
/// <c>Width * Height * 4</c>, and only that prefix holds the frame.
/// </param>
/// <param name="Width">Pixel width of the frame.</param>
/// <param name="Height">Pixel height of the frame.</param>
/// <param name="CapturedAt">When the frame was captured, on the recorder's clock.</param>
/// <param name="RenderMilliseconds">The frame loop's unclamped delta for that frame.</param>
/// <param name="Generation">The <see cref="FrameHistory.Generation"/> current when the frame was captured.</param>
internal sealed record RawClipFrame(byte[] Rgba, int Width, int Height, TimeSpan CapturedAt, double RenderMilliseconds,
    int Generation);
