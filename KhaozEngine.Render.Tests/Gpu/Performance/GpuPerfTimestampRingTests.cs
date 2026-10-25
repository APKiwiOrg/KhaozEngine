using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu.CoveragePerf;
using Xunit;

namespace KhaozEngine.Tests.Gpu.Performance;

public sealed partial class GpuPerfTimestampRingTests
{
    [Fact]
    public void Retains_before_begin_and_never_reuses_unconsumed_fence()
    {
        using var rig = new Rig();
        var fence = rig.Device.Fences[0];
        rig.Device.Pools[0].OnBegin = () => Assert.True(rig.Ring.HasPending(fence));
        rig.OnPublish = (index, _) => Assert.True(rig.Ring.HasPending(rig.Device.Fences[index]));
        rig.Record(0);
        fence.Signaled = true;
        rig.Record(1);
        Assert.Equal(0, fence.ResetCalls);
        Assert.Equal(1, rig.Device.Pools[0].Begins);
        rig.Device.WaitForIdle();
        rig.Ring.PollCompleted();
        rig.Ring.PollCompleted();
        Assert.Equal(new[] { 1, 1, 0 }, rig.Device.Pools.Select(p => p.Resolves));
        Assert.Equal(new[] { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, rig.Publications);
        rig.Submission.Begin(); rig.Submission.Abandon();
        Assert.Equal(1, fence.ResetCalls);
    }

    [Fact]
    public void Full_ring_or_missing_sample_invalidates_block()
    {
        using (var full = new Rig())
        {
            full.Record(0); full.Record(1); full.Record(2);
            full.Submission.Begin(); full.Commands.Begin();
            Assert.False(full.Ring.TryBegin(full.Commands, full.Submission, 3, 2, out var pool));
            Assert.Null(pool);
            Assert.Contains("full", full.Ring.Failure, StringComparison.Ordinal);
            Assert.Equal(0, full.Device.Waits);
            Assert.Equal(3, full.Device.Submissions);
            Assert.All(full.Device.Pools, p => Assert.Equal(1, p.Begins));
        }
        using var missing = new Rig(sampleCapacity: 2);
        missing.Record(0);
        missing.Device.WaitForIdle(); missing.Ring.PollCompleted();
        Assert.False(missing.Ring.CompleteBlock());
        Assert.Contains("missing", missing.Ring.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(missing.Results[0]);
        Assert.Null(missing.Results[1]);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("invalid")]
    [InlineData("disjoint")]
    [InlineData("wrap")]
    [InlineData("order")]
    [InlineData("foreign")]
    public void Rejects_pending_invalid_disjoint_wrap_and_foreign_fence(string mode)
    {
        using var rig = new Rig(3);
        if (mode == "foreign")
        {
            using var other = new GpuPerfTimestampTestDevice();
            using var submission = new GpuFrameSubmission(other);
            submission.Begin(); rig.Commands.Begin();
            Assert.True(rig.Ring.TryBegin(rig.Commands, submission, 0, 3, out _));
            rig.Finish(3); submission.Submit(rig.Commands);
            other.WaitForIdle();
            var foreign = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(rig.Ring.PollCompleted).Failures);
            Assert.Equal("renderer.batch.complete", foreign.Stage);
            Assert.IsType<ArgumentException>(foreign.Error);
            Assert.True(rig.Ring.HasPending(other.Fences[0]));
        }
        else
        {
            rig.Record(0, 3);
            var pool = rig.Device.Pools[0];
            pool.Hold = mode == "pending";
            pool.Invalid = mode == "invalid";
            pool.Disjoint = mode == "disjoint";
            if (mode == "wrap") pool.RawCounters = [0, ulong.MaxValue, 0];
            if (mode == "order") pool.ElapsedTicks = [0, 20, 10];
            rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
            if (mode == "pending") Assert.Null(rig.Results[0]);
            else Assert.False(Assert.IsType<GpuPerfTimestampResult>(rig.Results[0]).Valid);
            Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
        }
        Assert.NotNull(rig.Ring.Failure);
        rig.Ring.PollCompleted();
        Assert.Equal(1, rig.Device.Pools[0].Resolves);
    }

    [Fact]
    public void Tail_contains_deferred_clear_and_native_finalization()
    {
        using var native = new CoveragePerfTimestampEndFixture();
        using var device = new GpuPerfTimestampTestDevice { PoolFactory = capacity => native.NewPool(capacity) };
        using var ring = new GpuPerfTimestampRing(device, 1, 2, (_, _) => { });
        using var submission = new GpuFrameSubmission(device);
        submission.Begin(); native.List.Begin();
        Assert.True(ring.TryBegin(native.List, submission, 0, 2, out var pool));
        using var target = native.Resources.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var framebuffer = native.Resources.Factory.CreateFramebuffer(null, target);
        native.List.SetFramebuffer(framebuffer);
        native.List.ClearColorTarget(0, Color.Black);
        ((IGpuCommandListTimestampEnd)native.List).FinishTimestampsOnEnd(pool!, 1);
        native.List.End();
        var events = native.Resources.Trace.Where(e => e == "Write0" || e == "Write1" || e == "ReleaseMemory"
            || e == "NativeEnd" || e == "EndRendering" || e.StartsWith("BeginRendering(", StringComparison.Ordinal))
            .Select(e => e.StartsWith("BeginRendering(", StringComparison.Ordinal) ? "ClearFlush" : e);
        Assert.Equal(new[] { "Write0", "ClearFlush", "EndRendering", "ReleaseMemory", "Write1", "NativeEnd" }, events);
        Assert.Equal(0, native.Commands.Submissions);
        Assert.Equal(0, native.Resources.Semaphore.WaitCount);
        Assert.True(ring.HasPending(device.Fences[0]));
        submission.Abandon();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Partial_final_batches_publish_every_identity_once(int finalBatch)
    {
        using var rig = new Rig(sampleCapacity: 3 + finalBatch);
        for (int i = 0; i < 3; i++) rig.Record(i);
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        for (int i = 3; i < 3 + finalBatch; i++) rig.Record(i);
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted(); rig.Ring.PollCompleted();
        Assert.True(rig.Ring.CompleteBlock());
        Assert.All(rig.Publications, count => Assert.Equal(1, count));
        Assert.Equal(2, rig.Device.Waits);
        Assert.Equal(3 + finalBatch, rig.Device.Submissions);
        Assert.Equal(3 + finalBatch, rig.Device.Pools.Sum(p => p.Resolves));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(5u)]
    [InlineData(512u)]
    public void Layout_is_fixed_before_warming_and_never_grows(uint markerCount)
    {
        using var rig = new Rig(markerCount, 1);
        Assert.All(rig.Device.Pools, p => Assert.Equal(markerCount, p.Capacity));
        rig.Record(0, markerCount);
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        Assert.True(rig.Ring.CompleteBlock());
        Assert.Equal((int)markerCount, rig.Results[0]!.Ticks.Length);
        Assert.Equal(3, rig.Device.Pools.Count);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(513u)]
    public void Query_cap_is_refused_before_allocating_pools(uint markerCount)
    {
        using var device = new GpuPerfTimestampTestDevice();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuPerfTimestampRing(device, 1, markerCount, (_, _) => { }));
        Assert.Empty(device.Pools);
    }

    [Fact]
    public void Skipped_cached_pass_keeps_reserved_markers_and_unexpected_pass_invalidates_same_layout()
    {
        using var rig = new Rig(6, 2);
        rig.Begin(0, 6);
        var pool = rig.Ring.ActivePool!;
        // Slots 1 and 2 belong to a cached pass. Its adapter retains NotExecuted separately.
        rig.Commands.WriteTimestamp(pool, 1); rig.Commands.WriteTimestamp(pool, 2);
        rig.Commands.WriteTimestamp(pool, 3); rig.Commands.WriteTimestamp(pool, 4);
        rig.Commands.FinishTimestampsOnEnd(pool, 5); rig.Commands.End(); rig.Submission.Submit(rig.Commands);
        rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        Assert.Equal(new ulong[] { 0, 10, 20, 30, 40, 50 }, rig.Results[0]!.Ticks);
        rig.Begin(1, 6);
        rig.Ring.InvalidateBlock("Unexpected pass occurrence outside the frozen layout.");
        Assert.False(rig.Ring.CompleteBlock());
        Assert.Equal(new[] { 2, 0, 0 }, rig.Device.Pools.Select(p => p.Begins));
        Assert.All(rig.Device.Pools, p => Assert.Equal(6u, p.Capacity));
        Assert.Contains("Unexpected pass", rig.Ring.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 2u)]
    [InlineData(12, 2u)]
    [InlineData(0, 4u)]
    public void Invalid_identity_or_changed_layout_never_begins_a_pool(int sampleId, uint markerCount)
    {
        using var rig = new Rig();
        rig.Submission.Begin(); rig.Commands.Begin();
        Assert.False(rig.Ring.TryBegin(rig.Commands, rig.Submission, sampleId, markerCount, out var pool));
        Assert.Null(pool);
        Assert.NotNull(rig.Ring.Failure);
        Assert.All(rig.Device.Pools, p => Assert.Equal(0, p.Begins));
    }

    [Fact]
    public void Duplicate_identity_does_not_replace_published_evidence()
    {
        using var rig = new Rig();
        rig.Record(0); rig.Device.WaitForIdle(); rig.Ring.PollCompleted();
        var published = rig.Results[0];
        rig.Submission.Begin(); rig.Commands.Begin();
        Assert.False(rig.Ring.TryBegin(rig.Commands, rig.Submission, 0, 2, out _));
        Assert.Same(published, rig.Results[0]);
        Assert.Equal(1, rig.Publications[0]);
    }
}
