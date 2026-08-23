namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE <c>VkSamplerCreateInfo</c>'s worth of decisions, in plain values, so the whole sampler mapping is a pure
    /// function of the seam's description plus one device feature and is driven by <c>[Fact]</c>s with no loader.
    /// </summary>
    /// <param name="AddressU">Addressing on U.</param>
    /// <param name="AddressV">Addressing on V.</param>
    /// <param name="AddressW">Addressing on W.</param>
    /// <param name="Filter">The filter, AFTER the anisotropy degradation below has been applied, so the value here
    /// is what the sampler is really created with.</param>
    /// <param name="AnisotropyEnable"><c>VkSamplerCreateInfo.anisotropyEnable</c>.</param>
    /// <param name="MaxAnisotropy"><c>maxAnisotropy</c>, 0 when anisotropy is off.</param>
    /// <param name="MinLod">Always 0, which is the value the engine's Veldrid path hardcodes.</param>
    /// <param name="MaxLod">Always <c>uint.MaxValue</c> as a float, which is the value the engine's Veldrid path
    /// hardcodes and which every implementation clamps to the real chain.</param>
    /// <param name="MipLodBias">The seam's whole-mip-level bias.</param>
    internal readonly record struct VulkanSamplerSpec(
        GpuSamplerAddress AddressU,
        GpuSamplerAddress AddressV,
        GpuSamplerAddress AddressW,
        GpuSamplerFilter Filter,
        bool AnisotropyEnable,
        float MaxAnisotropy,
        float MinLod,
        float MaxLod,
        float MipLodBias);

    /// <summary>
    /// THE SAMPLER MAPPING, reproduced exactly from what the incumbent created before it was deleted in 18.0.0.
    /// Section 14's last paragraph.
    ///
    /// <para><b>FOUR VALUES THE SEAM DOES NOT EXPOSE ARE HARDCODED, and they are hardcoded because the incumbent
    /// hardcoded them and the committed goldens were baked through them.</b> No comparison function (the shadow
    /// path does manual PCF and never asks for a comparison sampler, so <c>compareEnable</c> is false and
    /// <c>compareOp</c> is <c>NEVER</c>), a minimum LOD of 0, a maximum LOD of <c>uint.MaxValue</c>, and a
    /// transparent-black border colour. All four came from the engine's own Veldrid path, which built every
    /// sampler as <c>new SamplerDescription(u, v, w, filter, null, maxAniso, 0, uint.MaxValue, lodBias,
    /// SamplerBorderColor.TransparentBlack)</c>. Changing one would move pixels.</para>
    ///
    /// <para><b>THE ANISOTROPY DEGRADATION IS REPRODUCED, unlike on the Direct3D 11 backend where it was
    /// unreachable.</b> The engine's Veldrid path fell back from anisotropic filtering to trilinear when the
    /// device reported no <c>samplerAnisotropy</c>, and dropped the maximum anisotropy to 0 with it. Every Direct3D 11
    /// device has the feature, so that backend dropped the branch as dead code. A VULKAN device may genuinely lack
    /// it, lavapipe being the case that matters most here because it is the rasterizer the golden gate runs on, and
    /// asking for <c>anisotropyEnable</c> without the feature is
    /// <c>VUID-VkSamplerCreateInfo-anisotropyEnable-01070</c> rather than a slow path. So the branch is live, it
    /// reads the SAME capability the Veldrid path read, and the two agreed by construction.</para>
    ///
    /// <para><b>THE LOD-BIAS DEGRADATION IS NOT REPRODUCED, and that one really is unreachable.</b> The Veldrid
    /// path dropped a non-zero bias when the device reported no <c>SamplerLodBias</c>, which existed because
    /// Metal's sampler has no bias at all. <c>mipLodBias</c> is core Vulkan with no feature bit in front of it,
    /// this backend's capability read answers true unconditionally, and section 14 required that answer to be
    /// identical to the incumbent's (it was: <c>VeldridMap</c> answered true for Vulkan too). A branch that can
    /// never be taken would be a branch nothing can test.</para>
    /// </summary>
    internal static class VulkanSamplerPolicy
    {
        /// <summary>The minimum LOD every sampler on this backend is created with.</summary>
        internal const float MinLod = 0f;

        /// <summary>The maximum LOD every sampler on this backend is created with:
        /// <c>uint.MaxValue</c> converted to a float, which is the value the engine's Veldrid path passes and which
        /// arrives at <c>VkSamplerCreateInfo.maxLod</c> through the same widening there.</summary>
        internal const float MaxLod = uint.MaxValue;

        /// <summary>
        /// The spec for <paramref name="description"/> on a device whose <c>samplerAnisotropy</c> feature is
        /// <paramref name="deviceSamplerAnisotropy"/>.
        /// </summary>
        internal static VulkanSamplerSpec For(in GpuSamplerDescription description, bool deviceSamplerAnisotropy)
        {
            GpuSamplerFilter filter = description.Filter;
            uint maxAnisotropy = description.MaximumAnisotropy;

            // THE DEGRADATION, in the same direction and with the same two assignments the engine's Veldrid path
            // makes. See the class note for why this branch is live here and dead on the other native backend.
            if (filter == GpuSamplerFilter.Anisotropic && !deviceSamplerAnisotropy)
            {
                filter = GpuSamplerFilter.MinLinearMagLinearMipLinear;
                maxAnisotropy = 0;
            }

            bool anisotropic = filter == GpuSamplerFilter.Anisotropic;

            return new VulkanSamplerSpec(
                description.AddressModeU,
                description.AddressModeV,
                description.AddressModeW,
                filter,
                anisotropic,
                anisotropic ? maxAnisotropy : 0f,
                MinLod,
                MaxLod,
                description.MipLodBias);
        }
    }
}
