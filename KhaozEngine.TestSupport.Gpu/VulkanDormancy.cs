using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE WAY OUT OF A DORMANT ROW LOOKING LIKE A PASSING ONE. Every row that needs a real native Vulkan device
    /// returns early on a machine whose functional probe refuses it, which is correct on a developer box with no
    /// loader and on every CI leg but the <c>vulkan-native</c> one
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/529">#529</see>). The problem is what that
    /// early return LOOKS like on the leg built to run those rows: a test that asserted nothing and reported
    /// green, which the zero-skipped gate cannot see, because the row did not skip.
    /// <para>
    /// <c>KE_VULKAN_REQUIRED=1</c> is a leg saying "I am the machine that has one". With it set,
    /// <see cref="NativeDeviceAvailable"/> THROWS instead of answering false, and the message names what the
    /// probe actually objected to, so a loader regression on that leg turns it red rather than quietly emptying
    /// the rows it was built for. Unset, every row goes dormant exactly as before, which is what keeps a
    /// developer box and every other leg green.
    /// </para>
    /// <para>
    /// It reads the SAME probe the rows read (<see cref="GpuBackendSelector.IsBackendSupported"/>), rather than
    /// an operating-system check, because Vulkan is not a Windows API and decision V-P1 leaves the whole
    /// question to the probe. The value is compared against <c>"1"</c> exactly, the spelling
    /// <see cref="GpuFactAttribute"/> already uses for <c>KE_GPU_TESTS</c>.
    /// </para>
    /// </summary>
    public static class VulkanDormancy
    {
        /// <summary>The variable a leg sets to declare it must have a native Vulkan device. Named as a constant so
        /// the workflow, the test file headers and the failure message cannot drift apart on the spelling.</summary>
        public const string RequiredVariable = "KE_VULKAN_REQUIRED";

        /// <summary>Whether this leg declared a native Vulkan device mandatory.</summary>
        public static bool IsRequired => Environment.GetEnvironmentVariable(RequiredVariable) == "1";

        /// <summary>
        /// Whether this machine can run the native Vulkan backend, THROWING instead of answering false when
        /// <see cref="IsRequired"/>. A caller treats false as "go dormant" and does not have to know the variable
        /// exists.
        /// </summary>
        /// <exception cref="InvalidOperationException">The probe refused and the leg set
        /// <see cref="RequiredVariable"/>.</exception>
        public static bool NativeDeviceAvailable()
        {
            if (GpuBackendSelector.IsBackendSupported(GpuBackendKind.VulkanNative)) return true;
            if (!IsRequired) return false;

            throw new InvalidOperationException(RefusalMessage(VulkanSupportProbe.MissingRequirement()));
        }

        /// <summary>
        /// The pure half: what a refusal reads like, given the probe's own answer. Split out so the message is
        /// assertable on a machine with no Vulkan loader, which is every machine that will ever be in a position
        /// to review this file.
        /// </summary>
        /// <param name="probeAnswer">What <c>VulkanSupportProbe.MissingRequirement</c> said, or null when it
        /// found nothing missing, which on this path means the refusal came from somewhere other than the
        /// requirement walk.</param>
        internal static string RefusalMessage(string? probeAnswer)
            => $"{RequiredVariable}=1 says this leg must have a native Vulkan device, and the backend's own "
                + "functional probe refused this machine: "
                + (probeAnswer ?? "the probe named no missing requirement, so the refusal came from outside the "
                    + "requirement walk (a loader that resolved and then failed, or a KE_VULKAN_DEVICE selection "
                    + "matching nothing)")
                + ". This is a hard failure rather than a dormant row because a dormant row on THIS leg is a "
                + "pass with no assertions in it, which the zero-skipped gate cannot see. Unset "
                + $"{RequiredVariable} to let these rows go dormant again.";
    }
}
