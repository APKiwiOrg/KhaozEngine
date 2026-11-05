using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of WaterFrame input or state.</summary>
public sealed class WaterFrameArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public WaterFrameArgumentException(WaterFrameRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public WaterFrameArgumentException(WaterFrameRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or state was refused.</summary>
    public WaterFrameRefusal Reason { get; }

    /// <summary>The number of entries in the absolute queue.</summary>
    public int AbsoluteCount { get; init; }

    /// <summary>The number of water planes in the frame.</summary>
    public int PlaneCount { get; init; }
}
