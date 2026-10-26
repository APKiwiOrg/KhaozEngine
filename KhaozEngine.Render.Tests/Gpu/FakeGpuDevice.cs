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

        internal FakeGpuDevice(GpuBackendKind backend = GpuBackendKind.Vulkan, bool supportsShadowMaps = true,
            int maxColorAttachments = 8, int maxMsaaSampleCount = 1, GpuCapabilities? capabilityOverride = null)
        {
            Backend = backend;
            Capabilities = capabilityOverride ?? new GpuCapabilities(
                clipSpaceYInverted: false, depthRangeZeroToOne: true, deviceName: "FakeGpuDevice",
                samplerAnisotropy: false, samplerLodBias: false, maxMsaaSampleCount: maxMsaaSampleCount,
                supportsShadowMaps: supportsShadowMaps, supportsCompute: false, supportsCompletionFences: false)
            { MaxColorAttachments = maxColorAttachments };
            _factory = new FakeGpuResourceFactory(maxColorAttachments);
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
        public void Submit(IGpuCommandList cl, ReadOnlySpan<IGpuFence> completions)
        {
            GpuCompletionFences.Validate(completions);
            if (!completions.IsEmpty) Submit(cl, completions[0]);
            else Submit(cl);
        }
        /// <summary>How many times <see cref="WaitForIdle"/> was called. A full GPU stall is the most expensive
        /// thing a resource transaction does, so a test asserting that a retry was skipped asserts on this.</summary>
        internal int WaitForIdleCalls { get; private set; }

        /// <summary>Runs inside every <see cref="WaitForIdle"/>, so a test can read what was already freed when a
        /// drain began and pin that the drain came first.</summary>
        internal Action? OnWaitForIdle { get; set; }

        public void WaitForIdle()
        {
            WaitForIdleCalls++;
            OnWaitForIdle?.Invoke();
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
        /// <summary>Observes or refuses an upload at the device boundary without replacing resource ownership.</summary>
        internal Action<IGpuTexture, byte[]>? OnTextureUpload { get; set; }

        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => OnTextureUpload?.Invoke(texture, data);
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
            => OnTextureUpload?.Invoke(texture, data);

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
        readonly int _maxColorAttachments;
        internal FakeGpuResourceFactory(int maxColorAttachments = 8) => _maxColorAttachments = maxColorAttachments;
        /// <summary>Every resource set this factory has handed out, in creation order, so a test can ask WHEN one
        /// was freed rather than only whether the count changed. That is the question deferred retirement raises:
        /// a set dropped from a cache and a set actually destroyed are no longer the same moment (#84).</summary>
        internal List<FakeResourceSet> ResourceSets { get; } = new();

        /// <summary>Every texture this factory has handed out, in creation order. Same job as
        /// <see cref="ResourceSets"/>: deferred retirement made "dropped by its owner" and "actually destroyed"
        /// two different moments, so a test asserting WHEN a mid-life unload frees something needs the object
        /// itself and not just a count (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/383">#383</see>).</summary>
        internal List<FakeTexture> Textures { get; } = new();

        /// <summary>Absolute 1-based texture creation to fail, or zero for no injected failure.</summary>
        internal int ThrowOnTextureCreate { get; set; }
        internal int ThrowOnFramebufferCreate { get; set; }
        internal int ThrowOnResourceSetCreate { get; set; }
        /// <summary>Absolute 1-based buffer creation to fail, or zero for no injected failure.</summary>
        internal int ThrowOnBufferCreate { get; set; }
        internal int ThrowOnShaderCreate { get; set; }
        internal int ThrowOnPipelineCreate { get; set; }
        internal List<FakePipeline> Pipelines { get; } = new();

        internal void FailBufferCreationAfter(int successfulCreates) =>
            ThrowOnBufferCreate = Buffers.Count + successfulCreates + 1;
        internal void FailTextureCreationAfter(int successfulCreates) =>
            ThrowOnTextureCreate = Textures.Count + successfulCreates + 1;

        /// <summary>Every buffer this factory has handed out, in creation order. See <see cref="Textures"/>.</summary>
        internal List<FakeBuffer> Buffers { get; } = new();
        internal List<FakeFramebuffer> Framebuffers { get; } = new();

        /// <summary>Every graphics pipeline request with the GLSL source carried by its real shader-set handle.</summary>
        internal List<FakeGraphicsPipelineRequest> GraphicsPipelines { get; } = new();

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

        /// <summary>How many LIVE sets name a texture that has since been freed. A resource set is immutable, so
        /// a texture swapped out from under one leaves the old handle in it for ever, and the only observable is
        /// the pairing: the set is still bound by its holder and the handle in it is gone. Zero is the only
        /// healthy answer, and a count no test can read is how a use-after-free ships.</summary>
        internal int LiveSetsNamingAFreedTexture
        {
            get
            {
                int n = 0;
                foreach (FakeResourceSet set in ResourceSets)
                {
                    if (set.Disposed) continue;
                    foreach (IGpuBindableResource resource in set.Resources)
                    {
                        if (resource is not FakeTexture { Disposed: true }) continue;
                        n++;
                        break;
                    }
                }
                return n;
            }
        }

        public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
        {
            if (ThrowOnBufferCreate == Buffers.Count + 1)
                throw new InvalidOperationException("planned buffer creation failure");
            var b = new FakeBuffer(d.SizeInBytes, d.StructureByteStride, d.Usage);
            Buffers.Add(b);
            return b;
        }

        public IGpuTexture CreateTexture(in GpuTextureDescription d)
        {
            if (ThrowOnTextureCreate == Textures.Count + 1)
                throw new InvalidOperationException("planned texture creation failure");
            var t = new FakeTexture(d.Width, d.Height, d.MipLevels, d.SampleCount, d.Format, d.Usage, d.ArrayLayers,
                d.IsArray);
            Textures.Add(t);
            return t;
        }

        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
        {
            if (colour.Length > _maxColorAttachments) throw new InvalidOperationException("Framebuffer exceeds attachment capability.");
            if (ThrowOnFramebufferCreate == Framebuffers.Count + 1)
                throw new InvalidOperationException("the fake refused framebuffer creation");
            var formats = new GpuPixelFormat[colour.Length];
            for (int i = 0; i < colour.Length; i++) formats[i] = colour[i].Format;
            var outputs = new GpuOutputDescription(depth?.Format, formats);
            IGpuTexture? first = colour.Length > 0 ? colour[0] : depth;
            uint w = first?.Width ?? 1, h = first?.Height ?? 1;
            uint samples = first?.SampleCount ?? 1;
            var framebuffer = new FakeFramebuffer(samples > 1 ? outputs.WithSampleCount((int)samples) : outputs, w, h,
                (IGpuTexture[])colour.Clone());
            Framebuffers.Add(framebuffer);
            return framebuffer;
        }

        public IGpuSampler CreateSampler(in GpuSamplerDescription d) => new FakeSampler();
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => new FakeResourceLayout(d);
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
        {
            if (ThrowOnResourceSetCreate == ResourceSets.Count + 1)
                throw new InvalidOperationException("planned resource-set creation failure");
            var set = new FakeResourceSet(d.Resources, d.Layout);
            ResourceSets.Add(set);
            return set;
        }
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
        {
            ShaderRequests.Add(new FakeShaderRequest(vertGlsl, fragGlsl));
            if (ThrowOnShaderCreate == ShaderRequests.Count)
                throw new InvalidOperationException("planned shader creation failure");
            var shaders = new FakeShaderSet(vertGlsl, fragGlsl);
            ShaderSets.Add(shaders);
            return shaders;
        }

        /// <summary>Every shader set handed out, in request order, so a test can ask whether its owner freed it.</summary>
        internal List<FakeShaderSet> ShaderSets { get; } = new();

        /// <summary>Every shader set requested, in request order, whether or not a pipeline was built from it, so a
        /// test can see a program compiled that no pipeline uses.</summary>
        internal List<FakeShaderRequest> ShaderRequests { get; } = new();

        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
        {
            if (d.Outputs.Colour.Length > _maxColorAttachments) throw new InvalidOperationException("Pipeline exceeds attachment capability.");
            if (d.ShaderSet is not FakeShaderSet shaders) return new FakePipeline();
            var request = new FakeGraphicsPipelineRequest(shaders.VertexGlsl, shaders.FragmentGlsl, d);
            GraphicsPipelines.Add(request);
            if (ThrowOnPipelineCreate == GraphicsPipelines.Count)
                throw new InvalidOperationException("planned pipeline creation failure");
            var pipeline = new FakePipeline(request);
            Pipelines.Add(pipeline);
            return pipeline;
        }
        public IGpuCommandList CreateCommandList()
        {
            var cl = new NullGpuCommandList();
            CommandLists.Add(cl);
            return cl;
        }

        /// <summary>Every command list this factory handed out, so a test can walk their recorded mip
        /// generations.</summary>
        internal List<NullGpuCommandList> CommandLists { get; } = new();

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
        internal FakeBuffer(uint sizeInBytes, uint structureByteStride = 0, GpuBufferUsage usage = default)
        {
            SizeInBytes = sizeInBytes;
            StructureByteStride = structureByteStride;
            Usage = usage;
        }
        public uint SizeInBytes { get; }
        internal uint StructureByteStride { get; }
        internal GpuBufferUsage Usage { get; }

        /// <summary>Whether the owner freed this handle, the same one thing <see cref="FakeTexture"/> records and
        /// for the same reason: it is what pins the frame a retired buffer is actually destroyed on.</summary>
        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeTexture : IGpuTexture
    {
        internal FakeTexture(uint w, uint h, uint mips, uint samples, GpuPixelFormat format,
            GpuTextureUsage usage = GpuTextureUsage.Sampled, uint arrayLayers = 1, bool isArray = false)
        {
            Width = w; Height = h; MipLevels = mips < 1 ? 1 : mips; SampleCount = samples < 1 ? 1 : samples;
            Format = format; Usage = usage; ArrayLayers = arrayLayers < 1 ? 1 : arrayLayers;
            IsArray = isArray || ArrayLayers > 1;
        }

        /// <summary>The layer count the texture was created with, so a test can pin what actually reached the
        /// device rather than what the caller meant.</summary>
        internal uint ArrayLayers { get; }

        /// <summary>The description's <see cref="GpuTextureDescription.IsArray"/>, so a test can pin that a
        /// ONE-layer set reaches the device as a one-layer ARRAY rather than as a padded two-layer one or as a
        /// plain 2D texture (#666).</summary>
        internal bool IsArray { get; }

        /// <summary>The usage flags the texture was created with, so a test can pin that a single-level texture
        /// never declares GenerateMipmaps (the native Vulkan backend refuses generation with nothing to generate).</summary>
        internal GpuTextureUsage Usage { get; }

        public uint Width { get; }
        public uint Height { get; }
        public uint MipLevels { get; }
        public uint SampleCount { get; }
        public GpuPixelFormat Format { get; }

        /// <summary>Whether the owner freed this handle. The one thing the fake records about a texture, because
        /// an unfreed one is a real defect off this harness: the native Vulkan backend reports it at device
        /// teardown as a VUID-vkDestroyDevice-device-05137 object leak (#618).</summary>
        internal bool Disposed { get; private set; }

        internal Action? OnDispose { get; set; }

        public void Dispose()
        {
            OnDispose?.Invoke();
            Disposed = true;
        }
    }

    internal sealed class FakeFramebuffer : IGpuFramebuffer
    {
        internal FakeFramebuffer(GpuOutputDescription outputs, uint width, uint height, IGpuTexture[]? colour = null)
        {
            Outputs = outputs; Width = width; Height = height;
            Colour = colour ?? Array.Empty<IGpuTexture>();
        }

        public GpuOutputDescription Outputs { get; }
        public uint Width { get; }
        public uint Height { get; }

        /// <summary>The colour attachments the factory was handed, in order, so a test can tell one framebuffer from
        /// another by what it writes. Empty for one built directly.</summary>
        internal IGpuTexture[] Colour { get; }

        /// <summary>Whether the owner freed this framebuffer, to pin the frame a retired one is destroyed on, as
        /// <see cref="FakeResourceSet.Disposed"/> does for a set.</summary>
        internal bool Disposed { get; private set; }

        internal Action? OnDispose { get; set; }
        public void Dispose() { OnDispose?.Invoke(); Disposed = true; }
    }

    internal sealed class FakeSampler : IGpuSampler { public void Dispose() { } }
    internal sealed class FakeResourceLayout(GpuResourceLayoutDescription description = default) : IGpuResourceLayout
    {
        internal GpuResourceLayoutDescription Description { get; } = description;
        public void Dispose() { }
    }

    internal sealed class FakeResourceSet : IGpuResourceSet
    {
        // Default empty for the tests that hand a set straight to a backend and never ask what is in it.
        internal FakeResourceSet(IGpuBindableResource[]? resources = null, IGpuResourceLayout? layout = null)
        {
            Resources = resources ?? Array.Empty<IGpuBindableResource>();
            Layout = layout;
        }

        internal IGpuResourceLayout? Layout { get; }

        /// <summary>What was bound into this set, in binding order. Kept because a set outlives the handles in
        /// it: see <see cref="FakeGpuResourceFactory.LiveSetsNamingAFreedTexture"/>.</summary>
        internal IGpuBindableResource[] Resources { get; }

        /// <summary>Whether the owner freed this binding. Paired with
        /// <see cref="FakeGpuResourceFactory.ResourceSets"/> to pin the frame a retired set is destroyed on.</summary>
        internal bool Disposed { get; private set; }

        internal Action? OnDispose { get; set; }
        internal int DisposeCalls { get; private set; }
        public void Dispose() { DisposeCalls++; OnDispose?.Invoke(); Disposed = true; }
    }

    internal sealed class FakeShaderSet : IGpuShaderSet
    {
        internal FakeShaderSet(string vertexGlsl, string fragmentGlsl)
        {
            VertexGlsl = vertexGlsl;
            FragmentGlsl = fragmentGlsl;
        }

        internal string VertexGlsl { get; }
        internal string FragmentGlsl { get; }

        /// <summary>Whether the owner freed this program.</summary>
        internal bool Disposed { get; private set; }

        internal int DisposeCount { get; private set; }
        public void Dispose() { DisposeCount++; Disposed = true; }
    }

    internal readonly record struct FakeShaderRequest(string VertexGlsl, string FragmentGlsl);
    internal readonly record struct FakeGraphicsPipelineRequest(
        string VertexGlsl, string FragmentGlsl, GpuPipelineDescription Description);
    internal sealed class FakePipeline : IGpuPipeline
    {
        internal FakePipeline(FakeGraphicsPipelineRequest? request = null) => Request = request;

        /// <summary>The request that built this pipeline, so a test reading which pipelines a pass BOUND can tell
        /// them apart by their shader sources. Null for a pipeline a test made directly.</summary>
        internal FakeGraphicsPipelineRequest? Request { get; }

        internal int DisposeCount { get; private set; }
        internal bool Disposed => DisposeCount != 0;
        public void Dispose() => DisposeCount++;
    }

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
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
        { }
        public void GenerateMipmaps(IGpuTexture texture) => MipGenerations.Add(texture);

        /// <summary>Every texture a GenerateMipmaps call named, so a test can pin that a single-level upload
        /// never asks for a generation pass.</summary>
        internal List<IGpuTexture> MipGenerations { get; } = new();
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
