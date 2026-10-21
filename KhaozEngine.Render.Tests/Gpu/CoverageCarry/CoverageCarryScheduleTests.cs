using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageCarryScheduleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stage_markers_change_neither_uploads_nor_pipeline_resource_or_dispatch_order(bool nativeFloat)
    {
        using var device = new CoverageCarryLayoutDevice();
        using var carry = new CoverageCarryDevice(device, new CoverageCarryFixture(), 32, nativeFloat);
        using var outer = new CoverageCarryLayoutDevice.Commands();
        using var stages = new CoverageCarryLayoutDevice.Commands();
        using var pool = new Pool();
        carry.Prepare(97);
        outer.Begin(); carry.Record(outer); outer.End();
        carry.Prepare(97);
        stages.Begin(); carry.Record(stages, stagePool: pool); stages.End();
        Assert.Equal(2, outer.Uploads.Count);
        Assert.Equal(outer.Uploads.Count, stages.Uploads.Count);
        for (int i = 0; i < outer.Uploads.Count; i++)
        {
            Assert.Same(outer.Uploads[i].Buffer, stages.Uploads[i].Buffer);
            Assert.Equal(outer.Uploads[i].Offset, stages.Uploads[i].Offset);
            Assert.Equal(outer.Uploads[i].Words, stages.Uploads[i].Words);
        }
        Assert.Equal(4, outer.Dispatches.Count);
        Assert.Equal(outer.Dispatches, stages.Dispatches);
        Assert.Equal(outer.Groups, stages.Groups);
        Assert.Empty(outer.Markers);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (uint)i), stages.Markers);
    }

    sealed class Pool : IGpuTimestampPool
    {
        public uint Capacity => 10;
        public GpuTimestampStatus TryResolve(IGpuFence completion, Span<ulong> ticks, out double tickPeriodNanoseconds)
            => throw new NotSupportedException();
        public void Dispose() { }
    }
}
