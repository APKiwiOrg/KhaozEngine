using System;
using System.IO;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Core.Internal;
using KhaozEngine.Gpu.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class CoreDeviceTests
{
    [NativeGpuFact]
    public void Texture_upload_round_trips_through_readback()
        => WithCoreDevice(device =>
        {
            _ = device.PointSampler;
            _ = device.LinearSampler;
            const int size = 16;
            var pattern = new byte[size * size * 4];
            for (int i = 0; i < pattern.Length; i++)
                pattern[i] = (byte)(i * 29 + i / 256);

            using var texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                size, size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            device.UpdateTexture(texture, pattern, 0, 0, size, size);
            Assert.Equal(pattern, GpuReadback.ToRgba(device, texture, size, size));
        });

    [NativeGpuFact]
    public void Subresource_copy_addresses_both_mips_and_layers()
        => WithCoreDevice(device =>
        {
            var desc = GpuTextureDescription.Texture2DArray(8, 8, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.Sampled, arrayLayers: 2, mipLevels: 2);
            using var source = device.Factory.CreateTexture(desc);
            using var destination = device.Factory.CreateTexture(desc);
            var expected = new byte[4 * 4 * 4];
            byte[] patch = [17, 83, 211, 255];
            patch.CopyTo(expected, (4 * 2 + 1) * 4);
            device.UpdateTexture(source, new byte[expected.Length], 0, 0, 4, 4, mipLevel: 1, arrayLayer: 1);
            device.UpdateTexture(source, patch, 1, 2, 1, 1, mipLevel: 1, arrayLayer: 1);
            using var commands = device.Factory.CreateCommandList();
            commands.Begin();
            commands.CopyTextureSubresource(source, 1, 1, destination, 1, 0, 4, 4);
            commands.End();
            device.Submit(commands);
            Assert.Equal(expected, GpuReadback.ToRgbaMip(device, destination, 1, 0, 4, 4));
        });

    [NativeGpuFact]
    public void Fence_marks_buffer_copy_completion_and_resets_for_reuse()
        => WithCoreDevice(device =>
        {
            using var source = device.Factory.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.VertexBuffer));
            using var staging = device.Factory.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.Staging));
            using var commands = device.Factory.CreateCommandList();
            using var fence = device.Factory.CreateFence();
            Assert.False(fence.Signaled);

            uint[] expected = [0x5A67C3D1, 0x2913E54B];
            device.UpdateBuffer(source, 4, expected);
            commands.Begin();
            commands.CopyBuffer(source, 4, staging, 4, 8);
            commands.End();
            device.Submit(commands, fence);
            device.WaitForIdle();
            Assert.True(fence.Signaled);

            fence.Reset();
            Assert.False(fence.Signaled);
            uint replacement = 0x11223344;
            device.UpdateBuffer(source, 8, in replacement);
            commands.Begin();
            commands.CopyBuffer(source, 4, staging, 4, 8);
            commands.End();
            device.Submit(commands, fence);

            MappedData data = device.Map(staging, GpuMapMode.Read);
            try
            {
                Assert.Equal(expected[0], unchecked((uint)Marshal.ReadInt32(data.Data, 4)));
                Assert.Equal(replacement, unchecked((uint)Marshal.ReadInt32(data.Data, 8)));
            }
            finally
            {
                device.Unmap(staging);
            }
            Assert.True(fence.Signaled);
        });

    [NativeGpuFact]
    public void Logical_cube_can_upload_and_read_back_its_last_face()
        => WithCoreDevice(device =>
        {
            using var cube = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled | GpuTextureUsage.Cubemap));
            byte[] pixel = [27, 89, 156, 255];
            device.UpdateTexture(cube, pixel, 0, 0, 1, 1, mipLevel: 0, arrayLayer: 5);
            Assert.Equal(pixel, GpuReadback.ToRgbaMip(device, cube, 0, 5, 1, 1));
        });

    [NativeGpuFact]
    public void Upload_refusal_reports_the_requested_layer_and_texture_bound()
        => WithCoreDevice(device =>
        {
            using var texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            var refusal = Assert.Throws<GpuTextureTransferRangeException>(
                () => device.UpdateTexture(texture, new byte[4], 0, 0, 1, 1, mipLevel: 0, arrayLayer: 1));
            Assert.Equal(GpuTextureTransferRefusal.SubresourceOutOfRange, refusal.Reason);
            Assert.Equal("arrayLayer", refusal.ParamName);
            Assert.Equal(1u, refusal.ArrayLayer);
            Assert.Equal(1u, refusal.ArrayLayers);
        });

    [NativeGpuFact]
    public void Subresource_copy_refusal_reports_the_destination_mip_and_bound()
        => WithCoreDevice(device =>
        {
            var description = GpuTextureDescription.Texture2D(1, 1, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.Sampled);
            using var source = device.Factory.CreateTexture(description);
            using var destination = device.Factory.CreateTexture(description);
            using var commands = device.Factory.CreateCommandList();
            commands.Begin();
            var refusal = Assert.Throws<GpuTextureTransferRangeException>(
                () => commands.CopyTextureSubresource(source, 0, 0, destination, 1, 0, 1, 1));
            Assert.Equal(GpuTextureTransferRefusal.SubresourceOutOfRange, refusal.Reason);
            Assert.Equal("dstMipLevel", refusal.ParamName);
            Assert.Equal("destination", refusal.Side);
            Assert.Equal(1u, refusal.MipLevel);
            Assert.Equal(1u, refusal.MipLevels);
            commands.End();
        });

    [NativeGpuFact]
    public void R32_float_depth_and_colour_copies_are_refused_while_colour_copies_record()
        => WithCoreDevice(device =>
        {
            using var depth = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                4, 4, GpuPixelFormat.R32Float, GpuTextureUsage.DepthStencil));
            using var colour = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                4, 4, GpuPixelFormat.R32Float, GpuTextureUsage.RenderTarget));
            using var otherColour = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                4, 4, GpuPixelFormat.R32Float, GpuTextureUsage.RenderTarget));
            using var commands = device.Factory.CreateCommandList();

            commands.Begin();
            AssertIncompatibleTextureCopy(() => commands.CopyTexture(depth, colour));
            AssertIncompatibleTextureCopy(() => commands.CopyTextureSubresource(colour, 0, 0, depth, 4, 4));
            commands.CopyTexture(colour, otherColour);
            commands.End();
        });

    private static void AssertIncompatibleTextureCopy(Action copy)
    {
        var refusal = Assert.Throws<GpuTextureTransferArgumentException>(copy);
        Assert.Equal(GpuTextureTransferRefusal.TextureShapeMismatch, refusal.Reason);
        Assert.Equal("destination", refusal.ParamName);
        Assert.Equal(GpuPixelFormat.R32Float, refusal.SourceFormat);
        Assert.Equal(GpuPixelFormat.R32Float, refusal.DestinationFormat);
    }

    [NativeFact]
    public void Create_headless_refuses_when_the_vulkan_loader_is_missing()
    {
        const string variable = "KHAOZ_NATIVE_VULKAN_LOADER";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, Path.Combine(AppContext.BaseDirectory, "missing-vulkan-loader"));
            CoreBackendRegistration.SeatAsVulkanNative();
            var refusal = Assert.Throws<VulkanBackendCreationUnsupportedException>(
                () => GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative));
            Assert.Equal(VulkanBackendCreationRefusal.MachineUnsupported, refusal.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static void WithCoreDevice(Action<IGpuDevice> test)
    {
        CoreBackendRegistration.SeatAsVulkanNative();
        var context = GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative);
        CoreGpuDevice device;
        using (context)
        {
            device = Assert.IsType<CoreGpuDevice>(context.GpuDevice);
            test(device);
        }

        Assert.Equal(0u, device.DisposeValidationErrors);
    }
}
