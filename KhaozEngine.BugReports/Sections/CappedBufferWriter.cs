using System;
using System.Buffers;
using System.Globalization;

namespace KhaozEngine.BugReports;

/// <summary>
/// A growable buffer that refuses to request or hold more than a fixed number of bytes, so a runaway section is
/// stopped while it writes rather than after it has filled memory.
/// </summary>
sealed class CappedBufferWriter(int cap) : IBufferWriter<byte>
{
    const int InitialSize = 256;

    byte[] buffer = [];
    int written;

    public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if ((long)written + count > buffer.Length) throw new InvalidOperationException("Advanced past the buffer.");
        written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return buffer.AsMemory(written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return buffer.AsSpan(written);
    }

    void Ensure(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        long needed = (long)written + Math.Max(sizeHint, 1);
        if (needed > cap)
            throw new InvalidOperationException("Section output exceeded " + cap.ToString(CultureInfo.InvariantCulture) + " bytes.");
        if (needed <= buffer.Length) return;
        long grown = Math.Max(needed, Math.Max((long)buffer.Length * 2, InitialSize));
        Array.Resize(ref buffer, (int)Math.Min(grown, cap));
    }
}
