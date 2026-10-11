using System;
using System.IO;
using System.IO.Compression;

namespace PixelLabSheetAssembler.Tests;

/// <summary>
/// Builds PNG files in code for the layouts the engine's own writer never emits: indexed colour with an
/// optional tRNS chunk and Adam7 interlacing. Every sample is 8-bit except packed palette indices.
/// </summary>
internal static class ImageFixtures
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

    // Adam7 pass origin and step, in pass order: x, y, dx, dy.
    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7Passes =
    {
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    };

    /// <summary>
    /// An indexed-colour PNG. <paramref name="indices"/> holds one palette index per pixel and is packed
    /// most-significant bit first at <paramref name="bitDepth"/>. A non-null <paramref name="alpha"/> is written
    /// as tRNS and may be shorter than the palette.
    /// </summary>
    internal static byte[] Palette(int width, int height, int bitDepth, byte[] indices, byte[] rgb, byte[]? alpha)
    {
        int rowBytes = (width * bitDepth + 7) / 8;
        var filtered = new byte[height * (rowBytes + 1)];
        for (int y = 0; y < height; y++)
        {
            int row = y * (rowBytes + 1) + 1;
            for (int x = 0; x < width; x++)
            {
                int bit = x * bitDepth;
                int shift = 8 - bitDepth - bit % 8;
                filtered[row + bit / 8] |= (byte)(indices[y * width + x] << shift);
            }
        }

        byte[] transparency = alpha is null ? Array.Empty<byte>() : Chunk("tRNS", alpha);
        return Join(Signature, Chunk("IHDR", Header(width, height, bitDepth, 3, 0)), Chunk("PLTE", rgb),
            transparency, Chunk("IDAT", Compress(filtered)), Chunk("IEND", Array.Empty<byte>()));
    }

    /// <summary>
    /// An Adam7-interlaced 8-bit PNG of colour type 2 (RGB) or 6 (RGBA). Every pass row uses the Up filter, so
    /// a decoder that carries the previous row across a pass boundary reads wrong pixels.
    /// </summary>
    internal static byte[] Adam7(int width, int height, int colorType, byte[] pixels)
    {
        int channels = colorType == 6 ? 4 : 3;
        using var filtered = new MemoryStream();
        foreach (var pass in Adam7Passes)
        {
            int passWidth = width <= pass.X ? 0 : (width - pass.X + pass.Dx - 1) / pass.Dx;
            int passHeight = height <= pass.Y ? 0 : (height - pass.Y + pass.Dy - 1) / pass.Dy;
            if (passWidth == 0 || passHeight == 0) continue;
            var above = new byte[passWidth * channels];
            for (int py = 0; py < passHeight; py++)
            {
                var raw = new byte[passWidth * channels];
                for (int px = 0; px < passWidth; px++)
                {
                    int source = ((pass.Y + py * pass.Dy) * width + pass.X + px * pass.Dx) * channels;
                    Array.Copy(pixels, source, raw, px * channels, channels);
                }
                filtered.WriteByte(2);
                for (int i = 0; i < raw.Length; i++) filtered.WriteByte(unchecked((byte)(raw[i] - above[i])));
                above = raw;
            }
        }

        return Join(Signature, Chunk("IHDR", Header(width, height, 8, colorType, 1)),
            Chunk("IDAT", Compress(filtered.ToArray())), Chunk("IEND", Array.Empty<byte>()));
    }

    private static byte[] Header(int width, int height, int bitDepth, int colorType, int interlace)
    {
        var bytes = new byte[13];
        Write32(bytes, 0, (uint)width);
        Write32(bytes, 4, (uint)height);
        bytes[8] = (byte)bitDepth;
        bytes[9] = (byte)colorType;
        bytes[12] = (byte)interlace;
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
        var bytes = new byte[12 + data.Length];
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
