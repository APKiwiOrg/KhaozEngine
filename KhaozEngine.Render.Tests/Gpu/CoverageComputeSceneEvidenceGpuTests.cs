using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeSceneEvidenceGpuTests(CoverageGpuScene scene) : IClassFixture<CoverageGpuScene>
{
    [GpuFact]
    public void GPU_scene_evidence_preserves_surface_masks_and_negative_controls()
    {
        var row = new CoverageCase(0) { Outlines = true };
        var baseline = scene.Capture(row, false, true);
        var capture = scene.Capture(row, true, true);
        var reference = scene.CaptureReference(row, true);
        CoveragePixelProof.AssertSurface(baseline, capture, reference, requireBreakthrough: true);
        Assert.Empty(baseline.Surfaces);
        Assert.Empty(reference.Surfaces);
        Assert.Equal(2, capture.Surfaces.Length);
        foreach (var surface in capture.Surfaces)
        {
            Assert.Equal(CoveragePresentation.Dispatched, surface.StagedPresentation);
            var outcome = Assert.IsType<CoverageGpuOutcome>(surface.CompletedOutcome);
            Assert.Equal((surface.FrameId, surface.ViewId, surface.Actor), (outcome.FrameId, outcome.ViewId, outcome.Actor.Value));
            Assert.Equal(CoveragePresentation.Covered, outcome.Outcome.Presentation);
            Assert.Equal("final", surface.Selection);
            Assert.NotEmpty(surface.SourceWords);
            Assert.NotEmpty(surface.FinalWords);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(surface.FinalWords.AsSpan()))), surface.Signature);
            Assert.Contains(surface.Consumers, consumer => consumer.Pass == "main");
            Assert.Contains(surface.Consumers, consumer => consumer.Pass == "full");
            Assert.Contains(surface.Consumers, consumer => consumer.Pass == "visible");
            Assert.All(surface.Consumers, consumer =>
            {
                Assert.Equal(surface.CurrentBufferId, consumer.CurrentBufferId);
                Assert.Equal(surface.Reservation.CurrentBase, consumer.CurrentBase);
                Assert.Equal(surface.Actor, consumer.Actor);
            });
        }
        Assert.Equal(capture.Surfaces[0].Signature, capture.SubmittedSurfaceSignature);
        var original = capture.Surfaces[0];
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CoverageSceneEvidence.AssertAssociation(original with
        { CompletedOutcome = original.CompletedOutcome!.Value with { FrameId = original.FrameId + 1 } }));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CoverageSceneEvidence.AssertAssociation(original with
        { CompletedOutcome = original.CompletedOutcome!.Value with { Actor = MotionKey.From(original.Actor + 1) } }));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CoverageSceneEvidence.AssertAssociation(original with
        { PreparationId = original.PreparationId + 1 }));
        var consumers = (CoverageSceneConsumer[])original.Consumers.Clone();
        consumers[0] = consumers[0] with { CurrentBase = consumers[0].CurrentBase + 16 };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CoverageSceneEvidence.AssertAssociation(original with { Consumers = consumers }));
        consumers[0] = original.Consumers[0] with { CurrentBufferId = original.PreviousBufferId };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CoverageSceneEvidence.AssertAssociation(original with { Consumers = consumers }));
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_compute_admission_uses_ordinary_current_pose_without_a_gpu_result(bool gpuSkinning)
    {
        using var unavailable = new CoverageGpuScene(suppressCompute: true);
        var row = new CoverageCase(0) { Outlines = true };
        var baseline = unavailable.Capture(row, false, gpuSkinning);
        var fallback = unavailable.Capture(row, true, gpuSkinning);
        CoveragePixelProof.AssertTargetsEqual(fallback, baseline);
        CoveragePixelProof.AssertOutlines(fallback, baseline);
        Assert.Equal("cpu-admission-current-pose", fallback.SignatureSource);
        Assert.Equal(baseline.SubmittedSurfaceSignature, fallback.SubmittedSurfaceSignature);
        Assert.Equal(2, fallback.Surfaces.Length);
        foreach (var surface in fallback.Surfaces)
        {
            Assert.Equal(CoveragePresentation.UncoveredFallback, surface.StagedPresentation);
            Assert.Equal(CoverageFailureCode.UnsupportedAdapter, surface.Admission.Failure.Code);
            Assert.Null(surface.CompletedOutcome);
            Assert.Equal("cpu-fallback", surface.Selection);
            Assert.Empty(surface.HeaderWords);
            Assert.Empty(surface.SourceWords);
            Assert.Empty(surface.FinalWords);
            Assert.Empty(surface.Consumers);
            Assert.Equal(0, surface.CurrentBufferId);
        }
    }

}
