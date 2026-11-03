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

/// <summary>Shares the queue's submit lock. Publishing prefix reads and CPU writes are atomic against one another.
/// Explicit read drains cover unsignalled prefixes outside the gate. A zero final value keeps a frame
/// quarantined even after that drain.</summary>
internal sealed class MetalCpuReadProtection(object gate, Func<ulong> completed, Func<ulong> lastSubmitted,
    int framesInFlight = MetalFramesInFlight.Default)
{
    record struct Frame(ulong Id, int Segment, MetalRecordedReads Reads)
    {
        internal ulong FinalValue;
        internal bool PrefixCommitted;
        internal ulong SubmittedBeforePrefix;
        internal bool QueueDrained;
    }
    readonly List<Frame> _frames = new(framesInFlight);
    readonly Dictionary<object, List<(MetalCpuRead Range, bool Writable)>> _mappings = new(ReferenceEqualityComparer.Instance);
    readonly Stack<List<(MetalCpuRead Range, bool Writable)>> _freeMappings = new();
    ulong _nextFrame;
    internal object Gate => gate;
    internal ulong Protect(int segment, MetalRecordedReads reads)
    {
        lock (gate)
        {
            RetireCompleted();
            ulong id = ++_nextFrame;
            foreach (var maps in _mappings.Values)
                foreach (var map in maps)
                    if (map.Writable && reads.Overlaps(map.Range))
                        throw Refusal(MetalFrameAccessFailure.MappedPrefix, map.Range, id);
            _frames.Add(new(id, segment, reads));
            return id;
        }
    }

    internal void Submitted(ulong frameId, ulong finalValue)
    {
        lock (gate)
        {
            for (int i = 0; i < _frames.Count; i++)
            {
                var frame = _frames[i];
                if (frame.Id != frameId) continue;
                frame.FinalValue = finalValue;
                _frames[i] = frame;
                return;
            }
        }
    }

    internal void Committed(ulong frameId)
    {
        lock (gate)
            for (int i = 0; i < _frames.Count; i++)
            {
                var frame = _frames[i];
                if (frame.Id != frameId) continue;
                frame.SubmittedBeforePrefix = lastSubmitted();
                frame.PrefixCommitted = true;
                _frames[i] = frame;
                return;
            }
    }

    // An explicit CPU drain also covers prefixes with no final signal, including abandoned recordings.
    // Snapshot under the submit gate, wait outside it, then acknowledge only work in that snapshot.
    // This proves readback readiness, never slot reuse or writable access before final completion.
    internal void DrainCommittedPrefixes(bool committedSetup, Action drainQueue)
    {
        ulong through;
        bool required = committedSetup;
        lock (gate)
        {
            ulong value = _frames.Count == 0 ? 0 : completed();
            ReleaseCompleted(value);
            through = _nextFrame;
            foreach (var frame in _frames)
                // Any completed final queued after this prefix covers its GPU work, even if its own tail
                // was abandoned. It still cannot release that frame's slot or immutable byte ranges.
                if (frame.PrefixCommitted && !frame.QueueDrained && value <= frame.SubmittedBeforePrefix)
                { required = true; break; }
        }
        if (!required) return;
        drainQueue();
        lock (gate)
            for (int i = 0; i < _frames.Count; i++)
            {
                var frame = _frames[i];
                if (frame.Id > through || !frame.PrefixCommitted) continue;
                frame.QueueDrained = true;
                _frames[i] = frame;
            }
    }

    internal void RequireSlotAvailable(int segment)
    {
        RetireCompleted();
        foreach (var frame in _frames)
            if (frame.Segment == segment && frame.FinalValue == 0)
                throw new MetalFrameAccessException(MetalFrameAccessFailure.AbandonedFrameSlot, 0, 0, 0, frame.Id, segment);
    }

    // Caller holds Gate through the actual copy or mapping-base capture and lease registration.
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
            if (!_mappings.TryGetValue(owner, out var maps))
            {
                maps = _freeMappings.Count == 0 ? [] : _freeMappings.Pop();
                _mappings.Add(owner, maps);
            }
            maps.Add((new(resource, offset, length), writable));
        }
    }
    internal void Unmap(object owner)
    {
        lock (gate)
        {
            if (!_mappings.TryGetValue(owner, out var maps)) return;
            maps.RemoveAt(maps.Count - 1);
            if (maps.Count == 0)
            {
                _mappings.Remove(owner);
                _freeMappings.Push(maps);
            }
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
            // The recording owns its read storage. Retirement drops protection without clearing a slot
            // another Begin may already have reclaimed after its completion wait.
            _frames.RemoveAt(i);
        }
    }
    static MetalFrameAccessException Refusal(MetalFrameAccessFailure reason, in MetalCpuRead access, ulong frameId)
        => new(reason, access.Resource.Id, access.Offset, access.Length, frameId);
}
