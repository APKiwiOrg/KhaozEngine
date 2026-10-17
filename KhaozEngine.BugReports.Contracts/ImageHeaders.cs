using System;
using System.Buffers.Binary;

namespace KhaozEngine.BugReports.Contracts;

/// <summary>
/// Reads image dimensions from PNG and JPEG headers without decoding pixels. <see cref="BundleValidator"/> and the
/// bundle writer both read sizes through <see cref="TryReadSize"/>, so they agree on what a valid header is.
/// </summary>
public static class ImageHeaders
{
    public const string PngMediaType = "image/png";
    public const string JpegMediaType = "image/jpeg";

    static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    static ReadOnlySpan<byte> Ihdr => "IHDR"u8;

    /// <summary>
    /// Reads the pixel size from the header of a <c>image/png</c> or <c>image/jpeg</c> file. Returns false, with
    /// both sizes zero, for any other media type or a header that does not parse. Sizes are not range checked.
    /// </summary>
    public static bool TryReadSize(string mediaType, ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        switch (mediaType)
        {
            case PngMediaType:
                return TryReadPng(bytes, out width, out height);
            case JpegMediaType:
                return TryReadJpeg(bytes, out width, out height);
            default:
                width = height = 0;
                return false;
        }
    }

    static bool TryReadPng(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 24 || !data[..8].SequenceEqual(PngSignature)) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13 || !data.Slice(12, 4).SequenceEqual(Ihdr)) return false;
        uint w = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        if (w > int.MaxValue || h > int.MaxValue) return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    // Walks marker segments from SOI to the first start-of-frame marker (C0 to CF other than the
    // DHT, JPG and DAC markers C4, C8 and CC). Scan data or EOI before a frame header fails.
    static bool TryReadJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
        int p = 2;
        while (true)
        {
            if (p >= data.Length || data[p] != 0xFF) return false;
            while (p < data.Length && data[p] == 0xFF) p++;
            if (p >= data.Length) return false;
            byte marker = data[p++];
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue;
            if (marker is 0x00 or 0xD8 or 0xD9 or 0xDA) return false;
            if (2 > data.Length - p) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[p..]);
            if (length < 2 || length > data.Length - p) return false;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(data[(p + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(p + 5)..]);
                return true;
            }
            p += length;
        }
    }
}
