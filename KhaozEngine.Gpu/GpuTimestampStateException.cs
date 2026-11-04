using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuTimestamp refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class GpuTimestampStateException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuTimestampStateException(GpuTimestampRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuTimestampRefusal Reason { get; }

    /// <summary>The backend refusing the operation, when known.</summary>
    public GpuBackendKind? Backend { get; init; }

    /// <summary>The operation that was refused.</summary>
    public string? Command { get; init; }

    /// <summary>The current timestamp batch count.</summary>
    public uint? Count { get; init; }

    /// <summary>The number of timestamp indices written.</summary>
    public uint? WrittenCount { get; init; }

    /// <summary>The refused timestamp index.</summary>
    public uint? Index { get; init; }

    /// <summary>The next required timestamp index.</summary>
    public uint? ExpectedIndex { get; init; }

    /// <summary>The required final timestamp index.</summary>
    public uint? FinalIndex { get; init; }

    /// <summary>The recording identity requested by the caller, when available.</summary>
    public ulong? RecordingId { get; init; }
}
