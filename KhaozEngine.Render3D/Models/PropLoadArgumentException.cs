using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of PropLoad inputs.</summary>
public sealed class PropLoadArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public PropLoadArgumentException(PropLoadRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public PropLoadRefusal Reason { get; }

    /// <summary>The refused material part count.</summary>
    public int? PartCount { get; init; }
}
