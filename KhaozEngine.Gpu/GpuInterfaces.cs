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

    /// <summary>A resource-layout handle (binding-slot shape). Engine wrapper over Veldrid <c>ResourceLayout</c>.</summary>
    public interface IGpuResourceLayout : IDisposable { }

    /// <summary>A bound resource set handle. Engine wrapper over Veldrid <c>ResourceSet</c>.</summary>
    public interface IGpuResourceSet : IDisposable { }

    /// <summary>A compiled shader set (vertex + fragment) handle. Engine wrapper over the Veldrid
    /// <c>Shader[]</c> a SPIR-V cross-compile produces.</summary>
    public interface IGpuShaderSet : IDisposable { }

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
        /// <summary>Create a graphics pipeline.</summary>
        IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d);
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
        /// <summary>Copy a whole texture (e.g. render target -> staging) for readback.</summary>
        void CopyTexture(IGpuTexture src, IGpuTexture dst);
    }

    /// <summary>The GPU device: backend info, capabilities, the resource factory, the swapchain framebuffer,
    /// buffer/texture updates, submission, and staging map/unmap. Engine mirror of Veldrid <c>GraphicsDevice</c>
    /// (the subset the 5.x renderers use). Veldrid is hidden inside the impl.</summary>
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
        /// <summary>Block until the GPU is idle.</summary>
        void WaitForIdle();

        /// <summary>Upload a span of unmanaged elements into a buffer at <paramref name="offsetBytes"/>.</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged;
        /// <summary>Upload an array (convenience).</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged;
        /// <summary>Upload a single unmanaged struct (convenience).</summary>
        void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged;

        /// <summary>Upload CPU RGBA (or format-matching) bytes into a texture sub-region (mip 0, layer 0).</summary>
        void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height);

        /// <summary>Map a staging resource for CPU access.</summary>
        MappedData Map(IGpuTexture staging, GpuMapMode mode);
        /// <summary>Unmap a previously mapped staging resource.</summary>
        void Unmap(IGpuTexture staging);

        /// <summary>Resize the main swapchain.</summary>
        void ResizeSwapchain(uint w, uint h);
        /// <summary>Present the main swapchain.</summary>
        void Present();
    }
}
