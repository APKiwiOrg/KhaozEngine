using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11MapResult refusal.</summary>
public sealed class D3D11MapResultException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public D3D11MapResultException(D3D11MapResultRefusal reason, string message, int hResult) : base(message)
    {
        Reason = reason;
        HResult = hResult;
    }

    /// <summary>The rule that refused the operation.</summary>
    public D3D11MapResultRefusal Reason { get; }

    /// <summary>Site associated with the refused operation, when applicable.</summary>
    public string? Site { get; init; }

    /// <summary>ResultToken associated with the refused operation, when applicable.</summary>
    public string? ResultToken { get; init; }

    /// <summary>DeviceLost associated with the refused operation, when applicable.</summary>
    public bool? DeviceLost { get; init; }
}
