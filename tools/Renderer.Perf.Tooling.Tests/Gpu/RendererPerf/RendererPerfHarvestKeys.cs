using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Metric keys and logical descriptors fixed by a recorder's frozen passes. Built once so every harvested
// frame reuses the same key instances and its layout resolves by reference.
internal sealed class RendererPerfHarvestKeys
{
    const string LogicalSource = "logical requested seam commands";
    static readonly int CounterCount = Enum.GetValues<RendererPerfCounter>().Length;
    static readonly string[] DispatchFields = ["owner", "groupX", "groupY", "groupZ", "local0", "local1", "local2"];
    static readonly string[] TargetFields = ["owner", "width", "height", "samples", "depth",
        "colour0", "colour1", "colour2", "colour3", "colour4", "colour5", "colour6", "colour7"];
    internal static readonly string[] GcCollections = ["gc.gen0.collections", "gc.gen1.collections", "gc.gen2.collections"];
    internal const int DispatchOwner = 0, DispatchGroup = 1, DispatchLocal = 4;
    internal const int TargetOwner = 0, TargetWidth = 1, TargetHeight = 2, TargetSamples = 3, TargetDepth = 4, TargetColour = 5;

    readonly string[] _logical;
    readonly string?[] _known;
    readonly RendererPerfFrameMetricDescriptor[] _values, _incomplete, _knownValues;
    readonly List<string[]> _dispatches = [], _targets = [];

    // Owner 0 is unattributed, owner n is pass n - 1, and the last owner is the frame total.
    internal RendererPerfHarvestKeys(RendererPerfFrameMetricTable table, ReadOnlySpan<RendererPerfPassDefinition> passes)
    {
        int owners = passes.Length + 2;
        _logical = new string[owners * CounterCount];
        _known = new string?[_logical.Length];
        for (int owner = 0; owner < owners; owner++)
        {
            string name = owner == 0 ? "unattributed" : owner == owners - 1 ? "total"
                : $"pass.{passes[owner - 1].Id}.{passes[owner - 1].Occurrence}";
            for (int counter = 0; counter < CounterCount; counter++)
                _logical[owner * CounterCount + counter] = $"logical.{name}.{(RendererPerfCounter)counter}";
        }
        _values = new RendererPerfFrameMetricDescriptor[CounterCount];
        _incomplete = new RendererPerfFrameMetricDescriptor[CounterCount];
        _knownValues = new RendererPerfFrameMetricDescriptor[CounterCount];
        for (int counter = 0; counter < CounterCount; counter++)
        {
            string unit = Unit((RendererPerfCounter)counter);
            _values[counter] = table.Descriptor(unit, LogicalSource, RendererPerfMetricStatus.Available, null, true);
            _incomplete[counter] = table.Descriptor(unit, LogicalSource, RendererPerfMetricStatus.ModelIncomplete,
                "Some commands have no resource or pipeline metadata.", false);
            _knownValues[counter] = table.Descriptor(unit, "known logical portion only", RendererPerfMetricStatus.Available, null, true);
        }
        TotalOwner = owners - 1;
    }

    internal int TotalOwner { get; }
    internal string Logical(int owner, RendererPerfCounter counter) => _logical[owner * CounterCount + (int)counter];
    internal string Known(int owner, RendererPerfCounter counter)
        => _known[owner * CounterCount + (int)counter] ??= Logical(owner, counter) + ".known";
    internal RendererPerfFrameMetricDescriptor Value(RendererPerfCounter counter) => _values[(int)counter];
    internal RendererPerfFrameMetricDescriptor Incomplete(RendererPerfCounter counter) => _incomplete[(int)counter];
    internal RendererPerfFrameMetricDescriptor KnownValue(RendererPerfCounter counter) => _knownValues[(int)counter];
    internal string Dispatch(int index, int field) => Indexed(_dispatches, "dispatch", DispatchFields, index)[field];
    internal string Target(int index, int field) => Indexed(_targets, "target", TargetFields, index)[field];

    static string[] Indexed(List<string[]> keys, string prefix, string[] fields, int index)
    {
        while (keys.Count <= index)
        {
            int next = keys.Count;
            keys.Add(Array.ConvertAll(fields, field => $"{prefix}.{next}.{field}"));
        }
        return keys[index];
    }

    static string Unit(RendererPerfCounter counter) => counter is RendererPerfCounter.BufferUploadBytes or RendererPerfCounter.TextureUploadBytes
        or RendererPerfCounter.RequestedBufferBytes or RendererPerfCounter.RequestedTextureBytes or RendererPerfCounter.CopyBytes
        or RendererPerfCounter.ResolveReadBytes or RendererPerfCounter.ResolveWriteBytes ? "bytes" : "count";
}
