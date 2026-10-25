using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

// A real headless device with a texture standing in for the window's output. Only present and the
// platform swapchain copy are substituted. All rendering, submission and capture copies reach the GPU.
internal sealed class FrameOutputDevice(IGpuDevice inner, PostChainRig.Output output) : IGpuDevice, IGpuSwapchainCapture
{
    internal int Submits, Presents;
    internal PostChainRig.Output Output { get; set; } = output;
    internal Action<IGpuCommandList>? BeforeSubmit;
    internal Action? BeforePresent;
    public GpuBackendKind Backend => inner.Backend;
    public GpuCapabilities Capabilities => inner.Capabilities;
    public IGpuResourceFactory Factory => inner.Factory;
    public IGpuFramebuffer SwapchainFramebuffer => Output.Framebuffer;
    public IGpuSampler PointSampler => inner.PointSampler;
    public IGpuSampler LinearSampler => inner.LinearSampler;
    public bool SyncToVerticalBlank { get => inner.SyncToVerticalBlank; set => inner.SyncToVerticalBlank = value; }
    public bool SwapchainCopySourceRequested { get; set; }
    public bool SwapchainCopySupported => true;
    public bool SwapchainCopyable => SwapchainCopySourceRequested;
    public void CopySwapchainColor(IGpuCommandList commands, IGpuTexture destination) => commands.CopyTexture(Output.Texture, destination);
    static IGpuCommandList Unwrap(IGpuCommandList commands) => commands is RecordingGpuCommandList recording ? recording.Inner : commands;
    public void Submit(IGpuCommandList commands) { BeforeSubmit?.Invoke(commands); Submits++; inner.Submit(Unwrap(commands)); }
    public void Submit(IGpuCommandList commands, IGpuFence fence) { BeforeSubmit?.Invoke(commands); Submits++; inner.Submit(Unwrap(commands), fence); }
    public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> fences) { BeforeSubmit?.Invoke(commands); Submits++; inner.Submit(Unwrap(commands), fences); }
    public void WaitForIdle() => inner.WaitForIdle();
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, ReadOnlySpan<T> values) where T : unmanaged => inner.UpdateBuffer(buffer, offset, values);
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, T[] values) where T : unmanaged => inner.UpdateBuffer(buffer, offset, values);
    public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, in T value) where T : unmanaged => inner.UpdateBuffer(buffer, offset, in value);
    public void UpdateTexture(IGpuTexture texture, byte[] values, uint x, uint y, uint width, uint height) => inner.UpdateTexture(texture, values, x, y, width, height);
    public void UpdateTexture(IGpuTexture texture, byte[] values, uint x, uint y, uint width, uint height, uint mip, uint layer) => inner.UpdateTexture(texture, values, x, y, width, height, mip, layer);
    public MappedData Map(IGpuTexture texture, GpuMapMode mode) => inner.Map(texture, mode);
    public MappedData Map(IGpuBuffer buffer, GpuMapMode mode) => inner.Map(buffer, mode);
    public void Unmap(IGpuTexture texture) => inner.Unmap(texture);
    public void Unmap(IGpuBuffer buffer) => inner.Unmap(buffer);
    public void ResizeSwapchain(uint width, uint height) => throw new NotSupportedException();
    public void Present() { BeforePresent?.Invoke(); Presents++; }
    public void Dispose() { }
}
