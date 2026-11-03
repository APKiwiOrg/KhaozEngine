using System;

namespace KhaozEngine.Gpu;

/// <summary>A structured ShaderValidation refusal.</summary>
public sealed class ShaderValidationRangeException : ArgumentOutOfRangeException
{
    /// <summary>Creates the refusal retaining the original parameter and value.</summary>
    public ShaderValidationRangeException(ShaderValidationRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused this operation.</summary>
    public ShaderValidationRefusal Reason { get; }

    /// <summary>The label identifying this refusal, when applicable.</summary>
    public string? Label { get; init; }

    /// <summary>The stream count identifying this refusal, when applicable.</summary>
    public int? StreamCount { get; init; }

    /// <summary>The buffer table size identifying this refusal, when applicable.</summary>
    public int? BufferTableSize { get; init; }
}
