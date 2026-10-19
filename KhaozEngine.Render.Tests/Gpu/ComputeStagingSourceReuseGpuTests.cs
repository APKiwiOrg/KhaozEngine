using System;
using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class ComputeStagingSourceReuseGpuTests(ITestOutputHelper output)
{
    const uint Bytes = 256 * 1024;

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Upload_after_retired_storage_preserves_every_word(bool recorded)
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        var factory = device.Factory;
        using var prior = factory.CreateBuffer(new(Bytes, GpuBufferUsage.StructuredBufferReadWrite, 4));
        using var destination = factory.CreateBuffer(new(Bytes, GpuBufferUsage.StructuredBufferReadWrite, 4));
        var before = new uint[Bytes / 4];
        var expected = new uint[Bytes / 4];
        for (uint i = 0; i < before.Length; i++)
        {
            before[i] = 0xfee00000u | i;
            expected[i] = 0x13579bdu ^ unchecked(i * 2654435761u);
        }
        device.UpdateBuffer(prior, 0, before);
        SubmitSetupAndRetire(device, prior);
        using var commands = factory.CreateCommandList();
        commands.Begin();
        if (recorded) commands.UpdateBuffer(destination, 0, expected);
        else device.UpdateBuffer(destination, 0, expected);
        commands.End();
        device.Submit(commands);
        Assert.Equal(expected, GpuReadback.ReadBuffer<uint>(device, destination, expected.Length));
        output.WriteLine($"{device.Backend} {device.Capabilities.DeviceName}: setup/recorded={recorded}, exact words={expected.Length}");
    }

    [GpuFact]
    public void Texture_upload_after_retired_storage_preserves_every_texel()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        var factory = device.Factory;
        using var prior = factory.CreateBuffer(new(Bytes, GpuBufferUsage.StructuredBufferReadWrite, 4));
        using var destination = factory.CreateTexture(GpuTextureDescription.Texture2D(512, 128,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        var before = new uint[Bytes / 4];
        Array.Fill(before, 0xfee00000u);
        var expected = new byte[Bytes];
        for (int i = 0; i < expected.Length; i++) expected[i] = unchecked((byte)(i * 37 + 11));
        device.UpdateBuffer(prior, 0, before);
        SubmitSetupAndRetire(device, prior);
        device.UpdateTexture(destination, expected, 0, 0, 512, 128);
        Assert.Equal(expected, GpuReadback.ToRgba(device, destination, 512, 128));
        output.WriteLine($"{device.Backend} {device.Capabilities.DeviceName}: exact texture bytes={expected.Length}");
    }

    static void SubmitSetupAndRetire(IGpuDevice device, IGpuBuffer prior)
    {
        using var commands = device.Factory.CreateCommandList();
        commands.Begin(); commands.End();
        device.Submit(commands);
        // Complete the old allocation before retiring it. The next upload can obtain its recycled memory.
        device.WaitForIdle();
        prior.Dispose();
        device.Present();
    }
}
