using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalEncoder refusal.</summary>
public sealed class MetalEncoderStateException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalEncoderStateException(MetalEncoderRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalEncoderRefusal Reason { get; }

    /// <summary>EncoderKind associated with the refused operation, when applicable.</summary>
    public string? EncoderKind { get; init; }

    /// <summary>Command associated with the refused operation, when applicable.</summary>
    public string? Command { get; init; }
}
