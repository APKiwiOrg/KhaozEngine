namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE TWO DESCRIPTIONS THE DEVICE'S SHARED SAMPLER PAIR IS BUILT FROM, and the one place their address mode is
    /// decided. Both are WRAP on all three axes, because the seam's shared pair is contractually wrap and the
    /// incumbent's pair is wrap. Section 14.
    ///
    /// <para><b>THESE MIRROR THE INCUMBENT'S BUILT-INS, NOT THEIR ENGINE NAMESAKES.</b> The incumbent wrapped
    /// Veldrid's own <c>GraphicsDevice.PointSampler</c> and <c>LinearSampler</c>, which come from
    /// <c>Veldrid.SamplerDescription.Point</c> and <c>.Linear</c>, and both of those are documented and built as
    /// <c>SamplerAddressMode.Wrap</c> on U, V and W. The engine's own <see cref="GpuSamplerDescription.Point"/> and
    /// <see cref="GpuSamplerDescription.Linear"/> statics carry the SAME NAMES and the OPPOSITE address mode: their
    /// constructor defaults every axis to <see cref="GpuSamplerAddress.Clamp"/>, and that is documented public API
    /// which is not changed here, because callers that ask for those statics by name asked for a clamped
    /// sampler.</para>
    ///
    /// <para><b>THE NAME COLLISION IS WHAT SHIPPED THE DEFECT ON THE OTHER NATIVE BACKEND, AND THE SAME MISTAKE IS
    /// AVAILABLE HERE.</b> That device built its shared pair from the engine statics, so every renderer that
    /// assumes the shared linear sampler wraps (<c>ModelRenderer</c> says so in writing) got clamping on the native
    /// backend alone. It renders wrong and throws nothing, so only a golden sees it: CI run 30963173087 came back
    /// with <c>scene3d_texbillboard</c> at a worst delta of 0.393 and <c>scene3d_particles_flipbook</c> at 0.359,
    /// both scenes whose sampling leaves [0,1] by design. The design document's section 14 spends a paragraph on
    /// this for one reason, which is that reading the address mode off the statics because the names matched is the
    /// cheapest mistake in the backend to make and the most expensive to find.</para>
    ///
    /// <para>Everything else is the constructor default and deliberately so: no anisotropy and no LOD bias,
    /// matching the incumbent's built-ins. The four values the seam does not expose at all (comparison function,
    /// minimum LOD, maximum LOD, border colour) are decided one level down in
    /// <see cref="VulkanSamplerPolicy"/>.</para>
    ///
    /// <para><b>THE PAIR IS DEVICE-OWNED AND A CONSUMER CANNOT DESTROY IT.</b> The wrappers the device hands out
    /// for these two do not own their <c>VkSampler</c>, so a consumer that disposes one it got from
    /// <c>IGpuDevice.PointSampler</c> destroys nothing, which is the same rule the Direct3D 11 backend applies to
    /// its shared pair.</para>
    /// </summary>
    internal static class VulkanSharedSamplers
    {
        /// <summary>The device's shared POINT sampler description: nearest on min, mag and mip, WRAP on all three
        /// axes. Mirrors <c>Veldrid.SamplerDescription.Point</c>.</summary>
        internal static GpuSamplerDescription Point => new(
            GpuSamplerFilter.MinPointMagPointMipPoint,
            GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap);

        /// <summary>The device's shared LINEAR sampler description: bilinear on min, mag and mip, WRAP on all three
        /// axes. Mirrors <c>Veldrid.SamplerDescription.Linear</c>.</summary>
        internal static GpuSamplerDescription Linear => new(
            GpuSamplerFilter.MinLinearMagLinearMipLinear,
            GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap);
    }
}
