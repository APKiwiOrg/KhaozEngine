using System.Collections.Generic;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Slot-owned read footprint. Adjacent contiguous ranges coalesce without protecting gaps.</summary>
internal sealed class MetalRecordedReads
{
    sealed class ResourceReads
    {
        internal readonly MetalReadIntervalIndex Contiguous = new();
        internal readonly MetalReadIntervalIndex Strided = new();
        internal void Clear() { Contiguous.Clear(); Strided.Clear(); }
    }

    readonly Dictionary<MetalCpuResource, ResourceReads> _resources = new();
    readonly List<ResourceReads> _storage = new();

    // Counts index node examinations and rotations, independent of the clock (#1692).
    internal long ProbeStepsForTests
    {
        get
        {
            long steps = 0;
            foreach (var reads in _resources.Values)
                steps += reads.Contiguous.ProbeStepsForTests + reads.Strided.ProbeStepsForTests;
            return steps;
        }
    }

    internal bool Overlaps(in MetalCpuRead access)
        => _resources.TryGetValue(access.Resource, out var reads)
            && (reads.Contiguous.Overlaps(access) || reads.Strided.Overlaps(access));

    internal void Clear()
    {
        foreach (var reads in _resources.Values) reads.Clear();
        _resources.Clear();
    }

    ResourceReads ReadsFor(MetalCpuResource resource)
    {
        if (_resources.TryGetValue(resource, out var reads)) return reads;
        // Stable recording positions retain their capacity across differently sized main/capture frames.
        int index = _resources.Count;
        if (index == _storage.Count) _storage.Add(new());
        reads = _storage[index];
        _resources.Add(resource, reads);
        return reads;
    }

    internal void AddStrided(MetalCpuResource resource, ulong offset, ulong bytes, ulong stride, ulong count)
    {
        if (count == 0 || bytes == 0) return;
        if (stride == bytes) { Add(resource, offset, checked(bytes * count)); return; }
        ReadsFor(resource).Strided.Add(new(resource, offset, bytes, stride, count));
    }

    internal void Add(MetalCpuResource resource, ulong offset, ulong length)
    {
        if (length == 0) return;
        ReadsFor(resource).Contiguous.AddContiguous(new(resource, offset, length));
    }
}
