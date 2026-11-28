using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Interns the descriptors and key layouts that one recorder's or one decoder's frames share.
internal sealed class RendererPerfFrameMetricTable
{
    readonly Dictionary<DescriptorKey, RendererPerfFrameMetricDescriptor> _descriptors = [];
    readonly Dictionary<int, List<RendererPerfFrameMetricLayout>> _layouts = [];
    readonly object _gate = new();

    internal RendererPerfFrameMetricDescriptor Descriptor(RendererPerfMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        return Descriptor(metric.Unit, metric.Source, metric.Status, metric.Reason, metric.Value.HasValue);
    }

    internal RendererPerfFrameMetricDescriptor Descriptor(string unit, string source, RendererPerfMetricStatus status,
        string? reason, bool hasValue)
    {
        var key = new DescriptorKey(unit, source, status, reason, hasValue);
        lock (_gate)
        {
            if (!_descriptors.TryGetValue(key, out var descriptor))
                _descriptors.Add(key, descriptor = new(unit, source, status, reason, hasValue));
            return descriptor;
        }
    }

    internal RendererPerfFrameMetricLayout Layout(ReadOnlySpan<string> keys, ReadOnlySpan<RendererPerfFrameMetricDescriptor> descriptors)
    {
        var hash = new HashCode();
        foreach (string key in keys) hash.Add(key, StringComparer.Ordinal);
        foreach (var descriptor in descriptors) hash.Add(RuntimeHelpers.GetHashCode(descriptor));
        int code = hash.ToHashCode();
        lock (_gate)
        {
            if (!_layouts.TryGetValue(code, out var bucket)) _layouts.Add(code, bucket = []);
            foreach (var candidate in bucket)
                if (candidate.Matches(keys, descriptors)) return candidate;
            var layout = new RendererPerfFrameMetricLayout(this, keys.ToArray(), descriptors.ToArray());
            bucket.Add(layout);
            return layout;
        }
    }

    readonly record struct DescriptorKey(string Unit, string Source, RendererPerfMetricStatus Status, string? Reason, bool HasValue);
}

// Everything a metric carries except its value. Equal values materialize as one shared metric instance.
internal sealed class RendererPerfFrameMetricDescriptor
{
    readonly ConcurrentDictionary<long, RendererPerfMetric> _metrics = new();
    readonly RendererPerfMetric? _unvalued;

    internal RendererPerfFrameMetricDescriptor(string unit, string source, RendererPerfMetricStatus status, string? reason, bool hasValue)
    {
        Unit = unit; Source = source; Status = status; Reason = reason; HasValue = hasValue;
        if (!hasValue) _unvalued = new(null, unit, source, status, reason);
    }

    internal string Unit { get; }
    internal string Source { get; }
    internal RendererPerfMetricStatus Status { get; }
    internal string? Reason { get; }
    internal bool HasValue { get; }

    internal RendererPerfMetric Metric(double value) => _unvalued ?? _metrics.GetOrAdd(BitConverter.DoubleToInt64Bits(value),
        static (bits, descriptor) => new(BitConverter.Int64BitsToDouble(bits), descriptor.Unit, descriptor.Source,
            descriptor.Status, descriptor.Reason), this);
}

// One key set with a descriptor per slot, shared by every frame that records the same metrics.
internal sealed class RendererPerfFrameMetricLayout
{
    readonly RendererPerfFrameMetricDescriptor[] _descriptors;
    readonly Dictionary<string, int> _slots;

    internal RendererPerfFrameMetricLayout(RendererPerfFrameMetricTable table, string[] keys, RendererPerfFrameMetricDescriptor[] descriptors)
    {
        Table = table;
        Keys = ImmutableCollectionsMarshal.AsImmutableArray(keys);
        _descriptors = descriptors;
        _slots = new(keys.Length, StringComparer.Ordinal);
        for (int slot = 0; slot < keys.Length; slot++) _slots.Add(keys[slot], slot);
    }

    internal RendererPerfFrameMetricTable Table { get; }
    internal ImmutableArray<string> Keys { get; }
    internal int Count => _descriptors.Length;
    internal RendererPerfFrameMetricDescriptor Descriptor(int slot) => _descriptors[slot];
    internal bool TryGetSlot(string key, out int slot) => _slots.TryGetValue(key, out slot);

    internal bool Matches(ReadOnlySpan<string> keys, ReadOnlySpan<RendererPerfFrameMetricDescriptor> descriptors)
    {
        if (keys.Length != _descriptors.Length || descriptors.Length != _descriptors.Length) return false;
        for (int slot = 0; slot < keys.Length; slot++)
            if (!ReferenceEquals(descriptors[slot], _descriptors[slot]) || !string.Equals(keys[slot], Keys[slot], StringComparison.Ordinal))
                return false;
        return true;
    }
}

// Collects one frame's metrics in slot order and resolves them to an interned layout.
internal sealed class RendererPerfFrameMetricsBuilder(RendererPerfFrameMetricTable table)
{
    readonly List<string> _keys = [];
    readonly List<RendererPerfFrameMetricDescriptor> _descriptors = [];
    readonly List<double> _values = [];
    RendererPerfFrameMetricLayout? _last;

    internal RendererPerfFrameMetricTable Table => table;

    internal void Add(string key, RendererPerfMetric metric) => Add(key, table.Descriptor(metric), metric.Value ?? 0);

    internal void Add(string key, RendererPerfFrameMetricDescriptor descriptor, double value = 0)
    {
        _keys.Add(key); _descriptors.Add(descriptor); _values.Add(value);
    }

    internal void Reset() { _keys.Clear(); _descriptors.Clear(); _values.Clear(); }

    internal RendererPerfFrameMetrics ToMetrics()
    {
        try
        {
            var keys = CollectionsMarshal.AsSpan(_keys);
            var descriptors = CollectionsMarshal.AsSpan(_descriptors);
            var layout = _last is { } last && last.Matches(keys, descriptors) ? last : table.Layout(keys, descriptors);
            _last = layout;
            return RendererPerfFrameMetrics.Create(layout, CollectionsMarshal.AsSpan(_values));
        }
        finally { Reset(); }
    }
}
