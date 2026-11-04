using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuResourceOwnership refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuResourceOwnershipArgumentException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuResourceOwnershipArgumentException(GpuResourceOwnershipRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuResourceOwnershipRefusal Reason { get; }

    /// <summary>The expected backend reported by the refusing validator, when relevant to the reason.</summary>
    public GpuBackendKind? ExpectedBackend { get; init; }

    /// <summary>The resource kind reported by the refusing validator, when relevant to the reason.</summary>
    public string? ResourceKind { get; init; }

    /// <summary>The context reported by the refusing validator, when relevant to the reason.</summary>
    public string? Context { get; init; }

    /// <summary>The actual type reported by the refusing validator, when relevant to the reason.</summary>
    public Type? ActualType { get; init; }

    /// <summary>The expected type reported by the refusing validator, when relevant to the reason.</summary>
    public Type? ExpectedType { get; init; }

    /// <summary>The declared resource-set element name, when the guard has it.</summary>
    public string? ElementName { get; init; }

    /// <summary>The native register letter whose view is absent, when the guard has it.</summary>
    public char? RegisterLetter { get; init; }

    /// <summary>The resource index reported by the refusing validator, when relevant to the reason.</summary>
    public int? ResourceIndex { get; init; }

    /// <summary>The pipeline kind reported by the refusing validator, when relevant to the reason.</summary>
    public string? PipelineKind { get; init; }
}
