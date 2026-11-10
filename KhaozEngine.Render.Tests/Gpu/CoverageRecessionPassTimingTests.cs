using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageRecessionPassTimingTests
{
    [Fact]
    public void Nested_gpu_intervals_exclude_children_and_keep_skipped_and_fused_motion_metadata()
    {
        using var commands = new CoverageCarryTestDevice.Commands();
        using var pool = new Pool();
        var timer = new CoverageRecessionPassTiming(new Dictionary<IGpuComputePipeline, int>());
        var parent = new RenderPassKey("parent");
        var child = new RenderPassKey("child", 2);
        var skipped = new RenderPassKey("skipped");
        timer.Discover(); Record(); timer.Complete(); timer.Freeze(1);
        timer.BeginFrame(commands, pool, 0); Record(); timer.Complete();
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, commands.Markers);
        var result = timer.Resolve(0, new ulong[] { 0, 10, 20, 50, 90, 120 }, 1000);
        Assert.Equal(3, result.Length);
        var p = Assert.Single(result, s => s.Pass == parent);
        Assert.Equal(80, p.InclusiveGpuMicroseconds); Assert.Equal(50, p.ExclusiveGpuMicroseconds);
        var c = Assert.Single(result, s => s.Pass == child);
        Assert.True(c.FusedMotion); Assert.True(c.Executed); Assert.Equal(30, c.ExclusiveGpuMicroseconds);
        var s = Assert.Single(result, s => s.Pass == skipped);
        Assert.False(s.Executed); Assert.Equal(0, s.InclusiveGpuMicroseconds); Assert.Equal(0, s.ExclusiveGpuMicroseconds);
        timer.BeginFrame(commands, pool, 0);
        Assert.Throws<InvalidOperationException>(() => timer.BeginPass(commands, child));

        void Record()
        {
            timer.BeginPass(commands, parent); timer.BeginPass(commands, child); timer.FusedMotion(commands, child);
            timer.EndPass(commands, child); timer.EndPass(commands, parent); timer.SkipPass(commands, skipped);
        }
    }

    sealed class Pool : IGpuTimestampPool
    {
        public uint Capacity => 8;
        public GpuTimestampStatus TryResolve(IGpuFence fence, Span<ulong> ticks, out double period)
            => throw new InvalidOperationException("Pass callbacks must never resolve inside a measured frame.");
        public void Dispose() { }
    }
}
