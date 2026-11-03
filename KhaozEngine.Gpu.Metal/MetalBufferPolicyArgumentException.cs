using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalBufferPolicy refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class MetalBufferPolicyArgumentException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public MetalBufferPolicyArgumentException(MetalBufferPolicyRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public MetalBufferPolicyRefusal Reason { get; }

    /// <summary>The refused buffer usage combination.</summary>
    public GpuBufferUsage Usage { get; init; }
}
