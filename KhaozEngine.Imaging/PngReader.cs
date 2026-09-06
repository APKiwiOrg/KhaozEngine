using System;
using System.IO;
using System.IO.Compression;

namespace KhaozEngine.Imaging;

/// <summary>Dependency-free decoder for noninterlaced 8-bit and 16-bit greyscale, GA, RGB and RGBA PNGs.</summary>
public static class PngReader
{
    /// <summary>Maximum decoded sample bytes and filtered payload accepted from one image.</summary>
    public const int MaxDecodedBytes = 256 * 1024 * 1024;

    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

    /// <summary>
    /// Decodes a complete PNG. Output samples are top-to-bottom in PNG channel order. A 16-bit sample remains
    /// two bytes, most-significant byte first.
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
        Header header = default;
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
                if (!sawHeader || sawData || sawEnd)
                    throw new InvalidDataException("PNG PLTE chunk is out of order");
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
        return DecodePayload(header, compressed.ToArray());
    }

    private static Header ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length != 13) throw new InvalidDataException("PNG IHDR must contain 13 bytes");
        uint width = Read32(data, 0);
        uint height = Read32(data, 4);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new InvalidDataException("PNG dimensions must be positive supported integers");

        int bitDepth = data[8];
        if (bitDepth is not (8 or 16))
            throw new NotSupportedException($"PNG bit depth {bitDepth} is not supported");
        int channels = data[9] switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            3 => throw new NotSupportedException("palette PNGs are not supported"),
            _ => throw new NotSupportedException($"PNG color type {data[9]} is not supported"),
        };
        if (data[10] != 0 || data[11] != 0)
            throw new NotSupportedException("unsupported PNG compression or filter method");
        if (data[12] != 0) throw new NotSupportedException("interlaced PNGs are not supported");

        long stride = (long)width * channels * (bitDepth / 8);
        long decoded = stride * height;
        long filtered = decoded + height;
        if (stride > int.MaxValue || decoded > MaxDecodedBytes || filtered > MaxDecodedBytes)
            throw new InvalidDataException($"PNG decoded payload exceeds the {MaxDecodedBytes}-byte allocation cap");
        return new Header((int)width, (int)height, channels, bitDepth, (int)stride, (int)filtered);
    }

    private static PngImage DecodePayload(Header header, byte[] compressed)
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

        byte[] decoded = PngFilters.Unfilter(
            filtered, header.Stride, header.Height, header.Channels * (header.BitDepth / 8));
        return new PngImage(header.Width, header.Height, header.Channels, header.BitDepth, decoded);
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
        int Width, int Height, int Channels, int BitDepth, int Stride, int FilteredLength);
}
