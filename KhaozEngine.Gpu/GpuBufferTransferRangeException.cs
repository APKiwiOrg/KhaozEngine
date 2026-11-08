using System;

namespace KhaozEngine.Gpu;

/// <summary>A buffer transfer refusal. Existing ArgumentOutOfRangeException catches remain valid.</summary>
public sealed class GpuBufferTransferRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal with its required reason and original parameter data.</summary>
    public GpuBufferTransferRangeException(GpuBufferTransferRefusal reason, string? paramName,
        object? actualValue, string message) : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The range rule that refused the operation.</summary>
    public GpuBufferTransferRefusal Reason { get; }

    /// <summary>The first byte of the refused window.</summary>
    public ulong? OffsetBytes { get; init; }

    /// <summary>The requested window length in bytes.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>The logical size of the buffer in bytes.</summary>
    public ulong? BufferBytes { get; init; }

    /// <summary>The copy side whose window was refused, when the guard has it.</summary>
    public string? Side { get; init; }
}
