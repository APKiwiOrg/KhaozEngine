using System;
using System.Buffers.Binary;

namespace KhaozEngine.Render2D;

public enum ImageDecodeStatus { Invalid, Unsupported, CapacityExceeded }
public sealed class ImageDecodeRefusal(ImageDecodeStatus status, string detail) : Exception(detail)
{
    public ImageDecodeStatus Status { get; } = status;
}

public readonly record struct ImageDecodeInfo(int Width, int Height, int Channels, int EncodedBytes);

/// <summary>Reads supported image headers without entering the pixel decoder.</summary>
public static class ImageDecodeAdmission
{
    [ThreadStatic] static int _pixelDecodeEntries;
    internal static int PixelDecodeEntries => _pixelDecodeEntries;
    internal static void EnterPixelDecode() => _pixelDecodeEntries++;

    public static ImageDecodeInfo Probe(ReadOnlySpan<byte> encoded)
    {
        int width, height, channels;
        if (encoded.Length >= 33 && encoded[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(encoded[8..]) != 13 ||
                !encoded.Slice(12, 4).SequenceEqual("IHDR"u8)) throw Invalid("PNG has no complete IHDR.");
            width = BinaryPrimitives.ReadInt32BigEndian(encoded[16..]);
            height = BinaryPrimitives.ReadInt32BigEndian(encoded[20..]);
            channels = encoded[25] switch { 0 => 1, 2 => 3, 3 => 4, 4 => 2, 6 => 4, _ => 0 };
            if (channels == 0 || encoded[24] is not (1 or 2 or 4 or 8 or 16) || encoded[26] != 0 || encoded[27] != 0 || encoded[28] > 1)
                throw Invalid("PNG header is invalid.");
        }
        else if (encoded.Length >= 4 && encoded[0] == 255 && encoded[1] == 216)
        {
            int offset = 2;
            while (true)
            {
                if (offset >= encoded.Length || encoded[offset++] != 255) throw Invalid("JPEG marker is incomplete.");
                while (offset < encoded.Length && encoded[offset] == 255) offset++;
                if (offset >= encoded.Length) throw Invalid("JPEG marker is incomplete.");
                int marker = encoded[offset++];
                if (marker is 0xd8 or 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
                if (marker is 0xd9 or 0xda || offset + 2 > encoded.Length) throw Invalid("JPEG has no frame header.");
                int length = BinaryPrimitives.ReadUInt16BigEndian(encoded[offset..]);
                if (length < 2 || length > encoded.Length - offset) throw Invalid("JPEG segment is incomplete.");
                if (marker is 0xc0 or 0xc1 or 0xc2)
                {
                    if (length < 8) throw Invalid("JPEG frame header is incomplete.");
                    height = BinaryPrimitives.ReadUInt16BigEndian(encoded[(offset + 3)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(encoded[(offset + 5)..]);
                    channels = encoded[offset + 7];
                    if (channels is not (1 or 3 or 4)) throw Invalid("JPEG component count is invalid.");
                    break;
                }
                offset += length;
            }
        }
        else throw new ImageDecodeRefusal(ImageDecodeStatus.Unsupported, "Bounded image decoding requires a PNG or JPEG header.");
        if (width <= 0 || height <= 0) throw Invalid("Image dimensions must be positive signed integers.");
        return new(width, height, channels, encoded.Length);
    }

    internal static long Demand(ImageDecodeInfo info) => checked(256L + 2L * info.EncodedBytes + 12L * info.Width * info.Height);
    static ImageDecodeRefusal Invalid(string detail) => new(ImageDecodeStatus.Invalid, detail);
}
