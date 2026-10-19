using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D;

internal sealed class CoverageCaptureDevice : IGpuDevice
{
    internal readonly IGpuDevice Inner;
    internal readonly FakeGpuResourceFactory? RecordingInner;
    readonly CoverageRecordingFactory? recordingFactory;
    internal bool CoverageShadersDisposed => recordingFactory is { Shaders.Count: 2 } f && f.Shaders.TrueForAll(s => s.Disposed);
    internal int CoverageBuildAttempts => recordingFactory?.CoverageBuildAttempts ?? 0;
    internal CoverageCaptureDevice(GpuBackendKind backend = GpuBackendKind.Vulkan, bool supportsCompute = true,
        bool failCoverageBuild = false, IGpuDevice? nativeDevice = null)
    {
        Inner = nativeDevice ?? new FakeGpuDevice(backend);
        if (nativeDevice is null)
        {
            RecordingInner = (FakeGpuResourceFactory)Inner.Factory;
            recordingFactory = new(RecordingInner, failCoverageBuild);
        }
        Capabilities = nativeDevice?.Capabilities ?? new GpuCapabilities(false, true, deviceName: "CoverageRecordingDevice",
            supportsCompute: supportsCompute)
        { MaxColorAttachments = 8 };
    }
    internal readonly Dictionary<IGpuBuffer, byte[]> Uploads = new();
    public GpuBackendKind Backend => Inner.Backend;
    public GpuCapabilities Capabilities { get; }
    public IGpuResourceFactory Factory => recordingFactory ?? Inner.Factory;
    public IGpuFramebuffer? SwapchainFramebuffer => null;
    public IGpuSampler PointSampler => Inner.PointSampler;
    public IGpuSampler LinearSampler => Inner.LinearSampler;
    public bool SyncToVerticalBlank { get => Inner.SyncToVerticalBlank; set => Inner.SyncToVerticalBlank = value; }
    public void Submit(IGpuCommandList cl) => Inner.Submit(cl);
    public void Submit(IGpuCommandList cl, IGpuFence fence) => Inner.Submit(cl, fence);
    public void WaitForIdle() => Inner.WaitForIdle();
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, ReadOnlySpan<T> data) where T : unmanaged
    {
        if (!Uploads.TryGetValue(b, out byte[]? bytes)) Uploads.Add(b, bytes = new byte[b.SizeInBytes]);
        MemoryMarshal.AsBytes(data).CopyTo(bytes.AsSpan((int)offset));
        Inner.UpdateBuffer(b, offset, data);
    }
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, T[] data) where T : unmanaged => UpdateBuffer(b, offset, (ReadOnlySpan<T>)data);
    public void UpdateBuffer<T>(IGpuBuffer b, uint offset, in T data) where T : unmanaged =>
        UpdateBuffer(b, offset, MemoryMarshal.CreateReadOnlySpan(in data, 1));
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint w, uint h) => Inner.UpdateTexture(texture, data, x, y, w, h);
    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint w, uint h, uint mip, uint layer) =>
        Inner.UpdateTexture(texture, data, x, y, w, h, mip, layer);
    public MappedData Map(IGpuTexture texture, GpuMapMode mode) => Inner.Map(texture, mode);
    public MappedData Map(IGpuBuffer buffer, GpuMapMode mode) => Inner.Map(buffer, mode);
    public void Unmap(IGpuTexture texture) => Inner.Unmap(texture);
    public void Unmap(IGpuBuffer buffer) => Inner.Unmap(buffer);
    public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
        => Inner.TryReadBuffer(staging, completion, offsetBytes, destination);
    public void ResizeSwapchain(uint w, uint h) => Inner.ResizeSwapchain(w, h);
    public void Present() => Inner.Present();
    public void Dispose() { if (RecordingInner is not null) Inner.Dispose(); }
}
