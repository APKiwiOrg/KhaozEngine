using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.Performance;

public sealed partial class GpuPerfTimestampRingTests
{
    [Fact]
    public void Software_opt_in_retains_the_same_timestamp_and_completion_lifecycle()
    {
        using var device = new GpuPerfTimestampTestDevice { Software = true };
        Assert.Throws<NotSupportedException>(() => new GpuPerfTimestampRing(device, 1, 2, (_, _) => { }));
        GpuPerfTimestampResult? result = null;
        using var ring = new GpuPerfTimestampRing(device, 1, 2, (_, value) => result = value, allowSoftware: true);
        using var submission = new GpuFrameSubmission(device);
        using var commands = new GpuPerfTimestampTestCommands();
        submission.Begin(); commands.Begin();
        Assert.True(ring.TryBegin(commands, submission, 0, 2, out var pool));
        commands.FinishTimestampsOnEnd(pool!, 1); commands.End(); submission.Submit(commands);
        Assert.Null(result);
        Assert.True(ring.HasPending(device.Fences[0]));
        device.WaitForIdle(); ring.PollCompleted();
        Assert.True(result!.Valid);
        Assert.True(ring.CompleteBlock());
        Assert.Equal(1, device.Submissions);
        Assert.Equal(1, device.Waits);
    }

    [Fact]
    public void Software_opt_in_never_bypasses_missing_timestamp_capability()
    {
        using var device = new GpuPerfTimestampTestDevice
        { Software = true, Features = new(false, true, supportsCompletionFences: true) { SupportsTimestamps = false } };
        Assert.Throws<NotSupportedException>(() => new GpuPerfTimestampRing(device, 1, 2, (_, _) => { }, allowSoftware: true));
    }
}
