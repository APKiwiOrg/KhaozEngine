using System;
using System.Collections.Generic;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Identity of CPU-visible bytes. Offsets include a uniform ring's physical segment base.</summary>
internal sealed class MetalCpuResource
{
    static long _nextId;
    internal ulong Id { get; } = (ulong)Interlocked.Increment(ref _nextId);
}

internal readonly record struct MetalCpuRead(MetalCpuResource Resource, ulong Offset, ulong Length, ulong Stride = 0, ulong Count = 1)
{
    internal bool Overlaps(in MetalCpuRead other)
    {
        if (!ReferenceEquals(Resource, other.Resource) || Length == 0 || other.Length == 0) return false;
        if (other.Stride != 0) throw new ArgumentException("A CPU access must be a contiguous range.", nameof(other));
        if (Stride == 0) return Offset < other.Offset + other.Length && other.Offset < Offset + Length;
        ulong first = other.Offset < Offset + Length ? 0 : (other.Offset - Offset - Length) / Stride + 1;
        return first < Count && Offset + first * Stride < other.Offset + other.Length;
    }
}

/// <summary>Read footprint of one recording. Adjacent ranges coalesce without protecting gaps.</summary>
internal sealed class MetalRecordedReads
{
    readonly Dictionary<MetalCpuResource, List<MetalCpuRead>> _ranges = new();
    readonly Stack<List<MetalCpuRead>> _freeRanges = new();
    internal Dictionary<MetalCpuResource, List<MetalCpuRead>>.ValueCollection Ranges => _ranges.Values;
    internal bool Overlaps(in MetalCpuRead access)
    {
        if (!_ranges.TryGetValue(access.Resource, out var ranges)) return false;
        foreach (var range in ranges) if (range.Overlaps(access)) return true;
        return false;
    }
    internal void Clear()
    {
        foreach (var ranges in _ranges.Values) { ranges.Clear(); _freeRanges.Push(ranges); }
        _ranges.Clear();
    }
    List<MetalCpuRead> RangesFor(MetalCpuResource resource)
    {
        if (_ranges.TryGetValue(resource, out var ranges)) return ranges;
        ranges = _freeRanges.Count == 0 ? [] : _freeRanges.Pop();
        _ranges.Add(resource, ranges);
        return ranges;
    }
    internal void AddStrided(MetalCpuResource resource, ulong offset, ulong bytes, ulong stride, ulong count)
    {
        if (count == 0 || bytes == 0) return;
        if (stride == bytes) { Add(resource, offset, checked(bytes * count)); return; }
        var ranges = RangesFor(resource);
        var read = new MetalCpuRead(resource, offset, bytes, stride, count);
        if (!ranges.Contains(read)) ranges.Add(read);
    }
    internal void Add(MetalCpuResource resource, ulong offset, ulong length)
    {
        if (length == 0) return;
        var ranges = RangesFor(resource);
        ulong end = checked(offset + length);
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            var range = ranges[i];
            if (range.Stride != 0) continue;
            if (range.Offset > end || offset > range.Offset + range.Length) continue;
            offset = Math.Min(offset, range.Offset);
            end = Math.Max(end, range.Offset + range.Length);
            ranges.RemoveAt(i);
        }
        ranges.Add(new(resource, offset, end - offset));
    }
}

/// <summary>Shares the queue's submit lock. Publishing prefix reads and CPU writes are atomic against one another.
/// No GPU wait, copy or submission is introduced. A zero final value is a quarantined, unfinished frame.</summary>
internal sealed class MetalCpuReadProtection(object gate, Func<ulong> completed)
{
    sealed record Frame(ulong Id, int Segment, MetalRecordedReads Reads)
    {
        internal ulong FinalValue;
    }
    readonly List<Frame> _frames = [];
    readonly Stack<MetalRecordedReads> _freeReads = new();
    readonly Dictionary<object, List<(MetalCpuRead Range, bool Writable)>> _mappings = new(ReferenceEqualityComparer.Instance);
    ulong _nextFrame;
    internal object Gate => gate;
    internal MetalRecordedReads TakeReads()
    {
        lock (gate)
        {
            RetireCompleted();
            return _freeReads.Count == 0 ? new() : _freeReads.Pop();
        }
    }

    internal ulong Protect(int segment, MetalRecordedReads reads)
    {
        lock (gate)
        {
            RetireCompleted();
            ulong id = ++_nextFrame;
            foreach (var ranges in reads.Ranges)
                foreach (var range in ranges)
                    foreach (var maps in _mappings.Values)
                        foreach (var map in maps)
                            if (map.Writable && range.Overlaps(map.Range))
                                throw Refusal(MetalFrameAccessFailure.MappedPrefix, map.Range, id);
            _frames.Add(new(id, segment, reads));
            return id;
        }
    }

    internal void Submitted(ulong frameId, ulong finalValue)
    {
        lock (gate)
        {
            foreach (var frame in _frames)
                if (frame.Id == frameId) { frame.FinalValue = finalValue; return; }
        }
    }

    internal void RequireSlotAvailable(int segment)
    {
        RetireCompleted();
        foreach (var frame in _frames)
            if (frame.Segment == segment && frame.FinalValue == 0)
                throw new MetalFrameAccessException(MetalFrameAccessFailure.AbandonedFrameSlot, 0, 0, 0, frame.Id, segment);
    }

    // Caller holds Gate through the actual copy or pointer handoff, not only through this check.
    internal void RequireWritable(MetalCpuResource resource, ulong offset, ulong length,
        MetalFrameAccessFailure reason = MetalFrameAccessFailure.OverlappingWrite)
    {
        var write = new MetalCpuRead(resource, offset, length);
        ulong? value = null;
        foreach (var frame in _frames)
        {
            if (!frame.Reads.Overlaps(write)) continue;
            // Disjoint writes never poll the driver. An open tail has no final value to poll for either.
            if (frame.FinalValue == 0) throw Refusal(reason, write, frame.Id);
            value ??= completed();
            if (value.Value < frame.FinalValue) throw Refusal(reason, write, frame.Id);
        }
        if (value is { } known) ReleaseCompleted(known);
    }

    internal void MapWritable(object owner, MetalCpuResource resource, ulong offset, ulong length)
        => Map(owner, resource, offset, length, writable: true);

    internal void Map(object owner, MetalCpuResource resource, ulong offset, ulong length, bool writable)
    {
        lock (gate)
        {
            if (writable) RequireWritable(resource, offset, length, MetalFrameAccessFailure.WritableMapping);
            if (!_mappings.TryGetValue(owner, out var maps)) _mappings.Add(owner, maps = []);
            maps.Add((new(resource, offset, length), writable));
        }
    }
    internal void Unmap(object owner)
    {
        lock (gate)
        {
            if (!_mappings.TryGetValue(owner, out var maps)) return;
            maps.RemoveAt(maps.Count - 1);
            if (maps.Count == 0) _mappings.Remove(owner);
        }
    }
    void RetireCompleted()
    {
        if (_frames.Count == 0) return;
        ReleaseCompleted(completed());
    }
    void ReleaseCompleted(ulong value)
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            if (_frames[i].FinalValue == 0 || _frames[i].FinalValue > value) continue;
            var reads = _frames[i].Reads;
            _frames.RemoveAt(i);
            reads.Clear();
            _freeReads.Push(reads);
        }
    }
    static MetalFrameAccessException Refusal(MetalFrameAccessFailure reason, in MetalCpuRead access, ulong frameId)
        => new(reason, access.Resource.Id, access.Offset, access.Length, frameId);
}
