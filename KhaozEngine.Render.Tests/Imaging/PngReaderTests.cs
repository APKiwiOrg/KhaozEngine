using System;
using System.IO;
using System.IO.Compression;
using KhaozEngine.Imaging;
using Xunit;

namespace KhaozEngine.Tests.Imaging;

public class PngReaderTests
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

    [Fact]
    public void Writer_output_roundtrips_byte_for_byte()
    {
        byte[] rgba = { 1, 2, 3, 4, 250, 240, 230, 220 };

        PngImage image = PngReader.Decode(PngWriter.Encode(rgba, 2, 1));

        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(4, image.Channels);
        Assert.Equal(8, image.BitDepth);
        Assert.Equal(rgba, image.Bytes);
    }

    [Fact]
    public void Independent_sixteen_bit_greyscale_fixture_preserves_network_byte_order()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACEAAAAAAHTY67AAAAEklEQVR4nGNgYBQyYVh99v9/AAnYA77rxuqwAAAAAElFTkSuQmCC");

        PngImage image = PngReader.Decode(png);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(1, image.Channels);
        Assert.Equal(16, image.BitDepth);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x12, 0x34, 0xab, 0xcd, 0xff, 0xff }, image.Bytes);
    }

    [Fact]
    public void Independent_sixteen_bit_rgb_fixture_uses_full_pixel_distance_for_sub_and_paeth()
    {
        // Generated with a standalone PNG encoder. Row 1 uses Sub and row 2 uses Paeth. The six-byte RGB16
        // pixel distance is load-bearing because a decoder using one byte or three channels reconstructs different
        // nonzero samples in both rows.
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACEAIAAACtREYwAAAAIklEQVR42mNkZBJQMHBgYeVg4eRm4eASEhCVYmHn4OEVBQAUfgF21mnYeQAAAABJRU5ErkJggg==");

        PngImage image = PngReader.Decode(png);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(3, image.Channels);
        Assert.Equal(16, image.BitDepth);
        Assert.Equal(new byte[]
        {
            0x01, 0x02, 0x10, 0x20, 0x30, 0x40, 0x05, 0x07, 0x18, 0x24, 0x39, 0x4b,
            0x09, 0x0c, 0x22, 0x30, 0x45, 0x5a, 0x0d, 0x13, 0x2a, 0x3c, 0x52, 0x6f,
        }, image.Bytes);
    }

    [Theory]
    [InlineData(0, 10, 20, 0, 30, 50)]
    [InlineData(1, 10, 10, 1, 30, 20)]
    [InlineData(2, 10, 20, 2, 20, 30)]
    [InlineData(3, 10, 15, 3, 25, 25)]
    [InlineData(4, 10, 10, 4, 20, 20)]
    public void Every_filter_reconstructs_the_same_pixels(
        byte firstFilter, byte firstA, byte firstB, byte secondFilter, byte secondA, byte secondB)
    {
        byte[] filtered = { firstFilter, firstA, firstB, secondFilter, secondA, secondB };

        PngImage image = PngReader.Decode(BuildPng(2, 2, 8, 0, 0, filtered));

        Assert.Equal(new byte[] { 10, 20, 30, 50 }, image.Bytes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 2)]
    [InlineData(2, 3)]
    [InlineData(6, 4)]
    public void Supported_color_types_report_their_channel_count(byte colorType, int channels)
    {
        byte[] row = new byte[1 + channels];

        PngImage image = PngReader.Decode(BuildPng(1, 1, 8, colorType, 0, row));

        Assert.Equal(channels, image.Channels);
    }

    [Fact]
    public void Bad_crc_and_truncated_chunks_are_rejected()
    {
        byte[] png = BuildPng(1, 1, 8, 0, 0, new byte[] { 0, 42 });
        png[29] ^= 1;

        Assert.Throws<InvalidDataException>(() => PngReader.Decode(png));
        Assert.Throws<InvalidDataException>(() => PngReader.Decode(png.AsSpan(0, png.Length - 3)));
    }

    [Fact]
    public void Invalid_chunk_order_and_wrong_payload_size_are_rejected()
    {
        byte[] ihdr = Header(1, 1, 8, 0, 0);
        byte[] tooShort = BuildPng(1, 1, 8, 0, 0, new byte[] { 0 });
        byte[] tooLong = BuildPng(1, 1, 8, 0, 0, new byte[] { 0, 1, 2 });
        byte[] wrongOrder = Join(Signature, Chunk("IDAT", Compress(new byte[] { 0, 1 })), Chunk("IHDR", ihdr), Chunk("IEND", Array.Empty<byte>()));

        Assert.Throws<InvalidDataException>(() => PngReader.Decode(tooShort));
        Assert.Throws<InvalidDataException>(() => PngReader.Decode(tooLong));
        Assert.Throws<InvalidDataException>(() => PngReader.Decode(wrongOrder));
    }

    [Fact]
    public void Palette_interlace_and_oversized_decodes_are_rejected_before_allocation()
    {
        Assert.Throws<NotSupportedException>(() => PngReader.Decode(BuildPng(1, 1, 8, 3, 0, new byte[] { 0, 0 })));
        Assert.Throws<NotSupportedException>(() => PngReader.Decode(BuildPng(1, 1, 8, 0, 1, new byte[] { 0, 0 })));
        byte[] huge = Join(Signature, Chunk("IHDR", Header(100_000, 100_000, 16, 6, 0)), Chunk("IEND", Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => PngReader.Decode(huge));
    }

    private static byte[] BuildPng(int width, int height, byte depth, byte color, byte interlace, byte[] filtered) =>
        Join(Signature, Chunk("IHDR", Header(width, height, depth, color, interlace)),
            Chunk("IDAT", Compress(filtered)), Chunk("IEND", Array.Empty<byte>()));

    private static byte[] Header(int width, int height, byte depth, byte color, byte interlace)
    {
        var bytes = new byte[13];
        Write32(bytes, 0, (uint)width);
        Write32(bytes, 4, (uint)height);
        bytes[8] = depth;
        bytes[9] = color;
        bytes[12] = interlace;
        return bytes;
    }

    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(raw);
        return output.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] bytes = new byte[12 + data.Length];
        Write32(bytes, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) bytes[4 + i] = (byte)type[i];
        data.CopyTo(bytes, 8);
        Write32(bytes, 8 + data.Length, Crc(bytes.AsSpan(4, 4 + data.Length)));
        return bytes;
    }

    private static byte[] Join(params byte[][] parts)
    {
        using var output = new MemoryStream();
        foreach (byte[] part in parts) output.Write(part);
        return output.ToArray();
    }

    private static void Write32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xffffffff;
    }
}
