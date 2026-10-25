using System.Collections.Immutable;
using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed record MetalCounterSetMetadata(string Name, ImmutableArray<string> Counters);
internal sealed record MetalCounterMetadata(ImmutableArray<MetalCounterSetMetadata> Sets,
    ImmutableArray<uint> SamplingPoints, GpuDiagnosticStatus Status, string? Reason)
{
    internal GpuDiagnosticMetric? TimestampCapability { get; init; }
}

/// <summary>Optional enumeration seam. Advertising a counter never asserts that it was sampled.</summary>
internal interface IMetalCounterMetadataNative
{
    ImmutableArray<MetalCounterSetMetadata> ReadCounterSets();
    string? SampleBufferFailureReason { get; }
}

internal interface IMetalCounterSetReader
{
    nuint SetCount { get; }
    string SetName(nuint set);
    nuint CounterCount(nuint set);
    string CounterName(nuint set, nuint counter);
}

/// <summary>Bound native enumeration before allocation, preserving original names and refusing malformed metadata.</summary>
internal static class MetalCounterEnumeration
{
    internal const nuint MaximumSets = 64;
    internal const nuint MaximumCountersPerSet = 256;

    internal static ImmutableArray<MetalCounterSetMetadata> Read(IMetalCounterSetReader native)
    {
        nuint count = native.SetCount;
        if (count > MaximumSets) throw new InvalidOperationException("Metal counter set enumeration exceeds its fixed bound.");
        var result = ImmutableArray.CreateBuilder<MetalCounterSetMetadata>((int)count);
        for (nuint i = 0; i < count; i++)
        {
            nuint n = native.CounterCount(i);
            if (n > MaximumCountersPerSet) throw new InvalidOperationException("Metal counter enumeration exceeds its fixed bound.");
            var names = ImmutableArray.CreateBuilder<string>((int)n);
            for (nuint c = 0; c < n; c++) names.Add(RequireName(native.CounterName(i, c)));
            result.Add(new(RequireName(native.SetName(i)), names.ToImmutable()));
        }
        return result.ToImmutable();
    }

    static string RequireName(string name) => !string.IsNullOrEmpty(name) ? name
        : throw new InvalidOperationException("Metal returned an empty counter or counter-set name.");
}
