using System;
using System.IO;
using KhaozEngine.Imaging;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class RgbaImageTests
{
    [Fact]
    public void Indexer_reads_back_what_it_writes_in_row_major_order()
    {
        var img = new RgbaImage(3, 2);
        img[2, 1] = new Rgba32(10, 20, 30, 40);

        Assert.Equal(new Rgba32(10, 20, 30, 40), img[2, 1]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), img[1, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => img[3, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => img[0, 2]);
    }

    [Fact]
    public void Png_round_trip_preserves_every_channel_and_position()
    {
        var img = new RgbaImage(4, 3);
        img[0, 0] = new Rgba32(1, 2, 3, 4);
        img[3, 0] = new Rgba32(250, 0, 128, 255);
        img[1, 2] = new Rgba32(9, 8, 7, 0);
        img[3, 2] = new Rgba32(255, 255, 255, 128);

        string dir = Directory.CreateTempSubdirectory("rgba_image_test_").FullName;
        try
        {
            string path = Path.Combine(dir, "round.png");
            img.SaveAsPng(path);
            var back = RgbaImage.Load(path);

            Assert.Equal(4, back.Width);
            Assert.Equal(3, back.Height);
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 4; x++)
                    Assert.Equal(img[x, y], back[x, y]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Rgb_input_gets_opaque_alpha()
    {
        var png = new PngImage(2, 1, Channels: 3, BitDepth: 8, new byte[] { 1, 2, 3, 4, 5, 6 });

        var img = RgbaImage.FromPng(png);

        Assert.Equal(new Rgba32(1, 2, 3, 255), img[0, 0]);
        Assert.Equal(new Rgba32(4, 5, 6, 255), img[1, 0]);
    }

    [Fact]
    public void Grey_and_grey_alpha_inputs_replicate_the_grey_sample()
    {
        var grey = RgbaImage.FromPng(new PngImage(1, 1, Channels: 1, BitDepth: 8, new byte[] { 77 }));
        var greyAlpha = RgbaImage.FromPng(new PngImage(1, 1, Channels: 2, BitDepth: 8, new byte[] { 77, 9 }));

        Assert.Equal(new Rgba32(77, 77, 77, 255), grey[0, 0]);
        Assert.Equal(new Rgba32(77, 77, 77, 9), greyAlpha[0, 0]);
    }

    [Fact]
    public void Sixteen_bit_samples_round_to_the_nearest_eight_bit_value()
    {
        // 0xFFFF -> 255, 0x8080 (128 * 257) -> 128, 0x0080 (128 / 257 rounds to 0) -> 0, 0x0081 -> 1.
        var png = new PngImage(1, 1, Channels: 4, BitDepth: 16,
            new byte[] { 0xFF, 0xFF, 0x80, 0x80, 0x00, 0x80, 0x00, 0x81 });

        var img = RgbaImage.FromPng(png);

        Assert.Equal(new Rgba32(255, 128, 0, 1), img[0, 0]);
    }
}
