namespace KhaozEngine.Dungeon;

/// <summary>
/// The content of one raster cell in a <see cref="DungeonLayout"/>. <see cref="RoomFloor"/>, <see cref="Corridor"/>,
/// <see cref="DoorFrame"/>, <see cref="StairLower"/>, <see cref="StairMid"/>, <see cref="StairUpper"/>, and
/// <see cref="StairTop"/> are the seven walkable kinds (see <see cref="DungeonLayout.IsWalkable"/>). A stair run's
/// three treads (<see cref="StairLower"/>, <see cref="StairMid"/>, <see cref="StairUpper"/>) climb one floor over a
/// three-cell run (a walkable ~34-degree pitch, well below the default max slope), and the landing
/// (<see cref="StairTop"/>) sits on the upper floor one cell PAST the top tread, at the edge of the open shaft.
/// <see cref="StairVoid"/> is the cutout above a tread, the open headroom the ramp climbs through, and is
/// deliberately not walkable. (<see cref="StairMid"/> is the enum's last member so the byte values of the other
/// kinds are unchanged - single-floor rasters, which never carry a mid tread, hash identically.)
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

    /// <summary>The top tread cell of a stair, on the lower floor (the ramp reaches the upper floor at its far edge).</summary>
    StairUpper,

    /// <summary>The landing cell of a stair, on the upper floor, one cell past the top tread at the shaft edge.</summary>
    StairTop,

    /// <summary>The carved-out hole a stair run passes through: not walkable, and left open ABOVE for headroom (the
    /// ceiling pass exempts it). The wall pass encloses its lateral SIDES: an empty cell 8-adjacent to a StairVoid
    /// becomes a wall, so a climber cannot jump out the side of the shaft near the top.</summary>
    StairVoid,

    /// <summary>The middle tread cell of a stair, on the lower floor between <see cref="StairLower"/> and
    /// <see cref="StairUpper"/>. Last in the enum so adding it left every other kind's byte value unchanged.</summary>
    StairMid
}

/// <summary>A single cell address in dungeon-local tile coordinates: <see cref="X"/>/<see cref="Z"/> on the plot
/// raster, <see cref="Floor"/> as the vertical level index (0-based).</summary>
public readonly record struct DungeonTile(int X, int Z, int Floor);
