using System.Linq;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalPostTargetsWaitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void First_allocation_does_not_drain_but_replacement_drains_before_freeing_targets(bool opaque)
    {
        using var device = new FakeGpuDevice();
        using var resources = new RenderResources(device, 64, 36, hdrColor: true);
        using var targets = new TemporalPostTargets(device);
        var history = new TemporalHistory();
        var factory = (FakeGpuResourceFactory)device.Factory;

        targets.Ensure(resources, history, 96, 54, bloomEnabled: false);
        Assert.True(targets.Allocated);
        Assert.Equal((64u, 36u), (targets.OpaqueColor.Width, targets.OpaqueColor.Height));
        Assert.Equal(0, device.WaitForIdleCalls);
        targets.Ensure(resources, history, 96, 54, bloomEnabled: false);
        Assert.Equal(0, device.WaitForIdleCalls);

        var oldOpaque = (FakeTexture)targets.OpaqueColor;
        var oldPings = factory.Textures.Where(t => t.Width == 96 && t.Height == 54).ToArray();
        Assert.Equal(2, oldPings.Length);
        if (opaque) resources.Resize(80, 45, mipped: false, sampleCount: 1, bloomEnabled: false, hdrColor: true);
        int drains = device.WaitForIdleCalls;
        device.OnWaitForIdle = () =>
        {
            Assert.False(oldOpaque.Disposed);
            Assert.All(oldPings, texture => Assert.False(texture.Disposed));
        };
        targets.Ensure(resources, history, opaque ? 96 : 128, opaque ? 54 : 72, bloomEnabled: false);
        Assert.Equal(drains + 1, device.WaitForIdleCalls);
        Assert.Equal(opaque, oldOpaque.Disposed);
        Assert.All(oldPings, texture => Assert.Equal(!opaque, texture.Disposed));
        Assert.Equal(opaque ? (96, 54) : (128, 72), (targets.Width, targets.Height));
        Assert.Equal(opaque ? (80u, 45u) : (64u, 36u), (targets.OpaqueColor.Width, targets.OpaqueColor.Height));
        device.OnWaitForIdle = null;
    }
}
