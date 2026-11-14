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

    // #1691. The device-free trace resolves every handle the real emitter resolves, through the same shared member,
    // so a handle from another backend earns the same refusal without a device. The framebuffer and index buffer
    // rows ask twice because the real emitter resolves BEFORE its redundancy guard: resolving after it would record
    // the first foreign bind and pass the second one as redundant.
    [Theory]
    [InlineData("SetFramebuffer")]
    [InlineData("SetIndexBuffer")]
    [InlineData("SetVertexBuffer")]
    [InlineData("UpdateBuffer")]
    [InlineData("CopyBuffer")]
    [InlineData("ResourceBind")]
    [InlineData("SetComputePipeline")]
    public void D3D11TraceRefusesTheForeignHandlesTheDeviceRefuses(string entry)
    {
        D3D11NativeTraceEmitter emitter = new D3D11BindFixtures.Harness().Emitter;
        using var layout = D3D11BindFixtures.Layout(D3D11BindFixtures.U("U", GpuShaderStages.Vertex));
        using var pipeline = new D3D11StateCacheTests.FakeD3D11Pipeline(new object(), new object(), new object(),
            new object(), new object(), new object(), 4u, layout) { VertexStrides = new[] { 16u } };
        var outputs = new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm);
        using var foreignTarget = new FakeFramebuffer(outputs, 4, 4);
        using var foreignBuffer = new FakeBuffer(64);
        using var ownBuffer = new TraceBuffer(64);
        using var foreignSet = D3D11BindFixtures.Set(layout, foreignBuffer);
        using var ownSet = D3D11BindFixtures.Set(layout, ownBuffer);
        emitter.SetPipeline(pipeline);
        emitter.SetGraphicsResourceSet(0, ownSet);

        (Action foreign, Action own, Type actual, Type expected, string param, string kind) row = entry switch
        {
            "SetFramebuffer" => (() => emitter.SetFramebuffer(foreignTarget),
                () => emitter.SetFramebuffer(new TraceFramebuffer(outputs, 4, 4)),
                typeof(FakeFramebuffer), typeof(ID3D11RenderTargetSurface), "framebuffer", "framebuffer"),
            "SetIndexBuffer" => (() => emitter.SetIndexBuffer(foreignBuffer, GpuIndexFormat.UInt16),
                () => emitter.SetIndexBuffer(ownBuffer, GpuIndexFormat.UInt16),
                typeof(FakeBuffer), typeof(ID3D11BindableViews), "buffer", "buffer"),
            "SetVertexBuffer" => (() => { emitter.SetVertexBuffer(0, foreignBuffer, 0); emitter.Draw(3, 1, 0, 0); },
                () => { emitter.SetVertexBuffer(0, ownBuffer, 0); emitter.Draw(3, 1, 0, 0); },
                typeof(FakeBuffer), typeof(ID3D11BindableViews), "buffer", "buffer"),
            "UpdateBuffer" => (() => emitter.UpdateBuffer(foreignBuffer, 0, new byte[4]),
                () => emitter.UpdateBuffer(ownBuffer, 0, new byte[4]),
                typeof(FakeBuffer), typeof(ID3D11BindableViews), "buffer", "buffer"),
            // The source is the foreign one because the destination is resolved first, so this row also shows
            // the second handle is asked.
            "CopyBuffer" => (() => emitter.CopyBuffer(foreignBuffer, 0, ownBuffer, 0, 4),
                () => emitter.CopyBuffer(ownBuffer, 0, ownBuffer, 0, 4),
                typeof(FakeBuffer), typeof(ID3D11BindableViews), "buffer", "buffer"),
            "ResourceBind" => (() => { emitter.SetGraphicsResourceSet(0, foreignSet); emitter.Draw(3, 1, 0, 0); },
                () => { emitter.SetGraphicsResourceSet(0, ownSet); emitter.Draw(3, 1, 0, 0); },
                typeof(FakeBuffer), typeof(ID3D11BindableViews), "resource", "bindable resource"),
            "SetComputePipeline" => (() => emitter.SetComputePipeline(new D3D11RecordingDriverTests.FakeComputePipeline()),
                () => emitter.SetComputePipeline(new D3D11BindFlushTests.FakeComputePipeline()),
                typeof(D3D11RecordingDriverTests.FakeComputePipeline), typeof(ID3D11ComputePipelineState),
                "pipeline", "pipeline"),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null),
        };

        bool resolvesBeforeRedundancyGuard = entry is "SetFramebuffer" or "SetIndexBuffer";
        for (int ask = 0; ask < (resolvesBeforeRedundancyGuard ? 2 : 1); ask++)
        {
            var refusal = Assert.Throws<GpuResourceOwnershipArgumentException>(row.foreign);
            Assert.Equal(GpuResourceOwnershipRefusal.ForeignBackend, refusal.Reason);
            Assert.Equal(GpuBackendKind.Direct3D11Native, refusal.ExpectedBackend);
            Assert.Equal(row.actual, refusal.ActualType);
            Assert.Equal(row.expected, refusal.ExpectedType);
            Assert.Equal(row.param, refusal.ParamName);
            Assert.Equal(row.kind, refusal.ResourceKind);
        }

        row.own();
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
        Assert.Equal(GpuResourceOwnershipRefusal.ForeignBackend, refusal.Reason);
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
