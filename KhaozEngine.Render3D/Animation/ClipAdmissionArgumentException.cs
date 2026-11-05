using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of clip admission inputs.</summary>
public sealed class ClipAdmissionArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public ClipAdmissionArgumentException(ClipAdmissionRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public ClipAdmissionRefusal Reason { get; }

    /// <summary>The name of a second clip of one name.</summary>
    public string? ClipName { get; init; }

    /// <summary>The required joint absent from the skeleton.</summary>
    public string? JointName { get; init; }
}
