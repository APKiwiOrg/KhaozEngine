using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu
{
    /// <summary>Describes a GPU buffer to create. Engine mirror of Veldrid <c>BufferDescription</c>.</summary>
    public readonly struct GpuBufferDescription
    {
        /// <summary>Size of the buffer in bytes.</summary>
        public uint SizeInBytes { get; }
        /// <summary>How the buffer is used.</summary>
        public GpuBufferUsage Usage { get; }
        /// <summary>For a structured buffer, the per-element stride in bytes (0 otherwise).</summary>
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

        public GpuTextureDescription(uint width, uint height, GpuPixelFormat format, GpuTextureUsage usage,
            uint mipLevels = 1, uint arrayLayers = 1)
        {
            Width = width; Height = height; Format = format; Usage = usage;
            MipLevels = mipLevels; ArrayLayers = arrayLayers;
        }

        /// <summary>Convenience for a single-mip, single-layer 2D texture (mirrors <c>TextureDescription.Texture2D</c>).</summary>
        public static GpuTextureDescription Texture2D(uint width, uint height, GpuPixelFormat format, GpuTextureUsage usage)
            => new(width, height, format, usage, 1, 1);
    }

    /// <summary>Describes a GPU sampler. Engine mirror of Veldrid <c>SamplerDescription</c> (the subset used).</summary>
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

        public GpuSamplerDescription(GpuSamplerFilter filter,
            GpuSamplerAddress addressU = GpuSamplerAddress.Clamp,
            GpuSamplerAddress addressV = GpuSamplerAddress.Clamp,
            GpuSamplerAddress addressW = GpuSamplerAddress.Clamp)
        {
            Filter = filter; AddressModeU = addressU; AddressModeV = addressV; AddressModeW = addressW;
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

        public GpuResourceLayoutElement(string name, GpuResourceKind kind, GpuShaderStages stages)
        {
            Name = name; Kind = kind; Stages = stages;
        }
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

    /// <summary>Vertex attribute component format. Mirrors the subset of Veldrid <c>VertexElementFormat</c> the
    /// renderers use (Float2/3/4).</summary>
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

    /// <summary>Per-attachment blend state. Engine mirror of Veldrid <c>BlendAttachmentDescription</c>, with the
    /// three presets the renderers use.</summary>
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

    /// <summary>Depth-stencil pipeline state. Engine mirror of the subset of Veldrid
    /// <c>DepthStencilStateDescription</c> the renderers use (depth test/write/comparison, no stencil).</summary>
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

    /// <summary>Rasterizer pipeline state. Engine mirror of Veldrid <c>RasterizerStateDescription</c>.</summary>
    public readonly struct GpuRasterizerState
    {
        public GpuFaceCull CullMode { get; }
        public GpuPolygonFill FillMode { get; }
        public GpuFrontFace FrontFace { get; }
        public bool DepthClipEnabled { get; }
        public bool ScissorTestEnabled { get; }

        public GpuRasterizerState(GpuFaceCull cullMode, GpuPolygonFill fillMode, GpuFrontFace frontFace,
            bool depthClipEnabled, bool scissorTestEnabled)
        {
            CullMode = cullMode; FillMode = fillMode; FrontFace = frontFace;
            DepthClipEnabled = depthClipEnabled; ScissorTestEnabled = scissorTestEnabled;
        }
    }

    /// <summary>Describes a framebuffer's attachment formats for pipeline creation. Engine mirror of Veldrid
    /// <c>OutputDescription</c>: an optional depth format plus the colour attachment formats.</summary>
    public readonly struct GpuOutputDescription
    {
        /// <summary>Depth-stencil attachment format, or null if none.</summary>
        public GpuPixelFormat? Depth { get; }
        /// <summary>Colour attachment formats, in order.</summary>
        public GpuPixelFormat[] Colour { get; }

        public GpuOutputDescription(GpuPixelFormat? depth, params GpuPixelFormat[] colour)
        {
            Depth = depth; Colour = colour ?? Array.Empty<GpuPixelFormat>();
        }
    }

    /// <summary>Describes a graphics pipeline. Engine mirror of Veldrid <c>GraphicsPipelineDescription</c>: the
    /// per-attachment blend state, depth-stencil, rasterizer, topology, the vertex layouts (one per vertex
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
}
