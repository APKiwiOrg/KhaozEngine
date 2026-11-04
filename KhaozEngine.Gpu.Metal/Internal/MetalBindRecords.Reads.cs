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
    internal long ReadShaderLookupsForTests { get; private set; }
    struct ReadBinding
    {
        internal MetalRecordedReads.Cursor Cursor;
        internal bool Live;
    }
    readonly ReadBinding[]?[] _readCursors = new ReadBinding[MaxSlot + 1][];

    internal void RecordReads(MetalRecordedReads reads, int segment)
    {
        if (_table is null) return;
        if (!ReferenceEquals(_readTarget, reads) || _readGeneration != reads.Generation || _readSegment != segment)
        {
            _readTarget = reads;
            _readGeneration = reads.Generation;
            _readSegment = segment;
            ReadShaderLookupsForTests = 0;
            _pendingReads = _fullReads = (1u << _recorded) - 1;
        }
        while (_pendingReads != 0)
        {
            int slot = BitOperations.TrailingZeroCount(_pendingReads);
            uint bit = 1u << slot;
            bool full = (_fullReads & bit) != 0;
            ref SlotRecord record = ref _slots[slot];
            ReadOnlySpan<MetalBoundResource> bindings = record.Bound.Resources;
            var cursors = _readCursors[slot];
            if (cursors is null || cursors.Length < bindings.Length)
                _readCursors[slot] = cursors = new ReadBinding[bindings.Length];
            for (int binding = 0; binding < bindings.Length; binding++)
            {
                ref readonly var bound = ref bindings[binding];
                if (!full && !bound.AppliesCallerOffset) continue;
                if (bound.Resource is not MetalBuffer buffer || buffer.Handle.IsNull) continue;
                ref var readBinding = ref cursors[binding];
                if (full)
                {
                    readBinding.Live = false;
                    foreach (var stage in _stages)
                    {
                        ReadShaderLookupsForTests++;
                        if (!_table.TryGetIndex(slot, binding, stage, out _)) continue;
                        readBinding.Live = true;
                        break;
                    }
                }
                if (!readBinding.Live) continue;
                ulong offset = (ulong)ComposedOffset(bound, segment, record.DynamicOffset, _offsetAlignment);
                reads.Add(buffer.CpuResource, offset, bound.Range, ref readBinding.Cursor);
            }
            _pendingReads &= ~bit;
            _fullReads &= ~bit;
        }
    }
}
