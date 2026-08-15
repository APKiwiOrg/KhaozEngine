using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>An object anchored to a tile. <see cref="X"/>/<see cref="Z"/> are WORLD tile coordinates of the
/// footprint's SW tile, its region is <c>RegionCoord.Of(X, Z)</c>. <see cref="Rotation"/> is quarter turns
/// clockwise from above (0 west, 1 north, 2 east, 3 south). The footprint and collision come from the
/// archetype, never from here.</summary>
public sealed class TileObject
{
    /// <summary>Document-unique id, allocated once and never reused.</summary>
    public long Id { get; set; }
    /// <summary>Catalog archetype this instance is placed from.</summary>
    public string ArchetypeId { get; set; } = "";
    /// <summary>World x of the footprint's SW tile.</summary>
    public int X { get; set; }
    /// <summary>World z of the footprint's SW tile.</summary>
    public int Z { get; set; }
    /// <summary>Plane the object stands on.</summary>
    public int Plane { get; set; }
    /// <summary>Quarter turns clockwise from above, 0..3.</summary>
    public int Rotation { get; set; }
    /// <summary>Free-form authoring tags, null when none.</summary>
    public List<string>? Tags { get; set; }

    /// <summary>The anchor tile as one address.</summary>
    public TileCoord Coord => new(X, Z, Plane);
}

/// <summary>A named tile position (spawn, bank anchor, later an NPC spawn site). Unique by name per document.
/// World coordinates.</summary>
public sealed class TileMarker
{
    /// <summary>Document-unique name.</summary>
    public string Name { get; set; } = "";
    /// <summary>World x of the marked tile.</summary>
    public int X { get; set; }
    /// <summary>World z of the marked tile.</summary>
    public int Z { get; set; }
    /// <summary>Plane the marker sits on.</summary>
    public int Plane { get; set; }
    /// <summary>Free-form authoring tags, null when none.</summary>
    public List<string>? Tags { get; set; }

    /// <summary>The marked tile as one address.</summary>
    public TileCoord Coord => new(X, Z, Plane);
}
