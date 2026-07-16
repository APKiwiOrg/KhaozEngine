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
            GpuPixelFormat.R16G16B16A16Float => PixelFormat.R16_G16_B16_A16_Float,
            GpuPixelFormat.R16G16Float => PixelFormat.R16_G16_Float,
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
            PixelFormat.R16_G16_B16_A16_Float => GpuPixelFormat.R16G16B16A16Float,
            PixelFormat.R16_G16_Float => GpuPixelFormat.R16G16Float,
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
            // MSAA sample count from the description (1 = single-sample). A pipeline's count must match the
            // framebuffer it targets; RenderResources sets it from the live multisampled framebuffer's Outputs.
            return new OutputDescription(depth, colour, ToVeldrid((uint)o.SampleCount));
        }

        public static GpuOutputDescription FromVeldrid(in OutputDescription o)
        {
            GpuPixelFormat? depth = o.DepthAttachment.HasValue
                ? FromVeldrid(o.DepthAttachment.Value.Format)
                : (GpuPixelFormat?)null;
            var colour = new GpuPixelFormat[o.ColorAttachments.Length];
            for (int i = 0; i < colour.Length; i++)
                colour[i] = FromVeldrid(o.ColorAttachments[i].Format);
            return new GpuOutputDescription(depth, colour).WithSampleCount((int)SampleCountToInt(o.SampleCount));
        }

        /// <summary>Map an integer MSAA sample count to the Veldrid enum (non-power-of-two / unsupported falls to
        /// Count1; callers clamp to a supported power of two via <c>AntiAliasing.ResolveFor</c>).</summary>
        public static TextureSampleCount ToVeldrid(uint samples) => samples switch
        {
            2 => TextureSampleCount.Count2,
            4 => TextureSampleCount.Count4,
            8 => TextureSampleCount.Count8,
            16 => TextureSampleCount.Count16,
            32 => TextureSampleCount.Count32,
            _ => TextureSampleCount.Count1,
        };

        /// <summary>Map a Veldrid MSAA sample-count enum back to the integer count.</summary>
        public static uint SampleCountToInt(TextureSampleCount c) => c switch
        {
            TextureSampleCount.Count2 => 2,
            TextureSampleCount.Count4 => 4,
            TextureSampleCount.Count8 => 8,
            TextureSampleCount.Count16 => 16,
            TextureSampleCount.Count32 => 32,
            _ => 1,
        };

        /// <summary>Whether the device can drive the shadow-map path: R32_Float usable as BOTH a render target and a
        /// sampled texture (the manual-PCF depth-compare samples the depth target). Defensive: a query failure =>
        /// false (degrade to blob), never throw.</summary>
        public static bool SupportsShadowMaps(GraphicsDevice gd)
        {
            try
            {
                return gd.GetPixelFormatSupport(PixelFormat.R32_Float, TextureType.Texture2D,
                    TextureUsage.RenderTarget | TextureUsage.Sampled);
            }
            catch { return false; }
        }

        /// <summary>The largest MSAA sample count usable for the engine's MRT: the MIN over the colour
        /// (R8G8B8A8_UNorm), linear-depth (R32_Float), and depth-stencil (D32_Float_S8_UInt) formats the 3D scene
        /// renders into (every attachment must support the count). Defensive: any device query failure => 1 (no MSAA).</summary>
        public static int MaxMsaaSampleCount(GraphicsDevice gd)
        {
            try
            {
                uint color = SampleCountToInt(gd.GetSampleCountLimit(PixelFormat.R8_G8_B8_A8_UNorm, false));
                uint depthColor = SampleCountToInt(gd.GetSampleCountLimit(PixelFormat.R32_Float, false));
                uint depth = SampleCountToInt(gd.GetSampleCountLimit(PixelFormat.D32_Float_S8_UInt, true));
                return (int)Math.Min(color, Math.Min(depthColor, depth));
            }
            catch { return 1; }
        }
    }
}
