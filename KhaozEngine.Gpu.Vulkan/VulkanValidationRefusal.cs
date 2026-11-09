namespace KhaozEngine.Gpu.Vulkan;

/// <summary>The rules of the VulkanValidation refusal family.</summary>
public enum VulkanValidationRefusal
{
    /// <summary>Refuses continuing a strict Vulkan run after its first validation error.</summary>
    StrictErrorLatched,

    /// <summary>Refuses validation-enabled CI instance creation without a debug-utils messenger.</summary>
    MessengerUnavailable,
}
