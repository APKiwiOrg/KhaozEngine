using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11CompilerEvidence refusal.</summary>
public sealed class D3D11CompilerEvidenceDisposedException : ObjectDisposedException
{
    /// <summary>Creates the refusal retaining the original object name.</summary>
    public D3D11CompilerEvidenceDisposedException(D3D11CompilerEvidenceRefusal reason, string? objectName, string message)
        : base(objectName, message) => Reason = reason;

    /// <summary>The rule that refused this operation.</summary>
    public D3D11CompilerEvidenceRefusal Reason { get; }
}
