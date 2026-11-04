using System;

namespace KhaozEngine.Gpu;

/// <summary>A timestamp lifetime refusal. Existing ObjectDisposedException catches remain valid.</summary>
public sealed class GpuTimestampDisposedException : ObjectDisposedException
{
    /// <summary>Creates a refusal with its required reason and original lifetime data.</summary>
    public GpuTimestampDisposedException(GpuTimestampRefusal reason, string? objectName, string message)
        : base(objectName, message) => Reason = reason;

    /// <summary>The timestamp rule that refused the operation.</summary>
    public GpuTimestampRefusal Reason { get; }

    /// <summary>The backend whose timestamp device is unavailable.</summary>
    public GpuBackendKind Backend { get; init; }
}
