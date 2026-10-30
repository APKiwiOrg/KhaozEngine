using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed class MetalRingWaitPolicyTests
{
    [Fact]
    public void Acquisition_reuses_the_two_existing_timestamp_sites_and_write_policy_is_unchanged()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var acquire = typeof(MetalRingAllocator).GetMethod("BeginRecordingWithWaitReceipt", flags)!;
        var write = typeof(MetalRingAllocator).GetMethod("WaitForCurrentSegmentUnderLock", flags)!;
        static bool Timestamp(MethodBase m) => m.DeclaringType == typeof(Stopwatch) && m.Name == nameof(Stopwatch.GetTimestamp);
        Assert.Equal(2, IlCallGraph.Callees(acquire).Count(Timestamp));
        Assert.Equal(2, IlCallGraph.Callees(write).Count(Timestamp));
        Assert.Single(IlCallGraph.Callees(acquire), m => m.DeclaringType == typeof(MetalTimeline) && m.Name == "WaitForValue");
        Assert.DoesNotContain(IlCallGraph.Callees(typeof(MetalCommandList).GetMethod(nameof(MetalCommandList.TryGetRecordingWait))!), Timestamp);
        Assert.DoesNotContain(IlCallGraph.Callees(typeof(MetalRingAllocator).GetMethod("TryReadRingWaitTotals", flags)!), Timestamp);
    }

    [Fact]
    public void Failed_end_invalidates_the_receipt_and_preserves_the_original_failure()
    {
        using var h = new MetalRingHarness();
        var calls = new FakeMetalEncoderCalls();
        using var list = h.NewList(new object(), calls: calls);
        var original = new InvalidOperationException("encoder end");
        list.Begin(); list.Encoders.EnsureBlitEncoder();
        Assert.True(list.TryGetRecordingWait(out _));
        calls.AfterEnd = _ => throw original;
        Assert.Same(original, Assert.Throws<InvalidOperationException>(list.End));
        Assert.False(list.TryGetRecordingWait(out _)); Assert.False(list.IsRecording);
    }

    [Fact]
    public void Warmed_native_scalar_receipts_and_coherent_snapshots_allocate_zero_bytes()
    {
        using var h = new MetalRingHarness();
        using var list = h.NewList(new object(), new ScalarBuffers());
        for (int i = 0; i < 16; i++) { list.Begin(); list.TryGetRecordingWait(out _); list.End(); h.Rings.TryReadRingWaitTotals(out _); }
        AllocationMeasurement.NoPerCallAllocation("native ring receipt bookkeeping and snapshot", () =>
        {
            for (int i = 0; i < 16; i++)
            { list.Begin(); list.TryGetRecordingAttempt(out _); list.TryGetRecordingWait(out _); list.End(); h.Rings.TryReadRingWaitTotals(out _); }
        });
    }
    sealed class ScalarBuffers : IMetalCommandBufferSource
    {
        int _next = 1;
        public IntPtr Acquire() => new(_next++);
        public void Release(IntPtr commandBuffer) { }
    }
}
