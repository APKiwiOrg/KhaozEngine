using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalTimestampAdapter refusal. Existing NotSupportedException catches remain valid.</summary>
public sealed class MetalTimestampAdapterUnsupportedException : NotSupportedException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public MetalTimestampAdapterUnsupportedException(MetalTimestampAdapterRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>Construct a refusal retaining the underlying adapter failure.</summary>
    public MetalTimestampAdapterUnsupportedException(MetalTimestampAdapterRefusal reason, string message, Exception inner)
        : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public MetalTimestampAdapterRefusal Reason { get; }

    /// <summary>Whether the caller requested a stage sampling boundary.</summary>
    public bool? StageBoundary { get; init; }
}
