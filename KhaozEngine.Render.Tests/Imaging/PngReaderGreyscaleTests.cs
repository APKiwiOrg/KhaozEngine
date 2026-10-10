using System;
using System.IO;
using KhaozEngine.Imaging;
using Xunit;
using static KhaozEngine.Tests.Imaging.PngReaderPaletteInterlaceTests;
using static KhaozEngine.Tests.Imaging.PngReaderTests;

namespace KhaozEngine.Tests.Imaging;

public class PngReaderGreyscaleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Low_depth_greyscale_scales_to_eight_bit(int depth)
    {
        byte[] raw = Ramp(11 * 3, depth);

        PngImage image = PngReader.Decode(Png(11, 3, depth, 0, 0, Filtered(11, 3, depth, raw, interlaced: false)));

        Assert.Equal((1, 8), (image.Channels, image.BitDepth));
        Assert.Equal(Scale(raw, depth), image.Bytes);
    }

    [Fact]
    public void Adam7_two_bit_greyscale_lands_in_raster_order()
    {
        byte[] raw = Ramp(10 * 10, 2);

        PngImage image = PngReader.Decode(Png(10, 10, 2, 0, 1, Filtered(10, 10, 2, raw, interlaced: true)));

        Assert.Equal(Scale(raw, 2), image.Bytes);
    }

    [Fact]
    public void Low_depth_greyscale_colour_key_matches_the_unscaled_sample()
    {
        // Key 5 at 4 bits is the raw sample, not its scaled value 85.
        byte[] raw = { 5, 6, 15, 5, 0 };
        byte[] png = Png(5, 1, 4, 0, 0, Filtered(5, 1, 4, raw, interlaced: false), Chunk("tRNS", new byte[] { 0, 5 }));

        PngImage image = PngReader.Decode(png);

        Assert.Equal((2, 8), (image.Channels, image.BitDepth));
        Assert.Equal(new byte[] { 85, 0, 102, 255, 255, 255, 85, 0, 0, 255 }, image.Bytes);
    }

    [Fact]
    public void Low_depth_greyscale_colour_key_outside_the_depth_is_rejected()
    {
        byte[] png = Png(1, 1, 2, 0, 0, Filtered(1, 1, 2, new byte[] { 0 }, interlaced: false),
            Chunk("tRNS", new byte[] { 0, 4 }));

        Assert.Throws<InvalidDataException>(() => PngReader.Decode(png));
    }

    private static byte[] Ramp(int count, int depth)
    {
        var raw = new byte[count];
        for (int i = 0; i < count; i++) raw[i] = (byte)(i * 3 % (1 << depth));
        return raw;
    }

    private static byte[] Scale(byte[] raw, int depth)
    {
        var scaled = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++) scaled[i] = (byte)(raw[i] * 255 / ((1 << depth) - 1));
        return scaled;
    }
}
