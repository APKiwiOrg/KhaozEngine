using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of a tile-ground layer's relief members.</summary>
public sealed class TileGroundReliefArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TileGroundReliefArgumentException(TileGroundReliefRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TileGroundReliefArgumentException(TileGroundReliefRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public TileGroundReliefRefusal Reason { get; }

    /// <summary>The zero-based index of the refused layer.</summary>
    public int LayerIndex { get; init; }

    /// <summary>The refused value: the sharpness, the breakup or the undefined anti-tiling value. Null when unused
    /// by the reason.</summary>
    public float? Value { get; init; }
}
