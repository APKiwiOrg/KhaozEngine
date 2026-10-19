using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class CoverageComputeOutcomeGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Two_scenes_share_one_submission_and_report_original_gpu_identities_after_begin()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        using var first = new CoverageOutcomeNativeHost(device);
        using var second = new CoverageOutcomeNativeHost(device);
        using var commands = device.Factory.CreateCommandList();
        using var submission = new GpuFrameSubmission(device);
        first.Stage(); second.Stage();
        submission.Begin();
        using (GpuRecording.Open(device, commands, "Coverage.Outcomes"))
        {
            first.Record(commands, submission);
            second.Record(commands, submission);
        }
        var old = first.Actors[0].Frame.Preparation!;
        ulong frameId = old.Generation, viewId = old.View;
        var actor = old.Actor;
        Assert.Equal(CoveragePresentation.Dispatched, first.Scene.GetCoverageOutcome(first.Actors[0].Frame).Presentation);
        Assert.False(first.Scene.TryReadCoverageOutcome(out _));
        submission.Submit(commands);
        first.Scene.Begin(); second.Scene.Begin();
        // Assertion boundary only. D3D11 needs one explicit drain before the one successful poll.
        device.WaitForIdle();
        Assert.True(first.Scene.TryReadCoverageOutcome(out var read));
        Assert.Equal(frameId, read.FrameId); Assert.Equal(viewId, read.ViewId); Assert.Equal(actor, read.Actor);
        Assert.Equal(CoveragePresentation.Covered, read.Outcome.Presentation);
        Assert.True(first.Scene.TryReadCoverageOutcome(out var refusal));
        Assert.Equal(MotionKey.From(11), refusal.Actor);
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, refusal.Outcome.Failure.Code);
        Assert.Equal(CoveragePresentation.UncoveredFallback, refusal.Outcome.Presentation);
        Assert.Equal(first.Actors[1].Topology.HostIdentity, refusal.Outcome.Failure.HostIdentity);
        Assert.False(first.Scene.TryReadCoverageOutcome(out _));
        Assert.True(second.Scene.TryReadCoverageOutcome(out _));
        Assert.True(second.Scene.TryReadCoverageOutcome(out _));
        Assert.False(second.Scene.TryReadCoverageOutcome(out _));
        output.WriteLine($"{device.Backend} {device.Capabilities.DeviceName}: two scenes, one submitted list, native covered and refused outcomes after Begin, immutable frame/view/actor/host attribution.");
    }

    [GpuFact]
    public void Full_rings_drop_diagnostics_while_submission_participants_continue()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        using var host = new CoverageOutcomeNativeHost(device);
        using var commands = device.Factory.CreateCommandList();
        using var submission = new GpuFrameSubmission(device);
        var participant = new GpuFrameSubmissionTests.Participant();
        for (int i = 0; i < 4; i++)
        {
            host.Stage(); submission.Begin();
            Assert.True(submission.TryRegister(participant));
            using (GpuRecording.Open(device, commands, "Coverage.FullOutcomes")) host.Record(commands, submission);
            Assert.Equal(CoveragePresentation.Dispatched, host.Scene.GetCoverageOutcome(host.Actors[0].Frame).Presentation);
            submission.Submit(commands);
        }
        Assert.Equal(4, participant.Submissions);
        device.WaitForIdle();
        int count = 0;
        while (host.Scene.TryReadCoverageOutcome(out var read))
        {
            Assert.InRange(read.FrameId, 1ul, 3ul);
            count++;
        }
        Assert.Equal(6, count);
        output.WriteLine($"{device.Backend}: four rendered submissions, three diagnostic frames, six actual GPU actor results, four participant Submitted callbacks.");
    }

    [GpuFact]
    public void Partial_enumeration_and_cold_growth_keep_old_actor_metadata_and_rings()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        using var host = new CoverageOutcomeNativeHost(device);
        using var commands = device.Factory.CreateCommandList();
        using var submission = new GpuFrameSubmission(device);
        host.Stage(); submission.Begin();
        uint oldCapacity = host.Scene.CoverageGpuBatchForTests!.Results.SizeInBytes;
        using (GpuRecording.Open(device, commands, "Coverage.BeforeGrowth")) host.Record(commands, submission);
        submission.Submit(commands);
        device.WaitForIdle();
        Assert.True(host.Scene.TryReadCoverageOutcome(out var first));
        Assert.Equal(MotionKey.From(10), first.Actor);
        host.Scene.Begin();
        host.Actors[1].Dispose();
        host.Actors[1] = new(host.Scene, 2, false, false);
        using var extra = new CoveragePullingTestScene.Actor(host.Scene, 3, false, false);
        host.Stage(begin: false, prepare: false); extra.Draw(true, false, outlines: false);
        host.Scene.PrepareFrame(32, 24);
        Assert.True(host.Scene.CoverageGpuBatchForTests!.Results.SizeInBytes > oldCapacity);
        submission.Begin();
        using (GpuRecording.Open(device, commands, "Coverage.AfterGrowth")) host.Record(commands, submission);
        submission.Submit(commands);
        // The partially enumerated old result remains readable before draining the new frame.
        Assert.True(host.Scene.TryReadCoverageOutcome(out var old));
        Assert.Equal(1ul, old.FrameId); Assert.Equal(MotionKey.From(11), old.Actor);
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, old.Outcome.Failure.Code);
        device.WaitForIdle();
        Assert.True(host.Scene.TryReadCoverageOutcome(out var replacement));
        Assert.Equal(2ul, replacement.FrameId); Assert.Equal(MotionKey.From(10), replacement.Actor);
        Assert.True(host.Scene.TryReadCoverageOutcome(out var reused));
        Assert.Equal(MotionKey.From(12), reused.Actor);
        Assert.True(host.Scene.TryReadCoverageOutcome(out var added));
        Assert.Equal(MotionKey.From(13), added.Actor);
        Assert.False(host.Scene.TryReadCoverageOutcome(out _));
        output.WriteLine($"{device.Backend}: old partially enumerated metadata survived Begin, conformer disposal, actor-slot reuse and cold capacity growth. New generation outcomes retain their own identities.");
    }
}

internal sealed class CoverageOutcomeNativeHost : IDisposable
{
    readonly IGpuTexture texture;
    readonly IGpuFramebuffer target;
    internal readonly Scene3D Scene;
    internal readonly CoveragePullingTestScene.Actor[] Actors;

    internal CoverageOutcomeNativeHost(IGpuDevice device)
    {
        texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 24, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        target = device.Factory.CreateFramebuffer(null, texture);
        Scene = new(device, target.Outputs) { FrustumCulling = false };
        Scene.Post.Starfield = Scene.Post.Sky.Enabled = Scene.Post.Atmosphere.Enabled = false;
        Scene.Post.Outline = Scene.Post.Hdr.Enabled = Scene.Post.Bloom.Enabled = false;
        Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Actors = [new(Scene, 0, false, false), new(Scene, 1, true, false)];
    }

    internal void Stage(bool begin = true, bool prepare = true)
    {
        if (begin) Scene.Begin();
        foreach (var actor in Actors) actor.Draw(true, false, outlines: false);
        if (prepare) Scene.PrepareFrame(32, 24);
    }

    internal void Record(IGpuCommandList commands, GpuFrameSubmission submission)
    {
        Scene.RenderInternal(commands, 32, 24, target);
        Scene.RecordCoverageReadbacks(commands, submission);
    }

    public void Dispose()
    {
        foreach (var actor in Actors) actor.Dispose();
        Scene.Dispose(); target.Dispose(); texture.Dispose();
    }
}
