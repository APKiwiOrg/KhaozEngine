using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured GpuPipelineOutputs refusal. Existing ArgumentException catches remain valid.</summary>
public sealed class GpuPipelineOutputsException : ArgumentException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public GpuPipelineOutputsException(GpuPipelineOutputsRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public GpuPipelineOutputsRefusal Reason { get; }

    /// <summary>The declared blend count reported by the refusing validator, when relevant to the reason.</summary>
    public int? DeclaredBlendCount { get; init; }

    /// <summary>The colour output count reported by the refusing validator, when relevant to the reason.</summary>
    public int? ColourOutputCount { get; init; }

    /// <summary>The pipeline kind reported by the refusing validator, when relevant to the reason.</summary>
    public string? PipelineKind { get; init; }
}
