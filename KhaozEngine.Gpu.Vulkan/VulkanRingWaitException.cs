using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanRingWait refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class VulkanRingWaitException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public VulkanRingWaitException(VulkanRingWaitRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanRingWaitRefusal Reason { get; }

    /// <summary>The operation that was refused.</summary>
    public string? Operation { get; init; }
}
