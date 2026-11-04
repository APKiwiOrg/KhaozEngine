namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Why VulkanBufferBinding refused an operation.</summary>
public enum VulkanBufferBindingRefusal
{
    /// <summary>Refuses binding a Vulkan buffer without the required native usage declaration.</summary>
    RequiredUsageMissing,
}
