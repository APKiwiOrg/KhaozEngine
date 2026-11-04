using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuResourceLayout refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuResourceLayoutException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuResourceLayoutException(GpuResourceLayoutRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuResourceLayoutRefusal Reason { get; }

    /// <summary>The backend reported by the refusing validator, when relevant to the reason.</summary>
    public GpuBackendKind? Backend { get; init; }

    /// <summary>The element name reported by the refusing validator, when relevant to the reason.</summary>
    public string? ElementName { get; init; }

    /// <summary>The binding reported by the refusing validator, when relevant to the reason.</summary>
    public int? Binding { get; init; }

    /// <summary>The resource kind reported by the refusing validator, when relevant to the reason.</summary>
    public GpuResourceKind? ResourceKind { get; init; }
}
