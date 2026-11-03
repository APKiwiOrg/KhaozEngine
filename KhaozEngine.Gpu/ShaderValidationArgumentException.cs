using System;

namespace KhaozEngine.Gpu;

/// <summary>A shader validation refusal retaining the original ArgumentException contract.</summary>
public sealed class ShaderValidationArgumentException : ArgumentException
{
    /// <summary>Creates the refusal retaining the original parameter.</summary>
    public ShaderValidationArgumentException(ShaderValidationRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>The rule that refused this module.</summary>
    public ShaderValidationRefusal Reason { get; }

    /// <summary>The rejected module's byte length.</summary>
    public int? ByteLength { get; init; }

    /// <summary>The required byte width of a SPIR-V word.</summary>
    public int? WordBytes { get; init; }
}
