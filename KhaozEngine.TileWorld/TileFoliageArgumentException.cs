using System;

namespace KhaozEngine.TileWorld;

/// <summary>A foliage layer refusal that preserves ArgumentException catch compatibility.</summary>
public sealed class TileFoliageArgumentException : ArgumentException
{
    /// <summary>Creates a refusal with its stable reason and original parameter context.</summary>
    public TileFoliageArgumentException(TileWorldRefusal reason, string message, string? paramName)
        : base(message, paramName) => Reason = reason;

    /// <summary>Creates a refusal retaining its original cause and parameter context.</summary>
    public TileFoliageArgumentException(TileWorldRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner) => Reason = reason;

    /// <summary>The stable reason the foliage layer was refused.</summary>
    public TileWorldRefusal Reason { get; }

    /// <summary>The refused foliage layer id, as supplied.</summary>
    public string? LayerId { get; init; }

    /// <summary>The zero-based index of a null foliage layer entry, when the caller supplied an index.</summary>
    public int? LayerIndex { get; init; }

    /// <summary>The foliage layer plane refused by admission.</summary>
    public int? Plane { get; init; }

    /// <summary>The number of planes in the receiving world.</summary>
    public int? PlaneCount { get; init; }

    /// <summary>The refused foliage raster origin on the world X axis.</summary>
    public float? OriginX { get; init; }

    /// <summary>The refused foliage raster origin on the world Z axis.</summary>
    public float? OriginZ { get; init; }

    /// <summary>The refused foliage sampling cell edge in world units.</summary>
    public float? CellSize { get; init; }

    /// <summary>The refused foliage raster width in samples.</summary>
    public int? Width { get; init; }

    /// <summary>The refused foliage raster height in samples.</summary>
    public int? Height { get; init; }

    /// <summary>The foliage raster sample count, calculated without integer overflow.</summary>
    public long? SampleCount { get; init; }

    /// <summary>The maximum supported foliage raster sample count.</summary>
    public int? MaximumSampleCount { get; init; }

    /// <summary>The supplied foliage density byte count.</summary>
    public int? ActualCount { get; init; }

    /// <summary>The foliage density sample count required by the raster dimensions.</summary>
    public long? ExpectedCount { get; init; }

    /// <summary>The refused foliage placement spacing in world units.</summary>
    public float? Spacing { get; init; }

    /// <summary>The refused foliage scale range minimum.</summary>
    public float? ScaleMin { get; init; }

    /// <summary>The refused foliage scale range maximum.</summary>
    public float? ScaleMax { get; init; }

    /// <summary>The refused foliage root offset in world units.</summary>
    public float? RootOffset { get; init; }

    /// <summary>The refused foliage door clearance in world units.</summary>
    public float? DoorClearance { get; init; }

    /// <summary>The refused foliage edge fade in world units.</summary>
    public float? EdgeFade { get; init; }

    /// <summary>The number of supplied foliage archetypes.</summary>
    public int? ArchetypeCount { get; init; }

    /// <summary>The refused foliage archetype id, as supplied.</summary>
    public string? ArchetypeId { get; init; }

    /// <summary>The zero-based index of a null or blank-id foliage archetype entry.</summary>
    public int? ArchetypeIndex { get; init; }

    /// <summary>The refused foliage archetype weight.</summary>
    public float? Weight { get; init; }

    /// <summary>The accumulated foliage archetype weight.</summary>
    public float? TotalWeight { get; init; }

    /// <summary>The underlay material id refused by foliage admission.</summary>
    public ushort? MaterialId { get; init; }
}
