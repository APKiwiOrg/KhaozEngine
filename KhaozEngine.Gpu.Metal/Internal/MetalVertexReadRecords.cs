using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Binding and draw-window coverage, independent of encoder state. Only Begin forgets the bindings.</summary>
internal sealed class MetalVertexReadRecords
{
    struct Binding
    {
        internal MetalBuffer? Buffer;
        internal uint Offset;
        internal MetalVertexReadLayout? Layout;
        internal bool Reachable;
        internal ulong First, Last;
        internal MetalRecordedReads.Cursor[]? Cursors;
    }

    readonly Binding[] _bindings = new Binding[MetalVertexStreamRecords.MaxSlot + 1];
    MetalRecordedReads? _target;
    ulong _generation;

    internal void Record(uint slot, MetalBuffer buffer, uint offset)
    {
        ref var binding = ref _bindings[slot];
        if (ReferenceEquals(binding.Buffer, buffer) && binding.Offset == offset) return;
        binding = new() { Buffer = buffer, Offset = offset, Cursors = binding.Cursors };
    }

    internal void Reset()
    {
        for (int slot = 0; slot < _bindings.Length; slot++)
            _bindings[slot] = new() { Cursors = _bindings[slot].Cursors };
        _target = null;
    }

    internal void RecordReads(MetalRecordedReads reads, MetalVertexReadLayout layout,
        uint vertexStart, uint vertexCount, uint instanceStart, uint instanceCount, bool indexed)
    {
        if (vertexCount == 0 || instanceCount == 0) return;
        if (!ReferenceEquals(_target, reads) || _generation != reads.Generation)
        {
            _target = reads;
            _generation = reads.Generation;
            for (int slot = 0; slot < _bindings.Length; slot++) _bindings[slot].Layout = null;
        }
        for (int slot = 0; slot < Math.Min(layout.Streams.Length, _bindings.Length); slot++)
        {
            ref var binding = ref _bindings[slot];
            if (binding.Buffer is not { } buffer || buffer.Handle.IsNull) continue;
            ref readonly var stream = ref layout.Streams[slot];
            bool reachable = indexed && !stream.PerInstance;
            bool same = ReferenceEquals(binding.Layout, layout) && binding.Reachable == reachable;
            if (same && reachable) continue;
            ulong first = stream.PerInstance ? instanceStart / stream.StepRate : vertexStart;
            ulong last = stream.PerInstance ? ((ulong)instanceStart + instanceCount - 1) / stream.StepRate
                : (ulong)vertexStart + vertexCount - 1;
            if (same && first >= binding.First && last <= binding.Last) continue;

            var cursors = binding.Cursors;
            if (cursors is null || cursors.Length < stream.Attributes.Length)
                binding.Cursors = cursors = new MetalRecordedReads.Cursor[stream.Attributes.Length];
            for (int attributeIndex = 0; attributeIndex < stream.Attributes.Length; attributeIndex++)
            {
                var attribute = stream.Attributes[attributeIndex];
                ulong offset = binding.Offset + attribute.Offset;
                if (reachable)
                {
                    if (offset + attribute.Bytes > buffer.SizeInBytes || stream.Stride == 0) continue;
                    reads.AddStrided(buffer.CpuResource, offset, attribute.Bytes, stream.Stride,
                        (buffer.SizeInBytes - offset - attribute.Bytes) / stream.Stride + 1, ref cursors[attributeIndex]);
                }
                else reads.AddStrided(buffer.CpuResource, offset + first * stream.Stride,
                    attribute.Bytes, stream.Stride, last - first + 1, ref cursors[attributeIndex]);
            }
            if (same && first <= binding.Last + 1 && last + 1 >= binding.First)
            {
                first = Math.Min(first, binding.First);
                last = Math.Max(last, binding.Last);
            }
            binding.Layout = layout;
            binding.Reachable = reachable;
            binding.First = first;
            binding.Last = last;
        }
    }
}
