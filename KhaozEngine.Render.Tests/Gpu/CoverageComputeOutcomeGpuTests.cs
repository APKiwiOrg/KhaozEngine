using System;
using System.Collections.Generic;
using System.Linq;
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
        using var second = new CoverageOutcomeNativeHost(device, actorIndexOffset: 2);
        using var commands = device.Factory.CreateCommandList();
        using var submission = new GpuFrameSubmission(device);
        first.Stage(); second.Stage();
        submission.Begin();
        using (GpuRecording.Open(device, commands, "Coverage.Outcomes"))
        {
            first.Record(commands, submission);
            second.Record(commands, submission);
        }
        var expectedFirst = first.Actors.Select(CoverageOutcomeExpected.Capture).ToArray();
        var expectedSecond = second.Actors.Select(CoverageOutcomeExpected.Capture).ToArray();
        int submitted = 0;
        Assert.Equal(CoveragePresentation.Dispatched, first.Scene.GetCoverageOutcome(first.Actors[0].Frame).Presentation);
        Assert.False(first.Scene.TryReadCoverageOutcome(out _));
        submission.Submit(commands);
        submitted++;
        first.Scene.Begin(); second.Scene.Begin();
        // Assertion boundary only. D3D11 needs one explicit drain before the one successful poll.
        device.WaitForIdle();
        Assert.True(first.Scene.TryReadCoverageOutcome(out var read));
        expectedFirst[0].AssertMatches(read);
        AssertResult(read, refused: false);
        Assert.Equal(CoveragePresentation.Covered, read.Outcome.Presentation);
        Assert.True(first.Scene.TryReadCoverageOutcome(out var refusal));
        expectedFirst[1].AssertMatches(refusal);
        AssertResult(refusal, refused: true);
        Assert.Equal(MotionKey.From(11), refusal.Actor);
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, refusal.Outcome.Failure.Code);
        Assert.Equal(CoveragePresentation.UncoveredFallback, refusal.Outcome.Presentation);
        Assert.Equal(first.Actors[1].Topology.HostIdentity, refusal.Outcome.Failure.HostIdentity);
        Assert.False(first.Scene.TryReadCoverageOutcome(out _));
        Assert.True(second.Scene.TryReadCoverageOutcome(out var secondRead));
        expectedSecond[0].AssertMatches(secondRead);
        AssertResult(secondRead, refused: false);
        Assert.Equal(MotionKey.From(12), secondRead.Actor);
        Assert.Equal(CoveragePresentation.Covered, secondRead.Outcome.Presentation);
        Assert.True(second.Scene.TryReadCoverageOutcome(out var secondRefusal));
        expectedSecond[1].AssertMatches(secondRefusal);
        AssertResult(secondRefusal, refused: true);
        Assert.Equal(MotionKey.From(13), secondRefusal.Actor);
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, secondRefusal.Outcome.Failure.Code);
        Assert.False(second.Scene.TryReadCoverageOutcome(out _));
        string path = CoverageOutcomeEvidence.Write(device, "two-scenes", new { HostFrameSubmissions = submitted },
            expectedFirst[0].Observe("first", read), expectedFirst[1].Observe("first", refusal),
            expectedSecond[0].Observe("second", secondRead), expectedSecond[1].Observe("second", secondRefusal));
        output.WriteLine($"{device.Backend} {device.Capabilities.DeviceName}: two scenes, one submitted list, native covered and refused outcomes after Begin, immutable frame/view/actor/host attribution.");
        output.WriteLine($"Decoded native outcomes: {path}");
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
        var expected = new List<CoverageOutcomeExpected>();
        var presentations = new List<string>();
        int submitted = 0;
        for (int i = 0; i < 4; i++)
        {
            host.Stage(); submission.Begin();
            Assert.True(submission.TryRegister(participant));
            using (GpuRecording.Open(device, commands, "Coverage.FullOutcomes")) host.Record(commands, submission);
            var presentation = host.Scene.GetCoverageOutcome(host.Actors[0].Frame).Presentation;
            Assert.Equal(CoveragePresentation.Dispatched, presentation);
            presentations.Add(presentation.ToString());
            expected.AddRange(host.Actors.Select(CoverageOutcomeExpected.Capture));
            submission.Submit(commands);
            submitted++;
        }
        Assert.Equal(4, participant.Submissions);
        Assert.Equal(0, participant.Abandons);
        device.WaitForIdle();
        var rows = new List<CoverageOutcomeObservation>();
        while (host.Scene.TryReadCoverageOutcome(out var read))
        {
            Assert.InRange(read.FrameId, 1ul, 3ul);
            var identity = expected.Single(item => item.FrameId == read.FrameId && item.Actor == read.Actor);
            identity.AssertMatches(read);
            AssertResult(read, refused: identity.Actor == MotionKey.From(11));
            rows.Add(identity.Observe("scene", read));
        }
        Assert.Equal(6, rows.Count);
        int diagnosticFrames = rows.Select(row => row.Value.FrameId).Distinct().Count();
        Assert.Equal(3, diagnosticFrames);
        string path = CoverageOutcomeEvidence.Write(device, "full-ring", new
        {
            HostFrameSubmissions = submitted,
            DiagnosticFrames = diagnosticFrames,
            ParticipantSubmitted = participant.Submissions,
            ParticipantAbandoned = participant.Abandons,
            RecordedPresentations = presentations,
        }, rows.ToArray());
        output.WriteLine($"{device.Backend}: four rendered submissions, three diagnostic frames, six actual GPU actor results, four participant Submitted callbacks.");
        output.WriteLine($"Decoded native outcomes: {path}");
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
        var expectedBefore = host.Actors.Select(CoverageOutcomeExpected.Capture).ToArray();
        int submitted = 0;
        submission.Submit(commands);
        submitted++;
        device.WaitForIdle();
        Assert.True(host.Scene.TryReadCoverageOutcome(out var first));
        expectedBefore[0].AssertMatches(first);
        AssertResult(first, refused: false);
        Assert.Equal(MotionKey.From(10), first.Actor);
        host.Scene.Begin();
        host.Actors[1].Dispose();
        host.Actors[1] = new(host.Scene, 2, false, false);
        using var extra = new CoveragePullingTestScene.Actor(host.Scene, 3, false, false);
        host.Stage(begin: false, prepare: false); extra.Draw(true, false, outlines: false);
        host.Scene.PrepareFrame(32, 24);
        uint newCapacity = host.Scene.CoverageGpuBatchForTests!.Results.SizeInBytes;
        Assert.True(newCapacity > oldCapacity);
        submission.Begin();
        using (GpuRecording.Open(device, commands, "Coverage.AfterGrowth")) host.Record(commands, submission);
        var expectedAfter = host.Actors.Append(extra).Select(CoverageOutcomeExpected.Capture).ToArray();
        submission.Submit(commands);
        submitted++;
        // The partially enumerated old result remains readable before draining the new frame.
        Assert.True(host.Scene.TryReadCoverageOutcome(out var old));
        expectedBefore[1].AssertMatches(old);
        AssertResult(old, refused: true);
        Assert.Equal(1ul, old.FrameId); Assert.Equal(MotionKey.From(11), old.Actor);
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, old.Outcome.Failure.Code);
        device.WaitForIdle();
        Assert.True(host.Scene.TryReadCoverageOutcome(out var replacement));
        expectedAfter[0].AssertMatches(replacement);
        AssertResult(replacement, refused: false);
        Assert.Equal(2ul, replacement.FrameId); Assert.Equal(MotionKey.From(10), replacement.Actor);
        Assert.True(host.Scene.TryReadCoverageOutcome(out var reused));
        expectedAfter[1].AssertMatches(reused);
        AssertResult(reused, refused: false);
        Assert.Equal(MotionKey.From(12), reused.Actor);
        Assert.True(host.Scene.TryReadCoverageOutcome(out var added));
        expectedAfter[2].AssertMatches(added);
        AssertResult(added, refused: false);
        Assert.Equal(MotionKey.From(13), added.Actor);
        Assert.False(host.Scene.TryReadCoverageOutcome(out _));
        string path = CoverageOutcomeEvidence.Write(device, "cold-growth", new
        {
            HostFrameSubmissions = submitted,
            BeforeResultBufferBytes = oldCapacity,
            AfterResultBufferBytes = newCapacity,
            RemovedActor = expectedBefore[1].Actor.Value,
            ReplacementActor = expectedAfter[1].Actor.Value,
        }, expectedBefore[0].Observe("scene", first), expectedBefore[1].Observe("scene", old),
            expectedAfter[0].Observe("scene", replacement), expectedAfter[1].Observe("scene", reused),
            expectedAfter[2].Observe("scene", added));
        output.WriteLine($"{device.Backend}: old partially enumerated metadata survived Begin, conformer disposal, actor-slot reuse and cold capacity growth. New generation outcomes retain their own identities.");
        output.WriteLine($"Decoded native outcomes: {path}");
    }

    static void AssertResult(CoverageGpuOutcome actual, bool refused)
    {
        Assert.Equal(refused ? CoveragePresentation.UncoveredFallback : CoveragePresentation.Covered, actual.Outcome.Presentation);
        Assert.Equal(refused ? CoverageFailureCode.UnsupportedTransform : CoverageFailureCode.None, actual.Outcome.Failure.Code);
    }
}

internal sealed class CoverageOutcomeNativeHost : IDisposable
{
    readonly IGpuTexture texture;
    readonly IGpuFramebuffer target;
    internal readonly Scene3D Scene;
    internal readonly CoveragePullingTestScene.Actor[] Actors;

    internal CoverageOutcomeNativeHost(IGpuDevice device, int actorIndexOffset = 0)
    {
        texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 24, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        target = device.Factory.CreateFramebuffer(null, texture);
        Scene = new(device, target.Outputs) { FrustumCulling = false };
        Scene.Post.Starfield = Scene.Post.Sky.Enabled = Scene.Post.Atmosphere.Enabled = false;
        Scene.Post.Outline = Scene.Post.Hdr.Enabled = Scene.Post.Bloom.Enabled = false;
        Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
        Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        Actors = [new(Scene, actorIndexOffset, false, false), new(Scene, actorIndexOffset + 1, true, false)];
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
