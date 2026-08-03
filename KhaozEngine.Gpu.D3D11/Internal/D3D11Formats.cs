using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The whole engine-to-Direct3D mapping: pixel formats and their typeless and view variants, index and vertex
    /// formats, bind flags, and every pipeline state enum. One place, so a wrapper is thin and the mapping cannot
    /// drift between two of them.
    /// <para>
    /// THIS IS A WINDOWS-BOUNDARY TYPE. Every member names a Vortice enum, so every body is
    /// <see cref="MethodImplOptions.NoInlining"/> under a type-level <c>[SupportedOSPlatform("windows")]</c>, the
    /// same shape <see cref="D3D11FeatureProbe"/> uses. Callers are the resource wrappers, which are themselves
    /// only reachable from a guarded entry point, so the Vortice assembly stays off the load path on macOS and
    /// Linux even though this file ships there. The DERIVATIONS that decide WHICH flags and views a resource gets
    /// live in <see cref="D3D11ViewPolicy"/> instead, in engine types, so the interesting half is testable without
    /// a device.
    /// </para>
    /// <para>
    /// ONLY THE ENGINE'S OWN FORMATS ARE MAPPED. <see cref="GpuPixelFormat"/> has eight members and the seam
    /// cannot express a ninth, so an unmapped value is a seam change that has not reached here yet, and it throws
    /// rather than guessing.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class D3D11Formats
    {
        /// <summary>
        /// The DXGI format for a pixel format. <paramref name="depthStencil"/> asks for the DEPTH reading, which is
        /// typeless for the combined depth formats: a depth texture is created typeless so the same resource can
        /// carry a depth-stencil view and a shader resource view with two different concrete formats, which is what
        /// sampling a depth buffer needs.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Format ToDxgiFormat(GpuPixelFormat format, bool depthStencil) => format switch
        {
            GpuPixelFormat.R8G8B8A8UNorm => Format.R8G8B8A8_UNorm,
            GpuPixelFormat.B8G8R8A8UNorm => Format.B8G8R8A8_UNorm,
            GpuPixelFormat.R8UNorm => Format.R8_UNorm,
            GpuPixelFormat.R16G16B16A16Float => Format.R16G16B16A16_Float,
            GpuPixelFormat.R16G16Float => Format.R16G16_Float,
            GpuPixelFormat.R32Float => depthStencil ? Format.R32_Typeless : Format.R32_Float,
            GpuPixelFormat.D24UNormS8UInt => Format.R24G8_Typeless,
            GpuPixelFormat.D32FloatS8UInt => Format.R32G8X24_Typeless,
            _ => throw Unmapped(format),
        };

        /// <summary>
        /// The TYPELESS variant the texture RESOURCE is created with. Reproduced from the incumbent, which creates
        /// every texture typeless and gives each view its own concrete format, so a depth target can also be
        /// sampled and a render target can be read back without a second resource.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Format ToTypelessFormat(Format format) => format switch
        {
            Format.R8G8B8A8_UNorm => Format.R8G8B8A8_Typeless,
            Format.B8G8R8A8_UNorm => Format.B8G8R8A8_Typeless,
            Format.R8_UNorm => Format.R8_Typeless,
            Format.R16G16B16A16_Float => Format.R16G16B16A16_Typeless,
            Format.R16G16_Float => Format.R16G16_Typeless,
            Format.R32_Float => Format.R32_Typeless,
            _ => format,   // already typeless (the two combined depth formats arrive that way)
        };

        /// <summary>The concrete format a SHADER RESOURCE view reads a typeless resource through.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Format ToViewFormat(Format format) => format switch
        {
            Format.R32_Typeless => Format.R32_Float,
            Format.R32G8X24_Typeless => Format.R32_Float_X8X24_Typeless,
            Format.R24G8_Typeless => Format.R24_UNorm_X8_Typeless,
            Format.R16_Typeless => Format.R16_UNorm,
            _ => format,
        };

        /// <summary>The concrete format a DEPTH-STENCIL view writes through. A format that cannot be a depth
        /// attachment throws, because the alternative is a view the runtime silently refuses at draw time.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Format ToDepthViewFormat(GpuPixelFormat format) => format switch
        {
            GpuPixelFormat.R32Float => Format.D32_Float,
            GpuPixelFormat.D24UNormS8UInt => Format.D24_UNorm_S8_UInt,
            GpuPixelFormat.D32FloatS8UInt => Format.D32_Float_S8X24_UInt,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format,
                "That pixel format cannot back a depth-stencil attachment on Direct3D 11."),
        };

        /// <summary>Index element format.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Format ToDxgiFormat(GpuIndexFormat format) => format switch
        {
            GpuIndexFormat.UInt16 => Format.R16_UInt,
            GpuIndexFormat.UInt32 => Format.R32_UInt,
            _ => throw Unmapped(format),
        };

        /// <summary>Vertex attribute component format.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Format ToDxgiFormat(GpuVertexElementFormat format) => format switch
        {
            GpuVertexElementFormat.Float1 => Format.R32_Float,
            GpuVertexElementFormat.Float2 => Format.R32G32_Float,
            GpuVertexElementFormat.Float3 => Format.R32G32B32_Float,
            GpuVertexElementFormat.Float4 => Format.R32G32B32A32_Float,
            _ => throw Unmapped(format),
        };

        /// <summary>The real bind flags for a <see cref="D3D11BindUsage"/> set. The DERIVATION of that set from
        /// the seam's usage bits is <see cref="D3D11ViewPolicy"/>'s, and is tested without a device.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static BindFlags ToBindFlags(D3D11BindUsage usage)
        {
            BindFlags flags = BindFlags.None;
            if ((usage & D3D11BindUsage.VertexBuffer) != 0) flags |= BindFlags.VertexBuffer;
            if ((usage & D3D11BindUsage.IndexBuffer) != 0) flags |= BindFlags.IndexBuffer;
            if ((usage & D3D11BindUsage.ConstantBuffer) != 0) flags |= BindFlags.ConstantBuffer;
            if ((usage & D3D11BindUsage.ShaderResource) != 0) flags |= BindFlags.ShaderResource;
            if ((usage & D3D11BindUsage.UnorderedAccess) != 0) flags |= BindFlags.UnorderedAccess;
            if ((usage & D3D11BindUsage.RenderTarget) != 0) flags |= BindFlags.RenderTarget;
            if ((usage & D3D11BindUsage.DepthStencil) != 0) flags |= BindFlags.DepthStencil;
            return flags;
        }

        /// <summary>Blend source or destination factor.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Blend ToBlend(GpuBlendFactor factor) => factor switch
        {
            GpuBlendFactor.Zero => Blend.Zero,
            GpuBlendFactor.One => Blend.One,
            GpuBlendFactor.SourceColor => Blend.SourceColor,
            GpuBlendFactor.InverseSourceColor => Blend.InverseSourceColor,
            GpuBlendFactor.SourceAlpha => Blend.SourceAlpha,
            GpuBlendFactor.InverseSourceAlpha => Blend.InverseSourceAlpha,
            GpuBlendFactor.DestinationColor => Blend.DestinationColor,
            GpuBlendFactor.InverseDestinationColor => Blend.InverseDestinationColor,
            GpuBlendFactor.DestinationAlpha => Blend.DestinationAlpha,
            GpuBlendFactor.InverseDestinationAlpha => Blend.InverseDestinationAlpha,
            GpuBlendFactor.BlendFactor => Blend.BlendFactor,
            GpuBlendFactor.InverseBlendFactor => Blend.InverseBlendFactor,
            _ => throw Unmapped(factor),
        };

        /// <summary>Blend equation.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static BlendOperation ToBlendOperation(GpuBlendFunction function) => function switch
        {
            GpuBlendFunction.Add => BlendOperation.Add,
            GpuBlendFunction.Subtract => BlendOperation.Subtract,
            GpuBlendFunction.ReverseSubtract => BlendOperation.ReverseSubtract,
            GpuBlendFunction.Minimum => BlendOperation.Min,
            GpuBlendFunction.Maximum => BlendOperation.Max,
            _ => throw Unmapped(function),
        };

        /// <summary>Depth comparison.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ComparisonFunction ToComparison(GpuComparison comparison) => comparison switch
        {
            GpuComparison.Never => ComparisonFunction.Never,
            GpuComparison.Less => ComparisonFunction.Less,
            GpuComparison.Equal => ComparisonFunction.Equal,
            GpuComparison.LessEqual => ComparisonFunction.LessEqual,
            GpuComparison.Greater => ComparisonFunction.Greater,
            GpuComparison.NotEqual => ComparisonFunction.NotEqual,
            GpuComparison.GreaterEqual => ComparisonFunction.GreaterEqual,
            GpuComparison.Always => ComparisonFunction.Always,
            _ => throw Unmapped(comparison),
        };

        /// <summary>Face culling.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static CullMode ToCullMode(GpuFaceCull cull) => cull switch
        {
            GpuFaceCull.Back => CullMode.Back,
            GpuFaceCull.Front => CullMode.Front,
            GpuFaceCull.None => CullMode.None,
            _ => throw Unmapped(cull),
        };

        /// <summary>Polygon fill.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static FillMode ToFillMode(GpuPolygonFill fill) => fill switch
        {
            GpuPolygonFill.Solid => FillMode.Solid,
            GpuPolygonFill.Wireframe => FillMode.Wireframe,
            _ => throw Unmapped(fill),
        };

        /// <summary>Primitive topology.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Vortice.Direct3D.PrimitiveTopology ToTopology(GpuPrimitiveTopology topology) => topology switch
        {
            GpuPrimitiveTopology.TriangleList => Vortice.Direct3D.PrimitiveTopology.TriangleList,
            GpuPrimitiveTopology.TriangleStrip => Vortice.Direct3D.PrimitiveTopology.TriangleStrip,
            GpuPrimitiveTopology.LineList => Vortice.Direct3D.PrimitiveTopology.LineList,
            GpuPrimitiveTopology.LineStrip => Vortice.Direct3D.PrimitiveTopology.LineStrip,
            GpuPrimitiveTopology.PointList => Vortice.Direct3D.PrimitiveTopology.PointList,
            _ => throw Unmapped(topology),
        };

        /// <summary>Sampler addressing.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static TextureAddressMode ToAddressMode(GpuSamplerAddress address) => address switch
        {
            GpuSamplerAddress.Wrap => TextureAddressMode.Wrap,
            GpuSamplerAddress.Mirror => TextureAddressMode.Mirror,
            GpuSamplerAddress.Clamp => TextureAddressMode.Clamp,
            GpuSamplerAddress.Border => TextureAddressMode.Border,
            _ => throw Unmapped(address),
        };

        /// <summary>Sampler filtering. The engine has no comparison sampler (its shadow path does manual PCF), so
        /// only the plain filters are reachable.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Filter ToFilter(GpuSamplerFilter filter) => filter switch
        {
            GpuSamplerFilter.MinPointMagPointMipPoint => Filter.MinMagMipPoint,
            GpuSamplerFilter.MinLinearMagLinearMipLinear => Filter.MinMagMipLinear,
            GpuSamplerFilter.Anisotropic => Filter.Anisotropic,
            _ => throw Unmapped(filter),
        };

        static ArgumentOutOfRangeException Unmapped<T>(T value) where T : struct
            => new(nameof(value), value,
                $"Unmapped {typeof(T).Name} on the native Direct3D 11 backend. The seam gained a member that this "
                + "mapping has not been taught, and guessing would render the wrong thing rather than fail.");
    }
}
