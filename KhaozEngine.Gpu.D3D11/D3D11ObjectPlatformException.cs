using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11ObjectPlatform refusal.</summary>
public sealed class D3D11ObjectPlatformException : PlatformNotSupportedException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public D3D11ObjectPlatformException(D3D11ObjectPlatformRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public D3D11ObjectPlatformRefusal Reason { get; }

    /// <summary>ObjectKind associated with the refused operation, when applicable.</summary>
    public string? ObjectKind { get; init; }

    /// <summary>RequiredOperatingSystem associated with the refused operation, when applicable.</summary>
    public string? RequiredOperatingSystem { get; init; }

    /// <summary>SupportProbe associated with the refused operation, when applicable.</summary>
    public string? SupportProbe { get; init; }
}
