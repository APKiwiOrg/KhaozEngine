using System;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Index of one navigation tile, a square block of storage tiles counted from the grids' origin.</summary>
public readonly record struct MapNavTileCoord(int X, int Z);

/// <summary>Index of one server cell, a square block of storage tiles counted from the grids' origin. (X, Z) equals
/// Sharding's <c>CellGrid.CoordFor</c> (X, Y) at any point of the cell for the same cell size and origin.</summary>
public readonly record struct MapServerCellCoord(int X, int Z);

/// <summary>The three aligned world grids: storage tiles, navigation tiles and server cells. Navigation tiles and server
/// cells are whole blocks of storage tiles, so every storage tile lies in exactly one of each. A storage tile is the
/// document's own tile, so its index carries no origin. A navigation tile or server cell index counts blocks from
/// <see cref="Origin"/>, which lies on a storage tile seam.</summary>
/// <param name="StorageTileSize">The storage tile edge in metres. Equals the document's tile size and is a positive
/// whole number of metres.</param>
/// <param name="NavTileStorageTiles">Storage tiles along one edge of a navigation tile. Positive.</param>
/// <param name="ServerCellStorageTiles">Storage tiles along one edge of a server cell. Positive.</param>
/// <param name="Origin">The world XZ point where navigation tile and server cell (0, 0) begin. Each component is a
/// whole multiple of <paramref name="StorageTileSize"/>.</param>
public sealed record MapWorldGrids(float StorageTileSize, int NavTileStorageTiles, int ServerCellStorageTiles, Vector2 Origin)
{
    /// <summary>The navigation tile edge in metres.</summary>
    public float NavTileSize => StorageTileSize * NavTileStorageTiles;

    /// <summary>The server cell edge in metres.</summary>
    public float ServerCellSize => StorageTileSize * ServerCellStorageTiles;

    /// <summary>Refuses grids that do not align with <paramref name="world"/>. Throws
    /// <see cref="MapDocumentException"/> naming the grid alignment when the storage tile size differs from the
    /// document's tile size or is not a positive whole number of metres, a multiplier is not positive, an origin
    /// component is not a whole multiple of the storage tile size, or the origin lies so far from the world that a
    /// navigation tile or server cell index of the world's bounds, widened by one block on each side, leaves the int
    /// range.</summary>
    public void Validate(MapBuiltWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireAligned();
        if (StorageTileSize != world.TileSize)
            throw Misaligned(FormattableString.Invariant(
                $"storage tile size {StorageTileSize} m differs from the document's tile size {world.TileSize} m"));
        RequireIndexable(world.Bounds.MinX, world.Bounds.MaxX, Origin.X, "X");
        RequireIndexable(world.Bounds.MinZ, world.Bounds.MaxZ, Origin.Y, "Z");
    }

    /// <summary>The navigation tile holding storage tile <paramref name="tile"/>. Throws
    /// <see cref="MapDocumentException"/> when the grids are not aligned on their own terms, or naming the tile range
    /// when the index leaves the int range.</summary>
    public MapNavTileCoord NavTileOf(MapTileCoord tile)
    {
        RequireAligned();
        return AlignedNavTileOf(tile);
    }

    /// <summary>The server cell holding storage tile <paramref name="tile"/>. Throws
    /// <see cref="MapDocumentException"/> when the grids are not aligned on their own terms, or naming the tile range
    /// when the index leaves the int range.</summary>
    public MapServerCellCoord ServerCellOf(MapTileCoord tile)
    {
        RequireAligned();
        return AlignedServerCellOf(tile);
    }

    /// <summary><see cref="NavTileOf"/> without the alignment checks, for callers that have already run
    /// <see cref="Validate"/>.</summary>
    internal MapNavTileCoord AlignedNavTileOf(MapTileCoord tile) =>
        new(Block(tile.X, Origin.X, NavTileStorageTiles), Block(tile.Z, Origin.Y, NavTileStorageTiles));

    /// <summary><see cref="ServerCellOf"/> without the alignment checks, for callers that have already run
    /// <see cref="Validate"/>.</summary>
    internal MapServerCellCoord AlignedServerCellOf(MapTileCoord tile) =>
        new(Block(tile.X, Origin.X, ServerCellStorageTiles), Block(tile.Z, Origin.Y, ServerCellStorageTiles));

    // Floor division of the tile offset from the origin's tile, so tiles below the origin floor downward. Validate keeps
    // the world's own tiles in range. A tile farther out, or an origin no long can hold, refuses naming the tile range.
    int Block(int tile, float origin, int storageTiles)
    {
        double originTile = Math.Round(origin / (double)StorageTileSize);
        if (Math.Abs(originTile) <= MaxOriginTiles)
        {
            long offset = tile - (long)originTile;
            long quotient = offset / storageTiles;
            if (offset % storageTiles < 0) quotient--;
            if (quotient is >= int.MinValue and <= int.MaxValue) return (int)quotient;
        }
        throw new MapDocumentException(string.Format(CultureInfo.InvariantCulture,
            "Storage tile {0} counted in blocks of {1} from origin {2} m lies outside the {3:N0} to {4:N0} tile range.",
            tile, storageTiles, origin, int.MinValue, int.MaxValue));
    }

    // An origin tile within 2^62 keeps the tile offset inside long.
    const double MaxOriginTiles = 4_611_686_018_427_387_904d;

    // Every block index the world's bounds reach on one axis, one block wider on each side, must fit an int, so the
    // indices of the world's tiles and the candidate ranges around them never overflow.
    void RequireIndexable(double min, double max, float origin, string axis)
    {
        RequireIndexable(min, max, origin, axis, NavTileStorageTiles, "navigation tiles");
        RequireIndexable(min, max, origin, axis, ServerCellStorageTiles, "server cells");
    }

    void RequireIndexable(double min, double max, float origin, string axis, int storageTiles, string blocks)
    {
        double size = (double)StorageTileSize * storageTiles;
        double first = Math.Floor((min - origin) / size) - 1, last = Math.Floor((max - origin) / size) + 1;
        if (!(first >= int.MinValue && last <= int.MaxValue))
            throw Misaligned(string.Format(CultureInfo.InvariantCulture,
                "origin {0} m on {1} puts the world's bounds [{2}, {3}] at {4} {5:F0} to {6:F0} with one block either " +
                "side, outside the {7:N0} to {8:N0} tile range", origin, axis, min, max, blocks, first, last,
                int.MinValue, int.MaxValue));
    }

    void RequireAligned()
    {
        if (!float.IsFinite(StorageTileSize) || !(StorageTileSize > 0f) || StorageTileSize != MathF.Floor(StorageTileSize))
            throw Misaligned(FormattableString.Invariant(
                $"storage tile size {StorageTileSize} m is not a positive whole number of metres"));
        if (NavTileStorageTiles <= 0 || ServerCellStorageTiles <= 0)
            throw Misaligned(FormattableString.Invariant(
                $"navigation tile ({NavTileStorageTiles}) and server cell ({ServerCellStorageTiles}) storage tile counts must be positive"));
        if (!float.IsFinite(Origin.X) || !float.IsFinite(Origin.Y) ||
            Origin.X % StorageTileSize != 0f || Origin.Y % StorageTileSize != 0f)
            throw Misaligned(string.Format(CultureInfo.InvariantCulture,
                "origin ({0}, {1}) is not a whole multiple of the {2} m storage tile", Origin.X, Origin.Y, StorageTileSize));
    }

    static MapDocumentException Misaligned(string detail) => new("grid alignment: " + detail + ".");
}
