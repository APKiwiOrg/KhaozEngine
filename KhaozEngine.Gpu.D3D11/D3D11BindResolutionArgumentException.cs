using System;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>A structured D3D11BindResolution refusal.</summary>
public sealed class D3D11BindResolutionArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its required reason and existing diagnostic data.</summary>
    public D3D11BindResolutionArgumentException(D3D11BindResolutionRefusal reason, string message, string? paramName) : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the operation.</summary>
    public D3D11BindResolutionRefusal Reason { get; }

    /// <summary>ResourceType associated with the refused operation, when applicable.</summary>
    public Type? ResourceType { get; init; }

    /// <summary>RequiredInterface associated with the refused operation, when applicable.</summary>
    public Type? RequiredInterface { get; init; }

    /// <summary>RegisterIndex associated with the refused operation, when applicable.</summary>
    public int? RegisterIndex { get; init; }

    /// <summary>FirstConstant associated with the refused operation, when applicable.</summary>
    public uint? FirstConstant { get; init; }

    /// <summary>ConstantCount associated with the refused operation, when applicable.</summary>
    public uint? ConstantCount { get; init; }

    /// <summary>SuppliedCount associated with the refused operation, when applicable.</summary>
    public int? SuppliedCount { get; init; }

    /// <summary>RequiredCount associated with the refused operation, when applicable.</summary>
    public int? RequiredCount { get; init; }

    /// <summary>RegisterLetter associated with the refused operation, when applicable.</summary>
    public char? RegisterLetter { get; init; }
}
