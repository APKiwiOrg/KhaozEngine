namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Why VulkanBufferCopy refused an operation.</summary>
public enum VulkanBufferCopyRefusal
{
    /// <summary>Refuses an empty VkBufferCopy region.</summary>
    EmptyCopy,
}
