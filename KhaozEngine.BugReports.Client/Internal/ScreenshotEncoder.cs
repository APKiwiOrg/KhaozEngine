using System;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Imaging;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>
/// Encodes the trigger frame's screenshot as PNG when it is under <see cref="BugReportLimits.MaxPngScreenshotBytes"/>,
/// JPEG otherwise. The capture path delivers at most <see cref="BugReportLimits.MaxScreenshotLongEdge"/>, so the
/// encoder never resizes.
/// </summary>
internal static class ScreenshotEncoder
{
    internal const int JpegQuality = 92;

    /// <summary>
    /// Encodes the first <c>width * height * 4</c> bytes of <paramref name="rgba"/>. The buffer may be longer, such
    /// as a pooled one.
    /// </summary>
    internal static ReportImage Encode(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ReadOnlySpan<byte> pixels = rgba.AsSpan(0, checked(width * height * 4));

        byte[] png = PngWriter.Encode(pixels, width, height);
        if (png.Length < BugReportLimits.MaxPngScreenshotBytes) return new ReportImage(ImageHeaders.PngMediaType, width, height, png);

        return new ReportImage(ImageHeaders.JpegMediaType, width, height, JpegWriter.Encode(pixels, width, height, JpegQuality));
    }
}
