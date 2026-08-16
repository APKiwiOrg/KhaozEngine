using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A device-free <see cref="IGpuDevice"/>: every resource is an inert handle that remembers only what a
    /// renderer can legitimately read back off it (a buffer's size, a texture's dimensions and format, a
    /// framebuffer's outputs), and every command is dropped. Nothing here touches a GPU, a driver, or a native
    /// library, so a renderer can be driven under a plain <c>dotnet test</c> on any machine and any OS.
    /// <para>
    /// This exists for ONE job: letting a test assert the SHAPE of what a renderer records (see
    /// <see cref="CommandTallyGpuCommandList"/>). It renders nothing, so it can never replace a
    /// <c>[GpuFact]</c> golden. It is the counting harness, not a software rasterizer.
    /// </para>
    /// <para>
    /// The capabilities are deliberately the LEAST capable device the engine still supports: no compute, no
    /// completion fences, no anisotropy, single-sample. That is what keeps the recorded command counts stable
    /// and backend-independent. A renderer path gated on a capability this fake reports false is simply not
    /// covered, and the test that uses it says so.
    /// </para>
    /// </summary>
    internal sealed class FakeGpuDevice : IGpuDevice
    {
        readonly FakeGpuResourceFactory _factory;

        internal FakeGpuDevice(GpuBackendKind backend = GpuBackendKind.Vulkan, bool supportsShadowMaps = true)
        {
            Backend = backend;
            Capabilities = new GpuCapabilities(
                clipSpaceYInverted: false, depthRangeZeroToOne: true, deviceName: "FakeGpuDevice",
                samplerAnisotropy: false, samplerLodBias: false, maxMsaaSampleCount: 1,
                supportsShadowMaps: supportsShadowMaps, supportsCompute: false, supportsCompletionFences: false);
            _factory = new FakeGpuResourceFactory();
            PointSampler = new FakeSampler();
            LinearSampler = new FakeSampler();
        }

        public GpuBackendKind Backend { get; }
        public GpuCapabilities Capabilities { get; }
        public IGpuResourceFactory Factory => _factory;
        public IGpuFramebuffer? SwapchainFramebuffer => null;
        public IGpuSampler PointSampler { get; }
        public IGpuSampler LinearSampler { get; }
        public bool SyncToVerticalBlank { get; set; }

        public void Submit(IGpuCommandList cl) { }
        public void Submit(IGpuCommandList cl, IGpuFence fence)
            => throw new NotSupportedException("FakeGpuDevice reports SupportsCompletionFences = false.");
        public void WaitForIdle() { }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height) { }
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer) { }

        // Readback is the one thing a fake genuinely cannot do: there are no pixels behind it. Throwing beats
        // handing back zeros, which would let a snapshot test "pass" against a black image.
        public MappedData Map(IGpuTexture staging, GpuMapMode mode)
            => throw new NotSupportedException("FakeGpuDevice has no pixels to map. Use a real device for readback.");
        public void Unmap(IGpuTexture staging) { }
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode)
            => throw new NotSupportedException("FakeGpuDevice has no memory to map. Use a real device for readback.");
        public void Unmap(IGpuBuffer staging) { }

        public void ResizeSwapchain(uint w, uint h) { }
        public void Present() { }
        public void Dispose() { }
    }

    /// <summary>Hands out the inert resource handles <see cref="FakeGpuDevice"/> is built on.</summary>
    internal sealed class FakeGpuResourceFactory : IGpuResourceFactory
    {
        /// <summary>Every resource set this factory has handed out, in creation order, so a test can ask WHEN one
        /// was freed rather than only whether the count changed. That is the question deferred retirement raises:
        /// a set dropped from a cache and a set actually destroyed are no longer the same moment (#84).</summary>
        internal List<FakeResourceSet> ResourceSets { get; } = new();

        /// <summary>How many of <see cref="ResourceSets"/> have been disposed.</summary>
        internal int DisposedResourceSetCount
        {
            get
            {
                int n = 0;
                foreach (FakeResourceSet s in ResourceSets) if (s.Disposed) n++;
                return n;
            }
        }

        public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => new FakeBuffer(d.SizeInBytes);
        public IGpuTexture CreateTexture(in GpuTextureDescription d)
            => new FakeTexture(d.Width, d.Height, d.MipLevels, d.SampleCount, d.Format);

        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
        {
            var formats = new GpuPixelFormat[colour.Length];
            for (int i = 0; i < colour.Length; i++) formats[i] = colour[i].Format;
            var outputs = new GpuOutputDescription(depth?.Format, formats);
            IGpuTexture? first = colour.Length > 0 ? colour[0] : depth;
            uint w = first?.Width ?? 1, h = first?.Height ?? 1;
            uint samples = first?.SampleCount ?? 1;
            return new FakeFramebuffer(samples > 1 ? outputs.WithSampleCount((int)samples) : outputs, w, h);
        }

        public IGpuSampler CreateSampler(in GpuSamplerDescription d) => new FakeSampler();
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => new FakeResourceLayout();
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
        {
            var set = new FakeResourceSet();
            ResourceSets.Add(set);
            return set;
        }
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl) => new FakeShaderSet();
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => new FakePipeline();
        public IGpuCommandList CreateCommandList() => new NullGpuCommandList();

        // The three the fake device reports it cannot do. Each throws the same way the real factory does, so a
        // renderer that forgot to gate on the capability fails here rather than silently taking a path the
        // counting harness was never meant to measure.
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl)
            => throw new NotSupportedException("FakeGpuDevice reports SupportsCompute = false.");
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
            => throw new NotSupportedException("FakeGpuDevice reports SupportsCompute = false.");
        public IGpuFence CreateFence()
            => throw new NotSupportedException("FakeGpuDevice reports SupportsCompletionFences = false.");
    }

    internal sealed class FakeBuffer : IGpuBuffer
    {
        internal FakeBuffer(uint sizeInBytes) => SizeInBytes = sizeInBytes;
        public uint SizeInBytes { get; }
        public void Dispose() { }
    }

    internal sealed class FakeTexture : IGpuTexture
    {
        internal FakeTexture(uint w, uint h, uint mips, uint samples, GpuPixelFormat format)
        {
            Width = w; Height = h; MipLevels = mips < 1 ? 1 : mips; SampleCount = samples < 1 ? 1 : samples;
            Format = format;
        }

        public uint Width { get; }
        public uint Height { get; }
        public uint MipLevels { get; }
        public uint SampleCount { get; }
        public GpuPixelFormat Format { get; }

        /// <summary>Whether the owner freed this handle. The one thing the fake records about a texture, because
        /// an unfreed one is a real defect off this harness: the native Vulkan backend reports it at device
        /// teardown as a VUID-vkDestroyDevice-device-05137 object leak (#618).</summary>
        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeFramebuffer : IGpuFramebuffer
    {
        internal FakeFramebuffer(GpuOutputDescription outputs, uint width, uint height)
        {
            Outputs = outputs; Width = width; Height = height;
        }

        public GpuOutputDescription Outputs { get; }
        public uint Width { get; }
        public uint Height { get; }
        public void Dispose() { }
    }

    internal sealed class FakeSampler : IGpuSampler { public void Dispose() { } }
    internal sealed class FakeResourceLayout : IGpuResourceLayout { public void Dispose() { } }

    internal sealed class FakeResourceSet : IGpuResourceSet
    {
        /// <summary>Whether the owner freed this binding. Paired with
        /// <see cref="FakeGpuResourceFactory.ResourceSets"/> to pin the frame a retired set is destroyed on.</summary>
        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeShaderSet : IGpuShaderSet { public void Dispose() { } }
    internal sealed class FakePipeline : IGpuPipeline { public void Dispose() { } }

    /// <summary>Drops every recorded command. The terminal sink <see cref="CommandTallyGpuCommandList"/> forwards
    /// to when there is no real device behind it.</summary>
    internal sealed class NullGpuCommandList : IGpuCommandList
    {
        public void Begin() { }
        public void End() { }
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

    /// <summary>Shared list of the command kinds <see cref="CommandTallyGpuCommandList"/> counts. Ordered the way
    /// a failure message reads best: uploads first (the historical regression), then binds, then draws.</summary>
    internal enum GpuCommandKind
    {
        UpdateBuffer,
        SetPipeline,
        SetGraphicsResourceSet,
        SetVertexBuffer,
        SetIndexBuffer,
        SetFramebuffer,
        ClearColorTarget,
        ClearDepthStencil,
        Draw,
        DrawIndexed,
        CopyTexture,
        ResolveTexture,
        Dispatch,
    }

    /// <summary>A tally of recorded commands by kind, with a stable printable form for assertion messages.</summary>
    internal sealed class GpuCommandTally
    {
        readonly Dictionary<GpuCommandKind, int> _counts = new();

        internal void Add(GpuCommandKind kind)
            => _counts[kind] = _counts.TryGetValue(kind, out int n) ? n + 1 : 1;

        internal void Clear() => _counts.Clear();

        internal int this[GpuCommandKind kind] => _counts.TryGetValue(kind, out int n) ? n : 0;

        public override string ToString()
        {
            var parts = new List<string>();
            foreach (GpuCommandKind kind in Enum.GetValues<GpuCommandKind>())
                if (this[kind] > 0) parts.Add($"{kind}={this[kind]}");
            return parts.Count == 0 ? "(nothing recorded)" : string.Join(", ", parts);
        }
    }
}
