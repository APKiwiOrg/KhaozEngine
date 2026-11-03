using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanLayout refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class VulkanLayoutArgumentException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public VulkanLayoutArgumentException(VulkanLayoutRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanLayoutRefusal Reason { get; }

    /// <summary>The image reported by the refusing validator, when relevant to the reason.</summary>
    public ulong? Image { get; init; }
}
