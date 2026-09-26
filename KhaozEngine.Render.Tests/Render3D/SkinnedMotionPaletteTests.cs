using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedMotionPaletteTests
{
    [Fact]
    public void ASlotHoldsTheWorldThenAFullPaletteAndIsDynamicOffsetAligned()
    {
        Assert.Equal(64u + 128u * 64u, SkinnedMotionPalette.PayloadBytes);
        Assert.Equal(8448u, SkinnedMotionPalette.SlotBytes);
        Assert.Equal(0u, SkinnedMotionPalette.SlotBytes % 256u);
        Assert.Equal(3u * 8448u, SkinnedMotionPalette.OffsetFor(3));
    }

    [Fact]
    public void PackLaysTheWorldAndBonesIntoTheirSlotOfOneWholeBufferUpload()
    {
        using var device = new FakeGpuDevice();
        using GpuRetireQueue retired = GpuRetireQueue.CreateFrameCounted(device, GpuRetireQueue.DefaultFrameDelay);
        using IGpuBuffer frame = device.Factory.CreateBuffer(
            new GpuBufferDescription(MotionFrameUbo.SizeInBytes, GpuBufferUsage.UniformBuffer));
        using var palette = new SkinnedMotionPalette(device, frame, retired);
        palette.EnsureCapacity(2);
        Matrix4x4 world = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        Matrix4x4[] bones = { Matrix4x4.CreateScale(2f), Matrix4x4.CreateRotationY(.5f) };
        palette.Pack(1, world, bones);

        using var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        palette.Upload(cl);

        RecordingGpuCommandList.Upload upload = Assert.Single(cl.Uploads);
        Assert.True(upload.IsWholeBuffer);
        ReadOnlySpan<byte> slot = upload.Data.AsSpan((int)SkinnedMotionPalette.SlotBytes);
        Assert.Equal(world, MemoryMarshal.Read<Matrix4x4>(slot));
        Assert.Equal(bones[0], MemoryMarshal.Read<Matrix4x4>(slot[64..]));
        Assert.Equal(bones[1], MemoryMarshal.Read<Matrix4x4>(slot[128..]));
    }
}
