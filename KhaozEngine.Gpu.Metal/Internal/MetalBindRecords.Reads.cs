using System;
using System.Numerics;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalBindRecords
{
    // Read coverage survives encoder boundaries. It changes only with bindings, shader use or the footprint.
    uint _pendingReads, _fullReads;
    MetalRecordedReads? _readTarget;
    ulong _readGeneration;
    int _readSegment;

    internal void RecordReads(MetalRecordedReads reads, int segment)
    {
        if (_table is null) return;
        if (!ReferenceEquals(_readTarget, reads) || _readGeneration != reads.Generation || _readSegment != segment)
        {
            _readTarget = reads;
            _readGeneration = reads.Generation;
            _readSegment = segment;
            _pendingReads = _fullReads = (1u << _recorded) - 1;
        }
        while (_pendingReads != 0)
        {
            int slot = BitOperations.TrailingZeroCount(_pendingReads);
            uint bit = 1u << slot;
            bool full = (_fullReads & bit) != 0;
            ref SlotRecord record = ref _slots[slot];
            ReadOnlySpan<MetalBoundResource> bindings = record.Bound.Resources;
            for (int binding = 0; binding < bindings.Length; binding++)
            {
                ref readonly var bound = ref bindings[binding];
                if (!full && !bound.AppliesCallerOffset) continue;
                if (bound.Resource is not MetalBuffer buffer || buffer.Handle.IsNull) continue;
                foreach (var stage in _stages)
                {
                    if (!_table.TryGetIndex(slot, binding, stage, out _)) continue;
                    ulong offset = (ulong)ComposedOffset(bound, segment, record.DynamicOffset, _offsetAlignment);
                    reads.Add(buffer.CpuResource, offset, bound.Range);
                    break;
                }
            }
            _pendingReads &= ~bit;
            _fullReads &= ~bit;
        }
    }
}
