using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalBindRecords refusal.</summary>
public sealed class MetalBindRecordsStateException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalBindRecordsStateException(MetalBindRecordsRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalBindRecordsRefusal Reason { get; }

    /// <summary>Set associated with the refused operation, when applicable.</summary>
    public uint? Set { get; init; }

    /// <summary>Binding associated with the refused operation, when applicable.</summary>
    public int? Binding { get; init; }

    /// <summary>Stage associated with the refused operation, when applicable.</summary>
    public GpuShaderStages? Stage { get; init; }

    /// <summary>ExpectedSpace associated with the refused operation, when applicable.</summary>
    public string? ExpectedSpace { get; init; }

    /// <summary>ActualSpace associated with the refused operation, when applicable.</summary>
    public string? ActualSpace { get; init; }

    /// <summary>ArgumentIndex associated with the refused operation, when applicable.</summary>
    public int? ArgumentIndex { get; init; }
}
