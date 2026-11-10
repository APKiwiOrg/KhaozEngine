using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu.Performance;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageRecessionStageTimingTests
{
    [Fact]
    public void Only_carry_dispatches_receive_ordered_brackets_in_the_outer_frame_pool()
    {
        using var commands = new CoverageCarryTestDevice.Commands();
        using var pool = new Pool();
        var stages = new Dictionary<IGpuComputePipeline, int>();
        var timer = new CoverageRecessionStageTiming(stages);
        using var tally = new CommandTallyGpuCommandList(commands) { ComputeDispatchObserver = timer.Dispatch };
        commands.Begin(); timer.Begin(commands, pool);
        commands.SetComputeResourceSet(0, new CoverageCarryTestDevice.Set(default));
        using var unrelated = new CoverageCarryTestDevice.Pipeline(default);
        tally.SetComputePipeline(unrelated); tally.Dispatch(1, 1, 1);
        for (int stage = 0; stage < 5; stage++)
        {
            using var pipeline = new CoverageCarryTestDevice.Pipeline(default);
            stages.Add(pipeline, stage);
            tally.SetComputePipeline(pipeline); tally.Dispatch(1, 1, 1);
        }
        timer.Complete();
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, commands.Markers);
        var resolved = CoverageRecessionStageTiming.Resolve(new ulong[] { 0, 10, 20, 40, 60, 80, 110, 140, 180, 200, 250, 300 }, 1000);
        Assert.Equal(new[] { "Skin", "Transport", "Basis", "Gather", "Publish" }, resolved.Select(s => s.Stage));
        Assert.Equal(new double[] { 10, 20, 30, 40, 50 }, resolved.Select(s => s.GpuMicroseconds));
    }

    // An outer row exists to cancel a backend's per-marker cost against the baseline scene it is compared with.
    [Theory]
    [InlineData("carry-only-outer", "carry-only")]
    [InlineData("covered-scene-outer", "covered-scene")]
    public void Outer_layouts_write_the_baseline_marker_count_and_fewer_than_their_staged_twin(string outer, string staged)
    {
        uint baseline = MarkersWrittenInOneFrame("baseline-scene");
        Assert.Equal(baseline, MarkersWrittenInOneFrame(outer));
        Assert.True(MarkersWrittenInOneFrame(staged) > baseline);
    }

    static uint MarkersWrittenInOneFrame(string layout)
    {
        using var compute = new CoveragePublicationDevice();
        using var device = CoverageRecessionWarmAllocationTests.InertCostDevice(compute);
        using var driver = new CoverageRecessionCostMeasurement.Driver(device, new(4, 4), 1, layout, 1);
        driver.Frame(0, retain: false);
        uint written = Assert.Single(device.Pools, pool => pool.Begins != 0).Written;
        device.WaitForIdle(); driver.Harvest(Stopwatch.GetTimestamp() + 900 * Stopwatch.Frequency);
        return written;
    }

    sealed class Pool : IGpuTimestampPool
    {
        public uint Capacity => 12;
        public GpuTimestampStatus TryResolve(IGpuFence completion, Span<ulong> ticks, out double period)
            => throw new InvalidOperationException("Dispatch timing must not resolve a pool inside a measured frame.");
        public void Dispose() { }
    }
}
