namespace KhaozEngine.Dungeon;

/// <summary>
/// The content of one raster cell in a <see cref="DungeonLayout"/>. <see cref="RoomFloor"/>, <see cref="Corridor"/>,
/// <see cref="DoorFrame"/>, <see cref="StairLower"/>, <see cref="StairUpper"/>, and <see cref="StairTop"/> are the
/// six walkable kinds (see <see cref="DungeonLayout.IsWalkable"/>). <see cref="StairVoid"/> is the cutout above (or
/// below) a stair run, the negative space the stair mesh pokes through, and is deliberately not walkable.
/// </summary>
public enum DungeonCellKind : byte
{
    /// <summary>No floor: outside the carved layout entirely.</summary>
    Empty,

    /// <summary>Interior floor of a <see cref="DungeonRoom"/>.</summary>
    RoomFloor,

    /// <summary>A corridor tile connecting two rooms.</summary>
    Corridor,

    /// <summary>A solid, non-walkable wall cell.</summary>
    Wall,

    /// <summary>A doorway opening between a room and a corridor or stairwell.</summary>
    DoorFrame,

    /// <summary>The bottom tread cell of a stair, on the lower floor.</summary>
    StairLower,

    /// <summary>The mid-run tread cell of a stair.</summary>
    StairUpper,

    /// <summary>The landing cell of a stair, on the upper floor.</summary>
    StairTop,

    /// <summary>The carved-out hole a stair run passes through: not walkable.</summary>
    StairVoid
}

/// <summary>A single cell address in dungeon-local tile coordinates: <see cref="X"/>/<see cref="Z"/> on the plot
/// raster, <see cref="Floor"/> as the vertical level index (0-based).</summary>
public readonly record struct DungeonTile(int X, int Z, int Floor);
