using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The shared RGBA8 box filter on small images with known values. Device-free.</summary>
public sealed class Rgba8StatsTests
{
    [Fact]
    public void A_2x2_box_takes_the_rounded_mean_of_each_block_in_every_byte()
    {
        // Red counts 0 to 15 across the rows, so every block's red mean ends in a half, which rounds up. Blue is a
        // checker of 0 and 255, and alpha carries one extra count in the first block, 100.25, which rounds down.
        byte[] image = Image(4, 4, (x, y) =>
            ((byte)(x + 4 * y), 255, (byte)((x + y) % 2 == 1 ? 255 : 0), (byte)(x == 0 && y == 0 ? 101 : 100)));

        byte[] small = Rgba8Stats.BoxDownsample(image, 4, 4, 2);

        Assert.Equal(new byte[] { 3, 255, 128, 100, 5, 255, 128, 100, 11, 255, 128, 100, 13, 255, 128, 100 }, small);
    }

    [Fact]
    public void A_4x4_box_shrinks_a_4x4_image_to_one_pixel()
    {
        // Red sums to 120 over 16 pixels, 7.5. Green is 255 in one pixel, 15.94. Blue and alpha are flat.
        byte[] image = Image(4, 4, (x, y) => ((byte)(x + 4 * y), (byte)(x == 3 && y == 3 ? 255 : 0), 200, 255));

        Assert.Equal(new byte[] { 8, 16, 200, 255 }, Rgba8Stats.BoxDownsample(image, 4, 4, 4));
    }

    [Fact]
    public void A_size_that_is_not_a_multiple_of_the_factor_is_refused()
    {
        byte[] image = Image(6, 4, (_, _) => (0, 0, 0, 255));

        Assert.Throws<ArgumentException>(() => Rgba8Stats.BoxDownsample(image, 6, 4, 4));
    }

    static byte[] Image(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                int i = (y * width + x) * 4;
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a;
            }
        return rgba;
    }
}
