using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>The three filter selections one <see cref="GpuSamplerFilter"/> resolves to. A record struct rather
    /// than three <c>out</c> parameters, so a <c>[Fact]</c> can compare a whole answer in one assertion where the
    /// incumbent's <c>out</c>-parameter shape would need three.</summary>
    /// <param name="Min">Minification filter.</param>
    /// <param name="Mag">Magnification filter.</param>
    /// <param name="Mip">Mip filter.</param>
    internal readonly record struct MetalFilterSelection(
        MTLSamplerMinMagFilter Min, MTLSamplerMinMagFilter Mag, MTLSamplerMipFilter Mip);

    /// <summary>
    /// THE PIPELINE AND SAMPLER STATE DOMAIN of the format map (see <c>MetalFormats.Pixel.cs</c> for the split and
    /// its reason). Addressing and filtering, plus the rasterizer, depth, blend and topology maps a pipeline
    /// resolves at creation, all reproduced from <c>Veldrid.MTL.MTLFormats</c>.
    ///
    /// <para><b>EVERY MAP HERE IS TOTAL AND EVERY THROW IS FOR A MEMBER THAT DOES NOT EXIST YET.</b> The seam's
    /// state enums are small and closed, so each of these lists every member rather than defaulting one, and the
    /// <see cref="ArgumentOutOfRangeException"/> arms exist for a seam that grows. That matters more here than it
    /// looks: a defaulted blend factor renders a wrong pixel with no error at all, on a path where the committed
    /// <c>metal</c> goldens were baked through the incumbent's own answers.</para>
    /// </summary>
    internal static partial class MetalFormats
    {
        /// <summary><c>MTLFormats.VdToMTLCullMode</c>. Total over <see cref="GpuFaceCull"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A seam cull mode this map has not been taught.</exception>
        internal static MTLCullMode ToCullMode(GpuFaceCull cull) => cull switch
        {
            GpuFaceCull.Back => MTLCullMode.Back,
            GpuFaceCull.Front => MTLCullMode.Front,
            GpuFaceCull.None => MTLCullMode.None,
            _ => throw new ArgumentOutOfRangeException(nameof(cull), cull,
                "The native Metal backend has no MTLCullMode for that GPU seam cull mode."),
        };

        /// <summary>
        /// <c>MTLFormats.VdVoMTLFrontFace</c>. Two-valued, and written as a total switch rather than as the
        /// incumbent's <c>== CounterClockwise ? ... : ...</c>: an equality test against ONE member answers
        /// clockwise for a third member added later, where this refuses.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A seam winding this map has not been taught.</exception>
        internal static MTLWinding ToWinding(GpuFrontFace face) => face switch
        {
            GpuFrontFace.Clockwise => MTLWinding.Clockwise,
            GpuFrontFace.CounterClockwise => MTLWinding.CounterClockwise,
            _ => throw new ArgumentOutOfRangeException(nameof(face), face,
                "The native Metal backend has no MTLWinding for that GPU seam front face."),
        };

        /// <summary><c>MTLFormats.VdToMTLFillMode</c>. Total over <see cref="GpuPolygonFill"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A seam fill mode this map has not been taught.</exception>
        internal static MTLTriangleFillMode ToFillMode(GpuPolygonFill fill) => fill switch
        {
            GpuPolygonFill.Solid => MTLTriangleFillMode.Fill,
            GpuPolygonFill.Wireframe => MTLTriangleFillMode.Lines,
            _ => throw new ArgumentOutOfRangeException(nameof(fill), fill,
                "The native Metal backend has no MTLTriangleFillMode for that GPU seam fill mode."),
        };

        /// <summary><c>MTLFormats.VdToMTLCompareFunction</c>. Total over <see cref="GpuComparison"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A seam comparison this map has not been taught.</exception>
        internal static MTLCompareFunction ToCompareFunction(GpuComparison comparison) => comparison switch
        {
            GpuComparison.Never => MTLCompareFunction.Never,
            GpuComparison.Less => MTLCompareFunction.Less,
            GpuComparison.Equal => MTLCompareFunction.Equal,
            GpuComparison.LessEqual => MTLCompareFunction.LessEqual,
            GpuComparison.Greater => MTLCompareFunction.Greater,
            GpuComparison.NotEqual => MTLCompareFunction.NotEqual,
            GpuComparison.GreaterEqual => MTLCompareFunction.GreaterEqual,
            GpuComparison.Always => MTLCompareFunction.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison,
                "The native Metal backend has no MTLCompareFunction for that GPU seam comparison."),
        };

        /// <summary>
        /// <c>MTLFormats.VdToMTLBlendFactor</c>. Total over <see cref="GpuBlendFactor"/>, including the two
        /// constant-colour factors, which Metal spells <c>BlendColor</c> where the seam says
        /// <c>BlendFactor</c> and which read their value from the encoder's <c>-setBlendColor:</c> rather than
        /// from the pipeline.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A seam blend factor this map has not been taught.</exception>
        internal static MTLBlendFactor ToBlendFactor(GpuBlendFactor factor) => factor switch
        {
            GpuBlendFactor.Zero => MTLBlendFactor.Zero,
            GpuBlendFactor.One => MTLBlendFactor.One,
            GpuBlendFactor.SourceColor => MTLBlendFactor.SourceColor,
            GpuBlendFactor.InverseSourceColor => MTLBlendFactor.OneMinusSourceColor,
            GpuBlendFactor.SourceAlpha => MTLBlendFactor.SourceAlpha,
            GpuBlendFactor.InverseSourceAlpha => MTLBlendFactor.OneMinusSourceAlpha,
            GpuBlendFactor.DestinationColor => MTLBlendFactor.DestinationColor,
            GpuBlendFactor.InverseDestinationColor => MTLBlendFactor.OneMinusDestinationColor,
            GpuBlendFactor.DestinationAlpha => MTLBlendFactor.DestinationAlpha,
            GpuBlendFactor.InverseDestinationAlpha => MTLBlendFactor.OneMinusDestinationAlpha,
            GpuBlendFactor.BlendFactor => MTLBlendFactor.BlendColor,
            GpuBlendFactor.InverseBlendFactor => MTLBlendFactor.OneMinusBlendColor,
            _ => throw new ArgumentOutOfRangeException(nameof(factor), factor,
                "The native Metal backend has no MTLBlendFactor for that GPU seam blend factor."),
        };

        /// <summary><c>MTLFormats.VdToMTLBlendOp</c>. Total over <see cref="GpuBlendFunction"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A seam blend function this map has not been
        /// taught.</exception>
        internal static MTLBlendOperation ToBlendOperation(GpuBlendFunction function) => function switch
        {
            GpuBlendFunction.Add => MTLBlendOperation.Add,
            GpuBlendFunction.Subtract => MTLBlendOperation.Subtract,
            GpuBlendFunction.ReverseSubtract => MTLBlendOperation.ReverseSubtract,
            GpuBlendFunction.Minimum => MTLBlendOperation.Min,
            GpuBlendFunction.Maximum => MTLBlendOperation.Max,
            _ => throw new ArgumentOutOfRangeException(nameof(function), function,
                "The native Metal backend has no MTLBlendOperation for that GPU seam blend function."),
        };

        /// <summary>
        /// <c>MTLFormats.VdToMTLPrimitiveTopology</c>. Total over <see cref="GpuPrimitiveTopology"/>, and the
        /// resolved value is a DRAW argument on this API rather than pipeline state (see
        /// <see cref="MTLPrimitiveType"/>).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A seam topology this map has not been taught.</exception>
        internal static MTLPrimitiveType ToPrimitiveType(GpuPrimitiveTopology topology) => topology switch
        {
            GpuPrimitiveTopology.TriangleList => MTLPrimitiveType.Triangle,
            GpuPrimitiveTopology.TriangleStrip => MTLPrimitiveType.TriangleStrip,
            GpuPrimitiveTopology.LineList => MTLPrimitiveType.Line,
            GpuPrimitiveTopology.LineStrip => MTLPrimitiveType.LineStrip,
            GpuPrimitiveTopology.PointList => MTLPrimitiveType.Point,
            _ => throw new ArgumentOutOfRangeException(nameof(topology), topology,
                "The native Metal backend has no MTLPrimitiveType for that GPU seam topology."),
        };

        /// <summary><c>MTLFormats.VdToMTLAddressMode</c>. Total over <see cref="GpuSamplerAddress"/>, with the
        /// throw kept for a member added later rather than for a member that exists.</summary>
        internal static MTLSamplerAddressMode ToAddressMode(GpuSamplerAddress mode) => mode switch
        {
            GpuSamplerAddress.Wrap => MTLSamplerAddressMode.Repeat,
            GpuSamplerAddress.Mirror => MTLSamplerAddressMode.MirrorRepeat,
            GpuSamplerAddress.Clamp => MTLSamplerAddressMode.ClampToEdge,
            GpuSamplerAddress.Border => MTLSamplerAddressMode.ClampToBorderColor,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode,
                "The native Metal backend has no MTLSamplerAddressMode for that GPU seam address mode."),
        };

        /// <summary>
        /// <c>MTLFormats.GetMinMagMipFilter</c>, restricted to the three filters
        /// <see cref="GpuSamplerFilter"/> has.
        ///
        /// <para><b>ANISOTROPIC RESOLVES TO TRILINEAR HERE AND THE ANISOTROPY RIDES A SEPARATE PROPERTY</b>, which
        /// is the incumbent's own shape: its <c>SamplerFilter.Anisotropic</c> arm sets linear on all three and the
        /// <c>maxAnisotropy</c> field is what makes the sampler anisotropic. So this function answering the same
        /// triple for two different seam filters is correct rather than a collapsed case, and
        /// <see cref="MetalSamplerPolicy"/> is where the two stop being the same sampler.</para>
        ///
        /// <para><b>THE DEGRADATION BRANCH THE VULKAN SIBLING HAS IS UNREACHABLE HERE</b>, for the same reason it
        /// was unreachable on Direct3D 11. The engine's Veldrid path falls back from anisotropic to trilinear when
        /// the device reports no <c>SamplerAnisotropy</c>, and the incumbent's Metal device constructs its
        /// <c>GraphicsDeviceFeatures</c> with <c>samplerAnisotropy: true</c> unconditionally, so no Metal device
        /// takes that fallback. <see cref="MetalSamplerPolicy"/> carries the same statement where the capability is
        /// read.</para>
        /// </summary>
        internal static MetalFilterSelection ToFilterSelection(GpuSamplerFilter filter) => filter switch
        {
            GpuSamplerFilter.MinPointMagPointMipPoint => new MetalFilterSelection(
                MTLSamplerMinMagFilter.Nearest, MTLSamplerMinMagFilter.Nearest, MTLSamplerMipFilter.Nearest),
            GpuSamplerFilter.MinLinearMagLinearMipLinear => new MetalFilterSelection(
                MTLSamplerMinMagFilter.Linear, MTLSamplerMinMagFilter.Linear, MTLSamplerMipFilter.Linear),
            GpuSamplerFilter.Anisotropic => new MetalFilterSelection(
                MTLSamplerMinMagFilter.Linear, MTLSamplerMinMagFilter.Linear, MTLSamplerMipFilter.Linear),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter,
                "The native Metal backend has no filter selection for that GPU seam sampler filter."),
        };
    }
}
