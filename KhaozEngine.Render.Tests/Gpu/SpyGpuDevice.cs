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
        readonly bool _suppressFences;

        /// <summary><paramref name="suppressFences"/> reports <see cref="GpuCapabilities.SupportsCompletionFences"/>
        /// as false while forwarding everything else to a device that really does have them. That is how the
        /// retired-resource A/B runs both ripeness policies in ONE process on ONE device: a number measured against
        /// a second process is not comparable.</summary>
        public SpyGpuDevice(IGpuDevice inner, bool suppressFences = false)
        {
            _inner = inner;
            _suppressFences = suppressFences;
        }

        /// <summary>How many times <see cref="WaitForIdle"/> has been called through this wrapper.</summary>
        public int WaitForIdleCalls { get; private set; }

        /// <summary>How many times a fenced <see cref="Submit(IGpuCommandList,IGpuFence)"/> went through this
        /// wrapper: the retirement barrier's empty submissions, and nothing else in the engine today.</summary>
        public int FencedSubmitCalls { get; private set; }

        public GpuBackendKind Backend => _inner.Backend;

        public GpuCapabilities Capabilities
        {
            get
            {
                GpuCapabilities c = _inner.Capabilities;
                if (!_suppressFences) return c;
                return new GpuCapabilities(c.ClipSpaceYInverted, c.DepthRangeZeroToOne, c.DeviceName,
                    c.SamplerAnisotropy, c.SamplerLodBias, c.MaxMsaaSampleCount, c.SupportsShadowMaps,
                    c.SupportsCompute, supportsCompletionFences: false);
            }
        }

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

        public void Submit(IGpuCommandList cl, IGpuFence fence)
        {
            FencedSubmitCalls++;
            _inner.Submit(cl, fence);
        }

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

        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuBuffer staging) => _inner.Unmap(staging);

        public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
        public void Present() => _inner.Present();

        // Non-owning: the wrapped device belongs to the GpuDeviceContext the test created it from, same contract
        // as the one VeldridGpuDevice's own non-owning wrapper carried until 18.0.0 (see
        // GpuDeviceContext.GpuDevice).
        public void Dispose() { }
    }
}
