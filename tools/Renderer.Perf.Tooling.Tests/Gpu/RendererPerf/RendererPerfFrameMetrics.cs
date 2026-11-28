using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// A unit holds every frame until its report is written, so a frame keeps only a presence bitmap, per-word ranks
// and nonzero values over a shared layout. Positive zero and unvalued slots are absent bits.
internal sealed class RendererPerfFrameMetrics : IReadOnlyDictionary<string, RendererPerfMetric>
{
    readonly ulong[] _present;
    readonly int[] _ranks;
    readonly double[] _values;

    RendererPerfFrameMetrics(RendererPerfFrameMetricLayout layout, ulong[] present, int[] ranks, double[] values)
    {
        Layout = layout; _present = present; _ranks = ranks; _values = values;
    }

    internal static RendererPerfFrameMetrics Empty { get; } = new RendererPerfFrameMetricsBuilder(new()).ToMetrics();

    internal RendererPerfFrameMetricLayout Layout { get; }
    public int Count => Layout.Count;
    public IEnumerable<string> Keys => Layout.Keys;
    public IEnumerable<RendererPerfMetric> Values
    {
        get { for (int slot = 0; slot < Count; slot++) yield return Metric(slot); }
    }

    public RendererPerfMetric this[string key]
        => TryGetValue(key, out var metric) ? metric : throw new KeyNotFoundException("The frame has no metric " + key + ".");

    public bool ContainsKey(string key) => Layout.TryGetSlot(key, out _);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out RendererPerfMetric value)
    {
        bool found = Layout.TryGetSlot(key, out int slot);
        value = found ? Metric(slot) : null;
        return found;
    }

    public IEnumerator<KeyValuePair<string, RendererPerfMetric>> GetEnumerator()
    {
        for (int slot = 0; slot < Count; slot++) yield return new(Layout.Keys[slot], Metric(slot));
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal RendererPerfMetric Metric(int slot) => Layout.Descriptor(slot).Metric(Value(slot));

    internal double Value(int slot)
    {
        ulong word = _present[slot >> 6], bit = 1UL << (slot & 63);
        return (word & bit) == 0 ? 0 : _values[_ranks[slot >> 6] + BitOperations.PopCount(word & (bit - 1))];
    }

    internal static RendererPerfFrameMetrics Create(RendererPerfFrameMetricLayout layout, ReadOnlySpan<double> values)
    {
        if (values.Length != layout.Count) throw new ArgumentException("One value is required per layout slot.", nameof(values));
        if (values.IsEmpty) return new(layout, [], [], []);
        var present = new ulong[(values.Length + 63) >> 6];
        int stored = 0;
        for (int slot = 0; slot < values.Length; slot++)
            if (layout.Descriptor(slot).HasValue && BitConverter.DoubleToInt64Bits(values[slot]) != 0)
            { present[slot >> 6] |= 1UL << (slot & 63); stored++; }
        var ranks = new int[present.Length];
        double[] packed = stored == 0 ? [] : new double[stored];
        int next = 0;
        for (int word = 0; word < present.Length; word++)
        {
            ranks[word] = next;
            for (ulong bits = present[word]; bits != 0; bits &= bits - 1)
                packed[next++] = values[(word << 6) + BitOperations.TrailingZeroCount(bits)];
        }
        return new(layout, present, ranks, packed);
    }

    internal static RendererPerfFrameMetrics From(IEnumerable<KeyValuePair<string, RendererPerfMetric>> metrics)
        => From(metrics, new RendererPerfFrameMetricTable());

    internal RendererPerfFrameMetrics Add(string key, RendererPerfMetric metric)
        => From(this.Append(new(key, metric)), Layout.Table);

    internal RendererPerfFrameMetrics SetItem(string key, RendererPerfMetric metric)
        => From(this.Where(pair => pair.Key != key).Append(new(key, metric)), Layout.Table);

    internal RendererPerfFrameMetrics Remove(string key) => From(this.Where(pair => pair.Key != key), Layout.Table);

    static RendererPerfFrameMetrics From(IEnumerable<KeyValuePair<string, RendererPerfMetric>> metrics, RendererPerfFrameMetricTable table)
    {
        var builder = new RendererPerfFrameMetricsBuilder(table);
        foreach (var pair in metrics.OrderBy(pair => pair.Key, StringComparer.Ordinal)) builder.Add(pair.Key, pair.Value);
        return builder.ToMetrics();
    }
}
