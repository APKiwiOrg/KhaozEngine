using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>One 64x64 map square: N planes of dense layers plus the objects and markers anchored inside it.
/// The streaming unit on every head and the file unit on disk.</summary>
public sealed class TileRegion
{
    public const int Size = 64;
    public const int TileCount = Size * Size;

    public RegionCoord Coord { get; }
    public TilePlaneData[] Planes { get; }
    public List<TileObject> Objects { get; } = new();
    public List<TileMarker> Markers { get; } = new();
    /// <summary>Set by every mutation, cleared by a save. Only dirty regions are rewritten.</summary>
    public bool Dirty { get; set; }

    public TileRegion(RegionCoord coord, int planeCount)
    {
        if (planeCount < 1) throw new ArgumentOutOfRangeException(nameof(planeCount));
        Coord = coord;
        Planes = new TilePlaneData[planeCount];
        for (int i = 0; i < planeCount; i++) Planes[i] = new TilePlaneData();
    }

    public TilePlaneData Plane(int p)
    {
        if ((uint)p >= (uint)Planes.Length) throw new ArgumentOutOfRangeException(nameof(p), $"plane {p} is outside 0..{Planes.Length - 1}");
        return Planes[p];
    }
}
