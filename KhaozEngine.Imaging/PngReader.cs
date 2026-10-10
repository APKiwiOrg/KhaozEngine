using System;
using System.IO;
using System.IO.Compression;

namespace KhaozEngine.Imaging;

/// <summary>
/// Dependency-free decoder for every PNG colour type and bit depth, noninterlaced or Adam7-interlaced. Palette and
/// greyscale images below 8 bits decode to 8-bit samples. All other samples keep their stored depth.
/// </summary>
public static class PngReader
{
    /// <summary>Maximum filtered, decoded, or transparency-expanded sample bytes accepted from one image.</summary>
    public const int MaxDecodedBytes = 256 * 1024 * 1024;

    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

    /// <summary>
    /// Decodes a complete PNG. Output samples are top-to-bottom in PNG channel order, deinterlaced when the image
    /// is Adam7. Greyscale and RGB transparency chunks add an alpha channel. A palette image expands to 8-bit RGB,
    /// or to RGBA when it carries a tRNS chunk. Greyscale at 1, 2 or 4 bits scales to 8 bits as
    /// <c>value * 255 / (2^depth - 1)</c>. A 16-bit sample remains two bytes, most-significant byte first.
    /// </summary>
    public static PngImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
            throw new InvalidDataException("invalid PNG signature");

        int offset = Signature.Length;
        bool sawHeader = false;
        bool sawData = false;
        bool dataEnded = false;
        bool sawEnd = false;
        bool sawPalette = false;
        bool sawTransparency = false;
        Header header = default;
        PngPalette? palette = null;
        PngTransparency? transparency = null;
        using var compressed = new MemoryStream();

        while (offset < png.Length)
        {
            if (png.Length - offset < 12) throw new InvalidDataException("truncated PNG chunk");
            uint unsignedLength = Read32(png, offset);
            if (unsignedLength > int.MaxValue) throw new InvalidDataException("PNG chunk is too large");
            int length = (int)unsignedLength;
            long end = (long)offset + 12 + length;
            if (end > png.Length) throw new InvalidDataException("truncated PNG chunk data");

            ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = png.Slice(offset + 8, length);
            uint expectedCrc = Read32(png, offset + 8 + length);
            if (PngCrc.Compute(type, data) != expectedCrc)
                throw new InvalidDataException("PNG chunk CRC mismatch");

            if (sawData && !IsType(type, "IDAT")) dataEnded = true;
            if (IsType(type, "IHDR"))
            {
                if (sawHeader || offset != Signature.Length)
                    throw new InvalidDataException("PNG IHDR must be the first and only header chunk");
                header = ParseHeader(data);
                sawHeader = true;
            }
            else if (IsType(type, "IDAT"))
            {
                if (!sawHeader || dataEnded || sawEnd)
                    throw new InvalidDataException("PNG IDAT chunks are out of order");
                if ((long)compressed.Length + data.Length > MaxDecodedBytes)
                    throw new InvalidDataException("PNG compressed payload exceeds the allocation cap");
                compressed.Write(data);
                sawData = true;
            }
            else if (IsType(type, "IEND"))
            {
                if (!sawHeader || !sawData || sawEnd || data.Length != 0)
                    throw new InvalidDataException("invalid PNG IEND chunk");
                sawEnd = true;
            }
            else if (IsType(type, "PLTE"))
            {
                if (!sawHeader || sawPalette || sawTransparency || sawData || sawEnd)
                    throw new InvalidDataException("PNG PLTE chunk is out of order");
                sawPalette = true;
                if (header.ColorType == 3) palette = PngPalette.Parse(data, header.BitDepth);
            }
            else if (IsType(type, "tRNS"))
            {
                if (!sawHeader || sawTransparency || sawData || sawEnd)
                    throw new InvalidDataException("PNG tRNS chunk is out of order");
                sawTransparency = true;
                if (header.ColorType == 3)
                {
                    if (palette is null) throw new InvalidDataException("PNG tRNS must follow PLTE in a palette image");
                    palette.SetTransparency(data);
                }
                else
                {
                    transparency = PngTransparency.Parse(
                        header.ColorType, header.BitDepth, header.Width, header.Height, data);
                }
            }
            else if ((type[0] & 0x20) == 0 && !IsType(type, "PLTE"))
            {
                throw new NotSupportedException("unsupported critical PNG chunk " + TypeName(type));
            }

            offset = (int)end;
            if (sawEnd)
            {
                if (offset != png.Length) throw new InvalidDataException("PNG contains data after IEND");
                break;
            }
        }

        if (!sawEnd) throw new InvalidDataException("PNG is missing IEND");
        if (header.ColorType == 3 && palette is null) throw new InvalidDataException("PNG palette image is missing PLTE");
        return DecodePayload(header, palette, transparency, compressed.ToArray());
    }

    private static Header ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length != 13) throw new InvalidDataException("PNG IHDR must contain 13 bytes");
        uint width = Read32(data, 0);
        uint height = Read32(data, 4);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new InvalidDataException("PNG dimensions must be positive supported integers");

        int bitDepth = data[8];
        int colorType = data[9];
        int channels = colorType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException($"PNG color type {colorType} is not supported"),
        };
        bool supportedDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!supportedDepth)
            throw new NotSupportedException($"PNG bit depth {bitDepth} is not supported for color type {colorType}");
        if (data[10] != 0 || data[11] != 0)
            throw new NotSupportedException("unsupported PNG compression or filter method");
        if (data[12] > 1) throw new NotSupportedException($"PNG interlace method {data[12]} is not supported");
        bool interlaced = data[12] == 1;

        // A palette expands to at most four 8-bit samples per pixel.
        int bitsPerPixel = channels * bitDepth;
        long pixels = (long)width * height;
        long raster = pixels * Math.Max(1, bitsPerPixel / 8);
        long expanded = colorType == 3 ? pixels * 4 : raster;
        long filtered = PngInterlace.FilteredLength((int)width, (int)height, bitsPerPixel, interlaced);
        if (raster > MaxDecodedBytes || expanded > MaxDecodedBytes || filtered > MaxDecodedBytes)
            throw new InvalidDataException($"PNG decoded payload exceeds the {MaxDecodedBytes}-byte allocation cap");
        return new Header((int)width, (int)height, colorType, channels, bitDepth, interlaced, (int)filtered);
    }

    private static PngImage DecodePayload(
        Header header, PngPalette? palette, PngTransparency? transparency, byte[] compressed)
    {
        var filtered = new byte[header.FilteredLength];
        using var input = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        int read = 0;
        while (read < filtered.Length)
        {
            int count = zlib.Read(filtered, read, filtered.Length - read);
            if (count == 0) break;
            read += count;
        }
        if (read != filtered.Length || zlib.ReadByte() != -1)
            throw new InvalidDataException("PNG decoded payload size does not match IHDR");

        byte[] decoded = PngInterlace.Reconstruct(
            filtered, header.Width, header.Height, header.Channels * header.BitDepth, header.Interlaced);
        if (palette is not null)
            return new PngImage(header.Width, header.Height, palette.OutputChannels, 8, palette.Expand(decoded));
        if (header.BitDepth < 8)
            return PngLowDepthGrey.Expand(decoded, header.Width, header.Height, header.BitDepth, transparency);
        if (transparency is not { } transparent)
            return new PngImage(header.Width, header.Height, header.Channels, header.BitDepth, decoded);

        byte[] expanded = transparent.Expand(decoded, header.BitDepth);
        return new PngImage(header.Width, header.Height, transparent.OutputChannels, header.BitDepth, expanded);
    }

    private static bool IsType(ReadOnlySpan<byte> type, string expected) =>
        type[0] == expected[0] && type[1] == expected[1] && type[2] == expected[2] && type[3] == expected[3];

    private static string TypeName(ReadOnlySpan<byte> type) =>
        string.Create(4, type.ToArray(), static (chars, bytes) =>
        {
            for (int i = 0; i < 4; i++) chars[i] = (char)bytes[i];
        });

    private static uint Read32(ReadOnlySpan<byte> bytes, int offset) =>
        ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
        ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

    private readonly record struct Header(
        int Width, int Height, int ColorType, int Channels, int BitDepth, bool Interlaced, int FilteredLength);
}
