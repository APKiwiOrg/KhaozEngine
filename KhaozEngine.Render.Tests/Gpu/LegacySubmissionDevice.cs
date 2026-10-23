using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

// Deliberately implements the pre-multi-fence API to exercise external implementer compatibility.
internal sealed class LegacySubmissionDevice(IGpuDevice inner) : IGpuDevice
{
    public GpuBackendKind Backend => inner.Backend;
    public GpuCapabilities Capabilities => inner.Capabilities;
    public IGpuResourceFactory Factory => inner.Factory;
    public IGpuFramebuffer? SwapchainFramebuffer => inner.SwapchainFramebuffer;
    public IGpuSampler PointSampler => inner.PointSampler;
    public IGpuSampler LinearSampler => inner.LinearSampler;
    public bool SyncToVerticalBlank { get => inner.SyncToVerticalBlank; set => inner.SyncToVerticalBlank = value; }
    public void Submit(IGpuCommandList commands) { inner.Submit(commands); }
    public void Submit(IGpuCommandList commands, IGpuFence fence) { inner.Submit(commands, fence); }
    public void WaitForIdle()
    {
        inner.WaitForIdle();
    }
    public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
    { return inner.TryReadBuffer(staging, completion, offsetBytes, destination); }
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
}
