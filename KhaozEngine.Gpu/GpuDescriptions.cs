using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu
{
    // PROVENANCE FOR THE DESCRIPTIONS BELOW, recorded once. Their shapes were taken from the equivalent
    // Veldrid 4.9 descriptions in 2025, when that library was the one implementation behind this seam. They are
    // the seam's own now: 18.0.0 deleted that backend, and each native backend translates these into its own
    // API's structures itself.

    /// <summary>Describes a GPU buffer to create.</summary>
    public readonly struct GpuBufferDescription
    {
        /// <summary>Size of the buffer in bytes.</summary>
        public uint SizeInBytes { get; }
        /// <summary>How the buffer is used.</summary>
        public GpuBufferUsage Usage { get; }
        /// <summary>For a structured buffer, the per-element stride in bytes (0 otherwise). Advisory only on the
        /// Direct3D11 path, which binds every structured buffer as a RAW (byte-address) view to match what
        /// SPIRV-Cross emits for a GLSL storage block; the size must be a multiple of 4 either way.</summary>
        public uint StructureByteStride { get; }

        public GpuBufferDescription(uint sizeInBytes, GpuBufferUsage usage, uint structureByteStride = 0)
        {
            SizeInBytes = sizeInBytes;
            Usage = usage;
            StructureByteStride = structureByteStride;
        }
    }

    /// <summary>Describes a 2D GPU texture to create. Engine mirror of <c>TextureDescription.Texture2D</c>.</summary>
    public readonly struct GpuTextureDescription
    {
        /// <summary>Texel width.</summary>
        public uint Width { get; }
        /// <summary>Texel height.</summary>
        public uint Height { get; }
        /// <summary>Mip-level count.</summary>
        public uint MipLevels { get; }
        /// <summary>Array-layer count.</summary>
        public uint ArrayLayers { get; }
        /// <summary>Pixel format.</summary>
        public GpuPixelFormat Format { get; }
        /// <summary>How the texture is used.</summary>
        public GpuTextureUsage Usage { get; }
        /// <summary>MSAA sample count (1 = single-sample, the default). &gt; 1 makes a multisampled render target for
        /// MSAA; such a texture cannot be sampled directly (resolve it to a single-sample texture with
        /// <see cref="IGpuCommandList.ResolveTexture"/> first) and must have <see cref="MipLevels"/> == 1. Clamp a
        /// request to <see cref="GpuCapabilities.MaxMsaaSampleCount"/>. Must be a power of two.</summary>
        public uint SampleCount { get; }

        /// <summary>Whether the texture is a 2D texture ARRAY rather than a plain 2D texture, which is what
        /// decides the type a shader binds it as (<c>texture2DArray</c> / <c>Texture2DArray</c> against
        /// <c>texture2D</c> / <c>Texture2D</c>).
        /// <para>
        /// TRUE WHENEVER <see cref="ArrayLayers"/> IS ABOVE ONE, so every caller written before this property
        /// existed keeps the behaviour it had. The <c>isArray</c> constructor parameter is what a ONE-layer array
        /// needs, because the layer count alone cannot tell one apart from a plain 2D texture, and
        /// <see cref="Texture2DArray"/> sets it for you. Before #666 a one-layer set therefore created a plain 2D
        /// texture, and a pipeline whose fragment declares an array sampler bound the wrong type: Metal kills the
        /// process when validation is armed, lavapipe tolerates it silently.
        /// </para>
        /// <para>
        /// IT NAMES THE 2D-ARRAY CASE AND NOTHING ELSE. A cubemap (<see cref="GpuTextureUsage.Cubemap"/>) keeps
        /// its own layer-count rule in every backend, since a cube is already six faces and the seam has no
        /// one-cube cube-array caller. A MULTISAMPLED ARRAY is not a shape this seam carries at all, and the
        /// constructor refuses one rather than letting each backend decide: Metal and Vulkan would take the
        /// multisample type, and Direct3D 11's shader resource view nests its multisample test INSIDE the
        /// non-array arm, so the same description would take a plain (non-multisampled)
        /// <c>Texture2DArray</c> view over a multisampled resource there. No backend agreeing on it is the
        /// reason it is refused rather than documented.
        /// </para></summary>
        public bool IsArray { get; }

        public GpuTextureDescription(uint width, uint height, GpuPixelFormat format, GpuTextureUsage usage,
            uint mipLevels = 1, uint arrayLayers = 1, uint sampleCount = 1, bool isArray = false)
        {
            Width = width; Height = height; Format = format; Usage = usage;
            MipLevels = mipLevels; ArrayLayers = arrayLayers;
            SampleCount = sampleCount < 1 ? 1 : sampleCount;
            IsArray = isArray || arrayLayers > 1;
            RequireNoMultisampledArray(IsArray, SampleCount);
        }

        /// <summary>A MULTISAMPLED TEXTURE ARRAY IS NOT EXPRESSIBLE ON THIS SEAM, and saying so here is what stops
        /// it from becoming a per-backend surprise. The three natives derive a multisampled type before they
        /// derive an array type, Direct3D 11 derives the array type first and loses the multisample one, and no
        /// engine path asks for the shape either way (every array the engine builds goes through
        /// <see cref="Texture2DArray"/>, which is single-sample). Refusing at description time costs one
        /// comparison and turns a silently wrong view into a message at the call that asked for it.</summary>
        static void RequireNoMultisampledArray(bool isArray, uint sampleCount)
        {
            if (!isArray || sampleCount <= 1) return;

            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount,
                "A texture cannot be both a 2D ARRAY and MULTISAMPLED on this seam. The backends do not agree on "
                + "which type wins: Metal, Vulkan and Direct3D 11's own render-target views take the multisample "
                + "type, while Direct3D 11's shader resource view takes a plain Texture2DArray and drops the "
                + "multisampling. Build the multisampled render target as a single-layer texture and resolve it "
                + "(IGpuCommandList.ResolveTexture) into an array layer, or drop the sample count to 1.");
        }

        /// <summary>Convenience for a single-mip, single-layer 2D texture (mirrors <c>TextureDescription.Texture2D</c>).</summary>
        public static GpuTextureDescription Texture2D(uint width, uint height, GpuPixelFormat format, GpuTextureUsage usage)
            => new(width, height, format, usage, 1, 1);

        /// <summary>Convenience for a 2D texture ARRAY with explicit layer + mip counts (the splat-terrain layer
        /// stacks). The ctor already carries <see cref="MipLevels"/>/<see cref="ArrayLayers"/>; this names the
        /// array case, and it is the only thing that can name a ONE-layer array, because it sets
        /// <see cref="IsArray"/> rather than leaving the backends to infer array-ness from the layer count
        /// (#666).</summary>
        public static GpuTextureDescription Texture2DArray(uint width, uint height, GpuPixelFormat format,
            GpuTextureUsage usage, uint arrayLayers, uint mipLevels)
            => new(width, height, format, usage, mipLevels, arrayLayers, isArray: true);
    }

    /// <summary>Describes a GPU sampler, in the subset the renderers use.</summary>
    public readonly struct GpuSamplerDescription
    {
        /// <summary>Addressing on U.</summary>
        public GpuSamplerAddress AddressModeU { get; }
        /// <summary>Addressing on V.</summary>
        public GpuSamplerAddress AddressModeV { get; }
        /// <summary>Addressing on W.</summary>
        public GpuSamplerAddress AddressModeW { get; }
        /// <summary>Min/mag/mip filtering.</summary>
        public GpuSamplerFilter Filter { get; }
        /// <summary>Max anisotropy when <see cref="Filter"/> is <see cref="GpuSamplerFilter.Anisotropic"/>
        /// (ignored otherwise). 0 keeps the historical behaviour.</summary>
        public uint MaximumAnisotropy { get; }
        /// <summary>Mip-level bias added to the computed LOD, in whole mip levels.
        /// A positive value biases sampling toward blurrier mips, which tames grazing-angle / distance shimmer on
        /// high-frequency tiling textures (e.g. noisy terrain albedo). 0 (default) keeps the historical behaviour.
        /// Honoured on D3D11 / Vulkan; Metal's sampler has no LOD bias, so it is a no-op there.</summary>
        public int MipLodBias { get; }

        public GpuSamplerDescription(GpuSamplerFilter filter,
            GpuSamplerAddress addressU = GpuSamplerAddress.Clamp,
            GpuSamplerAddress addressV = GpuSamplerAddress.Clamp,
            GpuSamplerAddress addressW = GpuSamplerAddress.Clamp,
            uint maximumAnisotropy = 0,
            int mipLodBias = 0)
        {
            Filter = filter; AddressModeU = addressU; AddressModeV = addressV; AddressModeW = addressW;
            MaximumAnisotropy = maximumAnisotropy;
            MipLodBias = mipLodBias;
        }

        /// <summary>Point (nearest) sampler clamped on all axes.</summary>
        public static GpuSamplerDescription Point => new(GpuSamplerFilter.MinPointMagPointMipPoint);
        /// <summary>Linear (bilinear) sampler clamped on all axes.</summary>
        public static GpuSamplerDescription Linear => new(GpuSamplerFilter.MinLinearMagLinearMipLinear);
    }

    /// <summary>One element of a resource layout. Engine mirror of <c>ResourceLayoutElementDescription</c>.</summary>
    public readonly struct GpuResourceLayoutElement
    {
        /// <summary>Binding name (matches the shader).</summary>
        public string Name { get; }
        /// <summary>The resource kind bound here.</summary>
        public GpuResourceKind Kind { get; }
        /// <summary>Which shader stages see it.</summary>
        public GpuShaderStages Stages { get; }
        /// <summary>When true, the buffer bound here is rebased per draw by a byte offset supplied to
        /// <see cref="IGpuCommandList.SetGraphicsResourceSet(uint,IGpuResourceSet,uint)"/> (a dynamic-offset
        /// uniform/structured buffer). The set binds a <see cref="GpuBufferRange"/> whose size is the per-draw
        /// window; the offset varies per draw. Lets many draws read their own slice of one shared buffer without
        /// recreating the set or re-uploading.</summary>
        public bool Dynamic { get; }

        public GpuResourceLayoutElement(string name, GpuResourceKind kind, GpuShaderStages stages, bool dynamic = false)
        {
            Name = name; Kind = kind; Stages = stages; Dynamic = dynamic;
        }
    }

    /// <summary>A windowed view of a buffer bound to a resource set: <see cref="Buffer"/> starting at
    /// <see cref="Offset"/> for <see cref="Size"/> bytes. Used for dynamic-offset bindings (offset 0 + the window
    /// size; the per-draw offset is supplied at draw time). Engine mirror of <c>DeviceBufferRange</c>.</summary>
    public readonly struct GpuBufferRange : IGpuBindableResource
    {
        public IGpuBuffer Buffer { get; }
        public uint Offset { get; }
        public uint Size { get; }
        public GpuBufferRange(IGpuBuffer buffer, uint offset, uint size) { Buffer = buffer; Offset = offset; Size = size; }
    }

    /// <summary>Describes a resource layout (the binding slots of a set). Engine mirror of
    /// <c>ResourceLayoutDescription</c>.</summary>
    public readonly struct GpuResourceLayoutDescription
    {
        /// <summary>The ordered binding elements.</summary>
        public GpuResourceLayoutElement[] Elements { get; }

        public GpuResourceLayoutDescription(params GpuResourceLayoutElement[] elements) => Elements = elements;
    }

    /// <summary>Describes a resource set: a layout plus the concrete resources bound to its slots, in order
    /// (buffers, textures, samplers). Engine mirror of <c>ResourceSetDescription</c>.</summary>
    public readonly struct GpuResourceSetDescription
    {
        /// <summary>The layout this set satisfies.</summary>
        public IGpuResourceLayout Layout { get; }
        /// <summary>The bound resources, in binding order. Each is an <see cref="IGpuBuffer"/>,
        /// <see cref="IGpuTexture"/>, or <see cref="IGpuSampler"/>.</summary>
        public IGpuBindableResource[] Resources { get; }

        public GpuResourceSetDescription(IGpuResourceLayout layout, params IGpuBindableResource[] resources)
        {
            Layout = layout; Resources = resources;
        }
    }

    /// <summary>One vertex attribute. Engine mirror of <c>VertexElementDescription</c>. The semantic name is a
    /// label only (SPIR-V binds by location order), so a single <see cref="GpuVertexElementFormat"/> + name
    /// suffices.</summary>
    public readonly struct GpuVertexElement
    {
        /// <summary>Attribute name (label; SPIR-V binds by location order).</summary>
        public string Name { get; }
        /// <summary>Component format.</summary>
        public GpuVertexElementFormat Format { get; }

        public GpuVertexElement(string name, GpuVertexElementFormat format)
        {
            Name = name; Format = format;
        }
    }

    /// <summary>Vertex attribute component format, in the subset the renderers use (Float2/3/4).</summary>
    public enum GpuVertexElementFormat
    {
        /// <summary>One 32-bit float.</summary>
        Float1,
        /// <summary>Two 32-bit floats.</summary>
        Float2,
        /// <summary>Three 32-bit floats.</summary>
        Float3,
        /// <summary>Four 32-bit floats.</summary>
        Float4,
    }

    /// <summary>Describes one vertex buffer's layout (a buffer slot). Engine mirror of
    /// <c>VertexLayoutDescription</c>, including the per-instance step rate the 3D model pass uses.</summary>
    public readonly struct GpuVertexLayoutDescription
    {
        /// <summary>Explicit stride in bytes, or 0 to let the impl compute it from the elements.</summary>
        public uint Stride { get; }
        /// <summary>Advance rate: 0 = per-vertex, 1 = per-instance (the model pass instance buffer uses 1).</summary>
        public uint InstanceStepRate { get; }
        /// <summary>The vertex attributes in this buffer.</summary>
        public GpuVertexElement[] Elements { get; }

        public GpuVertexLayoutDescription(params GpuVertexElement[] elements)
        {
            Stride = 0; InstanceStepRate = 0; Elements = elements;
        }

        public GpuVertexLayoutDescription(uint stride, uint instanceStepRate, GpuVertexElement[] elements)
        {
            Stride = stride; InstanceStepRate = instanceStepRate; Elements = elements;
        }
    }

    /// <summary>Per-attachment blend state, with the three presets the renderers use.</summary>
    public readonly struct GpuBlendAttachment
    {
        /// <summary>Whether blending is enabled (else the source overwrites).</summary>
        public bool BlendEnabled { get; }
        public GpuBlendFactor SourceColorFactor { get; }
        public GpuBlendFactor DestinationColorFactor { get; }
        public GpuBlendFunction ColorFunction { get; }
        public GpuBlendFactor SourceAlphaFactor { get; }
        public GpuBlendFactor DestinationAlphaFactor { get; }
        public GpuBlendFunction AlphaFunction { get; }

        public GpuBlendAttachment(bool blendEnabled,
            GpuBlendFactor sourceColorFactor, GpuBlendFactor destinationColorFactor, GpuBlendFunction colorFunction,
            GpuBlendFactor sourceAlphaFactor, GpuBlendFactor destinationAlphaFactor, GpuBlendFunction alphaFunction)
        {
            BlendEnabled = blendEnabled;
            SourceColorFactor = sourceColorFactor; DestinationColorFactor = destinationColorFactor; ColorFunction = colorFunction;
            SourceAlphaFactor = sourceAlphaFactor; DestinationAlphaFactor = destinationAlphaFactor; AlphaFunction = alphaFunction;
        }

        /// <summary>No blending: the source overwrites the destination (model + post passes).</summary>
        public static GpuBlendAttachment OverrideBlend => new(
            false,
            GpuBlendFactor.One, GpuBlendFactor.Zero, GpuBlendFunction.Add,
            GpuBlendFactor.One, GpuBlendFactor.Zero, GpuBlendFunction.Add);

        /// <summary>Standard alpha blend (src.a / 1-src.a) for the 2D batch, lines, and alpha billboards.</summary>
        public static GpuBlendAttachment AlphaBlend => new(
            true,
            GpuBlendFactor.SourceAlpha, GpuBlendFactor.InverseSourceAlpha, GpuBlendFunction.Add,
            GpuBlendFactor.SourceAlpha, GpuBlendFactor.InverseSourceAlpha, GpuBlendFunction.Add);

        /// <summary>Additive blend (src.a / 1) for glowy billboards (sparks, flashes).</summary>
        public static GpuBlendAttachment Additive => new(
            true,
            GpuBlendFactor.SourceAlpha, GpuBlendFactor.One, GpuBlendFunction.Add,
            GpuBlendFactor.SourceAlpha, GpuBlendFactor.One, GpuBlendFunction.Add);

        /// <summary>Keep the destination untouched (out = dst): src factor Zero, dst factor One. For an MRT
        /// attachment a pass writes a fragment for (so the SPIR-V output count matches) but must not modify - e.g.
        /// the normal / linear-depth attachments under the textured-billboard pass, which only paints colour.</summary>
        public static GpuBlendAttachment PreserveDestination => new(
            true,
            GpuBlendFactor.Zero, GpuBlendFactor.One, GpuBlendFunction.Add,
            GpuBlendFactor.Zero, GpuBlendFactor.One, GpuBlendFunction.Add);
    }

    /// <summary>Depth-stencil pipeline state, in the subset the renderers use (depth test, write and
    /// comparison, no stencil).</summary>
    public readonly struct GpuDepthStencilState
    {
        /// <summary>Whether the depth test runs.</summary>
        public bool DepthTestEnabled { get; }
        /// <summary>Whether passing fragments write depth.</summary>
        public bool DepthWriteEnabled { get; }
        /// <summary>The depth comparison.</summary>
        public GpuComparison Comparison { get; }

        public GpuDepthStencilState(bool depthTestEnabled, bool depthWriteEnabled, GpuComparison comparison)
        {
            DepthTestEnabled = depthTestEnabled; DepthWriteEnabled = depthWriteEnabled; Comparison = comparison;
        }

        /// <summary>Depth off (2D batch, post chain, overlays).</summary>
        public static GpuDepthStencilState Disabled => new(false, false, GpuComparison.Always);
        /// <summary>Depth test + write with less-or-equal (the 3D model pass).</summary>
        public static GpuDepthStencilState DepthOnlyLessEqual => new(true, true, GpuComparison.LessEqual);
        /// <summary>Depth test (less-or-equal) WITHOUT depth write: alpha-blended geometry that must interleave
        /// with the opaque pass (read its depth so a nearer mesh occludes it and it draws over a farther mesh) but
        /// not write depth itself, so transparent quads don't occlude each other and stay ordered by submission /
        /// the host's back-to-front sort. Used by the textured-billboard pass.</summary>
        public static GpuDepthStencilState DepthTestLessEqualNoWrite => new(true, false, GpuComparison.LessEqual);
    }

    /// <summary>Rasterizer pipeline state.</summary>
    public readonly struct GpuRasterizerState
    {
        public GpuFaceCull CullMode { get; }
        public GpuPolygonFill FillMode { get; }
        public GpuFrontFace FrontFace { get; }

        /// <summary>
        /// Whether primitives are CLIPPED against the near and far planes (<c>true</c>) or CLAMPED to them
        /// (<c>false</c>).
        /// <para>
        /// THIS IS A BINDING CONTRACT ON EVERY BACKEND, not a hint one may ignore. Clamping keeps geometry that
        /// falls outside the depth range: it still rasterizes, with its depth pinned to the nearer or farther
        /// limit. It is what a far-plane background pass wants, and it is the classic directional-shadow
        /// pancaking trick, where casters behind the light's near plane must still write depth.
        /// </para>
        /// <para>
        /// It is the ONLY member here with no direct Metal equivalent, and that is why the contract is spelled
        /// out. Metal has no rasterizer depth-clip enable, so the Metal backend expresses <c>false</c> as
        /// <c>MTLDepthClipModeClamp</c> on the render encoder. Direct3D 11 passes it to
        /// <c>RasterizerDescription.DepthClipEnable</c> and Vulkan passes its INVERSE to
        /// <c>depthClampEnable</c>. Until 17.39.0 both Metal paths of the day derived the mode from
        /// <see cref="GpuDepthStencilState.DepthTestEnabled"/> and read this flag nowhere, which made four
        /// shipped pipelines rasterize differently on macOS
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/598).
        /// </para>
        /// <para>
        /// IT BINDS ON A PASS WITH NO DEPTH ATTACHMENT TOO, on every backend, and Metal was one release short of
        /// that until 18.10.0 (https://github.com/APKiwiOrg/KhaozEngine/issues/674). It used to emit
        /// <c>-setDepthClipMode:</c> inside the same guard as the depth-stencil state, whose condition is the
        /// bound framebuffer having a depth attachment, because a depth-stencil state on a depth-less pass IS a
        /// validation failure. A colour-only target therefore rasterized at the encoder default (clip) whatever
        /// this said. The clip mode is rasterizer state rather than depth state, so it is emitted unguarded now
        /// and the contract holds everywhere. No shipped pipeline could see the old hole: the engine's
        /// colour-only passes are the fullscreen post ones, whose vertex stage emits z = 0 exactly, and
        /// <c>SpriteBatch</c> takes its z from a 2D ortho, both inside the depth range where the two modes agree.
        /// </para>
        /// </summary>
        public bool DepthClipEnabled { get; }
        public bool ScissorTestEnabled { get; }

        public GpuRasterizerState(GpuFaceCull cullMode, GpuPolygonFill fillMode, GpuFrontFace frontFace,
            bool depthClipEnabled, bool scissorTestEnabled)
        {
            CullMode = cullMode; FillMode = fillMode; FrontFace = frontFace;
            DepthClipEnabled = depthClipEnabled; ScissorTestEnabled = scissorTestEnabled;
        }
    }

    /// <summary>Describes a framebuffer's attachment formats for pipeline creation: an optional depth format
    /// plus the colour attachment formats.</summary>
    public readonly struct GpuOutputDescription
    {
        /// <summary>Depth-stencil attachment format, or null if none.</summary>
        public GpuPixelFormat? Depth { get; }
        /// <summary>Colour attachment formats, in order.</summary>
        public GpuPixelFormat[] Colour { get; }
        /// <summary>MSAA sample count of the target framebuffer (1 = single-sample, the default). A pipeline's
        /// sample count MUST match the framebuffer it renders into, so a pipeline built for a multisampled target
        /// carries the same count here. Read off a live multisampled framebuffer via <see cref="IGpuFramebuffer.Outputs"/>.</summary>
        public int SampleCount { get; }

        public GpuOutputDescription(GpuPixelFormat? depth, params GpuPixelFormat[] colour)
        {
            Depth = depth; Colour = colour ?? Array.Empty<GpuPixelFormat>();
            SampleCount = 1;
        }

        GpuOutputDescription(GpuPixelFormat? depth, GpuPixelFormat[] colour, int sampleCount)
        {
            Depth = depth; Colour = colour ?? Array.Empty<GpuPixelFormat>();
            SampleCount = sampleCount < 1 ? 1 : sampleCount;
        }

        /// <summary>A copy with the MSAA <paramref name="sampleCount"/> set (for building a pipeline that targets a
        /// multisampled framebuffer). Formats are unchanged.</summary>
        public GpuOutputDescription WithSampleCount(int sampleCount) => new(Depth, Colour, sampleCount);
    }

    /// <summary>Describes a graphics pipeline: the per-attachment blend state, depth-stencil, rasterizer, topology, the vertex layouts (one per vertex
    /// buffer slot, including the per-instance buffer), the compiled shader set, the resource layouts, and the
    /// target outputs.</summary>
    public struct GpuPipelineDescription
    {
        /// <summary>Blend constant (rarely used; defaults to black).</summary>
        public System.Numerics.Vector4 BlendFactor;
        /// <summary>Per-attachment blend states (one per colour output; the MRT model pass passes three).</summary>
        public GpuBlendAttachment[] BlendAttachments;
        /// <summary>Depth-stencil state.</summary>
        public GpuDepthStencilState DepthStencil;
        /// <summary>Rasterizer state.</summary>
        public GpuRasterizerState Rasterizer;
        /// <summary>Primitive topology.</summary>
        public GpuPrimitiveTopology Topology;
        /// <summary>One layout per vertex buffer slot (empty for fullscreen passes).</summary>
        public List<GpuVertexLayoutDescription> VertexLayouts;
        /// <summary>The compiled shaders (created via <see cref="IGpuResourceFactory.CreateShadersFromSpirv"/>).</summary>
        public IGpuShaderSet ShaderSet;
        /// <summary>The resource layouts (binding sets), in set order.</summary>
        public IGpuResourceLayout[] ResourceLayouts;
        /// <summary>The render-target formats this pipeline draws into.</summary>
        public GpuOutputDescription Outputs;
    }

    /// <summary>Describes a compute pipeline: the compiled compute shader plus the resource layouts (binding sets,
    /// in set order). It carries no thread-group size:
    /// the engine reads that off the shader module instead (see <see cref="IGpuComputeShader.ThreadGroupSizeX"/>),
    /// so there is no second copy of the workgroup size to disagree with the GLSL.
    ///
    /// Deliberately separate from <see cref="GpuPipelineDescription"/>: a compute pipeline has no vertex layout, no
    /// blend/depth/raster state, and no render-target outputs, and the two produce different handle types
    /// (<see cref="IGpuComputePipeline"/> vs <see cref="IGpuPipeline"/>) so a compute pipeline cannot be bound for
    /// a draw.</summary>
    public readonly struct GpuComputePipelineDescription
    {
        /// <summary>The compiled compute shader (from <see cref="IGpuResourceFactory.CreateComputeShaderFromSpirv"/>).</summary>
        public IGpuComputeShader Shader { get; }
        /// <summary>The resource layouts (binding sets), in set order. Every element's
        /// <see cref="GpuResourceLayoutElement.Stages"/> must include <see cref="GpuShaderStages.Compute"/>.</summary>
        public IGpuResourceLayout[] ResourceLayouts { get; }

        public GpuComputePipelineDescription(IGpuComputeShader shader, params IGpuResourceLayout[] resourceLayouts)
        {
            Shader = shader;
            ResourceLayouts = resourceLayouts ?? Array.Empty<IGpuResourceLayout>();
        }
    }
}
