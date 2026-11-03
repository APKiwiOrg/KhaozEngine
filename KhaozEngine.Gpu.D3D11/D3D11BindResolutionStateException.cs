using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11BindResolution refusal.</summary>
public sealed class D3D11BindResolutionStateException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public D3D11BindResolutionStateException(D3D11BindResolutionRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public D3D11BindResolutionRefusal Reason { get; }

    /// <summary>Command associated with the refused operation, when applicable.</summary>
    public string? Command { get; init; }
}
