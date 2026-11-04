using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanInstance refusal.</summary>
public sealed class VulkanInstanceUnsupportedException : NotSupportedException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public VulkanInstanceUnsupportedException(VulkanInstanceRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public VulkanInstanceRefusal Reason { get; }

    /// <summary>LiveWindowed associated with the refused operation, when applicable.</summary>
    public bool? LiveWindowed { get; init; }

    /// <summary>LiveWindowKind associated with the refused operation, when applicable.</summary>
    public GpuWindowKind? LiveWindowKind { get; init; }

    /// <summary>LiveValidationMode associated with the refused operation, when applicable.</summary>
    public int? LiveValidationMode { get; init; }

    /// <summary>RequestedWindowed associated with the refused operation, when applicable.</summary>
    public bool? RequestedWindowed { get; init; }

    /// <summary>RequestedWindowKind associated with the refused operation, when applicable.</summary>
    public GpuWindowKind? RequestedWindowKind { get; init; }

    /// <summary>RequestedValidationMode associated with the refused operation, when applicable.</summary>
    public int? RequestedValidationMode { get; init; }
}
