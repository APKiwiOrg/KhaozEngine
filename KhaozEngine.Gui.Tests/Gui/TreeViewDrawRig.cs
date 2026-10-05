using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Tests.Gui;

// Inert resource handles let the real batch record rows without a device or pixel readback.
internal sealed class TreeViewDrawRig : IDisposable
{
    readonly Device _device = new();
    internal SpriteBatch Batch { get; }
    internal Texture2D White { get; }
    internal SpriteFont Font { get; }
    internal Commands CommandList { get; } = new();

    internal TreeViewDrawRig()
    {
        Batch = new SpriteBatch(_device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        White = Texture2D.Wrap(new Texture(1, 1), 1, 1);
        Font = SpriteFont.Build(_device, DefaultFont.Bytes, 16f);
    }

    internal void Draw(TreeView tree, Rect? outerClip = null)
    {
        Batch.NewFrame(CommandList, 400, 300);
        Batch.Begin();
        if (outerClip is { } clip) Batch.SetScissor(clip);
        tree.Draw(Batch, White, Font);
        if (outerClip is not null) Batch.ClearScissor();
        Batch.End();
    }

    public void Dispose()
    {
        Font.Dispose();
        White.Dispose();
        Batch.Dispose();
        _device.Dispose();
    }

    sealed class Device : IGpuDevice
    {
        public GpuBackendKind Backend => GpuBackendKind.Vulkan;
        public GpuCapabilities Capabilities { get; } = new(false, true, "Row recorder",
            samplerAnisotropy: false, samplerLodBias: false, maxMsaaSampleCount: 1,
            supportsCompute: false, supportsCompletionFences: false);
        public IGpuResourceFactory Factory { get; } = new Factory();
        public IGpuFramebuffer? SwapchainFramebuffer => null;
        public IGpuSampler PointSampler { get; } = new Handle();
        public IGpuSampler LinearSampler { get; } = new Handle();
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
        public void Unmap(IGpuTexture staging) { }
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => throw new NotSupportedException();
        public void Unmap(IGpuBuffer staging) { }
        public void ResizeSwapchain(uint w, uint h) { }
        public void Present() { }
        public void Dispose() { }
    }

    sealed class Factory : IGpuResourceFactory
    {
        public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => new Buffer(d.SizeInBytes);
        public IGpuTexture CreateTexture(in GpuTextureDescription d) => new Texture(d.Width, d.Height);
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) =>
            throw new NotSupportedException();
        public IGpuSampler CreateSampler(in GpuSamplerDescription d) => new Handle();
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => new Handle();
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => new Handle();
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl) => new Handle();
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => new Handle();
        public IGpuCommandList CreateCommandList() => new Commands();
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl) => throw new NotSupportedException();
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => throw new NotSupportedException();
        public IGpuFence CreateFence() => throw new NotSupportedException();
    }

    sealed class Buffer(uint size) : IGpuBuffer
    {
        public uint SizeInBytes => size;
        public void Dispose() { }
    }

    sealed class Texture(uint width, uint height) : IGpuTexture
    {
        public uint Width => width;
        public uint Height => height;
        public uint MipLevels => 1;
        public uint SampleCount => 1;
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8UNorm;
        public void Dispose() { }
    }

    sealed class Handle : IGpuSampler, IGpuResourceLayout, IGpuResourceSet, IGpuShaderSet, IGpuPipeline
    {
        public void Dispose() { }
    }

    internal sealed class Commands : IGpuCommandList
    {
        internal List<Rect> Scissors { get; } = new();
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
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) => Scissors.Add(new Rect(x, y, w, h));
        public void SetFullScissorRects() { }
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart) { }
        public void Draw(uint vertexCount) { }
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart) { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes) { }
        public void CopyTexture(IGpuTexture src, IGpuTexture dst) { }
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint width, uint height)
        { }
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
        { }
        public void GenerateMipmaps(IGpuTexture texture) { }
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { }
        public void SetComputePipeline(IGpuComputePipeline p) { }
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { }
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
        public void Dispose() { }
    }
}
