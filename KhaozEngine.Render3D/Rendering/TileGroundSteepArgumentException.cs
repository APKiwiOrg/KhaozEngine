using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of TileGroundSteep input.</summary>
public sealed class TileGroundSteepArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TileGroundSteepArgumentException(TileGroundSteepRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TileGroundSteepArgumentException(TileGroundSteepRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public TileGroundSteepRefusal Reason { get; }

    /// <summary>The zero-based index of the refused layer.</summary>
    public int LayerIndex { get; init; }

    /// <summary>The refused companion slot. Null when unused by the reason.</summary>
    public int? CompanionSlot { get; init; }

    /// <summary>The number of supplied layers. Null when unused by the reason.</summary>
    public int? LayerCount { get; init; }

    /// <summary>The start of the refused band in degrees. Null when unused by the reason.</summary>
    public float? FromDegrees { get; init; }

    /// <summary>The end of the refused band in degrees. Null when unused by the reason.</summary>
    public float? ToDegrees { get; init; }
}
