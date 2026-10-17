using System;
using System.Collections.Generic;
using KhaozEngine.Imaging;
using KhaozEngine.Render2D;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// Decodes the report screen's thumbnails from the encoded media E1 captures. Every member is pure and keeps no
/// shared state, so a caller may run them on the thread pool. Texture creation stays with the caller.
/// </summary>
public static class ReportThumbnails
{
    /// <summary>The longest edge of a screenshot thumbnail, in pixels.</summary>
    public const int ScreenshotLongEdge = 320;

    const string Png = "image/png";
    const string Jpeg = "image/jpeg";

    /// <summary>
    /// Decodes <paramref name="image"/> and box downscales it so its longer edge is at most
    /// <see cref="ScreenshotLongEdge"/>. A PNG decodes through <see cref="ImageRgba.Decode"/>. A JPEG decodes at one
    /// eighth scale through <see cref="JpegThumbnailDecoder"/>. Decoder exceptions propagate, so the caller shows no
    /// thumbnail: <see cref="System.IO.InvalidDataException"/> or <see cref="NotSupportedException"/> from the JPEG
    /// decoder, and whatever the PNG decoder throws for a corrupt file.
    /// </summary>
    /// <exception cref="NotSupportedException">The media type is neither PNG nor JPEG.</exception>
    public static ImageRgba FromScreenshot(ReportImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ImageRgba decoded = image.MediaType switch
        {
            Png => ImageRgba.Decode(image.Bytes.Span),
            Jpeg => FromJpeg(image.Bytes.Span),
            _ => throw new NotSupportedException($"Unsupported screenshot media type '{image.MediaType}'."),
        };
        return Downscale(decoded, ScreenshotLongEdge);
    }

    /// <summary>
    /// Decodes a clip frame at one eighth scale through <see cref="JpegThumbnailDecoder"/>, so a 960 pixel frame
    /// gives a 120 pixel thumbnail. Decoder exceptions propagate, so the caller leaves that frame out:
    /// <see cref="System.IO.InvalidDataException"/> for a malformed or truncated frame and
    /// <see cref="NotSupportedException"/> for an unsupported coding process.
    /// </summary>
    public static ImageRgba FromClipFrame(ReportClipFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return FromJpeg(frame.Jpeg.Span);
    }

    /// <summary>
    /// Up to <paramref name="strip"/> evenly spaced frame indices, first and last included. Fewer frames than
    /// <paramref name="strip"/> give every index, and no frames give none.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="strip"/> is less than 2.</exception>
    public static IReadOnlyList<int> StripIndices(int frameCount, int strip = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(strip, 2);
        if (frameCount <= 0) return Array.Empty<int>();
        int count = Math.Min(frameCount, strip);
        var indices = new int[count];
        if (count < strip)
        {
            for (int i = 0; i < count; i++) indices[i] = i;
            return indices;
        }
        long span = frameCount - 1;
        long steps = strip - 1;
        for (int i = 0; i < count; i++)
            indices[i] = (int)((2 * i * span + steps) / (2 * steps));   // i * span / steps, rounded half up
        return indices;
    }

    static ImageRgba FromJpeg(ReadOnlySpan<byte> jpeg)
    {
        JpegThumbnail thumbnail = JpegThumbnailDecoder.DecodeEighthScale(jpeg);
        return new ImageRgba(thumbnail.Rgba, thumbnail.Width, thumbnail.Height);
    }

    /// <summary>Averages whole source boxes into a target whose longer edge is <paramref name="longEdge"/>.</summary>
    static ImageRgba Downscale(ImageRgba source, int longEdge)
    {
        int sw = source.Width, sh = source.Height;
        if (Math.Max(sw, sh) <= longEdge) return source;
        double scale = (double)longEdge / Math.Max(sw, sh);
        int tw = Math.Clamp((int)Math.Round(sw * scale), 1, longEdge);
        int th = Math.Clamp((int)Math.Round(sh * scale), 1, longEdge);
        byte[] src = source.Pixels;
        var dst = new byte[tw * th * 4];
        for (int ty = 0; ty < th; ty++)
        {
            int y0 = (int)((long)ty * sh / th);
            int y1 = Math.Max(y0 + 1, (int)((long)(ty + 1) * sh / th));
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = (int)((long)tx * sw / tw);
                int x1 = Math.Max(x0 + 1, (int)((long)(tx + 1) * sw / tw));
                long r = 0, g = 0, b = 0, a = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = (y * sw + x0) * 4;
                    for (int x = x0; x < x1; x++, row += 4)
                    {
                        r += src[row];
                        g += src[row + 1];
                        b += src[row + 2];
                        a += src[row + 3];
                    }
                }
                long n = (long)(x1 - x0) * (y1 - y0);
                int o = (ty * tw + tx) * 4;
                dst[o] = (byte)((r + n / 2) / n);
                dst[o + 1] = (byte)((g + n / 2) / n);
                dst[o + 2] = (byte)((b + n / 2) / n);
                dst[o + 3] = (byte)((a + n / 2) / n);
            }
        }
        return new ImageRgba(dst, tw, th);
    }
}
