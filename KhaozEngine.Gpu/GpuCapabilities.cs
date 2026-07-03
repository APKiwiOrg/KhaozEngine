namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Live-device facts the renderers (and diagnostics) need: clip-space conventions used to build correct
    /// projection matrices, plus the adapter name and the sampler feature flags. Read off the active backend
    /// (Veldrid's <c>GraphicsDevice</c> / <c>GraphicsDeviceFeatures</c>) and surfaced on
    /// <see cref="GpuDeviceContext.Capabilities"/> / <c>AppWindow.Capabilities</c>.
    /// </summary>
    public readonly struct GpuCapabilities
    {
        /// <summary>
        /// True if the backend's clip-space Y axis points down relative to the texture-space convention
        /// (i.e. a render to a texture appears vertically flipped unless compensated). Veldrid:
        /// <c>GraphicsDevice.IsClipSpaceYInverted</c>.
        /// </summary>
        public bool ClipSpaceYInverted { get; }

        /// <summary>
        /// True if the backend's normalized device depth range is [0, 1] (D3D/Metal/Vulkan style) rather than
        /// [-1, 1] (legacy GL). Veldrid: <c>GraphicsDevice.IsDepthRangeZeroToOne</c>.
        /// </summary>
        public bool DepthRangeZeroToOne { get; }

        /// <summary>The active GPU adapter / driver name (Veldrid <c>GraphicsDevice.DeviceName</c>); empty if the
        /// backend does not report one. Diagnostic (which physical GPU/driver is actually rendering).</summary>
        public string DeviceName { get; }

        /// <summary>True if the device supports anisotropic sampling (Veldrid <c>Features.SamplerAnisotropy</c>).
        /// When false, an anisotropic sampler silently falls back to trilinear.</summary>
        public bool SamplerAnisotropy { get; }

        /// <summary>True if the device supports a sampler mip LOD bias (Veldrid <c>Features.SamplerLodBias</c>).
        /// When false, a requested <c>MipLodBias</c> is silently forced to 0 (e.g. Metal has no LOD bias).</summary>
        public bool SamplerLodBias { get; }

        public GpuCapabilities(bool clipSpaceYInverted, bool depthRangeZeroToOne,
            string deviceName = "", bool samplerAnisotropy = false, bool samplerLodBias = false)
        {
            ClipSpaceYInverted = clipSpaceYInverted;
            DepthRangeZeroToOne = depthRangeZeroToOne;
            DeviceName = deviceName ?? "";
            SamplerAnisotropy = samplerAnisotropy;
            SamplerLodBias = samplerLodBias;
        }
    }
}
