using System;

namespace KhaozEngine.ItemInstances;

/// <summary>Sorts complete rewritten entries by unsigned reference key while preserving equal-key order.</summary>
internal readonly ref struct InstanceEntrySorter(
    ReadOnlySpan<byte> bytes,
    Span<int> starts,
    Span<int> lengths,
    Span<ulong> keys)
{
    readonly ReadOnlySpan<byte> _bytes = bytes;
    readonly Span<int> _starts = starts;
    readonly Span<int> _lengths = lengths;
    readonly Span<ulong> _keys = keys;

    internal void Record(int index, int start, int length, ulong key)
    {
        _starts[index] = start;
        _lengths[index] = length;
        _keys[index] = key;
    }

    internal int CopySorted(int count, Span<byte> destination)
    {
        for (int outer = 1; outer < count; outer++)
        {
            int start = _starts[outer];
            int length = _lengths[outer];
            ulong key = _keys[outer];
            int inner = outer - 1;
            while (inner >= 0 && _keys[inner] > key)
            {
                _starts[inner + 1] = _starts[inner];
                _lengths[inner + 1] = _lengths[inner];
                _keys[inner + 1] = _keys[inner];
                inner--;
            }

            _starts[inner + 1] = start;
            _lengths[inner + 1] = length;
            _keys[inner + 1] = key;
        }

        int written = 0;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = _bytes.Slice(_starts[index], _lengths[index]);
            if (destination.Length - written < entry.Length) return -1;

            entry.CopyTo(destination[written..]);
            written += entry.Length;
        }

        return written;
    }
}
