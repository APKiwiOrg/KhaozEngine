using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Captures the carry harness's actual setup and recording without creating a native device.</summary>
internal sealed class CoverageCarryLayoutDevice : IGpuDevice, IGpuResourceFactory
{
    readonly FakeGpuDevice inner = new();
    internal readonly List<IGpuBuffer> Buffers = new();
    internal readonly List<Pipeline> Pipelines = new();
    public GpuBackendKind Backend => inner.Backend;
    public GpuCapabilities Capabilities => inner.Capabilities;
    public IGpuResourceFactory Factory => this;
    public IGpuFramebuffer? SwapchainFramebuffer => null;
    public IGpuSampler PointSampler => inner.PointSampler;
    public IGpuSampler LinearSampler => inner.LinearSampler;
    public bool SyncToVerticalBlank { get; set; }
    public void Submit(IGpuCommandList commands) => throw new NotSupportedException();
    public void Submit(IGpuCommandList commands, IGpuFence fence) => throw new NotSupportedException();
    public void WaitForIdle() => throw new NotSupportedException();
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, ReadOnlySpan<T> data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, T[] data) where T : unmanaged { }
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, in T data) where T : unmanaged { }
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height) => throw new NotSupportedException();
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mip, uint layer) => throw new NotSupportedException();
    public MappedData Map(IGpuTexture texture, GpuMapMode mode) => throw new NotSupportedException();
    public void Unmap(IGpuTexture texture) => throw new NotSupportedException();
    public MappedData Map(IGpuBuffer buffer, GpuMapMode mode) => throw new NotSupportedException();
    public void Unmap(IGpuBuffer buffer) => throw new NotSupportedException();
    public void ResizeSwapchain(uint width, uint height) => throw new NotSupportedException();
    public void Present() => throw new NotSupportedException();
    public void Dispose() => inner.Dispose();

    public IGpuBuffer CreateBuffer(in GpuBufferDescription description)
    {
        var buffer = inner.Factory.CreateBuffer(description);
        Buffers.Add(buffer);
        return buffer;
    }
    public IGpuTexture CreateTexture(in GpuTextureDescription description) => throw new NotSupportedException();
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => throw new NotSupportedException();
    public IGpuSampler CreateSampler(in GpuSamplerDescription description) => throw new NotSupportedException();
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription description) => new Layout(description);
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription description) => new Set(description);
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment) => throw new NotSupportedException();
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source) => new Shader();
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source, GpuShaderCompileOptions options) => new Shader();
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription description) => throw new NotSupportedException();
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription description)
    {
        var pipeline = new Pipeline(description);
        Pipelines.Add(pipeline);
        return pipeline;
    }
    public IGpuCommandList CreateCommandList() => new Commands();
    public IGpuFence CreateFence() => throw new NotSupportedException();

    internal sealed class Layout(GpuResourceLayoutDescription description) : IGpuResourceLayout
    {
        internal GpuResourceLayoutDescription Description { get; } = description;
        public void Dispose() { }
    }
    internal sealed class Set(GpuResourceSetDescription description) : IGpuResourceSet
    {
        internal GpuResourceSetDescription Description { get; } = description;
        public void Dispose() { }
    }
    internal sealed class Pipeline(GpuComputePipelineDescription description) : IGpuComputePipeline
    {
        internal GpuComputePipelineDescription Description { get; } = description;
        public void Dispose() { }
    }
    sealed class Shader : IGpuComputeShader
    {
        public uint ThreadGroupSizeX => 64;
        public uint ThreadGroupSizeY => 1;
        public uint ThreadGroupSizeZ => 1;
        public void Dispose() { }
    }

    internal sealed class Commands : IGpuCommandList
    {
        Pipeline? pipeline;
        Set? set;
        internal readonly List<(Pipeline Pipeline, Set Set)> Dispatches = new();
        public void Begin() { pipeline = null; set = null; Dispatches.Clear(); }
        public void End() { }
        public void SetComputePipeline(IGpuComputePipeline value) => pipeline = Assert.IsType<Pipeline>(value);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet value)
        { Assert.Equal(0u, slot); set = Assert.IsType<Set>(value); }
        public void SetComputeResourceSet(uint slot, IGpuResourceSet value, uint dynamicOffset) => throw new NotSupportedException();
        public void Dispatch(uint x, uint y, uint z)
        { Assert.NotNull(pipeline); Assert.NotNull(set); Dispatches.Add((pipeline, set)); }
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, in T data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, ReadOnlySpan<T> data) where T : unmanaged { }
        public void SetFramebuffer(IGpuFramebuffer framebuffer) => throw new NotSupportedException();
        public void ClearColorTarget(uint index, Color colour) => throw new NotSupportedException();
        public void ClearDepthStencil(float depth) => throw new NotSupportedException();
        public void SetPipeline(IGpuPipeline value) => throw new NotSupportedException();
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet value) => throw new NotSupportedException();
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet value, uint dynamicOffset) => throw new NotSupportedException();
        public void SetVertexBuffer(uint slot, IGpuBuffer buffer) => throw new NotSupportedException();
        public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offset) => throw new NotSupportedException();
        public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format) => throw new NotSupportedException();
        public void SetScissorRect(uint index, uint x, uint y, uint width, uint height) => throw new NotSupportedException();
        public void SetFullScissorRects() => throw new NotSupportedException();
        public void Draw(uint count, uint instances, uint first, uint firstInstance) => throw new NotSupportedException();
        public void Draw(uint count) => throw new NotSupportedException();
        public void DrawIndexed(uint count, uint instances, uint first, int offset, uint firstInstance) => throw new NotSupportedException();
        public void CopyBuffer(IGpuBuffer source, uint sourceOffset, IGpuBuffer target, uint targetOffset, uint size) => throw new NotSupportedException();
        public void CopyTexture(IGpuTexture source, IGpuTexture target) => throw new NotSupportedException();
        public void CopyTextureSubresource(IGpuTexture source, uint mip, uint layer, IGpuTexture target, uint width, uint height) => throw new NotSupportedException();
        public void CopyTextureSubresource(IGpuTexture source, uint sourceMip, uint sourceLayer, IGpuTexture target,
            uint targetMip, uint targetLayer, uint width, uint height) => throw new NotSupportedException();
        public void GenerateMipmaps(IGpuTexture texture) => throw new NotSupportedException();
        public void ResolveTexture(IGpuTexture source, IGpuTexture target) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
