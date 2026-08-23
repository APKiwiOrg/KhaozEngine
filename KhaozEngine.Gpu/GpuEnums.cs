using System;

namespace KhaozEngine.Gpu
{
    /// <summary>Pixel format for a GPU texture. Engine-side mirror of the Veldrid <c>PixelFormat</c> members the
    /// 5.x renderers use. Each native backend maps these members to its own API format itself. Until 18.0.0
    /// <c>Internal/VeldridMap</c> mapped them 1:1 to Veldrid for the incumbent device.</summary>
    public enum GpuPixelFormat
    {
        /// <summary>8-bit-per-channel RGBA, unsigned normalized. The 2D + 3D colour-target format.</summary>
        R8G8B8A8UNorm,
        /// <summary>Single 32-bit float channel. The 3D linear-depth MRT attachment (not linearly filterable on Metal).</summary>
        R32Float,
        /// <summary>32-bit depth + 8-bit stencil. The 3D model-pass depth-stencil.</summary>
        D32FloatS8UInt,
        /// <summary>24-bit depth + 8-bit stencil.</summary>
        D24UNormS8UInt,
        /// <summary>Single 8-bit channel, unsigned normalized.</summary>
        R8UNorm,
        /// <summary>BGRA 8-bit-per-channel, unsigned normalized (common swapchain format).</summary>
        B8G8R8A8UNorm,
        /// <summary>16-bit-float-per-channel RGBA. The HDR internal colour-target format (headroom above 1.0,
        /// tonemapped back to LDR by the post chain before the swapchain blit).</summary>
        R16G16B16A16Float,
        /// <summary>Two-channel 16-bit-float (red + green). The screen-space distortion offset target: signed
        /// per-pixel UV offsets accumulate here at half or quarter res, re-sampled by the post apply pass.</summary>
        R16G16Float,
    }

    /// <summary>How a GPU texture may be used. Flags so a texture can be both a render target and sampled, etc.
    /// Mirrors Veldrid <c>TextureUsage</c>.</summary>
    [Flags]
    public enum GpuTextureUsage
    {
        /// <summary>No usage.</summary>
        None = 0,
        /// <summary>Readable from a shader (a sampled texture).</summary>
        Sampled = 1 << 0,
        /// <summary>Writable as a colour render target.</summary>
        RenderTarget = 1 << 1,
        /// <summary>Usable as a depth-stencil attachment.</summary>
        DepthStencil = 1 << 2,
        /// <summary>CPU-accessible staging texture (for readback via <see cref="GpuMapMode.Read"/>).</summary>
        Staging = 1 << 3,
        /// <summary>A cubemap texture.</summary>
        Cubemap = 1 << 4,
        /// <summary>Allows mipmap generation.</summary>
        GenerateMipmaps = 1 << 5,
        /// <summary>Read-write from a compute shader (a storage / UAV image, GLSL <c>image2D</c>), bound through a
        /// <see cref="GpuResourceKind.TextureReadWrite"/> slot. Combine with <see cref="Sampled"/> for the usual
        /// compute-writes-then-graphics-samples handoff. Mirrors Veldrid <c>TextureUsage.Storage</c>; needs
        /// <see cref="GpuCapabilities.SupportsCompute"/>.</summary>
        Storage = 1 << 6,
    }

    /// <summary>How a GPU buffer is used. Mirrors Veldrid <c>BufferUsage</c>.</summary>
    [Flags]
    public enum GpuBufferUsage
    {
        /// <summary>No usage.</summary>
        None = 0,
        /// <summary>Vertex buffer.</summary>
        VertexBuffer = 1 << 0,
        /// <summary>Index buffer.</summary>
        IndexBuffer = 1 << 1,
        /// <summary>Uniform (constant) buffer.</summary>
        UniformBuffer = 1 << 2,
        /// <summary>Read-only structured buffer.</summary>
        StructuredBufferReadOnly = 1 << 3,
        /// <summary>Read-write structured buffer.</summary>
        StructuredBufferReadWrite = 1 << 4,
        /// <summary>Indirect-draw argument buffer.</summary>
        IndirectBuffer = 1 << 5,
        /// <summary>CPU-mappable dynamic buffer (frequent updates).</summary>
        Dynamic = 1 << 6,
        /// <summary>CPU-readable staging buffer.</summary>
        Staging = 1 << 7,
    }

    /// <summary>Element size of an index buffer. Mirrors Veldrid <c>IndexFormat</c>.</summary>
    public enum GpuIndexFormat
    {
        /// <summary>16-bit unsigned indices (the 3D meshes use <see cref="UInt16"/>).</summary>
        UInt16,
        /// <summary>32-bit unsigned indices.</summary>
        UInt32,
    }

    /// <summary>How vertices are assembled into primitives. Mirrors Veldrid <c>PrimitiveTopology</c>.</summary>
    public enum GpuPrimitiveTopology
    {
        /// <summary>Independent triangles (3 verts each).</summary>
        TriangleList,
        /// <summary>Connected triangle strip.</summary>
        TriangleStrip,
        /// <summary>Independent line segments (2 verts each).</summary>
        LineList,
        /// <summary>Connected line strip.</summary>
        LineStrip,
        /// <summary>Independent points.</summary>
        PointList,
    }

    /// <summary>Which shader stages a resource is visible to. Flags. Mirrors Veldrid <c>ShaderStages</c>.</summary>
    [Flags]
    public enum GpuShaderStages
    {
        /// <summary>No stage.</summary>
        None = 0,
        /// <summary>Vertex shader.</summary>
        Vertex = 1 << 0,
        /// <summary>Geometry shader.</summary>
        Geometry = 1 << 1,
        /// <summary>Tessellation control / hull shader.</summary>
        TessellationControl = 1 << 2,
        /// <summary>Tessellation evaluation / domain shader.</summary>
        TessellationEvaluation = 1 << 3,
        /// <summary>Fragment / pixel shader.</summary>
        Fragment = 1 << 4,
        /// <summary>Compute shader.</summary>
        Compute = 1 << 5,
    }

    /// <summary>The kind of resource bound at a layout slot. Mirrors Veldrid <c>ResourceKind</c>.</summary>
    public enum GpuResourceKind
    {
        /// <summary>A uniform (constant) buffer.</summary>
        UniformBuffer,
        /// <summary>A read-only structured buffer.</summary>
        StructuredBufferReadOnly,
        /// <summary>A read-write structured buffer.</summary>
        StructuredBufferReadWrite,
        /// <summary>A sampled (read-only) texture.</summary>
        TextureReadOnly,
        /// <summary>A read-write (storage) texture.</summary>
        TextureReadWrite,
        /// <summary>A sampler.</summary>
        Sampler,
    }

    /// <summary>Triangle face culling mode. Mirrors Veldrid <c>FaceCullMode</c>.</summary>
    public enum GpuFaceCull
    {
        /// <summary>Cull back faces.</summary>
        Back,
        /// <summary>Cull front faces.</summary>
        Front,
        /// <summary>Cull nothing (the 5.x renderers use <see cref="None"/>).</summary>
        None,
    }

    /// <summary>Polygon fill mode. Mirrors Veldrid <c>PolygonFillMode</c>.</summary>
    public enum GpuPolygonFill
    {
        /// <summary>Filled (solid) polygons.</summary>
        Solid,
        /// <summary>Wireframe polygons.</summary>
        Wireframe,
    }

    /// <summary>Winding order treated as front-facing. Mirrors Veldrid <c>FrontFace</c>.</summary>
    public enum GpuFrontFace
    {
        /// <summary>Clockwise winding is front (the 5.x renderers use <see cref="Clockwise"/>).</summary>
        Clockwise,
        /// <summary>Counter-clockwise winding is front.</summary>
        CounterClockwise,
    }

    /// <summary>Depth / stencil comparison function. Mirrors Veldrid <c>ComparisonKind</c>.</summary>
    public enum GpuComparison
    {
        /// <summary>Never passes.</summary>
        Never,
        /// <summary>Passes if less.</summary>
        Less,
        /// <summary>Passes if equal.</summary>
        Equal,
        /// <summary>Passes if less-or-equal (the model pass depth test).</summary>
        LessEqual,
        /// <summary>Passes if greater.</summary>
        Greater,
        /// <summary>Passes if not equal.</summary>
        NotEqual,
        /// <summary>Passes if greater-or-equal.</summary>
        GreaterEqual,
        /// <summary>Always passes.</summary>
        Always,
    }

    /// <summary>Blend source/destination factor. Mirrors Veldrid <c>BlendFactor</c>.</summary>
    public enum GpuBlendFactor
    {
        /// <summary>Zero.</summary>
        Zero,
        /// <summary>One.</summary>
        One,
        /// <summary>Source colour.</summary>
        SourceColor,
        /// <summary>One minus source colour.</summary>
        InverseSourceColor,
        /// <summary>Source alpha (alpha + additive blends).</summary>
        SourceAlpha,
        /// <summary>One minus source alpha (alpha blend).</summary>
        InverseSourceAlpha,
        /// <summary>Destination colour.</summary>
        DestinationColor,
        /// <summary>One minus destination colour.</summary>
        InverseDestinationColor,
        /// <summary>Destination alpha.</summary>
        DestinationAlpha,
        /// <summary>One minus destination alpha.</summary>
        InverseDestinationAlpha,
        /// <summary>Constant blend factor.</summary>
        BlendFactor,
        /// <summary>One minus constant blend factor.</summary>
        InverseBlendFactor,
    }

    /// <summary>Blend equation. Mirrors Veldrid <c>BlendFunction</c>.</summary>
    public enum GpuBlendFunction
    {
        /// <summary>source*srcFactor + dest*destFactor.</summary>
        Add,
        /// <summary>source*srcFactor - dest*destFactor.</summary>
        Subtract,
        /// <summary>dest*destFactor - source*srcFactor.</summary>
        ReverseSubtract,
        /// <summary>min(source, dest).</summary>
        Minimum,
        /// <summary>max(source, dest).</summary>
        Maximum,
    }

    /// <summary>Sampler texture-filtering mode. Mirrors the subset of Veldrid <c>SamplerFilter</c> the renderers
    /// use (point vs linear min/mag/mip).</summary>
    public enum GpuSamplerFilter
    {
        /// <summary>Nearest-neighbour (point) sampling - the pixelated upscale + crisp 2D path.</summary>
        MinPointMagPointMipPoint,
        /// <summary>Bilinear sampling - the smooth upscale path.</summary>
        MinLinearMagLinearMipLinear,
        /// <summary>Anisotropic filtering (grazing-angle quality for tiled ground). Requires device support;
        /// the impl falls back to <see cref="MinLinearMagLinearMipLinear"/> when the backend lacks it.</summary>
        Anisotropic,
    }

    /// <summary>Sampler addressing (wrap) mode. Mirrors Veldrid <c>SamplerAddressMode</c>.</summary>
    public enum GpuSamplerAddress
    {
        /// <summary>Wrap (repeat).</summary>
        Wrap,
        /// <summary>Mirror.</summary>
        Mirror,
        /// <summary>Clamp to edge.</summary>
        Clamp,
        /// <summary>Clamp to border colour.</summary>
        Border,
    }

    /// <summary>CPU map access mode for a staging resource. Mirrors Veldrid <c>MapMode</c>.</summary>
    public enum GpuMapMode
    {
        /// <summary>Read-only mapping (readback path).</summary>
        Read,
        /// <summary>Write-only mapping.</summary>
        Write,
        /// <summary>Read-write mapping.</summary>
        ReadWrite,
    }
}
