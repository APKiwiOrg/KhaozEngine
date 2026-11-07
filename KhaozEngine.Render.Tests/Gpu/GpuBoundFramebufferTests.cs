using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuBoundFramebufferTests
{
    // Task 12f fix round 1 covers the previously untyped Metal clear guards.
    [Theory]
    [InlineData("ClearColorTarget")]
    [InlineData("ClearDepthStencil")]
    public void MetalClearsNameTheRefusedCommandAndAcceptABoundTarget(string command)
    {
        using var harness = new MetalRingHarness();
        using var list = harness.NewList(new object());
        list.Begin();
        Action clear = command == "ClearColorTarget"
            ? () => list.ClearColorTarget(0, Color.White)
            : () => list.ClearDepthStencil(1f);
        var refusal = Assert.Throws<GpuBoundFramebufferException>(clear);
        Assert.Equal(GpuBoundFramebufferRefusal.FramebufferNotBound, refusal.Reason);
        Assert.Equal(command, refusal.Command);
        Assert.IsAssignableFrom<InvalidOperationException>(refusal);
        list.SetFramebuffer(new RecordedFramebuffer(depth: true));
        clear();
        list.End();
    }

    [Theory]
    [InlineData("Draw")]
    [InlineData("DrawIndexed")]
    public void VulkanDrawRecorderPassesTheOriginalCommandThroughItsSharedPath(string command)
    {
        var schedule = new VulkanRenderingSchedule(new FakeVulkanRenderApi());
        var recorder = new VulkanDrawRecorder(new FakeVulkanDrawEmitter());
        var binds = new VulkanBindRecords(PipelineBindPoint.Graphics);
        Action draw = command == "Draw"
            ? () => recorder.Draw(1, schedule, binds, new VulkanDrawCall(3, 1, 0, 0))
            : () => recorder.DrawIndexed(1, schedule, binds, new VulkanIndexedDrawCall(3, 1, 0, 0, 0));
        var refusal = Assert.Throws<GpuBoundFramebufferException>(draw);
        Assert.Equal(GpuBoundFramebufferRefusal.FramebufferNotBound, refusal.Reason);
        Assert.Equal(command, refusal.Command);
        schedule.SetFramebuffer(1, new VulkanBoundFramebuffer(1, 64, 64,
            [new VulkanAttachment(2, 3, GpuPixelFormat.B8G8R8A8UNorm, false, VulkanRestingLayout.ColorAttachmentOptimal)], default));
        draw();
    }
}
