using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanResult refusal.</summary>
public sealed class VulkanResultException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public VulkanResultException(VulkanResultRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public VulkanResultRefusal Reason { get; }

    /// <summary>Call associated with the refused operation, when applicable.</summary>
    public string? Call { get; init; }

    /// <summary>ResultCode associated with the refused operation, when applicable.</summary>
    public int? ResultCode { get; init; }

    /// <summary>ResultToken associated with the refused operation, when applicable.</summary>
    public string? ResultToken { get; init; }
}
