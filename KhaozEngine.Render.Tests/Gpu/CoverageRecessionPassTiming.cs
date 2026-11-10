using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Cold-discovered scene callbacks and carry brackets in the existing retained frame pool.
/// Recording only writes markers and preallocated flags. Resolution belongs to completed batch harvest.</summary>
internal sealed class CoverageRecessionPassTiming(IReadOnlyDictionary<IGpuComputePipeline, int> stages) : IRenderPassObserver
{
    internal sealed record PassSample(RenderPassKey Pass, bool Executed, bool FusedMotion, uint BeginMarker, uint EndMarker,
        double InclusiveGpuMicroseconds, double ExclusiveGpuMicroseconds);
    readonly record struct Boundary(RenderPassKey Pass, bool Begin, bool Executed, uint Marker);
    readonly List<Boundary> layout = [];
    readonly RenderPassKey[] carry = CoverageRecessionCostMeasurement.DispatchSchedule.Select(s => new RenderPassKey("carry." + s)).ToArray();
    bool[][] executed = [], fused = [];
    IGpuCommandList? commands;
    IGpuTimestampPool? pool;
    int next, sample;
    uint markers;
    bool discovering;
    internal uint MarkerCount => checked(markers + 2);

    internal void Discover()
    { discovering = true; layout.Clear(); next = 0; markers = 0; }

    internal void Freeze(int capacity)
    {
        discovering = false;
        if (layout.Count == 0 || MarkerCount > Performance.GpuPerfTimestampRing.MaximumMarkerCount)
            throw new InvalidOperationException("The coverage pass layout is empty or exceeds the frame pool capacity.");
        executed = Enumerable.Range(0, capacity).Select(_ => new bool[layout.Count]).ToArray();
        fused = Enumerable.Range(0, capacity).Select(_ => new bool[layout.Count]).ToArray();
    }

    internal void BeginFrame(IGpuCommandList list, IGpuTimestampPool timestamps, int id)
    { commands = list; pool = timestamps; sample = id; next = 0; }

    internal void Complete()
    {
        if (next != layout.Count) throw new InvalidOperationException("The coverage pass layout changed after cold discovery.");
        commands = null; pool = null;
    }

    void Mark(RenderPassKey key, bool begin, bool runs)
    {
        if (discovering) { layout.Add(new(key, begin, runs, runs ? ++markers : markers)); next++; return; }
        if (commands is null || pool is null || next >= layout.Count || layout[next].Pass != key
            || layout[next].Begin != begin || layout[next].Executed != runs)
            throw new InvalidOperationException("The coverage pass layout changed after cold discovery.");
        executed[sample][next] = runs;
        fused[sample][next] = false;
        if (runs) commands.WriteTimestamp(pool, layout[next].Marker);
        next++;
    }

    internal void Dispatch(IGpuComputePipeline? pipeline, bool before)
    {
        if (pipeline is not null && stages.TryGetValue(pipeline, out int stage)) Mark(carry[stage], before, true);
    }
    public void BeginPass(IGpuCommandList list, RenderPassKey key) => Mark(key, true, true);
    public void EndPass(IGpuCommandList list, RenderPassKey key) => Mark(key, false, true);
    public void SkipPass(IGpuCommandList list, RenderPassKey key)
    { Mark(key, true, false); Mark(key, false, false); }
    public void FusedMotion(IGpuCommandList list, RenderPassKey key)
    {
        if (!discovering && next > 0 && layout[next - 1].Pass == key && layout[next - 1].Begin) fused[sample][next - 1] = true;
    }
    public void BeginCpuPhase(string id) { }
    public void EndCpuPhase(string id) { }

    internal PassSample[] Resolve(int id, IReadOnlyList<ulong> ticks, double period)
    {
        if (ticks.Count != MarkerCount) throw new InvalidOperationException("The coverage timestamp layout is incomplete.");
        var stack = new Stack<int>();
        var children = new double[layout.Count];
        var result = new List<PassSample>();
        for (int i = 0; i < layout.Count; i++)
        {
            if (layout[i].Begin) { stack.Push(i); continue; }
            int start = stack.Pop();
            if (layout[start].Pass != layout[i].Pass) throw new InvalidOperationException("Coverage pass scopes are unbalanced.");
            double inclusive = (ticks[(int)layout[i].Marker] - ticks[(int)layout[start].Marker]) * period / 1000;
            if (stack.TryPeek(out int parent)) children[parent] += inclusive;
            bool runs = executed[id][start];
            result.Add(new(layout[start].Pass, runs, fused[id][start], layout[start].Marker, layout[i].Marker, runs ? inclusive : 0,
                runs ? inclusive - children[start] : 0));
        }
        if (stack.Count != 0) throw new InvalidOperationException("Coverage pass scopes are unbalanced.");
        return result.ToArray();
    }
}
