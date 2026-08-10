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
    /// THE SAMPLER-STATE DOMAIN of the format map (see <c>MetalFormats.Pixel.cs</c> for the split and its
    /// reason). Addressing and filtering, both reproduced from <c>Veldrid.MTL.MTLFormats</c>.
    /// </summary>
    internal static partial class MetalFormats
    {
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
