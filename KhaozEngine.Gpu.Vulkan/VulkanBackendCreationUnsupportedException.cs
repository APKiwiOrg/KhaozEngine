using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanBackendCreation refusal.</summary>
public sealed class VulkanBackendCreationUnsupportedException : NotSupportedException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public VulkanBackendCreationUnsupportedException(VulkanBackendCreationRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Creates a refusal while retaining the native loader failure.</summary>
    public VulkanBackendCreationUnsupportedException(VulkanBackendCreationRefusal reason, string message, Exception inner)
        : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanBackendCreationRefusal Reason { get; }

    /// <summary>WindowKind associated with the refused operation, when applicable.</summary>
    public GpuWindowKind? WindowKind { get; init; }

    /// <summary>RequiredTranslationLayer associated with the refused operation, when applicable.</summary>
    public string? RequiredTranslationLayer { get; init; }

    /// <summary>RequiredExtension associated with the refused operation, when applicable.</summary>
    public string? RequiredExtension { get; init; }

    /// <summary>MissingRequirement associated with the refused operation, when applicable.</summary>
    public string? MissingRequirement { get; init; }

    /// <summary>Probe associated with the refused operation, when applicable.</summary>
    public string? Probe { get; init; }

    /// <summary>DriverPackage associated with the refused operation, when applicable.</summary>
    public string? DriverPackage { get; init; }

    /// <summary>LoaderExceptionType associated with the refused operation, when applicable.</summary>
    public Type? LoaderExceptionType { get; init; }
}
