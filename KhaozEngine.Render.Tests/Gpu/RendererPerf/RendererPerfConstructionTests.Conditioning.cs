using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfConstructionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(299)]
    public void Conditioning_creation_is_cold_and_keeps_its_counted_frame_index(int frameIndex)
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation());
        capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        capture.EnterFrame(RendererPerfConstructionStage.Cold, frameIndex);
        using var pipeline = Compute(device);
        capture.LeaveFrame();
        capture.EndOperation();
        var inventory = capture.Freeze();
        Assert.True(inventory.Complete, inventory.Reason);
        Assert.False(capture.HasMeasuredCreation);
        var occurrence = Assert.Single(inventory.Occurrences);
        Assert.Equal(RendererPerfConstructionStage.Cold, occurrence.Stage);
        Assert.Equal(frameIndex, occurrence.FrameIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(299)]
    public void Correspondence_accepts_counted_cold_conditioning_occurrences(int frameIndex)
    {
        var x = Correspondence();
        var inventory = x.Inventory with
        {
            Occurrences = [.. x.Inventory.Occurrences.Select(o => o with { FrameIndex = frameIndex })]
        };
        Assert.Empty(RendererPerfCompilerCorrespondence.Validate(inventory, x.Pipelines, x.Compiler));
    }

    [Fact]
    public void Cold_frame_membership_refuses_negative_and_nonconditioning_indices()
    {
        var manifest = OneOperation() with { Blocks = [new("S3", RendererPerfLayout.TotalOnly, 0, 64, 128)] };
        var capture = new RendererPerfConstructionCapture(manifest);
        capture.BeginOperation(0);
        Assert.Throws<InvalidOperationException>(() => capture.EnterFrame(RendererPerfConstructionStage.Cold, -1));
        capture.EndOperation();
        capture.BeginOperation(1);
        Assert.Throws<InvalidOperationException>(() => capture.EnterFrame(RendererPerfConstructionStage.Cold, 0));
        capture.EndOperation();

        var x = Correspondence();
        var inventory = x.Inventory with
        {
            Operations = ["S3/TotalOnly/0"],
            Occurrences = [.. x.Inventory.Occurrences.Select(o => o with { FrameIndex = 0 })]
        };
        Assert.NotEmpty(RendererPerfCompilerCorrespondence.Validate(inventory, x.Pipelines, x.Compiler));
    }
}
