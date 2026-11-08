using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuResourceOwnership refusal. Existing ObjectDisposedException catches remain valid.</summary>
public sealed class GpuResourceOwnershipDisposedException : ObjectDisposedException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuResourceOwnershipDisposedException(GpuResourceOwnershipRefusal reason, string? objectName, string message)
        : base(objectName, message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuResourceOwnershipRefusal Reason { get; }

    /// <summary>The native handle kind reported by the refusing validator, when relevant to the reason.</summary>
    public string? NativeHandleKind { get; init; }

    /// <summary>The resource kind reported by the refusing validator, when relevant to the reason.</summary>
    public string? ResourceKind { get; init; }

    /// <summary>The context reported by the refusing validator, when relevant to the reason.</summary>
    public string? Context { get; init; }
}
