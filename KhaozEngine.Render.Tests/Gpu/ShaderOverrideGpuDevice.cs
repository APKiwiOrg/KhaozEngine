using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A real device with a test-owned shader oracle at its factory boundary.</summary>
internal sealed class ShaderOverrideGpuDevice(IGpuDevice inner, Func<string, string> rewrite,
    Func<GpuResourceLayoutDescription, GpuResourceLayoutDescription>? layoutRewrite = null,
    Func<GpuResourceSetDescription, GpuResourceSetDescription>? setRewrite = null,
    Func<GpuBufferDescription, GpuBufferDescription>? bufferRewrite = null,
    Func<Type, byte[], byte[]>? uploadRewrite = null,
    Action<GpuResourceLayoutDescription, IGpuResourceLayout>? layoutCreated = null) : IGpuDevice, IGpuResourceFactory
{
    public GpuBackendKind Backend => inner.Backend;
    public GpuCapabilities Capabilities => inner.Capabilities;
    public IGpuResourceFactory Factory => this;
    public IGpuFramebuffer? SwapchainFramebuffer => inner.SwapchainFramebuffer;
    public IGpuSampler PointSampler => inner.PointSampler;
    public IGpuSampler LinearSampler => inner.LinearSampler;
    public GpuDeviceDiagnostics Diagnostics => inner.Diagnostics;
    public bool SyncToVerticalBlank { get => inner.SyncToVerticalBlank; set => inner.SyncToVerticalBlank = value; }
    public void Submit(IGpuCommandList cl) => inner.Submit(cl);
    public void Submit(IGpuCommandList cl, IGpuFence fence) => inner.Submit(cl, fence);
    public void Submit(IGpuCommandList cl, ReadOnlySpan<IGpuFence> fences) => inner.Submit(cl, fences);
    public void WaitForIdle() => inner.WaitForIdle();
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, ReadOnlySpan<T> data) where T : unmanaged
    {
        if (uploadRewrite is null) inner.UpdateBuffer(b, offset, data);
        else inner.UpdateBuffer(b, offset, MemoryMarshal.Cast<byte, T>(uploadRewrite(typeof(T), MemoryMarshal.AsBytes(data).ToArray())));
    }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, T[] data) where T : unmanaged => UpdateBuffer<T>(b, offset, data.AsSpan());
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, in T data) where T : unmanaged
    {
        if (uploadRewrite is null) inner.UpdateBuffer(b, offset, in data);
        else UpdateBuffer<T>(b, offset, new[] { data }.AsSpan());
    }
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h) => inner.UpdateTexture(t, data, x, y, w, h);
    public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h, uint mip, uint layer) => inner.UpdateTexture(t, data, x, y, w, h, mip, layer);
    public MappedData Map(IGpuTexture t, GpuMapMode mode) => inner.Map(t, mode);
    public MappedData Map(IGpuBuffer b, GpuMapMode mode) => inner.Map(b, mode);
    public void Unmap(IGpuTexture t) => inner.Unmap(t);
    public void Unmap(IGpuBuffer b) => inner.Unmap(b);
    public void ResizeSwapchain(uint w, uint h) => inner.ResizeSwapchain(w, h);
    public void Present() => inner.Present();
    public void Dispose() { } // The test owns the underlying context.
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => inner.Factory.CreateBuffer(bufferRewrite?.Invoke(d) ?? d);
    public IGpuTexture CreateTexture(in GpuTextureDescription d) => inner.Factory.CreateTexture(d);
    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => inner.Factory.CreateFramebuffer(depth, colour);
    public IGpuSampler CreateSampler(in GpuSamplerDescription d) => inner.Factory.CreateSampler(d);
    // layoutCreated sees the description the engine asked for and the layout it got, so a set rewrite can tell a
    // rewritten layout's sets apart by reference rather than by resource count.
    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
    {
        IGpuResourceLayout layout = inner.Factory.CreateResourceLayout(layoutRewrite?.Invoke(d) ?? d);
        layoutCreated?.Invoke(d, layout);
        return layout;
    }
    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => inner.Factory.CreateResourceSet(setRewrite?.Invoke(d) ?? d);
    public IGpuShaderSet CreateShadersFromSpirv(string vertex, string fragment) => inner.Factory.CreateShadersFromSpirv(rewrite(vertex), rewrite(fragment));
    public IGpuComputeShader CreateComputeShaderFromSpirv(string source) => inner.Factory.CreateComputeShaderFromSpirv(rewrite(source));
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => inner.Factory.CreateGraphicsPipeline(d);
    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => inner.Factory.CreateComputePipeline(d);
    public IGpuCommandList CreateCommandList() => inner.Factory.CreateCommandList();
    public IGpuFence CreateFence() => inner.Factory.CreateFence();
    public IGpuTimestampPool CreateTimestampPool(uint capacity) => inner.Factory.CreateTimestampPool(capacity);
}
