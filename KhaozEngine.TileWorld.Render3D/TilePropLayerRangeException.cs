using System;

namespace KhaozEngine.TileWorld;

/// <summary>A structured refusal of TilePropLayer input.</summary>
public sealed class TilePropLayerRangeException : ArgumentOutOfRangeException
{
    /// <summary>Create a refusal preserving the original parameter and actual value.</summary>
    public TilePropLayerRangeException(TilePropLayerRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public TilePropLayerRefusal Reason { get; }

    /// <summary>The id of the refused layer.</summary>
    public string LayerId { get; init; } = string.Empty;

    /// <summary>The refused LOD distance.</summary>
    public float LodDistance { get; init; }

    /// <summary>The refused HLOD distance.</summary>
    public float HlodDistance { get; init; }

    /// <summary>The refused draw radius.</summary>
    public float DrawRadius { get; init; }
}
