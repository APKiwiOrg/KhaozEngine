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
    /// document's tile size or is not a positive whole number of metres, a multiplier is not positive, or an origin
    /// component is not a whole multiple of the storage tile size.</summary>
    public void Validate(MapBuiltWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireAligned();
        if (StorageTileSize != world.TileSize)
            throw Misaligned(FormattableString.Invariant(
                $"storage tile size {StorageTileSize} m differs from the document's tile size {world.TileSize} m"));
    }

    /// <summary>The navigation tile holding storage tile <paramref name="tile"/>. Throws
    /// <see cref="MapDocumentException"/> when the grids are not aligned on their own terms.</summary>
    public MapNavTileCoord NavTileOf(MapTileCoord tile)
    {
        RequireAligned();
        return AlignedNavTileOf(tile);
    }

    /// <summary>The server cell holding storage tile <paramref name="tile"/>. Throws
    /// <see cref="MapDocumentException"/> when the grids are not aligned on their own terms.</summary>
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

    // Floor division of the tile offset from the origin's tile, so tiles below the origin floor downward.
    int Block(int tile, float origin, int storageTiles)
    {
        long offset = tile - (long)Math.Round(origin / (double)StorageTileSize);
        long quotient = offset / storageTiles;
        if (offset % storageTiles < 0) quotient--;
        return checked((int)quotient);
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
