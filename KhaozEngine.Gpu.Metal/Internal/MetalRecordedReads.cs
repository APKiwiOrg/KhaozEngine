using System.Collections.Generic;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Slot-owned read footprint. Adjacent contiguous ranges coalesce without protecting gaps.</summary>
internal sealed class MetalRecordedReads
{
    internal sealed class ResourceReads
    {
        internal readonly MetalContiguousReadIndex Contiguous = new();
        internal readonly MetalReadIntervalIndex Strided = new();
        internal void Clear() { Contiguous.Clear(); Strided.Clear(); }
        internal void ReserveLike(ResourceReads other)
        {
            Contiguous.ReserveLike(other.Contiguous);
            Strided.ReserveLike(other.Strided);
        }
    }

    // Owned by a binding/attribute, never by a shared resource. Generation checks prevent stale node hints.
    internal struct Cursor
    {
        internal MetalRecordedReads? Target;
        internal ulong Generation;
        internal MetalCpuResource? Resource;
        internal ResourceReads? Reads;
        internal int Tail;
    }

    readonly Dictionary<MetalCpuResource, ResourceReads> _resources = new();
    readonly List<ResourceReads> _storage = new();
    internal ulong Generation { get; private set; }
    long _resourceLookups;
    internal long HashLookupsForTests => _resourceLookups;
    internal long InsertsForTests
    {
        get
        {
            long count = 0;
            foreach (var reads in _resources.Values)
                count += reads.Contiguous.InsertsForTests + reads.Strided.InsertsForTests;
            return count;
        }
    }
    internal long ExtendsForTests
    {
        get
        {
            long count = 0;
            foreach (var reads in _resources.Values)
                count += reads.Contiguous.ExtendsForTests + reads.Strided.ExtendsForTests;
            return count;
        }
    }
    internal long RotationsForTests
    {
        get
        {
            long count = 0;
            foreach (var reads in _resources.Values)
                count += reads.Contiguous.RotationsForTests + reads.Strided.RotationsForTests;
            return count;
        }
    }

    // Counts index search/hint probes and rotations, independent of the clock (#1692, #1696).
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

    internal long NodeWritesForTests
    {
        get
        {
            long writes = 0;
            foreach (var reads in _resources.Values)
                writes += reads.Contiguous.NodeWritesForTests + reads.Strided.NodeWritesForTests;
            return writes;
        }
    }

    internal bool Overlaps(in MetalCpuRead access)
        => _resources.TryGetValue(access.Resource, out var reads)
            && (reads.Contiguous.Overlaps(access) || reads.Strided.Overlaps(access));

    // Share observed capacity, never the byte union or resource identities of an in-flight prefix.
    internal void ReserveLike(MetalRecordedReads other)
    {
        if (ReferenceEquals(this, other)) return;
        _resources.EnsureCapacity(other._storage.Count);
        _storage.EnsureCapacity(other._storage.Count);
        for (int i = 0; i < other._storage.Count; i++)
        {
            if (i == _storage.Count) _storage.Add(new());
            _storage[i].ReserveLike(other._storage[i]);
        }
    }

    internal void Clear()
    {
        Generation++;
        foreach (var reads in _resources.Values) reads.Clear();
        _resources.Clear();
        _resourceLookups = 0;
    }

    ResourceReads ReadsFor(MetalCpuResource resource)
    {
        _resourceLookups++;
        if (_resources.TryGetValue(resource, out var reads)) return reads;
        // Stable recording positions retain their capacity across differently sized main/capture frames.
        int index = _resources.Count;
        if (index == _storage.Count) _storage.Add(new());
        reads = _storage[index];
        _resources.Add(resource, reads);
        return reads;
    }

    ResourceReads ReadsFor(MetalCpuResource resource, ref Cursor cursor)
    {
        if (!ReferenceEquals(cursor.Target, this) || cursor.Generation != Generation
            || !ReferenceEquals(cursor.Resource, resource))
            cursor = new() { Target = this, Generation = Generation, Resource = resource, Reads = ReadsFor(resource) };
        return cursor.Reads!;
    }

    internal void AddStrided(MetalCpuResource resource, ulong offset, ulong bytes, ulong stride, ulong count, ref Cursor cursor)
    {
        if (count == 0 || bytes == 0) return;
        if (stride == bytes) { Add(resource, offset, checked(bytes * count), ref cursor); return; }
        ReadsFor(resource, ref cursor).Strided.Add(new(resource, offset, bytes, stride, count), ref cursor.Tail);
    }

    internal void Add(MetalCpuResource resource, ulong offset, ulong length, ref Cursor cursor)
    {
        if (length == 0) return;
        ReadsFor(resource, ref cursor).Contiguous.AddContiguous(new(resource, offset, length));
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
