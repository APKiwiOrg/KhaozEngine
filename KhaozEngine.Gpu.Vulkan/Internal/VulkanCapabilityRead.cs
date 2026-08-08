using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// DECISION V-G1's CAPABILITY ASSEMBLY, WITH NO DEVICE IN IT. Section 14 of
    /// <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c> carries a member-by-member table saying where
    /// each <see cref="GpuCapabilities"/> member comes from on this backend, and five of the nine are CONSTANTS of
    /// the configuration this backend creates rather than answers a device gives. Those five, the device-name
    /// normalisation and the sample-count floor are all here, so every rule that decides what the engine believes
    /// about the device is a plain <c>[Fact]</c> on a machine with no Vulkan loader at all.
    /// <para>
    /// THE THREE ANSWERS A DEVICE ACTUALLY GIVES are the reported device name, <c>samplerAnisotropy</c> off the
    /// feature chain, and the <c>R32_SFLOAT</c> format-properties read behind
    /// <see cref="GpuCapabilities.SupportsShadowMaps"/>. <see cref="VulkanPhysicalDeviceReader"/> asks for those
    /// against a real <c>VkPhysicalDevice</c> and hands them here as plain data, which is the whole split.
    /// </para>
    /// <para>
    /// PARITY WITH THE INCUMBENT IS THE POINT, AND ZERO MEMBERS MAY DIFFER (V-G1), which is a stricter bar than
    /// the Direct3D 11 backend's and is the correct one here: that backend had a capability defect to correct,
    /// and this one does not, because <c>KhaozEngine.Gpu.Internal.VeldridMap.SupportsCompletionFences</c> already
    /// answers true for <c>GraphicsBackend.Vulkan</c>. <c>VeldridMap.ReadCapabilities</c> is the ground truth
    /// this must match member for member, and <c>NativeVsVeldridVulkanCapabilityParityTests</c> asserts it with
    /// nothing exempted. A difference the parity test finds is a bug HERE until proven otherwise.
    /// </para>
    /// <para>
    /// <b><see cref="GpuCapabilities.MaxMsaaSampleCount"/> IS THE ONE MEMBER THIS TYPE DOES NOT DECIDE</b>, and it takes it as a
    /// parameter for that reason. V-C5 rules that the computation is READ OFF the incumbent's own
    /// <c>GetSampleCountLimit</c> and reproduced with its citation pinned, which is work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525). Until that lands the caller passes
    /// <see cref="NoMultisampling"/>, which under-promises rather than inventing a formula: two drafts of the
    /// design each invented one, the two differ, and both then asserted equality with the incumbent as a test.
    /// </para>
    /// </summary>
    internal static class VulkanCapabilityRead
    {
        /// <summary>
        /// FALSE, and it is the one capability that flips every image. Vulkan's clip space really does point Y the
        /// other way, and this backend answers false because it corrects for that at the source: the viewport it
        /// emits on a framebuffer change carries NEGATIVE height (7.2, row 12), which puts the rendered image the
        /// same way up as Direct3D's and leaves <c>GpuClip.Correct</c> as the identity. The incumbent answers
        /// false for the same reason through Veldrid's own negative-height viewport, so this is a reproduction
        /// rather than a divergence.
        /// </summary>
        internal const bool ClipSpaceYInverted = false;

        /// <summary>Vulkan normalized device depth is [0, 1], not the legacy GL [-1, 1].</summary>
        internal const bool DepthRangeZeroToOne = true;

        /// <summary>Every Vulkan implementation honours a sampler mip LOD bias: <c>mipLodBias</c> is a plain field
        /// of <c>VkSamplerCreateInfo</c> with no feature bit behind it, bounded only by the
        /// <c>maxSamplerLodBias</c> limit. There is no device to ask and no fallback to write.</summary>
        internal const bool SamplerLodBias = true;

        /// <summary>Compute shaders are core Vulkan rather than an optional feature: there is no feature bit to
        /// read and no device to ask, so this is a constant on both paths. The incumbent reports true for Vulkan
        /// through the same reasoning.</summary>
        internal const bool SupportsCompute = true;

        /// <summary>
        /// TRUE, unlike the Direct3D 11 native backend's, and this is where V-G1's zero-permitted-difference bar
        /// comes from. A fence handed to <c>Submit</c> is a value on the device's one timeline semaphore that
        /// <c>vkQueueSubmit</c> itself signals on GPU completion (V-F1), and the incumbent answers true for
        /// Vulkan too, so the member that had to be exempted on the other backend is identical here.
        /// </summary>
        internal const bool SupportsCompletionFences = true;

        /// <summary>One sample, which is how "no MSAA" is spelled everywhere in the seam, and the value the
        /// capability read carries until row 15 supplies the incumbent's own computation.</summary>
        internal const int NoMultisampling = 1;

        /// <summary>
        /// THE DEVICE NAME AS THE SEAM WANTS IT: exactly what the driver reported, or the empty string when it
        /// reported nothing.
        /// <para>
        /// THIS IS NOT <see cref="VulkanDeviceFacts.DeviceName"/>, AND THE DIFFERENCE IS A PARITY ONE. That one
        /// is the LOGGABLE name and substitutes <c>"unnamed device 0x…"</c> for a driver that reports nothing,
        /// because a rejection line naming an empty string is a line nobody can act on. The incumbent performs no
        /// such substitution (Veldrid's Vulkan device reports <c>VkPhysicalDeviceProperties.deviceName</c> read
        /// as a C string, and <c>VeldridMap.ReadCapabilities</c> turns a null into <c>""</c>), and
        /// <see cref="GpuCapabilities.DeviceName"/> is compared string for string by the parity test, so carrying
        /// the substituted name across the seam would be a capability difference on exactly the devices that
        /// report no name. The seam's own doc already says empty is what "the backend does not report one" looks
        /// like.
        /// </para>
        /// <para>
        /// NO WHITESPACE TRIM, for the reason the Direct3D 11 backend does not trim either: the incumbent does
        /// not, and trimming on one path alone converts a cosmetic improvement into a parity failure on every
        /// machine whose vendor pads its name. The NUL cut is defensive and expects to find nothing, since the
        /// marshaller on both paths already stops at the first terminator.
        /// </para>
        /// </summary>
        internal static string ReportedDeviceName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            int terminator = name.IndexOf('\0', StringComparison.Ordinal);
            return terminator >= 0 ? name.Substring(0, terminator) : name;
        }

        /// <summary>
        /// Section 14's table, assembled. Everything not passed in is a constant above, and every constant is
        /// asserted BY VALUE in the parity test rather than read back from here, so a change to one fails that
        /// test rather than agreeing with itself.
        /// </summary>
        /// <param name="deviceName">The driver's own <c>VkPhysicalDeviceProperties.deviceName</c>, before the
        /// loggable substitution.</param>
        /// <param name="samplerAnisotropy">The device's <c>samplerAnisotropy</c> feature bit, which is also
        /// whether this backend asked <c>vkCreateDevice</c> for it.</param>
        /// <param name="supportsShadowMaps">Whether <c>R32_SFLOAT</c> can be both a depth-stencil attachment and
        /// a sampled image, off <c>vkGetPhysicalDeviceFormatProperties</c>.</param>
        /// <param name="maxMsaaSampleCount">Row 15's reproduction of the incumbent's own computation, or
        /// <see cref="NoMultisampling"/> until it lands.</param>
        internal static GpuCapabilities Assemble(
            string? deviceName,
            bool samplerAnisotropy,
            bool supportsShadowMaps,
            int maxMsaaSampleCount)
            => new(
                clipSpaceYInverted: ClipSpaceYInverted,
                depthRangeZeroToOne: DepthRangeZeroToOne,
                deviceName: ReportedDeviceName(deviceName),
                samplerAnisotropy: samplerAnisotropy,
                samplerLodBias: SamplerLodBias,
                maxMsaaSampleCount: maxMsaaSampleCount,
                supportsShadowMaps: supportsShadowMaps,
                supportsCompute: SupportsCompute,
                supportsCompletionFences: SupportsCompletionFences);
    }
}
