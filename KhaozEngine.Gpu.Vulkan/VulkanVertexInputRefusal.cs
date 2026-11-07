namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Why VulkanVertexInput refused an operation.</summary>
public enum VulkanVertexInputRefusal
{
    /// <summary>Refuses an instance divisor that the enabled Vulkan core vertex input cannot express.</summary>
    InstanceDivisorUnsupported,
}
