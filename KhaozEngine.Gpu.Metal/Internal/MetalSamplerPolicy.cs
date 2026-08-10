using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE <c>MTLSamplerDescriptor</c>'s worth of decisions in plain values, so the whole sampler mapping is a
    /// pure function of the seam's description and is driven by <c>[Fact]</c>s with no device.
    /// </summary>
    /// <param name="AddressS">Addressing on S, which is the seam's U.</param>
    /// <param name="AddressT">Addressing on T, the seam's V.</param>
    /// <param name="AddressR">Addressing on R, the seam's W.</param>
    /// <param name="Filters">The min, mag and mip filters.</param>
    /// <param name="MaxAnisotropy">What reaches <c>-setMaxAnisotropy:</c>, already raised to at least 1.</param>
    /// <param name="BorderColor">The border colour, which is always transparent black.</param>
    /// <param name="LodMinClamp">Always 0.</param>
    /// <param name="LodMaxClamp">Always <c>uint.MaxValue</c> as a float.</param>
    internal readonly record struct MetalSamplerSpec(
        MTLSamplerAddressMode AddressS,
        MTLSamplerAddressMode AddressT,
        MTLSamplerAddressMode AddressR,
        MetalFilterSelection Filters,
        nuint MaxAnisotropy,
        MTLSamplerBorderColor BorderColor,
        float LodMinClamp,
        float LodMaxClamp);

    /// <summary>
    /// THE SAMPLER MAPPING, reproduced from what the engine's Veldrid path plus <c>Veldrid.MTL.MTLSampler</c>
    /// really create between them.
    ///
    /// <para><b>FOUR VALUES THE SEAM DOES NOT EXPOSE ARE HARDCODED, because the incumbent hardcodes them and the
    /// committed goldens were baked through them.</b> No comparison function (the shadow path does manual PCF and
    /// never asks for a comparison sampler), a minimum LOD of 0, a maximum LOD of <c>uint.MaxValue</c>, and a
    /// transparent-black border colour. All four come from the engine's own Veldrid path, which builds every
    /// sampler as <c>new SamplerDescription(u, v, w, filter, null, maxAniso, 0, uint.MaxValue, lodBias,
    /// SamplerBorderColor.TransparentBlack)</c>. Changing one would move pixels.</para>
    ///
    /// <para><b>THE INCUMBENT'S TWO CONDITIONALS ARE RESOLVED RATHER THAN REPRODUCED, and this is row 6's "both
    /// reachable conditionals".</b> <c>MTLSampler</c> writes the border colour only <c>if
    /// (gd.MetalFeatures.IsMacOS)</c>, which is TRUE on every machine this backend can run on, so that arm is
    /// always taken and the border colour is written unconditionally here. It writes the compare function only
    /// when the seam supplied a comparison kind, and the engine's Veldrid path passes <c>null</c> at its ONE call
    /// site, so that arm is NEVER taken and no compare function is written here at all. Carrying a branch whose
    /// condition is constant would be reproducing a branch instead of a behaviour, and it would be a branch no
    /// test could reach either way.</para>
    ///
    /// <para><b>THE ANISOTROPY DEGRADATION IS UNREACHABLE ON METAL, exactly as it was on Direct3D 11 and unlike
    /// on Vulkan.</b> The engine's Veldrid path falls back from anisotropic filtering to trilinear when the device
    /// reports no <c>SamplerAnisotropy</c>, and <c>MTLGraphicsDevice</c> constructs its
    /// <c>GraphicsDeviceFeatures</c> with <c>samplerAnisotropy: true</c> unconditionally, so no Metal device takes
    /// it. This backend's capability read answers true for the same reason and section 14 requires the two to
    /// agree. A branch nothing can reach is a branch nothing can test, so it is written down here instead of
    /// coded.</para>
    ///
    /// <para><b>THE LOD BIAS IS DROPPED, AND THAT IS THE ONE PLACE THE SEAM LOSES SOMETHING ON THIS BACKEND.</b>
    /// <see cref="GpuSamplerDescription.MipLodBias"/> has no <c>MTLSamplerDescriptor</c> field at all, which is
    /// why <c>GpuCapabilities.SamplerLodBias</c> is the one capability that differs from BOTH other native
    /// backends. The engine's Veldrid path already zeroes the bias on a device that reports no support, so a
    /// non-zero bias reaches neither implementation and the two agree by construction rather than by accident.
    /// The seam's own doc comment already says it is a no-op on Metal.</para>
    /// </summary>
    internal static class MetalSamplerPolicy
    {
        /// <summary>The minimum LOD every sampler on this backend is created with.</summary>
        internal const float MinLod = 0f;

        /// <summary>The maximum LOD every sampler on this backend is created with: <c>uint.MaxValue</c> converted
        /// to a float, which is the value the engine's Veldrid path passes and which arrives at
        /// <c>-setLodMaxClamp:</c> through the same widening there.</summary>
        internal const float MaxLod = uint.MaxValue;

        /// <summary>The spec for <paramref name="description"/>.</summary>
        internal static MetalSamplerSpec For(in GpuSamplerDescription description)
            => new(
                MetalFormats.ToAddressMode(description.AddressModeU),
                MetalFormats.ToAddressMode(description.AddressModeV),
                MetalFormats.ToAddressMode(description.AddressModeW),
                MetalFormats.ToFilterSelection(description.Filter),
                // RAISED TO AT LEAST 1, which is Math.Max(1, MaximumAnisotropy) in the incumbent. Metal rejects
                // zero, and the seam documents 0 as "keep the historical behaviour" rather than as a value.
                description.MaximumAnisotropy < 1 ? 1 : description.MaximumAnisotropy,
                MTLSamplerBorderColor.TransparentBlack,
                MinLod,
                MaxLod);
    }
}
