using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanBufferRing refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class VulkanBufferRingException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public VulkanBufferRingException(VulkanBufferRingRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanBufferRingRefusal Reason { get; }

    /// <summary>The usage reported by the refusing validator, when relevant to the reason.</summary>
    public GpuBufferUsage? Usage { get; init; }

    /// <summary>The conflicting usage reported by the refusing validator, when relevant to the reason.</summary>
    public GpuBufferUsage? ConflictingUsage { get; init; }
}
