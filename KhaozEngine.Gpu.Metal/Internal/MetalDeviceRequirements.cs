using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The DECISION half of the machine probe: whether a device described by <see cref="MetalDeviceFacts"/> can
    /// run this backend, and if not, the sentence saying what is missing. Device-free by construction, so every
    /// requirement is exercised on every leg from fabricated facts rather than only on the one leg with a Metal
    /// device.
    /// <para>
    /// The four reads are M-N4's, in the order section 4.1 lists them, and each is cheap here and expensive
    /// anywhere later. A device exists and reports a NAME, which is what <c>GpuCapabilities.DeviceName</c> parity
    /// depends on. <c>supportsFamily:</c> answers at or above the floor, so a machine below it is refused rather
    /// than crashing on frame one. The device's minimum buffer-offset alignment divides M-M3's 256 stride, which
    /// is the one number that would silently corrupt every ring bind on a future device. And
    /// <c>supportsTextureSampleCount:</c> answers for 1, which is where M-C3's limit walk starts.
    /// </para>
    /// <para>
    /// EVERY MESSAGE IS A SENTENCE A TESTER CAN ACT ON, because of where it ends up: the provider logs it, and
    /// the creation path puts it in a <c>NotSupportedException</c> that the fallback catches and WARNs with. A
    /// refusal that reads as "unsupported" and nothing else is indistinguishable in a session log from a
    /// forgotten registration, and telling those two apart is what decision M-I4 exists for.
    /// </para>
    /// </summary>
    internal static class MetalDeviceRequirements
    {
        /// <summary>
        /// The uniform ring's stride granularity (M-M3). Every ring bind lands at a multiple of this, so the
        /// device's own minimum alignment has to divide it. Stated here rather than imported from the ring
        /// because the ring is row 8 and this check is the reason a machine that could not honour it never
        /// reaches row 8's code at all.
        /// </summary>
        internal const nuint UniformRingStride = 256;

        /// <summary>
        /// The lowest <c>MTLGPUFamilyApple</c><i>n</i> that clears the floor on its own. One, because the floor
        /// is stated as a family KIND rather than a generation: section 5's M-N3 pins <c>MTLGPUFamilyApple*</c>,
        /// <c>Mac2</c> and <c>Metal3</c> as the families new reads use, and does not pin a generation, so
        /// inventing one here would be refusing machines on a number nobody adjudicated.
        /// </summary>
        internal const int MinimumAppleFamily = 1;

        /// <summary>
        /// What stops <paramref name="facts"/> running this backend, or null when nothing does. Null is the ONLY
        /// yes: an empty or whitespace string would read in a session log as a machine refused for no reason.
        /// </summary>
        internal static string? MissingRequirement(in MetalDeviceFacts facts)
        {
            if (!facts.DeviceCreated)
            {
                return "MTLCreateSystemDefaultDevice() returned nil, so this machine has no usable Metal device. "
                    + "That is the floor the incumbent Veldrid Metal backend's own support check stops at, and a "
                    + "machine that fails it cannot run either implementation";
            }

            if (string.IsNullOrWhiteSpace(facts.DeviceName))
            {
                return "the Metal device reports no name. GpuCapabilities.DeviceName is compared field for field "
                    + "against the incumbent backend under a zero-permitted-difference bar (M-G1), so a nameless "
                    + "device cannot satisfy capability parity and is refused here rather than failing that "
                    + "comparison later";
            }

            if (facts.HighestAppleFamily < MinimumAppleFamily && !facts.SupportsMac2)
            {
                return "the Metal device answers supportsFamily: for neither MTLGPUFamilyApple"
                    + MinimumAppleFamily.ToString(CultureInfo.InvariantCulture)
                    + " nor MTLGPUFamilyMac2"
                    + (facts.SupportsCommon1 ? " (it does answer MTLGPUFamilyCommon1)" : " (nor Common1)")
                    + ". Every Mac GPU on a macOS this engine supports answers at least one of them, so a device "
                    + "that answers neither is below the floor section 5 pins and is refused here rather than "
                    + "crashing on frame one";
            }

            if (facts.BufferOffsetAlignment == 0)
            {
                return "the Metal device would not report a buffer-offset alignment at all (read attempted "
                    + "through " + facts.BufferOffsetAlignmentSource + "). The uniform ring binds at multiples "
                    + "of " + UniformRingStride.ToString(CultureInfo.InvariantCulture)
                    + " bytes (M-M3) and an unreadable alignment cannot be checked against that, so this refuses "
                    + "in the conservative direction: a wrong answer here is not a failed frame, it is every "
                    + "ring bind landing at an offset the device never agreed to";
            }

            if (facts.BufferOffsetAlignment > UniformRingStride
                || UniformRingStride % facts.BufferOffsetAlignment != 0)
            {
                return "the Metal device requires buffer offsets aligned to "
                    + facts.BufferOffsetAlignment.ToString(CultureInfo.InvariantCulture)
                    + " bytes (read through " + facts.BufferOffsetAlignmentSource + "), which "
                    + UniformRingStride.ToString(CultureInfo.InvariantCulture)
                    + " is not a multiple of. M-M3's ring stride is that number on every device, so this machine "
                    + "would bind every uniform range at an offset the device does not accept";
            }

            if (!facts.SupportsTextureSampleCount1)
            {
                return "the Metal device answers no to supportsTextureSampleCount:1, which is the only "
                    + "sample-count query Metal has and the value every non-multisampled texture uses. "
                    + "GpuCapabilities.MaxMsaaSampleCount walks upward from it (M-C3), so a device that refuses "
                    + "1 has no sample count this backend can offer at all";
            }

            return null;
        }
    }
}
