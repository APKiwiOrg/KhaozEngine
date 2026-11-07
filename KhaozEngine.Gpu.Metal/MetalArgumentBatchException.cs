using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalArgumentBatch refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class MetalArgumentBatchException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public MetalArgumentBatchException(MetalArgumentBatchRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public MetalArgumentBatchRefusal Reason { get; }

    /// <summary>The duplicated native argument index.</summary>
    public int Index { get; init; }

    /// <summary>The argument space containing the collision.</summary>
    public string? Space { get; init; }

    /// <summary>The shader stage containing the collision.</summary>
    public GpuShaderStages Stage { get; init; }
}
