namespace KhaozEngine.Gpu.Vulkan;

/// <summary>Why VulkanBufferRing refused an operation.</summary>
public enum VulkanBufferRingRefusal
{
    /// <summary>Refuses combining a Vulkan ring-backed uniform buffer with another binding usage.</summary>
    UniformUsageCombination,
}
