using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuResourceOwnershipRegressionTests
{
    [Fact]
    public void VulkanReadbackRefusesAnotherDevicesStagingBuffer()
    {
        using var fixture = new VulkanDeviceFixture();
        using var other = new VulkanDeviceFixture();
        using IGpuBuffer foreign = other.StagingBuffer();
        using IGpuBuffer own = fixture.StagingBuffer();
        using IGpuFence completion = fixture.Device.Factory.CreateFence();
        using VulkanCommandList commands = fixture.RecordedList();
        fixture.Device.Submit(commands, completion);
        fixture.Semaphore.Completed = fixture.CommandApi.Submissions[^1].Value;

        var refusal = Assert.Throws<GpuResourceOwnershipArgumentException>(
            () => fixture.Device.TryReadBuffer(foreign, completion, 0, Span<byte>.Empty));
        Assert.Equal(GpuResourceOwnershipRefusal.DifferentDevice, refusal.Reason);
        Assert.Equal("staging", refusal.ParamName);
        Assert.Equal(GpuBackendKind.VulkanNative, refusal.ExpectedBackend);
        Assert.Equal("buffer", refusal.ResourceKind);
        Assert.Equal("TryReadBuffer", refusal.Context);
        Assert.True(fixture.Device.TryReadBuffer(own, completion, 0, Span<byte>.Empty));
    }

    [Fact]
    public void VulkanPipelineLayoutRefusesAMissingSlot()
    {
        var api = new FakeVulkanDescriptorApi();
        var setLayouts = new VulkanDescriptorSetLayoutCache(api);
        var cache = new VulkanPipelineLayoutCache(api, maxDynamicUniformBuffers: 0);
        using var layout = new VulkanResourceLayout(setLayouts, new GpuResourceLayoutDescription());

        var refusal = Assert.Throws<GpuResourceOwnershipArgumentException>(
            () => cache.GetOrCreate(new VulkanResourceLayout[] { layout, null! }));
        Assert.Equal(GpuResourceOwnershipRefusal.MissingResource, refusal.Reason);
        Assert.Equal("layouts", refusal.ParamName);
        Assert.Equal("resource layout", refusal.ResourceKind);
        Assert.Equal("pipeline layout", refusal.Context);
        Assert.Equal(1, refusal.ResourceIndex);
        Assert.NotEqual(0UL, cache.GetOrCreate(new[] { layout, layout }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void D3D11FlushRefusesAForeignResourceSet(bool compute)
    {
        var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), new D3D11NativeCallLog());
        using var layout = D3D11BindFixtures.Layout();
        using var foreign = new FakeResourceSet();
        using var own = D3D11BindFixtures.Set(layout);
        using var graphics = D3D11BindFixtures.Pipeline(layout);
        using var kernel = new D3D11BindFlushTests.FakeComputePipeline(layout);
        emitter.SetPipeline(graphics);
        emitter.SetComputePipeline(kernel);
        if (compute) emitter.SetComputeResourceSet(0, foreign);
        else emitter.SetGraphicsResourceSet(0, foreign);

        void Flush()
        {
            if (compute) emitter.Dispatch(1, 1, 1);
            else emitter.Draw(3, 1, 0, 0);
        }

        var refusal = Assert.Throws<GpuResourceOwnershipArgumentException>(Flush);
        Assert.Equal(GpuResourceOwnershipRefusal.ForeignBackend, refusal.Reason);
        Assert.Null(refusal.ParamName);
        Assert.Equal(GpuBackendKind.Direct3D11Native, refusal.ExpectedBackend);
        Assert.Equal(typeof(FakeResourceSet), refusal.ActualType);
        Assert.Equal(typeof(D3D11ResourceSet), refusal.ExpectedType);
        Assert.Equal("resource set", refusal.ResourceKind);
        Assert.Equal(0, refusal.ResourceIndex);
        Assert.Equal(compute ? "compute" : "graphics", refusal.PipelineKind);
        if (compute) emitter.SetComputeResourceSet(0, own);
        else emitter.SetGraphicsResourceSet(0, own);
        Flush();
    }

    [Fact]
    public void VulkanSwapchainCopyRefusesAForeignCommandListOnAValidRequest()
    {
        var window = new VulkanWindowedParts(new FakeVulkanSurfaceApi(), new FakeVulkanSwapchainApi(),
            FakeVulkanSurfaceApi.Handle, new VulkanExtent(1280, 720), true, VulkanAcquireMode.Semaphore);
        using var fixture = new VulkanDeviceFixture(windowed: window);
        fixture.Device.SwapchainCopySourceRequested = true;
        fixture.Device.Present();
        Assert.True(fixture.Device.SwapchainCopyable);
        IGpuFramebuffer framebuffer = fixture.Device.SwapchainFramebuffer!;
        using IGpuTexture destination = fixture.Device.Factory.CreateTexture(new GpuTextureDescription(
            framebuffer.Width, framebuffer.Height, framebuffer.Outputs.Colour[0], GpuTextureUsage.Sampled));
        using var foreign = new NullGpuCommandList();

        var refusal = Assert.Throws<GpuResourceOwnershipArgumentException>(
            () => fixture.Device.CopySwapchainColor(foreign, destination));
        Assert.Equal(GpuResourceOwnershipRefusal.ResourceNotOwnedByDevice, refusal.Reason);
        Assert.Equal("commands", refusal.ParamName);
        Assert.Equal(GpuBackendKind.VulkanNative, refusal.ExpectedBackend);
        Assert.Equal(typeof(NullGpuCommandList), refusal.ActualType);
        Assert.Equal("command list", refusal.ResourceKind);
        Assert.Equal("CopySwapchainColor", refusal.Context);

        var resources = new VulkanResourceFixture();
        using var own = new VulkanCommandList(
            new VulkanCommandPoolRing(fixture.CommandApi, fixture.FramesInFlight, fixture.Timeline,
                fixture.Device.Backpressure), fixture.Device.Retired, render: resources.RenderApi,
            layouts: new VulkanLayoutTracker(resources.Barriers), transfers: resources.TransferSink);
        own.Begin();
        own.SetFramebuffer(framebuffer);
        fixture.Device.CopySwapchainColor(own, destination);
        own.End();
        Assert.Equal(((VulkanTexture)destination).Image,
            Assert.Single(resources.TransferSink.ImageCopies).Destination);
    }
}
