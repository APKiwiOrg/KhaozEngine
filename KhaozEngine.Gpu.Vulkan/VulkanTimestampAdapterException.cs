using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanTimestampAdapter refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class VulkanTimestampAdapterException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public VulkanTimestampAdapterException(VulkanTimestampAdapterRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanTimestampAdapterRefusal Reason { get; }

    /// <summary>The native call site that reported device loss.</summary>
    public string? Site { get; init; }
}
