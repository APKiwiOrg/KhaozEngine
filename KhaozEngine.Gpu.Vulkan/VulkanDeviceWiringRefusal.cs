namespace KhaozEngine.Gpu.Vulkan;

/// <summary>The rules of the VulkanDeviceWiring refusal family.</summary>
public enum VulkanDeviceWiringRefusal
{
    /// <summary>Refuses a native factory operation on a seam-built device with no VkInstance.</summary>
    InstanceMissing,
}
