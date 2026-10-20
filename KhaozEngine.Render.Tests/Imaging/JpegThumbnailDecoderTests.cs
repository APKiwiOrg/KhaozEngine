using System;
using System.IO;
using System.Runtime.CompilerServices;
using KhaozEngine.Imaging;
using Xunit;

namespace KhaozEngine.Tests.Imaging;

public class JpegThumbnailDecoderTests
{
    [Fact]
    public void Solid_colour_decodes_within_tolerance()
    {
        JpegThumbnail image = JpegThumbnailDecoder.DecodeEighthScale(Encode(64, 48, (x, y) => (200, 40, 90)));
        Assert.Equal((8, 6), (image.Width, image.Height));
        AssertPixels(image, (x, y) => (200, 40, 90), 8);
    }

    [Fact]
    public void Halves_keep_their_colours()
    {
        JpegThumbnail image = JpegThumbnailDecoder.DecodeEighthScale(Encode(64, 64, ColourHalves));
        AssertColourHalves(image);
    }

    [Fact]
    public void Odd_sizes_round_up()
    {
        JpegThumbnail image = JpegThumbnailDecoder.DecodeEighthScale(Encode(61, 37, (x, y) => (200, 40, 90)));
        Assert.Equal((8, 5), (image.Width, image.Height));
        AssertPixels(image, (x, y) => (200, 40, 90), 8);
    }

    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(7, 9, 1, 2)]
    [InlineData(17, 15, 3, 2)]
    [InlineData(16384, 1, 2048, 1)]
    [InlineData(1, 16384, 1, 2048)]
    public void Partial_MCUs_keep_their_pixels(int width, int height, int columns, int rows)
    {
        JpegThumbnail image = JpegThumbnailDecoder.DecodeEighthScale(Encode(width, height, (x, y) => (40, 120, 200)));
        Assert.Equal((columns, rows), (image.Width, image.Height));
        AssertPixels(image, (x, y) => (40, 120, 200), 8);
    }

    [Fact]
    public void AC_coefficients_are_consumed_without_changing_block_averages()
    {
        byte[] jpeg = Encode(64, 48, (x, y) =>
        {
            int average = 40 + (x / 8 + y / 8 * 8) % 6 * 32;
            byte value = (byte)(average + ((x + y) % 2 == 0 ? -24 : 24));
            return (value, value, value);
        });
        AssertPixels(JpegThumbnailDecoder.DecodeEighthScale(jpeg), (x, y) =>
        {
            byte value = (byte)(40 + (x + y * 8) % 6 * 32);
            return (value, value, value);
        }, 4);
    }

    [Fact]
    public void Progressive_is_not_supported() => Assert.Throws<NotSupportedException>(() =>
        JpegThumbnailDecoder.DecodeEighthScale(new byte[] { 0xFF, 0xD8, 0xFF, 0xC2 }));

    [Fact]
    public void Truncated_input_is_invalid_data()
    {
        byte[] jpeg = Encode(64, 48, ColourHalves);
        Assert.Throws<InvalidDataException>(() => JpegThumbnailDecoder.DecodeEighthScale(jpeg.AsSpan(0, jpeg.Length / 2)));
        Assert.Throws<InvalidDataException>(() => JpegThumbnailDecoder.DecodeEighthScale(jpeg.AsSpan(0, jpeg.Length - 2)));
    }

    [Fact]
    public void Oversized_dimensions_are_invalid_data()
    {
        byte[] jpeg = Encode(64, 48, ColourHalves);
        jpeg.AsSpan(Marker(jpeg, 0xC0) + 5, 4).Fill(255);
        Assert.Throws<InvalidDataException>(() => JpegThumbnailDecoder.DecodeEighthScale(jpeg));
    }

    [Theory]
    [InlineData(0, 48)]
    [InlineData(64, 0)]
    [InlineData(16385, 48)]
    [InlineData(64, 16385)]
    public void Invalid_edges_are_rejected(int width, int height)
    {
        byte[] jpeg = Encode(64, 48, ColourHalves);
        int p = Marker(jpeg, 0xC0);
        jpeg[p + 5] = (byte)(height >> 8);
        jpeg[p + 6] = (byte)height;
        jpeg[p + 7] = (byte)(width >> 8);
        jpeg[p + 8] = (byte)width;
        Assert.Throws<InvalidDataException>(() => JpegThumbnailDecoder.DecodeEighthScale(jpeg));
    }

    [Fact]
    public void Eight_bit_SOF1_is_supported()
    {
        byte[] jpeg = Encode(64, 64, ColourHalves);
        jpeg[Marker(jpeg, 0xC0) + 1] = 0xC1;
        AssertColourHalves(JpegThumbnailDecoder.DecodeEighthScale(jpeg));
    }

    [Theory]
    [InlineData(0xC9)]
    [InlineData(0xCA)]
    [InlineData(0xC3)]
    public void Other_coding_processes_are_not_supported(byte marker) => Assert.Throws<NotSupportedException>(() =>
        JpegThumbnailDecoder.DecodeEighthScale(new byte[] { 0xFF, 0xD8, 0xFF, marker }));

    [Fact]
    public void Twelve_bit_samples_are_not_supported()
    {
        byte[] jpeg = Encode(64, 48, ColourHalves);
        jpeg[Marker(jpeg, 0xC0) + 4] = 12;
        Assert.Throws<NotSupportedException>(() => JpegThumbnailDecoder.DecodeEighthScale(jpeg));
    }

    [Fact]
    public void Grayscale_decodes_its_halves()
    {
        JpegThumbnail image = JpegThumbnailDecoder.DecodeEighthScale(Fixture("grayscale-halves.jpg"));
        Assert.Equal((8, 6), (image.Width, image.Height));
        AssertPixels(image, (x, y) => x < 4 ? ((byte)32, (byte)32, (byte)32) : ((byte)224, (byte)224, (byte)224), 4);
    }

    [Fact]
    public void Full_chroma_decodes_its_halves() =>
        AssertColourHalves(JpegThumbnailDecoder.DecodeEighthScale(Fixture("full-chroma-halves.jpg")));

    [Fact]
    public void Incorrect_restart_sequence_is_invalid_data()
    {
        byte[] jpeg = Fixture("full-chroma-halves.jpg");
        jpeg[Marker(jpeg, 0xD0) + 1] = 0xD1;
        Assert.Throws<InvalidDataException>(() => JpegThumbnailDecoder.DecodeEighthScale(jpeg));
    }

    [Fact]
    public void Missing_component_scans_are_invalid_data()
    {
        byte[] jpeg = Fixture("full-chroma-halves.jpg");
        int first = Marker(jpeg, 0xDA);
        for (int p = first + 2; p < jpeg.Length - 1; p++)
            if (jpeg[p] == 255 && jpeg[p + 1] == 0xDA)
            {
                jpeg[p + 1] = 0xD9;
                Assert.Throws<InvalidDataException>(() => JpegThumbnailDecoder.DecodeEighthScale(jpeg));
                return;
            }
        throw new InvalidOperationException("Fixture has no second component scan.");
    }

    // The writer's interleaved 4:2:0 file plus the two fixtures, so the restart, single-component and
    // non-interleaved scan paths are mutated too.
    [Theory]
    [InlineData(null)]
    [InlineData("grayscale-halves.jpg")]
    [InlineData("full-chroma-halves.jpg")]
    public void Mutated_inputs_only_throw_documented_exceptions(string? fixture)
    {
        byte[] valid = fixture is null ? Encode(64, 48, ColourHalves) : Fixture(fixture);
        var random = new Random(11017);
        for (int i = 0; i < 2000; i++)
        {
            byte[] mutated = (byte[])valid.Clone();
            mutated[random.Next(mutated.Length)] ^= (byte)random.Next(1, 256);
            AssertDocumentedOutcome(mutated);
        }
        for (int length = 0; length < valid.Length; length += 7)
            AssertDocumentedOutcome(valid.AsSpan(0, length));
    }

    [Theory]
    [InlineData(0xDB, 0, 0xF0)]
    [InlineData(0xC4, 0, 0xF0)]
    [InlineData(0xC0, 7, 0)]
    [InlineData(0xDA, 2, 0xFF)]
    public void Malformed_tables_and_selectors_are_invalid_data(byte marker, int offset, byte value)
    {
        byte[] jpeg = Encode(64, 48, ColourHalves);
        jpeg[Marker(jpeg, marker) + 4 + offset] = value;
        Assert.Throws<InvalidDataException>(() => JpegThumbnailDecoder.DecodeEighthScale(jpeg));
    }

    [Fact]
    public void Marker_fill_bytes_terminate_and_valid_fill_is_accepted()
    {
        byte[] valid = Encode(64, 48, ColourHalves);
        byte[] filled = new byte[valid.Length + 64];
        valid.AsSpan(0, 2).CopyTo(filled);
        filled.AsSpan(2, 64).Fill(255);
        valid.AsSpan(2).CopyTo(filled.AsSpan(66));
        AssertPixels(JpegThumbnailDecoder.DecodeEighthScale(filled), (x, y) => x < 4 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255), 8);
        Assert.Throws<InvalidDataException>(() => JpegThumbnailDecoder.DecodeEighthScale(filled.AsSpan(0, 66)));
    }

    private static void AssertDocumentedOutcome(ReadOnlySpan<byte> jpeg)
    {
        try { JpegThumbnailDecoder.DecodeEighthScale(jpeg); }
        catch (InvalidDataException) { }
        catch (NotSupportedException) { }
    }

    private static void AssertColourHalves(JpegThumbnail image)
    {
        Assert.Equal((8, 8), (image.Width, image.Height));
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int p = (y * 8 + x) * 4;
                Assert.True(image.Rgba[p + (x < 4 ? 0 : 2)] > 200);
                Assert.True(image.Rgba[p + (x < 4 ? 2 : 0)] < 60);
                Assert.Equal(255, image.Rgba[p + 3]);
            }
    }

    private static void AssertPixels(JpegThumbnail image, Func<int, int, (byte R, byte G, byte B)> expected, int tolerance)
    {
        Assert.Equal(image.Width * image.Height * 4, image.Rgba.Length);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var (r, g, b) = expected(x, y);
                int p = (y * image.Width + x) * 4;
                Assert.InRange((int)image.Rgba[p], r - tolerance, r + tolerance);
                Assert.InRange((int)image.Rgba[p + 1], g - tolerance, g + tolerance);
                Assert.InRange((int)image.Rgba[p + 2], b - tolerance, b + tolerance);
                Assert.Equal(255, image.Rgba[p + 3]);
            }
    }

    private static (byte, byte, byte) ColourHalves(int x, int y) => x < 32 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255);

    private static byte[] Encode(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                int p = (y * width + x) * 4;
                rgba[p] = r;
                rgba[p + 1] = g;
                rgba[p + 2] = b;
                rgba[p + 3] = 255;
            }
        return JpegWriter.Encode(rgba, width, height, 90);
    }

    private static int Marker(byte[] jpeg, byte marker)
    {
        for (int p = 0; p < jpeg.Length - 1; p++)
            if (jpeg[p] == 255 && jpeg[p + 1] == marker) return p;
        throw new InvalidOperationException($"Fixture has no marker {marker:X2}.");
    }

    private static byte[] Fixture(string name, [CallerFilePath] string source = "") =>
        File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "JpegThumbnail", name));
}
