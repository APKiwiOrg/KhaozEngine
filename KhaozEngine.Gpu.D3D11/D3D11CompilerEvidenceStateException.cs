using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11CompilerEvidence refusal.</summary>
public sealed class D3D11CompilerEvidenceStateException : InvalidOperationException
{
    /// <summary>Creates the refusal with its reason.</summary>
    public D3D11CompilerEvidenceStateException(D3D11CompilerEvidenceRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused this operation.</summary>
    public D3D11CompilerEvidenceRefusal Reason { get; }

    /// <summary>The field identifying this refusal, when applicable.</summary>
    public string? Field { get; init; }

    /// <summary>The maximum utf8 bytes identifying this refusal, when applicable.</summary>
    public int? MaximumUtf8Bytes { get; init; }

    /// <summary>The actual utf8 bytes identifying this refusal, when applicable.</summary>
    public int? ActualUtf8Bytes { get; init; }

    /// <summary>The requested profile identifying this refusal, when applicable.</summary>
    public GpuPipelineCompilerReadRequest? RequestedProfile { get; init; }

    /// <summary>The required profile identifying this refusal, when applicable.</summary>
    public GpuPipelineCompilerReadRequest? RequiredProfile { get; init; }

    /// <summary>The missing stages identifying this refusal, when applicable.</summary>
    public GpuShaderStages? MissingStages { get; init; }
}
