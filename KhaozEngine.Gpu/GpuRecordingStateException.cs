using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuRecordingState refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class GpuRecordingStateException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuRecordingStateException(GpuRecordingStateRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuRecordingStateRefusal Reason { get; }

    /// <summary>The backend refusing the operation, when known.</summary>
    public GpuBackendKind? Backend { get; init; }

    /// <summary>The operation that was refused.</summary>
    public string? Command { get; init; }
}
