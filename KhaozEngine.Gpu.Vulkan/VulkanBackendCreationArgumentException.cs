using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanBackendCreation refusal.</summary>
public sealed class VulkanBackendCreationArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public VulkanBackendCreationArgumentException(VulkanBackendCreationRefusal reason, string message, string? paramName) : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public VulkanBackendCreationRefusal Reason { get; }

    /// <summary>ActualType associated with the refused operation, when applicable.</summary>
    public Type? ActualType { get; init; }
}
