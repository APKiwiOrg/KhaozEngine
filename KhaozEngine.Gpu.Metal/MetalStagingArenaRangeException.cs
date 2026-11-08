using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalStagingArena refusal. Existing ArgumentOutOfRangeException catches remain valid.</summary>
public sealed class MetalStagingArenaRangeException : ArgumentOutOfRangeException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public MetalStagingArenaRangeException(MetalStagingArenaRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public MetalStagingArenaRefusal Reason { get; }

    /// <summary>The required native copy alignment.</summary>
    public ulong AlignmentBytes { get; init; }

    /// <summary>The operation that rounds a payload to the required lease size.</summary>
    public string? RecoveryOperation { get; init; }
}
