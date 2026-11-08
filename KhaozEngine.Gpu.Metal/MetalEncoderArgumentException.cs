using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalEncoder refusal.</summary>
public sealed class MetalEncoderArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalEncoderArgumentException(MetalEncoderRefusal reason, string message, string? paramName) : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalEncoderRefusal Reason { get; }

    /// <summary>NativeDescriptorKind associated with the refused operation, when applicable.</summary>
    public string? NativeDescriptorKind { get; init; }
}
