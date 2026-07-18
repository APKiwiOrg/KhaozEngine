using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Decorates a real <see cref="IGpuDevice"/> (there is no standalone fake resource factory, so this
    /// wraps a live device from <see cref="GpuDeviceContext"/> rather than reimplementing the whole GPU surface),
    /// forwarding every member to it and counting <see cref="WaitForIdle"/> calls. Lets a headless test assert
    /// that a caller drained the device before a mid-life resource disposal, without needing a lavapipe box to
    /// observe the crash the drain prevents.</summary>
    internal sealed class SpyGpuDevice : IGpuDevice
    {
        readonly IGpuDevice _inner;

        public SpyGpuDevice(IGpuDevice inner) => _inner = inner;

        /// <summary>How many times <see cref="WaitForIdle"/> has been called through this wrapper.</summary>
        public int WaitForIdleCalls { get; private set; }

        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _inner.Capabilities;
        public IGpuResourceFactory Factory => _inner.Factory;
        public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;

        public bool SyncToVerticalBlank
        {
            get => _inner.SyncToVerticalBlank;
            set => _inner.SyncToVerticalBlank = value;
        }

        public void Submit(IGpuCommandList cl) => _inner.Submit(cl);

        public void WaitForIdle()
        {
            WaitForIdleCalls++;
            _inner.WaitForIdle();
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, in data);

        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => _inner.UpdateTexture(texture, data, x, y, width, height);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mipLevel, uint arrayLayer)
            => _inner.UpdateTexture(texture, data, x, y, width, height, mipLevel, arrayLayer);

        public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuTexture staging) => _inner.Unmap(staging);

        public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
        public void Present() => _inner.Present();

        // Non-owning: the wrapped device belongs to the GpuDeviceContext the test created it from, same contract
        // as VeldridGpuDevice's own non-owning wrapper (see GpuDeviceContext.GpuDevice).
        public void Dispose() { }
    }
}
