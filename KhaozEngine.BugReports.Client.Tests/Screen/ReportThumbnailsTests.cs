using System;
using System.IO;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.Imaging;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client.Screen;

public sealed class ReportThumbnailsTests
{
    static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    [Fact]
    public void Strip_indices_are_evenly_spaced()
    {
        Assert.Equal(new[] { 0, 20, 39, 59 }, ReportThumbnails.StripIndices(60, 4));
        Assert.Equal(new[] { 0, 1, 2 }, ReportThumbnails.StripIndices(3, 4));
        Assert.Empty(ReportThumbnails.StripIndices(0, 4));
        Assert.Equal(new[] { 0, 59 }, ReportThumbnails.StripIndices(60, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportThumbnails.StripIndices(60, 1));
    }

    [Fact]
    public void Png_screenshot_downscales_to_320()
    {
        byte[] png = PngWriter.Encode(Solid(1280, 720, 200, 40, 10), 1280, 720);

        ImageRgba thumbnail = ReportThumbnails.FromScreenshot(new ReportImage("image/png", 1280, 720, png));

        Assert.Equal(320, thumbnail.Width);
        Assert.Equal(180, thumbnail.Height);
        Assert.Equal(new byte[] { 200, 40, 10, 255 }, thumbnail.Pixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 200, 40, 10, 255 }, thumbnail.Pixels.AsSpan(thumbnail.Pixels.Length - 4).ToArray());
    }

    [Fact]
    public void Jpeg_screenshot_and_clip_frame_decode_at_an_eighth()
    {
        byte[] jpeg = JpegWriter.Encode(Solid(960, 540, 128, 128, 128), 960, 540, 80);

        ImageRgba screenshot = ReportThumbnails.FromScreenshot(new ReportImage("image/jpeg", 960, 540, jpeg));
        ImageRgba frame = ReportThumbnails.FromClipFrame(new ReportClipFrame(TimeSpan.Zero, 16.6, 960, 540, jpeg));

        Assert.Equal((120, 68), (screenshot.Width, screenshot.Height));
        Assert.Equal((120, 68), (frame.Width, frame.Height));
        Assert.Throws<NotSupportedException>(() =>
            ReportThumbnails.FromScreenshot(new ReportImage("image/webp", 960, 540, jpeg)));
    }

    [Fact]
    public void Corrupt_frame_throws_a_decoder_exception()
    {
        byte[] jpeg = JpegWriter.Encode(Solid(256, 144, 90, 160, 30), 256, 144, 80);
        ReadOnlyMemory<byte> truncated = jpeg.AsMemory(0, jpeg.Length / 2);

        Assert.Throws<InvalidDataException>(() =>
            ReportThumbnails.FromClipFrame(new ReportClipFrame(TimeSpan.Zero, 16.6, 256, 144, truncated)));
        Assert.Throws<InvalidDataException>(() =>
            ReportThumbnails.FromScreenshot(new ReportImage("image/jpeg", 256, 144, truncated)));
    }
}
