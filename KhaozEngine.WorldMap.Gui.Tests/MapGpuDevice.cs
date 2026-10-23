using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.WorldMap.Gui;

// Inert GPU handles expose disposal timing while the real Render2D wrappers record draws.
internal sealed class MapGpuDevice : IGpuDevice, IGpuResourceFactory
{
    internal List<MapGpuResource> Textures { get; } = [];
    public GpuBackendKind Backend => GpuBackendKind.MetalNative;
    public GpuCapabilities Capabilities { get; } = new(false, true, "Map ownership fixture",
        false, false, 1, false, false, false);
    public IGpuResourceFactory Factory => this;
    public IGpuFramebuffer? SwapchainFramebuffer => null;
    public IGpuSampler PointSampler { get; } = new MapGpuResource();
    public IGpuSampler LinearSampler { get; } = new MapGpuResource();
    public bool SyncToVerticalBlank { get; set; }
    public void Submit(IGpuCommandList cl) { }
    public void Submit(IGpuCommandList cl, IGpuFence fence) => throw new NotSupportedException();
    public void WaitForIdle() { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height) { }
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
        uint mipLevel, uint arrayLayer)
    { }
    public MappedData Map(IGpuTexture staging, GpuMapMode mode) => throw new NotSupportedException();
    public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => throw new NotSupportedException();
    public void Unmap(IGpuTexture staging) { }
    public void Unmap(IGpuBuffer staging) { }
    public void ResizeSwapchain(uint w, uint h) { }
    public void Present() { }
    public void Dispose() { }
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => new MapGpuResource { SizeInBytes = d.SizeInBytes };
    public IGpuTexture CreateTexture(in GpuTextureDescription d)
    {
        var texture = new MapGpuResource { Width = d.Width, Height = d.Height, Format = d.Format };
        Textures.Add(texture);
        return texture;
    }
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => throw new NotSupportedException();
    public IGpuSampler CreateSampler(in GpuSamplerDescription d) => new MapGpuResource();
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => new MapGpuResource();
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => new MapGpuResource();
    public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl) => new MapGpuResource();
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => new MapGpuResource();
    public IGpuCommandList CreateCommandList() => new MapGpuCommands();
    public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl) => throw new NotSupportedException();
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => throw new NotSupportedException();
    public IGpuFence CreateFence() => throw new NotSupportedException();
}

internal sealed class MapGpuResource : IGpuTexture, IGpuBuffer, IGpuSampler, IGpuResourceLayout,
    IGpuResourceSet, IGpuShaderSet, IGpuPipeline
{
    internal int Disposals { get; private set; }
    public uint SizeInBytes { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint MipLevels => 1;
    public uint SampleCount => 1;
    public GpuPixelFormat Format { get; init; }
    public void Dispose() => Disposals++;
}

internal sealed class MapGpuCommands : IGpuCommandList
{
    public void Begin() { }
    public void End() { }
    public void SetFramebuffer(IGpuFramebuffer fb) { }
    public void ClearColorTarget(uint index, Color rgba) { }
    public void ClearDepthStencil(float depth) { }
    public void SetPipeline(IGpuPipeline p) { }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) { }
    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
    public void SetVertexBuffer(uint slot, IGpuBuffer b) { }
    public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) { }
    public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) { }
    public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) { }
    public void SetFullScissorRects() { }
    public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart) { }
    public void Draw(uint vertexCount) { }
    public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart) { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
    public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes) { }
    public void CopyTexture(IGpuTexture src, IGpuTexture dst) { }
    public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
        IGpuTexture dst, uint width, uint height)
    { }
    public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
        IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
    { }
    public void GenerateMipmaps(IGpuTexture texture) { }
    public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { }
    public void SetComputePipeline(IGpuComputePipeline p) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { }
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
    public void Dispose() { }
}
