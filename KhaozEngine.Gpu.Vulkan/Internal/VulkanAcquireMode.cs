using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>How the present boundary gets the next swapchain image, which is MV2's A/B in one enum.</summary>
    internal enum VulkanAcquireMode
    {
        /// <summary>
        /// THE SHIPPED PATH (V-W3). <c>vkAcquireNextImageKHR</c> signals a BINARY semaphore, the frame's submit
        /// waits on it at <c>COLOR_ATTACHMENT_OUTPUT</c>, the submit signals a render-finished semaphore, and the
        /// present waits on that. The index comes back synchronously either way, so the acquire TIMING is
        /// unchanged and only the synchronisation moves off the CPU.
        /// </summary>
        Semaphore,

        /// <summary>
        /// THE INCUMBENT'S SHAPE, RESTORED EXACTLY, for the A/B and for nothing else. The acquire takes a
        /// <c>VkFence</c>, the CPU blocks on it with an infinite timeout inside the present boundary, the submit
        /// carries no image-availability wait semaphore and the present carries no wait semaphore either.
        /// <para>
        /// THAT LAST PART IS A SPEC VIOLATION A VALIDATION LAYER FLAGS, which is why this mode and
        /// <c>KE_VULKAN_VALIDATION</c> are not usable together. It is a documented limitation rather than a bug:
        /// the whole point of the mode is to reproduce the configuration validation rejects, so a run with both on
        /// would report the thing it was asked to reproduce.
        /// </para>
        /// </summary>
        Stall,
    }

    /// <summary>
    /// <c>KE_VULKAN_ACQUIRE</c>, MV2'S KILL SWITCH: which of the two acquire models this run uses.
    /// <para>
    /// <b>IT KEEPS A SECOND IMPLEMENTATION ALIVE, so it is REMOVED at rollout gate 4 with the losing path deleted,
    /// whichever way the bet goes (V-RO4).</b> That is the difference between this variable and
    /// <see cref="VulkanFramesInFlight"/>, which selects a value inside one implementation and may survive its
    /// gate as a knob. A switch that outlives its bet is the failure phase 2 recorded, where a gate stayed blocked
    /// behind an unresolved A/B with two drivers still shipping.
    /// </para>
    /// <para>
    /// <b>WHY THE BET NEEDS A SWITCH AT ALL, given the semaphore path is FORCED rather than preferred.</b> The
    /// correctness argument is settled: presenting with no wait semaphore is a spec violation, and a design that
    /// gates on validation cannot deliberately reproduce a configuration validation rejects. What is NOT settled
    /// is the PACING claim, that removing the per-frame CPU stall costs no presentation smoothness, and that is
    /// only answerable by measuring both positions on one machine with one build. So the switch exists to A/B
    /// frame pacing rather than to hedge correctness, and it is not a fallback anybody should ship on.
    /// </para>
    /// <para>
    /// <b>THE MEASUREMENT IS READ OFF THE ACQUIRE-WAIT COUNTERS, not off mean frame time.</b> The reporting
    /// machine runs pinned at its refresh rate, where both positions produce the same mean by construction, so the
    /// A/B capture is taken with the frame cap and vsync BOTH OFF and the discriminating numbers are
    /// <c>AcquireWaitCount</c> and <c>AcquireWaitMs</c> (V-G6): near zero on the semaphore side against a
    /// substantial fraction of the frame interval on the stall side.
    /// </para>
    /// <para>
    /// Everything here is pure except <see cref="FromEnvironment"/>, so the parse is headless-testable on any
    /// operating system, matching <see cref="VulkanValidation"/> and <see cref="VulkanFramesInFlight"/>.
    /// </para>
    /// </summary>
    internal static class VulkanAcquire
    {
        /// <summary>The env var, following the engine's <c>KE_</c> convention. Unset, empty or unrecognised leaves
        /// <see cref="VulkanAcquireMode.Semaphore"/>.</summary>
        internal const string EnvVarName = "KE_VULKAN_ACQUIRE";

        /// <summary>The value that selects the incumbent's blocking shape. The only value that changes
        /// anything, spelled as the thing it does rather than as a number.</summary>
        internal const string StallValue = "stall";

        /// <summary>The value that selects the default explicitly, so a session can pin the shipped path in a
        /// script without relying on the variable being unset.</summary>
        internal const string SemaphoreValue = "semaphore";

        /// <summary>
        /// Which acquire model <paramref name="envValue"/> asks for. A non-blank value that is neither
        /// <see cref="StallValue"/> nor <see cref="SemaphoreValue"/> comes back through
        /// <paramref name="unrecognizedValue"/> verbatim so the caller can WARN, and the default is used.
        /// <para>
        /// The unrecognised case earns its branch for the same reason the frames-in-flight one does: this variable
        /// exists to settle a MEASUREMENT, and a mistyped value that silently left the default in place would
        /// produce a capture that reads as evidence about the stall path and was taken on the semaphore path.
        /// </para>
        /// </summary>
        internal static VulkanAcquireMode Resolve(string? envValue, out string? unrecognizedValue)
        {
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(envValue)) return VulkanAcquireMode.Semaphore;

            string trimmed = envValue.Trim();
            if (string.Equals(trimmed, StallValue, StringComparison.OrdinalIgnoreCase))
                return VulkanAcquireMode.Stall;
            if (string.Equals(trimmed, SemaphoreValue, StringComparison.OrdinalIgnoreCase))
                return VulkanAcquireMode.Semaphore;

            unrecognizedValue = envValue;
            return VulkanAcquireMode.Semaphore;
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        internal static VulkanAcquireMode FromEnvironment(out string? unrecognizedValue)
            => Resolve(Environment.GetEnvironmentVariable(EnvVarName), out unrecognizedValue);

        /// <summary>The WARN body for a value that was set and understood as nothing.</summary>
        internal static string UnrecognizedWarning(string value)
            => $"{EnvVarName}='{value}' is neither '{SemaphoreValue}' nor '{StallValue}'. The native Vulkan "
                + "backend acquires the next swapchain image with a binary semaphore, which is the default and "
                + $"the shipped path. Set {EnvVarName}={StallValue} to restore the incumbent's blocking acquire "
                + "for the frame-pacing A/B.";

        /// <summary>
        /// The INFO line naming which model this run got, so a capture proves the position its acquire-wait
        /// counters were measured in rather than resting on the tester believing they set the variable. MV2's exit
        /// criterion compares two captures, and a capture that cannot say which side it is from settles nothing.
        /// </summary>
        internal static string ActiveDescription(VulkanAcquireMode mode)
            => mode == VulkanAcquireMode.Semaphore
                ? "The native Vulkan backend acquires the next swapchain image with a binary semaphore the "
                    + "frame's submit waits on, which is the default and does not block the CPU. Set "
                    + $"{EnvVarName}={StallValue} to restore the incumbent's blocking acquire, which is MV2's A/B."
                : "The native Vulkan backend acquires the next swapchain image with a fence and BLOCKS THE CPU on "
                    + $"it, restoring the incumbent's shape ({EnvVarName}={StallValue}). The submit carries no "
                    + "image-availability wait semaphore and the present carries no wait semaphore, which is what "
                    + "a validation layer rejects, so this mode is not usable with KE_VULKAN_VALIDATION. It exists "
                    + "for the frame-pacing A/B and is removed at rollout gate 4.";

        /// <summary>
        /// The WARN body for the one combination that cannot work: the stall mode under any validation rung.
        /// Warned rather than refused, because the two variables are set by different people for different reasons
        /// and turning a diagnostic session into a startup failure is the wrong trade. What the message must do is
        /// say which of the two is about to be useless, and it is the validation output: a run in this mode
        /// deliberately presents without a wait semaphore, so the layer reports the thing the mode was asked to
        /// reproduce and buries anything else it found.
        /// </summary>
        internal static string ValidationConflictWarning(VulkanValidationMode validation)
            => $"{EnvVarName}={StallValue} and {VulkanValidation.EnvVarName} ({validation}) are set together and "
                + "they are not usable together. The stall mode restores the incumbent's acquire "
                + "exactly, which presents with no wait semaphore, and that is a specification violation the "
                + "validation layer reports on every present. This run keeps both, so expect a synchronisation "
                + "complaint per frame that says nothing about the rest of the backend. It is a documented "
                + "limitation of the A/B switch rather than a defect.";
    }
}
