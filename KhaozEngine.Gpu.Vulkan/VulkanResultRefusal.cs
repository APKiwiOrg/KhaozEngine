namespace KhaozEngine.Gpu.Vulkan;

/// <summary>The rules of the VulkanResult refusal family.</summary>
public enum VulkanResultRefusal
{
    /// <summary>Refuses a native Vulkan call returning a negative result code.</summary>
    NativeCallFailed,
}
