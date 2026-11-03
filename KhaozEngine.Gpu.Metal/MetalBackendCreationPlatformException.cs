using System;

namespace KhaozEngine.Gpu.Metal;

/// <summary>A structured MetalBackendCreation refusal.</summary>
public sealed class MetalBackendCreationPlatformException : PlatformNotSupportedException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public MetalBackendCreationPlatformException(MetalBackendCreationRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public MetalBackendCreationRefusal Reason { get; }

    /// <summary>ResourceKind associated with the refused operation, when applicable.</summary>
    public string? ResourceKind { get; init; }

    /// <summary>RequiredOperatingSystem associated with the refused operation, when applicable.</summary>
    public string? RequiredOperatingSystem { get; init; }

    /// <summary>CreationPath associated with the refused operation, when applicable.</summary>
    public string? CreationPath { get; init; }
}
