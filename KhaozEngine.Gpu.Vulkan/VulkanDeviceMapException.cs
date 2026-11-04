using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanDeviceMap refusal.</summary>
public sealed class VulkanDeviceMapException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public VulkanDeviceMapException(VulkanDeviceMapRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public VulkanDeviceMapRefusal Reason { get; }

    /// <summary>LossReason associated with the refused operation, when applicable.</summary>
    public string? LossReason { get; init; }

    /// <summary>LossSite associated with the refused operation, when applicable.</summary>
    public string? LossSite { get; init; }

    /// <summary>ResultCode associated with the refused operation, when applicable.</summary>
    public int? ResultCode { get; init; }
}
