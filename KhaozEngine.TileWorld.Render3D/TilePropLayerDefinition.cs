using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>Opt-in LOD and HLOD policy for one disjoint set of TileWorld object archetypes.</summary>
public sealed record TilePropLayerDefinition
{
    /// <summary>Stable layer identity used in region cluster keys.</summary>
    public required string Id { get; init; }

    /// <summary>Archetypes represented by this layer instead of the ordinary TileWorld prop draw.</summary>
    public required IReadOnlySet<string> ArchetypeIds { get; init; }

    /// <summary>Horizontal distance at which the layer has fully faded out.</summary>
    public required float DrawRadius { get; init; }

    /// <summary>Horizontal distance at the centre of the full to authored-LOD transition.</summary>
    public required float LodDistance { get; init; }

    /// <summary>Width in metres of the complementary full to authored-LOD transition.</summary>
    public float LodCrossfadeWidth { get; init; }

    /// <summary>Horizontal distance at the centre of the individual to merged-HLOD transition.</summary>
    public float HlodDistance { get; init; }

    /// <summary>Width in metres of the individual to merged-HLOD transition and final exit fade.</summary>
    public float HlodCrossfadeWidth { get; init; }

    /// <summary>Vertex-cluster weld cell size in metres for the merged HLOD.</summary>
    public float HlodWeldCell { get; init; }

    /// <summary>Whether individual and merged representations cast directional shadows.</summary>
    public bool CastsShadows { get; init; } = true;
}
