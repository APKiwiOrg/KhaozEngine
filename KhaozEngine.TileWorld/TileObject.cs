using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>An object anchored to a tile. <see cref="X"/>/<see cref="Z"/> are WORLD tile coordinates of the
/// footprint's SW tile, its region is <c>RegionCoord.Of(X, Z)</c>. <see cref="Rotation"/> is quarter turns
/// clockwise from above (0 west, 1 north, 2 east, 3 south). The footprint and collision come from the
/// archetype, never from here.</summary>
public sealed class TileObject
{
    public long Id { get; set; }
    public string ArchetypeId { get; set; } = "";
    public int X { get; set; }
    public int Z { get; set; }
    public int Plane { get; set; }
    public int Rotation { get; set; }
    public List<string>? Tags { get; set; }

    public TileCoord Coord => new(X, Z, Plane);
}

/// <summary>A named tile position (spawn, bank anchor, later an NPC spawn site). Unique by name per document.
/// World coordinates.</summary>
public sealed class TileMarker
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Z { get; set; }
    public int Plane { get; set; }
    public List<string>? Tags { get; set; }

    public TileCoord Coord => new(X, Z, Plane);
}
