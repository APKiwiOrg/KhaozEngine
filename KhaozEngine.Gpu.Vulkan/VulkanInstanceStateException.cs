using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanInstance refusal.</summary>
public sealed class VulkanInstanceStateException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public VulkanInstanceStateException(VulkanInstanceRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public VulkanInstanceRefusal Reason { get; }

}
