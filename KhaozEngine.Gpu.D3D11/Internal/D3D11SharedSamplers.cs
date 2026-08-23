namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE TWO DESCRIPTIONS THE DEVICE'S SHARED SAMPLER PAIR IS BUILT FROM, and the one place their address mode
    /// is decided. Both are WRAP on all three axes, because the seam's shared pair is contractually wrap and the
    /// incumbent's pair was wrap.
    /// <para>
    /// <b>THESE MIRROR THE INCUMBENT'S BUILT-INS, NOT THEIR ENGINE NAMESAKES.</b> The incumbent wrapped
    /// Veldrid's own <c>GraphicsDevice.PointSampler</c> and <c>LinearSampler</c>, which came from
    /// <c>Veldrid.SamplerDescription.Point</c> and <c>.Linear</c>, and both of those were documented and built as
    /// <c>SamplerAddressMode.Wrap</c> on U, V and W. The engine's own
    /// <see cref="GpuSamplerDescription.Point"/> and <see cref="GpuSamplerDescription.Linear"/> statics carry the
    /// SAME NAMES and the OPPOSITE address mode: their ctor defaults every axis to
    /// <see cref="GpuSamplerAddress.Clamp"/>, and that is documented public API which is not changed here, because
    /// callers that ask for those statics by name asked for a clamped sampler.
    /// </para>
    /// <para>
    /// <b>THE NAME COLLISION IS WHAT SHIPPED THE DEFECT, and this type exists so it cannot recur.</b> The device
    /// built its shared pair from the engine statics, so every renderer that assumes the shared linear sampler
    /// wraps (<c>ModelRenderer</c> says so in writing) got clamping on the native backend alone. It renders wrong
    /// and throws nothing, so only a golden sees it: CI run 30963173087 came back with
    /// <c>scene3d_texbillboard</c> at a worst delta of 0.393 and <c>scene3d_particles_flipbook</c> at 0.359, both
    /// scenes whose sampling leaves [0,1] by design (a two-texel-wide billboard texture whose edge fringe is half
    /// the quad, and the flipbook motion-vector warp that walks a tap out of the atlas).
    /// </para>
    /// <para>
    /// Everything else is the ctor default and deliberately so: no anisotropy and no LOD bias, matching the
    /// incumbent's built-ins byte for byte. The four values the seam does not expose at all (comparison function,
    /// minimum LOD, maximum LOD, border colour) are hardcoded one level down in <see cref="D3D11Sampler"/>, per
    /// decision G1. <c>NativeVsVeldridCapabilityParityTests</c> compared this pair against
    /// <c>Veldrid.SamplerDescription.Point</c> and <c>.Linear</c> field by field, on every OS, so a drift was a
    /// device-free failure rather than a golden one. That test went away with the incumbent in 18.0.0, so a drift
    /// is a golden failure again, which is the failure mode the paragraph above describes.
    /// </para>
    /// </summary>
    internal static class D3D11SharedSamplers
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
