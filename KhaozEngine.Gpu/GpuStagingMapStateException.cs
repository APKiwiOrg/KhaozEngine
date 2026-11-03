using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuStagingMap refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class GpuStagingMapStateException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public GpuStagingMapStateException(GpuStagingMapRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuStagingMapRefusal Reason { get; }

    /// <summary>The backend refusing the map state, when the rule is backend-specific.</summary>
    public GpuBackendKind? Backend { get; init; }

    /// <summary>The mode of the open mapping for an already-mapped refusal.</summary>
    public GpuMapMode? ExistingMode { get; init; }

    /// <summary>The requested mode for an already-mapped refusal.</summary>
    public GpuMapMode? RequestedMode { get; init; }

    /// <summary>The fenced-map command whose completion is not signaled.</summary>
    public string? Command { get; init; }
}
