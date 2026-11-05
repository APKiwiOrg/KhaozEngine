using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of DrawAdmission input or access.</summary>
public sealed class DrawAdmissionArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public DrawAdmissionArgumentException(DrawAdmissionRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public DrawAdmissionArgumentException(DrawAdmissionRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or access was refused.</summary>
    public DrawAdmissionRefusal Reason { get; }

    /// <summary>Whether the refused draw was shadow-only.</summary>
    public bool ShadowOnly { get; init; }

    /// <summary>Whether the refused draw cast shadows.</summary>
    public bool CastsShadows { get; init; }
}
