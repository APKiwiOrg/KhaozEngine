using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE ONE DEVICE LIMIT DECISION V-D4 SPENDS, AND THE THIRD OF ITS FOUR DEFENCES (8.3).
    ///
    /// <para><b>WHAT IS KNOWN AND WHAT IS NOT.</b> <c>maxDescriptorSetUniformBuffersDynamic</c> has a Vulkan
    /// REQUIRED MINIMUM of 8 across a whole pipeline layout, and required minimums are never lowered across core
    /// versions, so 8 is a floor every conformant device clears. Beyond that floor NOTHING about real device
    /// values is verifiable from this repository, so no claim is made here about what lavapipe, NVIDIA or AMD
    /// report. Both design drafts asserted vendor-specific numbers and neither was checkable.</para>
    ///
    /// <para><b>THE FOUR DEFENCES, IN THE ORDER THEY FIRE.</b>
    /// <list type="number">
    /// <item>Only <see cref="GpuBufferUsage.UniformBuffer"/> buffers are ring-backed
    /// (<see cref="VulkanBufferRingPolicy"/>), so a storage buffer never becomes dynamic and the count can only
    /// ever be the uniform elements.</item>
    /// <item>A DEVICE-FREE TEST computes the dynamic uniform descriptor count for every pipeline the shipped
    /// renderers declare and asserts it stays at or under <see cref="SpecRequiredMinimum"/>, so a layout that
    /// would break a minimum-spec device fails on the free Linux leg rather than on a player's machine. That is
    /// the defence that matters, because it is the only one that fires before anybody runs anything.</item>
    /// <item><see cref="RequirePipelineWithinLimit"/>, called at PIPELINE-LAYOUT creation
    /// (<see cref="VulkanPipelineLayoutCache"/>), which refuses by name above the device's actual limit.</item>
    /// <item><c>IsSupported()</c> reads the limit at probe time
    /// (<see cref="VulkanDeviceRequirements.RequiredDynamicUniformBuffers"/>), so a machine below what the engine
    /// needs answers false and falls back rather than throwing partway into a run.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>AND THE LIMIT IS BECOMING MEASURABLE (V-D7).</b> The CI <c>vulkaninfo</c> step drops
    /// <c>--summary</c>, so the full <c>VkPhysicalDeviceLimits</c> block is dumped on every Vulkan run and the
    /// next design resting on a device limit rests on a number somebody can read.</para>
    /// </summary>
    internal static class VulkanDescriptorLimits
    {
        /// <summary>
        /// Vulkan's REQUIRED MINIMUM for <c>maxDescriptorSetUniformBuffersDynamic</c>, 8. The same constant the
        /// support probe gates on, referenced rather than restated so the probe's bar and the device-free layout
        /// test's bar cannot drift apart.
        /// </summary>
        internal const uint SpecRequiredMinimum = VulkanDeviceRequirements.RequiredDynamicUniformBuffers;

        /// <summary>
        /// The limit to measure against: the device's own reported value, or <see cref="SpecRequiredMinimum"/>
        /// when nothing was read. A zero is treated as "not read" rather than as "this device supports none",
        /// which is the same reading <see cref="VulkanRingStride.AlignmentFor"/> gives an unread alignment: a
        /// device reporting a genuine 0 fails the support probe long before a pipeline layout is created here.
        /// </summary>
        internal static uint EffectiveLimit(uint reportedLimit)
            => reportedLimit == 0 ? SpecRequiredMinimum : reportedLimit;

        /// <summary>
        /// THE COUNTING DEFENCE AT PIPELINE-LAYOUT CREATION. Refuse a pipeline layout whose set layouts spend
        /// more dynamic uniform descriptors between them than the device allows.
        /// <para>
        /// It is checked HERE rather than at set-layout creation because the limit is a PIPELINE LAYOUT limit:
        /// one set layout with two dynamic uniform buffers is legal on every device, and eight pipelines each
        /// combining it differently is where the ceiling is really reached.
        /// </para>
        /// </summary>
        /// <param name="dynamicUniformCount">The sum over the pipeline's set layouts.</param>
        /// <param name="reportedLimit">The device's <c>maxDescriptorSetUniformBuffersDynamic</c>, or 0 when it
        /// was never read.</param>
        /// <param name="setLayoutCount">How many set layouts the pipeline layout carries, for the message.</param>
        /// <exception cref="NotSupportedException">The count is above the limit.</exception>
        internal static void RequirePipelineWithinLimit(int dynamicUniformCount, uint reportedLimit,
            int setLayoutCount)
        {
            if (dynamicUniformCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dynamicUniformCount), dynamicUniformCount,
                    "A pipeline layout cannot spend a negative number of dynamic uniform descriptors.");
            }

            uint limit = EffectiveLimit(reportedLimit);
            if ((ulong)dynamicUniformCount <= limit) return;

            throw new NotSupportedException(
                "A native Vulkan pipeline layout over "
                + setLayoutCount.ToString(CultureInfo.InvariantCulture)
                + " descriptor set layouts needs "
                + dynamicUniformCount.ToString(CultureInfo.InvariantCulture)
                + " dynamic uniform buffer descriptors, and this device reports "
                + "maxDescriptorSetUniformBuffersDynamic = "
                + limit.ToString(CultureInfo.InvariantCulture)
                + ". EVERY uniform buffer in every layout is a dynamic one on this backend (decision V-D4), "
                + "because the per-frame uniform ring's base is applied at bind and the dynamic offset is the "
                + "only bind-time knob Vulkan offers on a uniform buffer, so this count is the pipeline's uniform "
                + "buffer count rather than its declared-dynamic count. Vulkan's required minimum for this limit "
                + "is " + SpecRequiredMinimum.ToString(CultureInfo.InvariantCulture)
                + ", and every pipeline the shipped renderers declare is asserted at or under it by a device-free "
                + "test, so reaching this message means either a new layout combination or a device below the "
                + "required minimum. Split the uniform data into fewer buffers.");
        }
    }
}
