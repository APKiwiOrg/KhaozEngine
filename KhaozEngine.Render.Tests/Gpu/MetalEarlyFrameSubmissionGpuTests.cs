using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class MetalEarlyFrameSubmissionGpuTests
{
    // #1682. A synthetic drawable wraps an offscreen texture. No window or swapchain is created.
    [GpuFact]
    public void SplitFrameWithSpriteBatchTailWritesHasIdenticalReadbackBytes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Run();
    }

    [SupportedOSPlatform("macos")]
    static void Run()
    {
        using var device = (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;
        byte[] reference = Render(device, split: false);
        byte[] split = Render(device, split: true);
        Assert.NotEmpty(reference);
        Assert.Contains((byte)255, reference);
        Assert.Equal(reference, split);
        Assert.Equal(0, device.Uncommitted.Outstanding);
        Assert.Null(device.Diagnostics.DeviceLossReason);
    }

    [SupportedOSPlatform("macos")]
    static byte[] Render(MetalGpuDevice device, bool split)
    {
        const uint size = 64;
        const GpuPixelFormat format = GpuPixelFormat.R8G8B8A8UNorm;
        var factory = device.Factory;
        using var a = factory.CreateTexture(GpuTextureDescription.Texture2D(size, size, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var b = factory.CreateTexture(GpuTextureDescription.Texture2D(size, size, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var framebuffer = factory.CreateFramebuffer(null, a);
        using var stagingImage = factory.CreateTexture(new GpuTextureDescription(size, size, format, GpuTextureUsage.Staging, mipLevels: 2));
        using var shared = factory.CreateBuffer(new(128, GpuBufferUsage.Staging));
        using var readPrefix = factory.CreateBuffer(new(64, GpuBufferUsage.Staging));
        using var readTail = factory.CreateBuffer(new(64, GpuBufferUsage.Staging));
        device.UpdateBuffer(shared, 0, Enumerable.Repeat((byte)17, 128).ToArray());
        device.UpdateTexture(stagingImage, new byte[size * size * 4], 0, 0, size, size);
        using var core = new Render2DCore(device, framebuffer.Outputs, ownsDevice: false);
        Texture2D white = core.CreateTexture([255, 255, 255, 255], 1, 1);
        Texture2D red = core.CreateTexture([255, 0, 0, 255], 1, 1);
        using var commands = device.CreateCommandList();
        if (!split) commands.EarlySubmission = null; // Test oracle only, no runtime switch.
        MetalSwapchainFramebuffer? drawable = null;
        drawable = new(format, new(size, size), () => drawable!.Adopt(
            new(((MetalTexture)b).Handle.Handle, format), new(size, size)));
        commands.Begin();
        commands.CopyBuffer(shared, 0, readPrefix, 0, 64);
        commands.SetFramebuffer(framebuffer);
        commands.CopyTextureSubresource(stagingImage, 0, 0, a, size, size);
        core.Batch.NewFrame(commands, (int)size, (int)size);
        core.Batch.Begin();
        core.Batch.Draw(white, new Vector4(4, 4, 16, 16), Color.White);
        core.Batch.End();
        commands.SetFramebuffer(drawable);
        commands.ClearColorTarget(0, Color.Black);
        if (split)
        {
            Assert.Equal(MetalFrameAccessFailure.OverlappingWrite,
                Assert.Throws<MetalFrameAccessException>(() => device.UpdateBuffer(shared, 0, new byte[4])).Reason);
            Assert.Equal(MetalFrameAccessFailure.WritableMapping,
                Assert.Throws<MetalFrameAccessException>(() => device.Map(shared, GpuMapMode.Write)).Reason);
            Assert.Throws<MetalFrameAccessException>(() => device.UpdateTexture(stagingImage, new byte[4], 0, 0, 1, 1));
            Assert.Throws<MetalFrameAccessException>(() => device.Map(stagingImage, GpuMapMode.ReadWrite));
        }
        // Same allocation, disjoint bytes and a disjoint texture subresource. These must remain writable.
        device.UpdateBuffer(shared, 64, Enumerable.Repeat((byte)93, 64).ToArray());
        device.UpdateTexture(stagingImage, new byte[4], 0, 0, 1, 1, 1, 0);
        commands.CopyBuffer(shared, 64, readTail, 0, 64);
        core.Batch.Begin();
        for (int i = 0; i < 49; i++)
            core.Batch.Draw(i % 2 == 0 ? white : red, new Vector4(i % 7 * 8, i / 7 * 8, 6, 6), Color.White);
        core.Batch.End();
        commands.End();
        device.Submit(commands);
        device.WaitForIdle();
        // Completion releases both write and mapping refusals.
        device.UpdateBuffer(shared, 0, new byte[] { 1 });
        device.Map(shared, GpuMapMode.Write);
        device.Unmap(shared);
        device.UpdateTexture(stagingImage, new byte[4], 0, 0, 1, 1);
        byte[] ReadBuffer(IGpuBuffer buffer)
        {
            var map = device.Map(buffer, GpuMapMode.Read);
            try { byte[] bytes = new byte[64]; Marshal.Copy(map.Data, bytes, 0, bytes.Length); return bytes; }
            finally { device.Unmap(buffer); }
        }
        byte[] prefix = ReadBuffer(readPrefix), tail = ReadBuffer(readTail);
        Assert.All(prefix, value => Assert.Equal((byte)17, value));
        Assert.All(tail, value => Assert.Equal((byte)93, value));
        return PointShadowZeroNdlGpuTests.Read(device, a).Concat(PointShadowZeroNdlGpuTests.Read(device, b))
            .Concat(prefix).Concat(tail).ToArray();
    }
}
