namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Live-device facts the renderers (and diagnostics) need: clip-space conventions used to build correct
    /// projection matrices, plus the adapter name and the sampler feature flags. Read off the active backend's
    /// own capability read (<c>MetalCapabilityRead</c>, <c>D3D11CapabilityRead</c>, <c>VulkanCapabilityRead</c>)
    /// and surfaced on
    /// <see cref="GpuDeviceContext.Capabilities"/> / <c>AppWindow.Capabilities</c>.
    /// </summary>
    public readonly struct GpuCapabilities
    {
        /// <summary>
        /// True if the backend's clip-space Y axis points down relative to the texture-space convention
        /// (i.e. a render to a texture appears vertically flipped unless compensated). Until 18.0.0 it came from
        /// Veldrid <c>GraphicsDevice.IsClipSpaceYInverted</c>.
        /// </summary>
        public bool ClipSpaceYInverted { get; }

        /// <summary>
        /// True if the backend's normalized device depth range is [0, 1] (D3D/Metal/Vulkan style) rather than
        /// [-1, 1] (legacy GL). Until 18.0.0 it came from Veldrid <c>GraphicsDevice.IsDepthRangeZeroToOne</c>.
        /// </summary>
        public bool DepthRangeZeroToOne { get; }

        /// <summary>The active GPU adapter / driver name (until 18.0.0, Veldrid
        /// <c>GraphicsDevice.DeviceName</c>); empty if the
        /// backend does not report one. Diagnostic (which physical GPU/driver is actually rendering).</summary>
        public string DeviceName { get; }

        /// <summary>True if the device supports anisotropic sampling (until 18.0.0, Veldrid
        /// <c>Features.SamplerAnisotropy</c>).
        /// When false, an anisotropic sampler silently falls back to trilinear.</summary>
        public bool SamplerAnisotropy { get; }

        /// <summary>True if the device supports a sampler mip LOD bias (until 18.0.0, Veldrid
        /// <c>Features.SamplerLodBias</c>).
        /// When false, a requested <c>MipLodBias</c> is silently forced to 0 (e.g. Metal has no LOD bias).</summary>
        public bool SamplerLodBias { get; }

        /// <summary>The largest member of <see cref="SupportedMsaaSampleCounts"/>. Kept for source compatibility
        /// and quick diagnostics. A menu or allocator must consult the full set because a backend may have holes.
        /// </summary>
        public int MaxMsaaSampleCount => _maxMsaaSampleCount < 1 ? 1 : _maxMsaaSampleCount;
        readonly int _maxMsaaSampleCount;

        /// <summary>
        /// Every sample count supported across the engine's multisampled render targets. Unlike the maximum alone,
        /// this preserves holes in a backend's support mask.
        /// </summary>
        public GpuSampleCounts SupportedMsaaSampleCounts =>
            GpuSampleCountSet.Normalize(_supportedMsaaSampleCounts);
        readonly GpuSampleCounts _supportedMsaaSampleCounts;

        /// <summary>True if the device can drive the directional shadow-map path: it can render depth into an
        /// R32_Float target and SAMPLE that target in a shader (the manual-PCF depth-compare the shadow map uses).
        /// Every currently-supported backend (Metal / D3D11 / Vulkan) reports true; the flag exists so
        /// <c>ShadowSettings.ResolveFor</c> in KhaozEngine.Render3D can degrade <c>ShadowMode.ShadowMap</c> to
        /// <c>Blob</c> (never crash) on a hypothetical device that lacks it, mirroring the MSAA clamp. Each native
        /// backend answers it from its own per-format read (<c>MetalCapabilityRead</c>, <c>D3D11CapabilityRead</c>,
        /// <c>VulkanCapabilityRead</c>). Until 18.0.0 it came from Veldrid's per-format usage support
        /// (<c>VeldridMap.SupportsShadowMaps</c>).</summary>
        public bool SupportsShadowMaps { get; }

        /// <summary>True if the device can run compute shaders (until 18.0.0, Veldrid
        /// <c>Features.ComputeShader</c>). Metal,
        /// Vulkan and Direct3D11 all report true; an OpenGL / GLES device below the compute-capable version does
        /// not. Gate any compute path on this and degrade to a non-compute fallback rather than crashing:
        /// <c>CreateComputeShaderFromSpirv</c> / <c>CreateComputePipeline</c> throw on a device without it.</summary>
        public bool SupportsCompute { get; }

        /// <summary>True if a fence handed to <see cref="IGpuDevice.Submit(IGpuCommandList,IGpuFence)"/> is signaled
        /// by GPU COMPLETION rather than by the CPU-side submit call returning. Only a fence with that property can
        /// stand in for <see cref="IGpuDevice.WaitForIdle"/>, which is what deferred GPU-resource destruction needs.
        /// <para>All three live backends report true: the Vulkan fence is signaled from <c>vkQueueSubmit</c> itself,
        /// the Metal one from the command buffer's completion handler, and the native Direct3D11 one off a real
        /// timeline at the end of replay (<c>D3D11FenceSubsystem</c>). The Veldrid Direct3D11 incumbent reported
        /// FALSE, and not because the fence was missing: its D3D11 fence was a <c>ManualResetEvent</c> set on the
        /// CPU immediately after <c>ExecuteCommandList</c> returned, so it said nothing at all about what the GPU
        /// had finished. OpenGL reported false for the same reason (its command executor signaled off the submit
        /// thread). An unrecognized
        /// backend reports false, so the safe answer is the default rather than something a new backend has to
        /// remember to opt out of.</para>
        /// <para>False does not mean "unsafe", it means a caller that would have polled a fence must keep whatever
        /// it did before (for the retired-resource pool that is a frame-count delay behind one
        /// <see cref="IGpuDevice.WaitForIdle"/>).</para></summary>
        public bool SupportsCompletionFences { get; }

        public GpuCapabilities(bool clipSpaceYInverted, bool depthRangeZeroToOne,
            string deviceName = "", bool samplerAnisotropy = false, bool samplerLodBias = false,
            int maxMsaaSampleCount = 1, bool supportsShadowMaps = true, bool supportsCompute = false,
            bool supportsCompletionFences = false)
        {
            SupportsCompletionFences = supportsCompletionFences;
            ClipSpaceYInverted = clipSpaceYInverted;
            DepthRangeZeroToOne = depthRangeZeroToOne;
            DeviceName = deviceName ?? "";
            SamplerAnisotropy = samplerAnisotropy;
            SamplerLodBias = samplerLodBias;
            _maxMsaaSampleCount = maxMsaaSampleCount < 1 ? 1 : maxMsaaSampleCount;
            _supportedMsaaSampleCounts = GpuSampleCountSet.ContiguousThrough(_maxMsaaSampleCount);
            SupportsShadowMaps = supportsShadowMaps;
            SupportsCompute = supportsCompute;
        }

        /// <summary>
        /// Build capabilities from the complete supported sample-count set. This factory leaves the existing
        /// constructor source-compatible while allowing a backend to report a sparse mask.
        /// </summary>
        public static GpuCapabilities FromSupportedMsaaSampleCounts(
            bool clipSpaceYInverted, bool depthRangeZeroToOne, GpuSampleCounts supportedMsaaSampleCounts,
            string deviceName = "", bool samplerAnisotropy = false, bool samplerLodBias = false,
            bool supportsShadowMaps = true, bool supportsCompute = false,
            bool supportsCompletionFences = false)
        {
            GpuSampleCounts normalized = GpuSampleCountSet.Normalize(supportedMsaaSampleCounts);
            return new GpuCapabilities(
                clipSpaceYInverted, depthRangeZeroToOne, deviceName, samplerAnisotropy, samplerLodBias,
                GpuSampleCountSet.HighestAtMost(normalized, int.MaxValue), supportsShadowMaps, supportsCompute,
                supportsCompletionFences, normalized);
        }

        GpuCapabilities(bool clipSpaceYInverted, bool depthRangeZeroToOne, string deviceName,
            bool samplerAnisotropy, bool samplerLodBias, int maxMsaaSampleCount, bool supportsShadowMaps,
            bool supportsCompute, bool supportsCompletionFences, GpuSampleCounts supportedMsaaSampleCounts)
        {
            SupportsCompletionFences = supportsCompletionFences;
            ClipSpaceYInverted = clipSpaceYInverted;
            DepthRangeZeroToOne = depthRangeZeroToOne;
            DeviceName = deviceName ?? "";
            SamplerAnisotropy = samplerAnisotropy;
            SamplerLodBias = samplerLodBias;
            _maxMsaaSampleCount = maxMsaaSampleCount;
            _supportedMsaaSampleCounts = supportedMsaaSampleCounts;
            SupportsShadowMaps = supportsShadowMaps;
            SupportsCompute = supportsCompute;
        }

        /// <summary>Whether <paramref name="sampleCount"/> is one of the device's supported counts.</summary>
        public bool SupportsMsaaSampleCount(int sampleCount)
            => GpuSampleCountSet.Contains(SupportedMsaaSampleCounts, sampleCount);

        /// <summary>The largest supported count no greater than <paramref name="maximum"/>, with 1 as the floor.</summary>
        public int HighestSupportedMsaaSampleCountAtMost(int maximum)
            => GpuSampleCountSet.HighestAtMost(SupportedMsaaSampleCounts, maximum);
    }
}
