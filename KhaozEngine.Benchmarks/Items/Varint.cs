using System;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// Unsigned LEB128 exactly as contracts 15 defines it: seven value bits per byte, low group first, the
/// high bit set on every byte but the last, at most five bytes for a 32 bit value and ten for a 64 bit
/// one, and every encoding MINIMAL. Nothing here zig-zags, because every field the spike encodes is
/// declared unsigned.
/// </summary>
internal static class Varint
{
    internal const int MaximumBytes32 = 5;
    internal const int MaximumBytes64 = 10;

    internal static int Write(Span<byte> destination, ulong value)
    {
        int written = 0;
        while (true)
        {
            byte group = (byte)(value & 0x7F);
            value >>= 7;
            if (value == 0)
            {
                destination[written++] = group;
                return written;
            }

            destination[written++] = (byte)(group | 0x80);
        }
    }

    internal static int Size(ulong value)
    {
        int size = 1;
        while ((value >>= 7) != 0) size++;
        return size;
    }

    internal static int Size(int value) => Size((ulong)(uint)value);

    /// <summary>
    /// Reads one varint. Answers false for a non-terminating encoding, an over-long one, and a
    /// non-minimal one, which are the <c>varint-overflow</c> and <c>varint-not-minimal</c> reasons of
    /// contracts 9.7.
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> source, ref int offset, int maximumBytes, out ulong value, out string? reason)
    {
        value = 0;
        reason = null;
        int shift = 0;
        int read = 0;
        while (true)
        {
            if (offset >= source.Length)
            {
                reason = "field-truncated";
                return false;
            }

            if (read == maximumBytes)
            {
                reason = "varint-overflow";
                return false;
            }

            byte group = source[offset++];
            read++;
            value |= (ulong)(group & 0x7F) << shift;
            shift += 7;
            if ((group & 0x80) == 0)
            {
                if (read > 1 && group == 0)
                {
                    reason = "varint-not-minimal";
                    return false;
                }

                return true;
            }
        }
    }
}
