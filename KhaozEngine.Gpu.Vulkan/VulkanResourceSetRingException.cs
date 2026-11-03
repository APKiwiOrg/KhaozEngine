using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanResourceSetRing refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class VulkanResourceSetRingException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public VulkanResourceSetRingException(VulkanResourceSetRingRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanResourceSetRingRefusal Reason { get; }

    /// <summary>The element name reported by the refusing validator, when relevant to the reason.</summary>
    public string? ElementName { get; init; }

    /// <summary>The binding reported by the refusing validator, when relevant to the reason.</summary>
    public int? Binding { get; init; }

    /// <summary>The resource kind reported by the refusing validator, when relevant to the reason.</summary>
    public GpuResourceKind? ResourceKind { get; init; }

    /// <summary>The actual usage reported by the refusing validator, when relevant to the reason.</summary>
    public GpuBufferUsage? ActualUsage { get; init; }
}
