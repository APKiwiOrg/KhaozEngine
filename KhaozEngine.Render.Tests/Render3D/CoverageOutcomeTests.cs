using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageOutcomeTests
{
    [Fact]
    public void Failed_preview_construction_releases_its_precreated_completion_fences()
    {
        var device = new SubmissionTestDevice(0) { AllowCommandList = true };
        Assert.Throws<NotSupportedException>(() => new Render3DPreview(device, 32, 24));
        Assert.Equal(3, device.Fences.Count);
        Assert.All(device.Fences, fence => Assert.Equal(1, fence.DisposeCalls));
        Assert.Equal(1, device.WaitCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scene_registration_is_independent_of_zero_gpu_rows_and_cpu_fallback(bool fallback)
    {
        using var host = new CoverageSceneFixture(supportsCompute: false);
        host.Begin();
        if (fallback)
        {
            var frame = host.Stage();
            host.Scene.DrawSkinned(host.HostDraw(frame), host.Palette);
            host.SubmitEquipment();
        }
        host.Render();
        using var submission = new GpuFrameSubmission(host.Capture, consumerCapacity: 1);
        submission.Begin();
        host.Scene.RecordCoverageReadbacks(host.Record, submission);
        // The scene participant owns this slot even with no compute result copy to retain.
        Assert.False(submission.TryRegister(new GpuFrameSubmissionTests.Participant()));
        host.Scene.RecordCoverageReadbacks(host.Record, submission);
        submission.Submit(host.Record);
    }

    [Fact]
    public void Warm_submission_and_completed_outcome_decode_allocate_nothing()
    {
        var gpu = new SubmissionTestDevice(3);
        using var outcomes = new CoverageOutcomeReadbacks(gpu, 1);
        using var submission = new GpuFrameSubmission(gpu);
        using var source = new SubmissionTestBuffer(64);
        var identities = new[] { new CoverageOutcomeIdentity(100, 7, MotionKey.From(10), 55, "old-host")
            { Command0 = new(123, 42) } };
        Write(source, 0, identities[0]);
        submission.Begin();
        Assert.True(submission.TryRegister(outcomes));
        Assert.True(outcomes.Record(gpu, submission, source, identities));
        submission.Submit(gpu);
        ((SubmissionTestFence)gpu.SubmittedFence!).Signaled = true;
        Assert.True(outcomes.TryRead(out _));
        submission.Begin();
        Assert.True(submission.TryRegister(outcomes));

        using var region = AllocationRegion.Start("warm outcome record and read");
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool recorded = outcomes.Record(gpu, submission, source, identities);
        submission.Submit(gpu);
        ((SubmissionTestFence)gpu.SubmittedFence!).Signaled = true;
        bool read = outcomes.TryRead(out var outcome);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();

        Assert.True(recorded); Assert.True(read);
        Assert.Equal(100ul, outcome.FrameId);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void Preview_teardown_shares_the_owner_drain_with_scene_cleanup()
    {
        using var device = new FakeGpuDevice();
        var preview = new Render3DPreview(device, 32, 24);
        int before = device.WaitForIdleCalls;
        preview.Dispose();
        int previewDrains = device.WaitForIdleCalls - before;

        using var baselineDevice = new FakeGpuDevice();
        using var texture = baselineDevice.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 24,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var target = baselineDevice.Factory.CreateFramebuffer(null, texture);
        var baseline = new Scene3D(baselineDevice, target.Outputs);
        int baselineBefore = baselineDevice.WaitForIdleCalls;
        baseline.Dispose();
        // The host's owner replaces the general scene drain. Existing renderer cleanup drains still run.
        Assert.Equal(baselineDevice.WaitForIdleCalls - baselineBefore, previewDrains);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Teardown_in_either_order_retains_unsignaled_staging(bool consumerFirst)
    {
        var gpu = new SubmissionTestDevice(3);
        var outcomes = new CoverageOutcomeReadbacks(gpu, 1);
        using var submission = new GpuFrameSubmission(gpu);
        using var source = new SubmissionTestBuffer(64);
        var identity = new CoverageOutcomeIdentity(100, 7, MotionKey.From(10), 55, "old-host");
        submission.Begin();
        Assert.True(submission.TryRegister(outcomes));
        Assert.True(outcomes.Record(gpu, submission, source, [identity]));
        submission.Submit(gpu);
        var fence = gpu.SubmittedFence!;
        if (consumerFirst)
        {
            outcomes.Dispose();
            Assert.True(outcomes.HasPending(fence));
            Assert.All(gpu.Buffers, b => Assert.Equal(0, b.DisposeCalls));
            Assert.Equal(0, gpu.WaitCalls);
        }
        submission.Dispose();
        Assert.False(outcomes.HasPending(fence));
        outcomes.Dispose();
        Assert.All(gpu.Buffers, b => Assert.Equal(1, b.DisposeCalls));
        Assert.Equal(1, gpu.WaitCalls);
    }

    [Fact]
    public void Deferred_disposal_finishes_on_owner_poll_without_a_wait()
    {
        var gpu = new SubmissionTestDevice(3);
        var outcomes = new CoverageOutcomeReadbacks(gpu, 1);
        using var submission = new GpuFrameSubmission(gpu);
        using var source = new SubmissionTestBuffer(64);
        submission.Begin();
        Assert.True(submission.TryRegister(outcomes));
        Assert.True(outcomes.Record(gpu, submission, source, [new(100, 7, MotionKey.From(10), 55, "old-host")]));
        submission.Submit(gpu);
        outcomes.Dispose();
        ((SubmissionTestFence)gpu.SubmittedFence!).Signaled = true;
        submission.Begin();
        Assert.True(submission.TryRegister(outcomes)); submission.Abandon();
        Assert.All(gpu.Buffers, b => Assert.Equal(1, b.DisposeCalls));
        Assert.Equal(1, ((SubmissionTestFence)gpu.SubmittedFence).ResetCalls);
        Assert.Equal(0, gpu.WaitCalls);
    }

    [Fact]
    public void Corrupt_device_identity_reports_the_retained_actor_and_host()
    {
        var gpu = new SubmissionTestDevice(3);
        using var outcomes = new CoverageOutcomeReadbacks(gpu, 1);
        using var submission = new GpuFrameSubmission(gpu);
        using var source = new SubmissionTestBuffer(64);
        var identity = new CoverageOutcomeIdentity(100, 7, MotionKey.From(10), 55, "old-host");
        Write(source, 0, identity with { Actor = MotionKey.From(99) });
        submission.Begin();
        Assert.True(submission.TryRegister(outcomes));
        Assert.True(outcomes.Record(gpu, submission, source, [identity]));
        submission.Submit(gpu);
        ((SubmissionTestFence)gpu.SubmittedFence!).Signaled = true;
        Assert.True(outcomes.TryRead(out var outcome));
        Assert.Equal(MotionKey.From(10), outcome.Actor);
        Assert.Equal("old-host", outcome.Outcome.Failure.HostIdentity);
        Assert.Equal(CoverageFailureCode.IdentityMismatch, outcome.Outcome.Failure.Code);
    }

    [Fact]
    public void Abandon_or_reuse_never_relabels_an_old_actor()
    {
        var gpu = new SubmissionTestDevice(3);
        using var outcomes = new CoverageOutcomeReadbacks(gpu, 2);
        using var submission = new GpuFrameSubmission(gpu);
        using var source = new SubmissionTestBuffer(128);
        var identities = new[] { new CoverageOutcomeIdentity(100, 7, MotionKey.From(10), 55, "old-host") { Command0 = new(123, 42) },
            new CoverageOutcomeIdentity(100, 7, MotionKey.From(11), 56, "other-host") { Command0 = new(123, 42) } };
        Write(source, 0, identities[0]); Write(source, 1, identities[1]);
        submission.Begin();
        Assert.True(submission.TryRegister(outcomes));
        Assert.True(outcomes.Record(gpu, submission, source, identities));
        submission.Submit(gpu);
        Assert.False(outcomes.TryRead(out _));
        identities[0] = new(101, 8, MotionKey.From(99), 60, "replacement-host");
        ((SubmissionTestFence)gpu.SubmittedFence!).Signaled = true;
        Assert.True(outcomes.TryRead(out var first));
        Assert.Equal(100ul, first.FrameId);
        Assert.Equal(MotionKey.From(10), first.Actor);
        Assert.Equal("old-host", first.Outcome.Failure.HostIdentity);
        Assert.Equal(CoverageFailureCode.CapContact, first.Outcome.Failure.Code);
        Assert.Equal(123ul, first.Outcome.Failure.EquipmentInstanceId);
        Assert.Equal(42u, first.Outcome.Failure.CommandId);
        Assert.Equal(3, first.Outcome.Failure.Count);
        Assert.Equal(4, first.Outcome.Failure.Limit);
        submission.Begin();
        Assert.True(submission.TryRegister(outcomes));
        Write(source, 0, identities[0]);
        Assert.True(outcomes.Record(gpu, submission, source, identities.AsSpan(0, 1)));
        submission.Abandon();
        Assert.True(outcomes.TryRead(out var second));
        Assert.Equal(MotionKey.From(11), second.Actor);
        Assert.Equal(100ul, second.FrameId);
        Assert.False(outcomes.TryRead(out _));
    }

    internal static void Write(SubmissionTestBuffer source, int slot, CoverageOutcomeIdentity identity)
    {
        var words = MemoryMarshal.Cast<byte, uint>(source.Bytes.AsSpan()).Slice(slot * 16, 16);
        words.Clear();
        Pair(0, identity.FrameId); Pair(2, identity.ViewId); Pair(4, identity.Actor.Value); Pair(6, identity.Preparation);
        words[8] = (uint)CoverageFailureCode.CapContact;
        words[9] = 123; words[11] = 42; words[12] = 3; words[13] = 4; words[14] = 1;
        void Pair(int start, ulong value)
        {
            var target = MemoryMarshal.Cast<byte, uint>(source.Bytes.AsSpan()).Slice(slot * 16, 16);
            target[start] = (uint)value; target[start + 1] = (uint)(value >> 32);
        }
    }
}
