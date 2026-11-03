using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalBackendCreation refusal.</summary>
public sealed class MetalBackendCreationArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalBackendCreationArgumentException(MetalBackendCreationRefusal reason, string message, string? paramName) : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalBackendCreationRefusal Reason { get; }

    /// <summary>WindowKind associated with the refused operation, when applicable.</summary>
    public GpuWindowKind? WindowKind { get; init; }

    /// <summary>ActualType associated with the refused operation, when applicable.</summary>
    public Type? ActualType { get; init; }

    /// <summary>ActualWindowKind associated with the refused operation, when applicable.</summary>
    public GpuWindowKind? ActualWindowKind { get; init; }

    /// <summary>RequiredWindowKind associated with the refused operation, when applicable.</summary>
    public GpuWindowKind? RequiredWindowKind { get; init; }
}
