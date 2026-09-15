using System;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// Unsigned LEB128 with the zig-zag transform for declared-signed fields, per contracts 15. Seven value
/// bits per byte, low group first, the high bit set on every byte but the last, minimal encodings only.
/// Every reader here is total: a malformed varint returns false rather than throwing.
/// </summary>
public static class ContentVarint
{
    public static int Write(Span<byte> destination, uint value)
    {
        int written = 0;
        while (value >= 0x80)
        {
            destination[written++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[written++] = (byte)value;
        return written;
    }

    public static int WriteSigned(Span<byte> destination, int value) =>
        Write(destination, ZigZag(value));

    public static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));

    public static int UnZigZag(uint value) => (int)(value >> 1) ^ -(int)(value & 1);

    public static int Size(uint value)
    {
        int size = 1;
        while (value >= 0x80) { value >>= 7; size++; }
        return size;
    }

    public static int SizeSigned(int value) => Size(ZigZag(value));

    /// <summary>
    /// Reads one varint. Returns false on a truncated, non-terminating, over-long or non-minimal
    /// encoding, which are the decode failures contracts 15 names.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, ref int offset, out uint value)
    {
        value = 0;
        int shift = 0;
        int start = offset;
        while (true)
        {
            if (offset >= source.Length) { offset = start; return false; }
            if (shift > 28) { offset = start; return false; }
            byte b = source[offset++];
            if (shift == 28 && (b & 0xF0) != 0) { offset = start; return false; }
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                // Minimal encoding: a continuation byte may not be followed by a zero group.
                if (b == 0 && offset - start > 1) { offset = start; return false; }
                return true;
            }
            shift += 7;
        }
    }

    public static bool TryReadSigned(ReadOnlySpan<byte> source, ref int offset, out int value)
    {
        if (!TryRead(source, ref offset, out uint raw)) { value = 0; return false; }
        value = UnZigZag(raw);
        return true;
    }
}
