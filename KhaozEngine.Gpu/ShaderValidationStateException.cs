using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured ShaderValidation refusal.</summary>
public sealed class ShaderValidationStateException : InvalidOperationException
{
    /// <summary>Creates the refusal with its reason.</summary>
    public ShaderValidationStateException(ShaderValidationRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused this operation.</summary>
    public ShaderValidationRefusal Reason { get; }

    /// <summary>The native object kind identifying this refusal, when applicable.</summary>
    public string? NativeObjectKind { get; init; }

    /// <summary>The status identifying this refusal, when applicable.</summary>
    public int? Status { get; init; }

    /// <summary>The diagnostic identifying this refusal, when applicable.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>The native operation identifying this refusal, when applicable.</summary>
    public string? NativeOperation { get; init; }

    /// <summary>The native class identifying this refusal, when applicable.</summary>
    public string? NativeClass { get; init; }
}
