using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class RenderResourcesRetirementTests
{
    [Fact]
    public void Failed_standalone_resize_drains_before_freeing_its_partial_targets()
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var resources = new RenderResources(device, 32, 16, hdrColor: false);
        device.Resources.ThrowOnTextureCreate = device.Resources.Textures.Count + 2;
        Assert.Throws<InvalidOperationException>(() => resources.Resize(64, 32, false, 1, false, false));
        FakeTexture partial = device.Resources.Textures[^1];
        var order = new List<string>();
        device.Trace = operation => { if (operation == "drain") order.Add("drain"); };
        partial.OnDispose = () => order.Add("free");
        resources.Dispose();
        Assert.Equal(new[] { "drain", "free" }, order);
        Assert.True(partial.Disposed);
    }

    [Theory]
    [InlineData("resize")]
    [InlineData("pings")]
    [InlineData("bloom")]
    [InlineData("distortion")]
    public void Optional_targets_wait_for_completion_across_repeated_replacements(string path)
    {
        using var device = new GpuPerfTimestampTestDevice();
        using var queue = GpuRetireQueue.Create(device);
        using var resources = new RenderResources(device, 32, 16, hdrColor: false, retired: queue);
        var textures = new List<FakeTexture>();
        var framebuffers = new List<FakeFramebuffer>();
        for (int change = 0; change < 2; change++)
        {
            if (path == "bloom") resources.EnsureBloom(true);
            if (path is "distortion" or "resize") resources.EnsureDistortion(true, 2);
            if (path == "pings") resources.EnsurePings(true);
            switch (path)
            {
                case "pings":
                    textures.Add((FakeTexture)resources.PingA); textures.Add((FakeTexture)resources.PingB);
                    framebuffers.Add((FakeFramebuffer)resources.PingAFB); framebuffers.Add((FakeFramebuffer)resources.PingBFB);
                    break;
                case "bloom":
                    textures.Add((FakeTexture)resources.BloomA!); textures.Add((FakeTexture)resources.BloomB!);
                    framebuffers.Add((FakeFramebuffer)resources.BloomAFB!); framebuffers.Add((FakeFramebuffer)resources.BloomBFB!);
                    break;
                case "distortion":
                    textures.Add((FakeTexture)resources.DistortTex!); framebuffers.Add((FakeFramebuffer)resources.DistortFB!);
                    break;
                default:
                    textures.AddRange(device.Resources.Textures.Where(t => !t.Disposed && !textures.Contains(t)));
                    framebuffers.AddRange(device.Resources.Framebuffers.Where(f => !f.Disposed && !framebuffers.Contains(f)));
                    break;
            }
            int waits = device.Waits;
            switch (path)
            {
                case "pings": resources.EnsurePings(false); break;
                case "bloom": resources.EnsureBloom(false); break;
                case "distortion": resources.EnsureDistortion(false, 2); break;
                default:
                    resources.Resize(48 + change * 16, 32, mipped: true, sampleCount: change == 0 ? 4 : 1,
                        bloomEnabled: true, hdrColor: true, motion: change != 0, visibility: true);
                    break;
            }
            queue.BeginFrame();
            Assert.Equal(waits, device.Waits);
            Assert.All(textures, t => Assert.False(t.Disposed));
            Assert.All(framebuffers, f => Assert.False(f.Disposed));
        }
        foreach (var fence in device.Fences) fence.Signaled = true;
        queue.BeginFrame();
        Assert.All(textures, t => Assert.True(t.Disposed));
        Assert.All(framebuffers, f => Assert.True(f.Disposed));
        Assert.Equal(0, queue.PendingCount);
    }
}
