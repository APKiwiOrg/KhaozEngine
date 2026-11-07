namespace KhaozEngine.Gpu.Vulkan;

/// <summary>The rules of the VulkanDeviceMap refusal family.</summary>
public enum VulkanDeviceMapRefusal
{
    /// <summary>Refuses mapping a dead device whose token has no latched driver loss.</summary>
    DeadWithoutLossRecord,
    /// <summary>Refuses mapping after a native device loss was latched.</summary>
    LossLatched,
}
