using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11WaitPolicy refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class D3D11WaitPolicyException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public D3D11WaitPolicyException(D3D11WaitPolicyRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public D3D11WaitPolicyRefusal Reason { get; }

    /// <summary>The operation that was refused.</summary>
    public string? Operation { get; init; }
}
