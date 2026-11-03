namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Why VulkanBufferCopy refused an operation.</summary>
public enum VulkanBufferCopyRefusal
{
    /// <summary>Refuses an empty VkBufferCopy region.</summary>
    EmptyCopy,
    /// <summary>Refuses a Vulkan buffer copy window extending outside its selected buffer.</summary>
    WindowOutsideBuffer,
}
