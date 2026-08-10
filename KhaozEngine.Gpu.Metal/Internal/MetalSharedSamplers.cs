namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE TWO DESCRIPTIONS THE DEVICE'S SHARED SAMPLER PAIR IS BUILT FROM, and the one place their address mode
    /// is decided. Both are WRAP on all three axes, because the seam's shared pair is contractually wrap and the
    /// incumbent's pair is wrap.
    ///
    /// <para><b>THESE MIRROR THE INCUMBENT'S BUILT-INS, NOT THEIR ENGINE NAMESAKES.</b> The incumbent wraps
    /// Veldrid's own <c>GraphicsDevice.PointSampler</c> and <c>LinearSampler</c>, which come from
    /// <c>Veldrid.SamplerDescription.Point</c> and <c>.Linear</c>, and both are built as
    /// <c>SamplerAddressMode.Wrap</c> on U, V and W. The engine's own <see cref="GpuSamplerDescription.Point"/>
    /// and <see cref="GpuSamplerDescription.Linear"/> statics carry the SAME NAMES and the OPPOSITE address mode:
    /// their constructor defaults every axis to <see cref="GpuSamplerAddress.Clamp"/>, and that is documented
    /// public API which is not changed here, because callers that ask for those statics by name asked for a
    /// clamped sampler.</para>
    ///
    /// <para><b>THE NAME COLLISION IS WHAT SHIPPED THE DEFECT ON THE DIRECT3D 11 LEG, and this type exists on the
    /// third backend so it cannot recur a third time.</b> That device built its shared pair from the engine
    /// statics, so every renderer that assumes the shared linear sampler wraps (<c>ModelRenderer</c> says so in
    /// writing) got clamping on the native backend alone. It renders wrong and throws nothing, so only a golden
    /// sees it: <c>scene3d_texbillboard</c> came back at a worst delta of 0.393 and
    /// <c>scene3d_particles_flipbook</c> at 0.359, both scenes whose sampling leaves [0,1] by design. Section 18
    /// names it as this row's regression evidence for exactly that reason.</para>
    ///
    /// <para>Everything else is the constructor default and deliberately so: no anisotropy and no LOD bias,
    /// matching the incumbent's built-ins. The four values the seam does not expose at all (comparison function,
    /// minimum LOD, maximum LOD, border colour) are hardcoded one level down in
    /// <see cref="MetalSamplerPolicy"/>.</para>
    /// </summary>
    internal static class MetalSharedSamplers
    {
        /// <summary>The device's shared POINT sampler description: nearest on min, mag and mip, wrap on all three
        /// axes. Mirrors <c>Veldrid.SamplerDescription.Point</c>.</summary>
        internal static GpuSamplerDescription Point => new(
            GpuSamplerFilter.MinPointMagPointMipPoint,
            GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap);

        /// <summary>The device's shared LINEAR sampler description: bilinear on min, mag and mip, wrap on all
        /// three axes. Mirrors <c>Veldrid.SamplerDescription.Linear</c>.</summary>
        internal static GpuSamplerDescription Linear => new(
            GpuSamplerFilter.MinLinearMagLinearMipLinear,
            GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap);
    }
}
