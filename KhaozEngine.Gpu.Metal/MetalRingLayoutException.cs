using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalRingLayout refusal.</summary>
public sealed class MetalRingLayoutException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalRingLayoutException(MetalRingLayoutRefusal reason, string? paramName, object? actualValue, string message) : base(paramName, actualValue, message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalRingLayoutRefusal Reason { get; }

    /// <summary>RangeOffset associated with the refused operation, when applicable.</summary>
    public uint? RangeOffset { get; init; }

    /// <summary>DynamicOffset associated with the refused operation, when applicable.</summary>
    public uint? DynamicOffset { get; init; }

    /// <summary>RangeBytes associated with the refused operation, when applicable.</summary>
    public uint? RangeBytes { get; init; }

    /// <summary>SegmentStride associated with the refused operation, when applicable.</summary>
    public uint? SegmentStride { get; init; }

    /// <summary>RecoveryOperation associated with the refused operation, when applicable.</summary>
    public string? RecoveryOperation { get; init; }

    /// <summary>MinimumDepth associated with the refused operation, when applicable.</summary>
    public int? MinimumDepth { get; init; }

    /// <summary>MaximumDepth associated with the refused operation, when applicable.</summary>
    public int? MaximumDepth { get; init; }

    /// <summary>EnvironmentVariable associated with the refused operation, when applicable.</summary>
    public string? EnvironmentVariable { get; init; }
}
