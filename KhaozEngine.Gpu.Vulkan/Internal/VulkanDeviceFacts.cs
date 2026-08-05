namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// Everything the support probe reads off ONE physical device, as plain data with no Vulkan handle and no
    /// Silk.NET type anywhere in it.
    /// <para>
    /// The split is what makes the probe's decision testable. Reading these values needs a loader, an instance
    /// and a driver, which no CI leg outside the Vulkan one has and this developer machine has none of at all.
    /// DECIDING on them needs nothing, so the decision lives in <see cref="VulkanDeviceRequirements"/> over this
    /// struct and is driven device-free from fabricated values, one requirement at a time. The same shape the
    /// Direct3D 11 package arrived at from the other direction, where the probe and its two feature reads are
    /// one method because both are trivial.
    /// </para>
    /// <para>
    /// It is a snapshot rather than a live view: the probe destroys its throwaway instance before the decision is
    /// taken, so nothing here may hold anything that dies with it. That is why <see cref="DeviceName"/> is a
    /// managed string copied out of the driver's fixed byte buffer rather than the pointer it came from.
    /// </para>
    /// </summary>
    /// <param name="DeviceName">The driver's own name for the device, for the log line that says which device was
    /// rejected and why. Never null, and "unnamed device" when the driver reports nothing readable.</param>
    /// <param name="ApiVersion">The packed <c>VkPhysicalDeviceProperties.apiVersion</c>, compared against
    /// <see cref="VulkanDeviceRequirements.MinimumApiVersion"/>.</param>
    /// <param name="DynamicRendering">The 1.3 feature bit. The whole rendering path is
    /// <c>vkCmdBeginRendering</c> (V-A1), so there is no render-pass fallback to take.</param>
    /// <param name="Synchronization2">The 1.3 feature bit. Every barrier the tracker emits is
    /// <c>vkCmdPipelineBarrier2</c> (V-F6).</param>
    /// <param name="TimelineSemaphore">The 1.2 feature bit. One device-wide timeline replaces per-submit fences
    /// (V-F1), and <c>IGpuFence</c> is a counter read against it.</param>
    /// <param name="HasCoherentHostVisibleMemoryType">Whether any memory type carries both
    /// <c>HOST_VISIBLE</c> and <c>HOST_COHERENT</c>. The uniform ring is PINNED to one (V-M4) and 9.2's
    /// no-flush-required claim rests on it.</param>
    /// <param name="MaxDescriptorSetUniformBuffersDynamic">The device limit 8.3's fourth defence reads, and the
    /// only one of those four defences that answers for the MACHINE at runtime.</param>
    /// <param name="HasGraphicsQueueFamily">Whether any queue family carries <c>VK_QUEUE_GRAPHICS_BIT</c>. One
    /// graphics queue is the whole queue model (V-N5): no transfer queue and no async compute.</param>
    /// <param name="GraphicsFamilyPresents">Whether that graphics family can present to the target surface. Read
    /// only where a surface exists, which the probe has no way to build (see
    /// <see cref="VulkanDeviceRequirements.MissingRequirement"/>'s <c>presentationRequired</c> parameter).</param>
    internal readonly record struct VulkanDeviceFacts(
        string DeviceName,
        uint ApiVersion,
        bool DynamicRendering,
        bool Synchronization2,
        bool TimelineSemaphore,
        bool HasCoherentHostVisibleMemoryType,
        uint MaxDescriptorSetUniformBuffersDynamic,
        bool HasGraphicsQueueFamily,
        bool GraphicsFamilyPresents);
}
