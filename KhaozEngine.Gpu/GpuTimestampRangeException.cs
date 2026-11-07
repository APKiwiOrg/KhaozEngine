using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuTimestamp refusal. Existing ArgumentOutOfRangeException catches remain valid.</summary>
public sealed class GpuTimestampRangeException : ArgumentOutOfRangeException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuTimestampRangeException(GpuTimestampRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuTimestampRefusal Reason { get; }

    /// <summary>The required final timestamp index.</summary>
    public uint? FinalIndex { get; init; }

    /// <summary>The current timestamp batch count.</summary>
    public uint? Count { get; init; }
}
