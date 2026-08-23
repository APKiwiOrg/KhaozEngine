using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A device-free <see cref="IGpuDevice"/> that answers ONE question: does anything open a second command list
    /// while another one is recording? It wraps <see cref="FakeGpuDevice"/> (so nothing here touches a GPU),
    /// reports compute support, and hands out command lists that all share one open counter.
    /// <para>
    /// That counter is the headless stand-in for a real device fault. On the deleted Veldrid Direct3D11 leg's
    /// immediate-context mode a command list WAS the device's immediate context and <c>Begin</c> reset it, so a
    /// nested <c>Begin</c> silently invalidated every binding the outer list believed was live and the device
    /// faulted several draws later
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/423">#423</see>, and the latent sites in #424).
    /// The shape is cheap to assert on any machine, so it is asserted here rather than left to a WARP CI leg.
    /// </para>
    /// <para>
    /// <b>It stays after that leg's deletion</b>
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/690">#690</see>). The three native backends all
    /// pass it trivially, which is a reason it costs nothing and not a reason it is pointless: the question it
    /// answers is the SEAM's rule, not one backend's tolerance, and the engine still has to keep answering it.
    /// Read a pass here as "nothing nested", never as evidence about any backend.
    /// </para>
    /// </summary>
    internal sealed class OpenListTrackingGpuDevice : IGpuDevice
    {
        readonly FakeGpuDevice _inner = new();
        readonly TrackingFactory _factory;
        readonly bool _fences;

        /// <param name="completionFences">Report GPU completion fences, so a
        /// <c>GpuRetireBarrier</c> exists on this device and its own recording can be driven headless. Off by
        /// default, which is what every caller had before the barrier needed testing.</param>
        internal OpenListTrackingGpuDevice(bool completionFences = false)
        {
            _fences = completionFences;
            _factory = new TrackingFactory(_inner.Factory, this, completionFences);
            GpuCapabilities c = _inner.Capabilities;
            Capabilities = new GpuCapabilities(c.ClipSpaceYInverted, c.DepthRangeZeroToOne, c.DeviceName,
                c.SamplerAnisotropy, c.SamplerLodBias, c.MaxMsaaSampleCount, c.SupportsShadowMaps,
                supportsCompute: true, supportsCompletionFences: completionFences);
        }

        /// <summary>Command lists currently between <c>Begin</c> and <c>End</c>. Never above 1 on a correct
        /// caller.</summary>
        internal int OpenLists { get; private set; }

        /// <summary>The highest <see cref="OpenLists"/> ever reached. 2 or more means something opened a list
        /// inside another one's recording, which is the fault this harness exists to catch.</summary>
        internal int PeakOpenLists { get; private set; }

        /// <summary>How many times any list was opened, so a test can assert a priming pass ran exactly once.
        /// </summary>
        internal int Begins { get; private set; }

        /// <summary>How many command lists were submitted.</summary>
        internal int Submits { get; private set; }

        /// <summary>Textures handed out by this device's factory, and how many of them were freed. The difference
        /// is what a leak test watches across a call that throws: a refusal that walks past a texture nobody owns
        /// leaves one alive per attempt.</summary>
        internal int TexturesCreated { get; private set; }

        /// <summary>Textures freed. See <see cref="TexturesCreated"/>.</summary>
        internal int TexturesDisposed { get; private set; }

        /// <summary>Textures created and not yet freed.</summary>
        internal int TexturesAlive => TexturesCreated - TexturesDisposed;

        /// <summary>Device fences this factory created, so a test can prove a refused submission RECYCLED its
        /// fence instead of losing it (a lost one is invisible except as a fresh create next time).</summary>
        internal int FencesCreated { get; private set; }

        internal void NoteTextureCreated() => TexturesCreated++;

        internal void NoteTextureDisposed() => TexturesDisposed++;

        internal void NoteFenceCreated() => FencesCreated++;

        /// <summary>Run on this device's thread at the top of every <c>Begin</c>, before the open is counted.
        /// Null by default. A test sets it to block, which is what a real backend's Begin does when the GPU is
        /// behind (the Metal and Vulkan rings both wait there for a free slot), and is the only way to ask
        /// whether one device's stall reaches another's thread.</summary>
        internal Action? BeforeBegin { get; set; }

        internal void NoteBegin()
        {
            BeforeBegin?.Invoke();
            Begins++;
            OpenLists++;
            if (OpenLists > PeakOpenLists) PeakOpenLists = OpenLists;
        }

        internal void NoteEnd() => OpenLists--;

        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities { get; }
        public IGpuResourceFactory Factory => _factory;
        public IGpuFramebuffer? SwapchainFramebuffer => _inner.SwapchainFramebuffer;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public bool SyncToVerticalBlank { get => _inner.SyncToVerticalBlank; set => _inner.SyncToVerticalBlank = value; }

        public void Submit(IGpuCommandList cl) => Submits++;
        // With fences reported, a fenced submit is a real submit here. Without them, the fake's refusal is the
        // right answer and stays the answer.
        public void Submit(IGpuCommandList cl, IGpuFence fence)
        {
            if (_fences) Submits++;
            else _inner.Submit(cl, fence);
        }
        public void WaitForIdle() { }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height) { }
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer) { }

        public MappedData Map(IGpuTexture staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuTexture staging) { }
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuBuffer staging) { }

        public void ResizeSwapchain(uint w, uint h) { }
        public void Present() { }
        public void Dispose() => _inner.Dispose();

        /// <summary>Forwards to the fake factory, except that command lists report their open/close to the device
        /// and the two compute creates hand back inert handles (the fake factory throws for them, since it reports
        /// no compute).</summary>
        sealed class TrackingFactory : IGpuResourceFactory
        {
            readonly IGpuResourceFactory _inner;
            readonly OpenListTrackingGpuDevice _device;
            readonly bool _fences;

            internal TrackingFactory(IGpuResourceFactory inner, OpenListTrackingGpuDevice device, bool fences)
            {
                _inner = inner;
                _device = device;
                _fences = fences;
            }

            public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => _inner.CreateBuffer(d);
            public IGpuTexture CreateTexture(in GpuTextureDescription d)
                => new CountingGpuTexture(_inner.CreateTexture(d), _device);
            public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
                => _inner.CreateFramebuffer(depth, colour);
            public IGpuSampler CreateSampler(in GpuSamplerDescription d) => _inner.CreateSampler(d);
            public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
                => _inner.CreateResourceLayout(d);
            public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => _inner.CreateResourceSet(d);
            public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
                => _inner.CreateShadersFromSpirv(vertGlsl, fragGlsl);
            public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
                => _inner.CreateGraphicsPipeline(d);
            public IGpuCommandList CreateCommandList() => new TrackingCommandList(_device);
            public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl) => new StubComputeShader();
            public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
                => new StubComputePipeline();
            // The fake factory throws for this, which is the right answer when the device reports no fences.
            public IGpuFence CreateFence()
            {
                if (!_fences) return _inner.CreateFence();
                _device.NoteFenceCreated();
                return new StubFence();
            }
        }

        /// <summary>A fence that is never signaled, which is all a recording-order test needs from one.</summary>
        sealed class StubFence : IGpuFence
        {
            public bool Signaled => false;
            public void Reset() { }
            public void Dispose() { }
        }

        /// <summary>Drops every command like <c>NullGpuCommandList</c>, but tells the device when it opens and
        /// closes.</summary>
        sealed class TrackingCommandList : IGpuCommandList
        {
            readonly OpenListTrackingGpuDevice _device;

            internal TrackingCommandList(OpenListTrackingGpuDevice device) => _device = device;

            public void Begin() => _device.NoteBegin();
            public void End() => _device.NoteEnd();
            public void SetFramebuffer(IGpuFramebuffer fb) { }
            public void ClearColorTarget(uint index, Color rgba) { }
            public void ClearDepthStencil(float depth) { }
            public void SetPipeline(IGpuPipeline p) { }
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) { }
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
            public void SetVertexBuffer(uint slot, IGpuBuffer b) { }
            public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) { }
            public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) { }
            public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) { }
            public void SetFullScissorRects() { }
            public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart) { }
            public void Draw(uint vertexCount) { }
            public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart) { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
            public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes) { }
            public void CopyTexture(IGpuTexture src, IGpuTexture dst) { }
            public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height) { }
            public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
                IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height) { }
            public void GenerateMipmaps(IGpuTexture texture) { }
            public void ResolveTexture(IGpuTexture src, IGpuTexture dst) { }
            public void SetComputePipeline(IGpuComputePipeline p) { }
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set) { }
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) { }
            public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) { }
            public void Dispose() { }
        }

        sealed class StubComputeShader : IGpuComputeShader
        {
            public uint ThreadGroupSizeX => 1;
            public uint ThreadGroupSizeY => 1;
            public uint ThreadGroupSizeZ => 1;
            public void Dispose() { }
        }

        sealed class StubComputePipeline : IGpuComputePipeline { public void Dispose() { } }
    }
}
