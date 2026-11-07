using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuTimestamp refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuTimestampArgumentException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuTimestampArgumentException(GpuTimestampRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuTimestampRefusal Reason { get; }

    /// <summary>The backend refusing the operation, when known.</summary>
    public GpuBackendKind? Backend { get; init; }

    /// <summary>The actual object type, when supplied by the validator.</summary>
    public Type? ActualType { get; init; }

    /// <summary>The supplied output span length.</summary>
    public int? SuppliedCount { get; init; }

    /// <summary>The required output span length.</summary>
    public uint? RequiredCount { get; init; }
}
