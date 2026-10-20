using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

[Collection("AllocSensitive")]
public sealed class VulkanTimestampEndTests
{
    [Fact]
    public void TailIsAfterClearFlushAndRestingLayoutRestoreBeforeNativeEnd()
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        rig.Start();
        using var target = rig.Resources.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var framebuffer = rig.Resources.Factory.CreateFramebuffer(null, target);
        rig.List.SetFramebuffer(framebuffer);
        rig.List.ClearColorTarget(0, Color.Black);
        rig.Arm(rig.Pool, 2);
        using var fence = rig.Resources.Timeline.CreateFence();
        rig.Commands.OnEnd = () =>
        {
            Assert.Equal(GpuTimestampStatus.Pending, rig.Pool.TryResolve(fence, new ulong[3], out _));
            rig.Resources.Trace.Add("BatchEnded");
        };
        rig.Resources.Trace.Clear();
        rig.List.End();
        string[] events = rig.Resources.Trace.Select(text => text.StartsWith("PipelineBarrier2(", StringComparison.Ordinal)
            ? "LayoutBarrier" : text.StartsWith("BeginRendering(", StringComparison.Ordinal) ? "ClearFlush" : text).ToArray();
        Assert.Equal(new[] { "LayoutBarrier", "ClearFlush", "EndRendering", "LayoutBarrier",
            "ReleaseMemory", "Write2", "BatchEnded", "NativeEnd" }, events);
    }

    [Fact]
    public void OrdinaryEndOrderingIsUnchangedWithoutATail()
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        rig.List.Begin();
        using var target = rig.Resources.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var framebuffer = rig.Resources.Factory.CreateFramebuffer(null, target);
        rig.List.SetFramebuffer(framebuffer);
        rig.List.ClearColorTarget(0, Color.Black);
        rig.Resources.Trace.Clear();
        rig.List.End();
        string[] events = rig.Resources.Trace.Select(text => text.StartsWith("PipelineBarrier2(", StringComparison.Ordinal)
            ? "LayoutBarrier" : text.StartsWith("BeginRendering(", StringComparison.Ordinal) ? "ClearFlush" : text).ToArray();
        Assert.Equal(new[] { "LayoutBarrier", "ClearFlush", "EndRendering", "LayoutBarrier",
            "ReleaseMemory", "NativeEnd" }, events);
        Assert.Equal(0, rig.Queries.Writes);
        Assert.Equal(0, rig.Queries.Resets);
        Assert.Equal(0, rig.Commands.Submissions);
    }

    [Fact]
    public void ForeignPoolWrongIndexAndDoubleArmAreRefused()
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        using var foreign = new CoveragePerfTimestampEndFixture();
        foreign.Start();
        rig.Start();
        var foreignError = Assert.Throws<ArgumentException>(() => rig.Arm(foreign.Pool, 2));
        Assert.Contains("another Vulkan device", foreignError.Message);
        var indexError = Assert.Throws<ArgumentOutOfRangeException>(() => rig.Arm(rig.Pool, 1));
        Assert.Contains("last index", indexError.Message);
        rig.Arm(rig.Pool, 2);
        var doubleError = Assert.Throws<InvalidOperationException>(() => rig.Arm(rig.Pool, 2));
        Assert.Contains("already armed", doubleError.Message);
        Assert.NotEqual(foreignError.Message, indexError.Message);
        Assert.NotEqual(indexError.Message, doubleError.Message);
        Assert.Equal(2, rig.Queries.Writes);
        rig.List.End();
        Assert.Equal(3, rig.Queries.Writes);
    }

    [Fact]
    public void UnwrittenEarlierIndexIsRefusedWithoutArming()
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        rig.List.Begin();
        rig.List.BeginTimestamps(rig.Pool, 3);
        rig.List.WriteTimestamp(rig.Pool, 0);
        var error = Assert.Throws<InvalidOperationException>(() => rig.Arm(rig.Pool, 2));
        Assert.Contains("earlier timestamp", error.Message);
        rig.List.WriteTimestamp(rig.Pool, 1);
        rig.Arm(rig.Pool, 2);
        rig.List.End();
        Assert.Equal(3, rig.Queries.Writes);
    }

    [Fact]
    public void ManualWriteAndEndForTheArmedPoolAreRefused()
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        rig.Start();
        rig.Arm(rig.Pool, 2);
        Assert.Contains("armed", Assert.Throws<InvalidOperationException>(
            () => rig.List.WriteTimestamp(rig.Pool, 2)).Message);
        Assert.Contains("armed", Assert.Throws<InvalidOperationException>(
            () => rig.List.EndTimestamps(rig.Pool)).Message);
        Assert.Equal(2, rig.Queries.Writes);
        using var other = rig.NewPool();
        rig.List.BeginTimestamps(other, 1);
        rig.List.WriteTimestamp(other, 0);
        rig.List.EndTimestamps(other);
        rig.List.End();
        Assert.Equal(4, rig.Queries.Writes);
    }

    [Fact]
    public void ArmingRequiresAnOpenBatchOnThisListAndRecording()
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        Assert.Contains("open command list", Assert.Throws<InvalidOperationException>(() => rig.Arm(rig.Pool, 2)).Message);
        rig.List.Begin();
        Assert.Throws<InvalidOperationException>(() => rig.Arm(rig.Pool, 2));
        using var other = rig.NewList();
        other.Begin();
        other.BeginTimestamps(rig.Pool, 3);
        other.WriteTimestamp(rig.Pool, 0);
        other.WriteTimestamp(rig.Pool, 1);
        Assert.Contains("current recording", Assert.Throws<InvalidOperationException>(() => rig.Arm(rig.Pool, 2)).Message);
        other.End();
        // Ring slots can repeat native handles. A batch from an earlier recording is still foreign.
        rig.List.End();
        rig.List.Begin();
        using var stale = rig.NewPool();
        rig.List.BeginTimestamps(stale, 2);
        rig.List.WriteTimestamp(stale, 0);
        rig.List.End();
        for (int i = 0; i < 2; i++) { rig.List.Begin(); rig.List.End(); }
        rig.List.Begin();
        Assert.Contains("current recording", Assert.Throws<InvalidOperationException>(() => rig.Arm(stale, 1)).Message);
        rig.List.End();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void FinalIndexUsesBatchCountAndSupportsSingleQueryBatches(int count)
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        rig.Start((uint)count);
        rig.Arm(rig.Pool, (uint)count - 1);
        rig.List.End();
        Assert.Equal(count, rig.Queries.Writes);
        using var fence = rig.Resources.Timeline.CreateFence();
        Assert.Equal(GpuTimestampStatus.Pending, rig.Pool.TryResolve(fence, new ulong[count], out _));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("write")]
    [InlineData("native-end")]
    public void AbandonedOrFailedRecordingCannotLeakTailIntoNextBegin(string failure)
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        rig.Start();
        rig.Arm(rig.Pool, 2);
        rig.Commands.FailRelease = failure == "release";
        rig.Commands.FailEnd = failure == "native-end";
        rig.Queries.FailWrite = failure == "write";
        Assert.Contains("Injected", Assert.Throws<InvalidOperationException>(() => rig.List.End()).Message);
        int writes = rig.Queries.Writes;
        rig.Commands.FailRelease = rig.Commands.FailEnd = rig.Queries.FailWrite = false;
        // Existing End retry semantics remain intact, but cannot repeat the armed marker.
        rig.List.End();
        Assert.Equal(writes, rig.Queries.Writes);
        using var fence = rig.Resources.Timeline.CreateFence();
        if (failure == "native-end")
            Assert.Equal(GpuTimestampStatus.Pending, rig.Pool.TryResolve(fence, new ulong[3], out _));
        else
            Assert.Throws<InvalidOperationException>(() => rig.Pool.TryResolve(fence, new ulong[3], out _));
        rig.List.Begin();
        Assert.Throws<InvalidOperationException>(() => rig.List.BeginTimestamps(rig.Pool, 3));
        using var replacement = rig.NewPool();
        rig.List.BeginTimestamps(replacement, 1);
        rig.Arm(replacement, 0);
        rig.List.End();
        Assert.Equal(writes + 1, rig.Queries.Writes);
    }

    [Fact]
    public void DisposingAnArmedRecordingLeavesItsPoolQuarantined()
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        rig.Start();
        rig.Arm(rig.Pool, 2);
        rig.List.Dispose();
        Assert.Equal(2, rig.Queries.Writes);
        Assert.Equal(0, rig.Commands.Ends);
        using var replacement = rig.NewList();
        replacement.Begin();
        Assert.Throws<InvalidOperationException>(() => replacement.BeginTimestamps(rig.Pool, 3));
        replacement.End();
        Assert.Equal(2, rig.Queries.Writes);
        Assert.Equal(1, rig.Queries.Resets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostDeviceSkipsTheTailAndKeepsTheNativeSealPath(bool loseDuringRelease)
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        rig.Start();
        rig.Arm(rig.Pool, 2);
        if (loseDuringRelease) rig.Commands.OnRelease = rig.Resources.Liveness.Kill;
        else rig.Resources.Liveness.Kill();
        rig.List.End();
        Assert.Equal(2, rig.Queries.Writes);
        Assert.Equal(1, rig.Commands.Ends);
        rig.List.Begin();
        rig.List.End();
        Assert.Equal(2, rig.Queries.Writes);
        Assert.Equal(2, rig.Commands.Ends);
        Assert.Equal(1, rig.Queries.Resets);
    }

    [Fact]
    public void ArmingDoesNotSubmitOrAllocatePerFrame()
    {
        using var rig = new CoveragePerfTimestampEndFixture();
        using var fence = rig.Resources.Timeline.CreateFence();
        fence.Arm(1);
        rig.Resources.Semaphore.Completed = 1;
        rig.Commands.Record = rig.Queries.Record = false;
        ulong[] ticks = new ulong[3];
        void Frames()
        {
            for (int i = 0; i < 6; i++)
            {
                rig.Start();
                rig.Arm(rig.Pool, 2);
                rig.List.End();
                if (rig.Pool.TryResolve(fence, ticks, out _) != GpuTimestampStatus.Ready)
                    throw new InvalidOperationException("The fixed headless batch must complete.");
            }
        }
        Frames();
        long allocated = AllocationMeasurement.Measure("six steady headless Vulkan timestamp tails", Frames);
        Assert.Equal(0, allocated);
        Assert.Equal(1, rig.Queries.Creates);
        Assert.Equal(36, rig.Queries.Writes);
        Assert.Equal(12, rig.Commands.Releases);
        Assert.Equal(12, rig.Commands.Ends);
        Assert.Equal(0, rig.Commands.Submissions);
        Assert.Empty(rig.Resources.CommandApi.Submissions);
        Assert.Equal(0, rig.Resources.Semaphore.WaitCount);
        Assert.Equal(0, rig.Resources.Timeline.TotalDrain.Count);
    }
}
