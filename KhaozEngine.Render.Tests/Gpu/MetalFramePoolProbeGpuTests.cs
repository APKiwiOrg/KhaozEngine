using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Diagnostics;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class MetalFramePoolProbeGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Autoreleased_object_survives_bypassed_scope_and_deallocates_at_outer_drain()
    {
        if (!MetalDormancy.NativeDeviceAvailable(output)) return;
        int before = MetalPoolLifetimeNative.Deallocated;
        var counters = MetalFramePoolProbe.Counters;
        using (MetalFramePoolProbe.Enter(true))
        {
            IntPtr obj;
            using (ObjCAutoreleasePool.Enter()) obj = MetalPoolLifetimeNative.Autoreleased();
            Assert.Equal(before, MetalPoolLifetimeNative.Deallocated);
            Assert.StartsWith("KEPoolProbe_", ObjCRuntime.ClassNameOf(obj));
            using (MetalFramePoolProbe.Enter(true)) { using var local = ObjCAutoreleasePool.Enter(); }
        }
        Assert.Equal(before + 1, MetalPoolLifetimeNative.Deallocated);
        var end = MetalFramePoolProbe.Counters;
        Assert.Equal(1, end.NativePushCount - counters.NativePushCount);
        Assert.Equal(1, end.NativePopCount - counters.NativePopCount);
        Assert.Equal(2, end.BypassedLocalScopeCount - counters.BypassedLocalScopeCount);
        Assert.Equal(2, end.ExplicitFrameScopeCount - counters.ExplicitFrameScopeCount);
        output.WriteLine("Real NSObject dealloc witness fired once at the outer drain, not at the bypassed scope.");
    }

    [GpuFact]
    public void Standalone_nested_scopes_drain_normally_and_retained_results_survive_outer_drain()
    {
        if (!MetalDormancy.NativeDeviceAvailable(output)) return;
        int before = MetalPoolLifetimeNative.Deallocated;
        using (MetalFramePoolProbe.Enter(false))
        using (ObjCAutoreleasePool.Enter())
        {
            MetalPoolLifetimeNative.Autoreleased();
            using (ObjCAutoreleasePool.Enter()) MetalPoolLifetimeNative.Autoreleased();
            Assert.Equal(before + 1, MetalPoolLifetimeNative.Deallocated);
        }
        Assert.Equal(before + 2, MetalPoolLifetimeNative.Deallocated);
        IntPtr retained;
        using (MetalFramePoolProbe.Enter(true))
        {
            using var local = ObjCAutoreleasePool.Enter();
            retained = ObjCRuntime.ObjcRetain(MetalPoolLifetimeNative.Autoreleased());
        }
        Assert.Equal(before + 2, MetalPoolLifetimeNative.Deallocated);
        Assert.StartsWith("KEPoolProbe_", ObjCRuntime.ClassNameOf(retained));
        ObjCRuntime.ObjcRelease(retained);
        Assert.Equal(before + 3, MetalPoolLifetimeNative.Deallocated);
        output.WriteLine("Standalone inner and outer drains each deallocated one object. Explicit retain survived the frame drain.");
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_native_warmed_scopes_and_reads_allocate_zero_managed_bytes(bool enabled)
    {
        if (!MetalDormancy.NativeDeviceAvailable(output)) return;
        static MetalFramePoolCounters Run(bool mode)
        {
            using (MetalFramePoolProbe.Enter(mode)) { using var local = ObjCAutoreleasePool.Enter(); }
            return MetalFramePoolProbe.Counters;
        }
        Run(enabled);
        long bytes;
        var before = MetalFramePoolProbe.Counters;
        MetalFramePoolCounters end;
        using (AllocationRegion.Start("real native frame pool scope"))
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            end = Run(enabled);
            bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Assert.Equal(0, bytes);
        Assert.Equal(before.NativePushCount + 1, end.NativePushCount);
        Assert.Equal(before.NativePopCount + 1, end.NativePopCount);
    }

    [GpuTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Opaque_transparent_and_temporal_native_output_and_same_frame_capture_match(bool transparent, bool temporal)
    {
        if (!MetalDormancy.NativeDeviceAvailable(output)) return;
        using var gpu = GpuDeviceContext.CreateHeadless();
        Assert.Equal(GpuBackendKind.MetalNative, gpu.GpuDevice.Backend);
        byte[][] off = RendererInitializationGpuTests.Render(gpu.GpuDevice, true, transparent, temporal, true, true);
        var before = MetalFramePoolProbe.Counters;
        byte[][] on = RendererInitializationGpuTests.Render(gpu.GpuDevice, true, transparent, temporal, true, true, true);
        Assert.Equal(2, MetalFramePoolProbe.Counters.ExplicitFrameScopeCount - before.ExplicitFrameScopeCount);
        int zeroAlpha = 0, visibleAlpha = 0;
        for (int i = 0; i < off.Length; i++)
        {
            Assert.Equal(off[i], on[i]);
            Assert.NotEqual(on[i][..4], on[i][((20 * 64 + 32) * 4)..][..4]);
            for (int at = 3; at < on[i].Length; at += 4)
            {
                if (!transparent) Assert.Equal(255, on[i][at]);
                if (on[i][at] == 0) zeroAlpha++;
                else visibleAlpha++;
            }
        }
        Assert.True(visibleAlpha > 0);
        if (transparent) Assert.True(zeroAlpha > 0);
        Assert.Equal(20480, on[0].Length + on[1].Length);
        output.WriteLine($"Metal output and pre-present same-frame capture match for transparent={transparent}, temporal={temporal}. "
            + "Two 64x40 frames, 20480 RGBA bytes. Readbacks also succeeded after each outer pool drained.");
    }

    [GpuFact(RequiresCompletionFences = true)]
    public void Real_frame_history_clip_and_screenshot_lifecycle_survive_outer_pool_and_match_bytes()
    {
        if (!MetalDormancy.NativeDeviceAvailable(output)) return;
        var off = FrameHistoryRecorderGpuTests.RecordThroughALayerBackedDevice(output);
        var before = MetalFramePoolProbe.Counters;
        var on = FrameHistoryRecorderGpuTests.RecordThroughALayerBackedDevice(output, true);
        Assert.Equal(20, MetalFramePoolProbe.Counters.ExplicitFrameScopeCount - before.ExplicitFrameScopeCount);
        Assert.Equal(off.Screenshot, on.Screenshot);
        Assert.Equal(off.FirstClipFrame, on.FirstClipFrame);
        output.WriteLine($"Identical encoded screenshot {on.Screenshot.Length} bytes and first clip JPEG {on.FirstClipFrame.Length} bytes. "
            + "Both modes exercised the real layer, capture taps, fences, async encode, freeze and detach.");
    }
}
