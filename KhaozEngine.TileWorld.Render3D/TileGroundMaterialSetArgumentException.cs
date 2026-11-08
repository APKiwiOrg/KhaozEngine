using System;

namespace KhaozEngine.TileWorld;

/// <summary>A structured refusal of TileGroundMaterialSet input.</summary>
public sealed class TileGroundMaterialSetArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TileGroundMaterialSetArgumentException(TileGroundMaterialSetRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal with a stable reason and the original parameter context.</summary>
    public TileGroundMaterialSetArgumentException(TileGroundMaterialSetRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public TileGroundMaterialSetRefusal Reason { get; }

    /// <summary>The number of supplied material ids. Null when unused by the reason.</summary>
    public int? MaterialCount { get; init; }

    /// <summary>The number of supplied layers. Null when unused by the reason.</summary>
    public int? ActualLayerCount { get; init; }

    /// <summary>The number of layers required by the material ids. Null when unused by the reason.</summary>
    public int? RequiredLayerCount { get; init; }

    /// <summary>The number of supplied layers. Null when unused by the reason.</summary>
    public int? LayerCount { get; init; }

    /// <summary>The maximum supported layer count. Null when unused by the reason.</summary>
    public int? MaximumLayerCount { get; init; }

    /// <summary>The zero-based index of the refused layer. Null when unused by the reason.</summary>
    public int? LayerIndex { get; init; }

    /// <summary>The supplied RGBA byte length. Null when unused by the reason.</summary>
    public int? ActualByteLength { get; init; }

    /// <summary>The required RGBA byte length. Null when unused by the reason.</summary>
    public int? ExpectedByteLength { get; init; }

    /// <summary>The declared layer width in texels. Null when unused by the reason.</summary>
    public int? Width { get; init; }

    /// <summary>The declared layer height in texels. Null when unused by the reason.</summary>
    public int? Height { get; init; }

    /// <summary>The duplicated material id. Null when unused by the reason.</summary>
    public ushort? MaterialId { get; init; }

    /// <summary>The slot already holding the material id. Null when unused by the reason.</summary>
    public int? FirstSlot { get; init; }

    /// <summary>The slot that would shadow the first slot. Null when unused by the reason.</summary>
    public int? DuplicateSlot { get; init; }

    /// <summary>The refused companion slot. Null when unused by the reason.</summary>
    public int? CompanionSlot { get; init; }

    /// <summary>The number of material slots before the reserved missing slot. Null when unused by the reason.</summary>
    public int? MaterialSlotCount { get; init; }
}
