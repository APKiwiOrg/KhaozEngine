using System;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>One encoded clip frame as the ring holds it, stamped with the ring generation it was captured in.</summary>
/// <param name="CapturedAt">When the frame was captured, on the recorder's clock.</param>
/// <param name="RenderMilliseconds">The frame loop's unclamped delta for that frame.</param>
/// <param name="Width">Pixel width of this frame.</param>
/// <param name="Height">Pixel height of this frame.</param>
/// <param name="Jpeg">The encoded JPEG bytes.</param>
/// <param name="Generation">The <see cref="FrameHistory.Generation"/> current when the frame was captured.</param>
internal readonly record struct ClipFrameRecord(TimeSpan CapturedAt, double RenderMilliseconds, int Width, int Height,
    byte[] Jpeg, int Generation);
