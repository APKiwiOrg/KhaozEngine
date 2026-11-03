using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanVertexInput refusal.</summary>
public sealed class VulkanVertexInputException : ArgumentException
{
    /// <summary>Creates the refusal retaining the original parameter.</summary>
    public VulkanVertexInputException(VulkanVertexInputRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused this operation.</summary>
    public VulkanVertexInputRefusal Reason { get; }

    /// <summary>The instance step rate identifying this refusal, when applicable.</summary>
    public uint? InstanceStepRate { get; init; }

    /// <summary>The slot identifying this refusal, when applicable.</summary>
    public int? Slot { get; init; }

    /// <summary>The maximum step rate identifying this refusal, when applicable.</summary>
    public uint? MaximumStepRate { get; init; }
}
