using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalVisibilityHistoryGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
{
    [GpuTheory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void VisibilityPlumbingLeavesColourConfidenceAndDepthUnchanged(int choice, bool fallback)
    {
        using var rig = new Rig();
        using var reference = new Rig();
        using GpuRetireQueue retired = GpuRetireQueue.Create(rig.Device);
        rig.Renderer.SelectEntry((TemporalResolveEntry)choice, retired);
        reference.Renderer.SelectEntry((TemporalResolveEntry)choice, retired);
        using IGpuTexture signal = Signal(rig.Device);
        output.WriteLine($"{rig.Device.Backend} on {rig.Device.Capabilities.DeviceName}");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int frame = 0; frame < 4; frame++)
        {
            byte value = (byte)(frame % 2 == 0 ? 51 : 204);
            rig.Device.UpdateTexture(signal, Enumerable.Repeat(value, N * N).ToArray(), 0, 0, N, N);
            float[] colour = Grey((x, y) => Ramp((x + y + frame) % N));
            float[] motion = Motion((_, _) => Vector2.Zero);
            rig.Fill(colour, colour, motion);
            reference.Fill(colour, colour, motion);
            rig.BeginFrame();
            reference.BeginFrame();
            var uniforms = Uniforms(frame > 0, new Vector2(0.125f * frame, -0.0625f * frame));
            rig.Resolve(uniforms, fallback ? null : signal);
            reference.Resolve(uniforms);
            Assert.Equal(reference.ReadColor(), rig.ReadColor());
            Assert.Equal(reference.ReadState(), rig.ReadState());
            Assert.Equal(reference.ReadPreviousDepth(), rig.ReadPreviousDepth());
            hash.AppendData(MemoryMarshal.AsBytes(rig.ReadColor().AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(rig.ReadState().AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(rig.ReadPreviousDepth().AsSpan()));
        }
        output.WriteLine($"colour-state-depth {choice} fallback {fallback}: {Convert.ToHexString(hash.GetHashAndReset())}");
    }

    [GpuTheory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void BothEntriesCopyExactR8BytesAndKeepTheReadTextureAndJitterOnRepeatedFrames(int choice, bool fallback)
    {
        using var rig = new Rig();
        using GpuRetireQueue retired = GpuRetireQueue.Create(rig.Device);
        rig.Renderer.SelectEntry((TemporalResolveEntry)choice, retired);
        using IGpuTexture signal = Signal(rig.Device);
        output.WriteLine($"{rig.Device.Backend} on {rig.Device.Capabilities.DeviceName}");
        rig.Fill(Grey((x, _) => Ramp(x)), Grey((x, _) => Ramp(x)), Motion((_, _) => Vector2.Zero));
        byte[] previous = [];
        Vector2 previousJitter = default;
        for (int frame = 0; frame < 3; frame++)
        {
            byte[] bytes = Enumerable.Range(0, N * N).Select(i => (byte)(i + frame * 64)).ToArray();
            rig.Device.UpdateTexture(signal, bytes, 0, 0, N, N);
            rig.BeginFrame();
            Vector2 jitter = new(0.125f * (frame + 1), -0.0625f * (frame + 1));
            rig.Resolve(Uniforms(frame > 0, jitter), fallback ? null : signal);
            TemporalVisibilityHistory visibility = rig.Renderer.VisibilityHistory;
            Assert.True(visibility.Allocated);
            byte[] expected = fallback ? Enumerable.Repeat((byte)255, N * N).ToArray() : bytes;
            Assert.Equal(expected, Read(rig.Device, visibility.Texture(rig.History.WriteIndex)));
            Assert.Equal(new Vector4(previousJitter, frame > 0 ? 1f : 0f, 0f),
                rig.Renderer.LastVisibilityUniforms.PreviousJitter);
            if (frame > 0) Assert.Equal(previous, Read(rig.Device, visibility.Texture(rig.History.ReadIndex)));
            rig.History.MarkValidAfterFrame();
            previous = expected;
            previousJitter = jitter;
        }
        byte[] written = Read(rig.Device, rig.Renderer.VisibilityHistory.Texture(rig.History.WriteIndex));
        rig.Device.UpdateTexture(signal, new byte[N * N], 0, 0, N, N);
        rig.Resolve(Uniforms(true, new Vector2(0.49f, 0.49f)), fallback ? null : signal);
        Assert.Equal(written, Read(rig.Device, rig.Renderer.VisibilityHistory.Texture(rig.History.WriteIndex)));
        rig.BeginFrame();
        rig.Resolve(Uniforms(true), fallback ? null : signal);
        Assert.Equal(new Vector4(previousJitter, 1f, 0f), rig.Renderer.LastVisibilityUniforms.PreviousJitter);
    }

    static IGpuTexture Signal(IGpuDevice device) => device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
        N, N, GpuPixelFormat.R8UNorm, GpuTextureUsage.Sampled));

    static byte[] Read(IGpuDevice device, IGpuTexture texture)
    {
        using IGpuTexture staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            texture.Width, texture.Height, GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging));
        using IGpuCommandList commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, nameof(TemporalVisibilityHistoryGpuTests)))
            commands.CopyTexture(texture, staging);
        device.Submit(commands);
        device.WaitForIdle();
        var bytes = new byte[texture.Width * texture.Height];
        MappedData map = device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < texture.Height; y++)
                Marshal.Copy(map.Data + (int)(y * map.RowPitch), bytes, (int)(y * texture.Width), (int)texture.Width);
        }
        finally { device.Unmap(staging); }
        return bytes;
    }
}
