using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalShaderFunction refusal.</summary>
public sealed class MetalShaderFunctionException : InvalidOperationException
{
    /// <summary>Creates the refusal with its reason.</summary>
    public MetalShaderFunctionException(MetalShaderFunctionRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused this operation.</summary>
    public MetalShaderFunctionRefusal Reason { get; }

    /// <summary>The stage identifying this refusal, when applicable.</summary>
    public GpuShaderStages? Stage { get; init; }

    /// <summary>The resource kind identifying this refusal, when applicable.</summary>
    public string? ResourceKind { get; init; }
}
