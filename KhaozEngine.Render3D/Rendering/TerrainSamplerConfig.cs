using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    /// <summary>How a splat/terrain material samples its tileable detail textures at a distance. The default
    /// (<see cref="Default"/>) is anisotropic 16x + a +1 mip LOD bias, tuned for grazing ground. Games can override
    /// it per material (via <c>TerrainLayeredMaterial.Sampler</c> or <c>Scene3D.LoadSplatMaterial</c>) to trade
    /// grazing sharpness for less distance aliasing: high-frequency tiling albedo can shimmer/"fuzz" as the camera
    /// moves because anisotropy keeps the grazing floor sharp, so lowering the anisotropy, switching to trilinear
    /// (<see cref="GpuSamplerFilter.MinLinearMagLinearMipLinear"/>), or raising the bias blurs the far ground.
    /// Addressing is always Wrap (the textures tile across the world). Byte-identical to the pre-existing behaviour
    /// when a material leaves the sampler unset.</summary>
    public readonly struct TerrainSamplerConfig
    {
        /// <summary>Minification filter. <see cref="GpuSamplerFilter.Anisotropic"/> (grazing detail),
        /// <see cref="GpuSamplerFilter.MinLinearMagLinearMipLinear"/> (trilinear, softer at grazing), or
        /// <see cref="GpuSamplerFilter.MinPointMagPointMipPoint"/> (point, no filtering).</summary>
        public readonly GpuSamplerFilter Filter;

        /// <summary>Max anisotropy (used only when <see cref="Filter"/> is <see cref="GpuSamplerFilter.Anisotropic"/>).
        /// Lower = less grazing sharpness = less distance aliasing. Ignored on devices without anisotropy support
        /// (the sampler falls back to trilinear there).</summary>
        public readonly uint MaximumAnisotropy;

        /// <summary>Whole-mip bias added to the computed LOD. Positive = sample a blurrier mip (less distance
        /// aliasing). A D3D11/Vulkan feature; Metal has no sampler LOD bias, so it is forced to 0 there.</summary>
        public readonly int MipLodBias;

        public TerrainSamplerConfig(GpuSamplerFilter filter, uint maximumAnisotropy, int mipLodBias)
        {
            Filter = filter;
            MaximumAnisotropy = maximumAnisotropy;
            MipLodBias = mipLodBias;
        }

        /// <summary>The engine's tuned default: anisotropic 16x + a +1 mip LOD bias. Matches the shared sampler used
        /// when a material sets no override, so a material with <c>Sampler = null</c> renders byte-identically.</summary>
        public static TerrainSamplerConfig Default => new(GpuSamplerFilter.Anisotropic, 16, 1);
    }
}
