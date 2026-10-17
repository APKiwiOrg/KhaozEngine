using System;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client.Internal;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

public sealed class ScreenshotEncoderTests
{
    static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    static byte[] Gradient(int width, int height, int length)
    {
        var rgba = new byte[length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                rgba[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                rgba[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                rgba[i + 2] = 128;
                rgba[i + 3] = 255;
            }
        return rgba;
    }

    static byte[] Noise(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        uint state = 0x9E3779B9u;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            rgba[i] = (byte)state;
            rgba[i + 1] = (byte)(state >> 8);
            rgba[i + 2] = (byte)(state >> 16);
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    [Fact]
    public void A_small_image_is_a_PNG()
    {
        ReportImage image = ScreenshotEncoder.Encode(Gradient(64, 32, 64 * 32 * 4), 64, 32);

        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(64, image.Width);
        Assert.Equal(32, image.Height);
        Assert.True(image.Bytes.Span.StartsWith(PngSignature));
        Assert.True(image.Bytes.Length < ScreenshotEncoder.PngLimitBytes);
    }

    [Fact]
    public void An_image_whose_PNG_is_4_MB_or_more_falls_back_to_JPEG_quality_92()
    {
        Assert.Equal(92, ScreenshotEncoder.JpegQuality);
        ReportImage image = ScreenshotEncoder.Encode(Noise(2560, 1440), 2560, 1440);

        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(2560, image.Width);
        Assert.Equal(1440, image.Height);
        ReadOnlySpan<byte> bytes = image.Bytes.Span;
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);

        int dqt = bytes.IndexOf(new byte[] { 0xFF, 0xDB });
        Assert.True(dqt > 0);
        Assert.Equal(0x00, bytes[dqt + 4]);
        // Annex K luma entry 0 is 16. Quality 92 scales by 200 - 2 * 92 = 16 percent, rounded: (16 * 16 + 50) / 100 = 3.
        Assert.Equal(3, bytes[dqt + 5]);
    }

    [Fact]
    public void A_buffer_longer_than_one_frame_is_encoded_from_its_prefix()
    {
        const int width = 40, height = 24;
        byte[] exact = Gradient(width, height, width * height * 4);
        byte[] rented = Gradient(width, height, width * height * 4 + 1000);
        rented.AsSpan(width * height * 4).Fill(0xAB);

        ReportImage fromExact = ScreenshotEncoder.Encode(exact, width, height);
        ReportImage fromRented = ScreenshotEncoder.Encode(rented, width, height);

        Assert.Equal(fromExact.MediaType, fromRented.MediaType);
        Assert.Equal(fromExact.Bytes.ToArray(), fromRented.Bytes.ToArray());
    }
}
