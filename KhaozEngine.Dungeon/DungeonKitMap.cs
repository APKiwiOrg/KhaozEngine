using System;
using System.Collections.Generic;

namespace KhaozEngine.Dungeon;

/// <summary>
/// The abstract vocabulary of pieces a dungeon layout bakes down to when a sink instances kit content
/// for it (see <see cref="DungeonKitMap"/>). This is deliberately generator-agnostic: the layout only
/// knows walkable cell kinds (<see cref="DungeonCellKind"/>). A sink maps those down to this smaller,
/// stable set of piece roles before resolving each to a concrete kit id.
/// </summary>
public enum DungeonPiece
{
    /// <summary>A walkable floor tile.</summary>
    Floor,

    /// <summary>A solid, non-walkable wall.</summary>
    Wall,

    /// <summary>A doorway opening between two spaces.</summary>
    DoorFrame,

    /// <summary>A stair tread ascending to the floor above.</summary>
    StairUp,

    /// <summary>A stair tread descending to the floor below.</summary>
    StairDown
}

/// <summary>
/// Maps each <see cref="DungeonPiece"/> to a kit content id a sink resolves at bake time (e.g. a prefab
/// or tile-set entry name). The generator itself never references kit ids. A sink builds a
/// <see cref="DungeonKitMap"/> (or uses <see cref="Greybox"/>) to bridge from the abstract piece
/// vocabulary to whatever content the target project ships.
/// </summary>
public sealed class DungeonKitMap
{
    private readonly Dictionary<DungeonPiece, string> _ids = new();

    /// <summary>Maps <paramref name="piece"/> to <paramref name="kitId"/>, overwriting any prior mapping.</summary>
    public void Map(DungeonPiece piece, string kitId)
    {
        _ids[piece] = kitId;
    }

    /// <summary>
    /// Resolves the kit id mapped to <paramref name="piece"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="piece"/> has no mapping.</exception>
    public string Require(DungeonPiece piece)
    {
        if (_ids.TryGetValue(piece, out var kitId))
        {
            return kitId;
        }

        throw new InvalidOperationException($"No kit id mapped for dungeon piece '{piece}'.");
    }

    /// <summary>
    /// Builds a <see cref="DungeonKitMap"/> mapping every <see cref="DungeonPiece"/> to a placeholder
    /// greybox kit id, useful for tests and early integration before real content exists.
    /// </summary>
    public static DungeonKitMap Greybox()
    {
        var map = new DungeonKitMap();
        map.Map(DungeonPiece.Floor, "dungeon_floor");
        map.Map(DungeonPiece.Wall, "dungeon_wall");
        map.Map(DungeonPiece.DoorFrame, "dungeon_doorframe");
        map.Map(DungeonPiece.StairUp, "dungeon_stair");
        map.Map(DungeonPiece.StairDown, "dungeon_landing");
        return map;
    }
}
