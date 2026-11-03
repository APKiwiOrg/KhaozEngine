using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanValidation refusal.</summary>
public sealed class VulkanValidationException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public VulkanValidationException(VulkanValidationRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public VulkanValidationRefusal Reason { get; }

    /// <summary>Site associated with the refused operation, when applicable.</summary>
    public string? Site { get; init; }

    /// <summary>Diagnostic associated with the refused operation, when applicable.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>MessageId associated with the refused operation, when applicable.</summary>
    public int? MessageId { get; init; }

    /// <summary>MessageIdName associated with the refused operation, when applicable.</summary>
    public string? MessageIdName { get; init; }
}
