using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The cluster image is uploaded as its used prefix, the headers plus the indices this frame stored, never as
/// the whole 940,032-byte buffer (issue #1112).</summary>
public sealed class PointLightClusterUploadTests
{
    static readonly Matrix4x4 Ortho = Matrix4x4.CreateOrthographic(32f, 18f, 1f, 25f);

    static ModelRenderer NewRenderer(FakeGpuDevice device) => new(device,
        new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt,
            GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float),
        shadowMapResolution: 128, shadowCascadeCount: 1);

    [Fact]
    public void AFrameUploadsTheHeadersAndOnlyTheIndicesItStored()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer renderer = NewRenderer(device);
        var cl = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        ModelRenderer.PointLightData[] lights =
        [
            Light(new Vector3(1f, 0.5f, -1.5f), 0.6f),
            Light(new Vector3(-6f, 3f, -12f), 3f),
        ];

        renderer.BuildAndUploadPointLightClusters(cl, lights, Ortho, Vector3.Zero, -Vector3.UnitZ, Ortho, Vector3.Zero);

        RecordingGpuCommandList.Upload upload =
            Assert.Single(cl.Uploads, u => ReferenceEquals(u.Buffer, renderer.PointLightClusterBuffer));
        uint[] words = MemoryMarshal.Cast<byte, uint>(upload.Data!.AsSpan()).ToArray();
        int stored = 0;
        for (int cluster = 0; cluster < PointLightClusterBuilder.ClusterCount; cluster++)
        {
            uint count = words[cluster] & PointLightClusterBuilder.CountMask;
            if (count != PointLightClusterBuilder.OverflowCount) stored += (int)count;
        }
        int expectedWords = (PointLightClusterBuilder.HeaderRegionUInts + stored + 3) & ~3;
        Assert.Equal(0u, upload.Offset);
        Assert.True(stored > 0, "the two lights stored no references");
        Assert.Equal((uint)(expectedWords * sizeof(uint)), upload.Bytes);
        Assert.True(upload.Bytes < renderer.PointLightClusterBuffer.SizeInBytes / 10);
    }

    [Fact]
    public void AnEmptyFrameUploadsNothing()
    {
        using var device = new FakeGpuDevice();
        using ModelRenderer renderer = NewRenderer(device);
        var cl = new RecordingGpuCommandList(new NullGpuCommandList());

        renderer.BuildAndUploadPointLightClusters(cl, ReadOnlySpan<ModelRenderer.PointLightData>.Empty, Ortho,
            Vector3.Zero, -Vector3.UnitZ, Ortho, Vector3.Zero);

        Assert.DoesNotContain(cl.Uploads, u => ReferenceEquals(u.Buffer, renderer.PointLightClusterBuffer));
    }

    static ModelRenderer.PointLightData Light(Vector3 position, float radius) => new()
    {
        PosRadius = new Vector4(position, radius),
        ColorIntensity = Vector4.One,
    };
}
