using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>One recording's disconnected windows, retaining only aggregate high-water capacity.</summary>
internal sealed class MetalVertexWindowTable
{
    // Slots identify small coverage headers, not buffers or GPU objects. Begin and generation changes
    // clear every entry. One shared capacity serves any binding order with the same total window demand.
    readonly Dictionary<(int RecordSlot, ulong First), ulong> _windows = new();

    internal int RetainedCapacityForTests => _windows.EnsureCapacity(0);

    internal void Clear() => _windows.Clear();

    internal bool Covers(int recordSlot, ulong first, ulong last) =>
        _windows.TryGetValue((recordSlot, first), out ulong end) && last <= end;

    internal void Record(int recordSlot, ulong first, ulong last)
    {
        var key = (recordSlot, first);
        _windows[key] = _windows.TryGetValue(key, out ulong end) ? Math.Max(end, last) : last;
    }
}
