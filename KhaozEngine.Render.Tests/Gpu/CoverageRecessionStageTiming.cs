using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Dispatch-only brackets in the cost unit's existing pool. Resolution belongs to batch harvest.</summary>
internal sealed class CoverageRecessionStageTiming(IReadOnlyDictionary<IGpuComputePipeline, int> stages)
{
    internal const uint MarkerCount = 12;
    IGpuCommandList? commands;
    IGpuTimestampPool? pool;
    int next;
    internal void Begin(IGpuCommandList list, IGpuTimestampPool timestamps)
    { commands = list; pool = timestamps; next = 0; }
    internal void Dispatch(IGpuComputePipeline? pipeline, bool before)
    {
        if (pipeline is null || !stages.TryGetValue(pipeline, out int stage)) return;
        if (next != stage * 2 + (before ? 0 : 1) || commands is null || pool is null)
            throw new InvalidOperationException("The cost unit requires one ordered five-stage carry group.");
        commands.WriteTimestamp(pool, (uint)++next);
    }
    internal void Complete()
    {
        if (next != 10) throw new InvalidOperationException("The cost unit did not bracket every carry stage.");
        commands = null; pool = null;
    }
    internal sealed record StageSample(string Stage, double GpuMicroseconds);
    internal static StageSample[] Resolve(IReadOnlyList<ulong> ticks, double period)
        => ticks.Count == MarkerCount ? CoverageRecessionCostMeasurement.DispatchSchedule.Select((stage, i)
            => new StageSample(stage, (ticks[2 + i * 2] - ticks[1 + i * 2]) * period / 1000)).ToArray() : [];
}
