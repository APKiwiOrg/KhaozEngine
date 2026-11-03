using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuCopyAlignment refusal. Existing ArgumentOutOfRangeException catches remain valid.</summary>
public sealed class GpuCopyAlignmentException : ArgumentOutOfRangeException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public GpuCopyAlignmentException(GpuCopyAlignmentRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuCopyAlignmentRefusal Reason { get; }

    /// <summary>The operation and backend context that refused the offset.</summary>
    public string? Context { get; init; }

    /// <summary>The source or destination side carrying the offset.</summary>
    public string? Side { get; init; }

    /// <summary>The required byte alignment.</summary>
    public uint AlignmentBytes { get; init; }

    /// <summary>The upload payload size, when the caller supplied one.</summary>
    public uint? PayloadBytes { get; init; }

    /// <summary>The device-level upload operation that accepts an unaligned write.</summary>
    public string? RecoveryOperation { get; init; }
}
