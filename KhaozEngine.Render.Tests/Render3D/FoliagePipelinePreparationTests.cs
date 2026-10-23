using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliagePipelinePreparationTests
{
    [Fact]
    public void CascadeUpload_QueuesBothDepthProgramsBeforeTheFirstDraw()
    {
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 3);
        using var palette = new SkinnedBonePalette(device);
        using var shadows = new ShadowMapRenderer(device, 16, 1, palette, retired);
        using var layout = device.Factory.CreateResourceLayout(new([]));
        using var renderer = new FoliageShadowRenderer(device, shadows, layout, retired);
        var factory = Assert.IsType<FakeGpuResourceFactory>(device.Factory);
        int before = factory.ShaderRequests.Count;
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        renderer.UploadCascades(commands, [Matrix4x4.Identity], Matrix4x4.Identity, Vector3.Zero);
        Assert.Equal(2, factory.ShaderRequests.Count - before);
        Assert.Equal(0, commands.DrawCount);
        renderer.UploadCascades(commands, [Matrix4x4.Identity], Matrix4x4.Identity, Vector3.Zero);
        Assert.Equal(2, factory.ShaderRequests.Count - before);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 4)]
    public void CascadeUpload_PreparesOnlyDemandedCoordinateSpaces(bool ordinary, int expected)
    {
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 3);
        using var palette = new SkinnedBonePalette(device);
        using var shadows = new ShadowMapRenderer(device, 16, 1, palette, retired);
        using var foliage = device.Factory.CreateResourceLayout(new([]));
        using var anchor = device.Factory.CreateResourceLayout(new([]));
        using var renderer = new FoliageShadowRenderer(device, shadows, foliage, retired);
        var factory = Assert.IsType<FakeGpuResourceFactory>(device.Factory);
        int before = factory.ShaderRequests.Count;
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        renderer.UploadCascades(commands, [Matrix4x4.Identity], Matrix4x4.Identity, Vector3.Zero, ordinary, anchor);
        Assert.Equal(expected, factory.ShaderRequests.Count - before);
        Assert.Equal(0, commands.DrawCount);
        renderer.UploadCascades(commands, [Matrix4x4.Identity], Matrix4x4.Identity, Vector3.Zero, ordinary, anchor);
        Assert.Equal(expected, factory.ShaderRequests.Count - before);
    }

    [Fact]
    public void PointShadowPreparation_QueuesAnchoredProgramBeforeAnyFaceBindAndReusesIt()
    {
        using var device = new FakeGpuDevice();
        using var palette = new SkinnedBonePalette(device);
        using var renderer = new PointShadowRenderer(device, palette.Layout);
        using var anchor = device.Factory.CreateResourceLayout(new([]));
        var factory = Assert.IsType<FakeGpuResourceFactory>(device.Factory);
        int before = factory.ShaderRequests.Count;
        renderer.PrepareFoliagePipeline(anchor);
        Assert.Equal(1, factory.ShaderRequests.Count - before);
        renderer.PrepareFoliagePipeline(anchor);
        Assert.Equal(1, factory.ShaderRequests.Count - before);
    }
}
