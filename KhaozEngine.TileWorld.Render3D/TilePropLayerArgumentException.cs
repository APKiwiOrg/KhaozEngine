using System;

namespace KhaozEngine.TileWorld;

/// <summary>A structured refusal of TilePropLayer input.</summary>
public sealed class TilePropLayerArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TilePropLayerArgumentException(TilePropLayerRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TilePropLayerArgumentException(TilePropLayerRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public TilePropLayerRefusal Reason { get; }

    /// <summary>The zero-based index of the refused layer. Null when unused by the reason.</summary>
    public int? LayerIndex { get; init; }

    /// <summary>The id of the refused layer. Null when unused by the reason.</summary>
    public string? LayerId { get; init; }

    /// <summary>The authored archetype id that was refused. Null when unused by the reason.</summary>
    public string? ArchetypeId { get; init; }

    /// <summary>The first layer selecting the archetype. Null when unused by the reason.</summary>
    public string? FirstLayerId { get; init; }

    /// <summary>The second layer selecting the archetype. Null when unused by the reason.</summary>
    public string? SecondLayerId { get; init; }

    /// <summary>The roof archetype in the mixed selection. Null when unused by the reason.</summary>
    public string? RoofArchetypeId { get; init; }

    /// <summary>The ordinary archetype in the mixed selection. Null when unused by the reason.</summary>
    public string? OrdinaryArchetypeId { get; init; }
}
