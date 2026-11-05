using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PostChainRefusalTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(PixelPostProcess.MaxSourceSlots + 1)]
    public void UnsupportedSourceSlotCountsKeepThePreviousBinding(int slots)
    {
        using var device = new FakeGpuDevice();
        using var targets = new RenderResources(device, 16, 8, false);
        using var post = new PixelPostProcess(device, targets.PingAFB.Outputs, targets.PingAFB.Outputs);
        using var commands = new NullGpuCommandList();
        post.BindTargets(targets);
        var error = Assert.Throws<PostChainArgumentException>(() => post.BindTargets(new PostChainTestTargets(targets, slots)));
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal(PostChainRefusal.SourceSlotCountUnsupported, error.Reason);
        Assert.Equal(slots, error.SourceSlotCount);
        Assert.Equal(1, error.MinimumSourceSlotCount);
        Assert.Equal(PixelPostProcess.MaxSourceSlots, error.MaximumSourceSlotCount);
        Assert.Equal("res", error.ParamName);
        post.Run(commands, targets, targets.PingAFB, PostChainRig.LegacyPlain(), false, false);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("different")]
    [InlineData("stale")]
    public void RunningRequiresTheBoundTargetsAtTheirCurrentGeneration(string input)
    {
        using var device = new FakeGpuDevice();
        using var bound = new RenderResources(device, 16, 8, false);
        using var other = new RenderResources(device, 16, 8, false);
        using var post = new PixelPostProcess(device, bound.PingAFB.Outputs, bound.PingAFB.Outputs);
        using var commands = new NullGpuCommandList();
        int? generation = null;
        if (input != "unbound")
        {
            post.BindTargets(bound);
            generation = bound.Generation;
        }
        if (input == "stale") bound.Resize(20, 8, false, 1, false, false);
        RenderResources targets = input == "different" ? other : bound;
        var error = Assert.Throws<PostChainStateException>(() =>
            post.Run(commands, targets, targets.PingAFB, PostChainRig.LegacyPlain(), false, false));
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(PostChainRefusal.TargetsNotBoundAtCurrentGeneration, error.Reason);
        Assert.Equal(generation, error.BoundGeneration);
        Assert.Equal(targets.Generation, error.ActualGeneration);
        Assert.Equal(input == "stale", error.TargetsMatch);
        post.BindTargets(targets);
        post.Run(commands, targets, targets.PingAFB, PostChainRig.LegacyPlain(), false, false);
    }

    [Fact]
    public void RunningRequiresSetsForTheSelectedSourceSlot()
    {
        using var device = new FakeGpuDevice();
        using var targets = new RenderResources(device, 16, 8, false);
        using var post = new PixelPostProcess(device, targets.PingAFB.Outputs, targets.PingAFB.Outputs);
        using var commands = new NullGpuCommandList();
        var slots = new PostChainTestTargets(targets, 1);
        post.BindTargets(slots);
        slots.SourceSlot = 1;
        var error = Assert.Throws<PostChainStateException>(() =>
            post.Run(commands, slots, targets.PingAFB, PostChainRig.LegacyPlain(), false, false));
        Assert.Equal(PostChainRefusal.SourceSlotNotBuilt, error.Reason);
        Assert.Equal(1, error.SourceSlot);
        Assert.Equal(targets.Generation, error.BoundGeneration);
        slots.SourceSlot = 0;
        post.Run(commands, slots, targets.PingAFB, PostChainRig.LegacyPlain(), false, false);
    }

    [Fact]
    public void TemporalOutlineRequiresItsInternalPingPair()
    {
        using var device = new FakeGpuDevice();
        using var targets = new RenderResources(device, 16, 8, false);
        using var post = new PixelPostProcess(device, targets.PingAFB.Outputs, targets.PingAFB.Outputs);
        using var commands = new NullGpuCommandList();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        targets.EnsurePings(false);
        var error = Assert.Throws<PostChainStateException>(() =>
            post.RunTemporalOutline(commands, targets, targets.ColorTex, retired));
        Assert.Equal(PostChainRefusal.TemporalPingPairMissing, error.Reason);
        Assert.False(error.PingsAllocated);
        targets.EnsurePings(true);
        post.RunTemporalOutline(commands, targets, targets.ColorTex, retired);
    }
}
