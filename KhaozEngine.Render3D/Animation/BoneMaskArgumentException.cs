using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of named bone mask inputs.</summary>
public sealed class BoneMaskArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public BoneMaskArgumentException(BoneMaskRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public BoneMaskRefusal Reason { get; }

    /// <summary>The unresolved joint name, null when a joint list supplied null.</summary>
    public string? JointName { get; init; }

    /// <summary>The number of names in a mismatched explicit name table.</summary>
    public int? NameCount { get; init; }

    /// <summary>The number of nodes the explicit name table must cover.</summary>
    public int? NodeCount { get; init; }
}
