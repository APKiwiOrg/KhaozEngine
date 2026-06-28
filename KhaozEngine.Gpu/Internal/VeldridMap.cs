using System;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Pure 1:1 mapping between the engine GPU enums/descriptions and Veldrid's. Kept in one place so
    /// the impl wrappers stay thin and the mapping is unit-testable (the pure enum/format/blend conversions need
    /// no GPU).</summary>
    internal static class VeldridMap
    {
        public static PixelFormat ToVeldrid(GpuPixelFormat f) => f switch
        {
            GpuPixelFormat.R8G8B8A8UNorm => PixelFormat.R8_G8_B8_A8_UNorm,
            GpuPixelFormat.R32Float => PixelFormat.R32_Float,
            GpuPixelFormat.D32FloatS8UInt => PixelFormat.D32_Float_S8_UInt,
            GpuPixelFormat.D24UNormS8UInt => PixelFormat.D24_UNorm_S8_UInt,
            GpuPixelFormat.R8UNorm => PixelFormat.R8_UNorm,
            GpuPixelFormat.B8G8R8A8UNorm => PixelFormat.B8_G8_R8_A8_UNorm,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, "Unmapped GpuPixelFormat"),
        };

        public static GpuPixelFormat FromVeldrid(PixelFormat f) => f switch
        {
            PixelFormat.R8_G8_B8_A8_UNorm => GpuPixelFormat.R8G8B8A8UNorm,
            PixelFormat.R32_Float => GpuPixelFormat.R32Float,
            PixelFormat.D32_Float_S8_UInt => GpuPixelFormat.D32FloatS8UInt,
            PixelFormat.D24_UNorm_S8_UInt => GpuPixelFormat.D24UNormS8UInt,
            PixelFormat.R8_UNorm => GpuPixelFormat.R8UNorm,
            PixelFormat.B8_G8_R8_A8_UNorm => GpuPixelFormat.B8G8R8A8UNorm,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, "Unmapped Veldrid PixelFormat"),
        };

        public static TextureUsage ToVeldrid(GpuTextureUsage u)
        {
            TextureUsage r = 0;
            if ((u & GpuTextureUsage.Sampled) != 0) r |= TextureUsage.Sampled;
            if ((u & GpuTextureUsage.RenderTarget) != 0) r |= TextureUsage.RenderTarget;
            if ((u & GpuTextureUsage.DepthStencil) != 0) r |= TextureUsage.DepthStencil;
            if ((u & GpuTextureUsage.Staging) != 0) r |= TextureUsage.Staging;
            if ((u & GpuTextureUsage.Cubemap) != 0) r |= TextureUsage.Cubemap;
            if ((u & GpuTextureUsage.GenerateMipmaps) != 0) r |= TextureUsage.GenerateMipmaps;
            return r;
        }

        public static BufferUsage ToVeldrid(GpuBufferUsage u)
        {
            BufferUsage r = 0;
            if ((u & GpuBufferUsage.VertexBuffer) != 0) r |= BufferUsage.VertexBuffer;
            if ((u & GpuBufferUsage.IndexBuffer) != 0) r |= BufferUsage.IndexBuffer;
            if ((u & GpuBufferUsage.UniformBuffer) != 0) r |= BufferUsage.UniformBuffer;
            if ((u & GpuBufferUsage.StructuredBufferReadOnly) != 0) r |= BufferUsage.StructuredBufferReadOnly;
            if ((u & GpuBufferUsage.StructuredBufferReadWrite) != 0) r |= BufferUsage.StructuredBufferReadWrite;
            if ((u & GpuBufferUsage.IndirectBuffer) != 0) r |= BufferUsage.IndirectBuffer;
            if ((u & GpuBufferUsage.Dynamic) != 0) r |= BufferUsage.Dynamic;
            if ((u & GpuBufferUsage.Staging) != 0) r |= BufferUsage.Staging;
            return r;
        }

        public static IndexFormat ToVeldrid(GpuIndexFormat f) => f switch
        {
            GpuIndexFormat.UInt16 => IndexFormat.UInt16,
            GpuIndexFormat.UInt32 => IndexFormat.UInt32,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, "Unmapped GpuIndexFormat"),
        };

        public static PrimitiveTopology ToVeldrid(GpuPrimitiveTopology t) => t switch
        {
            GpuPrimitiveTopology.TriangleList => PrimitiveTopology.TriangleList,
            GpuPrimitiveTopology.TriangleStrip => PrimitiveTopology.TriangleStrip,
            GpuPrimitiveTopology.LineList => PrimitiveTopology.LineList,
            GpuPrimitiveTopology.LineStrip => PrimitiveTopology.LineStrip,
            GpuPrimitiveTopology.PointList => PrimitiveTopology.PointList,
            _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unmapped GpuPrimitiveTopology"),
        };

        public static ShaderStages ToVeldrid(GpuShaderStages s)
        {
            ShaderStages r = 0;
            if ((s & GpuShaderStages.Vertex) != 0) r |= ShaderStages.Vertex;
            if ((s & GpuShaderStages.Geometry) != 0) r |= ShaderStages.Geometry;
            if ((s & GpuShaderStages.TessellationControl) != 0) r |= ShaderStages.TessellationControl;
            if ((s & GpuShaderStages.TessellationEvaluation) != 0) r |= ShaderStages.TessellationEvaluation;
            if ((s & GpuShaderStages.Fragment) != 0) r |= ShaderStages.Fragment;
            if ((s & GpuShaderStages.Compute) != 0) r |= ShaderStages.Compute;
            return r;
        }

        public static ResourceKind ToVeldrid(GpuResourceKind k) => k switch
        {
            GpuResourceKind.UniformBuffer => ResourceKind.UniformBuffer,
            GpuResourceKind.StructuredBufferReadOnly => ResourceKind.StructuredBufferReadOnly,
            GpuResourceKind.StructuredBufferReadWrite => ResourceKind.StructuredBufferReadWrite,
            GpuResourceKind.TextureReadOnly => ResourceKind.TextureReadOnly,
            GpuResourceKind.TextureReadWrite => ResourceKind.TextureReadWrite,
            GpuResourceKind.Sampler => ResourceKind.Sampler,
            _ => throw new ArgumentOutOfRangeException(nameof(k), k, "Unmapped GpuResourceKind"),
        };

        public static FaceCullMode ToVeldrid(GpuFaceCull c) => c switch
        {
            GpuFaceCull.Back => FaceCullMode.Back,
            GpuFaceCull.Front => FaceCullMode.Front,
            GpuFaceCull.None => FaceCullMode.None,
            _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Unmapped GpuFaceCull"),
        };

        public static PolygonFillMode ToVeldrid(GpuPolygonFill f) => f switch
        {
            GpuPolygonFill.Solid => PolygonFillMode.Solid,
            GpuPolygonFill.Wireframe => PolygonFillMode.Wireframe,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, "Unmapped GpuPolygonFill"),
        };

        public static FrontFace ToVeldrid(GpuFrontFace f) => f switch
        {
            GpuFrontFace.Clockwise => FrontFace.Clockwise,
            GpuFrontFace.CounterClockwise => FrontFace.CounterClockwise,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, "Unmapped GpuFrontFace"),
        };

        public static ComparisonKind ToVeldrid(GpuComparison c) => c switch
        {
            GpuComparison.Never => ComparisonKind.Never,
            GpuComparison.Less => ComparisonKind.Less,
            GpuComparison.Equal => ComparisonKind.Equal,
            GpuComparison.LessEqual => ComparisonKind.LessEqual,
            GpuComparison.Greater => ComparisonKind.Greater,
            GpuComparison.NotEqual => ComparisonKind.NotEqual,
            GpuComparison.GreaterEqual => ComparisonKind.GreaterEqual,
            GpuComparison.Always => ComparisonKind.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Unmapped GpuComparison"),
        };

        public static BlendFactor ToVeldrid(GpuBlendFactor b) => b switch
        {
            GpuBlendFactor.Zero => BlendFactor.Zero,
            GpuBlendFactor.One => BlendFactor.One,
            GpuBlendFactor.SourceColor => BlendFactor.SourceColor,
            GpuBlendFactor.InverseSourceColor => BlendFactor.InverseSourceColor,
            GpuBlendFactor.SourceAlpha => BlendFactor.SourceAlpha,
            GpuBlendFactor.InverseSourceAlpha => BlendFactor.InverseSourceAlpha,
            GpuBlendFactor.DestinationColor => BlendFactor.DestinationColor,
            GpuBlendFactor.InverseDestinationColor => BlendFactor.InverseDestinationColor,
            GpuBlendFactor.DestinationAlpha => BlendFactor.DestinationAlpha,
            GpuBlendFactor.InverseDestinationAlpha => BlendFactor.InverseDestinationAlpha,
            GpuBlendFactor.BlendFactor => BlendFactor.BlendFactor,
            GpuBlendFactor.InverseBlendFactor => BlendFactor.InverseBlendFactor,
            _ => throw new ArgumentOutOfRangeException(nameof(b), b, "Unmapped GpuBlendFactor"),
        };

        public static BlendFunction ToVeldrid(GpuBlendFunction f) => f switch
        {
            GpuBlendFunction.Add => BlendFunction.Add,
            GpuBlendFunction.Subtract => BlendFunction.Subtract,
            GpuBlendFunction.ReverseSubtract => BlendFunction.ReverseSubtract,
            GpuBlendFunction.Minimum => BlendFunction.Minimum,
            GpuBlendFunction.Maximum => BlendFunction.Maximum,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, "Unmapped GpuBlendFunction"),
        };

        public static SamplerFilter ToVeldrid(GpuSamplerFilter f) => f switch
        {
            GpuSamplerFilter.MinPointMagPointMipPoint => SamplerFilter.MinPoint_MagPoint_MipPoint,
            GpuSamplerFilter.MinLinearMagLinearMipLinear => SamplerFilter.MinLinear_MagLinear_MipLinear,
            GpuSamplerFilter.Anisotropic => SamplerFilter.Anisotropic,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, "Unmapped GpuSamplerFilter"),
        };

        public static SamplerAddressMode ToVeldrid(GpuSamplerAddress a) => a switch
        {
            GpuSamplerAddress.Wrap => SamplerAddressMode.Wrap,
            GpuSamplerAddress.Mirror => SamplerAddressMode.Mirror,
            GpuSamplerAddress.Clamp => SamplerAddressMode.Clamp,
            GpuSamplerAddress.Border => SamplerAddressMode.Border,
            _ => throw new ArgumentOutOfRangeException(nameof(a), a, "Unmapped GpuSamplerAddress"),
        };

        public static MapMode ToVeldrid(GpuMapMode m) => m switch
        {
            GpuMapMode.Read => MapMode.Read,
            GpuMapMode.Write => MapMode.Write,
            GpuMapMode.ReadWrite => MapMode.ReadWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(m), m, "Unmapped GpuMapMode"),
        };

        public static VertexElementFormat ToVeldrid(GpuVertexElementFormat f) => f switch
        {
            GpuVertexElementFormat.Float1 => VertexElementFormat.Float1,
            GpuVertexElementFormat.Float2 => VertexElementFormat.Float2,
            GpuVertexElementFormat.Float3 => VertexElementFormat.Float3,
            GpuVertexElementFormat.Float4 => VertexElementFormat.Float4,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, "Unmapped GpuVertexElementFormat"),
        };

        public static BlendAttachmentDescription ToVeldrid(in GpuBlendAttachment a) => new(
            a.BlendEnabled,
            ToVeldrid(a.SourceColorFactor), ToVeldrid(a.DestinationColorFactor), ToVeldrid(a.ColorFunction),
            ToVeldrid(a.SourceAlphaFactor), ToVeldrid(a.DestinationAlphaFactor), ToVeldrid(a.AlphaFunction));

        public static OutputDescription ToVeldrid(in GpuOutputDescription o)
        {
            OutputAttachmentDescription? depth = o.Depth.HasValue
                ? new OutputAttachmentDescription(ToVeldrid(o.Depth.Value))
                : (OutputAttachmentDescription?)null;
            var colour = new OutputAttachmentDescription[o.Colour.Length];
            for (int i = 0; i < colour.Length; i++)
                colour[i] = new OutputAttachmentDescription(ToVeldrid(o.Colour[i]));
            // Sample count fixed at Count1 (the 5.x renderers don't MSAA); mirrors Veldrid default.
            return new OutputDescription(depth, colour);
        }

        public static GpuOutputDescription FromVeldrid(in OutputDescription o)
        {
            GpuPixelFormat? depth = o.DepthAttachment.HasValue
                ? FromVeldrid(o.DepthAttachment.Value.Format)
                : (GpuPixelFormat?)null;
            var colour = new GpuPixelFormat[o.ColorAttachments.Length];
            for (int i = 0; i < colour.Length; i++)
                colour[i] = FromVeldrid(o.ColorAttachments[i].Format);
            return new GpuOutputDescription(depth, colour);
        }
    }
}
