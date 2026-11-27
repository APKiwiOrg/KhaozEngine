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
