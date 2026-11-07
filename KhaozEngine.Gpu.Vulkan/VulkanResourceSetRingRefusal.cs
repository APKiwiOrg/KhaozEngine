namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Why VulkanResourceSetRing refused an operation.</summary>
public enum VulkanResourceSetRingRefusal
{
    /// <summary>Refuses a ring-backed uniform buffer through a storage binding that cannot apply its frame base.</summary>
    RingBufferUsedAsStorage,
    /// <summary>Refuses a Vulkan uniform binding without its required ring.</summary>
    UniformBufferNotRingBacked,
}
