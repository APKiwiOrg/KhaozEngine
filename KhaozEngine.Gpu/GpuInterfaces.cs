using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu
{
    /// <summary>A CPU-mapped view of a staging resource for readback. Engine mirror of Veldrid
    /// <c>MappedResource</c>: the base pointer, the row pitch (bytes per row, may exceed Width*bpp), and the
    /// total mapped size.</summary>
    public readonly struct MappedData
    {
        /// <summary>Base pointer of the mapped region.</summary>
        public IntPtr Data { get; }
        /// <summary>Bytes between consecutive rows (may be padded beyond the logical row width).</summary>
        public uint RowPitch { get; }
        /// <summary>Total mapped size in bytes.</summary>
        public uint SizeInBytes { get; }

        public MappedData(IntPtr data, uint rowPitch, uint sizeInBytes)
        {
            Data = data; RowPitch = rowPitch; SizeInBytes = sizeInBytes;
        }
    }

    /// <summary>Marker for anything bindable into a <see cref="GpuResourceSetDescription"/>:
    /// <see cref="IGpuBuffer"/>, <see cref="IGpuTexture"/>, or <see cref="IGpuSampler"/>.</summary>
    public interface IGpuBindableResource { }

    /// <summary>A GPU buffer handle (vertex / index / uniform). Engine wrapper over Veldrid <c>DeviceBuffer</c>.</summary>
    public interface IGpuBuffer : IGpuBindableResource, IDisposable
    {
        /// <summary>Buffer size in bytes.</summary>
        uint SizeInBytes { get; }
    }

    /// <summary>A GPU texture handle. Engine wrapper over Veldrid <c>Texture</c>; exposes its dimensions and
    /// format for pipeline / framebuffer reasoning.</summary>
    public interface IGpuTexture : IGpuBindableResource, IDisposable
    {
        /// <summary>Texel width.</summary>
        uint Width { get; }
        /// <summary>Texel height.</summary>
        uint Height { get; }
        /// <summary>Mip-level count (1 == level 0 only, no mip chain).</summary>
        uint MipLevels { get; }
        /// <summary>MSAA sample count (1 == single-sample). &gt; 1 is a multisampled render target that must be
        /// resolved (<see cref="IGpuCommandList.ResolveTexture"/>) into a single-sample texture before sampling.</summary>
        uint SampleCount { get; }
        /// <summary>Pixel format.</summary>
        GpuPixelFormat Format { get; }
    }

    /// <summary>A GPU sampler handle. Engine wrapper over Veldrid <c>Sampler</c>.</summary>
    public interface IGpuSampler : IGpuBindableResource, IDisposable { }

    /// <summary>A render-target framebuffer handle. Engine wrapper over Veldrid <c>Framebuffer</c>; exposes its
    /// <see cref="GpuOutputDescription"/> so a matching pipeline can be created.</summary>
    public interface IGpuFramebuffer : IDisposable
    {
        /// <summary>The attachment formats of this framebuffer (for pipeline <c>Outputs</c>).</summary>
        GpuOutputDescription Outputs { get; }
        /// <summary>Framebuffer width in pixels.</summary>
        uint Width { get; }
        /// <summary>Framebuffer height in pixels.</summary>
        uint Height { get; }
    }

    /// <summary>A graphics pipeline handle. Engine wrapper over Veldrid <c>Pipeline</c>.</summary>
    public interface IGpuPipeline : IDisposable { }

    /// <summary>A compute pipeline handle. Engine wrapper over the Veldrid <c>Pipeline</c> a
    /// <c>ComputePipelineDescription</c> produces. A distinct type from <see cref="IGpuPipeline"/> on purpose:
    /// Veldrid has one <c>Pipeline</c> type and one <c>SetPipeline</c> for both kinds, so binding a compute
    /// pipeline for a draw is a runtime error there and a compile error here.</summary>
    public interface IGpuComputePipeline : IDisposable { }

    /// <summary>A resource-layout handle (binding-slot shape). Engine wrapper over Veldrid <c>ResourceLayout</c>.</summary>
    public interface IGpuResourceLayout : IDisposable { }

    /// <summary>A bound resource set handle. Engine wrapper over Veldrid <c>ResourceSet</c>.</summary>
    public interface IGpuResourceSet : IDisposable { }

    /// <summary>A compiled shader set (vertex + fragment) handle. Engine wrapper over the Veldrid
    /// <c>Shader[]</c> a SPIR-V cross-compile produces.</summary>
    public interface IGpuShaderSet : IDisposable { }

    /// <summary>A compiled compute shader handle (the single-stage sibling of <see cref="IGpuShaderSet"/>).
    /// Engine wrapper over the Veldrid <c>Shader</c> a single-stage SPIR-V cross-compile produces, plus the
    /// workgroup size read out of the module itself.</summary>
    public interface IGpuComputeShader : IDisposable
    {
        /// <summary>Workgroup size on X, read from the shader's own <c>layout(local_size_x = ...)</c>. Cover N
        /// threads with <c>(N + ThreadGroupSizeX - 1) / ThreadGroupSizeX</c> groups in
        /// <see cref="IGpuCommandList.Dispatch"/>.</summary>
        uint ThreadGroupSizeX { get; }
        /// <summary>Workgroup size on Y (1 unless the shader declares <c>local_size_y</c>).</summary>
        uint ThreadGroupSizeY { get; }
        /// <summary>Workgroup size on Z (1 unless the shader declares <c>local_size_z</c>).</summary>
        uint ThreadGroupSizeZ { get; }
    }

    /// <summary>Creates GPU resources. Engine mirror of Veldrid <c>ResourceFactory</c> (the subset used).</summary>
    public interface IGpuResourceFactory
    {
        /// <summary>Create a buffer.</summary>
        IGpuBuffer CreateBuffer(in GpuBufferDescription d);
        /// <summary>Create a 2D texture.</summary>
        IGpuTexture CreateTexture(in GpuTextureDescription d);
        /// <summary>Create a framebuffer over an optional depth + colour textures.</summary>
        IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour);
        /// <summary>Create a sampler.</summary>
        IGpuSampler CreateSampler(in GpuSamplerDescription d);
        /// <summary>Create a resource layout.</summary>
        IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d);
        /// <summary>Create a resource set.</summary>
        IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d);
        /// <summary>Cross-compile GLSL 450 SPIR-V vertex + fragment sources (entry point <c>main</c>) into a
        /// backend shader set. Wraps <c>Veldrid.SPIRV.CreateFromSpirv</c>.</summary>
        IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl);
        /// <summary>Cross-compile a GLSL 450 SPIR-V COMPUTE source (entry point <c>main</c>) into a backend compute
        /// shader. Wraps the single-stage <c>Veldrid.SPIRV.CreateFromSpirv</c>. The workgroup size is read back off
        /// the compiled module and surfaced on <see cref="IGpuComputeShader.ThreadGroupSizeX"/>, which is also what
        /// the compute pipeline is built with, so there is no second copy to keep in sync.
        /// <para>DECLARE <c>layout(local_size_x = N) in;</c> in the source. Omitting it is not an error: GLSL's
        /// default workgroup size is 1x1x1 and that is what gets compiled in, so a dispatch runs ONE invocation per
        /// group and the shader is silently a few hundred times slower than intended rather than broken. Nothing
        /// can catch that for you, because a deliberate 1x1x1 is legal.</para>
        /// Validate the source device-free first with <see cref="ShaderValidation.ValidateCompute"/>. Throws
        /// <see cref="NotSupportedException"/> on a device whose <see cref="GpuCapabilities.SupportsCompute"/> is
        /// false, so a caller that forgot to gate fails loudly instead of at dispatch, and
        /// <see cref="ShaderValidationException"/> when the source does not compile.</summary>
        IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl);
        /// <summary>Create a graphics pipeline.</summary>
        IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d);
        /// <summary>Create a compute pipeline. Throws <see cref="NotSupportedException"/> when the device does not
        /// support compute (see <see cref="GpuCapabilities.SupportsCompute"/>).</summary>
        IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d);
        /// <summary>Create a command list.</summary>
        IGpuCommandList CreateCommandList();
    }

    /// <summary>Records GPU commands for one submission. Engine mirror of Veldrid <c>CommandList</c>.</summary>
    public interface IGpuCommandList : IDisposable
    {
        /// <summary>Begin recording.</summary>
        void Begin();
        /// <summary>Finish recording.</summary>
        void End();
        /// <summary>Bind a framebuffer as the render target.</summary>
        void SetFramebuffer(IGpuFramebuffer fb);
        /// <summary>Clear colour attachment <paramref name="index"/> to <paramref name="rgba"/>.</summary>
        void ClearColorTarget(uint index, Color rgba);
        /// <summary>Clear the depth attachment.</summary>
        void ClearDepthStencil(float depth);
        /// <summary>Bind a graphics pipeline.</summary>
        void SetPipeline(IGpuPipeline p);
        /// <summary>Bind a resource set to graphics slot <paramref name="slot"/>.</summary>
        void SetGraphicsResourceSet(uint slot, IGpuResourceSet set);
        /// <summary>Bind a resource set whose dynamic-offset buffer binding is rebased by <paramref name="dynamicOffset"/>
        /// bytes for this draw. The set must have exactly one element declared dynamic (see
        /// <see cref="GpuResourceLayoutElement.Dynamic"/>); the offset must satisfy the backend's uniform-buffer
        /// offset alignment (256 bytes is safe across Metal/D3D11/Vulkan).</summary>
        void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset);
        /// <summary>Bind a vertex buffer to slot <paramref name="slot"/>.</summary>
        void SetVertexBuffer(uint slot, IGpuBuffer b);
        /// <summary>Bind a vertex buffer to slot <paramref name="slot"/> starting at <paramref name="offsetBytes"/>
        /// into the buffer, so a draw reads its slice of a shared buffer as if from the buffer's start.</summary>
        void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes);
        /// <summary>Bind the index buffer with element format <paramref name="fmt"/>.</summary>
        void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt);
        /// <summary>Set scissor rect for output <paramref name="index"/>.</summary>
        void SetScissorRect(uint index, uint x, uint y, uint w, uint h);
        /// <summary>Reset scissor to the full framebuffer for all outputs.</summary>
        void SetFullScissorRects();
        /// <summary>Non-indexed draw. The fullscreen passes call <c>Draw(3, 1, 0, 0)</c>.</summary>
        void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart);
        /// <summary>Non-indexed draw of a single instance (convenience; the fullscreen-triangle passes use this).</summary>
        void Draw(uint vertexCount);
        /// <summary>Indexed (optionally instanced) draw.</summary>
        void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart);
        /// <summary>Upload a single unmanaged struct into a buffer at <paramref name="offsetBytes"/>.</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged;
        /// <summary>Upload a span of unmanaged elements into a buffer at <paramref name="offsetBytes"/>.</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged;
        /// <summary>Copy <paramref name="sizeInBytes"/> bytes between two buffers (e.g. a compute-written storage
        /// buffer -> a <see cref="GpuBufferUsage.Staging"/> buffer for readback). The counterpart of
        /// <see cref="CopyTexture"/> for buffers; <see cref="GpuReadback.ReadBuffer{T}"/> wraps the whole
        /// staging-copy-map-unmap dance.</summary>
        void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes);

        /// <summary>Copy a whole texture (e.g. render target -> staging) for readback.</summary>
        void CopyTexture(IGpuTexture src, IGpuTexture dst);

        /// <summary>Copy one mip level + array layer of <paramref name="src"/> (its top-left <paramref name="width"/> x
        /// <paramref name="height"/> region) into <paramref name="dst"/>'s mip 0 / layer 0 - for reading a specific
        /// mip of a texture array back to the CPU (e.g. verifying a generated mip chain). <paramref name="dst"/> must
        /// be at least that size.</summary>
        void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height);

        /// <summary>Generate the full mip chain of <paramref name="texture"/> from its base level. The texture must
        /// be created with <see cref="GpuTextureUsage.GenerateMipmaps"/> and a mip count &gt; 1.</summary>
        void GenerateMipmaps(IGpuTexture texture);

        /// <summary>Resolve a multisampled (<paramref name="src"/>, <see cref="IGpuTexture.SampleCount"/> &gt; 1) render
        /// target into the single-sample <paramref name="dst"/> (same width/height/format, sample count 1), averaging
        /// the samples - the MSAA resolve. Do this before the post chain / any pass that SAMPLES the target, since a
        /// multisampled texture cannot be bound as a normal sampled texture.</summary>
        void ResolveTexture(IGpuTexture src, IGpuTexture dst);

        // ---- Compute ----
        //
        // ORDERING CONTRACT. There is no explicit barrier call on this seam, because the backend layer has none:
        // what ordering exists comes from each backend's implicit handling, and the three do not agree. Two rules
        // fall out of that, and both are proved by the compute [GpuFact] suite on all three backends:
        //
        //   1. Compute writes a storage texture, then a GRAPHICS pass samples it: record BOTH in the SAME command
        //      list, and create the texture with Storage | Sampled (see GpuTextureUsage.Storage). All three
        //      backends then handle the handoff - Vulkan queues a layout restore at dispatch time and drains it
        //      before the next draw (per command list, and armed by the Sampled flag), Metal ends the compute
        //      encoder when the render encoder begins, and Direct3D11 unbinds the UAV as the SRV is bound. Split
        //      across two command lists, the Vulkan restore is silently skipped, so that split is NOT safe.
        //
        //   2. A dispatch that READS what an earlier dispatch WROTE (the classic ping-pong: an FFT stage, a
        //      multi-pass reduction) must be separated by End + IGpuDevice.Submit + IGpuDevice.WaitForIdle.
        //      Chaining dependent dispatches inside one command list is NOT safe: on Vulkan no memory barrier is
        //      emitted between them at all (storage buffers are not tracked, and a storage image stays in the same
        //      layout so the transition is a no-op), and dispatches inside a command buffer may overlap. A submit
        //      boundary plus a device drain is the only ordering this seam can guarantee.
        //
        // Rule 2 costs a GPU stall per dependent stage, which is real: it is the current ceiling on any multi-pass
        // compute chain built here.

        /// <summary>Bind a compute pipeline. Compute and graphics pipeline bindings are tracked separately, so
        /// this does not disturb a bound graphics pipeline (and vice versa).</summary>
        void SetComputePipeline(IGpuComputePipeline p);

        /// <summary>Bind a resource set to COMPUTE slot <paramref name="slot"/>. Compute and graphics resource-set
        /// bindings are separate: <see cref="SetGraphicsResourceSet(uint,IGpuResourceSet)"/> does not feed a
        /// dispatch and this does not feed a draw.</summary>
        void SetComputeResourceSet(uint slot, IGpuResourceSet set);

        /// <summary>Bind a compute resource set whose dynamic-offset buffer binding is rebased by
        /// <paramref name="dynamicOffset"/> bytes for this dispatch. The set must have exactly one element declared
        /// dynamic (see <see cref="GpuResourceLayoutElement.Dynamic"/>); the offset must satisfy the backend's
        /// uniform-buffer offset alignment (256 bytes is safe across Metal/Direct3D11/Vulkan). Lets a run of
        /// dispatches read their own per-stage parameter block out of one shared uniform buffer.</summary>
        void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset);

        /// <summary>Dispatch <paramref name="groupCountX"/> x <paramref name="groupCountY"/> x
        /// <paramref name="groupCountZ"/> WORKGROUPS of the bound compute pipeline. These are group counts, not
        /// thread counts: the total invocation count is the group count multiplied by the shader's
        /// <see cref="IGpuComputeShader.ThreadGroupSizeX"/>/<c>Y</c>/<c>Z</c>, so cover N elements with
        /// <c>(N + groupSize - 1) / groupSize</c> groups and bounds-check in the shader (the tail group runs on
        /// out-of-range indices). See the ordering contract above before chaining dependent dispatches.</summary>
        void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ);
    }

    /// <summary>The GPU device: backend info, capabilities, the resource factory, the swapchain framebuffer,
    /// buffer/texture updates, submission, and staging map/unmap. Engine mirror of Veldrid <c>GraphicsDevice</c>
    /// (the subset the 5.x renderers use). Veldrid is hidden inside the impl. Disposing any resource created
    /// by this device AFTER the device itself is disposed is a safe no-op, since device destruction already
    /// freed all child objects (teardown-order stragglers cannot destroy against a dead device).</summary>
    public interface IGpuDevice : IDisposable
    {
        /// <summary>The active backend.</summary>
        GpuBackendKind Backend { get; }
        /// <summary>Clip-space / depth conventions of the live device.</summary>
        GpuCapabilities Capabilities { get; }
        /// <summary>The resource factory.</summary>
        IGpuResourceFactory Factory { get; }
        /// <summary>The main swapchain framebuffer (null on a headless no-swapchain device).</summary>
        IGpuFramebuffer? SwapchainFramebuffer { get; }
        /// <summary>A shared point (nearest) sampler owned by the device.</summary>
        IGpuSampler PointSampler { get; }
        /// <summary>A shared linear (bilinear) sampler owned by the device.</summary>
        IGpuSampler LinearSampler { get; }

        /// <summary>Submit a finished command list for execution.</summary>
        void Submit(IGpuCommandList cl);
        /// <summary>Block until the GPU is idle. After the device is disposed this is a safe no-op (a dead
        /// device has nothing to wait for), so a resource wrapper draining before its own disposal stays safe
        /// when it outlives the device at teardown. Calling it concurrently WITH device disposal remains a
        /// consumer ordering error.</summary>
        void WaitForIdle();

        /// <summary>Upload a span of unmanaged elements into a buffer at <paramref name="offsetBytes"/>.</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged;
        /// <summary>Upload an array (convenience).</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged;
        /// <summary>Upload a single unmanaged struct (convenience).</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged;

        /// <summary>Upload CPU RGBA (or format-matching) bytes into a texture sub-region (mip 0, layer 0).</summary>
        void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height);

        /// <summary>Upload CPU bytes into a texture sub-region at an explicit <paramref name="mipLevel"/> and
        /// <paramref name="arrayLayer"/> (the splat-terrain layer stacks upload each layer's base mip).</summary>
        void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mipLevel, uint arrayLayer);

        /// <summary>Map a staging resource for CPU access.</summary>
        MappedData Map(IGpuTexture staging, GpuMapMode mode);
        /// <summary>Unmap a previously mapped staging resource.</summary>
        void Unmap(IGpuTexture staging);

        /// <summary>Map a staging BUFFER (created with <see cref="GpuBufferUsage.Staging"/>) for CPU access - the
        /// buffer half of the texture map/unmap pair, and how a compute-written storage buffer is read back after
        /// <see cref="IGpuCommandList.CopyBuffer"/>. <see cref="MappedData.RowPitch"/> is meaningless for a buffer
        /// (it equals the size); the data is a flat byte range. Prefer <see cref="GpuReadback.ReadBuffer{T}"/>,
        /// which wraps the whole staging-copy-map-unmap sequence.</summary>
        MappedData Map(IGpuBuffer staging, GpuMapMode mode);
        /// <summary>Unmap a previously mapped staging buffer.</summary>
        void Unmap(IGpuBuffer staging);

        /// <summary>Resize the main swapchain.</summary>
        void ResizeSwapchain(uint w, uint h);
        /// <summary>Present the main swapchain.</summary>
        void Present();

        /// <summary>
        /// Whether presentation syncs to the display's vertical blank. Settable at runtime: on a windowed device this
        /// reconfigures the live swapchain in place (no recreate, no leaked swapchain, size + depth preserved), so a
        /// game can flip vsync mid-session. A no-op backing value on a headless (no-swapchain) device. On Metal it
        /// sets the layer's <c>displaySyncEnabled</c>, but the Veldrid Metal present still does not throttle the CPU
        /// from this alone - pair with a software frame cap for a deterministic rate (see <c>PresentMode</c>).
        /// </summary>
        bool SyncToVerticalBlank { get; set; }
    }
}
