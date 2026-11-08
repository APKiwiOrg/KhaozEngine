using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11BackendCreation refusal.</summary>
public sealed class D3D11BackendCreationArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public D3D11BackendCreationArgumentException(D3D11BackendCreationRefusal reason, string message, string? paramName) : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public D3D11BackendCreationRefusal Reason { get; }

    /// <summary>ActualWindowKind associated with the refused operation, when applicable.</summary>
    public GpuWindowKind? ActualWindowKind { get; init; }

    /// <summary>RequiredWindowKind associated with the refused operation, when applicable.</summary>
    public GpuWindowKind? RequiredWindowKind { get; init; }
}
