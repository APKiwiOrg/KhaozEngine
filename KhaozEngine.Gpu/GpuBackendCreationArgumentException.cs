using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured refusal of backend creation arguments.</summary>
public sealed class GpuBackendCreationArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason and original parameter data.</summary>
    public GpuBackendCreationArgumentException(GpuBackendCreationRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The shared creation rule that refused the arguments.</summary>
    public GpuBackendCreationRefusal Reason { get; }

    /// <summary>The backend whose provider refused the settings.</summary>
    public GpuBackendKind? ExpectedBackend { get; init; }

    /// <summary>The supplied diagnostic settings type, or null when no settings were supplied.</summary>
    public Type? ActualType { get; init; }
}
