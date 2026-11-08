using System;
using System.IO;
using KhaozEngine.Imaging;
using Xunit;
using static KhaozEngine.Tests.Imaging.PngReaderTests;

namespace KhaozEngine.Tests.Imaging;

public class PngReaderPaletteInterlaceTests
{
    private static readonly byte[] Palette = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120 };

    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7 =
    {
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Palette_indices_at_every_depth_expand_to_rgb(int depth)
    {
        int entries = Math.Min(4, 1 << depth);
        var indices = new byte[11 * 3];
        for (int i = 0; i < indices.Length; i++) indices[i] = (byte)(i * 7 % entries);
        byte[] png = Png(11, 3, depth, 3, 0, Filtered(11, 3, depth, indices, interlaced: false),
            Chunk("PLTE", Palette.AsSpan(0, entries * 3).ToArray()));

        PngImage image = PngReader.Decode(png);

        Assert.Equal((3, 8), (image.Channels, image.BitDepth));
        Assert.Equal(ExpandPalette(indices, alpha: null), image.Bytes);
    }

    [Fact]
    public void Palette_transparency_adds_alpha_and_uncovered_entries_stay_opaque()
    {
        byte[] indices = { 0, 1, 2, 3 };
        byte[] alpha = { 0, 64, 128 };
        byte[] png = Png(4, 1, 8, 3, 0, Filtered(4, 1, 8, indices, interlaced: false),
            Chunk("PLTE", Palette), Chunk("tRNS", alpha));

        PngImage image = PngReader.Decode(png);

        Assert.Equal(4, image.Channels);
        Assert.Equal(ExpandPalette(indices, alpha), image.Bytes);
    }

    [Theory]
    [InlineData(PngDecodeRefusal.PaletteMissing)]
    [InlineData(PngDecodeRefusal.PaletteIndexOutsideRange)]
    [InlineData(PngDecodeRefusal.PaletteTransparencyTooLong)]
    [InlineData(PngDecodeRefusal.PaletteTransparencyBeforePalette)]
    [InlineData(PngDecodeRefusal.PaletteLengthInvalid)]
    [InlineData(PngDecodeRefusal.PaletteExceedsIndexDepth)]
    public void Malformed_palette_images_are_rejected(PngDecodeRefusal reason)
    {
        byte[] one = { 0, 0 };
        byte[] plte = Chunk("PLTE", Palette);
        byte[] png = reason switch
        {
            PngDecodeRefusal.PaletteMissing => Png(1, 1, 8, 3, 0, one),
            PngDecodeRefusal.PaletteIndexOutsideRange => Png(2, 1, 8, 3, 0, new byte[] { 0, 0, 4 }, plte),
            PngDecodeRefusal.PaletteTransparencyTooLong => Png(1, 1, 8, 3, 0, one, plte, Chunk("tRNS", new byte[5])),
            PngDecodeRefusal.PaletteTransparencyBeforePalette => Png(1, 1, 8, 3, 0, one, Chunk("tRNS", new byte[1]), plte),
            PngDecodeRefusal.PaletteLengthInvalid => Png(1, 1, 8, 3, 0, one, Chunk("PLTE", new byte[4])),
            _ => Png(1, 1, 1, 3, 0, one, plte),
        };
        Assert.Equal(new byte[] { 10, 20, 30 }, PngReader.Decode(Png(1, 1, 8, 3, 0, one, plte)).Bytes);

        PngDecodeDataException error = Assert.Throws<PngDecodeDataException>(() => PngReader.Decode(png));

        Assert.Equal(reason, error.Reason);
        switch (reason)
        {
            case PngDecodeRefusal.PaletteMissing: Assert.Equal(3, error.ColorType); break;
            case PngDecodeRefusal.PaletteIndexOutsideRange:
                Assert.Equal(1, error.PixelIndex);
                Assert.Equal(4, error.Index);
                Assert.Equal(4, error.PaletteCount);
                break;
            case PngDecodeRefusal.PaletteTransparencyTooLong:
                Assert.Equal(5, error.AlphaCount);
                Assert.Equal(4, error.PaletteCount);
                break;
            case PngDecodeRefusal.PaletteTransparencyBeforePalette: Assert.Equal(33, error.Offset); break;
            case PngDecodeRefusal.PaletteLengthInvalid: Assert.Equal(4, error.ByteLength); break;
            case PngDecodeRefusal.PaletteExceedsIndexDepth:
                Assert.Equal(4, error.EntryCount);
                Assert.Equal(1, error.BitDepth);
                break;
        }
    }

    [Fact]
    public void Palette_bit_depths_outside_the_format_are_rejected()
    {
        byte[] plte = Chunk("PLTE", Palette);
        Assert.Equal(8, PngReader.Decode(Png(1, 1, 8, 3, 0, new byte[] { 0, 0 }, plte)).BitDepth);

        PngDecodeNotSupportedException error = Assert.Throws<PngDecodeNotSupportedException>(() =>
            PngReader.Decode(Png(1, 1, 16, 3, 0, new byte[] { 0, 0 }, plte)));

        Assert.IsAssignableFrom<NotSupportedException>(error);
        Assert.Equal(PngDecodeRefusal.BitDepthUnsupported, error.Reason);
        Assert.Equal(16, error.BitDepth);
        Assert.Equal(3, error.ColorType);
    }

    [Fact]
    public void Adam7_sixteen_bit_grey_matches_the_noninterlaced_decode_sample_for_sample()
    {
        var raster = new byte[9 * 9 * 2];
        for (int i = 0; i < raster.Length; i++) raster[i] = (byte)(i * 37 + 5);

        PngImage interlaced = PngReader.Decode(Png(9, 9, 16, 0, 1, Filtered(9, 9, 16, raster, interlaced: true)));
        PngImage flat = PngReader.Decode(Png(9, 9, 16, 0, 0, Filtered(9, 9, 16, raster, interlaced: false)));

        Assert.Equal((1, 16), (interlaced.Channels, interlaced.BitDepth));
        Assert.Equal(raster, interlaced.Bytes);
        Assert.Equal(flat.Bytes, interlaced.Bytes);
    }

    [Fact]
    public void Adam7_grey_transparency_and_tiny_images_decode()
    {
        byte[] raster = { 1, 9, 3, 9, 5, 6 };
        byte[] png = Png(3, 2, 8, 0, 1, Filtered(3, 2, 8, raster, interlaced: true), Chunk("tRNS", new byte[] { 0, 9 }));

        PngImage image = PngReader.Decode(png);
        PngImage single = PngReader.Decode(BuildPng(1, 1, 8, 0, 1, new byte[] { 0, 42 }));

        Assert.Equal(new byte[] { 1, 255, 9, 0, 3, 255, 9, 0, 5, 255, 6, 255 }, image.Bytes);
        Assert.Equal(new byte[] { 42 }, single.Bytes);
    }

    [Fact]
    public void Adam7_packed_palette_indices_land_in_raster_order()
    {
        var indices = new byte[10 * 10];
        for (int i = 0; i < indices.Length; i++) indices[i] = (byte)(i * 5 % 4);
        byte[] png = Png(10, 10, 2, 3, 1, Filtered(10, 10, 2, indices, interlaced: true), Chunk("PLTE", Palette));

        Assert.Equal(ExpandPalette(indices, alpha: null), PngReader.Decode(png).Bytes);
    }

    [Fact]
    public void Unknown_interlace_methods_and_oversized_palettes_are_rejected()
    {
        Assert.Equal(new byte[] { 0 }, PngReader.Decode(BuildPng(1, 1, 8, 0, 1, new byte[] { 0, 0 })).Bytes);
        PngDecodeNotSupportedException interlace = Assert.Throws<PngDecodeNotSupportedException>(() =>
            PngReader.Decode(BuildPng(1, 1, 8, 0, 2, new byte[] { 0, 0 })));
        Assert.Equal(PngDecodeRefusal.InterlaceUnsupported, interlace.Reason);
        Assert.Equal(2, interlace.InterlaceMethod);

        byte[] huge = Join(Signature, Chunk("IHDR", Header(20_000, 20_000, 1, 3, 0)), Chunk("IEND", Array.Empty<byte>()));
        PngDecodeDataException allocation = Assert.Throws<PngDecodeDataException>(() => PngReader.Decode(huge));
        Assert.Equal(PngDecodeRefusal.DecodedAllocationCapExceeded, allocation.Reason);
        Assert.Equal(400_000_000L, allocation.RasterBytes);
        Assert.Equal(1_600_000_000L, allocation.ExpandedBytes);
        Assert.Equal(50_020_000L, allocation.FilteredBytes);
        Assert.Equal(PngReader.MaxDecodedBytes, allocation.MaximumBytes);
    }

    internal static byte[] Png(int width, int height, int depth, int color, int interlace, byte[] filtered,
        params byte[][] beforeData)
    {
        byte[] ancillary = Join(beforeData);
        return Join(Signature, Chunk("IHDR", Header(width, height, (byte)depth, (byte)color, (byte)interlace)),
            ancillary, Chunk("IDAT", Compress(filtered)), Chunk("IEND", Array.Empty<byte>()));
    }

    // Filters every pass row with Up, which restarts at the first row of each pass. Below eight bits the raster
    // holds one index per pixel and rows are packed most-significant bit first. Otherwise it holds whole pixels.
    internal static byte[] Filtered(int width, int height, int bits, byte[] raster, bool interlaced)
    {
        int pixelBytes = Math.Max(1, bits / 8);
        using var output = new MemoryStream();
        foreach (var pass in interlaced ? Adam7 : new[] { (X: 0, Y: 0, Dx: 1, Dy: 1) })
        {
            int passWidth = width <= pass.X ? 0 : (width - pass.X + pass.Dx - 1) / pass.Dx;
            int passHeight = height <= pass.Y ? 0 : (height - pass.Y + pass.Dy - 1) / pass.Dy;
            if (passWidth == 0 || passHeight == 0) continue;
            int rowBytes = (passWidth * bits + 7) / 8;
            var above = new byte[rowBytes];
            for (int py = 0; py < passHeight; py++)
            {
                var row = new byte[rowBytes];
                for (int px = 0; px < passWidth; px++)
                {
                    int pixel = (pass.Y + py * pass.Dy) * width + pass.X + px * pass.Dx;
                    if (bits >= 8)
                    {
                        Array.Copy(raster, pixel * pixelBytes, row, px * pixelBytes, pixelBytes);
                        continue;
                    }
                    int bit = px * bits;
                    row[bit / 8] |= (byte)(raster[pixel] << (8 - bits - bit % 8));
                }
                output.WriteByte(2);
                for (int i = 0; i < rowBytes; i++) output.WriteByte(unchecked((byte)(row[i] - above[i])));
                above = row;
            }
        }
        return output.ToArray();
    }

    private static byte[] ExpandPalette(byte[] indices, byte[]? alpha)
    {
        int channels = alpha is null ? 3 : 4;
        var bytes = new byte[indices.Length * channels];
        for (int i = 0; i < indices.Length; i++)
        {
            Array.Copy(Palette, indices[i] * 3, bytes, i * channels, 3);
            if (alpha is not null) bytes[i * channels + 3] = indices[i] < alpha.Length ? alpha[indices[i]] : (byte)255;
        }
        return bytes;
    }
}
