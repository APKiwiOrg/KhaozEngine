using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Tests.Gpu.CoveragePerf;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuTimestampRefusalTests
{
    [Fact]
    public void BatchStateRefusalsCarryTheirCursorAndKeepTheOriginalCatch()
    {
        var state = new GpuTimestampPoolState(3);
        var idle = Assert.Throws<GpuTimestampStateException>(() => state.Write(0));
        Assert.IsAssignableFrom<InvalidOperationException>(idle);
        Assert.Equal(GpuTimestampRefusal.BatchNotRecording, idle.Reason);
        Assert.Equal("Write", idle.Command);
        state.Begin(3);
        var unresolved = Assert.Throws<GpuTimestampStateException>(() => state.Begin(2));
        Assert.Equal(GpuTimestampRefusal.UnresolvedBatch, unresolved.Reason);
        Assert.Equal(3u, unresolved.Count);
        var index = Assert.Throws<GpuTimestampStateException>(() => state.Write(1));
        Assert.Equal(GpuTimestampRefusal.IndexOutOfSequence, index.Reason);
        Assert.Equal(1u, index.Index);
        Assert.Equal(0u, index.ExpectedIndex);
        Assert.Equal(3u, index.Count);
        var prefix = Assert.Throws<GpuTimestampStateException>(() => state.ValidateTimestampEnd(2));
        Assert.Equal(GpuTimestampRefusal.TailPrefixIncomplete, prefix.Reason);
        Assert.Equal(0u, prefix.ExpectedIndex);
        Assert.Equal(2u, prefix.FinalIndex);
        Assert.Equal(3u, prefix.Count);
        var incomplete = Assert.Throws<GpuTimestampStateException>(state.End);
        Assert.Equal(GpuTimestampRefusal.IncompleteBatch, incomplete.Reason);
        Assert.Equal(0u, incomplete.WrittenCount);
        Assert.Equal(3u, incomplete.Count);
        var pending = Assert.Throws<GpuTimestampStateException>(state.Resolved);
        Assert.Equal(GpuTimestampRefusal.BatchNotEnded, pending.Reason);
        Assert.Equal("Resolved", pending.Command);
        Assert.Equal(3u, pending.Count);
        state.Write(0);
        state.Write(1);
        state.ValidateTimestampEnd(2);
        state.Write(2);
        state.End();
        state.Resolved();
    }

    [Fact]
    public void ForeignCompletionAndShortOutputAreDistinguishedBeforePolling()
    {
        using var rig = new D3D11TimestampEndFixture(false);
        rig.Start();
        rig.Arm();
        rig.List.End();
        using var metal = new MetalRingHarness();
        using var foreign = metal.Timeline.CreateFence();
        var ex = Assert.Throws<GpuResourceOwnershipArgumentException>(() => rig.Pool.TryResolve(foreign, new ulong[2], out _));
        Assert.IsAssignableFrom<ArgumentException>(ex);
        Assert.Equal(GpuResourceOwnershipRefusal.ForeignBackend, ex.Reason);
        Assert.Equal("completion", ex.ParamName);
        Assert.Equal(GpuBackendKind.Direct3D11Native, ex.ExpectedBackend);
        Assert.Equal(typeof(MetalGpuFence), ex.ActualType);
        using var own = rig.Fences.CreateFence();
        var output = Assert.Throws<GpuTimestampArgumentException>(() => rig.Pool.TryResolve(own, new ulong[1], out _));
        Assert.Equal(GpuTimestampRefusal.OutputTooSmall, output.Reason);
        Assert.Equal("ticks", output.ParamName);
        Assert.Equal(1, output.SuppliedCount);
        Assert.Equal(2u, output.RequiredCount);
        Assert.Equal(GpuTimestampStatus.Pending, rig.Pool.TryResolve(own, new ulong[2], out _));
    }

}
