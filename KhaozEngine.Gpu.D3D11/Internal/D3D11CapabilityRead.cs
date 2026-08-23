using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// DECISION G1's CAPABILITY ASSEMBLY, WITH NO DEVICE IN IT. Section 11 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c> carries a field-by-field table saying where
    /// each <see cref="GpuCapabilities"/> member comes from on this backend, and five of the nine members are
    /// CONSTANTS of the feature levels this backend requires rather than answers a device gives. Those five, the
    /// fold that turns three per-format sample-count answers into one number, the adapter-name NUL cut and the
    /// out-of-range sample-count guard are all here, so every rule that decides what the engine believes about
    /// the device is a plain <c>[Fact]</c> on macOS and Linux.
    /// <para>
    /// THE FOUR ANSWERS A DEVICE ACTUALLY GIVES are the adapter name, the three multisample queries behind
    /// <see cref="MinOverFormats"/>, and the R32_FLOAT format-support query. <c>D3D11DxgiQueries</c> asks for
    /// those behind the Windows boundary and hands them here, which is the whole split: the interop is four
    /// queries and everything downstream of them is engine logic.
    /// </para>
    /// <para>
    /// PARITY WITH THE INCUMBENT WAS THE POINT, and exactly one member was allowed to differ. What
    /// <c>KhaozEngine.Gpu.Internal.VeldridMap.ReadCapabilities</c> (deleted in 18.0.0) answered was the ground
    /// truth this read was matched against member for member, with
    /// <see cref="GpuCapabilities.SupportsCompletionFences"/> as the ONE permitted difference (decision C5 makes
    /// it true here and Veldrid's Direct3D11 fence was a CPU-side submit receipt, so it was false there).
    /// <c>NativeVsVeldridCapabilityParityTests</c> asserted that, and the reason the assertion mattered most for
    /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/> is that a different answer silently changes what
    /// <c>AntiAliasing.ResolveFor</c> picks, which changes the field look and the golden output. Both went away
    /// in 18.0.0, so what the reads below stand on now is the reasoning recorded with each one.
    /// </para>
    /// </summary>
    internal static class D3D11CapabilityRead
    {
        /// <summary>Direct3D 11 renders into a clip space whose Y axis matches the texture-space convention, so
        /// nothing anywhere has to compensate. <c>GpuClip.Correct</c> negates clip-space Y only when this is set,
        /// which is why decision G1 says it needs no change.</summary>
        internal const bool ClipSpaceYInverted = false;

        /// <summary>Direct3D normalized device depth is [0, 1], not the legacy GL [-1, 1].</summary>
        internal const bool DepthRangeZeroToOne = true;

        /// <summary>Every Direct3D 11 device has anisotropic sampling. It is a feature-level 9_1 guarantee, so
        /// there is no device to ask and no fallback to write. This is one half of why the incumbent's
        /// anisotropic-to-trilinear sampler degradation is DROPPED rather than reproduced (see
        /// <see cref="D3D11Sampler"/>).</summary>
        internal const bool SamplerAnisotropy = true;

        /// <summary>Every Direct3D 11 device honours a sampler mip LOD bias. The other half of the dropped
        /// degradation: the incumbent forces <c>MipLodBias</c> to 0 when this is false, which on this backend is
        /// a branch nothing can enter.</summary>
        internal const bool SamplerLodBias = true;

        /// <summary>Compute shaders are a feature-level 11_0 guarantee, and the probe already refuses any machine
        /// that cannot give this backend an 11_0 device (see <see cref="D3D11FeatureProbe"/>).</summary>
        internal const bool SupportsCompute = true;

        /// <summary>One sample, which is what "no MSAA" is spelled as everywhere in the seam. Also the answer any
        /// failed query folds to, so a device that will not answer degrades to the safe value rather than to a
        /// count nothing supports.</summary>
        internal const int NoMultisampling = 1;

        /// <summary>The highest sample count Direct3D 11 defines, and therefore the top of the descending walk in
        /// <see cref="HighestSupportedSampleCount"/>. Matches the ceiling Veldrid's <c>GetSampleCountLimit</c>
        /// reports through <c>TextureSampleCount.Count32</c>, which is what keeps the two answers comparable.
        /// </summary>
        internal const int MaxQueriedSampleCount = 32;

        /// <summary>
        /// THE ADAPTER NAME, READ AS A C STRING. <c>DXGI_ADAPTER_DESC::Description</c> is a fixed 128-wide-char
        /// buffer, so cutting at the first NUL is what "trailing nulls trimmed" means for it. The cut is
        /// DEFENSIVE and is expected to find nothing: the Vortice marshaller already stops at the first NUL on
        /// the native path, and did on the incumbent's, so the string that arrives here normally has no terminator
        /// left in it. It stays because a marshalling change that stopped doing that would otherwise put 100-odd
        /// NULs into the session header.
        /// <para>
        /// NO WHITESPACE TRIM, AND THAT IS A PARITY DECISION RATHER THAN AN OVERSIGHT. The padding observation is
        /// real: at least one vendor pads its description with a space, so a trim would produce a tidier name.
        /// The incumbent did not trim (<c>D3D11GraphicsDevice</c> assigned <c>desc.Description</c> to its device
        /// name raw), and <see cref="GpuCapabilities.DeviceName"/> was compared string for string by
        /// <c>NativeVsVeldridCapabilityParityTests</c>, so trimming on one path alone converted a cosmetic
        /// improvement into a parity failure on every machine whose vendor pads. Parity with the incumbent
        /// outranked cosmetics here. Decision T4's assertion WAS that parity test, so it went away with the
        /// incumbent in 18.0.0: a trim is a live option again, on the one path left, with no assertion to change
        /// alongside it and nothing left to catch one that goes wrong.
        /// </para>
        /// <para>
        /// Null and a description whose first character is the terminator both come back as the empty string,
        /// which is the same "the backend reported no adapter name" the seam already documents on
        /// <see cref="GpuCapabilities.DeviceName"/> and which <c>GpuDeviceContext.LogAdapter</c> already renders.
        /// </para>
        /// </summary>
        internal static string TrimAdapterName(string? description)
        {
            if (string.IsNullOrEmpty(description)) return string.Empty;

            int terminator = description.IndexOf('\0', StringComparison.Ordinal);
            return terminator >= 0 ? description.Substring(0, terminator) : description;
        }

        /// <summary>
        /// THE HIGHEST SAMPLE COUNT ONE FORMAT SUPPORTS, walked downward from
        /// <see cref="MaxQueriedSampleCount"/>. <paramref name="qualityLevelsFor"/> is one
        /// <c>CheckMultisampleQualityLevels</c> call for a given count, and Direct3D answers zero quality levels
        /// for a count it does not support, so the first non-zero answer walking down is the limit.
        /// <para>
        /// DOWNWARD RATHER THAN UPWARD, because the counts are not required to be contiguous. Direct3D 11
        /// guarantees 4x on every render-target format and says nothing about 8x, and a driver that supports 4x
        /// and 16x but not 8x would stop an upward walk at 4. Walking down takes the true maximum in every case
        /// and costs the same five calls.
        /// </para>
        /// <para>
        /// A DELEGATE RATHER THAN A DEVICE is what puts this loop under a plain <c>[Fact]</c>. The Windows caller
        /// hands in a lambda over its real <c>ID3D11Device</c>, and a test hands in a table.
        /// </para>
        /// </summary>
        internal static int HighestSupportedSampleCount(Func<int, int> qualityLevelsFor)
        {
            ArgumentNullException.ThrowIfNull(qualityLevelsFor);

            for (int count = MaxQueriedSampleCount; count > NoMultisampling; count >>= 1)
            {
                if (qualityLevelsFor(count) > 0) return count;
            }
            return NoMultisampling;
        }

        /// <summary>
        /// DECISION C4's FOLD: the MIN over the three formats the 3D scene's MRT renders into, because every
        /// attachment of a framebuffer must support the count the framebuffer is created at. The colour target is
        /// <c>R8G8B8A8_UNORM</c>, the linear-depth target is <c>R32_FLOAT</c> and the depth-stencil target is the
        /// engine's <c>D32_FLOAT_S8X24_UINT</c>, which is the same three
        /// <c>KhaozEngine.Gpu.Internal.VeldridMap.MaxMsaaSampleCount</c> folded over, in the same order.
        /// <para>
        /// THE DEPTH ATTACHMENT WAS ASKED ABOUT AS <c>R32G8X24_TYPELESS</c> ON BOTH PATHS, which is what made
        /// "comparable by construction" true rather than nearly true. The incumbent asked for the sample-count
        /// limit of <c>PixelFormat.D32_Float_S8_UInt</c> with its depth flag set, and Veldrid's
        /// <c>D3D11Formats.ToDxgiFormat</c> mapped that pair to <c>Format.R32G8X24_Typeless</c> BEFORE the
        /// <c>CheckMultisampleQualityLevels</c> call, so the DXGI format the driver was handed was the typeless
        /// sibling and not the fully typed one. <see cref="D3D11DxgiQueries"/> passes that same typeless format
        /// for exactly this reason, and the note is here because this fold is where the parity claim lived.
        /// </para>
        /// <para>
        /// Anything at or below zero folds to <see cref="NoMultisampling"/>, which is how a failed query reaches
        /// here: the Windows caller answers 1 for a query that threw, and this keeps a negative or zero from any
        /// other source from becoming a sample count the seam would then hand to Direct3D.
        /// </para>
        /// </summary>
        internal static int MinOverFormats(int colour, int linearDepth, int depthStencil)
        {
            int lowest = Math.Min(colour, Math.Min(linearDepth, depthStencil));
            return lowest < NoMultisampling ? NoMultisampling : lowest;
        }

        /// <summary>
        /// THE WHOLE CAPABILITY SET, assembled from the four facts a device supplies plus the five constants
        /// above. This is the single source both <c>GpuDeviceContext.Capabilities</c> and
        /// <c>IGpuDevice.Capabilities</c> come from on this backend, exactly as
        /// <c>VeldridMap.ReadCapabilities</c> was on the incumbent, and section 11 says so for the same reason the
        /// incumbent had one: the two copies drifted before 15.2.0 and the device wrapper's silently left the
        /// adapter name and both sampler flags at their defaults.
        /// </summary>
        /// <param name="adapterName">Already through <see cref="TrimAdapterName"/>.</param>
        /// <param name="maxMsaaSampleCount">Already through <see cref="MinOverFormats"/>.</param>
        /// <param name="supportsShadowMaps">Whether R32_FLOAT is usable as both a render target and a sampled
        /// texture, which is what the manual-PCF shadow path needs.</param>
        /// <param name="supportsCompletionFences">
        /// The ONE difference the incumbent parity permitted, and it is a parameter rather than a constant because
        /// <see cref="D3D11FenceSubsystem.SupportsCompletionFences"/> owns the answer. Decision C5 makes it true
        /// on both fence mechanisms, so this is true in practice, and taking it from the subsystem is what stops
        /// the capability and the fence path being able to disagree.
        /// </param>
        internal static GpuCapabilities Assemble(string adapterName, int maxMsaaSampleCount,
            bool supportsShadowMaps, bool supportsCompletionFences)
            => new(
                ClipSpaceYInverted,
                DepthRangeZeroToOne,
                adapterName ?? string.Empty,
                SamplerAnisotropy,
                SamplerLodBias,
                maxMsaaSampleCount,
                supportsShadowMaps,
                SupportsCompute,
                supportsCompletionFences);

        /// <summary>
        /// DECISION C4's THROW: a requested sample count above what the device supports is a fault, not something
        /// to quietly round down. Null when <paramref name="requested"/> is fine, otherwise the message for an
        /// <see cref="ArgumentException"/> the caller raises against its own parameter name.
        /// <para>
        /// SILENT DEGRADATION IS THE FAILURE THIS EXISTS TO PREVENT, and the seam already carries the correct
        /// place to degrade: <c>AntiAliasing.ResolveFor</c> clamps a player's MSAA request to
        /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/> before any texture is created. A count that arrives
        /// here above the maximum therefore did NOT come through that clamp, so honouring it by rounding down
        /// would hide a caller that skipped the one place the engine decides this, and the visible symptom would
        /// be a framebuffer that is quietly not multisampled.
        /// </para>
        /// <para>
        /// A count of 1 always passes, whatever the maximum says, because 1 is not multisampling at all and
        /// <see cref="GpuTextureDescription"/> already normalises 0 to it.
        /// </para>
        /// </summary>
        internal static string? UnsupportedSampleCountMessage(uint requested, int maxSupported)
        {
            if (requested <= NoMultisampling) return null;
            if (maxSupported >= NoMultisampling && requested <= (uint)maxSupported) return null;

            int supported = maxSupported < NoMultisampling ? NoMultisampling : maxSupported;
            return $"A texture was requested at {requested} MSAA samples, but this device supports at most "
                + $"{supported} for the render-target formats the engine uses (GpuCapabilities."
                + "MaxMsaaSampleCount). Direct3D 11 would refuse the texture, and rounding the request down here "
                + "would silently produce a framebuffer that is not multisampled. Clamp the request through "
                + "AntiAliasing.ResolveFor, which is where the engine decides this.";
        }
    }
}
