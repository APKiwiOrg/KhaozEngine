using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>One 64x64 map square: N planes of dense layers plus the objects and markers anchored inside it.
/// The streaming unit on every head and the file unit on disk.</summary>
public sealed class TileRegion
{
    /// <summary>Tiles along one edge of a region.</summary>
    public const int Size = 64;
    /// <summary>Tiles in one plane of a region.</summary>
    public const int TileCount = Size * Size;

    /// <summary>Which region this is.</summary>
    public RegionCoord Coord { get; }
    /// <summary>One entry per plane, lowest first, never null and never resized.</summary>
    public TilePlaneData[] Planes { get; }
    /// <summary>Objects whose anchor tile falls in this region.</summary>
    public List<TileObject> Objects { get; } = new();
    /// <summary>Markers whose tile falls in this region.</summary>
    public List<TileMarker> Markers { get; } = new();
    /// <summary>Set by every mutation, cleared by a save. Only dirty regions are rewritten.</summary>
    public bool Dirty { get; set; }

    /// <summary>Builds an empty region with planeCount planes of unallocated layers.</summary>
    public TileRegion(RegionCoord coord, int planeCount)
    {
        if (planeCount < 1) throw new ArgumentOutOfRangeException(nameof(planeCount));
        Coord = coord;
        Planes = new TilePlaneData[planeCount];
        for (int i = 0; i < planeCount; i++) Planes[i] = new TilePlaneData();
    }

    /// <summary>The layers of plane p, or <see cref="ArgumentOutOfRangeException"/> when p is out of range.</summary>
    public TilePlaneData Plane(int p)
    {
        if ((uint)p >= (uint)Planes.Length) throw new ArgumentOutOfRangeException(nameof(p), $"plane {p} is outside 0..{Planes.Length - 1}");
        return Planes[p];
    }

    /// <summary>Trims every plane, handing each its own index. This is the intended entry point: calling
    /// <see cref="TilePlaneData.Trim(int)"/> directly risks passing the wrong index, which would drop an
    /// authored all-zero height layer above plane 0 and silently lift that plane on the next load.</summary>
    public void Trim()
    {
        for (int i = 0; i < Planes.Length; i++) Planes[i].Trim(i);
    }
}
