using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11TimestampAdapter refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class D3D11TimestampAdapterException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public D3D11TimestampAdapterException(D3D11TimestampAdapterRefusal reason, string message, int hResult)
        : base(message)
    {
        Reason = reason;
        HResult = hResult;
    }

    /// <summary>The rule that refused the operation.</summary>
    public D3D11TimestampAdapterRefusal Reason { get; }

    /// <summary>The operation that was refused.</summary>
    public string? Operation { get; init; }
}
