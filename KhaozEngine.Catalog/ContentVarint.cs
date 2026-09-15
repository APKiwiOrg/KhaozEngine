using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// Unsigned LEB128 with the zig-zag transform for declared-signed fields: seven value bits per byte, low
/// group first, the high bit set on every byte but the last, at most five bytes for a 32 bit value and ten
/// for a 64 bit one, MINIMAL encodings only. This is the one varint definition in the tree, so the pack
/// chunk, the manifest, the remap rule and the instance payload all write the same bytes.
/// <para>
/// Content ids, chunk indices, kind ids, lengths, counts and row counts are declared UNSIGNED and are never
/// zig-zagged, so a small id costs one byte. A field declared SIGNED is zig-zagged first, which maps 0 to
/// 0, -1 to 1 and 1 to 2, so a small negative number does not cost the full width.
/// </para>
/// <para>
/// Every reader here is TOTAL. The bytes come from a remote peer, so a malformed varint returns false with
/// a stable reason token rather than throwing, and leaves the offset exactly where it found it so the
/// caller can report the position that failed.
/// </para>
/// </summary>
public static class ContentVarint
{
    /// <summary>A varint longer than its value needs, which would give one value two byte forms.</summary>
    public const string ReasonNotMinimal = "varint-not-minimal";

    /// <summary>A varint that does not terminate within its width, or whose last byte carries value bits above it.</summary>
    public const string ReasonOverflow = "varint-overflow";

    /// <summary>A varint whose bytes run past the end of the span.</summary>
    public const string ReasonTruncated = "field-truncated";

    /// <summary>Writes an unsigned 32 bit value. Returns the bytes written, at most five.</summary>
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

    /// <summary>Writes an unsigned 64 bit value. Returns the bytes written, at most ten.</summary>
    public static int WriteUInt64(Span<byte> destination, ulong value)
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

    /// <summary>Writes a declared-signed 32 bit value, zig-zagged first.</summary>
    public static int WriteSigned(Span<byte> destination, int value) => Write(destination, ZigZag(value));

    /// <summary>Writes a declared-signed 64 bit value, zig-zagged first.</summary>
    public static int WriteSigned64(Span<byte> destination, long value) => WriteUInt64(destination, ZigZag64(value));

    /// <summary>The 32 bit zig-zag transform, <c>(n &lt;&lt; 1) ^ (n &gt;&gt; 31)</c>.</summary>
    public static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));

    /// <summary>The inverse of <see cref="ZigZag(int)"/>.</summary>
    public static int UnZigZag(uint value) => (int)(value >> 1) ^ -(int)(value & 1);

    /// <summary>The 64 bit zig-zag transform, <c>(n &lt;&lt; 1) ^ (n &gt;&gt; 63)</c>.</summary>
    public static ulong ZigZag64(long value) => (ulong)((value << 1) ^ (value >> 63));

    /// <summary>The inverse of <see cref="ZigZag64(long)"/>.</summary>
    public static long UnZigZag64(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>The bytes <see cref="Write"/> would take, so a caller can size a buffer without writing.</summary>
    public static int Size(uint value)
    {
        int size = 1;
        while (value >= 0x80) { value >>= 7; size++; }
        return size;
    }

    /// <summary>The bytes <see cref="WriteUInt64"/> would take.</summary>
    public static int SizeUInt64(ulong value)
    {
        int size = 1;
        while (value >= 0x80) { value >>= 7; size++; }
        return size;
    }

    /// <summary>The bytes <see cref="WriteSigned"/> would take.</summary>
    public static int SizeSigned(int value) => Size(ZigZag(value));

    /// <summary>The bytes <see cref="WriteSigned64"/> would take.</summary>
    public static int SizeSigned64(long value) => SizeUInt64(ZigZag64(value));

    /// <summary>
    /// Reads one unsigned 32 bit varint and advances <paramref name="offset"/> past it. Returns false with
    /// a reason on a truncated, non-terminating, over-wide or non-minimal encoding, leaving the offset
    /// unmoved.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, ref int offset, out uint value, out string? reason)
    {
        value = 0;
        reason = null;
        int shift = 0;
        int start = offset;
        while (true)
        {
            // A sixth byte cannot carry a 32 bit value at all, so the width check comes before the bounds
            // check: a runaway varint is an over-wide encoding rather than a truncated one.
            if (shift > 28) { offset = start; reason = ReasonOverflow; return false; }
            if (offset >= source.Length) { offset = start; reason = ReasonTruncated; return false; }

            byte b = source[offset++];
            if (shift == 28 && (b & 0xF0) != 0) { offset = start; reason = ReasonOverflow; return false; }

            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                // Minimal: only a single-byte encoding may end in a zero group.
                if (b == 0 && offset - start > 1) { offset = start; value = 0; reason = ReasonNotMinimal; return false; }
                return true;
            }

            shift += 7;
        }
    }

    /// <summary>
    /// Reads one unsigned 64 bit varint and advances <paramref name="offset"/> past it, with the same
    /// refusals as <see cref="TryRead"/> at ten bytes rather than five.
    /// </summary>
    public static bool TryReadUInt64(ReadOnlySpan<byte> source, ref int offset, out ulong value, out string? reason)
    {
        value = 0;
        reason = null;
        int shift = 0;
        int start = offset;
        while (true)
        {
            if (shift > 63) { offset = start; reason = ReasonOverflow; return false; }
            if (offset >= source.Length) { offset = start; reason = ReasonTruncated; return false; }

            byte b = source[offset++];
            // The tenth byte carries one value bit, bit 63, and nothing above it.
            if (shift == 63 && (b & 0xFE) != 0) { offset = start; reason = ReasonOverflow; return false; }

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (b == 0 && offset - start > 1) { offset = start; value = 0; reason = ReasonNotMinimal; return false; }
                return true;
            }

            shift += 7;
        }
    }

    /// <summary>Reads one declared-signed 32 bit varint, un-zig-zagging the raw value.</summary>
    public static bool TryReadSigned(ReadOnlySpan<byte> source, ref int offset, out int value, out string? reason)
    {
        if (!TryRead(source, ref offset, out uint raw, out reason)) { value = 0; return false; }
        value = UnZigZag(raw);
        return true;
    }

    /// <summary>Reads one declared-signed 64 bit varint, un-zig-zagging the raw value.</summary>
    public static bool TryReadSigned64(ReadOnlySpan<byte> source, ref int offset, out long value, out string? reason)
    {
        if (!TryReadUInt64(source, ref offset, out ulong raw, out reason)) { value = 0; return false; }
        value = UnZigZag64(raw);
        return true;
    }
}
