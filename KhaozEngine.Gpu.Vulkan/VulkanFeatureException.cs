using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanFeature refusal.</summary>
public sealed class VulkanFeatureException : NotSupportedException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public VulkanFeatureException(VulkanFeatureRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public VulkanFeatureRefusal Reason { get; }

    /// <summary>FeatureName associated with the refused operation, when applicable.</summary>
    public string? FeatureName { get; init; }

    /// <summary>DeviceName associated with the refused operation, when applicable.</summary>
    public string? DeviceName { get; init; }
}
