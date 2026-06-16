namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Backend-dependent clip-space conventions the renderers need to build correct projection matrices.
    /// Read off the live device (Veldrid's <c>IsClipSpaceYInverted</c> / <c>IsDepthRangeZeroToOne</c>) and
    /// surfaced on <see cref="GpuDeviceContext.Capabilities"/> so the clip-Y / depth handling is derived from
    /// the active backend instead of a baked Metal assumption.
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

        public GpuCapabilities(bool clipSpaceYInverted, bool depthRangeZeroToOne)
        {
            ClipSpaceYInverted = clipSpaceYInverted;
            DepthRangeZeroToOne = depthRangeZeroToOne;
        }
    }
}
