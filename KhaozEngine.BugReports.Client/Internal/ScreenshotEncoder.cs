using System;
using KhaozEngine.Imaging;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>Encodes the trigger frame's screenshot as PNG when it fits under the limit, JPEG otherwise.</summary>
internal static class ScreenshotEncoder
{
    /// <summary>The longest screenshot edge. The capture path delivers at most this, so the encoder never resizes.</summary>
    internal const int MaxLongEdge = 2560;

    /// <summary>A PNG this size or larger is replaced by a JPEG.</summary>
    internal const int PngLimitBytes = 4 * 1024 * 1024;

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
        if (png.Length < PngLimitBytes) return new ReportImage("image/png", width, height, png);

        return new ReportImage("image/jpeg", width, height, JpegWriter.Encode(pixels, width, height, JpegQuality));
    }
}
