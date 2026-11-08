using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanBufferBinding refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class VulkanBufferBindingException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public VulkanBufferBindingException(VulkanBufferBindingRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanBufferBindingRefusal Reason { get; }

    /// <summary>The usage reported by the refusing validator, when relevant to the reason.</summary>
    public GpuBufferUsage? Usage { get; init; }

    /// <summary>The required usage reported by the refusing validator, when relevant to the reason.</summary>
    public GpuBufferUsage? RequiredUsage { get; init; }

    /// <summary>The context reported by the refusing validator, when relevant to the reason.</summary>
    public string? Context { get; init; }
}
