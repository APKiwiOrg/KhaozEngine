using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanDeviceWiring refusal.</summary>
public sealed class VulkanDeviceWiringException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public VulkanDeviceWiringException(VulkanDeviceWiringRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public VulkanDeviceWiringRefusal Reason { get; }

    /// <summary>NativeObjectKind associated with the refused operation, when applicable.</summary>
    public string? NativeObjectKind { get; init; }
}
