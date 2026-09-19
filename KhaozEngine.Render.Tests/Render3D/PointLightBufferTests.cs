using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class PointLightBufferTests
{
    static ModelRenderer.PointLightData Light(int index) => new()
    {
        PosRadius = new Vector4(index + 100f, index + 200f, index + 300f, index + 4f),
        ColorIntensity = new Vector4(index + 0.1f, index + 0.2f, index + 0.3f, index + 0.4f),
    };

    static ModelRenderer NewRenderer(FakeGpuDevice device) => new(device,
        new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt,
            GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float),
        shadowMapResolution: 128, shadowCascadeCount: 1);

    [Fact]
    public void RecordsAreFortyEightBytesAndPackEverySubmittedLight()
    {
        var lights = Enumerable.Range(0, 40).Select(Light).ToArray();
        var records = new ModelRenderer.PointLightGpuData[lights.Length];
        int[] slots = Enumerable.Range(0, lights.Length).Select(i => i % 7 == 0 ? i : -1).ToArray();

        int count = ModelRenderer.BuildPointLightRecords(
            lights, records, slots, bias: 0.01f, slopeBias: 0.02f, new Vector3(10f, 20f, 30f));

        Assert.Equal(48, Marshal.SizeOf<ModelRenderer.PointLightGpuData>());
        Assert.Equal(lights.Length, count);
        Assert.Equal(new Vector4(129f, 219f, 309f, 43f), records[39].PosRadius);
        Assert.Equal(lights[39].ColorIntensity, records[39].ColorIntensity);
        Assert.Equal(new Vector4(-1f, 0.01f, 0.02f, 0f), records[39].ShadowParams);
        Assert.Equal(new Vector4(35f, 0.01f, 0.02f, 0f), records[35].ShadowParams);
    }

    [Fact]
    public void RepackingAShorterListClearsUnusedRecordsAndMissingShadowSlots()
    {
        var records = new ModelRenderer.PointLightGpuData[64];
        var first = Enumerable.Range(0, 40).Select(Light).ToArray();
        var firstSlots = Enumerable.Range(0, 40).ToArray();
        ModelRenderer.BuildPointLightRecords(first, records, firstSlots, 0.1f, 0.2f);

        var second = new[] { Light(2), Light(3), Light(4) };
        ModelRenderer.BuildPointLightRecords(second, records, [9], 0.3f, 0.4f);

        Assert.Equal(new Vector4(9f, 0.3f, 0.4f, 0f), records[0].ShadowParams);
        Assert.Equal(new Vector4(-1f, 0.3f, 0.4f, 0f), records[1].ShadowParams);
        Assert.Equal(new Vector4(-1f, 0.3f, 0.4f, 0f), records[2].ShadowParams);
        Assert.Equal(default, records[3]);
        Assert.Equal(default, records[39]);
    }

    [Fact]
    public void CapacityGrowthRebindsEverySetThatCapturedTheBufferAndShrinkReusesIt()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using ModelRenderer renderer = NewRenderer(device);
        IGpuResourceSet material = renderer.CreateMaterialSet();
        renderer.EnsureSkinnedMainCapacity(1);

        FakeBuffer oldBuffer = Assert.IsType<FakeBuffer>(renderer.PointLightBuffer);
        FakeBuffer clusterBuffer = Assert.IsType<FakeBuffer>(renderer.PointLightClusterBuffer);
        Assert.Equal(940032u, clusterBuffer.SizeInBytes);
        FakeResourceSet[] captured = factory.ResourceSets
            .Where(set => !set.Disposed && set.Resources.Contains(oldBuffer))
            .ToArray();
        Assert.Equal(5, captured.Length);
        Assert.All(captured, set => Assert.Contains(clusterBuffer, set.Resources));

        renderer.EnsurePointLightCapacity(17, [material], replace => material = replace(material));

        FakeBuffer grown = Assert.IsType<FakeBuffer>(renderer.PointLightBuffer);
        Assert.NotSame(oldBuffer, grown);
        Assert.Equal(32 * 48u, grown.SizeInBytes);
        Assert.True(oldBuffer.Disposed);
        Assert.All(captured, set => Assert.True(set.Disposed));
        Assert.Equal(captured.Length, factory.ResourceSets.Count(
            set => !set.Disposed && set.Resources.Contains(grown)));
        Assert.Contains(grown, Assert.IsType<FakeResourceSet>(material).Resources);
        Assert.Same(clusterBuffer, renderer.PointLightClusterBuffer);
        Assert.All(factory.ResourceSets.Where(set => !set.Disposed && set.Resources.Contains(grown)),
            set => Assert.Contains(clusterBuffer, set.Resources));

        int buffers = factory.Buffers.Count;
        int sets = factory.ResourceSets.Count;
        renderer.EnsurePointLightCapacity(3, [material], replace => material = replace(material));
        Assert.Same(grown, renderer.PointLightBuffer);
        Assert.Equal(buffers, factory.Buffers.Count);
        Assert.Equal(sets, factory.ResourceSets.Count);

        renderer.EnsurePointLightCapacity(257, [material], replace => material = replace(material));
        Assert.Equal(512 * 48u, renderer.PointLightBuffer.SizeInBytes);
    }

    [Fact]
    public void FailedSetRebuildRetainsThePreviousBufferAndMaterialThenCanRetry()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using ModelRenderer renderer = NewRenderer(device);
        IGpuResourceSet material = renderer.CreateMaterialSet();
        IGpuBuffer oldBuffer = renderer.PointLightBuffer;
        IGpuResourceSet oldMaterial = material;
        factory.ThrowOnResourceSetCreate = factory.ResourceSets.Count + 2;

        Assert.Throws<InvalidOperationException>(() =>
            renderer.EnsurePointLightCapacity(17, [material], replace => material = replace(material)));

        Assert.Same(oldBuffer, renderer.PointLightBuffer);
        Assert.Same(oldMaterial, material);
        Assert.False(Assert.IsType<FakeBuffer>(oldBuffer).Disposed);
        Assert.False(Assert.IsType<FakeResourceSet>(oldMaterial).Disposed);
        Assert.True(factory.Buffers[^1].Disposed);

        factory.ThrowOnResourceSetCreate = 0;
        renderer.EnsurePointLightCapacity(17, [material], replace => material = replace(material));
        Assert.NotSame(oldBuffer, renderer.PointLightBuffer);
        Assert.NotSame(oldMaterial, material);
    }

    [Fact]
    public void ImpossibleCapacityIsRejectedBeforeAnyAllocation()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using ModelRenderer renderer = NewRenderer(device);
        int buffers = factory.Buffers.Count;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            renderer.EnsurePointLightCapacity(int.MaxValue, [], _ => { }));

        Assert.Equal(buffers, factory.Buffers.Count);
    }
}
