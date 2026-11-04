using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuTimestamp refusal. Existing NotSupportedException catches remain valid.</summary>
public sealed class GpuTimestampUnsupportedException : NotSupportedException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuTimestampUnsupportedException(GpuTimestampRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>Construct a refusal retaining the underlying adapter failure.</summary>
    public GpuTimestampUnsupportedException(GpuTimestampRefusal reason, string message, Exception inner)
        : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuTimestampRefusal Reason { get; }

    /// <summary>The backend refusing the operation, when known.</summary>
    public GpuBackendKind? Backend { get; init; }

    /// <summary>The operation that was refused.</summary>
    public string? Command { get; init; }

    /// <summary>The adapter capability detail, when available.</summary>
    public string? Detail { get; init; }
}
