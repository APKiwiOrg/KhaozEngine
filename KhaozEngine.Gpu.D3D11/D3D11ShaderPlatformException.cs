using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11ShaderPlatform refusal.</summary>
public sealed class D3D11ShaderPlatformException : PlatformNotSupportedException
{
    /// <summary>Creates the refusal with its reason.</summary>
    public D3D11ShaderPlatformException(D3D11ShaderPlatformRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused this operation.</summary>
    public D3D11ShaderPlatformRefusal Reason { get; }

    /// <summary>The compiler identifying this refusal, when applicable.</summary>
    public string? Compiler { get; init; }

    /// <summary>The required operating system identifying this refusal, when applicable.</summary>
    public string? RequiredOperatingSystem { get; init; }

    /// <summary>The support probe identifying this refusal, when applicable.</summary>
    public string? SupportProbe { get; init; }

    /// <summary>The portable validator identifying this refusal, when applicable.</summary>
    public string? PortableValidator { get; init; }
}
