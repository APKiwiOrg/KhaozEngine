namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Why VulkanDescriptorLimit refused an operation.</summary>
public enum VulkanDescriptorLimitRefusal
{
    /// <summary>Refuses a pipeline layout exceeding the device dynamic uniform descriptor limit.</summary>
    DynamicUniformLimitExceeded,
}
