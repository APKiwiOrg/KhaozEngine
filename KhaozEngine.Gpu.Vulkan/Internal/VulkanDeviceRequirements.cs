using System.Globalization;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The DECISION half of the support probe: given what
    /// <see cref="VulkanSupportProbe"/> read off a physical device, whether the engine's native Vulkan backend can
    /// run on it, and if not, a sentence naming the one thing that is missing.
    /// <para>
    /// Pure and device-free on purpose. Section 5.2's four hard requirements plus 4.1's three further reads are
    /// the entire content of work-breakdown row 2, and every one of them is a comparison, so putting them behind a
    /// loader would mean the only machine that could test them is the one machine that already runs the backend.
    /// Driven from fabricated <see cref="VulkanDeviceFacts"/> a test can fail exactly one requirement at a time.
    /// </para>
    /// <para>
    /// The ORDER of the checks is the order of the message a tester reads, so it runs cheapest-and-most-decisive
    /// first: the version floor, then the three feature bits that a 1.3 device carries by definition, then the
    /// three reads that are about this specific driver. A 1.2 machine therefore reads "apiVersion below 1.3"
    /// rather than reading it as three unrelated missing features, which is the same failure phrased three times.
    /// </para>
    /// </summary>
    internal static class VulkanDeviceRequirements
    {
        /// <summary>
        /// The version floor, from the binding's own constant rather than from the packed arithmetic rewritten
        /// here (decision V-N2). A device below it is rejected with its version named, which is what "fails
        /// loudly on a 1.2 machine instead of crashing on frame one" means in practice: the three feature bits
        /// below are mandatory on a 1.3 device, so on a conformant driver this is the only version-shaped
        /// requirement that can ever fail.
        /// </summary>
        internal static readonly uint MinimumApiVersion = Vk.Version13;

        /// <summary>
        /// The dynamic-uniform descriptor count the engine's layouts are allowed to spend across one pipeline
        /// layout, which is 8.3's fourth defence and the only one of that section's four defences that answers
        /// FOR THE MACHINE at runtime.
        /// <para>
        /// It is Vulkan's own REQUIRED MINIMUM for <c>maxDescriptorSetUniformBuffersDynamic</c>, used here as the
        /// engine's budget rather than as a measured demand, and that is deliberate in both directions. No
        /// conformant device can fail this check, so the probe never rejects a machine that could have run the
        /// backend. And the engine can never need more, because 8.3's SECOND defence is a device-free test over
        /// every shipped pipeline asserting the count stays at or under this same number, landing with the
        /// descriptor row (https://github.com/APKiwiOrg/KhaozEngine/issues/520). Tightening this to the real
        /// per-pipeline maximum would only ever reject fewer devices, so it is the safe direction to be
        /// conservative in and there is nothing to revisit unless that test's bound moves.
        /// </para>
        /// </summary>
        internal const uint RequiredDynamicUniformBuffers = 8;

        /// <summary>
        /// Null when <paramref name="facts"/> describe a device the backend can run on, or one sentence naming
        /// what is missing, phrased for a log line a tester will read. Never an empty string, so null is the only
        /// "yes".
        /// </summary>
        /// <param name="facts">What the probe read off one physical device.</param>
        /// <param name="presentationRequired">
        /// Whether the graphics family must also PRESENT (V-N5), which is a windowed-path requirement and is
        /// false for the headless path. The probe passes false, and not because the requirement is optional: it
        /// answers <c>IGpuBackendProvider.IsSupported()</c>, which receives no window, and
        /// <c>vkGetPhysicalDeviceSurfaceSupportKHR</c> needs a <c>VkSurfaceKHR</c> that cannot exist without one.
        /// Building a surface inside the probe would also mean enabling a platform surface extension on the
        /// headless path, which V-N6 forbids outright. So the windowed clause is evaluated where a surface
        /// actually exists, at swapchain creation
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527), against this same method with the flag set. It
        /// lives here rather than there so both paths make the decision once, in one place, and so the rejection
        /// a windowed run gets is worded like every other one.
        /// </param>
        internal static string? MissingRequirement(in VulkanDeviceFacts facts, bool presentationRequired)
        {
            if (facts.ApiVersion < MinimumApiVersion)
            {
                return $"its Vulkan apiVersion is {FormatApiVersion(facts.ApiVersion)}, below the 1.3 floor this "
                    + "backend is built on (dynamic rendering, synchronization2 and timeline semaphores are all "
                    + "1.3 core here rather than extensions)";
            }

            if (!facts.DynamicRendering)
            {
                return "it reports no dynamicRendering, which the whole rendering path is: there is no VkRenderPass "
                    + "and no VkFramebuffer anywhere in this backend to fall back to";
            }

            if (!facts.Synchronization2)
            {
                return "it reports no synchronization2, which every barrier the layout tracker emits is "
                    + "(vkCmdPipelineBarrier2 with explicit stage and access masks)";
            }

            if (!facts.TimelineSemaphore)
            {
                return "it reports no timelineSemaphore, which the device's one monotonic timeline is: every "
                    + "submit signals it, every fence is a target on it, and WaitForIdle is a host wait on it";
            }

            if (!facts.HasCoherentHostVisibleMemoryType)
            {
                return "it exposes no HOST_VISIBLE and HOST_COHERENT memory type, which the per-frame uniform ring "
                    + "is pinned to: the ring writes segments from the CPU and submits with no flush, and that is "
                    + "only correct on coherent memory";
            }

            if (facts.MaxDescriptorSetUniformBuffersDynamic < RequiredDynamicUniformBuffers)
            {
                string reported = facts.MaxDescriptorSetUniformBuffersDynamic.ToString(CultureInfo.InvariantCulture);
                string needed = RequiredDynamicUniformBuffers.ToString(CultureInfo.InvariantCulture);
                return $"it reports maxDescriptorSetUniformBuffersDynamic = {reported}, below the {needed} the "
                    + "engine's pipeline layouts spend (every ring-backed uniform buffer binds as "
                    + "UNIFORM_BUFFER_DYNAMIC so the per-frame ring base can be applied at bind time). Vulkan's "
                    + $"own required minimum for this limit is {needed}, so a device below it is below spec";
            }

            if (!facts.HasGraphicsQueueFamily)
            {
                return "it exposes no queue family with VK_QUEUE_GRAPHICS_BIT, and one graphics queue is the "
                    + "entire queue model here";
            }

            if (presentationRequired && !facts.GraphicsFamilyPresents)
            {
                return "its graphics queue family cannot present to this surface. A separate present family is "
                    + "deliberately not supported (V-N5): the cross-family ownership transfer it needs has no "
                    + "device anyone can produce to test it on, so this device routes through the reported "
                    + "fallback instead";
            }

            return null;
        }

        /// <summary>
        /// A packed Vulkan version as <c>major.minor.patch</c>, for a message a human reads. Goes through the
        /// binding's own <see cref="Version32"/> rather than re-deriving the bit layout, because the variant
        /// field 1.2 added to the encoding is exactly the kind of detail a hand-rolled shift gets wrong once and
        /// then prints wrong forever.
        /// </summary>
        internal static string FormatApiVersion(uint packed)
        {
            var version = (Version32)packed;
            return version.Major.ToString(CultureInfo.InvariantCulture) + "."
                + version.Minor.ToString(CultureInfo.InvariantCulture) + "."
                + version.Patch.ToString(CultureInfo.InvariantCulture);
        }
    }
}
