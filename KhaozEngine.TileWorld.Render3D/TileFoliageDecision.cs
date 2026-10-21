using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>The released tile foliage rule stage that zeroed a sample, in the order the sampler applies them. A
/// positive raster is read first, then the tile eligibility stages, then the door factor and last the edge fade.</summary>
public enum TileFoliageRejection
{
    /// <summary>Every stage passed.</summary>
    None,
    /// <summary>The interpolated raster density is zero, including every point outside the raster.</summary>
    ZeroRaster,
    /// <summary>The sampled tile's region is not loaded.</summary>
    AbsentRegion,
    /// <summary>The tile has no underlay on the layer's plane.</summary>
    VoidUnderlay,
    /// <summary>The visible material is outside the layer's allowed underlays.</summary>
    UnderlayNotAllowed,
    /// <summary>The visible material is a water material.</summary>
    Water,
    /// <summary>The layer excludes indoors and the tile is flagged indoors.</summary>
    Indoors,
    /// <summary>A solid object footprint on the layer's plane covers the tile.</summary>
    Solid,
    /// <summary>A roof footprint on a higher plane covers the tile.</summary>
    Roof,
    /// <summary>The point lies within the door clearance of a door on the layer's plane.</summary>
    Door,
    /// <summary>The edge fade toward an ineligible cardinal neighbour reaches zero at the point.</summary>
    EdgeFade,
}

/// <summary>One released sampler answer with the rule stage that decided it. <paramref name="VisibleMaterial"/> is the
/// material the eligibility stages read: the overlay where the point lies on an overlay triangle, else the underlay. It is
/// zero when no eligibility stage read a material, for a zero raster, an absent region or a void underlay.</summary>
/// <param name="Sample">Exactly what <see cref="TileFoliageSurface.Sample"/> returns for the same point.</param>
/// <param name="Reason">The first stage that zeroed the density, or <see cref="TileFoliageRejection.None"/>.</param>
/// <param name="VisibleMaterial">The visible ground material the eligibility stages read.</param>
public readonly record struct TileFoliageDecision(GroundCoverSample Sample, TileFoliageRejection Reason, ushort VisibleMaterial);
