using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Identity-preserving native forwarding with actual wait, readback and resource creation counts.</summary>
internal sealed class CoverageBatchDevice(IGpuDevice inner) : IGpuDevice
{
    readonly FactoryCounts factory = new(inner.Factory);
    internal int Waits, Readbacks, Submissions;
    internal bool CaptureWaitSites;
    internal readonly string[] WaitSites = new string[8];
    internal int WaitSiteCount;
    internal int Creations => factory.Creations;
    public GpuBackendKind Backend => inner.Backend;
    public GpuCapabilities Capabilities => inner.Capabilities;
    public IGpuResourceFactory Factory => factory;
    public IGpuFramebuffer? SwapchainFramebuffer => inner.SwapchainFramebuffer;
    public IGpuSampler PointSampler => inner.PointSampler;
    public IGpuSampler LinearSampler => inner.LinearSampler;
    public bool SyncToVerticalBlank { get => inner.SyncToVerticalBlank; set => inner.SyncToVerticalBlank = value; }
    public void Submit(IGpuCommandList commands) { Submissions++; inner.Submit(commands); }
    public void Submit(IGpuCommandList commands, IGpuFence fence) { Submissions++; inner.Submit(commands, fence); }
    public void WaitForIdle()
    {
        if (CaptureWaitSites) WaitSites[WaitSiteCount++] = Environment.StackTrace;
        Waits++; inner.WaitForIdle();
    }
    public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
    { Readbacks++; return inner.TryReadBuffer(staging, completion, offsetBytes, destination); }
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, ReadOnlySpan<T> values) where T : unmanaged => inner.UpdateBuffer(buffer, offset, values);
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, T[] values) where T : unmanaged => inner.UpdateBuffer(buffer, offset, values);
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, in T value) where T : unmanaged => inner.UpdateBuffer(buffer, offset, in value);
    public void UpdateTexture(IGpuTexture texture, byte[] values, uint x, uint y, uint width, uint height) => inner.UpdateTexture(texture, values, x, y, width, height);
    public void UpdateTexture(IGpuTexture texture, byte[] values, uint x, uint y, uint width, uint height, uint mip, uint layer) => inner.UpdateTexture(texture, values, x, y, width, height, mip, layer);
    public MappedData Map(IGpuTexture texture, GpuMapMode mode) => inner.Map(texture, mode);
    public MappedData Map(IGpuBuffer buffer, GpuMapMode mode) => inner.Map(buffer, mode);
    public void Unmap(IGpuTexture texture) => inner.Unmap(texture);
    public void Unmap(IGpuBuffer buffer) => inner.Unmap(buffer);
    public void ResizeSwapchain(uint width, uint height) => inner.ResizeSwapchain(width, height);
    public void Present() => inner.Present();
    public void Dispose() { }

    sealed class FactoryCounts(IGpuResourceFactory source) : IGpuResourceFactory
    {
        internal int Creations;
        public IGpuBuffer CreateBuffer(in GpuBufferDescription description) { Creations++; return source.CreateBuffer(description); }
        public IGpuTexture CreateTexture(in GpuTextureDescription description) { Creations++; return source.CreateTexture(description); }
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) { Creations++; return source.CreateFramebuffer(depth, colour); }
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription description) { Creations++; return source.CreateResourceSet(description); }
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription description) { Creations++; return source.CreateResourceLayout(description); }
        public IGpuSampler CreateSampler(in GpuSamplerDescription description) { Creations++; return source.CreateSampler(description); }
        public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment) { Creations++; return source.CreateShadersFromSpirv(vertex, fragment); }
        public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment, GpuShaderCompileOptions options) { Creations++; return source.CreateShadersFromSpirv(vertex, fragment, options); }
        public IGpuComputeShader CreateComputeShaderFromSpirv(string compute) { Creations++; return source.CreateComputeShaderFromSpirv(compute); }
        public IGpuComputeShader CreateComputeShaderFromSpirv(string compute, GpuShaderCompileOptions options) { Creations++; return source.CreateComputeShaderFromSpirv(compute, options); }
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription description) { Creations++; return source.CreateGraphicsPipeline(description); }
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription description) { Creations++; return source.CreateComputePipeline(description); }
        public IGpuCommandList CreateCommandList() { Creations++; return source.CreateCommandList(); }
        public IGpuFence CreateFence() { Creations++; return source.CreateFence(); }
    }
}
