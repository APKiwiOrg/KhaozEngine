using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11BackendCreation refusal.</summary>
public sealed class D3D11BackendCreationPlatformException : PlatformNotSupportedException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public D3D11BackendCreationPlatformException(D3D11BackendCreationRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public D3D11BackendCreationRefusal Reason { get; }

    /// <summary>Backend associated with the refused operation, when applicable.</summary>
    public GpuBackendKind? Backend { get; init; }

    /// <summary>CreationPath associated with the refused operation, when applicable.</summary>
    public string? CreationPath { get; init; }

    /// <summary>RequiredOperatingSystem associated with the refused operation, when applicable.</summary>
    public string? RequiredOperatingSystem { get; init; }

    /// <summary>SupportProbe associated with the refused operation, when applicable.</summary>
    public string? SupportProbe { get; init; }
}
