using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalTimestampAdapter refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class MetalTimestampAdapterStateException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public MetalTimestampAdapterStateException(MetalTimestampAdapterRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public MetalTimestampAdapterRefusal Reason { get; }

    /// <summary>The native timestamp resource or encoder that was unavailable.</summary>
    public string? ResourceKind { get; init; }
}
