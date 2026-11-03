using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalBufferPolicy refusal. Existing ArgumentOutOfRangeException catches remain valid.</summary>
public sealed class MetalBufferPolicyRangeException : ArgumentOutOfRangeException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public MetalBufferPolicyRangeException(MetalBufferPolicyRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public MetalBufferPolicyRefusal Reason { get; }

    /// <summary>The first byte of the refused write.</summary>
    public uint OffsetBytes { get; init; }

    /// <summary>The refused write length.</summary>
    public uint WriteBytes { get; init; }

    /// <summary>The logical buffer capacity.</summary>
    public uint BufferBytes { get; init; }
}
