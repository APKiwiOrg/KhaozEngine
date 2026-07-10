using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KhaozEngine.Dungeon;

/// <summary>Gameplay role a <see cref="DungeonRoom"/> plays in the layout's critical path.</summary>
public enum DungeonRoomType
{
    /// <summary>The room the player starts in.</summary>
    Entrance,

    /// <summary>A generic room with no special role.</summary>
    Normal,

    /// <summary>A room gating a lock, where the corresponding key is required to leave (or was placed).</summary>
    Key,

    /// <summary>An optional reward room off the critical path.</summary>
    Treasure,

    /// <summary>The room at the end of the critical path.</summary>
    Boss,

    /// <summary>An elongated grand connector room: longer along the corridor that reached it than a normal room,
    /// so a run of them reads as monumental halls. Placed by growth when <see cref="DungeonConfig.HallChancePercent"/>
    /// is positive; otherwise a purely structural role (it can still hold keys and be promoted to
    /// <see cref="Boss"/>).</summary>
    Hall
}

/// <summary>How two rooms are connected by a <see cref="DungeonEdge"/>.</summary>
public enum DungeonEdgeKind
{
    /// <summary>A same-floor corridor run.</summary>
    Corridor,

    /// <summary>A stair run between two floors.</summary>
    Stair
}

/// <summary>Kind of a gameplay <see cref="DungeonMarker"/>.</summary>
public enum DungeonMarkerType
{
    /// <summary>Where an actor may spawn.</summary>
    Spawn,

    /// <summary>Where loot may be placed.</summary>
    Loot,

    /// <summary>A quest/objective marker.</summary>
    Objective,

    /// <summary>The player entrance point.</summary>
    Entrance
}

/// <summary>One rectangular room carved into the layout: its floor, its interior rect (in tile coordinates), and
/// its gameplay <see cref="RoomType"/>.</summary>
public sealed class DungeonRoom
{
    /// <summary>Stable identifier, unique within the layout, referenced by <see cref="DungeonEdge"/> and
    /// <see cref="DungeonKeyPlacement"/>.</summary>
    public int Id { get; init; }

    /// <summary>Vertical level index (0-based) the room sits on.</summary>
    public int Floor { get; init; }

    /// <summary>Interior rect min corner X, in tile coordinates.</summary>
    public int X { get; init; }

    /// <summary>Interior rect min corner Z, in tile coordinates.</summary>
    public int Z { get; init; }

    /// <summary>Interior width, in tiles.</summary>
    public int Width { get; init; }

    /// <summary>Interior depth, in tiles.</summary>
    public int Depth { get; init; }

    /// <summary>The room's gameplay role.</summary>
    [JsonInclude]
    public DungeonRoomType RoomType { get; internal set; }
}

/// <summary>One connection between two rooms: either a same-floor corridor or a cross-floor stair, carved
/// together with its doors so the layout is completable by construction.</summary>
public sealed class DungeonEdge
{
    /// <summary>Id of the first connected room.</summary>
    public int RoomA { get; init; }

    /// <summary>Id of the second connected room.</summary>
    public int RoomB { get; init; }

    /// <summary>Whether this edge is a corridor or a stair.</summary>
    public DungeonEdgeKind Kind { get; init; }

    /// <summary>The cells carved for the connection: corridor tiles for <see cref="DungeonEdgeKind.Corridor"/>,
    /// or the <c>[StairLower, StairUpper, StairTop]</c> run for <see cref="DungeonEdgeKind.Stair"/>.</summary>
    public IReadOnlyList<DungeonTile> Path { get; init; } = Array.Empty<DungeonTile>();

    /// <summary>The <see cref="DungeonCellKind.DoorFrame"/> cells at each end of the connection (two for a
    /// corridor, two for a stair).</summary>
    public IReadOnlyList<DungeonTile> Doors { get; init; } = Array.Empty<DungeonTile>();

    /// <summary>Id of the lock guarding this edge, or null if it is not locked.</summary>
    [JsonInclude]
    public int? LockId { get; internal set; }
}

/// <summary>Assigns a lock to the room its key is placed in.</summary>
public sealed class DungeonKeyPlacement
{
    /// <summary>The lock this key opens, matching a <see cref="DungeonEdge.LockId"/>.</summary>
    public int LockId { get; init; }

    /// <summary>Id of the room the key is placed in.</summary>
    public int RoomId { get; init; }
}

/// <summary>A gameplay marker (spawn point, loot spot, objective, or entrance) at a specific tile.</summary>
public sealed class DungeonMarker
{
    /// <summary>The marker's kind.</summary>
    public DungeonMarkerType Type { get; init; }

    /// <summary>The tile the marker sits on.</summary>
    public DungeonTile Tile { get; init; }

    /// <summary>Free-form gameplay tags describing this marker (e.g. enemy or loot table hints).</summary>
    public List<string> Tags { get; init; } = new();
}

/// <summary>Summary counts describing how well the generator satisfied a <see cref="DungeonConfig"/> request.</summary>
public sealed class LayoutStats
{
    /// <summary>Rooms the config asked for (<see cref="DungeonConfig.RoomCountTarget"/>).</summary>
    public int RoomsRequested { get; init; }

    /// <summary>Rooms actually placed.</summary>
    public int RoomsPlaced { get; init; }

    /// <summary>Length (in edges) of the realized entrance-to-boss critical path.</summary>
    [JsonInclude]
    public int CriticalPathLength { get; internal set; }

    /// <summary>Number of floors actually used.</summary>
    public int FloorsUsed { get; init; }

    /// <summary>Locks the config asked for (<see cref="DungeonConfig.LockCount"/>).</summary>
    public int LocksRequested { get; init; }

    /// <summary>Locks actually placed.</summary>
    [JsonInclude]
    public int LocksPlaced { get; internal set; }

    /// <summary>True if the generator ran out of room to keep placing (plot or room budget exhausted).</summary>
    public bool Saturated { get; init; }
}

/// <summary>
/// A generated dungeon: a 3D raster of <see cref="DungeonCellKind"/> cells plus the room graph (rooms, edges,
/// key placements, and gameplay markers) laid out on it. Built by <c>DungeonGenerator.Generate</c> via the
/// internal constructor, which allocates the raster. The generator then fills <see cref="CellsMutable"/> in
/// place and supplies the room-graph lists through object-initializer syntax.
/// </summary>
public sealed class DungeonLayout
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>World size of one tile edge, in metres.</summary>
    public float CellSizeMeters { get; init; }

    /// <summary>World height between floors, in metres.</summary>
    public float FloorHeightMeters { get; init; }

    /// <summary>Whether the sinks roof this layout or leave it open-top. Carried from
    /// <see cref="DungeonConfig.CeilingMode"/> at generation. A pure sink-time presentation choice, so it is
    /// deliberately NOT part of <see cref="LayoutHash"/> (like <see cref="Stats"/>): two layouts identical but
    /// for their ceiling mode share a structure hash. A layout rebuilt from JSON is always
    /// <see cref="DungeonCeilingMode.Open"/> (the field is not serialized, since the layout JSON is the
    /// structural artifact).</summary>
    public DungeonCeilingMode CeilingMode { get; init; } = DungeonCeilingMode.Open;

    /// <summary>Resolved ceiling height above each floor, in metres, used by the sinks when
    /// <see cref="CeilingMode"/> is <see cref="DungeonCeilingMode.Roofed"/>. Set by the generator to
    /// <see cref="DungeonConfig.CeilingHeightMeters"/> or, when that is null, <see cref="FloorHeightMeters"/>.
    /// Read only in <see cref="DungeonCeilingMode.Roofed"/>.</summary>
    public float CeilingHeightMeters { get; init; }

    /// <summary>Raster width, in tiles.</summary>
    public int Width { get; init; }

    /// <summary>Raster depth, in tiles.</summary>
    public int Depth { get; init; }

    /// <summary>Number of floor levels in the raster.</summary>
    public int Floors { get; init; }

    /// <summary>Every room in the layout, in the order the generator placed them.</summary>
    public IReadOnlyList<DungeonRoom> Rooms { get; init; } = Array.Empty<DungeonRoom>();

    /// <summary>Every corridor/stair connection in the layout, in the order the generator carved them.</summary>
    public IReadOnlyList<DungeonEdge> Edges { get; init; } = Array.Empty<DungeonEdge>();

    /// <summary>Every lock/key assignment in the layout.</summary>
    public IReadOnlyList<DungeonKeyPlacement> Keys { get; init; } = Array.Empty<DungeonKeyPlacement>();

    /// <summary>Every gameplay marker in the layout.</summary>
    public IReadOnlyList<DungeonMarker> Markers { get; init; } = Array.Empty<DungeonMarker>();

    /// <summary>Generation summary counts.</summary>
    public LayoutStats Stats { get; init; } = new LayoutStats();

    /// <summary>The <c>Width * Depth * Floors</c> cell raster. Internal so only the generator (and tests) can
    /// write into it. Consumers read cells through <see cref="GetCell"/>.</summary>
    internal DungeonCellKind[] CellsMutable { get; }

    /// <summary>Allocates a layout's raster for the given dimensions. Every cell starts as
    /// <see cref="DungeonCellKind.Empty"/>. The generator fills <see cref="CellsMutable"/> in place.</summary>
    internal DungeonLayout(int width, int depth, int floors, float cellSizeMeters, float floorHeightMeters)
    {
        Width = width;
        Depth = depth;
        Floors = floors;
        CellSizeMeters = cellSizeMeters;
        FloorHeightMeters = floorHeightMeters;
        CellsMutable = new DungeonCellKind[width * depth * floors];
    }

    /// <summary>Returns the cell at <paramref name="x"/>/<paramref name="z"/>/<paramref name="floor"/>, or
    /// <see cref="DungeonCellKind.Empty"/> if any coordinate is out of range.</summary>
    public DungeonCellKind GetCell(int x, int z, int floor)
    {
        if (x < 0 || x >= Width || z < 0 || z >= Depth || floor < 0 || floor >= Floors)
        {
            return DungeonCellKind.Empty;
        }

        return CellsMutable[CellIndex(x, z, floor)];
    }

    /// <summary>True for the six walkable kinds (room floor, corridor, door frame, and the three stair tread
    /// kinds). False for <see cref="DungeonCellKind.Empty"/>, <see cref="DungeonCellKind.Wall"/>, and
    /// <see cref="DungeonCellKind.StairVoid"/>.</summary>
    public static bool IsWalkable(DungeonCellKind kind)
    {
        switch (kind)
        {
            case DungeonCellKind.RoomFloor:
            case DungeonCellKind.Corridor:
            case DungeonCellKind.DoorFrame:
            case DungeonCellKind.StairLower:
            case DungeonCellKind.StairUpper:
            case DungeonCellKind.StairTop:
                return true;
            default:
                return false;
        }
    }

    /// <summary>A cross-platform-stable FNV-1a 64 hash over the layout's dimensions, raster cells, rooms, edges,
    /// keys, and markers, each in stored order. Floats are folded via their raw bit pattern
    /// (<see cref="BitConverter.SingleToUInt32Bits"/>), never <see cref="object.GetHashCode"/>, so the result is
    /// identical across platforms and process runs for identical layout state.</summary>
    public ulong LayoutHash()
    {
        ulong hash = ComputeStructureHash();

        for (int i = 0; i < Markers.Count; i++)
        {
            DungeonMarker marker = Markers[i];
            hash = MixByte(hash, (byte)marker.Type);
            hash = MixTile(hash, marker.Tile);

            for (int t = 0; t < marker.Tags.Count; t++)
            {
                hash = MixString(hash, marker.Tags[t]);
            }
        }

        return hash;
    }

    /// <summary>The same FNV-1a fold as <see cref="LayoutHash"/> over dimensions, raster cells, rooms, edges,
    /// and keys, but EXCLUDING <see cref="Markers"/> (and <see cref="Stats"/>, which was never part of either
    /// fold). Internal: used by <c>DungeonMarkerTests.MarkerStream_Isolated</c> to prove that retuning the
    /// marker phase's config never perturbs room growth or gating, independent of the marker phase's own
    /// output.</summary>
    internal ulong StructureHash()
    {
        return ComputeStructureHash();
    }

    private ulong ComputeStructureHash()
    {
        ulong hash = FnvOffsetBasis;

        hash = MixInt(hash, Width);
        hash = MixInt(hash, Depth);
        hash = MixInt(hash, Floors);
        hash = MixFloat(hash, CellSizeMeters);
        hash = MixFloat(hash, FloorHeightMeters);

        for (int i = 0; i < CellsMutable.Length; i++)
        {
            hash = MixByte(hash, (byte)CellsMutable[i]);
        }

        for (int i = 0; i < Rooms.Count; i++)
        {
            DungeonRoom room = Rooms[i];
            hash = MixInt(hash, room.Id);
            hash = MixInt(hash, room.Floor);
            hash = MixInt(hash, room.X);
            hash = MixInt(hash, room.Z);
            hash = MixInt(hash, room.Width);
            hash = MixInt(hash, room.Depth);
            hash = MixByte(hash, (byte)room.RoomType);
        }

        for (int i = 0; i < Edges.Count; i++)
        {
            DungeonEdge edge = Edges[i];
            hash = MixInt(hash, edge.RoomA);
            hash = MixInt(hash, edge.RoomB);
            hash = MixByte(hash, (byte)edge.Kind);

            for (int p = 0; p < edge.Path.Count; p++)
            {
                hash = MixTile(hash, edge.Path[p]);
            }

            for (int d = 0; d < edge.Doors.Count; d++)
            {
                hash = MixTile(hash, edge.Doors[d]);
            }

            hash = MixByte(hash, (byte)(edge.LockId.HasValue ? 1 : 0));
            if (edge.LockId.HasValue)
            {
                hash = MixInt(hash, edge.LockId.Value);
            }
        }

        for (int i = 0; i < Keys.Count; i++)
        {
            hash = MixInt(hash, Keys[i].LockId);
            hash = MixInt(hash, Keys[i].RoomId);
        }

        return hash;
    }

    private int CellIndex(int x, int z, int floor)
    {
        return (floor * Depth + z) * Width + x;
    }

    private static ulong MixByte(ulong hash, byte value)
    {
        hash ^= value;
        hash *= FnvPrime;
        return hash;
    }

    private static ulong MixUInt32(ulong hash, uint value)
    {
        hash = MixByte(hash, (byte)value);
        hash = MixByte(hash, (byte)(value >> 8));
        hash = MixByte(hash, (byte)(value >> 16));
        hash = MixByte(hash, (byte)(value >> 24));
        return hash;
    }

    private static ulong MixInt(ulong hash, int value)
    {
        return MixUInt32(hash, unchecked((uint)value));
    }

    private static ulong MixFloat(ulong hash, float value)
    {
        return MixUInt32(hash, BitConverter.SingleToUInt32Bits(value));
    }

    private static ulong MixTile(ulong hash, DungeonTile tile)
    {
        hash = MixInt(hash, tile.X);
        hash = MixInt(hash, tile.Z);
        hash = MixInt(hash, tile.Floor);
        return hash;
    }

    private static ulong MixString(ulong hash, string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            hash = MixUInt32(hash, value[i]);
        }

        return hash;
    }
}
