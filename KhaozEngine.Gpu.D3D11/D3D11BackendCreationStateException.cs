using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured native backend creation refusal.</summary>
public sealed class D3D11BackendCreationStateException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason.</summary>
    public D3D11BackendCreationStateException(D3D11BackendCreationRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused creation.</summary>
    public D3D11BackendCreationRefusal Reason { get; }

    /// <summary>DeviceReturned captured at the refused native creation operation.</summary>
    public bool? DeviceReturned { get; init; }

    /// <summary>ContextReturned captured at the refused native creation operation.</summary>
    public bool? ContextReturned { get; init; }
}
