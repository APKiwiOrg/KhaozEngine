using System;
using System.Reflection;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

public sealed class CoveragePerfTimestampEndTests
{
    [Fact]
    public void CompletedFrameProofRequiresTheOptionalInterface()
    {
        Type contract = CoveragePerfTimestampEndFixture.Contract;
        Assert.True(contract.IsPublic && contract.IsInterface);
        MethodInfo method = Assert.Single(contract.GetMethods());
        Assert.Equal("FinishTimestampsOnEnd", method.Name);
        Assert.Equal(typeof(void), method.ReturnType);
        Assert.Equal(new[] { typeof(IGpuTimestampPool), typeof(uint) },
            Array.ConvertAll(method.GetParameters(), parameter => parameter.ParameterType));

        using IGpuCommandList legacy = new NullGpuCommandList();
        Assert.False(contract.IsInstanceOfType(legacy));
        legacy.Begin();
        legacy.End();
        Assert.Throws<NotSupportedException>(() => legacy.BeginTimestamps(null!, 2));
    }

    [Fact]
    public void PublicTailArmsAnOrdinaryEndWithoutSubmitting()
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        rig.Start();
        int writes = rig.Queries.Writes;
        rig.Arm(rig.Pool, 2);
        Assert.Equal(writes, rig.Queries.Writes);
        rig.List.End();
        Assert.Equal(writes + 1, rig.Queries.Writes);
        using var fence = rig.Resources.Timeline.CreateFence();
        ulong[] ticks = [99, 99, 99];
        Assert.Equal(GpuTimestampStatus.Pending, rig.Pool.TryResolve(fence, ticks, out double period));
        Assert.Equal(new ulong[] { 99, 99, 99 }, ticks);
        Assert.Equal(0, period);
        Assert.Equal(0, rig.Commands.Submissions);
        fence.Arm(1);
        rig.Resources.Semaphore.Completed = 1;
        Assert.Equal(GpuTimestampStatus.Ready, rig.Pool.TryResolve(fence, ticks, out period));
        Assert.Equal(new ulong[] { 0, 1, 2 }, ticks);
        Assert.Equal(1, period);
    }
}
