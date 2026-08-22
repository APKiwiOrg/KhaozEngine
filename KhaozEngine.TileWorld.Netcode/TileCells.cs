using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The one place tile space meets the shard grid. TILE COORDINATES ARE THE PLANE: the host is built with
/// <see cref="CellSize"/> = <see cref="TileRegion.Size"/> and every insert, query and
/// <see cref="CellCoord.FromWorld"/> call takes (tileX, tileZ) as its floats, so a cell IS a region and the two
/// floor-divisions agree on negative coordinates as well as positive ones.
/// <para><c>TileWorldSpace</c> is NOT consulted anywhere on the server. It negates z for rendering, which would
/// put a cell boundary half a tile out of step with a region boundary and turn every crossing into an
/// off-by-one.</para>
/// <para>PLANES DO NOT SHARD. A cell holds every plane of its region, because two floors of one building are a few
/// tiles apart in x and z and would otherwise be handed between shards on a staircase. What separates them is the
/// SERVE, which filters a viewer's area of interest to the viewer's own plane, so the topology stays
/// two-dimensional and the visibility rule stays where it can see who is looking.</para>
/// </summary>
public static class TileCells
{
    /// <summary>The shard cell edge, in tiles: one region. The only value that makes a cell a region, which is what
    /// lets a head persist, stream and hand off on the same unit.</summary>
    public const float CellSize = TileRegion.Size;

    /// <summary>The cell a tile falls in. Routed through <see cref="CellCoord.FromWorld"/> rather than through the
    /// integer division <see cref="RegionCoord.Of"/> uses, so this answers exactly what the host answers for the
    /// same tile, including on the negative side of the origin where a truncating division would disagree.</summary>
    /// <param name="tile">The tile to place. Its plane is ignored, see the type doc.</param>
    public static CellCoord CoordOf(TileCoord tile) => CellCoord.FromWorld(tile.X, tile.Z, CellSize);

    /// <summary>The region a cell IS. A rename rather than a conversion, and that is the point: with
    /// <see cref="CellSize"/> at <see cref="TileRegion.Size"/> the two grids have the same lines in the same places,
    /// so a cell coordinate and a region coordinate carry the same pair of integers.</summary>
    /// <param name="cell">The cell to name as a region.</param>
    public static RegionCoord RegionOf(CellCoord cell) => new(cell.X, cell.Y);
}
