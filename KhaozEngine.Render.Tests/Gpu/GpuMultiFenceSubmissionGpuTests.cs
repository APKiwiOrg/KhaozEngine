using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class GpuMultiFenceSubmissionGpuTests
{
    [GpuFact]
    public void Both_fences_signal_at_the_same_native_submission_completion()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        var device = context.GpuDevice;
        using var commands = device.Factory.CreateCommandList();
        using var first = device.Factory.CreateFence();
        using var second = device.Factory.CreateFence();
        commands.Begin(); commands.End();
        device.Submit(commands, new[] { first, second }.AsSpan());
        device.WaitForIdle();
        Assert.True(first.Signaled);
        Assert.True(second.Signaled);
        first.Reset();
        Assert.False(first.Signaled);
        Assert.True(second.Signaled);
    }
}
