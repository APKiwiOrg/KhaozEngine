using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>One immutable selected-layer batch inside a region-plane prop snapshot.</summary>
public sealed record TilePropLayerSnapshot
{
    /// <summary>Stable layer identity.</summary>
    public required string Id { get; init; }

    /// <summary>Document object IDs in ascending order, parallel to <see cref="Placements"/>.</summary>
    public required IReadOnlyList<long> ObjectIds { get; init; }

    /// <summary>Detached placement values in ascending object-ID order.</summary>
    public required IReadOnlyList<PropPlacement> Placements { get; init; }

    /// <summary>Resolved full, optional LOD, and optional flattened-HLOD inputs for this batch.</summary>
    public required PropLayer Layer { get; init; }
}

/// <summary>An immutable, generation-tagged snapshot of one TileWorld region-plane's prop presentation.</summary>
/// <param name="Ground">Ordinary non-roof placements not selected into an opt-in layer.</param>
/// <param name="Roofs">Ordinary roof placements not selected into an opt-in layer.</param>
public sealed record TileRegionProps(IReadOnlyList<PropPlacement> Ground, IReadOnlyList<PropPlacement> Roofs)
{
    /// <summary>The region represented by this snapshot.</summary>
    public RegionCoord Region { get; init; }

    /// <summary>The plane represented by this snapshot.</summary>
    public int Plane { get; init; }

    /// <summary>Monotonic content generation for this region-plane within its view.</summary>
    public long Generation { get; init; }

    /// <summary>Selected prop batches keyed with ordinal layer IDs.</summary>
    public IReadOnlyDictionary<string, TilePropLayerSnapshot> Layers { get; init; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, TilePropLayerSnapshot>(
            new Dictionary<string, TilePropLayerSnapshot>(StringComparer.Ordinal));

    /// <summary>World tile footprint of each <see cref="Roofs"/> entry, in the same order.</summary>
    public IReadOnlyList<TileRect> RoofFootprints { get; init; } = Array.Empty<TileRect>();

    /// <summary>Document object ID behind each <see cref="Ground"/> entry, in the same order.</summary>
    public IReadOnlyList<long> GroundObjectIds { get; init; } = Array.Empty<long>();

    /// <summary>Document object ID behind each <see cref="Roofs"/> entry, in the same order.</summary>
    public IReadOnlyList<long> RoofObjectIds { get; init; } = Array.Empty<long>();
}
