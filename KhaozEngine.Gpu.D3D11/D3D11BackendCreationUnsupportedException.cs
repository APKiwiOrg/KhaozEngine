using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured native backend creation refusal.</summary>
public sealed class D3D11BackendCreationUnsupportedException : NotSupportedException
{
    /// <summary>Creates a refusal with its required reason.</summary>
    public D3D11BackendCreationUnsupportedException(D3D11BackendCreationRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused creation.</summary>
    public D3D11BackendCreationRefusal Reason { get; }

    /// <summary>RequiredInterface captured at the refused native creation operation.</summary>
    public string? RequiredInterface { get; init; }
}
