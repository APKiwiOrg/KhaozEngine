using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The one place tile space meets the shard grid. TILE COORDINATES ARE THE PLANE: the host is built with
/// <see cref="CellSize"/> = <see cref="TileRegion.Size"/> and every insert, query and
/// <see cref="CellCoord.FromWorld"/> call takes (tileX, tileZ) as its floats, so a cell IS a region.
/// <para>Both grids FLOOR, so the identity holds on the negative side of the origin as well as the positive one:
/// <see cref="RegionCoord.Of"/> floors through <c>FloorDiv</c> and <see cref="CellCoord.FromWorld"/> floors a
/// float division. The one bound on it is that float division: the cell answer is exact only while the tile
/// coordinate is exactly representable as a <c>float</c>, which is <c>|tileX|, |tileZ| &lt;= 2^24</c>. Past that
/// the two grids can disagree by one, so <c>RegionOf(CoordOf(t)) != t.Region</c> out there. That is the SHARD
/// grid's own limit rather than this type's, since the host is handed floats by the position accessor, and if a
/// world ever needs to run past it the change belongs in <see cref="CellCoord.FromWorld"/>. A 2^24 tile world is
/// 16.7 million tiles from the origin on each axis, which is 262144 regions.</para>
/// <para><c>TileWorldSpace</c> is NOT consulted anywhere on the server. It maps a tile to RENDER metres, negating
/// z and scaling by the tile size, so feeding it to the grid would flip the sign of every row (tiles at z in
/// [0, 64) would floor into cell -1 rather than 0, putting every region one cell south and moving the crossing at
/// z = 64 to z = 0) and scale x and z by metres per tile on top of that.</para>
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

    /// <summary>The cell a tile falls in. Routed through <see cref="CellCoord.FromWorld"/> rather than through
    /// <see cref="RegionCoord.Of"/>, and NOT because the integer path is wrong: <c>Of</c> floors too. It is because
    /// <c>FromWorld</c> is the same call <c>ShardHost.CoordFor</c> makes with the same cell size, so this cannot
    /// disagree with the grid the server actually runs on. Answering the region question with the region's own
    /// arithmetic would leave two implementations of one answer, and the one the host uses would win silently.
    /// See the type doc for the 2^24 bound the float division puts on the identity.</summary>
    /// <param name="tile">The tile to place. Its plane is ignored, see the type doc.</param>
    public static CellCoord CoordOf(TileCoord tile) => CellCoord.FromWorld(tile.X, tile.Z, CellSize);

    /// <summary>The region a cell IS. A rename rather than a conversion, and that is the point: with
    /// <see cref="CellSize"/> at <see cref="TileRegion.Size"/> the two grids have the same lines in the same places,
    /// so a cell coordinate and a region coordinate carry the same pair of integers.</summary>
    /// <param name="cell">The cell to name as a region.</param>
    public static RegionCoord RegionOf(CellCoord cell) => new(cell.X, cell.Y);
}
