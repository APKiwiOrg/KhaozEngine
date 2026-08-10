using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-G1's CAPABILITY ASSEMBLY, WITH NO DEVICE IN IT. Section 14 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> carries a member-by-member table saying where
    /// each <see cref="GpuCapabilities"/> member comes from on this backend, and SEVEN of the nine turn out to be
    /// constants of what the incumbent answers on Metal rather than answers a device gives. Those seven, the
    /// device-name pass-through and the sample-count walk are all here, so every rule that decides what the engine
    /// believes about the device is a plain <c>[Fact]</c> on a machine with no Metal at all.
    /// <para>
    /// THE TWO ANSWERS A DEVICE ACTUALLY GIVES are its own <c>-name</c> and the run of
    /// <c>-supportsTextureSampleCount:</c> answers behind <see cref="GpuCapabilities.MaxMsaaSampleCount"/>.
    /// Everything else is fixed, and the surprise is <see cref="GpuCapabilities.SupportsShadowMaps"/>, which the
    /// design expected to be a device read and is not: see its own remarks below.
    /// </para>
    /// <para>
    /// PARITY WITH THE INCUMBENT IS THE POINT, AND ZERO MEMBERS MAY DIFFER (M-G1), which is phase 3's bar rather
    /// than phase 2's and is right for phase 3's reason: the incumbent Metal backend has no capability defect to
    /// correct, because <c>KhaozEngine.Gpu.Internal.VeldridMap.SupportsCompletionFences</c> already answers true
    /// for <c>GraphicsBackend.Metal</c>. <c>VeldridMap.ReadCapabilities</c> over <c>MTLGraphicsDevice</c> is the
    /// ground truth this must match member for member, and <c>NativeVsVeldridMetalCapabilityParityTests</c>
    /// asserts it with nothing exempted. A difference that test finds is a bug HERE until proven otherwise.
    /// </para>
    /// </summary>
    internal static class MetalCapabilityRead
    {
        /// <summary>
        /// FALSE, with no viewport trick behind it, which is the one place this backend is simpler than its Vulkan
        /// sibling rather than merely different. Metal's clip space already points Y the way the engine's does
        /// (7.3), so nothing is corrected anywhere and <c>GpuClip.Correct</c> stays the identity. The incumbent
        /// answers false as a hardcoded <c>MTLGraphicsDevice.IsClipSpaceYInverted</c> property, so this is a
        /// reproduction of a constant rather than of a computation.
        /// </summary>
        internal const bool ClipSpaceYInverted = false;

        /// <summary>Metal normalized device depth is [0, 1], not the legacy GL [-1, 1], and the incumbent says so
        /// as a hardcoded <c>IsDepthRangeZeroToOne</c> property.</summary>
        internal const bool DepthRangeZeroToOne = true;

        /// <summary>
        /// TRUE, hardcoded, reproducing the incumbent's <c>GraphicsDeviceFeatures(samplerAnisotropy: true)</c>.
        /// There is no feature bit to read: <c>MTLSamplerDescriptor.maxAnisotropy</c> is a plain field every Metal
        /// device honours, so the incumbent's hardcode is correct rather than a shortcut, and asking a device
        /// would be inventing a question the API does not have.
        /// </summary>
        internal const bool SamplerAnisotropy = true;

        /// <summary>
        /// FALSE, AND IT IS THE ONE CAPABILITY THAT DIFFERS FROM BOTH OTHER NATIVE BACKENDS.
        /// <c>MTLSamplerDescriptor</c> has no LOD bias field at all, so there is nothing to set and nothing to
        /// ask, and the incumbent reaches the same answer the same way with
        /// <c>GraphicsDeviceFeatures(samplerLodBias: false)</c>.
        /// </summary>
        internal const bool SamplerLodBias = false;

        /// <summary>
        /// TRUE, AND IT IS A CONSTANT RATHER THAN THE DEVICE READ SECTION 14 EXPECTED, which is this row's one
        /// correction to the design and is written down here because the next reader will reach for a device
        /// query. The table says the native source is "the incumbent's own question: is <c>R32_Float</c> usable as
        /// BOTH render target and sampled", and asking the incumbent that question ON METAL has no device call in
        /// it: <c>VeldridMap.SupportsShadowMaps</c> calls <c>GetPixelFormatSupport</c>, which on
        /// <c>MTLGraphicsDevice.GetPixelFormatSupportCore</c> asks <c>MTLFormats.IsFormatSupported</c>, whose
        /// switch has no <c>R32_Float</c> case and falls to <c>default: return true</c>. The remainder of that
        /// method only fills in a properties struct nobody here reads, and returns true for a
        /// <c>TextureType.Texture2D</c> unconditionally. So the incumbent answers TRUE on every Metal device that
        /// exists, and reproducing the question faithfully means reproducing that constant.
        /// <para>
        /// AND ASKING METAL A REAL QUESTION HERE WOULD BE A PARITY FAILURE RATHER THAN AN IMPROVEMENT, which is
        /// M-N3's parity exception spelled out on the one member where it bites. A native backend that read the
        /// device's own format table could only ever produce a different answer from the incumbent, and a
        /// difference on this member is not a red leg: it is the shadow path degrading to blob shadows on one
        /// backend, silently, which is the phase-3 failure section 14 inherits by name.
        /// </para>
        /// </summary>
        internal const bool SupportsShadowMaps = true;

        /// <summary>Compute is core Metal rather than an optional feature, and the incumbent says so with
        /// <c>GraphicsDeviceFeatures(computeShader: true)</c>. There is no device to ask.</summary>
        internal const bool SupportsCompute = true;

        /// <summary>
        /// TRUE, and it was ALREADY true, which is why M-G1's bar is zero permitted differences. A fence handed to
        /// <c>Submit</c> is a value on this device's one <c>MTLSharedEvent</c> that the GPU signals on completion
        /// (M-F1), and <c>VeldridMap.SupportsCompletionFences</c> answers true for <c>GraphicsBackend.Metal</c>
        /// because the incumbent sets its fence from a command-buffer completion handler. The member that had to
        /// be exempted on the Direct3D 11 backend is identical on both Metal paths.
        /// </summary>
        internal const bool SupportsCompletionFences = true;

        /// <summary>One sample, which is how "no MSAA" is spelled everywhere in the seam, and the answer
        /// <see cref="HighestSupportedSampleCount"/> falls back to when a device answers no to every count it is
        /// asked about. The incumbent has the same floor, as the <c>return TextureSampleCount.Count1</c> after its
        /// walk finds nothing.</summary>
        internal const int NoMultisampling = 1;

        /// <summary>
        /// THE COUNTS THE WALK ASKS ABOUT, IN THE ORDER IT ASKS, which is DOWNWARD from the largest.
        /// <para>
        /// The set is the incumbent's own, and it is the <c>Veldrid.TextureSampleCount</c> enum rather than a
        /// choice made here: its members are <c>Count1</c> through <c>Count32</c> at values 0 to 5, the incumbent
        /// builds <c>_supportedSampleCounts</c> by asking <c>-supportsTextureSampleCount:</c> for each one at
        /// device creation, and <c>MTLGraphicsDevice.GetSampleCountLimit</c> walks that array from the TOP and
        /// returns the first supported entry. Asking 32 first and stopping at the first yes is the same walk with
        /// the array left out.
        /// </para>
        /// <para>
        /// DOWNWARD MATTERS AND UPWARD WOULD BE A DIFFERENT ANSWER. The supported counts are not required to be
        /// contiguous, so a device that answers yes to 4 and 16 but no to 8 stops an upward walk at 4 and
        /// under-reports the device by a factor of four, which on this member is a menu quietly offering less MSAA
        /// than the card has and a golden baked at the wrong sample count. The incumbent walks downward, so this
        /// does.
        /// </para>
        /// </summary>
        internal static ReadOnlySpan<int> SampleCountsHighestFirst => [32, 16, 8, 4, 2, 1];

        /// <summary>
        /// M-C3's <c>MaxMsaaSampleCount</c>, as the pure walk with the device call passed in.
        /// <para>
        /// <b>IT IGNORES THE FORMAT, AND THAT IS CORRECT RATHER THAN A BUG CARRIED FOR PARITY.</b>
        /// <c>-supportsTextureSampleCount:</c> is the ONLY sample-count query Metal has and it takes no pixel
        /// format, so a sample-count limit on Metal is format-independent by construction.
        /// <c>MTLGraphicsDevice.GetSampleCountLimit</c> declares <c>(PixelFormat format, bool depthFormat)</c> and
        /// reads neither, and <c>VeldridMap.MaxMsaaSampleCount</c> then takes a MIN over three formats whose three
        /// answers are the same number, so the min is a no-op and reproducing it means reproducing one walk. A
        /// native backend that "improved" this by asking per format would be inventing a question the API cannot
        /// answer, which is the C4 failure phase 2 corrected in flight and the V-C5 ruling phase 3 made against
        /// both of its drafts, arriving here for the third time.
        /// </para>
        /// <para>
        /// THIS IS THE MEMBER THAT IS GOLDEN-VISIBLE WITHOUT BEING LOUD. <c>AntiAliasing.ResolveFor</c> clamps the
        /// requested sample count against it, so a wrong answer here does not throw and does not log: it changes
        /// which MSAA level the scene renders at, and <c>scene3d_hdr_msaa</c> is baked under the incumbent's
        /// answer.
        /// </para>
        /// </summary>
        /// <param name="supports">The device's <c>-supportsTextureSampleCount:</c>, or a hand-written set in a
        /// test. Asked once per count in <see cref="SampleCountsHighestFirst"/> order, and never asked again after
        /// the first yes.</param>
        internal static int HighestSupportedSampleCount(Func<int, bool> supports)
        {
            ArgumentNullException.ThrowIfNull(supports);

            foreach (int count in SampleCountsHighestFirst)
            {
                if (supports(count)) return count;
            }

            return NoMultisampling;
        }

        /// <summary>
        /// THE DEVICE NAME AS THE SEAM WANTS IT: exactly what Metal reported, or the empty string when it reported
        /// nothing.
        /// <para>
        /// NO TRIM, WHICH SECTION 14 INHERITS FROM PHASE 3 BY NAME RATHER THAN REDISCOVERING. The incumbent stores
        /// <c>_device.name</c> as it comes and hands it back verbatim through <c>MTLGraphicsDevice.DeviceName</c>,
        /// and <see cref="GpuCapabilities.DeviceName"/> is compared string for string by the parity test, so a
        /// trim on the native path alone would convert a cosmetic improvement into a parity failure on any device
        /// whose reported name carries padding. Apple's own names do not pad today, which is exactly why this
        /// would ship green and fail on somebody else's machine.
        /// </para>
        /// <para>
        /// The null-to-empty fold is the seam's documented spelling of "the backend reports no name", and it is
        /// the same fold <c>VeldridMap.ReadCapabilities</c> performs with <c>gd.DeviceName ?? ""</c>.
        /// </para>
        /// </summary>
        internal static string ReportedDeviceName(string? name) => name ?? string.Empty;

        /// <summary>
        /// Section 14's table, assembled. Everything not passed in is a constant above, and every constant is
        /// asserted BY VALUE in the parity test rather than read back from here, so a change to one fails that
        /// test rather than agreeing with itself.
        /// </summary>
        /// <param name="deviceName">The device's own <c>-name</c>, verbatim.</param>
        /// <param name="maxMsaaSampleCount"><see cref="HighestSupportedSampleCount"/> over a real device, which is
        /// the only member here a device is asked about beyond its name.</param>
        internal static GpuCapabilities Assemble(string? deviceName, int maxMsaaSampleCount)
            => new(
                clipSpaceYInverted: ClipSpaceYInverted,
                depthRangeZeroToOne: DepthRangeZeroToOne,
                deviceName: ReportedDeviceName(deviceName),
                samplerAnisotropy: SamplerAnisotropy,
                samplerLodBias: SamplerLodBias,
                maxMsaaSampleCount: maxMsaaSampleCount,
                supportsShadowMaps: SupportsShadowMaps,
                supportsCompute: SupportsCompute,
                supportsCompletionFences: SupportsCompletionFences);
    }
}
