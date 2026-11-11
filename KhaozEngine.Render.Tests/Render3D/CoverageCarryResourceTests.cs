using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageCarryResourceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Surface_disposal_releases_every_buffer_before_rethrowing(int failures)
    {
        using var device = new SubmissionTestDevice(3);
        var surface = new CoverageCarrySurface(device, 1, 1, 1, 1, 1);
        IGpuBuffer[] buffers = [surface.Results, surface.Work, surface.Surface];
        var errors = Enumerable.Range(0, failures).Select(_ => new InvalidOperationException()).ToArray();
        var released = new HashSet<IGpuBuffer>();
        for (int i = 0; i < buffers.Length; i++)
        {
            var buffer = (SubmissionTestBuffer)buffers[i];
            Exception? error = i < failures ? errors[i] : null;
            buffer.OnDispose = () => { released.Add(buffer); if (error is not null) throw error; };
        }
        Exception? thrown = Record.Exception(surface.Dispose);
        Assert.All(buffers, buffer => Assert.Contains(buffer, released));
        Assert.True(surface.IsDisposed);
        if (failures == 1) Assert.Same(errors[0], thrown);
        else
        {
            var aggregate = Assert.IsType<AggregateException>(thrown);
            Assert.Equal(failures, aggregate.InnerExceptions.Count);
            Assert.All(errors, error => Assert.Contains(error, aggregate.InnerExceptions));
        }
        Assert.Null(Record.Exception(surface.Dispose));
    }

    [Fact]
    public void Acquire_never_returns_committed_or_retained_and_exhaustion_refuses()
    {
        using var f = new CoverageCarryGpuFixture();
        int previous = -1;
        for (int i = 0; i < f.Batch.SlotsPerActor; i++)
        {
            int slot = f.Stage();
            Assert.NotEqual(previous, slot);
            f.Batch.CommitSubmitted(null);
            previous = slot;
        }
        Assert.False(f.Batch.TryAcquireOutput(f.Actor, out _, out var failure));
        Assert.Equal(CoverageFailureCode.CapacityExceeded, failure.Code);
        Assert.Equal(f.Batch.SlotsPerActor, failure.Limit);
        Assert.Equal(previous, f.Batch.CommittedSlot(f.Actor));
    }

    [Fact]
    public void Abandon_preserves_committed_and_retained_ranges()
    {
        using var f = new CoverageCarryGpuFixture();
        int a = f.Stage(); f.Batch.CommitSubmitted(null);
        int b = f.Stage(); f.Batch.CommitSubmitted(null);
        var before = f.Batch.Surface(f.Actor, b, a);
        int pending = f.Stage(); f.Batch.AbandonRecording();
        Assert.Equal(b, f.Batch.CommittedSlot(f.Actor));
        Assert.Equal(before, f.Batch.Surface(f.Actor, b, a));
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out int reused, out _));
        Assert.Equal(pending, reused);
        f.Batch.AbandonRecording();
    }

    [Fact]
    public void Prior_surface_is_retained_until_the_readers_completion()
    {
        using var f = new CoverageCarryGpuFixture();
        var first = new CoverageCarryTestDevice.Fence();
        var second = new CoverageCarryTestDevice.Fence();
        int a = f.Stage(); f.Batch.CommitSubmitted(first);
        int b = f.Stage(); f.Batch.CommitSubmitted(second);
        first.Complete = true;
        f.Batch.PollCompleted();
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out int c, out _));
        Assert.NotEqual(a, c); Assert.NotEqual(b, c);
        f.Batch.AbandonRecording();
        second.Complete = true;
        f.Batch.PollCompleted();
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out int released, out _));
        Assert.Equal(a, released);
        Assert.Equal(b, f.Batch.CommittedSlot(f.Actor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Later_fence_releases_untracked_frames_and_consumer_retains_it_until_proof(bool signaled)
    {
        using var f = new CoverageCarryGpuFixture();
        int a = f.Stage(); f.Batch.CommitSubmitted(null);
        int b = f.Stage(); f.Batch.CommitSubmitted(null);
        var later = new CoverageCarryTestDevice.Fence();
        int c = f.Stage(); f.Batch.CommitSubmitted(later);
        IGpuFrameCompletionConsumer consumer = f.Batch;
        Assert.True(consumer.HasPending(later));
        consumer.DiscardUnsubmitted(new CoverageCarryTestDevice.Fence());
        Assert.True(consumer.HasPending(later));
        if (signaled) { later.Complete = true; f.Batch.PollCompleted(); }
        else consumer.ReleaseCompleted(later);
        Assert.False(consumer.HasPending(later));
        Assert.Equal(c, f.Batch.CommittedSlot(f.Actor));
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out int released, out _));
        Assert.Contains(released, new[] { a, b });
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Capability_or_pipeline_refusal_selects_ordinary_current_skin(bool compute, bool completion, bool pipeline)
    {
        using var f = new CoverageCarryGpuFixture(compute, completion, pipeline);
        bool acquired = f.Batch.TryAcquireOutput(f.Actor, out int slot, out var failure);
        bool staged = acquired && f.Stage(slot, out failure);
        Assert.False(staged);
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, failure.Code);
        Assert.Equal(-1, f.Batch.CommittedSlot(f.Actor));
        using var commands = new CoverageCarryTestDevice.Commands();
        commands.Begin(); f.Batch.Record(commands); commands.End();
        Assert.Empty(commands.Dispatches);
    }

    [Fact]
    public void Slot_count_uses_the_resolved_device_depth()
    {
        string? old = Environment.GetEnvironmentVariable("KE_VULKAN_FRAMES_IN_FLIGHT");
        try
        {
            Environment.SetEnvironmentVariable("KE_VULKAN_FRAMES_IN_FLIGHT", "7");
            using var device = new CoverageCarryTestDevice();
            using var retired = GpuRetireQueue.CreateFrameCounted(device, 3);
            using var batch = new CoverageCarryBatch(device, retired);
            batch.Reserve(1, 1, 1, 1);
            Assert.Equal(7, GpuRetireQueue.FramesInFlightFor(device));
            Assert.Equal(9, batch.SlotsPerActor);
        }
        finally { Environment.SetEnvironmentVariable("KE_VULKAN_FRAMES_IN_FLIGHT", old); }
    }

    [Fact]
    public void Cold_growth_preserves_submitted_buffer_through_abandoned_migration()
    {
        using var f = new CoverageCarryGpuFixture();
        int a = f.Stage(); f.Batch.CommitSubmitted(null);
        var submitted = f.Batch.Surface(f.Actor, a, -1);
        var t = f.Actor.Topology;
        f.Batch.Reserve(4, t.SourceVertexCount * 4, t.VertexCount * 4, t.TriangleCount * 4);
        Assert.Equal(submitted, f.Batch.Surface(f.Actor, a, -1));
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out int pending, out _));
        Assert.True(f.Stage(pending, out _));
        using var commands = new CoverageCarryTestDevice.Commands();
        commands.Begin(); f.Batch.Record(commands); commands.End();
        var migrated = f.Batch.Surface(f.Actor, pending, a);
        Assert.NotSame(submitted.Current, migrated.Current);
        Assert.Contains(commands.Copies, copy => ReferenceEquals(copy.Source, submitted.Current)
            && ReferenceEquals(copy.Target, migrated.Current));
        f.Batch.AbandonRecording();
        Assert.Equal(submitted, f.Batch.Surface(f.Actor, a, -1));
        Assert.False(Assert.IsType<KhaozEngine.Tests.Gpu.FakeBuffer>(submitted.Current).Disposed);
    }

    [Fact]
    public void Failed_cold_growth_preserves_submitted_ranges_and_live_bindings()
    {
        using var f = new CoverageCarryGpuFixture();
        int first = f.Stage(); f.Batch.CommitSubmitted(null);
        var before = f.Batch.Surface(f.Actor, first, -1);
        var t = f.Actor.Topology;
        f.Device.ResourceSetFailureAfter = 2;
        Assert.Throws<InvalidOperationException>(() => f.Batch.Reserve(4, t.SourceVertexCount * 4, t.VertexCount * 4, t.TriangleCount * 4));
        Assert.Equal(before, f.Batch.Surface(f.Actor, first, -1));
        int next = f.Stage();
        Assert.Same(before.Current, f.Batch.Surface(f.Actor, next, first).Current);
        f.Batch.AbandonRecording();
    }

    [Fact]
    public void Growth_is_refused_while_a_command_list_is_recording()
    {
        using var f = new CoverageCarryGpuFixture();
        using var commands = new CoverageCarryTestDevice.Commands();
        using (GpuRecording.Open(f.Device, commands, "carry recording"))
            Assert.Throws<InvalidOperationException>(() => f.Batch.Reserve(4, 1000, 1000, 1000));
        f.Batch.Reserve(4, 1000, 1000, 1000);
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out _, out _));
    }

    [Fact]
    public void Precise_policy_refusal_takes_the_typed_ordinary_skin_path()
    {
        using var f = new CoverageCarryGpuFixture();
        f.Device.RefusePrecise = true;
        Assert.False(f.Batch.TryAcquireOutput(f.Actor, out _, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, failure.Code);
        Assert.Equal(-1, f.Batch.CommittedSlot(f.Actor));
    }

    [Theory]
    [InlineData("palette")]
    [InlineData("model")]
    [InlineData("commands")]
    [InlineData("entry")]
    [InlineData("slot")]
    public void Staging_requires_the_exact_accepted_snapshot(string changed)
    {
        using var f = new CoverageCarryGpuFixture();
        Assert.True(f.Batch.TryAcquireOutput(f.Actor, out int slot, out _));
        var palette = f.Actor.Request.Palette.ToArray();
        var commands = f.Actor.Uniforms.Commands.ToArray();
        var model = f.Actor.Request.Model;
        int entry = f.Actor.Request.EntryIndex, target = slot;
        if (changed == "palette") palette[0].M41 += 1;
        if (changed == "model") model.M41 += 1;
        if (changed == "commands") commands[0] = commands[0] with { TransitionWeight = .5f };
        if (changed == "entry") entry = (entry + 1) % f.Host.Table.EntryCount;
        if (changed == "slot") target++;
        Assert.False(f.Batch.TryStage(f.Actor, entry, target, palette, model, commands, out var failure));
        Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
        Assert.Equal(-1, f.Batch.CommittedSlot(f.Actor));
        Assert.True(f.Stage(slot, out _));
    }

    [Fact]
    public void Disposal_keeps_the_aggregate_consumer_until_retirement_proves_completion()
    {
        using var f = new CoverageCarryGpuFixture();
        var fence = new CoverageCarryTestDevice.Fence();
        int slot = f.Stage(); f.Batch.CommitSubmitted(fence);
        var range = f.Batch.Surface(f.Actor, slot, -1);
        f.Batch.Dispose();
        Assert.True(f.Batch.HasPending(fence));
        Assert.False(Assert.IsType<KhaozEngine.Tests.Gpu.FakeBuffer>(range.Current).Disposed);
        f.Retired.FlushAll();
        Assert.True(Assert.IsType<KhaozEngine.Tests.Gpu.FakeBuffer>(range.Current).Disposed);
        Assert.False(f.Batch.HasPending(fence));
    }

    [Fact]
    public void Warm_slot_staging_recording_and_completion_allocate_nothing()
    {
        using var f = new CoverageCarryGpuFixture();
        var fence = new CoverageCarryTestDevice.Fence();
        f.Stage(); f.Batch.CommitSubmitted(fence); fence.Complete = true; f.Batch.PollCompleted();
        using var commands = new CoverageCarryTestDevice.Commands { Capture = false };
        commands.Begin();
        bool acquired, staged;
        long bytes;
        using (AllocationRegion.Start("warm GPU carry submission storage"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            acquired = f.Batch.TryAcquireOutput(f.Actor, out int slot, out _);
            staged = f.Stage(slot, out _);
            f.Batch.Record(commands);
            f.Batch.CommitSubmitted(fence);
            f.Batch.PollCompleted();
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        commands.End();
        Assert.True(acquired && staged);
        Assert.Equal(0, bytes);
        Assert.Equal(0, f.Device.WaitForIdleCalls);
    }
}
