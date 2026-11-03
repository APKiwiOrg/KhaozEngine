using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanDescriptorLimit refusal. Existing NotSupportedException catches remain valid.</summary>
public sealed class VulkanDescriptorLimitException : NotSupportedException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public VulkanDescriptorLimitException(VulkanDescriptorLimitRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanDescriptorLimitRefusal Reason { get; }

    /// <summary>The limit name reported by the refusing validator, when relevant to the reason.</summary>
    public string? LimitName { get; init; }

    /// <summary>The requested count reported by the refusing validator, when relevant to the reason.</summary>
    public int? RequestedCount { get; init; }

    /// <summary>The limit reported by the refusing validator, when relevant to the reason.</summary>
    public uint? Limit { get; init; }

    /// <summary>The set layout count reported by the refusing validator, when relevant to the reason.</summary>
    public int? SetLayoutCount { get; init; }
}
