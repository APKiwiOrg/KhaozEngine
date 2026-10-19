using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class CoverageComputeVerificationGpuTests(ITestOutputHelper output)
{
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_selected_surface_matches_retained_nonidentity_poses_on_frames_three_and_six(bool refusal)
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        using var host = new CoverageOutcomeNativeHost(device);
        using var commands = device.Factory.CreateCommandList();
        // More retained fences isolate sampling from the intentionally unconsumed outcome ring.
        using var submission = new GpuFrameSubmission(device, retainedFrames: 12);
        var scene = host.Scene;
        int selected = refusal ? 1 : 0;
        var actor = host.Actors[selected];
        Assert.Null(scene.CoverageVerificationForTests);
        Stage(host, 1); submission.Begin();
        using (GpuRecording.Open(device, commands, "Coverage.Verification.DefaultOff")) host.Record(commands, submission);
        submission.Submit(commands);
        Assert.Null(scene.CoverageVerificationForTests);
        scene.ConfigureCoverageVerification(actor.Key, 3);
        scene.CoverageDiagnosticCompletionHeldForTests = true; // Poll visibility only, not a held native fence.
        var sampled = new List<(ulong Frame, uint[] Upload, uint[] Palette, Matrix4x4 Model)>();
        for (int frame = 2; frame <= 7; frame++)
        {
            Stage(host, frame);
            submission.Begin();
            var physical = scene.CoverageGpuBatchForTests!.Current;
            using (GpuRecording.Open(device, commands, "Coverage.Verification.Sample")) host.Record(commands, submission);
            var p = actor.Frame.Preparation!;
            if (frame % 3 == 0)
                sampled.Add((p.Generation, (uint[])p.Owner.Gpu.Frame.Clone(),
                    System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, uint>(p.Palette.AsSpan()).ToArray(), p.Model));
            Assert.Equal(CoveragePresentation.Dispatched, scene.GetCoverageOutcome(actor.Frame).Presentation);
            submission.Submit(commands);
            Assert.Same(physical, scene.CoverageGpuBatchForTests.Previous);
        }
        var verification = Assert.IsType<CoverageVerificationReadbacks>(scene.CoverageVerificationForTests);
        Assert.Equal(0, verification.ReferenceCalls);
        Assert.Equal(2, verification.PendingCount);
        Assert.Equal(new ulong[] { 3, 6 }, sampled.Select(s => s.Frame));
        // Replace the conformer and reuse its scene storage before consuming the retained poses.
        scene.Begin();
        actor.Dispose();
        host.Actors[selected] = new(scene, 2, false, false);
        device.WaitForIdle(); // Assertion boundary only. Production adds no drain or flush.
        var rows = new List<object>();
        foreach (var expected in sampled)
        {
            Assert.True(verification.TryRead(out var result));
            Assert.Equal(-1, result.Vertex);
            Assert.Equal(refusal, result.Refused);
            Assert.Equal(!refusal, result.ReferenceAccepted);
            Assert.Equal(refusal ? CoverageFailureCode.UnsupportedTransform : CoverageFailureCode.None, result.ReferenceFailure);
            Assert.Equal(expected.Frame, result.Pose.FrameId);
            Assert.Equal(actor.Key, result.Pose.Actor);
            Assert.Equal(expected.Upload, result.Pose.UploadWords);
            Assert.Equal(expected.Palette, System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, uint>(result.Pose.Palette.AsSpan()).ToArray());
            Assert.Equal(expected.Model, result.Pose.Model);
            Assert.NotEqual(Matrix4x4.Identity, result.Pose.Model);
            Assert.NotEqual(Matrix4x4.Identity, result.Pose.Palette[0]);
            rows.Add(Observe(result, verification.CompletedSurfaceWords));
        }
        Assert.False(verification.TryRead(out _));
        Assert.Equal(2, verification.ReferenceCalls);
        Write(device, refusal ? "refused-samples" : "covered-samples", rows,
            new { NativeFenceHeld = false, ActorReplacedBeforeConsumption = true, SampledFrames = new[] { 3, 6 } });
    }

    [GpuFact]
    public void Native_full_ring_skips_only_verification_and_disable_keeps_pending_staging_owned()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        using var host = new CoverageOutcomeNativeHost(device);
        using var commands = device.Factory.CreateCommandList();
        using var submission = new GpuFrameSubmission(device, retainedFrames: 12);
        var scene = host.Scene;
        scene.ConfigureCoverageVerification(host.Actors[0].Key, 1);
        scene.CoverageDiagnosticCompletionHeldForTests = true;
        var physical = new List<IGpuBuffer>();
        for (int frame = 1; frame <= 4; frame++)
        {
            Stage(host, frame); submission.Begin();
            using (GpuRecording.Open(device, commands, "Coverage.Verification.Full")) host.Record(commands, submission);
            physical.Add(scene.CoverageGpuBatchForTests!.Current);
            submission.Submit(commands);
            Assert.Same(physical[^1], scene.CoverageGpuBatchForTests.Previous);
            Assert.Equal(CoveragePresentation.Dispatched, scene.GetCoverageOutcome(host.Actors[0].Frame).Presentation);
        }
        var retained = scene.CoverageVerificationForTests!;
        Assert.Equal(3, retained.PendingCount);
        Assert.Equal(0, retained.ReferenceCalls);
        scene.ConfigureCoverageVerification(MotionKey.None, 0);
        Assert.Null(scene.CoverageVerificationForTests);
        Assert.False(retained.TryRead(out _));
        Stage(host, 5); submission.Begin();
        using (GpuRecording.Open(device, commands, "Coverage.Verification.Disabled")) host.Record(commands, submission);
        submission.Submit(commands);
        device.WaitForIdle();
        submission.Begin(); submission.Abandon();
        Assert.Equal(0, retained.PendingCount);
        Assert.Equal(0, retained.ReferenceCalls);
        scene.ConfigureCoverageVerification(host.Actors[1].Key, 1);
        Stage(host, 6); submission.Begin();
        using (GpuRecording.Open(device, commands, "Coverage.Verification.ActorBeforeChange")) host.Record(commands, submission);
        submission.Submit(commands);
        var replaced = scene.CoverageVerificationForTests!;
        scene.ConfigureCoverageVerification(host.Actors[0].Key, 1);
        scene.CoverageDiagnosticCompletionHeldForTests = false;
        Stage(host, 7); submission.Begin();
        using (GpuRecording.Open(device, commands, "Coverage.Verification.ActorAfterChange")) host.Record(commands, submission);
        submission.Submit(commands);
        var active = scene.CoverageVerificationForTests!;
        Assert.NotSame(replaced, active);
        device.WaitForIdle();
        Stage(host, 8); // Normal PrepareFrame polling executes the completed reference comparison.
        Assert.Equal(1, active.ReferenceCalls);
        Assert.Equal(0, replaced.ReferenceCalls);
        Assert.False(replaced.TryRead(out _));
        scene.ConfigureCoverageVerification(MotionKey.None, 0);
        Write(device, "full-ring-disable", [], new
        {
            RenderedSubmissions = 7,
            RetainedSamplesBeforeDisable = 3,
            ReferenceCallsAfterDisable = retained.ReferenceCalls,
            NativeFenceHeld = false,
            PhysicalHistoryRotationAssertions = physical.Count,
            ReconfiguredActorReferenceCalls = replaced.ReferenceCalls,
            AutomaticCompletionReferenceCalls = active.ReferenceCalls
        });
    }

    static void Stage(CoverageOutcomeNativeHost host, int frame)
    {
        host.Scene.Begin();
        for (int actor = 0; actor < host.Actors.Length; actor++)
        {
            var world = Matrix4x4.CreateScale(1.25f) * Matrix4x4.CreateTranslation(actor * 3 + frame * .03125f, .125f, 0);
            var palette = Matrix4x4.CreateTranslation(frame * .015625f, 0, .03125f);
            host.Actors[actor].Draw(true, false, world, outlines: false, bonePose: palette);
        }
        host.Scene.PrepareFrame(32, 24);
    }

    static object Observe(CoverageVerificationComparison result, ReadOnlySpan<uint> surface) => new
    {
        result.Pose.FrameId,
        result.Pose.FrameIndex,
        result.Pose.ViewId,
        Actor = result.Pose.Actor.Value,
        result.Pose.Preparation,
        result.Refused,
        result.ReferenceAccepted,
        ReferenceFailure = result.ReferenceFailure.ToString(),
        result.Vertex,
        result.Field,
        result.Expected,
        result.Actual,
        result.Pose.PoseWords,
        result.Pose.UploadWords,
        SourceVertices = result.Pose.Topology.SourceVertexCount,
        FinalVertices = result.Pose.Topology.VertexCount,
        ActualNativeHeader = surface[..16].ToArray(),
        ActualNativeSelectedWords = surface.Slice(16 + (result.Refused ? 0 : result.Pose.Topology.SourceVertexCount * 16),
            (result.Refused ? result.Pose.Topology.SourceVertexCount : result.Pose.Topology.VertexCount) * 16).ToArray()
    };

    void Write(IGpuDevice device, string name, List<object> comparisons, object facts)
    {
        string directory = Path.Combine(CoverageArtifacts.DirectoryPath ?? "/tmp/grimhollow-equipment-coverage-task14", "verification");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + "." + device.Backend + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            EvidenceKind = "actual-native-selected-surface-construction-comparison",
            Backend = device.Backend.ToString(),
            Device = device.Capabilities.DeviceName,
            Facts = facts,
            Comparisons = comparisons,
            Reference = "retained upload palette plus pinned Compose provenance, TrySkin and TryConstruct",
            ExactCertification = false,
            InjectedWords = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine(path);
    }
}
