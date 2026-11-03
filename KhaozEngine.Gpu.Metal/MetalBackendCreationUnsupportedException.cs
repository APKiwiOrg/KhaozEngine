using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalBackendCreation refusal.</summary>
public sealed class MetalBackendCreationUnsupportedException : NotSupportedException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalBackendCreationUnsupportedException(MetalBackendCreationRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalBackendCreationRefusal Reason { get; }

    /// <summary>MissingRequirement associated with the refused operation, when applicable.</summary>
    public string? MissingRequirement { get; init; }

    /// <summary>Backend associated with the refused operation, when applicable.</summary>
    public GpuBackendKind? Backend { get; init; }
}
