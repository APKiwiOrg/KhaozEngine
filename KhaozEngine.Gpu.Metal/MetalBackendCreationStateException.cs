using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalBackendCreation refusal.</summary>
public sealed class MetalBackendCreationStateException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalBackendCreationStateException(MetalBackendCreationRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalBackendCreationRefusal Reason { get; }

    /// <summary>Detail associated with the refused operation, when applicable.</summary>
    public string? Detail { get; init; }

    /// <summary>NativeClass associated with the refused operation, when applicable.</summary>
    public string? NativeClass { get; init; }
    /// <summary>The device that could not create a command queue.</summary>
    public string? DeviceName { get; init; }
}
