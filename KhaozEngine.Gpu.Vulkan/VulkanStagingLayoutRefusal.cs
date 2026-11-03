namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Why VulkanStagingLayout refused an operation.</summary>
public enum VulkanStagingLayoutRefusal
{
    /// <summary>Refuses staging layout arithmetic exceeding the mapping 32-bit size contract.</summary>
    MappingSizeExceeded,
}
