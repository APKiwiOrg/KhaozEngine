using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ONE FACT <see cref="UniformRewriteAudit"/> CANNOT READ OFF A BUFFER: whether it was created with
    /// <see cref="GpuBufferUsage.UniformBuffer"/>. <see cref="IGpuBuffer"/> exposes a size and nothing else, and
    /// the usage is what decides whether a record-time write is a ring memcpy or a recorded copy, so the audit
    /// would either have to guess or to flag every vertex stream a frame legitimately re-streams. This decorator
    /// answers it exactly, by remembering what each buffer was asked for at the one place that knows.
    ///
    /// <para><b>A DECORATOR AND NOT A FAKE.</b> There is no standalone fake resource factory in this assembly, and
    /// a renderer under audit has to build real pipelines against a real device or the frame it records is not the
    /// frame that ships. Everything forwards untouched, so the scene renders exactly as it would have, which is
    /// the same trade <see cref="SpyGpuDevice"/> already makes.</para>
    ///
    /// <para><b>IDENTITY IS PRESERVED, WHICH IS LOAD-BEARING.</b> The factory hands back the REAL buffer rather
    /// than a wrapper, so the native backends' ownership and liveness checks still recognise it, and the audit's
    /// reference comparison between an upload's destination and a tracked buffer is comparing the same object.
    /// </para>
    ///
    /// <para><b>IT ALSO REMEMBERS WHAT EACH RESOURCE SET READS</b> (<see cref="UniformWindowIndex"/>), because the
    /// audit's rule is about the bytes an intervening draw actually bound and neither the layout's dynamic flags
    /// nor a set's bound resources can be read back off the handles. <see cref="WindowsOf"/> is what a
    /// <see cref="RecordingGpuCommandList"/> is given so its draws can stamp the windows they bind.</para>
    /// </summary>
    internal sealed class UniformBufferTrackingGpuDevice : IGpuDevice
    {
        readonly IGpuDevice _inner;
        readonly TrackingFactory _factory;

        public UniformBufferTrackingGpuDevice(IGpuDevice inner)
        {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
            _factory = new TrackingFactory(inner.Factory);
        }

        /// <summary>Whether this buffer was created through this device with the uniform bit set. False for a
        /// buffer created before the decorator existed or through the undecorated device, which is why the guard
        /// builds its scene on the decorator rather than wrapping one afterwards.</summary>
        public bool IsUniform(IGpuBuffer buffer) => _factory.IsUniform(buffer);

        /// <summary>How many uniform buffers have been created through this device. A guard asserts this is
        /// non-zero before believing an empty hazard list, because a scan that recognised nothing as a uniform
        /// buffer would also come back empty.</summary>
        public int UniformBufferCount => _factory.UniformBufferCount;

        /// <summary>The uniform windows a resource set binds, unrebased. Hand this to
        /// <see cref="RecordingGpuCommandList.UniformWindowsOfSet"/> before recording the frame.</summary>
        public IReadOnlyList<UniformWindow> WindowsOf(IGpuResourceSet set) => _factory.Windows.WindowsOf(set);

        /// <summary>How many resource sets were built against a layout created outside this device, whose windows
        /// are therefore over-reported. A guard asserts this is zero, because an over-reported window can turn a
        /// safe rewrite into a reported hazard.</summary>
        public int UnresolvedResourceSets => _factory.Windows.SetsWithUnknownLayout;

        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _inner.Capabilities;
        public IGpuResourceFactory Factory => _factory;
        public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public GpuDeviceDiagnostics Diagnostics => _inner.Diagnostics;
        public GpuDeviceCounters Counters => _inner.Counters;

        public bool SyncToVerticalBlank
        {
            get => _inner.SyncToVerticalBlank;
            set => _inner.SyncToVerticalBlank = value;
        }

        public void Submit(IGpuCommandList cl) => _inner.Submit(cl);
        public void Submit(IGpuCommandList cl, IGpuFence fence) => _inner.Submit(cl, fence);
        public void WaitForIdle() => _inner.WaitForIdle();

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => _inner.UpdateBuffer(b, offsetBytes, in data);

        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => _inner.UpdateTexture(texture, data, x, y, width, height);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
            => _inner.UpdateTexture(texture, data, x, y, width, height, mipLevel, arrayLayer);

        public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuTexture staging) => _inner.Unmap(staging);
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuBuffer staging) => _inner.Unmap(staging);

        public void ResizeSwapchain(uint w, uint h) => _inner.ResizeSwapchain(w, h);
        public void Present() => _inner.Present();

        // Non-owning: the wrapped device belongs to the GpuDeviceContext the test created it from, the same
        // contract SpyGpuDevice keeps.
        public void Dispose() { }

        /// <summary>The factory that does the remembering. Every member forwards; only
        /// <see cref="CreateBuffer"/> notes anything down.</summary>
        sealed class TrackingFactory : IGpuResourceFactory
        {
            readonly IGpuResourceFactory _inner;

            // Reference identity, and it does NOT keep the buffers alive: a renderer that retires and replaces a
            // grown buffer would otherwise pile up here for the life of the scene.
            readonly ConditionalWeakTable<IGpuBuffer, object> _uniform = new();
            int _uniformCount;

            internal TrackingFactory(IGpuResourceFactory inner) => _inner = inner;

            internal bool IsUniform(IGpuBuffer buffer) => _uniform.TryGetValue(buffer, out _);

            internal int UniformBufferCount => _uniformCount;

            internal UniformWindowIndex Windows { get; } = new();

            public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
            {
                IGpuBuffer buffer = _inner.CreateBuffer(in d);
                if ((d.Usage & GpuBufferUsage.UniformBuffer) != 0)
                {
                    _uniform.Add(buffer, Marker);
                    _uniformCount++;
                }

                return buffer;
            }

            public IGpuTexture CreateTexture(in GpuTextureDescription d) => _inner.CreateTexture(in d);
            public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
                => _inner.CreateFramebuffer(depth, colour);
            public IGpuSampler CreateSampler(in GpuSamplerDescription d) => _inner.CreateSampler(in d);
            public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
            {
                IGpuResourceLayout layout = _inner.CreateResourceLayout(in d);
                Windows.NoteLayout(layout, in d);
                return layout;
            }

            public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
            {
                IGpuResourceSet set = _inner.CreateResourceSet(in d);
                Windows.NoteSet(set, in d, IsUniform);
                return set;
            }
            public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
                => _inner.CreateShadersFromSpirv(vertGlsl, fragGlsl);
            public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl)
                => _inner.CreateComputeShaderFromSpirv(computeGlsl);
            public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
                => _inner.CreateGraphicsPipeline(in d);
            public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
                => _inner.CreateComputePipeline(in d);
            public IGpuCommandList CreateCommandList() => _inner.CreateCommandList();
            public IGpuFence CreateFence() => _inner.CreateFence();

            static readonly object Marker = new();
        }
    }
}
