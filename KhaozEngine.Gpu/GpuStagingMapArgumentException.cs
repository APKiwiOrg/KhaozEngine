using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuStagingMap refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuStagingMapArgumentException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public GpuStagingMapArgumentException(GpuStagingMapRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuStagingMapRefusal Reason { get; }

    /// <summary>The backend refusing the resource.</summary>
    public GpuBackendKind Backend { get; init; }

    /// <summary>The runtime resource type, when supplied by the validator.</summary>
    public Type? ActualType { get; init; }

    /// <summary>The kind of resource being mapped, read or written.</summary>
    public string? ResourceKind { get; init; }

    /// <summary>The required buffer usage for a buffer staging refusal.</summary>
    public GpuBufferUsage? RequiredBufferUsage { get; init; }

    /// <summary>The required texture usage for a texture staging refusal.</summary>
    public GpuTextureUsage? RequiredTextureUsage { get; init; }
}
