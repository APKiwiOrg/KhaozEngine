using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class VulkanPipelineRecordingTests
{
    [Theory]
    [InlineData("Draw")]
    [InlineData("DrawSingle")]
    [InlineData("DrawIndexed")]
    [InlineData("Dispatch")]
    public void ACommandRequiresItsOwnPipelineInThisRecordingBeforeSpendingAnything(string command)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1690
        var fixture = new VulkanResourceFixture();
        using var colour = fixture.Factory.CreateTexture(
            VulkanResourceFixture.Texture(64, 64, GpuTextureUsage.RenderTarget));
        using var target = fixture.Factory.CreateFramebuffer(null, colour);
        using var indices = fixture.Factory.CreateBuffer(
            VulkanResourceFixture.Buffer(64, GpuBufferUsage.IndexBuffer));
        using var graphics = VulkanPipelineFixtures.Graphics(fixture);
        using var compute = VulkanPipelineFixtures.Compute(fixture);
        using var list = fixture.CreateList();
        bool dispatch = command == "Dispatch";
        Action issue = command switch
        {
            "DrawSingle" => () => list.Draw(3),
            "DrawIndexed" => () => list.DrawIndexed(3, 2, 0, 0, 0),
            "Dispatch" => () => list.Dispatch(1, 1, 1),
            _ => () => list.Draw(3, 2, 0, 0),
        };

        list.Begin();
        if (dispatch) list.SetPipeline(graphics);
        else list.SetComputePipeline(compute);
        list.SetFramebuffer(target);
        list.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
        Color owed = new(0.2f, 0.4f, 0.6f, 1);
        list.ClearColorTarget(0, owed);
        fixture.Trace.Clear();

        var refusal = Assert.Throws<GpuRecordingStateException>(issue);
        var reason = dispatch ? GpuRecordingStateRefusal.ComputePipelineNotBound : GpuRecordingStateRefusal.GraphicsPipelineNotBound;
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(GpuBackendKind.VulkanNative, refusal.Backend);
        Assert.Equal(command == "DrawSingle" ? "Draw" : command, refusal.Command);
        Assert.Empty(fixture.Trace);
        Assert.False(list.Rendering.IsRendering);
        Assert.Empty(fixture.RenderApi.Begins);

        if (dispatch) list.SetComputePipeline(compute);
        else list.SetPipeline(graphics);
        issue();
        var pass = Assert.Single(fixture.RenderApi.Begins);
        Assert.Equal(owed, pass.Colour[0].ClearValue);
        Assert.Equal(KhaozEngine.Gpu.Vulkan.Internal.VulkanLoadOp.Clear, pass.Colour[0].LoadOp);
        Assert.Contains(fixture.Trace, entry => entry.StartsWith(dispatch ? "Dispatch" : "Draw", StringComparison.Ordinal));
        using var nextTarget = fixture.Factory.CreateFramebuffer(null, colour);
        list.SetFramebuffer(nextTarget);
        list.Dispatch(1, 1, 1);
        issue();
        list.End();

        list.Begin();
        list.SetFramebuffer(target);
        list.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
        var next = Assert.Throws<GpuRecordingStateException>(issue);
        Assert.Equal(reason, next.Reason);
        Assert.Equal(refusal.Backend, next.Backend);
        Assert.Equal(refusal.Command, next.Command);
        list.End();
    }
}
