using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Monotonic reads append without tree work. Earlier reads use a separate exact AVL union.</summary>
internal sealed class MetalContiguousReadIndex
{
    struct Range { internal ulong Start, End; }
    Range[] _run = new Range[16];
    int _count;
    long _probes, _writes;
    readonly MetalReadIntervalIndex _earlier = new(contiguous: true);

    internal long ProbeStepsForTests => _probes + _earlier.ProbeStepsForTests;
    internal long NodeWritesForTests => _writes + _earlier.NodeWritesForTests;
    internal long InsertsForTests => _earlier.InsertsForTests;
    internal long ExtendsForTests => _earlier.ExtendsForTests;
    internal long RotationsForTests => _earlier.RotationsForTests;

    internal void Clear()
    {
        // The run holds only byte offsets, so clearing its live length releases all coverage.
        _count = 0;
        _probes = _writes = 0;
        _earlier.Clear();
    }

    internal void AddContiguous(in MetalCpuRead read)
    {
        ulong end = checked(read.Offset + read.Length);
        if (_count != 0)
        {
            _probes++;
            ref var last = ref _run[_count - 1];
            if (read.Offset < last.Start)
            {
                int previous = Floor(read.Offset);
                if (previous >= 0 && _run[previous].End >= end) return;
                _earlier.AddContiguous(read);
                return;
            }
            if (end <= last.End) return;
            if (read.Offset <= last.End)
            {
                last.End = end;
                _writes++;
                return;
            }
        }
        if (_count == _run.Length) Array.Resize(ref _run, checked(_count * 2));
        _run[_count++] = new() { Start = read.Offset, End = end };
        _writes++;
    }

    internal bool Overlaps(in MetalCpuRead access)
    {
        if (access.Length == 0) return false;
        ulong end = access.Offset + access.Length;
        int previous = Floor(access.Offset);
        if (previous >= 0 && _run[previous].End > access.Offset && _run[previous].Start < end) return true;
        int next = previous + 1;
        return (next < _count && _run[next].Start < end) || _earlier.Overlaps(access);
    }

    int Floor(ulong offset)
    {
        int low = 0, high = _count - 1;
        while (low <= high)
        {
            _probes++;
            int middle = low + (high - low) / 2;
            if (_run[middle].Start <= offset) low = middle + 1;
            else high = middle - 1;
        }
        return high;
    }
}
