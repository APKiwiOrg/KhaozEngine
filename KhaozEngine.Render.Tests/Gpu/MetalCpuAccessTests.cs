using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalCpuAccessTests
{
    // #1682. No setup batch or final value exists to cover this held prefix, even after abandonment.
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task AnExplicitDrainOrReadMapWaitsForAnUnsignalledPrefix(int path, bool abandon)
    {
        using var h = new MetalRingHarness();
        MetalBuffer destination = h.NewBuffer(64, GpuBufferUsage.Staging);
        using var sourceTexture = h.NewTexture(new(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        using var stagingTexture = h.NewTexture(new(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
        using var list = h.NewList(new());
        list.EarlySubmission = new(new FakeMetalEarlyFrameApi(), h.SubmitLock);
        list.Begin();
        list.CopyBuffer(destination, 0, destination, 16, 16);
        list.CopyTexture(sourceTexture, stagingTexture);
        list.SetFramebuffer(Drawable());
        list.ClearColorTarget(0, KhaozEngine.Primitives.Color.Black);
        list.ResolveDeferredDrawable();
        if (abandon) list.Dispose();
        Assert.Empty(h.Event.Encoded);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishPrefix = new ManualResetEventSlim();
        var access = new MetalCpuAccess(h.Rings.CpuReads, () => false, h.Timeline.WaitForIdle, () =>
        {
            entered.SetResult();
            finishPrefix.Wait();
            Marshal.WriteByte(destination.Contents, 16, 9);
            Marshal.WriteByte(stagingTexture.StagingContents, 0, 9);
        }, h.Liveness);
        Task read = Task.Run(() =>
        {
            if (path != 0)
            {
                var data = path == 1 ? access.Map(destination, GpuMapMode.Read) : access.Map(stagingTexture, GpuMapMode.Read);
                Assert.Equal(9, Marshal.ReadByte(data.Data, path == 1 ? 16 : 0));
                h.Rings.CpuReads.Unmap(path == 1 ? destination : (object)stagingTexture);
            }
            else access.WaitForIdle();
        });
        try
        {
            await Task.WhenAny(entered.Task, read);
            Assert.True(entered.Task.IsCompleted, "CPU access returned without waiting for the committed prefix.");
            Assert.False(read.IsCompleted);
        }
        finally { finishPrefix.Set(); await read; }
        Assert.Equal(9, Marshal.ReadByte(destination.Contents, 16));
        Assert.Empty(h.Event.Encoded);
        Assert.Equal(MetalFrameAccessFailure.OverlappingWrite,
            Assert.Throws<MetalFrameAccessException>(() => h.Rings.CpuReads.RequireWritable(destination.CpuResource, 16, 1)).Reason);
        Assert.Equal(MetalFrameAccessFailure.AbandonedFrameSlot,
            Assert.Throws<MetalFrameAccessException>(() => h.Rings.CpuReads.RequireSlotAvailable(list.RingSegment)).Reason);
        h.DisposeWithoutRelease(destination);
    }

    // #1682. Final completion already covers the prefix. Ordinary frame drains must add no queue wait.
    [Fact]
    public void ACompletedFinalFrameNeedsNoAdditionalQueueDrain()
    {
        using var h = new MetalRingHarness();
        using var list = h.NewList(new());
        list.EarlySubmission = new(new FakeMetalEarlyFrameApi(), h.SubmitLock);
        list.Begin();
        list.SetFramebuffer(Drawable());
        list.End();
        h.Event.Completed = h.Submit(list);
        var access = new MetalCpuAccess(h.Rings.CpuReads, () => false, h.Timeline.WaitForIdle,
            () => throw new InvalidOperationException("Final completion already covers the queue."), h.Liveness);
        access.WaitForIdle();
        h.Rings.BeginRecording();
        Assert.Equal(h.Timeline.LastSubmitted, h.Event.Completed);
    }

    // #1682. Queue order lets any later final signal cover an abandoned prefix without releasing its slot.
    [Fact]
    public void ALaterFinalSubmissionCoversAnAbandonedPrefixWithoutAnotherQueueDrain()
    {
        using var h = new MetalRingHarness();
        using var abandoned = h.NewList(new());
        abandoned.EarlySubmission = new(new FakeMetalEarlyFrameApi(), h.SubmitLock);
        abandoned.Begin();
        int slot = abandoned.RingSegment;
        abandoned.SetFramebuffer(Drawable());
        abandoned.Dispose();
        using var later = h.NewList(new());
        later.Begin();
        later.End();
        h.Event.Completed = h.Submit(later);
        var access = new MetalCpuAccess(h.Rings.CpuReads, () => false, h.Timeline.WaitForIdle,
            () => throw new InvalidOperationException("A later completed final already covers this prefix."), h.Liveness);
        access.WaitForIdle();
        Assert.Equal(MetalFrameAccessFailure.AbandonedFrameSlot,
            Assert.Throws<MetalFrameAccessException>(() => h.Rings.CpuReads.RequireSlotAvailable(slot)).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADeviceLostBeforeOrDuringTheTimelineDrainNeverTouchesTheQueue(bool duringDrain)
    {
        using var h = new MetalRingHarness();
        if (!duringDrain) h.Liveness.MarkDead();
        var access = new MetalCpuAccess(h.Rings.CpuReads, () => true, h.Liveness.MarkDead,
            () => throw new InvalidOperationException("A dead device cannot drain a native queue."), h.Liveness);
        access.WaitForIdle();
        Assert.True(h.Liveness.IsDead);
    }

    // #1682. Setup flush is the controlled interval between lease registration and pointer handoff.
    [Fact]
    public async Task AUniformMappingLeasesThePhysicalSegmentItsPointerReturns()
    {
        using var h = new MetalRingHarness();
        MetalBuffer buffer = h.NewBuffer(64, GpuBufferUsage.UniformBuffer);
        h.Rings.BeginRecording();
        using var registered = new ManualResetEventSlim();
        using var rotated = new ManualResetEventSlim();
        var access = new MetalCpuAccess(h.Rings.CpuReads, () =>
        {
            registered.Set();
            rotated.Wait();
            return false;
        }, h.Timeline.WaitForIdle, () => { }, h.Liveness);
        Task rotate = Task.Run(() =>
        {
            registered.Wait();
            try { h.Rings.BeginRecording(); }
            finally { rotated.Set(); }
        });
        MappedData mapped = await Task.Run(() => access.Map(buffer, GpuMapMode.Write));
        await rotate;
        ulong physicalBase = (ulong)(mapped.Data.ToInt64() - buffer.Contents.ToInt64());
        var reads = new MetalRecordedReads();
        reads.Add(buffer.CpuResource, physicalBase, buffer.SizeInBytes);
        Assert.Equal(MetalFrameAccessFailure.MappedPrefix,
            Assert.Throws<MetalFrameAccessException>(() => h.Rings.CpuReads.Protect(0, reads)).Reason);
        h.Rings.CpuReads.Unmap(buffer);
        h.Rings.CpuReads.Protect(0, reads);
        h.DisposeWithoutRelease(buffer);
    }

    static MetalSwapchainFramebuffer Drawable()
    {
        MetalSwapchainFramebuffer? fb = null;
        fb = new(GpuPixelFormat.R8G8B8A8UNorm, new(16, 16),
            () => fb!.Adopt(new(new(0x777), GpuPixelFormat.R8G8B8A8UNorm), new(16, 16)));
        return fb;
    }

}
