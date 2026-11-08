using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Binding and draw-window coverage, independent of encoder state. Only Begin forgets the bindings.</summary>
internal sealed class MetalVertexReadRecords
{
    struct Binding
    {
        internal MetalBuffer? Buffer;
        internal uint Offset;
        internal MetalVertexReadLayout.Stream Stream;
        internal MetalVertexReadCoverage? Coverage;
    }
    readonly record struct Key(MetalBuffer Buffer, uint Offset, int Shape, bool Reachable);

    readonly Binding[] _bindings = new Binding[MetalVertexStreamRecords.MaxSlot + 1];
    readonly Dictionary<Key, MetalVertexReadCoverage> _coverage = new();
    readonly Dictionary<MetalVertexReadLayout.Stream, int> _shapes = new();
    readonly int[] _shapeIds = new int[MetalVertexStreamRecords.MaxSlot + 1];
    readonly List<MetalVertexReadCoverage> _storage = new();
    readonly MetalVertexWindowPool _windowPool = new();
    MetalRecordedReads? _target;
    MetalVertexReadLayout? _layout;
    ulong _generation;
    bool _indexed;
    uint _dirty, _windowSlots;
    long _preparations;
    internal long RetainedWindowCapacityForTests => _windowPool.RetainedCapacityForTests;

    internal long CoverageStepsForTests
    {
        get
        {
            long steps = _preparations;
            foreach (var coverage in _coverage.Values) steps += coverage.StepsForTests;
            return steps;
        }
    }

    internal void Record(uint slot, MetalBuffer buffer, uint offset)
    {
        ref var binding = ref _bindings[slot];
        if (ReferenceEquals(binding.Buffer, buffer) && binding.Offset == offset) return;
        binding.Buffer = buffer;
        binding.Offset = offset;
        _dirty |= 1u << (int)slot;
    }

    internal void Reset()
    {
        Array.Clear(_bindings);
        ClearCoverage();
        _target = null;
        _dirty = _windowSlots = 0;
    }

    internal void RecordReads(MetalRecordedReads reads, MetalVertexReadLayout layout,
        uint vertexStart, uint vertexCount, uint instanceStart, uint instanceCount, bool indexed)
    {
        if (vertexCount == 0 || instanceCount == 0) return;
        if (!ReferenceEquals(_target, reads) || _generation != reads.Generation)
        {
            if (_target is not null) ClearCoverage();
            _target = reads;
            _generation = reads.Generation;
        }
        if (!ReferenceEquals(_layout, layout) || _indexed != indexed)
        {
            if (!ReferenceEquals(_layout, layout)) ResolveShapes(layout);
            _layout = layout;
            _indexed = indexed;
            _dirty = (1u << Math.Min(layout.Streams.Length, _bindings.Length)) - 1;
            _windowSlots = 0;
        }
        if (_dirty != 0) Prepare(reads, layout, indexed);

        // Indexed streams were handled when their binding changed. The hot loop visits only draw windows.
        uint slots = _windowSlots;
        while (slots != 0)
        {
            int slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            ref readonly var binding = ref _bindings[slot];
            if (binding.Buffer!.Handle.IsNull) continue;
            ref readonly var stream = ref binding.Stream;
            ulong first = stream.PerInstance ? instanceStart : vertexStart;
            ulong last = first + (stream.PerInstance ? instanceCount : vertexCount) - 1;
            if (stream.PerInstance && stream.StepRate != 1)
            {
                first /= stream.StepRate;
                last /= stream.StepRate;
            }
            binding.Coverage!.Record(reads, binding.Buffer, binding.Offset, stream, first, last, reachable: false);
        }
    }

    // Window stores are CPU recording scratch. Published prefixes own the separate exact read index.
    void ClearCoverage()
    {
        foreach (var coverage in _coverage.Values) coverage.Clear();
        _coverage.Clear();
        _shapes.Clear();
        _layout = null;
        _preparations = 0;
    }

    // Resolve structural equality only when the pipeline layout changes, never on every mesh rebind.
    void ResolveShapes(MetalVertexReadLayout layout)
    {
        for (int slot = 0; slot < Math.Min(layout.Streams.Length, _bindings.Length); slot++)
        {
            var stream = layout.Streams[slot];
            if (!_shapes.TryGetValue(stream, out int shape))
            {
                shape = _shapes.Count;
                _shapes.Add(stream, shape);
            }
            _shapeIds[slot] = shape;
            _preparations++;
        }
    }

    void Prepare(MetalRecordedReads reads, MetalVertexReadLayout layout, bool indexed)
    {
        uint slots = _dirty & ((1u << Math.Min(layout.Streams.Length, _bindings.Length)) - 1);
        while (slots != 0)
        {
            int slot = BitOperations.TrailingZeroCount(slots);
            uint bit = 1u << slot;
            slots &= ~bit;
            _windowSlots &= ~bit;
            _preparations++;
            ref var binding = ref _bindings[slot];
            if (binding.Buffer is not { } buffer || buffer.Handle.IsNull) continue;
            binding.Stream = layout.Streams[slot];
            bool reachable = indexed && !binding.Stream.PerInstance;
            var key = new Key(buffer, binding.Offset, _shapeIds[slot], reachable);
            if (!_coverage.TryGetValue(key, out var coverage))
            {
                int index = _coverage.Count;
                if (index == _storage.Count) _storage.Add(new(_windowPool));
                coverage = _storage[index];
                coverage.Clear();
                _coverage.Add(key, coverage);
            }
            binding.Coverage = coverage;
            if (reachable) coverage.Record(reads, buffer, binding.Offset, binding.Stream, 0, 0, reachable: true);
            else _windowSlots |= bit;
        }
        _dirty = 0;
    }
}
