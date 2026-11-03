using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalBindRecords refusal.</summary>
public sealed class MetalBindRecordsRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalBindRecordsRangeException(MetalBindRecordsRefusal reason, string? paramName, object? actualValue, string message) : base(paramName, actualValue, message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalBindRecordsRefusal Reason { get; }

    /// <summary>ComposedOffset associated with the refused operation, when applicable.</summary>
    public ulong? ComposedOffset { get; init; }

    /// <summary>RingBase associated with the refused operation, when applicable.</summary>
    public ulong? RingBase { get; init; }

    /// <summary>RangeOffset associated with the refused operation, when applicable.</summary>
    public uint? RangeOffset { get; init; }

    /// <summary>DynamicOffset associated with the refused operation, when applicable.</summary>
    public uint? DynamicOffset { get; init; }

    /// <summary>AlignmentBytes associated with the refused operation, when applicable.</summary>
    public uint? AlignmentBytes { get; init; }
}
