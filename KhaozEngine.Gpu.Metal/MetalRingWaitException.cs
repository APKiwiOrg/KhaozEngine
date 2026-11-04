using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalRingWait refusal.</summary>
public sealed class MetalRingWaitException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalRingWaitException(MetalRingWaitRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalRingWaitRefusal Reason { get; }

    /// <summary>Operation associated with the refused operation, when applicable.</summary>
    public string? Operation { get; init; }
}
